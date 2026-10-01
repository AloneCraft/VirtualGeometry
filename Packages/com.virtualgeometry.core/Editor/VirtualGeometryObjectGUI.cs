using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// Virtual Geometryで描くGameObjectのInspector上部に、状態と設定を表示する。VGとつなぐ部品はInspectorに出さないため、
    /// 利用者はMeshRenderer(Material・影)とこの欄だけを見ればよい。設定は非表示の部品へSerializedObject経由で書き、Undoできる。
    /// </summary>
    [InitializeOnLoad]
    internal static class VirtualGeometryObjectGUI{
        private static bool details;

        static VirtualGeometryObjectGUI(){
            UnityEditor.Editor.finishedDefaultHeaderGUI+=DrawHeader;
        }

        private static void DrawHeader(UnityEditor.Editor editor){
            if(editor.targets.Length!=1||!(editor.target is GameObject go)){return;}
            if(!go.TryGetComponent(out VirtualGeometryRenderer renderer)){return;}
            EditorGUILayout.Space(2);
            using(new EditorGUILayout.VerticalScope(EditorStyles.helpBox)){
                EditorGUILayout.LabelField("Virtual Geometry",EditorStyles.boldLabel);
                VirtualGeometryMeshAsset asset=renderer.MeshAsset;
                if(asset==null){
                    EditorGUILayout.HelpBox("変換データがありません。元のモデルのInspectorで「Virtual Geometry」にチェックを入れてApplyしてください。",MessageType.Warning);
                    return;
                }
                string legacy=VirtualGeometryAssetText.LegacyNotice(asset);
                if(legacy!=null){EditorGUILayout.HelpBox(legacy,MessageType.Warning);return;}
                EditorGUILayout.LabelField(VirtualGeometryAssetText.Summary(asset)+"\nGPUで使用中 "+VirtualGeometryAssetText.Bytes(renderer.DiagnosticGeometryAllocatedBytes+renderer.DiagnosticSelectionAllocatedBytes)+"\n表示: "+renderer.DiagnosticReason,EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField("Material・影はMeshRendererで設定します。MeshFilterには影・当たり判定・代替表示用の低密度Meshが入ります。",EditorStyles.wordWrappedMiniLabel);
                DrawThresholdNotice(renderer);
                DrawMaterialProblems(renderer,asset);
                DrawPipelineStatus();
                details=EditorGUILayout.Foldout(details,"詳細",true);
                if(details){DrawDetails(renderer);}
            }
        }

        // 閾値の両端はLOD選択を固定するため、距離でLODが変わらない理由を示す。
        private static void DrawThresholdNotice(VirtualGeometryRenderer renderer){
            float threshold=renderer.LodErrorThresholdPixels;
            if(threshold<=0){
                EditorGUILayout.HelpBox("LODの閾値が0のため、常に最も細かい段を描きます(距離でLODは変わりません)。",MessageType.Warning);
            }else if(!float.IsFinite(threshold)||threshold>=float.MaxValue){
                EditorGUILayout.HelpBox("LODの閾値が最大値のため、常に最も粗い段を描きます(距離でLODは変わりません)。",MessageType.Warning);
            }
        }

        // 描けないMaterialは理由を示し、対応するVirtual Geometry Shaderが無ければ作成と切替を1クリックで行えるようにする。
        private static void DrawMaterialProblems(VirtualGeometryRenderer renderer,VirtualGeometryMeshAsset asset){
            var problems=new StringBuilder();var needed=new List<Material>();
            for(int slot=0;slot<asset.MaterialCount;slot++){
                Material material=renderer.GetMaterial(slot);
                if(!VirtualGeometryMaterials.IsSupported(material,out string reason)){problems.Append("slot ").Append(slot).Append(": ").Append(reason).Append('\n');}
                if(VirtualGeometryMaterials.NeedsVirtualShader(material)&&!needed.Contains(material)){needed.Add(material);}
            }
            if(problems.Length>0){EditorGUILayout.HelpBox(problems.ToString().TrimEnd()+"\n描けない間は低密度の代替Meshで表示します。",MessageType.Warning);}
            foreach(Material material in needed){
                if(GUILayout.Button(new GUIContent("「"+material.name+"」用のVirtual Geometry Shaderを作成して切り替え","shader \""+material.shader.name+"\" から.vgshaderを作り、このMaterialのshaderを生成shaderへ切り替えます。Propertyの値は保たれます。Undoで戻せます。"))){
                    CreateVirtualShader(material);GUIUtility.ExitGUI();
                }
            }
        }

        // URP Rendererが専用描画できる設定でなければ、変更内容を示して1クリックで揃えられるようにする。
        private static void DrawPipelineStatus(){
            UniversalRendererData data=VirtualGeometryRendererSetup.ActiveRendererData(out UniversalRenderPipelineAsset pipeline);
            List<string> changes=VirtualGeometryRendererSetup.RequiredChanges(data,pipeline);
            if(changes.Count==0){return;}
            EditorGUILayout.HelpBox("URP Rendererが専用描画できる設定ではないため、低密度の代替Meshで表示されます。必要な変更:\n・"+string.Join("\n・",changes),MessageType.Warning);
            if(data!=null&&GUILayout.Button(new GUIContent("URP RendererをVirtual Geometry用に設定","上に表示された変更を、現在のURP pipelineの既定Rendererへ適用します。Undoで戻せます。"))){
                if(EditorUtility.DisplayDialog("URP Rendererの設定",AssetDatabase.GetAssetPath(data)+" に次の変更を加えます。\n\n・"+string.Join("\n・",changes),"適用","キャンセル")){VirtualGeometryRendererSetup.Apply(data,pipeline);}
                GUIUtility.ExitGUI();
            }
        }

        private static void DrawDetails(VirtualGeometryRenderer renderer){
            var serialized=new SerializedObject(renderer);
            serialized.Update();
            EditorGUILayout.PropertyField(serialized.FindProperty("lodErrorThresholdPixels"),new GUIContent("LODの閾値 (pixel)","許容する画面上の誤差。大きいほど早く粗い段へ切り替わり、描画三角形が減ります。"));
            EditorGUILayout.PropertyField(serialized.FindProperty("lodHysteresis"),new GUIContent("切替の余裕","細かい段と粗い段の切替閾値に持たせる余裕の割合。境界付近での往復を防ぎます。"));
            EditorGUILayout.PropertyField(serialized.FindProperty("streamingMode"),new GUIContent("読み込み方式","Streaming: 必要なpageだけ読み込みます。AllResident: 全geometryを常駐させます(比較用)。"));
            SerializedProperty streaming=serialized.FindProperty("streamingSettings");
            foreach(string name in new[]{"GpuGeometryBudgetBytes","CpuCacheBudgetBytes","IoConcurrency","TransferBytesPerFrame","RequestQueueCapacity","RequestTimeoutFrames"}){
                EditorGUILayout.PropertyField(streaming.FindPropertyRelative(name));
            }
            using(new EditorGUI.DisabledScope(true)){
                EditorGUILayout.ObjectField(new GUIContent("変換データ","元のモデルのインポートで作られます。"),renderer.MeshAsset,typeof(VirtualGeometryMeshAsset),false);
            }
            serialized.ApplyModifiedProperties();
        }

        private static void CreateVirtualShader(Material material){
            if(material==null){return;}
            Shader source=material.shader;
            if(!EditorUtility.DisplayDialog("Virtual Geometry Shaderの作成","shader \""+source.name+"\" からVirtual Geometry Shader(.vgshader)を作り、Material \""+material.name+"\" のshaderをそれへ切り替えます。\n生成shaderは元のpassをすべて含むため、このMaterialは通常のMeshでも同じように描けます。","作成して切り替え","キャンセル")){return;}
            string path=VirtualGeometryShaderImporter.Create(source);
            Shader generated=AssetDatabase.LoadAssetAtPath<Shader>(path);
            if(generated==null){EditorUtility.DisplayDialog("Virtual Geometry Shaderの作成",path+" を変換できませんでした。ConsoleのVirtual Geometry Shaderのimportエラーに理由があります。","OK");return;}
            Undo.RecordObject(material,"Use Virtual Geometry Shader");
            int queue=material.renderQueue;material.shader=generated;material.renderQueue=queue;EditorUtility.SetDirty(material);
        }
    }
}
