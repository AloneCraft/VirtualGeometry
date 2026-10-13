using System;
using System.Collections.Generic;
using Omni.PlanetSurface;
using UnityEngine;
using L=Omni.Logical;

namespace Omni.CosmicScale{
    /// <summary>
    /// 冒頭の月を、EarthToMoon と共通の分割した地面（<see cref="PlanetSurfaceLod"/>）で描きます（COSMIC-PRECISION-001）。
    /// 単位直径の球を月の直径（3,475 km）へ拡大すると、物体空間の float の刻みが約 0.2 m になり、カメラが月面から 0.17 m の
    /// サッカーボールの段で面がちらつき、ボールの縁に月の点が出ていました。分割した地面は頂点をパッチの中心からの差で持つので、
    /// 近くでも桁が落ちません。
    /// 起伏は持たず（標高 0、<see cref="ZeroSurfaceHeightSource"/>）、見た目は今の月の球と同じシェーダーの式（CosmicPlanetPatch）、
    /// 細部の compute と岩は使いません。球（旧来の表示）とは切り替えず、常にパッチで描き、球は隠します。
    /// Edit Mode でも同じくパッチで描きます（Game View・Scene View で、球の精度不足の点がボールの縁に出ないように。Play と同じ描画経路）。
    /// Edit Mode の分割した地面は保存しない一時の物で、Scene の保存の直前に片付けて球を戻し、保存の後に作り直します（Scene は球の状態で保存）。
    /// この物体は月の中心にある倍率 1 の Logical Transform（月の姿勢と同じ位置・向き）で、パッチはその子として作られます。
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class CosmicPlanetSurface:MonoBehaviour,Omni.BatchRendering.IRadialBatchMaterialDependencyProvider{
        /// <summary>四分木の最大階層（月の半径でパッチの格子 1 つが約 0.2 m）。</summary>
        public const int MaximumLevel=20;
        /// <summary>許容する地表の格子の画面の画素数（EarthToMoon と同じ）。</summary>
        public const float TargetCellPixels=6f;

        [SerializeField]private string m_BodyId="Moon";
        [SerializeField]private double m_RadiusMeters;
        [SerializeField]private Material m_Material;
        [SerializeField]private L.Transform m_Viewer;
        [SerializeField]private MeshRenderer[] m_LegacyRenderers=Array.Empty<MeshRenderer>();
        private PlanetSurfaceLod m_Lod;
        private PlanetSurfaceDefinition m_Definition;

        public PlanetSurfaceLod Lod=>this.m_Lod;
        public Material Material=>this.m_Material;
        public L.Transform Viewer=>this.m_Viewer;
        public double RadiusMeters=>this.m_RadiusMeters;
        public IReadOnlyList<MeshRenderer> LegacyRenderers=>this.m_LegacyRenderers;

        /// <summary>Scene の生成器から設定します。</summary>
        public void Configure(string bodyId,double radiusMeters,Material material,L.Transform viewer,MeshRenderer[] legacyRenderers){
            if(string.IsNullOrWhiteSpace(bodyId)){throw new ArgumentException("天体の ID が必要です。",nameof(bodyId));}
            if(!double.IsFinite(radiusMeters)||radiusMeters<=0){throw new ArgumentOutOfRangeException(nameof(radiusMeters));}
            this.m_BodyId=bodyId;this.m_RadiusMeters=radiusMeters;this.m_Material=material;this.m_Viewer=viewer;
            this.m_LegacyRenderers=legacyRenderers??Array.Empty<MeshRenderer>();
        }

        /// <summary>実行時に作るパッチの材質を、ビルド時の BRG の依存の収集へ公開します。</summary>
        public void CollectBatchMaterialDependencies(List<Material> destination){
            if(this.m_Material!=null){destination.Add(this.m_Material);}
        }

        private void Start(){
            if(Application.isPlaying){this.Begin(false);}
        }

        private void Begin(bool editMode){
            if(this.m_Lod!=null){return;}
            if(this.m_Material==null||this.m_Viewer==null||!(this.m_RadiusMeters>0)){
                Debug.LogError("[CosmicScale] 分割した地面の参照が不足しています。CosmicScaleSceneBuilder を実行してください: "+this.name);
                if(!editMode){this.enabled=false;}
                return;
            }
            this.m_Definition=ScriptableObject.CreateInstance<PlanetSurfaceDefinition>();
            this.m_Definition.hideFlags=HideFlags.DontSave;
            this.m_Definition.name=this.m_BodyId+" Surface";
            this.m_Definition.Configure(this.m_BodyId,this.m_RadiusMeters,MaximumLevel,TargetCellPixels);
            // 岩は使いません（起伏の無い滑らかな球）。
            SurfaceQualityProfile profile=SurfaceQualityProfile.Current();
            profile.MaximumVisibleRocks=0;
            // Edit Mode は Time.time が描画のたびに進むとは限らないので、パッチの切り替えの補間を待たずに切り替えます。
            if(editMode){profile.MorphDurationSeconds=0;}
            this.m_Lod=this.GetComponent<PlanetSurfaceLod>();
            // MoonTriple が追加: Play では、Edit Mode で作った隠れた(保存しない)分割処理は、再利用せず、破棄して作り直す。
            // Enter Play Mode Options で「シーンを再読み込みしない」とき、Edit Mode で作ったコンポーネントは、Play で更新(Update)が回らない
            // (CosmicPlanetSurface は作り直されて m_Lod が null になり、Begin は呼ばれて初期化されるが、LastUpdateMilliseconds が 0 のまま、階層が進まなかった。
            //  破棄して作り直すと、階層 20 まで進む)。通常の Play(再読み込みする)では、隠れた分割処理は無いので、何もしない。
            // DisallowMultipleComponent のため、即時に破棄してから追加する。
            if(!editMode&&this.m_Lod!=null&&(this.m_Lod.hideFlags&HideFlags.DontSave)!=0){
                UnityEngine.Object.DestroyImmediate(this.m_Lod);
                this.m_Lod=null;
            }
            if(this.m_Lod==null){this.m_Lod=this.gameObject.AddComponent<PlanetSurfaceLod>();}
            // Edit Mode の Component は保存せず、Inspector にも出しません。
            if(editMode){this.m_Lod.hideFlags=HideFlags.HideAndDontSave;}
            // 登録解除したパッチの破棄を数フレーム遅らせます（Scene BRG の Fatal Fault を避ける。EarthToMoon と同じ）。
            this.m_Lod.DeferRendererDestruction=true;
            // 最初のカメラの位置の真下を、最初のフレームから最も細かい階層まで分割しておきます（冒頭はカメラが月面から 0.17 m）。
            Vector3? prewarm=TryGetViewerDirection(this.m_Viewer,this.GetComponent<L.Transform>(),out Vector3 direction)?direction:(Vector3?)null;
            this.m_Lod.Initialize(this.m_Definition,this.m_Material,this.m_Viewer,ZeroSurfaceHeightSource.Instance,this.m_LegacyRenderers,
                prewarm,false,profile,null,0);
        }

        private void OnDestroy(){
            if(this.m_Definition!=null){PlanetSurfaceLod.DestroyOwned(this.m_Definition);this.m_Definition=null;}
        }

        /// <summary>分割した地面を片付けて、球の表示へ戻します（Edit Mode）。保存しない Component は残し、次に作るとき使い直します。</summary>
        private void End(){
            if(this.m_Lod!=null){this.m_Lod.Release();this.m_Lod=null;}
            if(this.m_Definition!=null){PlanetSurfaceLod.DestroyOwned(this.m_Definition);this.m_Definition=null;}
        }

#if UNITY_EDITOR
        private void OnEnable(){
            if(Application.isPlaying){return;}
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving+=this.OnSceneSaving;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved+=this.OnSceneSaved;
            UnityEditor.EditorApplication.update+=this.EditModeTick;
            this.BeginEditMode();
        }

        private void OnDisable(){
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving-=this.OnSceneSaving;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved-=this.OnSceneSaved;
            UnityEditor.EditorApplication.update-=this.EditModeTick;
            if(!Application.isPlaying){this.EndEditMode();}
        }

        // 球の表示の切り替え（MeshRenderer.enabled）は Scene の変更にしません（開いただけで「保存しますか」と聞かれないように）。
        private void BeginEditMode(){
            bool wasDirty=this.gameObject.scene.isDirty;
            this.Begin(true);
            this.KeepSceneClean(wasDirty);
        }

        private void EndEditMode(){
            if(this==null){return;}
            bool wasDirty=this.gameObject.scene.IsValid()&&this.gameObject.scene.isDirty;
            this.End();
            this.KeepSceneClean(wasDirty);
        }

        private void EditModeTick(){
            if(this==null||Application.isPlaying||this.m_Lod==null){return;}
            this.m_Lod.Tick();
        }

        private void OnSceneSaving(UnityEngine.SceneManagement.Scene scene,string path){
            if(scene==this.gameObject.scene&&!Application.isPlaying){this.End();}
        }

        private void OnSceneSaved(UnityEngine.SceneManagement.Scene scene){
            if(this!=null&&this.isActiveAndEnabled&&scene==this.gameObject.scene&&!Application.isPlaying){this.BeginEditMode();}
        }

        private void KeepSceneClean(bool wasDirty){
            UnityEngine.SceneManagement.Scene scene=this.gameObject.scene;
            if(wasDirty||!scene.IsValid()||!scene.isDirty){return;}
            System.Reflection.MethodInfo clear=typeof(UnityEditor.SceneManagement.EditorSceneManager).GetMethod("ClearSceneDirtiness",
                System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
            clear?.Invoke(null,new object[]{scene});
        }
#endif

        /// <summary>天体の局所座標での、天体の中心から視点への向き（Logical の差のまま求める）。</summary>
        public static bool TryGetViewerDirection(L.Transform viewer,L.Transform body,out Vector3 direction){
            direction=default;
            if(viewer==null||body==null||
               !viewer.TryGetWorld(out L.Position3 viewerWorld,out _,out _)||
               !body.TryGetWorld(out L.Position3 bodyWorld,out L.Quaternion bodyRotation,out _)||
               !L.Position3.TryDifference(viewerWorld,bodyWorld,out L.Vector3 delta)||
               !L.Quaternion.TryConjugate(bodyRotation,out L.Quaternion inverse)||
               !L.Quaternion.TryRotate(inverse,delta,out L.Vector3 local)||
               !local.TryToFloat3(out Unity.Mathematics.float3 value)){
                return false;
            }
            // 向きだけを使うので float で十分です（差は Logical のまま求めてから変換する）。
            float length=Unity.Mathematics.math.length(value);
            if(!(length>0)||!float.IsFinite(length)){return false;}
            direction=(Vector3)(value/length);
            return true;
        }
    }
}
