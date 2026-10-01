using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEditor.UIElements;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// UE5のviewport toolbar(View Mode ▸ Nanite Visualization、Show、stat Nanite)を模したScene view toolbar。
    /// Game viewはOverlay APIに対応しないため、cameraごとの選択状態はVirtualGeometryMonitorWindowで見る。
    /// 状態はrendererのserialized fieldを正とし、overlay側に別状態を持たない。
    /// </summary>
    [Overlay(typeof(SceneView),Id,"Virtual Geometry")]
    internal sealed class VirtualGeometrySceneToolbar:ToolbarOverlay{
        public const string Id="virtual-geometry-toolbar";
        public VirtualGeometrySceneToolbar():base(VirtualGeometryVisualizationDropdown.Id,VirtualGeometryShowDropdown.Id,VirtualGeometryStatsToggle.Id){}

        internal static VirtualGeometryRenderer[] Renderers()=>Object.FindObjectsByType<VirtualGeometryRenderer>();

        internal static VirtualGeometryRenderer FirstActive(){
            foreach(VirtualGeometryRenderer renderer in Renderers()){if(renderer.isActiveAndEnabled){return renderer;}}
            return null;
        }

        internal static SceneView Owner(VisualElement element){
            foreach(SceneView view in SceneView.sceneViews){if(view!=null&&view.rootVisualElement.Contains(element)){return view;}}
            return SceneView.lastActiveSceneView;
        }
    }

    /// <summary>UE5のNanite Visualizationに相当する可視化mode選択。全instanceへ同じmodeを適用する。</summary>
    [EditorToolbarElement(Id,typeof(SceneView))]
    internal sealed class VirtualGeometryVisualizationDropdown:EditorToolbarDropdown{
        public const string Id="VirtualGeometry/Visualization";
        private static readonly (VirtualGeometryDebugView View,string Label)[] modes={
            (VirtualGeometryDebugView.None,"通常"),(VirtualGeometryDebugView.Triangles,"Triangles"),(VirtualGeometryDebugView.Clusters,"Clusters"),
            (VirtualGeometryDebugView.LodLevel,"LOD Level"),(VirtualGeometryDebugView.ScreenError,"Screen Error"),(VirtualGeometryDebugView.ResidentPages,"Resident Pages"),
            (VirtualGeometryDebugView.Loading,"Loading"),(VirtualGeometryDebugView.Culling,"Culling")};

        public VirtualGeometryVisualizationDropdown(){
            this.tooltip="Virtual Geometryの可視化mode(UE5のNanite Visualization相当)。Scene/Game viewの両方に反映されます。";
            this.clicked+=this.ShowMenu;
            this.Refresh();
            this.schedule.Execute(this.Refresh).Every(500);
        }

        private void Refresh(){
            VirtualGeometryRenderer first=VirtualGeometrySceneToolbar.FirstActive();
            if(first==null){this.text="VG: -";return;}
            this.text="VG: "+Label(first.DebugView);
        }

        private static string Label(VirtualGeometryDebugView view){
            foreach(var mode in modes){if(mode.View==view){return mode.Label;}}
            return view.ToString();
        }

        private void ShowMenu(){
            VirtualGeometryRenderer first=VirtualGeometrySceneToolbar.FirstActive();
            var menu=new GenericMenu();
            if(first==null){menu.AddDisabledItem(new GUIContent("Virtual Geometry Rendererがありません"));menu.DropDown(this.worldBound);return;}
            foreach(var mode in modes){
                VirtualGeometryDebugView view=mode.View;
                menu.AddItem(new GUIContent(mode.Label),first.DebugView==view,()=>this.Apply(view));
                if(view==VirtualGeometryDebugView.None){menu.AddSeparator(string.Empty);}
            }
            menu.DropDown(this.worldBound);
        }

        private void Apply(VirtualGeometryDebugView view){
            foreach(VirtualGeometryRenderer renderer in VirtualGeometrySceneToolbar.Renderers()){renderer.SetDebugView(view);}
            this.Refresh();InternalEditorUtility.RepaintAllViews();
        }
    }

    /// <summary>UE5のShow menuに相当する表示・カリング・診断の切替。</summary>
    [EditorToolbarElement(Id,typeof(SceneView))]
    internal sealed class VirtualGeometryShowDropdown:EditorToolbarDropdown{
        public const string Id="VirtualGeometry/Show";

        public VirtualGeometryShowDropdown(){
            this.text="Show";
            this.tooltip="カリング、Proxy強制、診断readbackの切替とMonitor windowを開きます。";
            this.clicked+=this.ShowMenu;
        }

        private void ShowMenu(){
            VirtualGeometryRenderer first=VirtualGeometrySceneToolbar.FirstActive();
            var menu=new GenericMenu();
            if(first==null){
                menu.AddDisabledItem(new GUIContent("Virtual Geometry Rendererがありません"));
            }else{
                bool frustum=first.EnableFrustumCulling;bool occlusion=first.EnableOcclusionCulling;bool proxy=first.IsProxyForced;
                menu.AddItem(new GUIContent("Frustum culling"),frustum,()=>ApplyToRenderers(renderer=>renderer.SetCullingSettings(!frustum,renderer.EnableOcclusionCulling)));
                menu.AddItem(new GUIContent("Occlusion culling (HZB)"),occlusion,()=>ApplyToRenderers(renderer=>renderer.SetCullingSettings(renderer.EnableFrustumCulling,!occlusion)));
                menu.AddItem(new GUIContent("通常Proxyを強制表示"),proxy,()=>ApplyToRenderers(renderer=>renderer.IsProxyForced=!proxy));
            }
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Monitor windowを開く"),false,VirtualGeometryMonitorWindow.Open);
            bool diagnostics=VirtualGeometryDiagnostics.Enabled;
            menu.AddItem(new GUIContent("診断readbackとCPU/GPU timing (GPU同期あり)"),diagnostics,()=>VirtualGeometryDiagnostics.Enabled=!diagnostics);
            menu.DropDown(this.worldBound);
        }

        private static void ApplyToRenderers(Action<VirtualGeometryRenderer> apply){
            foreach(VirtualGeometryRenderer renderer in VirtualGeometrySceneToolbar.Renderers()){apply(renderer);}
            InternalEditorUtility.RepaintAllViews();
        }
    }

    /// <summary>UE5のstat Naniteに相当するVG Stats panelの開閉。</summary>
    [EditorToolbarElement(Id,typeof(SceneView))]
    internal sealed class VirtualGeometryStatsToggle:EditorToolbarToggle{
        public const string Id="VirtualGeometry/Stats";

        public VirtualGeometryStatsToggle(){
            this.text="Stats";
            this.tooltip="VG Stats panelを表示します。";
            this.RegisterValueChangedCallback(change=>{if(this.TryGetStats(out Overlay stats)){stats.displayed=change.newValue;if(change.newValue){stats.collapsed=false;}}});
            this.schedule.Execute(()=>{if(this.TryGetStats(out Overlay stats)){this.SetValueWithoutNotify(stats.displayed);}}).Every(500);
        }

        private bool TryGetStats(out Overlay stats){
            SceneView view=VirtualGeometrySceneToolbar.Owner(this);
            stats=null;
            return view!=null&&view.TryGetOverlay(VirtualGeometryStatsOverlay.Id,out stats);
        }
    }

    /// <summary>Scene viewのVG Stats panel(UE5のstat Nanite相当)。診断readbackはGPU同期を含むため、手動更新だけで取得する。</summary>
    [Overlay(typeof(SceneView),Id,"VG Stats",defaultDisplay=false)]
    internal sealed class VirtualGeometryStatsOverlay:Overlay{
        public const string Id="virtual-geometry-stats";
        private Toggle enabledDiagnostics;private ObjectField camera;private Label values;

        public static void ShowInLastSceneView(){
            SceneView view=SceneView.lastActiveSceneView;
            if(view==null){return;}
            if(view.TryGetOverlay(Id,out Overlay overlay)){overlay.displayed=true;overlay.collapsed=false;}
            view.Focus();
        }

        public override void OnCreated(){
            // panelを閉じたら同期readbackとtimingを止め、通常性能へ診断負荷を残さない。
            this.displayedChanged+=shown=>{if(!shown){VirtualGeometryDiagnostics.Enabled=false;}};
        }

        public override VisualElement CreatePanelContent(){
            var root=new VisualElement();root.style.width=460;
            root.Add(new HelpBox("診断readbackはGPU同期を含みます。計測とdebug bufferの負荷を通常描画性能へ混ぜないでください。CPU/GPU時間は直前frameのVG marker集計です。",HelpBoxMessageType.Info));
            this.enabledDiagnostics=new Toggle("診断readbackとGPU/CPU timingを有効化"){value=VirtualGeometryDiagnostics.Enabled};
            this.enabledDiagnostics.RegisterValueChangedCallback(change=>VirtualGeometryDiagnostics.Enabled=change.newValue);
            this.enabledDiagnostics.schedule.Execute(()=>this.enabledDiagnostics.SetValueWithoutNotify(VirtualGeometryDiagnostics.Enabled)).Every(500);
            root.Add(this.enabledDiagnostics);
            this.camera=new ObjectField("対象camera（空欄は各instanceの直近camera）"){objectType=typeof(Camera),allowSceneObjects=true};root.Add(this.camera);
            root.Add(new Button(this.Refresh){text="診断を更新"});
            this.values=new Label("診断は無効です。");this.values.style.whiteSpace=WhiteSpace.Normal;
            var scroll=new ScrollView();scroll.style.maxHeight=360;scroll.Add(this.values);root.Add(scroll);
            return root;
        }

        private void Refresh(){
            if(this.values==null){return;}
            if(!VirtualGeometryDiagnostics.Enabled){this.values.text="診断は無効です。";return;}
            var rows=new List<VirtualGeometryInstanceMetrics>();var resources=new HashSet<string>();var text=new StringBuilder();
            long io=0,miss=0,quality=0,overflow=0,evictions=0;int queued=0,loading=0,uploading=0,failed=0,resident=0;
            Camera selected=this.camera.value as Camera;
            foreach(VirtualGeometryRenderer renderer in VirtualGeometrySceneToolbar.Renderers()){
                if(!renderer.isActiveAndEnabled){continue;}rows.Add(renderer.ReadMetricsForDiagnostics(selected));var streaming=renderer.StreamingStatistics;
                if(resources.Add(renderer.SharedResourceKey)&&streaming!=null){io+=streaming.IoBytes;miss+=streaming.Misses;quality+=streaming.QualityTargetMisses;overflow+=streaming.QueueOverflow;evictions+=streaming.Evictions;queued+=streaming.Queued;loading+=streaming.Loading;uploading+=streaming.Uploading;failed+=streaming.Failed;resident+=streaming.Resident;}
                text.Append(renderer.name).Append(": ");
                if(renderer.IsFallbackVisible){text.Append("Proxy ").Append(renderer.DiagnosticReason);}else{text.Append("専用描画");}
                text.AppendLine();
                try{
                    VirtualGeometryGpuSelectionSnapshot snapshot;
                    if(selected!=null){snapshot=renderer.ReadbackSelectionForDiagnostics(selected);}else{snapshot=renderer.ReadbackSelectionForDiagnostics();}
                    uint[] c=snapshot.CullingStatistics;text.AppendLine("  considered="+c[0]+" frustum="+c[1]+" HZB="+c[2]+" visible="+c[3]);
                }catch(InvalidOperationException){text.AppendLine("  camera選択: 未描画");}
            }
            var total=VirtualGeometryDebugMetrics.Aggregate(rows);
            this.values.text="入力(unique source)="+total.InputTriangles+" / unique triangles="+total.UniqueTriangles+" / instance triangles="+total.InstanceTriangles+
                "\nselected clusters="+total.SelectedClusters+" / drawn triangles="+total.DrawnTriangles+" / nonempty draws="+total.DrawCalls+
                "\nGPU geometry="+total.GpuGeometryBytes+" / metadata="+total.GpuMetadataBytes+" / instance×camera selection="+total.GpuSelectionBytes+" B\nCPU cache="+total.CpuCacheBytes+" B / I/O="+io+" B"+
                "\nqueued/loading/uploading/resident/failed="+queued+"/"+loading+"/"+uploading+"/"+resident+"/"+failed+
                "\nmiss="+miss+" / quality target miss="+quality+" / overflow="+overflow+" / eviction="+evictions+
                "\nVG CPU="+Format(VirtualGeometryDiagnostics.CpuMilliseconds)+" ms / GPU="+Format(VirtualGeometryDiagnostics.GpuMilliseconds)+" ms\n"+text;
        }

        private static string Format(double value){
            if(double.IsNaN(value)){return "未取得";}
            return value.ToString("F3");
        }
    }
}
