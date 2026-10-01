using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VirtualizedGeometry.Editor;

namespace VirtualizedGeometry.Tests{
    /// <summary>URPのshaderソースへVG passを加える変換と、生成shaderがUnityでcompileできることを確かめる。</summary>
    public sealed class VirtualGeometryShaderSourceTests{
        private static string Resolve(string assetPath){return FileUtil.GetPhysicalPath(assetPath);}

        private static VirtualGeometryShaderConversion ConvertPackageShader(string name){
            string assetPath="Packages/com.unity.render-pipelines.universal/Shaders/"+name+".shader";
            return VirtualGeometryShaderSource.Convert(File.ReadAllText(Resolve(assetPath)),assetPath,Resolve);
        }

        private static void AssertCompiles(string source){
            Shader shader=ShaderUtil.CreateShaderAsset(source,true);
            try{
                Assert.That(ShaderUtil.ShaderHasError(shader),Is.False,string.Join("\n",System.Array.ConvertAll(ShaderUtil.GetShaderMessages(shader),message=>message.message+" "+message.file+":"+message.line)));
                var material=new Material(shader);
                try{Assert.That(material.FindPass(VirtualGeometryMaterials.PassName),Is.GreaterThanOrEqualTo(0));}
                finally{Object.DestroyImmediate(material);}
            }finally{Object.DestroyImmediate(shader);}
        }

        [Test]
        public void Convert(){
            {
                //URP標準shaderは元の全passを残し、forward passを複製したVG passを1つだけ加え、compileできる
                foreach(string name in new[]{"Lit","SimpleLit","ComplexLit","Unlit"}){
                    VirtualGeometryShaderConversion result=ConvertPackageShader(name);
                    Assert.That(result.Error,Is.Null,name);
                    Assert.That(result.ShaderName,Does.StartWith(VirtualGeometryMaterials.ShaderPrefix+"Universal Render Pipeline/"),name);
                    Assert.That(Regex.Matches(result.Source,"Name \""+VirtualGeometryMaterials.PassName+"\"").Count,Is.EqualTo(1),name);
                    Assert.That(Regex.Matches(result.Source,"#pragma vertex VGVertex").Count,Is.EqualTo(1),name);
                    Assert.That(result.Source,Does.Contain("\"LightMode\"=\""+VirtualGeometryMaterials.PassName+"\""),name);
                    AssertCompiles(result.Source);
                }
                Assert.That(ConvertPackageShader("Lit").Source,Does.Contain("\"ShadowCaster\""));
            }
            {
                //手書きのURP shaderは、独自のAttributes(semantic付きfield、#if付きfield)を持っていても変換・compileできる
                const string source=@"Shader ""Tests/Handwritten""
{
    Properties{_BaseColor(""Color"",Color)=(1,1,1,1)}
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
            CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            CBUFFER_END
            struct AppData
            {
                float4 vertex:POSITION;
                float3 normal:NORMAL;
            #if defined(USE_UV)
                float2 uv:TEXCOORD0;
            #endif
                float4 color:COLOR;
            };
            struct V2F{float4 position:SV_POSITION;half3 color:TEXCOORD0;};
            V2F Vert(AppData input)
            {
                V2F output;
                output.position=TransformObjectToHClip(input.vertex.xyz);
                output.color=input.color.rgb*saturate(input.normal.z*0.5+0.5);
                return output;
            }
            half4 Frag(V2F input):SV_Target{return half4(input.color,1)*_BaseColor;}
            ENDHLSL
        }
    }
}";
                VirtualGeometryShaderConversion result=VirtualGeometryShaderSource.Convert(source,null,Resolve);
                Assert.That(result.Error,Is.Null);
                Assert.That(result.Source,Does.Contain("attributes.vertex="));
                Assert.That(result.Source,Does.Contain("#if defined(USE_UV)"));
                AssertCompiles(result.Source);
            }
            {
                //RenderPipelineタグを省略したURP shaderも対象にする(URPではタグは省略可能)
                const string untagged="Shader \"Tests/Untagged\"{SubShader{Pass{HLSLPROGRAM\n#pragma vertex Vert\n#pragma fragment Frag\n#include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl\"\nstruct A{float4 p:POSITION;};\nfloat4 Vert(A i):SV_POSITION{return TransformObjectToHClip(i.p.xyz);}\nhalf4 Frag():SV_Target{return 1;}\nENDHLSL}}}";
                VirtualGeometryShaderConversion converted=VirtualGeometryShaderSource.Convert(untagged,null,Resolve);
                Assert.That(converted.Error,Is.Null);AssertCompiles(converted.Source);
                //ShaderLabのキーワードは大文字小文字を区別しない
                converted=VirtualGeometryShaderSource.Convert(untagged.Replace("SubShader","Subshader").Replace("Pass{","pass{"),null,Resolve);
                Assert.That(converted.Error,Is.Null);AssertCompiles(converted.Source);
            }
            {
                //Built-in(CGPROGRAM / ForwardBase)・別pipeline・Deferred専用・geometry stageは理由付きで非対応になる
                const string builtIn="Shader \"Tests/BuiltIn\"{SubShader{Pass{CGPROGRAM\n#pragma vertex vert\n#pragma fragment frag\nENDCG}}}";
                Assert.That(VirtualGeometryShaderSource.Convert(builtIn,null,Resolve).Error,Does.Contain("CGPROGRAM"));
                const string surface="Shader \"Tests/Surface\"{SubShader{Tags{\"RenderType\"=\"Opaque\"}CGPROGRAM\n#pragma surface surf Standard\nENDCG}}";
                Assert.That(VirtualGeometryShaderSource.Convert(surface,null,Resolve).Error,Does.Contain("Surface Shader"));
                const string forwardBase="Shader \"Tests/ForwardBase\"{SubShader{Pass{Tags{\"LightMode\"=\"ForwardBase\"}HLSLPROGRAM\n#pragma vertex vert\n#pragma fragment frag\nENDHLSL}}}";
                Assert.That(VirtualGeometryShaderSource.Convert(forwardBase,null,Resolve).Error,Does.Contain("ForwardBase"));
                const string hdrp="Shader \"Tests/HDRP\"{SubShader{Tags{\"RenderPipeline\"=\"HDRenderPipeline\"}Pass{HLSLPROGRAM\n#pragma vertex vert\n#pragma fragment frag\nENDHLSL}}}";
                Assert.That(VirtualGeometryShaderSource.Convert(hdrp,null,Resolve).Error,Does.Contain("別のRender Pipeline"));
                const string deferred="Shader \"Tests/Deferred\"{SubShader{Tags{\"RenderPipeline\"=\"UniversalPipeline\"}Pass{Tags{\"LightMode\"=\"UniversalGBuffer\"}HLSLPROGRAM\n#pragma vertex vert\n#pragma fragment frag\nENDHLSL}}}";
                Assert.That(VirtualGeometryShaderSource.Convert(deferred,null,Resolve).Error,Does.Contain("forward pass"));
                const string geometry="Shader \"Tests/Geometry\"{SubShader{Tags{\"RenderPipeline\"=\"UniversalPipeline\"}Pass{HLSLPROGRAM\n#pragma vertex vert\n#pragma geometry geom\n#pragma fragment frag\nENDHLSL}}}";
                Assert.That(VirtualGeometryShaderSource.Convert(geometry,null,Resolve).Error,Does.Contain("geometry"));
            }
        }
    }
}
