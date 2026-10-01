using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace VirtualizedGeometry.Editor{
    /// <summary>.vgshaderのInspector。変換元の指定に加え、生成shader名と次の操作(Materialへの設定)を示す。</summary>
    [CustomEditor(typeof(VirtualGeometryShaderImporter))]
    internal sealed class VirtualGeometryShaderImporterEditor:ScriptedImporterEditor{
        public override void OnInspectorGUI(){
            this.serializedObject.Update();
            EditorGUILayout.PropertyField(this.serializedObject.FindProperty("source"));
            this.serializedObject.ApplyModifiedProperties();
            this.ApplyRevertGUI();
            var importer=(VirtualGeometryShaderImporter)this.target;
            Shader generated=AssetDatabase.LoadAssetAtPath<Shader>(importer.assetPath);
            if(importer.Source==null){
                EditorGUILayout.HelpBox("変換元のshader(.shaderまたはShader Graph)を指定してApplyを押してください。",MessageType.Info);return;
            }
            if(generated==null){
                EditorGUILayout.HelpBox("shaderを生成できませんでした。ConsoleのVirtual Geometry Shaderのimportエラーに理由があります。",MessageType.Error);return;
            }
            if(ShaderUtil.ShaderHasError(generated)){
                EditorGUILayout.HelpBox("生成したshaderにcompile errorがあります。Consoleを確認してください。",MessageType.Error);
            }
            EditorGUILayout.HelpBox("Materialのshaderに「"+generated.name+"」を選ぶと、同じMaterialで通常Mesh・影とVirtual Geometryの両方を描けます。変換元を編集すると自動で再生成されます。",MessageType.Info);
            if(GUILayout.Button("このshaderを使うMaterialを作成")){
                string folder=Path.GetDirectoryName(importer.assetPath).Replace('\\','/');
                string path=AssetDatabase.GenerateUniqueAssetPath(folder+"/"+Path.GetFileNameWithoutExtension(importer.assetPath)+".mat");
                var material=new Material(generated);AssetDatabase.CreateAsset(material,path);
                Selection.activeObject=material;EditorGUIUtility.PingObject(material);
            }
        }
    }
}
