using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VirtualizedGeometry.Editor;

namespace VirtualizedGeometry.Tests{
    /// <summary>Virtual Geometry Shader(.vgshader)の作成で、変換元 shader が .meta に保存され、VG pass 付きの Shader が生成されることを確かめる。</summary>
    public sealed class VirtualGeometryShaderImporterTests{
        private const string Folder="Assets/VirtualGeometryShaderImporterTestTemp";
        private string previousRenderPipeline;

        // URP の SubShader(RenderPipeline タグ)で pass を引くため、URP が初期化時に設定するのと同じタグをテストの間だけ入れる。
        [SetUp]
        public void SetUp(){
            this.previousRenderPipeline=Shader.globalRenderPipeline;
            Shader.globalRenderPipeline="UniversalPipeline";
        }

        [TearDown]
        public void TearDown(){
            Shader.globalRenderPipeline=this.previousRenderPipeline;
            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void Create(){
            {
                //作成直後に変換元が .meta へ保存され(SaveAndReimport は dirty な importer だけを書く)、生成 shader が VG pass を持つ
                Directory.CreateDirectory(Folder);
                const string source=@"Shader ""Tests/ImporterSource""
{
    SubShader
    {
        Tags{""RenderPipeline""=""UniversalPipeline"" ""RenderType""=""Opaque""}
        Pass
        {
            Name ""Forward""
            Tags{""LightMode""=""UniversalForward""}
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""
            struct AppData
            {
                float4 vertex:POSITION;
            };
            float4 Vert(AppData input):SV_POSITION
            {
                return TransformObjectToHClip(input.vertex.xyz);
            }
            half4 Frag():SV_Target{return half4(1,1,1,1);}
            ENDHLSL
        }
    }
}";
                File.WriteAllText(Folder+"/ImporterSource.shader",source);
                AssetDatabase.ImportAsset(Folder+"/ImporterSource.shader");
                var shader=AssetDatabase.LoadAssetAtPath<Shader>(Folder+"/ImporterSource.shader");
                // Create は変換元を設定する前に雛形を一度 import するため、その import が変換元なしのエラーを1回出す(既存の挙動)。
                LogAssert.Expect(LogType.Error,new Regex("変換元のshaderが設定されていません"));
                string path=VirtualGeometryShaderImporter.Create(shader);
                Assert.That(path,Is.EqualTo(Folder+"/ImporterSource.vgshader"));
                Assert.That(File.ReadAllText(path+".meta"),Does.Contain(AssetDatabase.AssetPathToGUID(Folder+"/ImporterSource.shader")));
                var importer=(VirtualGeometryShaderImporter)AssetImporter.GetAtPath(path);
                Assert.That(importer.Source,Is.SameAs(shader));
                var generated=AssetDatabase.LoadAssetAtPath<Shader>(path);
                Assert.That(generated,Is.Not.Null);
                Assert.That(generated.name,Is.EqualTo("VirtualGeometry/Tests/ImporterSource"));
                var material=new Material(generated);
                try{Assert.That(material.FindPass(VirtualGeometryMaterials.PassName),Is.GreaterThanOrEqualTo(0));}
                finally{Object.DestroyImmediate(material);}
            }
        }
    }
}
