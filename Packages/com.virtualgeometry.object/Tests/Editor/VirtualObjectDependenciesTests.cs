using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VirtualizedGeometry;
using Object=UnityEngine.Object;

namespace VirtualGeometry.VirtualObjects.Tests{
    public sealed class VirtualObjectDependenciesTests{
        private readonly List<Object> owned=new List<Object>();

        [TearDown]
        public void TearDown(){
            for(int i=0;i<this.owned.Count;i++){
                if(this.owned[i]!=null){Object.DestroyImmediate(this.owned[i]);}
            }
            this.owned.Clear();
        }

        private T Own<T>(T instance) where T:Object{
            this.owned.Add(instance);
            return instance;
        }

        private Material NewMaterial(string name){
            return this.Own(new Material(Shader.Find("Hidden/InternalErrorShader")){name=name});
        }

        private VirtualObject Define(string id,params RepresentationBand[] bands){
            GameObject gameObject=this.Own(new GameObject("Definition "+id));
            VirtualObject definition=gameObject.AddComponent<VirtualObject>();
            definition.Id=id;definition.Bands=bands;
            return definition;
        }

        [Test]
        public void FromSets(){
            {
                // 種類から、帯の設定(ビルボードの材質、プレハブの材質、VG の部品の材質)、子の型、割り当ての規則の種類をたどる
                Material billboard=this.NewMaterial("billboard");
                Material lit=this.NewMaterial("lit");
                Material virtualGeometry=this.NewMaterial("vg");
                GameObject detail=this.Own(new GameObject("Detail"));
                detail.AddComponent<MeshFilter>();
                detail.AddComponent<MeshRenderer>().sharedMaterial=lit;
                GameObject vg=this.Own(new GameObject("VG"));
                vg.AddComponent<VirtualGeometryRenderer>();
                vg.GetComponent<MeshRenderer>().sharedMaterial=virtualGeometry;
                VirtualObject child=this.Define("child",new RepresentationBand("none",0f,0f));
                VirtualObject special=this.Define("special",new RepresentationBand("none",0f,0f));
                VirtualObject building=this.Define("building",
                    new RepresentationBand("none",0f,0f),
                    new RepresentationBand("billboard",2f,1.5f,new BillboardRepresentationSettings{Material=billboard}),
                    new RepresentationBand("vgmesh",30f,24f,new VirtualGeometryRepresentationSettings{Prefab=vg}),
                    new RepresentationBand("prefab",150f,120f,new PrefabRepresentationSettings{Prefab=detail}),
                    new RepresentationBand("children",400f,320f));
                building.ChildTemplates=new[]{child};
                building.Assignment=new IChildAssignmentRule[]{
                    new DefaultTemplateRule{TemplateIndex=0},
                    new OverrideTableRule{Rows=new[]{new OverrideTableRule.Row{Number=2,Prefab=special}}},
                };
                VirtualObjectSet set=this.Own(ScriptableObject.CreateInstance<VirtualObjectSet>());
                set.Placements=new[]{
                    new VirtualObjectPlacementRecord{Id="a",Prefab=building,X="0",Y="0",Z="0"},
                    new VirtualObjectPlacementRecord{Id="b",Prefab=building,X="10",Y="0",Z="0"},
                };
                VirtualObjectDependencies dependencies=VirtualObjectDependencies.FromSets(new[]{set});
                Assert.That(dependencies.Definitions,Is.EquivalentTo(new[]{building,child,special}));
                Assert.That(dependencies.Prefabs,Has.Member(detail));
                Assert.That(dependencies.Prefabs,Has.Member(vg));
                Assert.That(dependencies.Materials,Is.EquivalentTo(new[]{billboard,lit}));
                Assert.That(dependencies.VirtualGeometryMaterials,Is.EquivalentTo(new[]{virtualGeometry}));
            }
            {
                // null の set、記録の無い set、種類の無い記録は無視する
                VirtualObjectSet empty=this.Own(ScriptableObject.CreateInstance<VirtualObjectSet>());
                empty.Placements=new[]{new VirtualObjectPlacementRecord{Id="x",Prefab=null,X="0",Y="0",Z="0"}};
                VirtualObjectDependencies dependencies=VirtualObjectDependencies.FromSets(new VirtualObjectSet[]{null,empty});
                Assert.That(dependencies.Definitions,Is.Empty);
                Assert.That(dependencies.Materials,Is.Empty);
                Assert.That(VirtualObjectDependencies.FromSets(null).Definitions,Is.Empty);
            }
            {
                // 種類が互いに子の型として参照し合っても、1 回ずつだけたどる
                VirtualObject a=this.Define("a",new RepresentationBand("none",0f,0f));
                VirtualObject b=this.Define("b",new RepresentationBand("none",0f,0f));
                a.ChildTemplates=new[]{b};
                b.ChildTemplates=new[]{a};
                var dependencies=new VirtualObjectDependencies();
                dependencies.AddDefinition(a);
                Assert.That(dependencies.Definitions,Is.EquivalentTo(new[]{a,b}));
            }
        }
    }
}
