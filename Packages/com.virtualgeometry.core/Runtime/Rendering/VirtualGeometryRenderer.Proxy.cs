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
        private MeshRenderer ordinaryCache;
        // 描画ごと・部品ごとに何度も引かれるため、GetComponentの結果を覚える。
        public MeshRenderer Ordinary{
            get{
                if(this.ordinaryCache==null){this.ordinaryCache=this.GetComponent<MeshRenderer>();}
                return this.ordinaryCache;
            }
        }
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
            ordinary.sharedMaterials=values??new Material[0];this.InvalidateMaterialCache();this.EnsureMaterialSlots();this.UpdateShadowCaster();
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
            // sharedMaterialsは呼ぶたびに配列を確保するため、描画ごとに引くここでは使い回しのListへ取る。
            MeshRenderer ordinary=this.Ordinary;
            if(ordinary!=null&&slot>=0){
                ordinary.GetSharedMaterials(materialScratch);
                if(slot<materialScratch.Count&&materialScratch[slot]!=null){return materialScratch[slot];}
            }
            if(this.meshAsset!=null){return this.meshAsset.ProxyLitMaterial;}
            return null;
        }
        /// <summary>全slotがVG描画できるか。できないslotがあればinstance全体を通常Proxyにする。</summary>
        private string UnsupportedMaterialReason(){
            if(this.meshAsset==null){return null;}
            this.EnsureMaterialCache();
            return this.unsupportedMaterialReason;
        }

        // slotごとのMaterial解決結果。判定と描画は部品×slot×cameraごとに毎frame呼ばれる。
        // .vgshaderを持たないshader(URP Lit等)では、解決のたびに内部Materialへ全propertyとkeywordを写すため非常に重く、
        // 部品×slotの数だけ毎frame繰り返すとframeの大半を占めた(space_elevator: 記録のうち24 ms/frame)。
        // そこで結果を覚える。Material・Meshを差し替えたとき(SetMaterials / Reload / 影専用の子の更新)は捨てる。
        // 元Materialの値の編集は、Editorでは変更回数(dirty count)が変わった時点で、実行時は一定のframeごとに内部Materialへ写し直す。
        private struct DrawSlot{public Material Source;public Material Material;public int Pass;public bool Ok;public int Dirty;}
        private DrawSlot[] drawSlots;private int drawSlotsFrame;private string unsupportedMaterialReason;
        private const int MaterialCacheFrames=30;
        private void InvalidateMaterialCache(){this.drawSlots=null;}
        private void EnsureMaterialCache(){
            int frame=Time.frameCount;
            if(this.drawSlots!=null&&frame>=this.drawSlotsFrame&&frame-this.drawSlotsFrame<MaterialCacheFrames){return;}
            this.drawSlotsFrame=frame;this.unsupportedMaterialReason=null;
            int count=0;if(this.meshAsset!=null){count=this.meshAsset.MaterialCount;}
            if(this.drawSlots==null||this.drawSlots.Length!=count){this.drawSlots=new DrawSlot[count];}
            for(int slot=0;slot<count;slot++){
                var entry=new DrawSlot();
                entry.Source=this.GetMaterial(slot);
                entry.Ok=VirtualGeometryMaterials.TryResolve(entry.Source,out entry.Material,out entry.Pass,out string _);
                entry.Dirty=MaterialDirtyCount(entry.Source);
                this.drawSlots[slot]=entry;
                if(this.unsupportedMaterialReason==null&&!VirtualGeometryMaterials.IsSupported(entry.Source,out string reason)){this.unsupportedMaterialReason="Material slot "+slot+": "+reason;}
            }
        }
        private static int MaterialDirtyCount(Material material){
#if UNITY_EDITOR
            if(material!=null&&!Application.isPlaying){return UnityEditor.EditorUtility.GetDirtyCount(material);}
#endif
            return 0;
        }
        /// <summary>VG描画に使うMaterialとpass。DrawPassが部品×slotごとに呼ぶ。描けない(半透明等)ときはfalse。</summary>
        internal bool TryResolveDrawMaterial(int slot,out Material material,out int pass){
#if UNITY_EDITOR
            // Inspectorで元Materialが編集されたら、次の描画で内部Materialへ反映する。
            if(this.drawSlots!=null&&slot>=0&&slot<this.drawSlots.Length&&this.drawSlots[slot].Source!=null&&MaterialDirtyCount(this.drawSlots[slot].Source)!=this.drawSlots[slot].Dirty){this.drawSlots=null;}
#endif
            this.EnsureMaterialCache();
            if(slot>=0&&slot<this.drawSlots.Length){
                DrawSlot entry=this.drawSlots[slot];
                material=entry.Material;pass=entry.Pass;return entry.Ok;
            }
            material=null;pass=-1;return false;
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
        private static readonly Unity.Profiling.ProfilerMarker SetCameraSupportMarker=new Unity.Profiling.ProfilerMarker("VG.SetCameraSupport");
        private static readonly Unity.Profiling.ProfilerMarker ApplyDecisionMarker=new Unity.Profiling.ProfilerMarker("VG.ApplyDecision");
        internal void SetCameraSupport(Camera camera,string reason){
            using(SetCameraSupportMarker.Auto()){this.SetCameraSupportCore(camera,reason);}
        }
        private void SetCameraSupportCore(Camera camera,string reason){
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
            using(ApplyDecisionMarker.Auto()){this.ApplyDecisionCore();}
        }
        private void ApplyDecisionCore(){
            MeshRenderer ordinary=this.Ordinary;
            if(ordinary==null){return;}
            // MeshRendererのMaterialはInspectorで直接変わるため、影専用の子へ反映する。描画ごとに呼ばれるので確保なしで比べる。
            if(this.shadowCaster!=null&&this.ShadowMaterialsChanged(ordinary)){this.UpdateShadowCaster();return;}
            bool virtualDraw=!this.renderDecision.UseProxy;
            // 代替 Mesh を描かない配置(ProxyFallback=false)では、VG が描けない間も通常 MeshRenderer を止めたままにする。
            ordinary.forceRenderingOff=virtualDraw||!this.proxyFallback;
            if(this.shadowCaster!=null){this.shadowCaster.enabled=virtualDraw&&ordinary.enabled&&ordinary.shadowCastingMode!=ShadowCastingMode.Off;}
        }
        internal bool CanDrawVirtual(Camera camera)=>this.decisionCamera==camera&&this.renderDecision.UseVirtual;
        private static readonly Unity.Profiling.ProfilerMarker BeforeCameraMarker=new Unity.Profiling.ProfilerMarker("VG.BeforeCamera");
        private void BeforeCamera(ScriptableRenderContext context,Camera camera){
            using(BeforeCameraMarker.Auto()){this.BeforeCameraCore(camera);}
        }
        private void BeforeCameraCore(Camera camera){
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
            this.InvalidateMaterialCache();
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
