using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualizedGeometry.Editor;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Samples.SpaceElevator.Editor{
    /// <summary>
    /// space_elevator.fbx の 33 マテリアルを、参照画像「地上が、宇宙へのはじまり。」へ寄せた外部 Material(SpaceElevator/Materials/SE_GA_*.mat)として作り、
    /// FBX の埋め込みマテリアルをそれへ remap する。あわせて FBX のライト・カメラの取り込みを止める(照明は SpaceElevator.unity 側で持つ)。
    /// メニュー: Tools/Virtualized Geometry/Space Elevator/Build Materials。batchmode: -executeMethod ...SpaceElevatorMaterialBuilder.Run。
    /// レシピ表の初期値は Blender の生成スクリプト(se_ground_anchor.py:35-78、se_common.py:434-524)の引数で、参照画像に合わせて変えた値には理由を書く。
    /// 値は Blender と同じ放射量の単位(太陽 9 W/m² 相当・発光強度 22 など)で、露出は SpaceElevator.unity の Volume(Post Exposure)で合わせる。
    /// 使用 scene: SpaceElevator.unity、Import.unity(どちらも同じ FBX を置く)。
    /// </summary>
    public static class SpaceElevatorMaterialBuilder{
        public const string Root="Assets/VirtualizedGeometrySample/SpaceElevator";
        public const string MaterialFolder=Root+"/Materials";
        public const string ShaderFolder=Root+"/Shaders";
        public const string ProceduralShaderPath=ShaderFolder+"/SpaceElevatorProcedural.shader";
        public const string CloudsShaderPath=ShaderFolder+"/SpaceElevatorClouds.shader";
        private const string LitShader="Universal Render Pipeline/Lit";
        private const string ComplexLitShader="Universal Render Pipeline/Complex Lit";
        private const float BlenderCoatRoughness=0.03f;

        private enum Kind{Panel,Steel,WetGround,Terrain,Water,Clouds,Simple,Emission}

        /// <summary>1 マテリアル分の作り方。値は線形(Blender と同じ)で持ち、書き込み時に shader の [HDR] 有無で変換する。</summary>
        private sealed class Recipe{
            public string Name;
            public Kind Kind;
            public string Source;
            public readonly List<KeyValuePair<string,object>> Values=new List<KeyValuePair<string,object>>();
            public Recipe Set(string property,object value){this.Values.Add(new KeyValuePair<string,object>(property,value));return this;}
        }

        [MenuItem("Tools/Virtualized Geometry/Space Elevator/Build Materials")]
        private static void BuildFromMenu(){
            Apply();
            Debug.Log("Space Elevator: マテリアルを更新しました。");
        }

        /// <summary>batchmode の入口。失敗したら終了コード 1。</summary>
        public static void Run(){
            try{
                Apply();
                EditorApplication.Exit(0);
            }catch(Exception error){
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// Material・vgshader・FBX の設定を、レシピ表どおりに作成または更新する。差分が無いものは書き換えない(GUID と参照を保つ)。
        /// </summary>
        public static void Apply(){
            Directory.CreateDirectory(MaterialFolder);
            AssetDatabase.ImportAsset(ProceduralShaderPath);
            AssetDatabase.ImportAsset(CloudsShaderPath);
            Shader procedural=EnsureVirtualShader();
            var materials=new Dictionary<string,Material>();
            AssetDatabase.StartAssetEditing();
            try{
                foreach(Recipe recipe in Recipes()){
                    Material desired=Build(recipe,procedural);
                    string path=MaterialFolder+"/"+recipe.Name+".mat";
                    Material existing=AssetDatabase.LoadAssetAtPath<Material>(path);
                    if(existing==null){
                        AssetDatabase.CreateAsset(desired,path);
                        materials[recipe.Name]=desired;
                        continue;
                    }
                    if(Compare(existing,desired).Count>0){
                        existing.shader=desired.shader;
                        existing.CopyPropertiesFromMaterial(desired);
                        existing.shaderKeywords=desired.shaderKeywords;
                        existing.renderQueue=desired.renderQueue;
                        existing.globalIlluminationFlags=desired.globalIlluminationFlags;
                        existing.doubleSidedGI=desired.doubleSidedGI;
                        EditorUtility.SetDirty(existing);
                    }
                    Object.DestroyImmediate(desired);
                    materials[recipe.Name]=existing;
                }
            }finally{
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.SaveAssets();
            ApplyImporter(materials);
        }

        /// <summary>
        /// レシピ表と資産の差分(書き込みはしない)。テストで「資産がレシピ表と同期している」ことを確かめるのに使う。
        /// </summary>
        public static List<string> Differences(){
            var result=new List<string>();
            Shader procedural=FindVirtualShader(out string vgPath);
            if(procedural==null){result.Add("SpaceElevatorProcedural.vgshader がありません。");return result;}
            var materials=new Dictionary<string,Material>();
            foreach(Recipe recipe in Recipes()){
                string path=MaterialFolder+"/"+recipe.Name+".mat";
                Material existing=AssetDatabase.LoadAssetAtPath<Material>(path);
                if(existing==null){result.Add(path+" がありません。");continue;}
                materials[recipe.Name]=existing;
                Material desired=Build(recipe,procedural);
                foreach(string difference in Compare(existing,desired)){result.Add(recipe.Name+": "+difference);}
                Object.DestroyImmediate(desired);
            }
            var importer=(ModelImporter)AssetImporter.GetAtPath(SpaceElevatorPatternSpace.ModelPath);
            if(importer.importLights){result.Add("FBX の importLights が true です。");}
            if(importer.importCameras){result.Add("FBX の importCameras が true です。");}
            Dictionary<AssetImporter.SourceAssetIdentifier,Object> map=importer.GetExternalObjectMap();
            foreach(KeyValuePair<string,Material> pair in materials){
                var key=new AssetImporter.SourceAssetIdentifier(typeof(Material),pair.Key);
                if(!map.TryGetValue(key,out Object mapped)||mapped!=pair.Value){result.Add(pair.Key+" が remap されていません。");}
            }
            return result;
        }

        /// <summary>レシピ表のマテリアル名(FBX のマテリアル名と同じ)。</summary>
        public static IReadOnlyList<string> MaterialNames(){
            return Recipes().Select(recipe=>recipe.Name).ToList();
        }

        /// <summary>半透明で VG の専用描画から外れる(仕様どおり Proxy で描かれる)マテリアル名。</summary>
        public static IReadOnlyList<string> TransparentMaterialNames(){
            return Recipes().Where(recipe=>recipe.Kind==Kind.Clouds).Select(recipe=>recipe.Name).ToList();
        }

        // VirtualGeometry/SpaceElevator/Procedural を生成する .vgshader を1つだけ持つ(Create は毎回新しいパスを作るため、既存を探してから作る)。
        private static Shader EnsureVirtualShader(){
            Shader shader=FindVirtualShader(out string path);
            if(shader!=null){return shader;}
            Shader source=AssetDatabase.LoadAssetAtPath<Shader>(ProceduralShaderPath);
            if(source==null){throw new InvalidOperationException(ProceduralShaderPath+" を読み込めません。");}
            // 変換元が外れた .vgshader が既定の場所に残っていれば、増やさずに変換元を設定し直す。
            path=ShaderFolder+"/SpaceElevatorProcedural.vgshader";
            if(AssetImporter.GetAtPath(path) is VirtualGeometryShaderImporter existing){
                existing.Source=source;
                EditorUtility.SetDirty(existing);
                existing.SaveAndReimport();
            }else{
                path=VirtualGeometryShaderImporter.Create(source);
            }
            shader=AssetDatabase.LoadAssetAtPath<Shader>(path);
            if(shader==null){throw new InvalidOperationException(path+" から Virtual Geometry Shader を生成できません(Console の import エラーを確認してください)。");}
            return shader;
        }

        private static Shader FindVirtualShader(out string path){
            Shader source=AssetDatabase.LoadAssetAtPath<Shader>(ProceduralShaderPath);
            foreach(string guid in AssetDatabase.FindAssets("",new[]{ShaderFolder})){
                string candidate=AssetDatabase.GUIDToAssetPath(guid);
                if(!candidate.EndsWith(".vgshader",StringComparison.OrdinalIgnoreCase)){continue;}
                if(AssetImporter.GetAtPath(candidate) is VirtualGeometryShaderImporter importer&&importer.Source==source){
                    path=candidate;
                    return AssetDatabase.LoadAssetAtPath<Shader>(candidate);
                }
            }
            path=null;
            return null;
        }

        private static void ApplyImporter(Dictionary<string,Material> materials){
            var importer=(ModelImporter)AssetImporter.GetAtPath(SpaceElevatorPatternSpace.ModelPath);
            bool changed=false;
            if(importer.importLights){importer.importLights=false;changed=true;}
            if(importer.importCameras){importer.importCameras=false;changed=true;}
            Dictionary<AssetImporter.SourceAssetIdentifier,Object> map=importer.GetExternalObjectMap();
            foreach(KeyValuePair<string,Material> pair in materials){
                var key=new AssetImporter.SourceAssetIdentifier(typeof(Material),pair.Key);
                if(map.TryGetValue(key,out Object mapped)&&mapped==pair.Value){continue;}
                importer.AddRemap(key,pair.Value);
                changed=true;
            }
            // 再インポートは VG の再変換(約 24 s)を伴うため、差分があるときだけ1回行う。
            if(changed){importer.SaveAndReimport();}
        }

        private static Material Build(Recipe recipe,Shader procedural){
            Shader shader;
            switch(recipe.Kind){
                case Kind.Clouds:
                    shader=AssetDatabase.LoadAssetAtPath<Shader>(CloudsShaderPath);
                    break;
                case Kind.Simple:
                case Kind.Emission:
                    if(UsesCoat(recipe)){shader=Shader.Find(ComplexLitShader);}
                    else{shader=Shader.Find(LitShader);}
                    break;
                default:
                    shader=procedural;
                    break;
            }
            if(shader==null){throw new InvalidOperationException(recipe.Name+" の shader が見つかりません。");}
            var material=new Material(shader){name=recipe.Name};
            if(recipe.Kind==Kind.Emission){material.globalIlluminationFlags=MaterialGlobalIlluminationFlags.RealtimeEmissive;}
            foreach(KeyValuePair<string,object> value in recipe.Values){
                if(!material.HasProperty(value.Key)){throw new InvalidOperationException(recipe.Name+": shader "+shader.name+" に "+value.Key+" がありません。");}
                switch(value.Value){
                    case float number:material.SetFloat(value.Key,number);break;
                    case Color color:SetLinearColor(material,value.Key,color);break;
                    case Vector4 vector:material.SetVector(value.Key,vector);break;
                    default:throw new InvalidOperationException(recipe.Name+": "+value.Key+" の型が不明です。");
                }
            }
            switch(recipe.Kind){
                case Kind.Simple:
                case Kind.Emission:
                    // URP の ShaderGUI と同じ規則で keyword(_SPECULAR_SETUP・_CLEARCOAT・_EMISSION・反射オフ)を決める。
                    BaseShaderGUI.SetMaterialKeywords(material,LitGUI.SetMaterialKeywords);
                    break;
                case Kind.Clouds:
                    material.renderQueue=(int)RenderQueue.Transparent;
                    break;
                default:
                    // KeywordEnum / Toggle の keyword を property の値から設定する(Inspector で開いたときと同じ結果)。
                    MaterialEditor.ApplyMaterialPropertyDrawers(material);
                    break;
            }
            return material;
        }

        private static string ShaderName(Shader shader){
            if(shader==null){return "null";}
            return shader.name;
        }

        private static bool UsesCoat(Recipe recipe){
            foreach(KeyValuePair<string,object> value in recipe.Values){
                if(value.Key=="_ClearCoat"&&value.Value is float coat&&coat>0f){return true;}
            }
            return false;
        }

        // [HDR] の Color は Unity が線形のまま渡し、それ以外は gamma として保存して線形へ戻す(URP の MaterialPostprocessor の注記)。
        private static void SetLinearColor(Material material,string property,Color linear){
            if(IsHdr(material.shader,property)){material.SetColor(property,linear);}
            else{material.SetColor(property,linear.gamma);}
        }

        /// <summary>Material の Color を線形値で読む(テスト用)。</summary>
        public static Color GetLinearColor(Material material,string property){
            Color stored=material.GetColor(property);
            if(IsHdr(material.shader,property)){return stored;}
            return stored.linear;
        }

        private static bool IsHdr(Shader shader,string property){
            int index=shader.FindPropertyIndex(property);
            if(index<0){return false;}
            return (shader.GetPropertyFlags(index)&ShaderPropertyFlags.HDR)!=0;
        }

        private static List<string> Compare(Material existing,Material desired){
            var result=new List<string>();
            if(existing.shader!=desired.shader){result.Add("shader "+ShaderName(existing.shader)+" → "+ShaderName(desired.shader));return result;}
            Shader shader=desired.shader;
            for(int i=0;i<shader.GetPropertyCount();i++){
                string name=shader.GetPropertyName(i);
                switch(shader.GetPropertyType(i)){
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        if(Math.Abs(existing.GetFloat(name)-desired.GetFloat(name))>1e-5f){result.Add(name+" "+existing.GetFloat(name).ToString(CultureInfo.InvariantCulture)+" → "+desired.GetFloat(name).ToString(CultureInfo.InvariantCulture));}
                        break;
                    case ShaderPropertyType.Color:
                    case ShaderPropertyType.Vector:
                        if((existing.GetVector(name)-desired.GetVector(name)).sqrMagnitude>1e-9f){result.Add(name+" "+existing.GetVector(name)+" → "+desired.GetVector(name));}
                        break;
                }
            }
            string have=string.Join(" ",existing.shaderKeywords.OrderBy(keyword=>keyword,StringComparer.Ordinal));
            string want=string.Join(" ",desired.shaderKeywords.OrderBy(keyword=>keyword,StringComparer.Ordinal));
            if(have!=want){result.Add("keywords ["+have+"] → ["+want+"]");}
            if(existing.renderQueue!=desired.renderQueue){result.Add("renderQueue "+existing.renderQueue+" → "+desired.renderQueue);}
            return result;
        }

        // ---- レシピ表 ----------------------------------------------------------------------------------------------
        // 色は Blender と同じ線形値。Haze の発光色は Blender の HAZE_COL × HAZE_STRENGTH(se_ground_anchor.py:82-92)。
        private static readonly Color Haze=new Color(0.55f*1.25f,0.68f*1.25f,0.86f*1.25f,1f);
        private static readonly Color Warm=new Color(1f,0.6f,0.28f,1f);

        private static List<Recipe> Recipes(){
            var list=new List<Recipe>();
            // 塗装・コンクリートのパネル(mat_panel)。参照画像寄せ: 雨だれ(_StreakStrength)・縦リブ(_RibPeriod/_RibStrength)・粒(_GrainStrength)・暖寒ムラ(_TintStrength)を追加し、
            // 参照画像の汚れの多い外装に合わせて汚れ(_Grime)と目地の暗さ(_LineDark)を Blender より強める。
            list.Add(Panel("SE_GA_Panel","se_ground_anchor.py:38",new Color(0.60f,0.60f,0.585f),0.07f,0.25f,0.42f,new Vector3(5f,5f,3.5f),0.06f,0.6f,0.35f,0.25f)
                .Set("_AccentColor",new Color(0.47f,0.48f,0.49f)).Set("_AccentFraction",0.07f)
                // 参照画像は塔の窓明かりが多いため、点灯パネルの割合を Blender の 0.035 から 0.15 へ上げる。
                .Set("_LightColor",Warm).Set("_LightFraction",0.15f).Set("_LightStrength",10f)
                .Set("_StreakStrength",0.45f).Set("_RibPeriod",1.25f).Set("_RibStrength",0.04f).Set("_GrainStrength",0.12f).Set("_TintStrength",0.2f));
            list.Add(Panel("SE_GA_PanelAlt","se_ground_anchor.py:41",new Color(0.68f,0.67f,0.64f),0.06f,0.2f,0.45f,new Vector3(4f,4f,2.5f),0.06f,0.6f,0.35f,0.25f)
                .Set("_AccentColor",new Color(0.55f,0.55f,0.53f)).Set("_AccentFraction",0.06f)
                // 参照画像は外装全体に灯りが散っているため、Blender に無い点灯窓を少し足す。
                .Set("_LightColor",Warm).Set("_LightFraction",0.06f).Set("_LightStrength",10f)
                .Set("_StreakStrength",0.4f).Set("_RibPeriod",0.9f).Set("_RibStrength",0.03f).Set("_GrainStrength",0.12f).Set("_TintStrength",0.2f));
            list.Add(Panel("SE_GA_PanelDark","se_ground_anchor.py:43",new Color(0.26f,0.27f,0.285f),0.12f,0.6f,0.35f,new Vector3(2f,2f,2f),0.04f,0.5f,0.15f,0.25f)
                .Set("_LightColor",Warm).Set("_LightFraction",0.05f).Set("_LightStrength",10f)
                .Set("_StreakStrength",0.2f).Set("_RibPeriod",0.5f).Set("_RibStrength",0.01f).Set("_GrainStrength",0.08f).Set("_TintStrength",0.1f));
            list.Add(Panel("SE_GA_Concrete","se_ground_anchor.py:45",new Color(0.56f,0.555f,0.53f),0.06f,0f,0.82f,new Vector3(6f,6f,3f),0.04f,0.45f,0.5f,0.15f)
                .Set("_StreakStrength",0.55f).Set("_RibPeriod",2f).Set("_RibStrength",0.04f).Set("_GrainStrength",0.25f).Set("_TintStrength",0.25f));
            list.Add(Panel("SE_GA_ConcreteDark","se_ground_anchor.py:47",new Color(0.36f,0.355f,0.34f),0.06f,0f,0.85f,new Vector3(4f,4f,2f),0.03f,0.5f,0.35f,0.25f)
                .Set("_StreakStrength",0.5f).Set("_RibPeriod",1.5f).Set("_RibStrength",0.03f).Set("_GrainStrength",0.25f).Set("_TintStrength",0.2f));
            // コンテナは波板(短い周期のリブ)
            list.Add(Panel("SE_GA_ContainerA","se_ground_anchor.py:70",new Color(0.75f,0.76f,0.76f),0.04f,0.2f,0.5f,new Vector3(0.6f,0.6f,5f),0.05f,0.5f,0.25f,0.25f)
                .Set("_StreakStrength",0.2f).Set("_RibPeriod",0.3f).Set("_RibStrength",0.015f).Set("_GrainStrength",0.1f).Set("_TintStrength",0.1f));
            list.Add(Panel("SE_GA_ContainerB","se_ground_anchor.py:72",new Color(0.12f,0.24f,0.42f),0.05f,0.2f,0.5f,new Vector3(0.6f,0.6f,5f),0.05f,0.5f,0.3f,0.25f)
                .Set("_StreakStrength",0.2f).Set("_RibPeriod",0.3f).Set("_RibStrength",0.015f).Set("_GrainStrength",0.1f).Set("_TintStrength",0.1f));
            // テザーの鋼材(mat_steel)。Blender には凹凸が無いため Bump 0。
            list.Add(Procedural("SE_GA_TetherSteel",Kind.Steel,"se_ground_anchor.py:49").Set("_BaseColor",new Color(0.74f,0.75f,0.78f)).Set("_Roughness",0.2f).Set("_Band",8f).Set("_Bump",0f).Set("_Metallic",1f));
            // 濡れた広場(mat_wet_ground)。参照画像は路面の大半が濡れて光るため、水たまりの閾値を Blender の 0.44 から 0.40 へ下げて範囲を広げ、
            // 水たまり以外も Blender(粗さ 0.22)より滑らかにする。路面は参照画像の明るい暖色のグレーに合わせて版の色を明るくする。
            list.Add(Procedural("SE_GA_Plaza",Kind.WetGround,"se_ground_anchor.py:95")
                .Set("_SlabColorA",new Color(0.30f,0.295f,0.285f)).Set("_SlabColorB",new Color(0.38f,0.37f,0.355f)).Set("_StainColor",new Color(0.14f,0.14f,0.135f))
                .Set("_LineWidth",0.06f).Set("_LineDark",0.7f).Set("_Bump",0.25f).Set("_SpecularLevel",0.6f).Set("_GrainStrength",0.15f)
                .Set("_WetThreshold",0.40f).Set("_WetSoftness",0.08f).Set("_DryRoughness",0.14f).Set("_WetRoughness",0.015f)
                .Set("_PlanarStrength",1f).Set("_PlanarDistortion",0.05f)
                .Set("_Haze",1f).Set("_HazeColor",Haze).Set("_HazeDistance",38000f).Set("_HazeMax",0.85f));
            // 山岳地形(mat_terrain)。面が下向きなので両面描画にして裏面の法線を反転する。
            list.Add(Procedural("SE_GA_Terrain",Kind.Terrain,"se_ground_anchor.py:117").Set("_Cull",0f)
                .Set("_PlainColorA",new Color(0.13f,0.12f,0.085f)).Set("_PlainColorB",new Color(0.25f,0.21f,0.15f)).Set("_MossColor",new Color(0.09f,0.1f,0.06f))
                .Set("_RockColorA",new Color(0.16f,0.155f,0.15f)).Set("_RockColorB",new Color(0.34f,0.32f,0.3f)).Set("_SnowColor",new Color(0.86f,0.88f,0.92f)).Set("_SnowLine",1250f)
                .Set("_Bump",0.6f).Set("_SpecularLevel",0.3f)
                .Set("_Haze",1f).Set("_HazeColor",Haze).Set("_HazeDistance",38000f).Set("_HazeMax",0.85f));
            // 湖面(mat_water)。面が下向きなので両面描画。
            list.Add(Procedural("SE_GA_Lake",Kind.Water,"se_ground_anchor.py:143").Set("_Cull",0f)
                .Set("_BaseColor",new Color(0.01f,0.03f,0.045f)).Set("_Roughness",0.03f).Set("_SpecularLevel",0.8f).Set("_Bump",0.05f)
                .Set("_PlanarStrength",1f).Set("_PlanarDistortion",0.03f)
                .Set("_Haze",1f).Set("_HazeColor",Haze).Set("_HazeDistance",38000f).Set("_HazeMax",0.85f));
            // 雲(mat_clouds)。半透明なので CloudLayer だけ VG の Proxy で描かれる。
            list.Add(new Recipe{Name="SE_GA_Clouds",Kind=Kind.Clouds,Source="se_ground_anchor.py:153"}
                .Set("_LitColor",new Color(1f,0.98f,0.95f)).Set("_ShadeColor",new Color(0.72f,0.76f,0.85f)).Set("_Brightness",1.4f)
                // 参照画像は積雲が多いため、Blender の雲量より厚くする
                .Set("_Coverage",0.08f)
                .Set("_HazeColor",Haze).Set("_HazeDistance",38000f).Set("_HazeMax",0.85f));
            // 単純な Principled(mat_simple)。coat は Complex Lit の Clear Coat、GlassDark の Specular IOR Level 0.8 は specular workflow の F0 0.064。
            list.Add(Simple("SE_GA_GlassDark","se_ground_anchor.py:55",new Color(0.015f,0.02f,0.025f),0f,0.04f,0f).Set("_WorkflowMode",0f).Set("_SpecColor",new Color(0.064f,0.064f,0.064f)));
            list.Add(Simple("SE_GA_PaintYellow","se_ground_anchor.py:56",new Color(0.85f,0.55f,0.04f),0f,0.45f,0f));
            list.Add(Simple("SE_GA_PaintWhite","se_ground_anchor.py:57",new Color(0.8f,0.8f,0.78f),0f,0.45f,0f));
            list.Add(Simple("SE_GA_PaintOrange","se_ground_anchor.py:58",new Color(0.9f,0.22f,0.02f),0f,0.5f,0f));
            list.Add(Simple("SE_GA_PaintRed","se_ground_anchor.py:59",new Color(0.6f,0.04f,0.03f),0f,0.5f,0f));
            list.Add(Simple("SE_GA_Rubber","se_ground_anchor.py:60",new Color(0.025f,0.025f,0.025f),0f,0.85f,0f));
            list.Add(Simple("SE_GA_HiVis","se_ground_anchor.py:61",new Color(0.7f,0.95f,0.03f),0f,0.6f,0f));
            list.Add(Simple("SE_GA_HiVisOrange","se_ground_anchor.py:62",new Color(1f,0.35f,0.02f),0f,0.6f,0f));
            list.Add(Simple("SE_GA_ClothDark","se_ground_anchor.py:63",new Color(0.035f,0.04f,0.055f),0f,0.85f,0f));
            list.Add(Simple("SE_GA_Skin","se_ground_anchor.py:64",new Color(0.5f,0.33f,0.24f),0f,0.6f,0f));
            list.Add(Simple("SE_GA_Helmet","se_ground_anchor.py:65",new Color(0.88f,0.88f,0.86f),0f,0.3f,0.5f));
            list.Add(Simple("SE_GA_HelmetYellow","se_ground_anchor.py:66",new Color(0.9f,0.7f,0.05f),0f,0.3f,0.5f));
            list.Add(Simple("SE_GA_VehicleWhite","se_ground_anchor.py:67",new Color(0.82f,0.83f,0.83f),0f,0.3f,0.6f));
            list.Add(Simple("SE_GA_VehicleYellow","se_ground_anchor.py:68",new Color(0.85f,0.52f,0.02f),0f,0.35f,0.4f));
            list.Add(Simple("SE_GA_VehicleGrey","se_ground_anchor.py:69",new Color(0.3f,0.31f,0.32f),0.4f,0.4f,0f));
            list.Add(Simple("SE_GA_Stencil","se_ground_anchor.py:74",new Color(0.08f,0.085f,0.09f),0f,0.7f,0f));
            // 発光(mat_emission)。Blender の Emission シェーダーは反射を持たないので、黒ベースで鏡面と環境反射を切る。
            list.Add(Emission("SE_GA_LampWarm","se_ground_anchor.py:50",Warm,22f));
            list.Add(Emission("SE_GA_LampWhite","se_ground_anchor.py:51",new Color(1f,0.9f,0.75f),30f));
            list.Add(Emission("SE_GA_LampRed","se_ground_anchor.py:52",new Color(1f,0.05f,0.02f),20f));
            list.Add(Emission("SE_GA_Beacon","se_ground_anchor.py:53",new Color(1f,0.45f,0.02f),15f));
            list.Add(Emission("SE_GA_TunnelEnd","se_ground_anchor.py:54",new Color(1f,0.8f,0.55f),2.5f));
            return list;
        }

        private static Recipe Procedural(string name,Kind kind,string source){
            var recipe=new Recipe{Name=name,Kind=kind,Source=source};
            string keyword="Panel";
            switch(kind){
                case Kind.Steel:keyword="Steel";break;
                case Kind.WetGround:keyword="WetGround";break;
                case Kind.Terrain:keyword="Terrain";break;
                case Kind.Water:keyword="Water";break;
            }
            recipe.Set("_Recipe",(float)Array.IndexOf(new[]{"Panel","Steel","WetGround","Terrain","Water"},keyword));
            recipe.Set("_PatternScale",SpaceElevatorPatternSpace.MeshToBlenderScale);
            return recipe;
        }

        private static Recipe Panel(string name,string source,Color color,float variation,float metallic,float roughness,Vector3 size,float lineWidth,float lineDark,float grime,float bump){
            return Procedural(name,Kind.Panel,source).Set("_BaseColor",color).Set("_Variation",variation).Set("_Metallic",metallic).Set("_Roughness",roughness)
                .Set("_PanelSize",new Vector4(size.x,size.y,size.z,0f)).Set("_LineWidth",lineWidth).Set("_LineDark",lineDark).Set("_Grime",grime).Set("_Bump",bump)
                .Set("_GrimeColor",new Color(0.45f,0.45f,0.45f));
        }

        private static Recipe Simple(string name,string source,Color color,float metallic,float roughness,float coat){
            var recipe=new Recipe{Name=name,Kind=Kind.Simple,Source=source}.Set("_BaseColor",color).Set("_Metallic",metallic).Set("_Smoothness",1f-roughness);
            if(coat>0f){recipe.Set("_ClearCoat",1f).Set("_ClearCoatMask",coat).Set("_ClearCoatSmoothness",1f-BlenderCoatRoughness);}
            return recipe;
        }

        private static Recipe Emission(string name,string source,Color color,float strength){
            return new Recipe{Name=name,Kind=Kind.Emission,Source=source}.Set("_BaseColor",Color.black).Set("_Metallic",0f).Set("_Smoothness",0f)
                .Set("_SpecularHighlights",0f).Set("_EnvironmentReflections",0f).Set("_EmissionColor",new Color(color.r*strength,color.g*strength,color.b*strength,1f));
        }
    }
}
