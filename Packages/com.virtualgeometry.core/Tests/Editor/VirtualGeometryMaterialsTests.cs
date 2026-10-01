using NUnit.Framework;
using UnityEngine;

namespace VirtualizedGeometry.Tests{
    /// <summary>RendererのMaterialから、VG描画に使うMaterialとpassを決める規則を確かめる。</summary>
    public sealed class VirtualGeometryMaterialsTests{
        [Test]
        public void TryResolve(){
            {
                //普通のURP Litは同梱のVirtual Geometry Shaderへ内部Materialで対応し、元Materialの値とkeywordを写す
                var source=new Material(Shader.Find("Universal Render Pipeline/Lit"));
                try{
                    source.SetColor("_BaseColor",new Color(0.2f,0.4f,0.6f,1));source.EnableKeyword("_EMISSION");
                    Assert.That(VirtualGeometryMaterials.TryResolve(source,out Material material,out int pass,out string reason),Is.True,reason);
                    Assert.That(material,Is.Not.SameAs(source));
                    Assert.That(material.shader.name,Is.EqualTo(VirtualGeometryMaterials.ShaderPrefix+"Universal Render Pipeline/Lit"));
                    Assert.That(pass,Is.GreaterThanOrEqualTo(0));
                    Assert.That(material.GetColor("_BaseColor"),Is.EqualTo(new Color(0.2f,0.4f,0.6f,1)));
                    Assert.That(material.IsKeywordEnabled("_EMISSION"),Is.True);
                }finally{Object.DestroyImmediate(source);VirtualGeometryMaterials.Reset();}
            }
            {
                //Materialのshader自体がVG passを持てば、そのMaterialをそのまま使う
                var source=new Material(Shader.Find(VirtualGeometryMaterials.ShaderPrefix+"Universal Render Pipeline/Unlit"));
                try{
                    Assert.That(VirtualGeometryMaterials.TryResolve(source,out Material material,out int pass,out string reason),Is.True,reason);
                    Assert.That(material,Is.SameAs(source));Assert.That(pass,Is.GreaterThanOrEqualTo(0));
                }finally{Object.DestroyImmediate(source);}
            }
            {
                //半透明、対応するVirtual Geometry Shaderが無いshader、未設定は理由付きで描かない
                var transparent=new Material(Shader.Find("Universal Render Pipeline/Lit")){renderQueue=3000};
                var unknown=new Material(Shader.Find("Hidden/Internal-Colored"));
                try{
                    Assert.That(VirtualGeometryMaterials.TryResolve(transparent,out _,out _,out string reason),Is.False);Assert.That(reason,Does.Contain("半透明"));
                    Assert.That(VirtualGeometryMaterials.TryResolve(unknown,out _,out _,out reason),Is.False);Assert.That(reason,Does.Contain("Virtual Geometry Shader"));
                    Assert.That(VirtualGeometryMaterials.TryResolve(null,out _,out _,out reason),Is.False);
                }finally{Object.DestroyImmediate(transparent);Object.DestroyImmediate(unknown);}
            }
        }

        [Test]
        public void NeedsVirtualShader(){
            var lit=new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var unknown=new Material(Shader.Find("Hidden/Internal-Colored"));
            try{
                //同梱の対応shaderがあるURP Litは作成不要、対応shaderの無い不透明Materialは作成対象、半透明とnullは対象外
                Assert.That(VirtualGeometryMaterials.NeedsVirtualShader(lit),Is.False);
                Assert.That(VirtualGeometryMaterials.NeedsVirtualShader(unknown),Is.True);
                unknown.renderQueue=3000;
                Assert.That(VirtualGeometryMaterials.NeedsVirtualShader(unknown),Is.False);
                Assert.That(VirtualGeometryMaterials.NeedsVirtualShader(null),Is.False);
            }finally{Object.DestroyImmediate(lit);Object.DestroyImmediate(unknown);}
        }

        [Test]
        public void IsSupported(){
            var lit=new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try{
                Assert.That(VirtualGeometryMaterials.IsSupported(lit,out string reason),Is.True,reason);
                lit.renderQueue=3000;
                Assert.That(VirtualGeometryMaterials.IsSupported(lit,out reason),Is.False);
            }finally{Object.DestroyImmediate(lit);}
        }
    }
}
