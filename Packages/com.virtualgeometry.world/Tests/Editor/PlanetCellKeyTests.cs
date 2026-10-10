using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace VirtualGeometry.World.Tests{
    public sealed class PlanetCellKeyTests{
        [Test]
        public void Child(){
            {
                //4つの子は親の範囲をちょうど覆う(重ならず、隙間もない)
                PlanetCellKey parent=new PlanetCellKey(3,2,new PlanetCellCoordinate(1,0),new PlanetCellCoordinate(2,0));
                PlanetCellSelector.FaceRange(in parent,out float ps,out float pt,out float psize);
                float area=0;
                for(int child=0;child<4;child++){
                    PlanetCellKey key=parent.Child(child);
                    PlanetCellSelector.FaceRange(in key,out float s,out float t,out float size);
                    Assert.That(size,Is.EqualTo(psize*0.5f));
                    Assert.That(s,Is.EqualTo(ps+((child&1)*size)));
                    Assert.That(t,Is.EqualTo(pt+(((child>>1)&1)*size)));
                    Assert.That(key.ChildIndex,Is.EqualTo(child));
                    area+=size*size;
                }
                Assert.That(area,Is.EqualTo(psize*psize));
            }
        }

        [Test]
        public void Parent(){
            {
                //子から親へ戻る
                PlanetCellKey parent=new PlanetCellKey(5,4,new PlanetCellCoordinate(9,0),new PlanetCellCoordinate(14,0));
                for(int child=0;child<4;child++){Assert.That(parent.Child(child).Parent(),Is.EqualTo(parent));}
            }
            {
                //root には親が無い
                Assert.Throws<System.InvalidOperationException>(()=>PlanetCellKey.Root(0).Parent());
            }
        }

        [Test]
        public void Neighbor(){
            {
                //全ての面・階層0〜3の全 Cell・全方向で、隣の隣のどれかが自分に戻り、共有する辺の中点が両側で一致する
                for(int face=0;face<CubeSphere.FaceCount;face++){
                    for(int level=0;level<=3;level++){
                        ulong count=1UL<<level;
                        for(ulong x=0;x<count;x++){
                            for(ulong y=0;y<count;y++){
                                var key=new PlanetCellKey(face,level,new PlanetCellCoordinate(x,0),new PlanetCellCoordinate(y,0));
                                for(int direction=0;direction<PlanetCellKey.DirectionCount;direction++){
                                    PlanetCellKey neighbor=key.Neighbor(direction);
                                    Assert.That(neighbor.Level,Is.EqualTo(level));
                                    bool back=false;
                                    for(int reverse=0;reverse<PlanetCellKey.DirectionCount;reverse++){
                                        if(neighbor.Neighbor(reverse).Equals(key)){back=true;}
                                    }
                                    Assert.That(back,Is.True,key+" -> "+neighbor);
                                    Vector3 edge=EdgeMidpoint(in key,direction);
                                    Assert.That(ClosestEdgeMidpoint(in neighbor,edge),Is.LessThan(1e-5f),key+" -> "+neighbor);
                                }
                            }
                        }
                    }
                }
            }
            {
                //64ビットを超える階層でも、面の境界をまたいで往復する
                PlanetCellCoordinate last=PlanetCellCoordinate.Last(80);
                var key=new PlanetCellKey(0,80,last,new PlanetCellCoordinate(12345,9));
                PlanetCellKey neighbor=key.Neighbor(1);
                Assert.That(neighbor.Face,Is.Not.EqualTo(0));
                bool back=false;
                for(int reverse=0;reverse<PlanetCellKey.DirectionCount;reverse++){
                    if(neighbor.Neighbor(reverse).Equals(key)){back=true;}
                }
                Assert.That(back,Is.True);
            }
        }

        [Test]
        public void Contains(){
            {
                //自分と子孫を含み、隣や親は含まない
                PlanetCellKey key=new PlanetCellKey(1,2,new PlanetCellCoordinate(1,0),new PlanetCellCoordinate(3,0));
                Assert.That(key.Contains(key),Is.True);
                Assert.That(key.Contains(key.Child(2).Child(1)),Is.True);
                Assert.That(key.Contains(key.Parent()),Is.False);
                Assert.That(key.Contains(key.Neighbor(0)),Is.False);
            }
        }

        private static Vector3 EdgeMidpoint(in PlanetCellKey key,int direction){
            PlanetCellSelector.FaceRange(in key,out float s,out float t,out float size);
            if(direction==0){return CubeSphere.Direction(key.Face,s,t+size*0.5f);}
            if(direction==1){return CubeSphere.Direction(key.Face,s+size,t+size*0.5f);}
            if(direction==2){return CubeSphere.Direction(key.Face,s+size*0.5f,t);}
            return CubeSphere.Direction(key.Face,s+size*0.5f,t+size);
        }

        private static float ClosestEdgeMidpoint(in PlanetCellKey key,Vector3 point){
            float best=float.MaxValue;
            for(int direction=0;direction<PlanetCellKey.DirectionCount;direction++){
                best=Mathf.Min(best,Vector3.Distance(EdgeMidpoint(in key,direction),point));
            }
            return best;
        }
    }
}
