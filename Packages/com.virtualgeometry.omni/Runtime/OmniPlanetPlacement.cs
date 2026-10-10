using System;
using Omni;
using Unity.Mathematics;
using UnityEngine;
using VirtualGeometry.World;
using VirtualizedGeometry;
using DrawTransform=Omni.Rendering.Radial.DrawTransform;
using Frustum=Omni.Rendering.Radial.Frustum;
using LogicalMatrix3x3=Omni.Logical.Matrix3x3;
using LogicalPosition3=Omni.Logical.Position3;
using LogicalRenderPose=Omni.Logical.RenderPose;
using LogicalVector3=Omni.Logical.Vector3;
using ViewSnapshot=Omni.Rendering.Radial.ViewSnapshot;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// 惑星を Omni の Logical 座標に置き、Radial 対数描画で描く <see cref="IPlanetPlacement"/>。
    /// 惑星の GameObject(<see cref="PlanetTerrainSource"/> と同じもの)に Omni の Logical.Transform と一緒に付ける。
    /// 位置の正本は Logical で、float にするのは eye からの相対差にした後だけ。
    /// cull は eye を原点とする world 軸の物理空間で行い、HZB は使わない(深度が対数写像のため)。
    /// Cell ごとに、Omni が precise な Renderer に書くのと同じ物体ごとの値を MaterialPropertyBlock へ書く。
    /// material には Omni の Radial shader を元にした .vgshader(このパッケージの Shaders/RadialGlobalLit.vgshader)を使う。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OmniPlanetPlacement:MonoBehaviour,IPlanetPlacement{
        // far plane は使わない(Radial は遠方を圧縮して描くので、Unity の far で切らない)。
        private const float UnboundedDistance=1e30f;

        private readonly Plane[] planes=new Plane[6];
        private IDisposable externalDraw;
        private ViewSnapshot snapshot;
        private LogicalRenderPose planetPose;
        private Matrix4x4 planetProxy;
        private Matrix4x4 basis;
        private string lastFailure;

        /// <summary>最後に描けなかった理由(描けた frame では null)。</summary>
        public string LastFailure=>this.lastFailure;

        public bool UsesProperties=>true;

        private void OnEnable(){
            // Omni の Renderer が無い Scene View でも、Radial の camera packet と描画を有効にさせる。
            this.externalDraw=ExternalDrawPaths.Register(RequireSceneViewContent);
        }

        private void OnDisable(){
            if(this.externalDraw!=null){
                this.externalDraw.Dispose();this.externalDraw=null;
            }
        }

        private static ExternalDrawRequirements RequireSceneViewContent(){
            return ExternalDrawRequirements.SceneViewContent;
        }

        public bool TryBeginFrame(in VirtualGeometryClusterView view,Transform planet,out PlanetPlacementFrame frame){
            frame=default;
            if(!RadialViewRegistry.TryGetCurrent(view.Camera.GetEntityId(),out this.snapshot)){
                return this.Fail("Omni の ViewSnapshot がこの camera と frame に無い。");
            }
            if(!this.snapshot.GpuFrame.IsUsable||!this.snapshot.Frustum.IsValid){
                return this.Fail("Omni の GpuFrame または錐台が使えない。");
            }
            if(this.snapshot.Frustum.IsOrthographic){
                return this.Fail("正投影の camera は未対応。");
            }
            if(!LogicalRenderPose.TryResolve(planet,out this.planetPose)){
                return this.Fail("惑星の Logical 姿勢を解決できない。");
            }
            if(!TryToMatrix(in this.planetPose.Basis,out this.basis)){
                return this.Fail("惑星の基底を float にできない。");
            }
            // 惑星のローカル座標でのカメラ位置は、Logical で差を取り、基底の逆で戻してから float にする。
            if(!LogicalPosition3.TryDifference(this.snapshot.Eye,this.planetPose.Position,out LogicalVector3 eyeFromPlanet)||
                !this.planetPose.Basis.TryInverse(out LogicalMatrix3x3 inverse)||
                !LogicalMatrix3x3.TryMultiplyVector(in inverse,in eyeFromPlanet,out LogicalVector3 eyeLocal)||
                !eyeLocal.TryToFloat3(out float3 cameraLocal)){
                return this.Fail("カメラの惑星ローカル位置を求められない。");
            }
            if(!LogicalPosition3.TryDifference(this.planetPose.Position,this.snapshot.Eye,out LogicalVector3 planetFromEye)||
                !planetFromEye.TryToFloat3(out float3 planetRelative)){
                return this.Fail("惑星の eye 相対位置を float にできない。");
            }
            if(!this.snapshot.NearClip.TryToFloat(out float near)||!(near>0.0f)){
                return this.Fail("Omni の near を float にできない。");
            }
            this.planetProxy=planet.localToWorldMatrix;
            Matrix4x4 viewProjection=this.BuildPlanes(near);
            Matrix4x4 localToCull=Matrix4x4.Translate(planetRelative)*this.basis;
            float scale=Mathf.Max(this.basis.GetColumn(0).magnitude,Mathf.Max(this.basis.GetColumn(1).magnitude,this.basis.GetColumn(2).magnitude));
            var space=new VirtualGeometryClusterCullSpace(Vector3.zero,this.planes,viewProjection,near,false);
            frame=new PlanetPlacementFrame(cameraLocal,localToCull,scale,space);
            this.lastFailure=null;
            return true;
        }

        public bool Describe(in PlanetPlacementFrame frame,Vector3 cellOrigin,out Matrix4x4 objectToWorld,out Matrix4x4 objectToCull,MaterialPropertyBlock properties){
            objectToWorld=default;objectToCull=default;
            // Cell の原点の Logical 位置 = 惑星 + 基底·cellOrigin。描画も cull もここからの相対で行う。
            if(!LogicalVector3.TryFromFloat3(cellOrigin,out LogicalVector3 local)||
                !LogicalMatrix3x3.TryMultiplyVector(in this.planetPose.Basis,in local,out LogicalVector3 offset)||
                !LogicalPosition3.TryAdd(this.planetPose.Position,offset,out LogicalPosition3 origin)||
                !LogicalRenderPose.TryCreate(origin,this.planetPose.Basis,this.planetPose.PrecisionGuaranteed,out LogicalRenderPose pose)||
                !DrawTransform.TryCreate(this.snapshot.WarpCenter,in pose,out DrawTransform drawTransform)||
                !LogicalPosition3.TryDifference(origin,this.snapshot.Eye,out LogicalVector3 fromEye)||
                !fromEye.TryToFloat3(out float3 relative)){
                this.lastFailure="Cell の Logical 位置を求められない。";
                return false;
            }
            RadialGlobalRenderSystem.WriteRadialTransformParameters(properties,in drawTransform);
            RadialGlobalRenderSystem.WriteGpuFrameParameters(properties,in this.snapshot.GpuFrame);
            // 照明は Omni の契約どおり Unity Proxy の行列から物理位置を作る。
            objectToWorld=this.planetProxy*Matrix4x4.Translate(cellOrigin);
            objectToCull=Matrix4x4.Translate(relative)*this.basis;
            return true;
        }

        // eye を原点とする world 軸の空間で、Omni の錐台から side と near の平面を作る。far は無限遠。
        // Radial で eye が warp 中心に無いときは錐台が写像後の可視域と一致しないので、全面を通す(fail-open)。
        private Matrix4x4 BuildPlanes(float near){
            Frustum frustum=this.snapshot.Frustum;
            Quaternion rotation=Quaternion.LookRotation(frustum.Forward,frustum.Up);
            Matrix4x4 worldToView=Matrix4x4.Scale(new Vector3(1.0f,1.0f,-1.0f))*Matrix4x4.Rotate(Quaternion.Inverse(rotation));
            Matrix4x4 projection=Matrix4x4.Perspective(frustum.VerticalFieldOfViewDegrees,frustum.Aspect,near,near*1e6f);
            Matrix4x4 viewProjection=projection*worldToView;
            if(this.snapshot.CanUseConeCulling){
                GeometryUtility.CalculateFrustumPlanes(viewProjection,this.planes);
                this.planes[5]=new Plane(-frustum.Forward,UnboundedDistance);
            }else{
                for(int i=0;i<this.planes.Length;i++){
                    this.planes[i]=new Plane(Vector3.up,UnboundedDistance);
                }
            }
            return viewProjection;
        }

        private bool Fail(string reason){
            this.lastFailure=reason;
            return false;
        }

        private static bool TryToMatrix(in LogicalMatrix3x3 value,out Matrix4x4 result){
            result=Matrix4x4.identity;
            if(!value.M00.TryToFloat(out float m00)||!value.M01.TryToFloat(out float m01)||!value.M02.TryToFloat(out float m02)||
                !value.M10.TryToFloat(out float m10)||!value.M11.TryToFloat(out float m11)||!value.M12.TryToFloat(out float m12)||
                !value.M20.TryToFloat(out float m20)||!value.M21.TryToFloat(out float m21)||!value.M22.TryToFloat(out float m22)){
                return false;
            }
            result.m00=m00;result.m01=m01;result.m02=m02;
            result.m10=m10;result.m11=m11;result.m12=m12;
            result.m20=m20;result.m21=m21;result.m22=m22;
            return true;
        }
    }
}
