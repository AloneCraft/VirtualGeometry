using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// モデル(FBX・OBJ等)のInspector上部に「Virtual Geometry」の設定を表示する。ModelImporterのInspectorは拡張できないため、
    /// header描画後のeventに載せる。変更はApplyでuserDataへ保存して再インポートし、変換はVirtualGeometryModelPostprocessorが行う。
    /// </summary>
    [InitializeOnLoad]
    internal static class VirtualGeometryModelImporterGUI{
        // 選択中のimporterごとの未適用の編集内容。選択が変わったら破棄する。
        private static readonly Dictionary<AssetImporter,VirtualGeometryModelSettings> pending=new Dictionary<AssetImporter,VirtualGeometryModelSettings>();
        private static bool details;

        static VirtualGeometryModelImporterGUI(){
            UnityEditor.Editor.finishedDefaultHeaderGUI+=DrawHeader;
            Selection.selectionChanged+=pending.Clear;
        }

        private static void DrawHeader(UnityEditor.Editor editor){
            if(!(editor.target is AssetImporter)){return;}
            ModelImporter[] importers=editor.targets.OfType<ModelImporter>().ToArray();
            if(importers.Length==0){
                if(ProducesMesh((AssetImporter)editor.target)){
                    EditorGUILayout.HelpBox("この形式のインポーター("+editor.target.GetType().Name+")はVirtual Geometryに対応していません。Unity標準のモデル形式(FBX・OBJ・DAE・3DS・DXF・.blend等)で読み込んでください。",MessageType.None);
                }
                return;
            }
            if(importers.Length!=editor.targets.Length){return;}
            var current=new VirtualGeometryModelSettings[importers.Length];
            for(int i=0;i<importers.Length;i++){
                if(!VirtualGeometryModelSettings.TryRead(importers[i].userData,out current[i])){
                    EditorGUILayout.HelpBox("このモデルのuserDataは他のツールが使っているため、Virtual Geometryを設定できません。",MessageType.Warning);
                    return;
                }
            }
            VirtualGeometryModelSettings edited=Pending(importers[0],current[0]);
            bool mixed=importers.Length>1&&current.Any(settings=>!settings.SameAs(current[0]));

            EditorGUILayout.Space(2);
            using(new EditorGUILayout.VerticalScope(EditorStyles.helpBox)){
                EditorGUI.showMixedValue=mixed;
                EditorGUI.BeginChangeCheck();
                bool enabled=EditorGUILayout.ToggleLeft(new GUIContent("Virtual Geometry","インポートのたびにMeshをクラスタ化してLOD階層を作り、このモデルのMeshRendererをVirtual Geometry Rendererへ置き換えます。距離に応じて必要な細かさだけを描きます。"),edited.Enabled,EditorStyles.boldLabel);
                if(EditorGUI.EndChangeCheck()){edited.Enabled=enabled;}
                EditorGUI.showMixedValue=false;
                using(new EditorGUI.DisabledScope(!edited.Enabled)){
                    details=EditorGUILayout.Foldout(details,"詳細",true);
                    if(details){
                        EditorGUI.indentLevel++;
                        edited.MaxTrianglesPerCluster=EditorGUILayout.IntSlider(new GUIContent("クラスタの三角形数","1クラスタに入れる三角形の最大数。最も粗い段もこの数以下になるまでLODの段を作ります。"),edited.MaxTrianglesPerCluster,1,128);
                        edited.HierarchyFanout=EditorGUILayout.IntSlider(new GUIContent("親が束ねる数","何個のノードを束ねて1段粗いノードを作るか。"),edited.HierarchyFanout,2,8);
                        edited.SimplificationRatio=EditorGUILayout.Slider(new GUIContent("1段ごとの三角形の割合","1段粗くするごとに三角形数を何倍へ減らすか。0.5で半分。"),edited.SimplificationRatio,0.1f,0.9f);
                        edited.PositionQuantizationBits=EditorGUILayout.IntSlider(new GUIContent("位置の精度 (bits)","頂点座標をモデル全体の格子へ丸める精度。0で丸めません。"),edited.PositionQuantizationBits,0,24);
                        EditorGUI.indentLevel--;
                    }
                }
                bool changed=current.Any(settings=>!settings.SameAs(edited));
                if(changed){
                    using(new EditorGUILayout.HorizontalScope()){
                        GUILayout.FlexibleSpace();
                        if(GUILayout.Button("Revert",GUILayout.Width(70))){pending.Remove(importers[0]);GUIUtility.ExitGUI();}
                        if(GUILayout.Button("Apply",GUILayout.Width(70))){Apply(importers,edited);GUIUtility.ExitGUI();}
                    }
                }else if(current[0].Enabled&&!mixed){
                    EditorGUILayout.LabelField(Describe(importers[0]),EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        private static VirtualGeometryModelSettings Pending(AssetImporter importer,VirtualGeometryModelSettings saved){
            if(!pending.TryGetValue(importer,out VirtualGeometryModelSettings edited)){edited=saved.Clone();pending[importer]=edited;}
            return edited;
        }

        /// <summary>InspectorのApply。全importerへ同じ設定を書いて再インポートする。</summary>
        internal static void Apply(ModelImporter[] importers,VirtualGeometryModelSettings edited){
            edited.Clamp();
            pending.Clear();
            AssetDatabase.StartAssetEditing();
            try{
                foreach(ModelImporter importer in importers){importer.userData=edited.Write();EditorUtility.SetDirty(importer);importer.SaveAndReimport();}
            }finally{
                AssetDatabase.StopAssetEditing();
            }
        }

        // 変換結果を平易な文で示す。失敗したMeshはインポートのwarningに理由が出る。
        private static string Describe(ModelImporter importer){
            VirtualGeometryMeshAsset[] assets=AssetDatabase.LoadAllAssetsAtPath(importer.assetPath).OfType<VirtualGeometryMeshAsset>().ToArray();
            if(assets.Length==0){return "変換されたMeshがありません。MeshRendererを持つMeshが無いか、変換に失敗しました(Consoleのインポートのwarningに理由があります)。";}
            var text=new StringBuilder();
            text.Append(assets.Length).Append("個のMeshを変換済み。");
            foreach(VirtualGeometryMeshAsset asset in assets){
                text.Append("\n・").Append(asset.name.Replace(" (Virtual Geometry)",string.Empty)).Append(": LOD ").Append(asset.LevelCount).Append("段、最も細かい段 ")
                    .Append(asset.LeafTriangleCount.ToString("N0")).Append(" 三角形、離れたときの代替表示 ").Append(asset.ProxyTriangleCount.ToString("N0")).Append(" 三角形");
            }
            return text.ToString();
        }

        // glTF等のパッケージ独自インポーターはScriptedImporterで、モデルをGameObjectとして読み込む。毎回の描画で全assetを読まないよう型だけで判定する。
        private static bool ProducesMesh(AssetImporter importer){
            if(string.IsNullOrEmpty(importer.assetPath)||!(importer is UnityEditor.AssetImporters.ScriptedImporter)){return false;}
            return AssetDatabase.GetMainAssetTypeAtPath(importer.assetPath)==typeof(GameObject);
        }
    }
}
