using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects.Tests{
    public sealed class ChildAssignmentsTests{
        private readonly List<GameObject> owned=new List<GameObject>();

        [TearDown]
        public void TearDown(){
            for(int i=0;i<this.owned.Count;i++){
                if(this.owned[i]!=null){Object.DestroyImmediate(this.owned[i]);}
            }
            this.owned.Clear();
        }

        private VirtualObject NewPrefab(string id){
            var gameObject=new GameObject("Prefab "+id);
            this.owned.Add(gameObject);
            VirtualObject definition=gameObject.AddComponent<VirtualObject>();
            definition.Id=id;
            return definition;
        }

        private static ChildAssignment Evaluate(IChildAssignmentRule[] rules,int number,float x){
            LogicalVector3.TryFromFloat3(new float3(x,0f,0f),out LogicalVector3 origin);
            VirtualObjectPart part=default;
            var context=new ChildAssignmentContext(null,number,in part,in origin,0u);
            return ChildAssignments.Evaluate(rules,in context);
        }

        // 中心 (0,0,0)・半径 100 m の領域の規則(型 2)。
        private static RegionRule Region(){
            return new RegionRule{CenterX="0",CenterY="0",CenterZ="0",Radius=100f,TemplateIndex=2};
        }

        [Test]
        public void Evaluate(){
            {
                // 規則が無い(null、空、null の要素だけ)なら、子を作らない
                Assert.That(Evaluate(null,0,0f).HasChild,Is.False);
                Assert.That(Evaluate(new IChildAssignmentRule[0],0,0f).TemplateIndex,Is.EqualTo(-1));
                Assert.That(Evaluate(new IChildAssignmentRule[]{null},0,0f).HasChild,Is.False);
            }
            {
                // 既定の規則は全番号に同じ型
                var rules=new IChildAssignmentRule[]{new DefaultTemplateRule{TemplateIndex=1}};
                ChildAssignment first=Evaluate(rules,0,0f);
                ChildAssignment last=Evaluate(rules,99,0f);
                Assert.That(first.TemplateIndex,Is.EqualTo(1));
                Assert.That(first.Source,Is.EqualTo("default"));
                Assert.That(first.HasChild,Is.True);
                Assert.That(last.TemplateIndex,Is.EqualTo(1));
            }
            {
                // 領域の規則は、既定の後に並べると、領域の中だけ既定を上書きする(領域の外は既定のまま)
                var rules=new IChildAssignmentRule[]{new DefaultTemplateRule{TemplateIndex=1},Region()};
                ChildAssignment inside=Evaluate(rules,0,50f);
                ChildAssignment outside=Evaluate(rules,0,500f);
                Assert.That(inside.TemplateIndex,Is.EqualTo(2));
                Assert.That(inside.Source,Is.EqualTo("region"));
                Assert.That(outside.TemplateIndex,Is.EqualTo(1));
                Assert.That(outside.Source,Is.EqualTo("default"));
            }
            {
                // 番号ごとの上書きは最後に並べると領域より強い(その番号だけ)
                var table=new OverrideTableRule{Rows=new[]{new OverrideTableRule.Row{Number=5,TemplateIndex=3,Parameters=new Vector4(1f,2f,3f,4f)}}};
                var rules=new IChildAssignmentRule[]{new DefaultTemplateRule{TemplateIndex=1},Region(),table};
                ChildAssignment overridden=Evaluate(rules,5,50f);
                ChildAssignment other=Evaluate(rules,4,50f);
                Assert.That(overridden.TemplateIndex,Is.EqualTo(3));
                Assert.That(overridden.Source,Is.EqualTo("override"));
                Assert.That(overridden.Parameters,Is.EqualTo(new Vector4(1f,2f,3f,4f)));
                Assert.That(other.TemplateIndex,Is.EqualTo(2));
                Assert.That(other.Source,Is.EqualTo("region"));
            }
            {
                // 並びの後ろが勝つ: 既定を最後に置くと、領域も上書きも既定に上書きされる
                var table=new OverrideTableRule{Rows=new[]{new OverrideTableRule.Row{Number=5,TemplateIndex=3}}};
                var rules=new IChildAssignmentRule[]{table,Region(),new DefaultTemplateRule{TemplateIndex=1}};
                ChildAssignment result=Evaluate(rules,5,50f);
                Assert.That(result.TemplateIndex,Is.EqualTo(1));
                Assert.That(result.Source,Is.EqualTo("default"));
            }
            {
                // TemplateIndex=-1 の上書きは、既定で決まった子を作らない指定になる
                var table=new OverrideTableRule{Rows=new[]{new OverrideTableRule.Row{Number=2,TemplateIndex=-1}}};
                var rules=new IChildAssignmentRule[]{new DefaultTemplateRule{TemplateIndex=1},table};
                Assert.That(Evaluate(rules,2,0f).HasChild,Is.False);
                Assert.That(Evaluate(rules,1,0f).HasChild,Is.True);
            }
            {
                // プレハブの上書きは、型の index が -1 でも子を作る。後の既定はプレハブを外す
                VirtualObject prefab=this.NewPrefab("volcano");
                var table=new OverrideTableRule{Rows=new[]{new OverrideTableRule.Row{Number=0,TemplateIndex=-1,Prefab=prefab}}};
                var overridden=new IChildAssignmentRule[]{new DefaultTemplateRule{TemplateIndex=1},table};
                ChildAssignment result=Evaluate(overridden,0,0f);
                Assert.That(result.HasChild,Is.True);
                Assert.That(result.Prefab,Is.SameAs(prefab));
                var reset=new IChildAssignmentRule[]{table,new DefaultTemplateRule{TemplateIndex=1}};
                Assert.That(Evaluate(reset,0,0f).Prefab,Is.Null);
            }
            {
                // 読めない中心の領域は何も変えない
                var broken=new RegionRule{CenterX="abc",Radius=100f,TemplateIndex=2};
                var rules=new IChildAssignmentRule[]{new DefaultTemplateRule{TemplateIndex=1},broken};
                Assert.That(Evaluate(rules,0,0f).TemplateIndex,Is.EqualTo(1));
            }
        }

        [Test]
        public void Mix(){
            {
                // 同じ種と番号なら同じ値(決定的)
                Assert.That(ChildAssignments.Mix(123u,7),Is.EqualTo(ChildAssignments.Mix(123u,7)));
                Assert.That(ChildAssignments.Mix(0u,0),Is.EqualTo(ChildAssignments.Mix(0u,0)));
            }
            {
                // 番号が違えば違う値(16 個で重複しない)
                var values=new HashSet<uint>();
                for(int number=0;number<16;number++){values.Add(ChildAssignments.Mix(123u,number));}
                Assert.That(values.Count,Is.EqualTo(16));
            }
            {
                // 種が違えば違う値
                Assert.That(ChildAssignments.Mix(1u,3),Is.Not.EqualTo(ChildAssignments.Mix(2u,3)));
            }
        }
    }
}
