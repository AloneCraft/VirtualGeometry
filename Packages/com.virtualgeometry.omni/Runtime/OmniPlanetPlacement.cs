using System;
using Omni;
using Unity.Mathematics;
using UnityEngine;
using VirtualGeometry.World;
using VirtualizedGeometry;
using DrawTransform=Omni.Rendering.Radial.DrawTransform;
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
            if(!OmniRadialCull.TryGetSnapshot(view.Camera,out this.snapshot,out float near,out string failure)){
                return this.Fail(failure);
            }
            if(!LogicalRenderPose.TryResolve(planet,out this.planetPose)){
                return this.Fail("惑星の Logical 姿勢を解決できない。");
            }
            if(!OmniRadialCull.TryToMatrix(in this.planetPose.Basis,out this.basis)){
                return this.Fail("惑星の基底を float にできない。");
            }
            // 惑星のローカル座標でのカメラ位置は、Logical で差を取り、基底の逆で戻す(Logical のまま選択に渡す)。
            if(!LogicalPosition3.TryDifference(this.snapshot.Eye,this.planetPose.Position,out LogicalVector3 eyeFromPlanet)||
                !this.planetPose.Basis.TryInverse(out LogicalMatrix3x3 inverse)||
                !LogicalMatrix3x3.TryMultiplyVector(in inverse,in eyeFromPlanet,out LogicalVector3 eyeLocal)){
                return this.Fail("カメラの惑星ローカル位置を求められない。");
            }
            this.planetProxy=planet.localToWorldMatrix;
            Matrix4x4 viewProjection=OmniRadialCull.BuildPlanes(in this.snapshot,near,this.planes);
            float scale=Mathf.Max(this.basis.GetColumn(0).magnitude,Mathf.Max(this.basis.GetColumn(1).magnitude,this.basis.GetColumn(2).magnitude));
            var space=new VirtualGeometryClusterCullSpace(Vector3.zero,this.planes,viewProjection,near,false);
            // cull 空間は eye が原点なので、カメラの cull 空間での位置は 0、惑星のローカルの向きは基底で写す。
            frame=new PlanetPlacementFrame(in eyeLocal,this.basis,Vector3.zero,scale,space);
            this.lastFailure=null;
            return true;
        }

        public bool Describe(in PlanetPlacementFrame frame,in PlanetCellFrame cell,out Matrix4x4 objectToWorld,out Matrix4x4 objectToCull,MaterialPropertyBlock properties){
            objectToWorld=default;objectToCull=default;
            // Cell の原点の Logical 位置 = 惑星 + 基底·cell.Origin(どちらも Logical のまま)。描画も cull もここからの相対で行う。
            if(!LogicalMatrix3x3.TryMultiplyVector(in this.planetPose.Basis,in cell.Origin,out LogicalVector3 offset)||
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
            objectToWorld=this.planetProxy*Matrix4x4.Translate(cell.ApproximateOrigin);
            objectToCull=Matrix4x4.Translate(relative)*this.basis;
            return true;
        }

        private bool Fail(string reason){
            this.lastFailure=reason;
            return false;
        }
    }
}
