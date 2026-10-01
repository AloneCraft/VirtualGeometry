using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// Virtual Geometry Shader(.vgshader)。参照先shader(.shader / Shader Graph)から、元の全passとVG用forward passを持つShaderを生成する。
    /// Materialのshaderにこれを選ぶと、同じMaterialで通常Mesh・Proxy・影とVG描画の両方に使える。参照先を編集すると再生成される。
    /// </summary>
    [ScriptedImporter(1,"vgshader")]
    public sealed class VirtualGeometryShaderImporter:ScriptedImporter{
        [SerializeField,Tooltip("変換元のshader(.shaderまたはShader Graph)。URPのforward passを持つものに対応します。")] private Shader source;
        public Shader Source{get=>this.source;set=>this.source=value;}

        public override void OnImportAsset(AssetImportContext ctx){
            if(this.source==null){ctx.LogImportError("Virtual Geometry Shader: 変換元のshaderが設定されていません。");return;}
            string sourcePath=AssetDatabase.GetAssetPath(this.source);
            if(string.IsNullOrEmpty(sourcePath)){ctx.LogImportError("Virtual Geometry Shader: 変換元shaderがproject内のassetではありません。");return;}
            ctx.DependsOnSourceAsset(sourcePath);
            string text;string baseAssetPath=sourcePath;Texture[] defaultTextures=null;string[] defaultTextureNames=null;
            if(sourcePath.EndsWith(".shadergraph",StringComparison.OrdinalIgnoreCase)){
                // Shader Graphは生成コードを使う。sub graphの変更はShader Graph側の再importとして伝わる。
                ctx.DependsOnArtifact(sourcePath);
                text=ShaderGraphText(sourcePath,out defaultTextureNames,out defaultTextures,out string error);
                if(text==null){ctx.LogImportError("Virtual Geometry Shader: Shader Graphの生成コードを取得できません: "+error);return;}
                baseAssetPath=null;
            }else{
                text=File.ReadAllText(FileUtil.GetPhysicalPath(sourcePath));
            }
            VirtualGeometryShaderConversion conversion=VirtualGeometryShaderSource.Convert(text,baseAssetPath,ResolvePhysical);
            foreach(string dependency in conversion.Dependencies){
                if(dependency.StartsWith("Assets/",StringComparison.Ordinal)){ctx.DependsOnSourceAsset(dependency);}
            }
            if(conversion.Source==null){ctx.LogImportError("Virtual Geometry Shader: "+sourcePath+" を変換できません: "+conversion.Error);return;}
            Shader shader=ShaderUtil.CreateShaderAsset(ctx,conversion.Source,true);
            if(shader==null){ctx.LogImportError("Virtual Geometry Shader: 生成したshaderを作成できません。");return;}
            if(defaultTextureNames!=null){EditorMaterialUtility.SetShaderDefaults(shader,defaultTextureNames,defaultTextures);}
            foreach(ShaderMessage message in ShaderUtil.GetShaderMessages(shader)){
                string detail=message.message+" ("+message.file+":"+message.line+")";
                if(message.severity==UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error){ctx.LogImportError("Virtual Geometry Shader: "+detail);}
                else{ctx.LogImportWarning("Virtual Geometry Shader: "+detail);}
            }
            ctx.AddObjectToAsset("Shader",shader);ctx.SetMainObject(shader);
        }

        private static string ResolvePhysical(string assetPath){
            try{return FileUtil.GetPhysicalPath(assetPath);}catch(Exception){return null;}
        }

        /// <summary>Shader Graphの生成コードと既定textureを内部API(ShaderGraphImporter.GetShaderText)から得る。版で変わり得るため失敗は理由付きで返す。</summary>
        private static string ShaderGraphText(string path,out string[] textureNames,out Texture[] textures,out string error){
            textureNames=null;textures=null;error=null;
            Type importer=Type.GetType("UnityEditor.ShaderGraph.ShaderGraphImporter, Unity.ShaderGraph.Editor");
            if(importer==null){error="Shader Graph packageが見つかりません。";return null;}
            MethodInfo method=null;
            foreach(MethodInfo candidate in importer.GetMethods(BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public)){
                if(candidate.Name!="GetShaderText"){continue;}
                ParameterInfo[] parameters=candidate.GetParameters();
                if(parameters.Length==2&&parameters[0].ParameterType==typeof(string)&&parameters[1].IsOut){method=candidate;break;}
            }
            if(method==null){error="ShaderGraphImporter.GetShaderText(string, out …)が見つかりません。";return null;}
            object[] arguments={path,null};
            string text;
            try{text=(string)method.Invoke(null,arguments);}catch(TargetInvocationException exception){error=exception.InnerException?.Message??exception.Message;return null;}
            if(arguments[1] is IList list){
                textureNames=new string[list.Count];textures=new Texture[list.Count];
                for(int i=0;i<list.Count;i++){
                    object info=list[i];Type type=info.GetType();
                    textureNames[i]=(string)type.GetField("name")?.GetValue(info);
                    object id=type.GetField("textureId")?.GetValue(info);
                    if(id is EntityId entity){textures[i]=EditorUtility.EntityIdToObject(entity) as Texture;}
                }
            }
            return text;
        }

        private const string Template="// Virtual Geometry Shader: 変換元はInspectorのSourceで指定します。\n";

        /// <summary>選択shaderを参照する.vgshaderを同じフォルダに作る。MaterialのshaderにはVirtualGeometry/<元の名前>を選ぶ。</summary>
        public static string Create(Shader shader){
            string sourcePath=AssetDatabase.GetAssetPath(shader);
            string folder="Assets";
            if(sourcePath.StartsWith("Assets/",StringComparison.Ordinal)){folder=Path.GetDirectoryName(sourcePath).Replace('\\','/');}
            string path=AssetDatabase.GenerateUniqueAssetPath(folder+"/"+Path.GetFileNameWithoutExtension(sourcePath)+".vgshader");
            File.WriteAllText(path,Template);
            AssetDatabase.ImportAsset(path);
            var importer=(VirtualGeometryShaderImporter)GetAtPath(path);
            importer.source=shader;importer.SaveAndReimport();
            return path;
        }

        [MenuItem("Assets/Virtual Geometry Shaderを作成",true)]
        private static bool ValidateCreate(){return Selection.activeObject is Shader shader&&!(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(shader)) is VirtualGeometryShaderImporter);}

        [MenuItem("Assets/Virtual Geometry Shaderを作成",false,20)]
        private static void CreateFromSelection(){
            string path=Create((Shader)Selection.activeObject);
            Object created=AssetDatabase.LoadMainAssetAtPath(path);Selection.activeObject=created;EditorGUIUtility.PingObject(created);
        }
    }
}
