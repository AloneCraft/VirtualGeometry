using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace VirtualizedGeometry.Editor{
    /// <summary>Game viewを覆わずに、対象cameraのcluster選択状態を別windowへ表示する。取得は非同期readbackだけで行う。</summary>
    public sealed class VirtualGeometryMonitorWindow:EditorWindow{
        private readonly VirtualGeometrySelectionMonitor monitor=new VirtualGeometrySelectionMonitor();
        private ObjectField camera;private Label values;

        [MenuItem("Tools/Virtualized Geometry/Monitor")]
        public static void Open(){var window=GetWindow<VirtualGeometryMonitorWindow>();window.titleContent=new GUIContent("VG Monitor");window.minSize=new Vector2(520,240);}

        public void CreateGUI(){
            this.rootVisualElement.Clear();
            this.camera=new ObjectField("対象camera（空欄はMain Camera）"){objectType=typeof(Camera),allowSceneObjects=true};this.rootVisualElement.Add(this.camera);
            this.values=new Label();this.values.style.whiteSpace=WhiteSpace.Normal;this.values.style.paddingTop=6;
            var scroll=new ScrollView();scroll.Add(this.values);this.rootVisualElement.Add(scroll);
            this.rootVisualElement.schedule.Execute(this.Refresh).Every(100);
        }

        private void Refresh(){
            if(this.values==null){return;}
            Camera target=this.camera.value as Camera;
            if(target==null){target=Camera.main;}
            this.monitor.Update(target);
            this.values.text=this.monitor.BuildReport(target);
        }
    }
}
