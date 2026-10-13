using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World.Tests{
    public sealed class PlanarCellTests{
        private const float Extent=33554432f;

        private sealed class Residency:IPlanetCellResidency{
            public bool AllReady=true;
            public readonly HashSet<PlanetCellKey> Ready=new HashSet<PlanetCellKey>();
            public readonly Dictionary<PlanetCellKey,float> Requested=new Dictionary<PlanetCellKey,float>();
            public bool IsReady(in PlanetCellKey key){return this.AllReady||this.Ready.Contains(key);}
            public void Request(in PlanetCellKey key,float priority){this.Requested[key]=priority;}
            public void Touch(in PlanetCellKey key){}
            public bool TryGetBounds(in PlanetCellKey key,out Vector3 offset,out float radius){offset=default;radius=0;return false;}
        }

        private static double ToDouble(in LogicalScalar value){
            Assert.That(value.TryToDouble(out double result),Is.True);
            return result;
        }

        private static PlanetCellSelector Create(int maximumLevel){
            return PlanetCellSelector.CreatePlane(Extent,0f,64,maximumLevel,2);
        }

        private static PlanetSelectionView View(Vector3 camera){
            Assert.That(LogicalVector3.TryFromFloat3(camera,out LogicalVector3 local),Is.True);
            Assert.That(LogicalScalar.TryFromFloat(Extent*0.5f,out LogicalScalar radius),Is.True);
            return new PlanetSelectionView(in local,in radius,Matrix4x4.identity,camera,1,null,500,0.1f,false,true);
        }

        [Test]
        public void TryCreatePlane(){
            Assert.That(LogicalScalar.TryFromFloat(Extent,out LogicalScalar extent),Is.True);
            {
                //root の中心は平面の中心(原点)、法線は +Y、持ち上げない
                Assert.That(PlanetCellFrame.TryCreatePlane(PlanetCellKey.Root(PlanetCellSelector.PlaneFace),in extent,out PlanetCellFrame frame),Is.True);
                Assert.That(ToDouble(frame.Origin.X),Is.EqualTo(0.0));
                Assert.That(ToDouble(frame.Origin.Y),Is.EqualTo(0.0));
                Assert.That(ToDouble(frame.Origin.Z),Is.EqualTo(0.0));
                Assert.That(ToDouble(frame.Direction.Y),Is.EqualTo(1.0));
                Assert.That(frame.HeightShift,Is.EqualTo(0f));
            }
            {
                //子の中心は、親の中心から ±extent/4 の位置(x が bit0、y が bit1 → x と z)。Logical で正確
                PlanetCellKey root=PlanetCellKey.Root(PlanetCellSelector.PlaneFace);
                double quarter=Extent*0.25;
                for(int child=0;child<4;child++){
                    Assert.That(PlanetCellFrame.TryCreatePlane(root.Child(child),in extent,out PlanetCellFrame frame),Is.True);
                    double expectedX=(child&1)==0?-quarter:quarter;
                    double expectedZ=(child&2)==0?-quarter:quarter;
                    Assert.That(ToDouble(frame.Origin.X),Is.EqualTo(expectedX));
                    Assert.That(ToDouble(frame.Origin.Z),Is.EqualTo(expectedZ));
                }
            }
            {
                //深い階層でも、隣り合う Cell の中心の差が Cell の一辺(extent·2^-L)にぴったり一致する(丸めで隣と食い違わない)
                PlanetCellKey key=PlanetCellKey.Root(PlanetCellSelector.PlaneFace);
                for(int level=0;level<40;level++){key=key.Child(3);}
                PlanetCellKey next=key.Neighbor(0);
                Assert.That(PlanetCellFrame.TryCreatePlane(in key,in extent,out PlanetCellFrame a),Is.True);
                Assert.That(PlanetCellFrame.TryCreatePlane(in next,in extent,out PlanetCellFrame b),Is.True);
                double side=Extent*System.Math.Pow(2.0,-40);
                Assert.That(ToDouble(a.Origin.X)-ToDouble(b.Origin.X),Is.EqualTo(side).Within(1e-12));
                Assert.That(ToDouble(a.Origin.Z),Is.EqualTo(ToDouble(b.Origin.Z)));
            }
            {
                //階層が上限を超えると作れない
                PlanetCellKey tooDeep=PlanetCellKey.Root(PlanetCellSelector.PlaneFace);
                for(int level=0;level<=PlanetCellFrame.MaximumLevel;level++){tooDeep=tooDeep.Child(0);}
                Assert.That(PlanetCellFrame.TryCreatePlane(in tooDeep,in extent,out _),Is.False);
            }
        }

        [Test]
        public void QuadArc(){
            {
                //平面の 1 quad の長さ = extent·2^-L/N(階層ごとに半分)
                PlanetCellSelector selector=Create(20);
                Assert.That(selector.IsPlane,Is.True);
                Assert.That(selector.Extent,Is.EqualTo(Extent));
                Assert.That(selector.QuadArc(0),Is.EqualTo(Extent/64f));
                for(int level=0;level<20;level++){
                    Assert.That(selector.QuadArc(level+1),Is.EqualTo(selector.QuadArc(level)*0.5f));
                }
            }
        }

        [Test]
        public void Bounds(){
            {
                //bounds は Cell の原点を中心とする平面の正方形(一辺 size)と skirt の深さを含む
                PlanetCellSelector selector=Create(20);
                PlanetCellKey key=PlanetCellKey.Root(PlanetCellSelector.PlaneFace).Child(1).Child(2);
                selector.Bounds(in key,out Vector3 offset,out float radius);
                float size=Extent*0.25f;
                float skirt=selector.SkirtDepth(key.Level);
                Assert.That(offset.x,Is.EqualTo(0f));Assert.That(offset.z,Is.EqualTo(0f));
                Assert.That(radius,Is.GreaterThanOrEqualTo(size*0.5f*Mathf.Sqrt(2f)));
                Assert.That(offset.y-radius,Is.LessThanOrEqualTo(-skirt));
            }
        }

        [Test]
        public void Select(){
            {
                //描く Cell は平面を重なりなく覆う(祖先と子孫が同時に選ばれず、面積の合計が1)。カメラは平面の中心の上 2 m
                PlanetCellSelector selector=Create(20);
                var residency=new Residency();
                var output=new List<PlanetCellKey>();var refined=new HashSet<PlanetCellKey>();
                selector.Select(View(new Vector3(0,2,0)),residency,null,output,refined);
                AssertCovers(output);
                Assert.That(refined.Count,Is.GreaterThan(0),"近くの Cell が細分化されていません。");
                foreach(PlanetCellKey key in output){Assert.That(key.Face,Is.EqualTo(PlanetCellSelector.PlaneFace));}
            }
            {
                //隣り合う Cell の階層差は1以下(2:1 制約。平面の縁の Neighbor が別の面を返しても壊れない)
                PlanetCellSelector selector=Create(20);
                var residency=new Residency();
                var output=new List<PlanetCellKey>();var refined=new HashSet<PlanetCellKey>();
                selector.Select(View(new Vector3(Extent*0.5f-1f,1,0)),residency,null,output,refined);
                foreach(PlanetCellKey key in output){
                    for(int direction=0;direction<PlanetCellKey.DirectionCount;direction++){
                        PlanetCellKey neighbor=key.Neighbor(direction);
                        foreach(PlanetCellKey other in output){
                            if(neighbor.Contains(other)){
                                Assert.That(other.Level-key.Level,Is.LessThanOrEqualTo(1),key+" next to "+other);
                            }
                        }
                    }
                }
                AssertCovers(output);
            }
            {
                //子が生成されていなければ root を描き、子の生成を要求する。root が無ければ何も描かず root を要求する
                PlanetCellSelector selector=Create(20);
                var residency=new Residency{AllReady=false};
                var output=new List<PlanetCellKey>();var refined=new HashSet<PlanetCellKey>();
                selector.Select(View(new Vector3(0,2,0)),residency,null,output,refined);
                Assert.That(output.Count,Is.EqualTo(0));
                Assert.That(residency.Requested.ContainsKey(PlanetCellKey.Root(PlanetCellSelector.PlaneFace)),Is.True);
                residency.Ready.Add(PlanetCellKey.Root(PlanetCellSelector.PlaneFace));
                selector.Select(View(new Vector3(0,2,0)),residency,null,output,refined);
                Assert.That(output.Count,Is.EqualTo(1));
                Assert.That(residency.Requested.ContainsKey(PlanetCellKey.Root(PlanetCellSelector.PlaneFace).Child(0)),Is.True);
            }
        }

        private static void AssertCovers(List<PlanetCellKey> cells){
            var set=new HashSet<PlanetCellKey>(cells);
            Assert.That(set.Count,Is.EqualTo(cells.Count),"同じ Cell が2回選ばれています。");
            float area=0;
            foreach(PlanetCellKey key in cells){
                area+=Mathf.Pow(0.25f,key.Level);
                PlanetCellKey ancestor=key;
                while(ancestor.Level>0){
                    ancestor=ancestor.Parent();
                    Assert.That(set.Contains(ancestor),Is.False,key+" と祖先 "+ancestor+" が両方選ばれています。");
                }
            }
            Assert.That(area,Is.EqualTo(1f).Within(1e-5f));
        }
    }
}
