using System;
using Omni;
using Unity.Mathematics;
using UnityEngine;
using VirtualizedGeometry;
using DrawTransform=Omni.Rendering.Radial.DrawTransform;
using LogicalPosition3=Omni.Logical.Position3;
using LogicalRenderPose=Omni.Logical.RenderPose;
using LogicalVector3=Omni.Logical.Vector3;
using ViewSnapshot=Omni.Rendering.Radial.ViewSnapshot;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// VG の部品(<see cref="VirtualGeometryRenderer"/>)を Omni の Logical 座標に置き、Radial 対数描画で描く <see cref="IVirtualGeometryInstancePlacement"/>。
    /// <see cref="VirtualGeometryRenderer"/> と同じ GameObject に付ける。位置は Omni の Logical.Transform(この GameObject か祖先)から解決し、
    /// float にするのは eye からの相対差にした後だけ。
    /// material には Omni の Radial shader を元にした .vgshader(このパッケージの Shaders/RadialGlobalLit.vgshader など)を使う。
    /// cull は eye を原点とする world 軸の物理空間で行い、HZB は使わない(<see cref="OmniRadialCull"/>)。
    /// 有効な間は、VG が描けない camera で代替 Mesh(通常の MeshRenderer)を描かせない(Radial の描画を通らないため、位置と深度が合わない)。
    /// </summary>
    [ExecuteAlways,DisallowMultipleComponent]
    public sealed class OmniVirtualGeometryPlacement:MonoBehaviour,IVirtualGeometryInstancePlacement{
        private readonly Plane[] planes=new Plane[6];
        private IDisposable externalDraw;
        private string lastFailure;

        /// <summary>最後に描けなかった理由(描けた camera では null)。</summary>
        public string LastFailure=>this.lastFailure;

        private void OnEnable(){
            // Omni の Renderer が無い Scene View でも、Radial の camera packet と描画を有効にさせる。
            this.externalDraw=ExternalDrawPaths.Register(RequireSceneViewContent);
            if(this.TryGetComponent(out VirtualGeometryRenderer renderer)){renderer.ProxyFallback=false;}
        }

        private void OnDisable(){
            if(this.externalDraw!=null){
                this.externalDraw.Dispose();this.externalDraw=null;
            }
            if(this.TryGetComponent(out VirtualGeometryRenderer renderer)){renderer.ProxyFallback=true;}
        }

        private static ExternalDrawRequirements RequireSceneViewContent(){
            return ExternalDrawRequirements.SceneViewContent;
        }

        public bool TryDescribe(VirtualGeometryRenderer renderer,in VirtualGeometryClusterView view,MaterialPropertyBlock properties,out VirtualGeometryInstancePose pose){
            pose=default;
            if(!OmniRadialCull.TryGetSnapshot(view.Camera,out ViewSnapshot snapshot,out float near,out string failure)){return this.Fail(failure);}
            if(!LogicalRenderPose.TryResolve(this.transform,out LogicalRenderPose objectPose)){return this.Fail("部品の Logical 姿勢を解決できない。");}
            if(!OmniRadialCull.TryToMatrix(in objectPose.Basis,out Matrix4x4 basis)){return this.Fail("部品の基底を float にできない。");}
            // 描画も cull も、部品の原点(Logical)から eye への相対で行う。
            if(!DrawTransform.TryCreate(snapshot.WarpCenter,in objectPose,out DrawTransform drawTransform)||
                !LogicalPosition3.TryDifference(objectPose.Position,snapshot.Eye,out LogicalVector3 fromEye)||
                !fromEye.TryToFloat3(out float3 relative)){
                return this.Fail("部品の Logical 位置を eye からの相対にできない。");
            }
            RadialGlobalRenderSystem.WriteRadialTransformParameters(properties,in drawTransform);
            RadialGlobalRenderSystem.WriteGpuFrameParameters(properties,in snapshot.GpuFrame);
            Matrix4x4 viewProjection=OmniRadialCull.BuildPlanes(in snapshot,near,this.planes);
            var space=new VirtualGeometryClusterCullSpace(Vector3.zero,this.planes,viewProjection,near,false);
            // 照明は Omni の契約どおり Unity Proxy の行列から物理位置を作る。
            pose=new VirtualGeometryInstancePose(this.transform.localToWorldMatrix,Matrix4x4.Translate(relative)*basis,space);
            this.lastFailure=null;
            return true;
        }

        private bool Fail(string reason){
            this.lastFailure=reason;
            return false;
        }
    }
}
