using System.Collections.Generic;
using Omni.BatchRendering;
using UnityEngine;
using VirtualGeometry.VirtualObjects;
using VirtualizedGeometry;
using LogicalPosition3=Omni.Logical.Position3;
using LogicalMatrix3x3=Omni.Logical.Matrix3x3;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalRenderPose=Omni.Logical.RenderPose;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalTransform=Omni.Logical.Transform;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// 仮想オブジェクト(<see cref="VirtualObjectSystem"/>)を Omni の Logical 座標に置く <see cref="IVirtualObjectPlacement"/>。
    /// <see cref="VirtualObjectSystem"/> と同じ GameObject に付ける。その GameObject には Omni の Logical.Transform が要る(ルート。惑星なら惑星の GameObject か、その直下の恒等の子)。
    /// <list type="bullet">
    /// <item>表現の GameObject はルートの直下に置き、Logical.Transform の局所の位置・向き・拡大率を書いてから Unity の Transform へ写す(普通の Transform を間に挟まない)。</item>
    /// <item>見せるときに、表現の MeshRenderer を Omni の一括描画(<see cref="RadialBatchRendererRegistry"/>)へ登録し、隠すときに外す。外した GameObject は 30 frame 後に破棄する(外した frame の破棄は一括描画を止める)。</item>
    /// <item>VG の部品(<see cref="VirtualGeometryRenderer"/>)は登録せず、Omni の差し込み口(<see cref="OmniVirtualGeometryPlacement"/>)を付けて VG に描かせる。</item>
    /// <item>一括描画の材質の準備(sidecar)のために、使う材質を Omni へ申告する(<see cref="IRadialBatchMaterialDependencyProvider"/>)。</item>
    /// </list>
    /// </summary>
    [ExecuteAlways,DisallowMultipleComponent]
    public sealed class OmniObjectPlacement:MonoBehaviour,IVirtualObjectPlacement,IRadialBatchMaterialDependencyProvider{
        private readonly Dictionary<GameObject,List<RadialBatchInstanceHandle>> registrations=new Dictionary<GameObject,List<RadialBatchInstanceHandle>>();
        private readonly List<MeshRenderer> scratch=new List<MeshRenderer>();

        /// <summary>外した GameObject を破棄するまでの frame 数(一括描画が外した frame の破棄を許さないため)。</summary>
        public int RetireFrames=>30;

        /// <summary>見せてから一括描画に載るまでの frame 数(登録は次の安全な更新で効く)。</summary>
        public int VisibleAfterFrames=>2;

        /// <summary>今、一括描画に登録している Renderer の数(漏れの検査用)。</summary>
        public int RegisteredRendererCount{
            get{
                int count=0;
                foreach(List<RadialBatchInstanceHandle> handles in this.registrations.Values){count+=handles.Count;}
                return count;
            }
        }

        public bool TryGetViewer(Transform root,Camera camera,out LogicalVector3 rootLocalPosition,out Quaternion rootLocalRotation,out float projectionScale){
            rootLocalPosition=default;rootLocalRotation=Quaternion.identity;projectionScale=0f;
            if(root==null||camera==null||root.GetComponent<LogicalTransform>()==null){return false;}
            if(!LogicalRenderPose.TryResolve(root,out LogicalRenderPose rootPose)||!LogicalRenderPose.TryResolve(camera.transform,out LogicalRenderPose cameraPose)){return false;}
            // camera のルートの局所の位置は、Logical で差を取り、ルートの基底の逆で戻す。
            if(!LogicalPosition3.TryDifference(cameraPose.Position,rootPose.Position,out LogicalVector3 fromRoot)||
                !rootPose.Basis.TryInverse(out LogicalMatrix3x3 inverse)||
                !LogicalMatrix3x3.TryMultiplyVector(in inverse,in fromRoot,out rootLocalPosition)){
                return false;
            }
            if(!OmniRadialCull.TryToMatrix(in rootPose.Basis,out Matrix4x4 rootBasis)||!OmniRadialCull.TryToMatrix(in cameraPose.Basis,out Matrix4x4 cameraBasis)){return false;}
            rootLocalRotation=Quaternion.Inverse(Rotation(rootBasis))*Rotation(cameraBasis);
            projectionScale=StandardObjectPlacement.ProjectionScale(camera);
            return projectionScale>0f;
        }

        // 基底(回転と拡大)の回転の部分。
        private static Quaternion Rotation(Matrix4x4 basis){
            Vector3 forward=basis.GetColumn(2);Vector3 up=basis.GetColumn(1);
            if(forward.sqrMagnitude<1e-20f||up.sqrMagnitude<1e-20f){return Quaternion.identity;}
            return Quaternion.LookRotation(forward,up);
        }

        public void Place(Transform root,GameObject instance,in LogicalVector3 rootLocalOrigin,Quaternion rootLocalRotation,float scale){
            Transform target=instance.transform;
            if(target.parent!=root){target.SetParent(root,false);}
            LogicalTransform logical=instance.GetComponent<LogicalTransform>();
            if(logical==null){logical=instance.AddComponent<LogicalTransform>();}
            Quaternion rotation=rootLocalRotation.normalized;
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion logicalRotation)||
                !LogicalScalar.TryFromFloat(scale,out LogicalScalar logicalScale)){
                return;
            }
            logical.LocalPosition=rootLocalOrigin;
            logical.LocalRotation=logicalRotation;
            logical.LocalScale=new LogicalVector3(in logicalScale,in logicalScale,in logicalScale);
            logical.SyncUnityTransform();
        }

        public void OnShown(GameObject instance){
            if(instance==null){return;}
            this.Unregister(instance);
            var handles=new List<RadialBatchInstanceHandle>();
            this.scratch.Clear();
            instance.GetComponentsInChildren(true,this.scratch);
            foreach(MeshRenderer renderer in this.scratch){
                if(renderer.TryGetComponent(out VirtualGeometryRenderer _)){
                    // VG が描く(代替 Mesh は止める)。
                    if(!renderer.TryGetComponent(out OmniVirtualGeometryPlacement _)){renderer.gameObject.AddComponent<OmniVirtualGeometryPlacement>();}
                    continue;
                }
                // VG の部品が作る影専用の子(VG の材質のまま)は登録しない。
                if(renderer.TryGetComponent(out VirtualGeometryProxyOwner _)){continue;}
                handles.Add(RadialBatchRendererRegistry.Register(renderer));
            }
            this.scratch.Clear();
            this.registrations[instance]=handles;
        }

        public void OnHidden(GameObject instance){
            this.Unregister(instance);
        }

        private void Unregister(GameObject instance){
            if(instance==null||!this.registrations.TryGetValue(instance,out List<RadialBatchInstanceHandle> handles)){return;}
            foreach(RadialBatchInstanceHandle handle in handles){RadialBatchRendererRegistry.Unregister(handle);}
            this.registrations.Remove(instance);
        }

        private void OnDisable(){
            foreach(List<RadialBatchInstanceHandle> handles in this.registrations.Values){
                foreach(RadialBatchInstanceHandle handle in handles){RadialBatchRendererRegistry.Unregister(handle);}
            }
            this.registrations.Clear();
        }

        /// <summary>一括描画の材質の準備のために、同じ GameObject の <see cref="VirtualObjectSystem"/> が使う材質を申告する(VG の部品の材質は除く)。</summary>
        public void CollectBatchMaterialDependencies(List<Material> destination){
            VirtualObjectSystem system=this.GetComponent<VirtualObjectSystem>();
            if(system==null){return;}
            VirtualObjectDependencies dependencies=VirtualObjectDependencies.FromSets(system.Sets);
            foreach(Material material in dependencies.Materials){destination.Add(material);}
        }
    }
}
