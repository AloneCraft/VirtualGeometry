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

        private static double Length(in LogicalVector3 vector){
            Assert.That(LogicalVector3.TryLength(in vector,out LogicalScalar length),Is.True);
            Assert.That(length.TryToDouble(out double value),Is.True);
            return value;
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
        public void Frame(){
            PlanetCellKey key=PlanetCellKey.Root(4).Child(0).Child(2).Child(1);
            {
                //高さの供給元があると、原点は半径 R+H の球面上の、Cell の中心の方向の点になる(SphereOrigin は半径 R)。同じ Cell には同じ値
                int calls=0;bool receivedLifted=false;
                var selector=new PlanetCellSelector(Radius,Height,64,10,2,(in PlanetCellFrame sphereFrame,out float shift)=>{
                    calls++;
                    // 供給元に渡るのは持ち上げる前の Cell の値
                    if(sphereFrame.HeightShift!=0f||Length(sphereFrame.Origin)!=Length(sphereFrame.SphereOrigin)){receivedLifted=true;}
                    shift=250f;return true;
                });
                PlanetCellFrame frame=selector.Frame(in key);
                Assert.That(frame.HeightShift,Is.EqualTo(250f));
                Assert.That(Length(frame.Origin),Is.EqualTo(Radius+250.0).Within(1e-6));
                Assert.That(Length(frame.SphereOrigin),Is.EqualTo(Radius).Within(1e-6));
                // 同じ Cell の値は覚えていて、供給元に聞き直さない
                Assert.That(selector.Frame(in key).HeightShift,Is.EqualTo(250f));
                Assert.That(calls,Is.EqualTo(1));
                Assert.That(selector.Frame(key.Child(1)).HeightShift,Is.EqualTo(250f));
                Assert.That(calls,Is.EqualTo(2));
                Assert.That(receivedLifted,Is.False);
            }
            {
                //供給元が失敗する、有限でない、|H| ≥ R、0 のときは持ち上げない
                foreach(float bad in new[]{float.NaN,float.PositiveInfinity,-Radius,Radius*2f,0f}){
                    float captured=bad;
                    var selector=new PlanetCellSelector(Radius,Height,64,10,2,(in PlanetCellFrame sphereFrame,out float shift)=>{shift=captured;return true;});
                    Assert.That(selector.Frame(in key).HeightShift,Is.EqualTo(0f),"shift="+bad);
                    Assert.That(Length(selector.Frame(in key).Origin),Is.EqualTo(Radius).Within(1e-6));
                }
                var failing=new PlanetCellSelector(Radius,Height,64,10,2,(in PlanetCellFrame sphereFrame,out float shift)=>{shift=250f;return false;});
                Assert.That(failing.Frame(in key).HeightShift,Is.EqualTo(0f));
                Assert.That(Create(10).Frame(in key).HeightShift,Is.EqualTo(0f),"供給元が無ければ持ち上げない");
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
            {
                //原点を地表の高さまで持ち上げた Cell でも、カメラ(地表から 1.7m)との距離は 1.7m(持ち上げた原点からの差を、Logical で取る)
                var selector=new PlanetCellSelector(6371000f,3000f,PlanetTerrainSource.CellQuads,PlanetCellFrame.MaximumLevel,2f,(in PlanetCellFrame sphereFrame,out float shift)=>{shift=2468.5f;return true;});
                PlanetCellKey key=PlanetCellKey.Root(4);
                for(int depth=0;depth<26;depth++){key=key.Child((depth*7)%4);}
                PlanetCellFrame frame=selector.Frame(in key);
                Assert.That(LogicalScalar.TryFromFloat(1.7f/(6371000f+2468.5f),out LogicalScalar ratio),Is.True);
                Assert.That(LogicalVector3.TryScale(frame.Origin,in ratio,out LogicalVector3 lift),Is.True);
                Assert.That(LogicalVector3.TryAdd(frame.Origin,lift,out LogicalVector3 camera),Is.True);
                var view=new PlanetSelectionView(in camera,selector.LogicalRadius,Matrix4x4.identity,Vector3.zero,1,null,500,0.1f,false,true);
                Assert.That(view.CameraAltitude,Is.EqualTo(2468.5f+1.7f).Within(1e-2f));
                Assert.That(selector.FromCamera(in view,in key,Vector3.zero).magnitude,Is.EqualTo(1.7f).Within(1e-3f));
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
            {
                //原点を持ち上げた Cell(H=±250 m)でも、粗い階層(SampledBounds)と細かい階層(FrameBounds)の bounds が、半径 R±Hmax と skirt の底の点をすべて含む
                foreach(float lift in new[]{250f,-250f}){
                    float captured=lift;
                    var selector=new PlanetCellSelector(Radius,Height,PlanetTerrainSource.CellQuads,PlanetCellFrame.MaximumLevel,2f,(in PlanetCellFrame sphereFrame,out float shift)=>{shift=captured;return true;});
                    var keys=new List<PlanetCellKey>{PlanetCellKey.Root(2),PlanetCellKey.Root(2).Child(3),PlanetCellKey.Root(4).Child(0).Child(2).Child(1)};
                    PlanetCellKey deep=PlanetCellKey.Root(1);
                    for(int depth=0;depth<PlanetCellSelector.FrameBoundsLevel+2;depth++){
                        deep=deep.Child(depth%4);
                        if(deep.Level>=PlanetCellSelector.FrameBoundsLevel){keys.Add(deep);}
                    }
                    foreach(PlanetCellKey key in keys){
                        selector.Bounds(in key,out Vector3 offset,out float radius);
                        PlanetCellFrame frame=selector.Frame(in key);
                        Assert.That(frame.HeightShift,Is.EqualTo(lift));
                        Vector3 center=frame.ApproximateOrigin+offset;
                        PlanetCellSelector.FaceRange(in key,out float s0,out float t0,out float size);
                        for(int j=0;j<=4;j++){
                            for(int i=0;i<=4;i++){
                                Vector3 direction=CubeSphere.Direction(key.Face,s0+size*i/4f,t0+size*j/4f);
                                foreach(float r in new[]{Radius-Height-selector.SkirtDepth(key.Level),Radius+Height}){
                                    Assert.That((direction*r-center).magnitude,Is.LessThanOrEqualTo(radius*1.0001f),key+" lift="+lift);
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
        public void SelectHugePlanet(){
            // 半径 10^20 m の惑星(float の二乗が 3.4e38 を超える大きさ)の地表の目の高さで、粗い Cell の bounds が有限で、地平線の向こうの Cell が選ばれない。
            // カメラの高さは、惑星の半径(float の 1e20f は 1e20 とは違う値)の Logical に 1.7m を足して作る(double の 1e20+1.7 を使うと、地下 2e12 m になる)。
            const float huge=1e20f;
            var selector=new PlanetCellSelector(huge,0f,64,80,2){TargetPixels=2,Hysteresis=0.1f};
            for(int face=0;face<CubeSphere.FaceCount;face++){
                PlanetCellKey key=PlanetCellKey.Root(face);
                for(int level=0;level<PlanetCellSelector.FrameBoundsLevel;level++){
                    selector.Bounds(in key,out Vector3 offset,out float radius);
                    Assert.That(float.IsInfinity(radius)||float.IsNaN(radius),Is.False,"bounds の半径が有限ではありません: "+key);
                    Assert.That(radius,Is.GreaterThan(0f),key.ToString());
                    key=key.Child(0);
                }
            }
            Assert.That(LogicalScalar.TryFromFloat(huge,out LogicalScalar radiusLogical),Is.True);
            Assert.That(LogicalScalar.TryFromDouble(1.7,out LogicalScalar height),Is.True);
            Assert.That(LogicalScalar.TryAdd(in radiusLogical,in height,out LogicalScalar eye),Is.True);
            var camera=new LogicalVector3(LogicalScalar.Zero,eye,LogicalScalar.Zero);
            var view=new PlanetSelectionView(in camera,in radiusLogical,Matrix4x4.identity,new Vector3(0,huge,0),1,null,500,0.1f,false,true);
            var residency=new Residency();
            var output=new List<PlanetCellKey>();var refined=new HashSet<PlanetCellKey>();
            selector.Select(in view,residency,null,output,refined);
            Assert.That(output.Count,Is.GreaterThan(0));
            Assert.That(output.Count,Is.LessThan(12000),"選ばれた Cell が多すぎます(地平線の向こうが culling されていません)。");
            int coarse=0;int finest=0;
            foreach(PlanetCellKey key in output){
                if(key.Level<10){coarse++;}
                finest=Mathf.Max(finest,key.Level);
            }
            Assert.That(coarse,Is.EqualTo(0),"粗い階層(< 10)の Cell が選ばれています。");
            Assert.That(finest,Is.GreaterThanOrEqualTo(25),"カメラの足元の Cell が細かくなっていません。");
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
