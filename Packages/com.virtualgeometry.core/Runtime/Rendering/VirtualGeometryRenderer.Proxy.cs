using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualizedGeometry{
    /// <summary>互換featureはVGより前にgeometryを必要とする場合だけ安全fallbackを要求する。</summary>
    public interface IVirtualGeometryCompatibleRenderFeature{bool RequiresEarlyGeometry{get;}}
    /// <summary>root ProxyのCollider/Editor pickingから元の専用Rendererを参照する。</summary>
    public sealed class VirtualGeometryProxyOwner:MonoBehaviour{public VirtualGeometryRenderer Owner{get;internal set;}}

    /// <summary>M5 sampleの専用Rendererに低密度fallback、Shadow Proxy、Colliderを接続する。</summary>
    public sealed partial class VirtualGeometryRenderer{
        [SerializeField] private bool forceProxy;
        [SerializeField] private bool forceUnsupportedBackend;
        [SerializeField,Tooltip("低密度Proxyで影を落とします。")] private bool enableShadowProxy=true;
        [SerializeField,Tooltip("低密度ProxyをMeshColliderとして使います。")] private bool enablePhysicsProxy=true;
        [SerializeField,Tooltip("submesh順のMaterial。MeshRendererと同じくURPのMaterialを割り当てます。VG描画はMaterialのshaderに対応するVirtual Geometry Shaderを使い、Proxy・影はこのMaterialをそのまま使います。未設定のslotは白のURP Litです。")] private Material[] materials=new Material[0];
        private GameObject proxyRoot;private MeshRenderer proxyVisual;private MeshRenderer proxyShadow;private MeshCollider proxyCollider;
        private Camera decisionCamera;private VirtualGeometryRenderDecision renderDecision;
        private readonly System.Collections.Generic.Dictionary<Camera,VirtualGeometryRenderDecision> cameraDecisions=new System.Collections.Generic.Dictionary<Camera,VirtualGeometryRenderDecision>();
        /// <summary>Monitor用。Proxy表示状態は描画中cameraで切り替わるため、camera別の直近判定を返す。</summary>
        internal bool TryGetCameraDecision(Camera camera,out VirtualGeometryRenderDecision decision){
            if(camera==null){decision=default;return false;}
            return this.cameraDecisions.TryGetValue(camera,out decision);
        }
        public string DiagnosticReason=>this.renderDecision.Reason;
        /// <summary>検証・比較用に通常Proxy表示を強制する。compute未対応の検証overrideは変更しない。</summary>
        public bool IsProxyForced{get=>this.forceProxy;set=>this.forceProxy=value;}
        public bool IsFallbackVisible=>this.proxyVisual!=null&&this.proxyVisual.enabled;
        public MeshCollider PhysicsProxy=>this.proxyCollider;
        public Material[] Materials=>this.materials;
        /// <summary>submesh順のMaterialを設定し、Proxyへも反映する。</summary>
        public void SetMaterials(params Material[] values){this.materials=values??new Material[0];this.UpdateProxyMaterials();}
        private void EnsureMaterialSlots(){
            if(this.materials==null){this.materials=new Material[0];}
            if(this.meshAsset!=null&&this.materials.Length<this.meshAsset.MaterialCount){System.Array.Resize(ref this.materials,this.meshAsset.MaterialCount);}
        }
        /// <summary>slotのMaterial。未設定なら変換assetの白いURP Lit。</summary>
        public Material GetMaterial(int slot){
            if(this.materials!=null&&slot>=0&&slot<this.materials.Length&&this.materials[slot]!=null){return this.materials[slot];}
            if(this.meshAsset!=null){return this.meshAsset.ProxyLitMaterial;}
            return null;
        }
        /// <summary>全slotがVG描画できるか。できないslotがあればinstance全体を通常Proxyにする。</summary>
        private string UnsupportedMaterialReason(){
            if(this.meshAsset==null){return null;}
            for(int slot=0;slot<this.meshAsset.MaterialCount;slot++){
                if(!VirtualGeometryMaterials.IsSupported(this.GetMaterial(slot),out string reason)){return "Material slot "+slot+": "+reason;}
            }
            return null;
        }
        public void SetFallbackOverride(bool forced,bool unsupported=false){this.forceProxy=forced;this.forceUnsupportedBackend=unsupported;}
        public void SetProxyOptions(bool shadow,bool physics){this.enableShadowProxy=shadow;this.enablePhysicsProxy=physics;if(this.proxyShadow!=null){this.proxyShadow.enabled=shadow;}if(this.proxyCollider!=null){this.proxyCollider.enabled=physics;}}
        internal void SetCameraSupport(Camera camera,string reason){
            this.gpuPage?.TickStreaming(Time.frameCount);if(reason==null){reason=VirtualGeometryTransformSupport.GetUnsupportedReason(this.transform.localToWorldMatrix);}
            if(reason==null){reason=this.UnsupportedMaterialReason();}
            if(this.lastError!=null){reason="Archive読込拒否: "+this.lastError;}
            if(!this.IsStreamingRootReady&&this.StreamingStatistics?.Failed>0){reason="Root準備失敗: "+this.StreamingStatistics.LastError;}
            if(this.forceUnsupportedBackend){reason="検証用override: Compute backend未対応";}
            this.renderDecision=VirtualGeometryRenderDecision.Resolve(reason,this.IsStreamingRootReady,this.forceProxy,this.lastError!=null);
            this.decisionCamera=camera;if(camera!=null){this.cameraDecisions[camera]=this.renderDecision;}if(this.proxyVisual!=null){this.proxyVisual.enabled=this.renderDecision.UseProxy;}
        }
        internal bool CanDrawVirtual(Camera camera)=>this.decisionCamera==camera&&this.renderDecision.UseVirtual;
        private void BeforeCamera(ScriptableRenderContext context,Camera camera){
            // RendererFeatureのOnCameraPreCullが対応確認してからvirtualへ切替える。Feature欠落時もProxyを維持する。
            this.SetCameraSupport(camera,"対応するVirtual Geometry RendererFeatureがありません。");
        }
        private void CreateProxy(){
            this.DestroyProxy();
            if(this.meshAsset==null||this.meshAsset.ProxyMesh==null){return;}
            this.proxyRoot=new GameObject("Virtual Geometry Proxy"){hideFlags=HideFlags.HideAndDontSave,layer=this.gameObject.layer};
            this.proxyRoot.transform.SetParent(this.transform,false);
            var filter=this.proxyRoot.AddComponent<MeshFilter>();filter.sharedMesh=this.meshAsset.ProxyMesh;
            this.proxyVisual=this.proxyRoot.AddComponent<MeshRenderer>();this.proxyVisual.shadowCastingMode=ShadowCastingMode.Off;
            this.proxyVisual.receiveShadows=true;
            this.proxyCollider=this.proxyRoot.AddComponent<MeshCollider>();this.proxyCollider.sharedMesh=this.meshAsset.ProxyMesh;this.proxyCollider.enabled=this.enablePhysicsProxy;
            this.proxyRoot.AddComponent<VirtualGeometryProxyOwner>().Owner=this;
            var shadow=new GameObject("Shadow Proxy"){hideFlags=HideFlags.HideAndDontSave,layer=this.gameObject.layer};shadow.transform.SetParent(this.proxyRoot.transform,false);
            shadow.AddComponent<MeshFilter>().sharedMesh=this.meshAsset.ProxyMesh;this.proxyShadow=shadow.AddComponent<MeshRenderer>();
            this.proxyShadow.shadowCastingMode=ShadowCastingMode.ShadowsOnly;this.proxyShadow.enabled=this.enableShadowProxy;this.UpdateProxyMaterials();
        }
        // Proxyは利用者のMaterialをそのまま使う。影はMaterialにShadowCaster passがあればそれを使い、無ければ(URP Unlit等)白のURP Litで落とす。
        private void UpdateProxyMaterials(){
            if(this.proxyVisual==null||this.meshAsset==null){return;}
            var visible=new Material[this.meshAsset.MaterialCount];var shadow=new Material[visible.Length];
            for(int slot=0;slot<visible.Length;slot++){
                visible[slot]=this.GetMaterial(slot);shadow[slot]=visible[slot];
                if(shadow[slot]==null||shadow[slot].FindPass("ShadowCaster")<0){shadow[slot]=this.meshAsset.ProxyLitMaterial;}
            }
            this.proxyVisual.sharedMaterials=visible;this.proxyShadow.sharedMaterials=shadow;
        }
        private void DestroyProxy(){
            // Undo/serialized inspector更新では非serialized参照が復元されない場合がある。
            // 所有markerと直接parentから実際の生成rootを回収し、古い可視Proxyを残さない。
            foreach(var owner in this.GetComponentsInChildren<VirtualGeometryProxyOwner>(true)){
                if(owner.transform.parent!=this.transform){continue;}
                GameObject root=owner.gameObject;root.SetActive(false);
                if(Application.isPlaying){Object.Destroy(root);}else{Object.DestroyImmediate(root);}
            }
            if(this.proxyRoot!=null){this.proxyRoot.SetActive(false);if(Application.isPlaying){Object.Destroy(this.proxyRoot);}else{Object.DestroyImmediate(this.proxyRoot);}}
            this.proxyRoot=null;this.proxyVisual=null;this.proxyShadow=null;this.proxyCollider=null;
        }
    }
}