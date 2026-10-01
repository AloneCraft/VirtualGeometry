using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualizedGeometry{
    /// <summary>互換featureはVGより前にgeometryを必要とする場合だけ安全fallbackを要求する。</summary>
    public interface IVirtualGeometryCompatibleRenderFeature{bool RequiresEarlyGeometry{get;}}
    /// <summary>VG描画中に影を落とす非表示の子を、元の専用Rendererから回収するための印。</summary>
    public sealed class VirtualGeometryProxyOwner:MonoBehaviour{public VirtualGeometryRenderer Owner{get;internal set;}}

    /// <summary>
    /// 同じGameObjectの通常MeshRenderer(MeshFilterは低密度の代替Mesh)を、UE5のFallback Meshと同じく代替表示・影・カリングに使う。
    /// VGが描くcameraでは通常MeshRendererの描画を止め(保存されないforceRenderingOff)、影は代替Meshの影専用の子で落とす。
    /// VGが描けないcameraでは通常MeshRendererがそのまま代替表示と影を受け持つ。Material・影の有無はMeshRendererの設定に従う。
    /// </summary>
    public sealed partial class VirtualGeometryRenderer{
        [SerializeField] private bool forceProxy;
        [SerializeField] private bool forceUnsupportedBackend;
        private GameObject shadowRoot;private MeshRenderer shadowCaster;
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
        /// <summary>同じGameObjectの通常MeshRenderer。代替表示・影・Materialの持ち主。</summary>
        public MeshRenderer Ordinary=>this.GetComponent<MeshRenderer>();
        public bool IsFallbackVisible{
            get{MeshRenderer ordinary=this.Ordinary;return ordinary!=null&&ordinary.enabled&&!ordinary.forceRenderingOff;}
        }
        public MeshCollider PhysicsProxy=>this.GetComponent<MeshCollider>();
        public Material[] Materials{
            get{MeshRenderer ordinary=this.Ordinary;if(ordinary==null){return new Material[0];}return ordinary.sharedMaterials;}
        }
        /// <summary>submesh順のMaterialを通常MeshRendererへ設定する。VG描画も同じMaterialを使う。</summary>
        public void SetMaterials(params Material[] values){
            MeshRenderer ordinary=this.Ordinary;if(ordinary==null){return;}
            ordinary.sharedMaterials=values??new Material[0];this.EnsureMaterialSlots();this.UpdateShadowCaster();
        }
        // 通常MeshRendererの空きslotはmagentaになるため、変換assetの白いURP Litで埋める。
        private void EnsureMaterialSlots(){
            MeshRenderer ordinary=this.Ordinary;
            if(ordinary==null||this.meshAsset==null){return;}
            Material[] current=ordinary.sharedMaterials;
            if(current.Length>=this.meshAsset.MaterialCount&&System.Array.IndexOf(current,null)<0){return;}
            var filled=new Material[Mathf.Max(current.Length,this.meshAsset.MaterialCount)];
            for(int slot=0;slot<filled.Length;slot++){
                if(slot<current.Length&&current[slot]!=null){filled[slot]=current[slot];}else{filled[slot]=this.meshAsset.ProxyLitMaterial;}
            }
            ordinary.sharedMaterials=filled;
        }
        /// <summary>slotのMaterial。通常MeshRendererに無ければ変換assetの白いURP Lit。</summary>
        public Material GetMaterial(int slot){
            Material[] materials=this.Materials;
            if(slot>=0&&slot<materials.Length&&materials[slot]!=null){return materials[slot];}
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
        /// <summary>影の有無を通常MeshRendererへ、低密度の当たり判定をMeshColliderとして設定する。</summary>
        public void SetProxyOptions(bool shadow,bool physics){
            MeshRenderer ordinary=this.Ordinary;
            if(ordinary!=null){
                if(shadow){ordinary.shadowCastingMode=ShadowCastingMode.On;}else{ordinary.shadowCastingMode=ShadowCastingMode.Off;}
            }
            MeshCollider collider=this.GetComponent<MeshCollider>();
            if(physics){
                if(collider==null){collider=this.gameObject.AddComponent<MeshCollider>();}
                if(this.meshAsset!=null){collider.sharedMesh=this.meshAsset.ProxyMesh;}
                collider.enabled=true;
            }else if(collider!=null){
                collider.enabled=false;
            }
            this.UpdateShadowCaster();
        }
        internal void SetCameraSupport(Camera camera,string reason){
            this.gpuPage?.TickStreaming(Time.frameCount);if(reason==null){reason=VirtualGeometryTransformSupport.GetUnsupportedReason(this.transform.localToWorldMatrix);}
            MeshRenderer ordinary=this.Ordinary;
            if(reason==null&&(ordinary==null||!ordinary.enabled)){reason="MeshRendererが無効です。";}
            if(reason==null){reason=this.UnsupportedMaterialReason();}
            if(this.lastError!=null){reason="Archive読込拒否: "+this.lastError;}
            if(!this.IsStreamingRootReady&&this.StreamingStatistics?.Failed>0){reason="Root準備失敗: "+this.StreamingStatistics.LastError;}
            if(this.forceUnsupportedBackend){reason="検証用override: Compute backend未対応";}
            this.renderDecision=VirtualGeometryRenderDecision.Resolve(reason,this.IsStreamingRootReady,this.forceProxy,this.lastError!=null);
            this.decisionCamera=camera;if(camera!=null){this.cameraDecisions[camera]=this.renderDecision;}
            this.ApplyDecision();
        }
        // VGが描くcameraでは通常MeshRendererの描画を止め、影は影専用の子で代替Meshから落とす。描けないcameraでは通常MeshRendererに戻す。
        private void ApplyDecision(){
            MeshRenderer ordinary=this.Ordinary;
            if(ordinary==null){return;}
            // MeshRendererのMaterialはInspectorで直接変わるため、影専用の子へ反映する。描画ごとに呼ばれるので確保なしで比べる。
            if(this.shadowCaster!=null&&this.ShadowMaterialsChanged(ordinary)){this.UpdateShadowCaster();return;}
            bool virtualDraw=!this.renderDecision.UseProxy;
            ordinary.forceRenderingOff=virtualDraw;
            if(this.shadowCaster!=null){this.shadowCaster.enabled=virtualDraw&&ordinary.enabled&&ordinary.shadowCastingMode!=ShadowCastingMode.Off;}
        }
        internal bool CanDrawVirtual(Camera camera)=>this.decisionCamera==camera&&this.renderDecision.UseVirtual;
        private void BeforeCamera(ScriptableRenderContext context,Camera camera){
            // RendererFeatureのOnCameraPreCullが対応確認してからvirtualへ切替える。Feature欠落時もProxyを維持する。
            this.SetCameraSupport(camera,"対応するVirtual Geometry RendererFeatureがありません。");
        }
        private void CreateProxy(){
            this.DestroyProxy();
            if(this.meshAsset==null||this.meshAsset.ProxyMesh==null){return;}
            this.shadowRoot=new GameObject("Virtual Geometry Shadow"){hideFlags=HideFlags.HideAndDontSave,layer=this.gameObject.layer};
            this.shadowRoot.transform.SetParent(this.transform,false);
            this.shadowRoot.AddComponent<MeshFilter>().sharedMesh=this.meshAsset.ProxyMesh;
            this.shadowCaster=this.shadowRoot.AddComponent<MeshRenderer>();this.shadowCaster.shadowCastingMode=ShadowCastingMode.ShadowsOnly;this.shadowCaster.enabled=false;
            this.shadowRoot.AddComponent<VirtualGeometryProxyOwner>().Owner=this;
            this.UpdateShadowCaster();
        }
        // 影は通常MeshRendererと同じMaterialで落とす。MaterialにShadowCaster passが無ければ(URP Unlit等)白のURP Litで落とす。
        private static readonly System.Collections.Generic.List<Material> materialScratch=new System.Collections.Generic.List<Material>();
        private readonly System.Collections.Generic.List<Material> shadowSource=new System.Collections.Generic.List<Material>();
        private bool ShadowMaterialsChanged(MeshRenderer ordinary){
            ordinary.GetSharedMaterials(materialScratch);
            if(materialScratch.Count!=this.shadowSource.Count){return true;}
            for(int i=0;i<materialScratch.Count;i++){if(materialScratch[i]!=this.shadowSource[i]){return true;}}
            return false;
        }
        private void UpdateShadowCaster(){
            if(this.shadowCaster==null||this.meshAsset==null){return;}
            MeshRenderer ordinary=this.Ordinary;
            this.shadowSource.Clear();if(ordinary!=null){ordinary.GetSharedMaterials(this.shadowSource);}
            var shadow=new Material[this.meshAsset.MaterialCount];
            for(int slot=0;slot<shadow.Length;slot++){
                shadow[slot]=this.GetMaterial(slot);
                if(shadow[slot]==null||shadow[slot].FindPass("ShadowCaster")<0){shadow[slot]=this.meshAsset.ProxyLitMaterial;}
            }
            this.shadowCaster.sharedMaterials=shadow;
            this.ApplyDecision();
        }
        private void DestroyProxy(){
            // Undo/serialized inspector更新では非serialized参照が復元されない場合がある。
            // 所有markerと直接parentから実際の生成rootを回収し、古い影専用の子を残さない。
            foreach(var owner in this.GetComponentsInChildren<VirtualGeometryProxyOwner>(true)){
                if(owner.transform.parent!=this.transform){continue;}
                GameObject root=owner.gameObject;root.SetActive(false);
                if(Application.isPlaying){Object.Destroy(root);}else{Object.DestroyImmediate(root);}
            }
            if(this.shadowRoot!=null){this.shadowRoot.SetActive(false);if(Application.isPlaying){Object.Destroy(this.shadowRoot);}else{Object.DestroyImmediate(this.shadowRoot);}}
            this.shadowRoot=null;this.shadowCaster=null;
            // VGを外したときに通常MeshRendererを止めたまま残さない。
            MeshRenderer ordinary=this.Ordinary;
            if(ordinary!=null){ordinary.forceRenderingOff=false;}
        }
    }
}
