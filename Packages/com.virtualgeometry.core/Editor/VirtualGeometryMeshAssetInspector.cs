using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// モデル内に作られた変換データの読み取り専用表示。設定の変更は元のモデルのInspector(「Virtual Geometry」)で行う。
    /// sceneへの配置はこのassetかモデル自体をScene view/Hierarchyへドラッグする。
    /// </summary>
    [CustomEditor(typeof(VirtualGeometryMeshAsset))]
    internal sealed class VirtualGeometryMeshAssetInspector:UnityEditor.Editor{
        public override VisualElement CreateInspectorGUI(){
            var asset=(VirtualGeometryMeshAsset)this.target;
            var root=new VisualElement();
            string legacy=VirtualGeometryAssetText.LegacyNotice(asset);
            if(legacy!=null){
                root.Add(new HelpBox(legacy,HelpBoxMessageType.Warning));
                return root;
            }
            root.Add(new HelpBox(VirtualGeometryAssetText.Summary(asset)+"\n設定は元のモデルのInspectorの「Virtual Geometry」で変更します。このassetをScene viewかHierarchyへドラッグすると配置できます。",HelpBoxMessageType.Info));
            string modelPath=AssetDatabase.GetAssetPath(asset);
            root.Add(new Button(()=>{
                Object model=AssetDatabase.LoadMainAssetAtPath(modelPath);
                Selection.activeObject=model;EditorGUIUtility.PingObject(model);
            }){text="元のモデルを選択",tooltip=modelPath});
            var details=new Foldout{text="詳細",value=false};
            details.Add(new Label("入力 "+asset.InputTriangleCount.ToString("N0")+" 三角形 / クラスタ "+asset.ClusterCount.ToString("N0")+" / ノード "+asset.NodeCount.ToString("N0")+" / Material "+asset.MaterialCount));
            details.Add(new Label("page "+asset.StreamingPageCount+" / GPU割り当て単位 "+VirtualGeometryAssetText.Bytes(asset.SlotGeometryBytes)+" / 代替表示の誤差 "+asset.ProxyGeometricError.ToString("G4")));
            details.Add(new Label("format v"+asset.FormatVersion+" / builder "+asset.BuilderVersion+" / "+asset.SettingsHash));
            root.Add(details);
            return root;
        }
    }
}
