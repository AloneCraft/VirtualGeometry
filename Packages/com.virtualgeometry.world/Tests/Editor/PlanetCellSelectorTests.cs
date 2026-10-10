using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World.Tests{
    public sealed class PlanetCellSelectorTests{
        private const float Radius=10000;
        private const float Height=300;

        private sealed class Residency:IPlanetCellResidency{
            public bool AllReady=true;
            public readonly HashSet<PlanetCellKey> Ready=new HashSet<PlanetCellKey>();
            public readonly Dictionary<PlanetCellKey,float> Requested=new Dictionary<PlanetCellKey,float>();
            public bool IsReady(in PlanetCellKey key){return this.AllReady||this.Ready.Contains(key);}
            public void Request(in PlanetCellKey key,float priority){this.Requested[key]=priority;}
            public void Touch(in PlanetCellKey key){}
            public bool TryGetBounds(in PlanetCellKey key,out Vector3 offset,out float radius){offset=default;radius=0;return false;}
        }

        private static PlanetCellSelector Create(int maximumLevel){
            return new PlanetCellSelector(Radius,Height,64,maximumLevel,2){TargetPixels=2,Hysteresis=0.1f};
        }

        // cull 空間は惑星のローカル座標と同じ。
        private static PlanetSelectionView View(Vector3 camera,bool horizon){
            Assert.That(LogicalVector3.TryFromFloat3(camera,out LogicalVector3 local),Is.True);
            Assert.That(LogicalScalar.TryFromFloat(Radius,out LogicalScalar radius),Is.True);
            return new PlanetSelectionView(in local,in radius,Matrix4x4.identity,camera,1,null,500,0.1f,false,horizon);
        }

        [Test]
        public void QuadArc(){
            {
                //細かい階層ほど半分になる(画面誤差が親から子へ単調に減る)
                PlanetCellSelector selector=Create(10);
                for(int level=0;level<10;level++){
                    Assert.That(selector.QuadArc(level+1),Is.EqualTo(selector.QuadArc(level)*0.5f).Within(1e-4f));
                }
            }
        }

        [Test]
        public void FromCamera(){
            {
                //半径 6,371km の地表から 1.7m のカメラと、真下の深い階層の Cell の原点の距離が、float の刻み(約 0.5m)に潰れない
                var selector=new PlanetCellSelector(6371000f,3000f,PlanetTerrainSource.CellQuads,PlanetCellFrame.MaximumLevel,2f);
                PlanetCellKey key=PlanetCellKey.Root(4);
                for(int depth=0;depth<26;depth++){key=key.Child((depth*7)%4);}
                PlanetCellFrame frame=selector.Frame(in key);
                // カメラ = 原点 × (1 + 1.7/R)(Logical で作る)。
                Assert.That(LogicalScalar.TryFromFloat(1.7f/6371000f,out LogicalScalar ratio),Is.True);
                Assert.That(LogicalVector3.TryScale(frame.Origin,in ratio,out LogicalVector3 lift),Is.True);
                Assert.That(LogicalVector3.TryAdd(frame.Origin,lift,out LogicalVector3 camera),Is.True);
                var view=new PlanetSelectionView(in camera,selector.LogicalRadius,Matrix4x4.identity,Vector3.zero,1,null,500,0.1f,false,true);
                Assert.That(view.CameraAltitude,Is.EqualTo(1.7f).Within(1e-3f));
                Vector3 relative=selector.FromCamera(in view,in key,Vector3.zero);
                Assert.That(relative.magnitude,Is.EqualTo(1.7f).Within(1e-3f));
                // 真下の Cell は地平線の向こうにも視錐台の外にも無い。
                selector.Bounds(in key,out Vector3 offset,out float radius);
                Assert.That(selector.IsCulled(in view,selector.FromCamera(in view,in key,offset),radius),Is.False);
            }
        }

        [Test]
        public void Bounds(){
            {
                //Cell の範囲の地表(半径 R±Hmax)と skirt の底の点は、すべて bounds の球の中にある
                PlanetCellSelector selector=Create(10);
                var keys=new List<PlanetCellKey>{PlanetCellKey.Root(2),PlanetCellKey.Root(2).Child(3),PlanetCellKey.Root(4).Child(0).Child(2).Child(1)};
                foreach(PlanetCellKey key in keys){
                    selector.Bounds(in key,out Vector3 offset,out float radius);
                    Vector3 center=selector.Frame(in key).ApproximateOrigin+offset;
                    PlanetCellSelector.FaceRange(in key,out float s0,out float t0,out float size);
                    for(int j=0;j<=16;j++){
                        for(int i=0;i<=16;i++){
                            Vector3 direction=CubeSphere.Direction(key.Face,s0+size*i/16f,t0+size*j/16f);
                            foreach(float r in new[]{Radius-Height-selector.SkirtDepth(key.Level),Radius+Height}){
                                Assert.That((direction*r-center).magnitude,Is.LessThanOrEqualTo(radius*1.0001f),key.ToString());
                            }
                        }
                    }
                }
            }
            {
                //細かい階層(FrameBounds)でも、面の角・辺の近くを含めて、Cell の範囲の点はすべて bounds の球の中にある
                // 高さの幅で隠れないよう、振幅 0 で角度の見積もりを確かめる。
                var selector=new PlanetCellSelector(Radius,0f,PlanetTerrainSource.CellQuads,PlanetCellFrame.MaximumLevel,2f);
                foreach(int child in new[]{0,1,2,3}){
                    PlanetCellKey key=PlanetCellKey.Root(1);
                    for(int depth=0;depth<PlanetCellSelector.FrameBoundsLevel+2;depth++){
                        key=key.Child(child);
                        if(key.Level<PlanetCellSelector.FrameBoundsLevel){continue;}
                        selector.FrameBounds(in key,out Vector3 offset,out float radius);
                        Vector3 center=selector.Frame(in key).ApproximateOrigin+offset;
                        PlanetCellSelector.FaceRange(in key,out float s0,out float t0,out float size);
                        for(int j=0;j<=4;j++){
                            for(int i=0;i<=4;i++){
                                Vector3 direction=CubeSphere.Direction(key.Face,s0+size*i/4f,t0+size*j/4f);
                                foreach(float r in new[]{Radius-selector.SkirtDepth(key.Level),Radius}){
                                    Assert.That((direction*r-center).magnitude,Is.LessThanOrEqualTo(radius*1.0001f),key.ToString());
                                }
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void Select(){
            {
                //描く Cell は球面を重なりなく覆う(祖先と子孫が同時に選ばれず、面ごとの面積の合計が1)
                PlanetCellSelector selector=Create(8);
                var residency=new Residency();
                var output=new List<PlanetCellKey>();var refined=new HashSet<PlanetCellKey>();
                selector.Select(View(new Vector3(0,Radius+500,0),false),residency,null,output,refined);
                AssertCovers(output);
                Assert.That(refined.Count,Is.GreaterThan(0),"近くの Cell が細分化されていません。");
            }
            {
                //隣り合う Cell の階層差は1以下(2:1 制約)
                PlanetCellSelector selector=Create(8);
                var residency=new Residency();
                var output=new List<PlanetCellKey>();var refined=new HashSet<PlanetCellKey>();
                selector.Select(View(new Vector3(0,Radius+50,0),false),residency,null,output,refined);
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
                //同じ視点で前回の細分化を渡すと、同じ Cell を選ぶ(ヒステリシスで揺れない)
                PlanetCellSelector selector=Create(8);
                var residency=new Residency();
                var first=new List<PlanetCellKey>();var refined=new HashSet<PlanetCellKey>();
                selector.Select(View(new Vector3(300,Radius+800,-200),false),residency,null,first,refined);
                var second=new List<PlanetCellKey>();var refinedAgain=new HashSet<PlanetCellKey>();
                selector.Select(View(new Vector3(300,Radius+800,-200),false),residency,refined,second,refinedAgain);
                Assert.That(new HashSet<PlanetCellKey>(second).SetEquals(first),Is.True);
            }
            {
                //子が生成されていなければ親を描き、子の生成を要求する。root が無ければ何も描かずに root を要求する
                PlanetCellSelector selector=Create(8);
                var residency=new Residency{AllReady=false};
                for(int face=0;face<CubeSphere.FaceCount-1;face++){residency.Ready.Add(PlanetCellKey.Root(face));}
                var output=new List<PlanetCellKey>();var refined=new HashSet<PlanetCellKey>();
                selector.Select(View(new Vector3(0,Radius+500,0),false),residency,null,output,refined);
                Assert.That(output.Count,Is.EqualTo(CubeSphere.FaceCount-1));
                Assert.That(residency.Requested.ContainsKey(PlanetCellKey.Root(CubeSphere.FaceCount-1)),Is.True);
                Assert.That(residency.Requested.ContainsKey(PlanetCellKey.Root(2).Child(0)),Is.True);
            }
        }

        [Test]
        public void IsCulled(){
            {
                //地表の近くから見ると、真下の Cell は残り、反対側の面は地平線の向こうで落ちる
                PlanetCellSelector selector=Create(8);
                PlanetSelectionView view=View(new Vector3(0,Radius+Height+100,0),true);
                PlanetCellKey below=PlanetCellKey.Root(2).Child(0).Child(3);
                selector.Bounds(in below,out Vector3 offset,out float radius);
                Assert.That(selector.IsCulled(in view,selector.FromCamera(in view,in below,offset),radius),Is.False);
                PlanetCellKey opposite=PlanetCellKey.Root(3).Child(1);
                selector.Bounds(in opposite,out offset,out radius);
                Assert.That(selector.IsCulled(in view,selector.FromCamera(in view,in opposite,offset),radius),Is.True);
            }
        }

        private static void AssertCovers(List<PlanetCellKey> cells){
            var set=new HashSet<PlanetCellKey>(cells);
            Assert.That(set.Count,Is.EqualTo(cells.Count),"同じ Cell が2回選ばれています。");
            var area=new float[CubeSphere.FaceCount];
            foreach(PlanetCellKey key in cells){
                area[key.Face]+=Mathf.Pow(0.25f,key.Level);
                PlanetCellKey ancestor=key;
                while(ancestor.Level>0){
                    ancestor=ancestor.Parent();
                    Assert.That(set.Contains(ancestor),Is.False,key+" と祖先 "+ancestor+" が両方選ばれています。");
                }
            }
            for(int face=0;face<CubeSphere.FaceCount;face++){
                Assert.That(area[face],Is.EqualTo(1f).Within(1e-5f),"face "+face);
            }
        }
    }
}
