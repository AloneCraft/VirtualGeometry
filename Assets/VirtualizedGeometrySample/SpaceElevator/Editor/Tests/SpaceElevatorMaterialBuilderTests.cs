using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace VirtualizedGeometry.Samples.SpaceElevator.Editor.Tests{
    /// <summary>
    /// space_elevator.fbx のマテリアル(SpaceElevator/Materials/SE_GA_*.mat)が、レシピ表・FBX の remap・VG の描画規則と食い違っていないことを確かめる。
    /// 資産は書き換えない(Apply を呼ばず、差分だけを見る)。
    /// </summary>
    public sealed class SpaceElevatorMaterialBuilderTests{
        private string previousRenderPipeline;

        // URP の SubShader(RenderPipeline タグ)で Material の pass を引くため、URP が初期化時に設定するのと同じタグをテストの間だけ入れる。
        [SetUp]
        public void SetUp(){
            this.previousRenderPipeline=Shader.globalRenderPipeline;
            Shader.globalRenderPipeline="UniversalPipeline";
        }

        [TearDown]
        public void TearDown(){
            Shader.globalRenderPipeline=this.previousRenderPipeline;
        }

        [Test]
        public void Differences(){
            {
                //資産がレシピ表と同期し、FBX の 30 マテリアルだけが remap され、ライト・カメラの取り込みが止まっている
                List<string> differences=SpaceElevatorMaterialBuilder.Differences();
                Assert.That(differences,Is.Empty,string.Join("\n",differences));
            }
            {
                //VG 版の手続きシェーダーがエラー無く生成され、VG pass を持つ
                var shader=AssetDatabase.LoadAssetAtPath<Shader>(SpaceElevatorMaterialBuilder.ShaderFolder+"/SpaceElevatorProcedural.vgshader");
                Assert.That(shader,Is.Not.Null);
                Assert.That(ShaderUtil.ShaderHasError(shader),Is.False,string.Join("\n",ShaderUtil.GetShaderMessages(shader).Select(message=>message.message+" "+message.file+":"+message.line)));
                var material=new Material(shader);
                try{Assert.That(material.FindPass(VirtualGeometryMaterials.PassName),Is.GreaterThanOrEqualTo(0));}
                finally{Object.DestroyImmediate(material);}
            }
        }

        [Test]
        public void MaterialNames(){
            {
                //FBX の全 MeshRenderer の全 slot が SpaceElevator/Materials の外部 Material を指し(埋め込みの白いマテリアルが残らない)、
                //レシピ表の 30 個がすべて使われている(レシピ表と FBX のマテリアルが過不足なく対応する)
                var model=AssetDatabase.LoadAssetAtPath<GameObject>(SpaceElevatorPatternSpace.ModelPath);
                var used=new HashSet<string>();
                foreach(MeshRenderer renderer in model.GetComponentsInChildren<MeshRenderer>(true)){
                    foreach(Material material in renderer.sharedMaterials){
                        Assert.That(material,Is.Not.Null,renderer.name);
                        Assert.That(AssetDatabase.GetAssetPath(material),Does.StartWith(SpaceElevatorMaterialBuilder.MaterialFolder+"/"),renderer.name+": "+material.name);
                        used.Add(material.name);
                    }
                }
                string[] recipes=SpaceElevatorMaterialBuilder.MaterialNames().OrderBy(name=>name,System.StringComparer.Ordinal).ToArray();
                Assert.That(used.OrderBy(name=>name,System.StringComparer.Ordinal).ToArray(),Is.EqualTo(recipes));
                Assert.That(recipes.Length,Is.EqualTo(30));
            }
            {
                //マテリアルはすべて VG で描ける(VG 版 shader を直接使う、または同梱の URP 用 Virtual Geometry Shader で解決できる)。Proxy に落ちる部品が無い
                foreach(string name in SpaceElevatorMaterialBuilder.MaterialNames()){
                    Material material=Load(name);
                    Assert.That(VirtualGeometryMaterials.TryResolve(material,out _,out _,out string reason),Is.True,name+": "+reason);
                }
            }
        }

        [Test]
        public void GetLinearColor(){
            {
                //[HDR] でない色(URP Lit の _BaseColor)は gamma で保存し、線形に戻すと Blender の値になる
                Assert.That(Distance(SpaceElevatorMaterialBuilder.GetLinearColor(Load("SE_GA_PaintYellow"),"_BaseColor"),new Color(0.85f,0.55f,0.04f)),Is.LessThan(2e-3f));
            }
            {
                //[HDR] の色(_EmissionColor)は線形のまま保存する。LampWhite は色 (1,0.9,0.75) × 強さ 30
                Assert.That(Distance(SpaceElevatorMaterialBuilder.GetLinearColor(Load("SE_GA_LampWhite"),"_EmissionColor"),new Color(30f,27f,22.5f)),Is.LessThan(1e-3f));
                Assert.That(Load("SE_GA_LampWhite").IsKeywordEnabled("_EMISSION"),Is.True);
            }
            {
                //手続きシェーダーの色はすべて [HDR] で、Blender の線形値のまま入る
                Assert.That(Distance(SpaceElevatorMaterialBuilder.GetLinearColor(Load("SE_GA_Concrete"),"_BaseColor"),new Color(0.56f,0.555f,0.53f)),Is.LessThan(1e-5f));
            }
        }

        private static Material Load(string name){
            var material=AssetDatabase.LoadAssetAtPath<Material>(SpaceElevatorMaterialBuilder.MaterialFolder+"/"+name+".mat");
            Assert.That(material,Is.Not.Null,name+" がありません。");
            return material;
        }

        private static float Distance(Color a,Color b){
            return Mathf.Abs(a.r-b.r)+Mathf.Abs(a.g-b.g)+Mathf.Abs(a.b-b.b);
        }
    }
}
