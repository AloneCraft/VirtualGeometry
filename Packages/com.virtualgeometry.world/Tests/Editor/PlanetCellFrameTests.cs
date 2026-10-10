using NUnit.Framework;
using UnityEngine;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World.Tests{
    public sealed class PlanetCellFrameTests{
        // 面の中央・角の近く・辺の近くを通る経路(子の番号の列)。
        private static readonly int[][] Paths={new[]{0,3,0,3,1,2},new[]{0,0,0,0,0,0},new[]{3,3,3,3,3,3},new[]{1,1,2,2,1,2}};

        [Test]
        public void TryCreate(){
            {
                //粗い階層では、原点は float の等角写像で求めた中心の方向 × R と一致する
                LogicalScalar radius=Scalar(600000);
                for(int face=0;face<CubeSphere.FaceCount;face++){
                    foreach(int[] path in Paths){
                        PlanetCellKey key=PlanetCellKey.Root(face);
                        for(int depth=0;depth<=path.Length;depth++){
                            Assert.That(PlanetCellFrame.TryCreate(in key,in radius,out PlanetCellFrame frame),Is.True,key.ToString());
                            PlanetCellSelector.FaceRange(in key,out float s0,out float t0,out float size);
                            Vector3 expected=CubeSphere.Direction(face,s0+size*0.5f,t0+size*0.5f)*600000f;
                            Assert.That((frame.ApproximateOrigin-expected).magnitude,Is.LessThan(0.2f),key.ToString());
                            if(depth<path.Length){key=key.Child(path[depth]);}
                        }
                    }
                }
            }
            {
                //中心の正接は tan(π·n/2^(L+2)) と double の精度で一致する
                foreach(int[] path in Paths){
                    PlanetCellKey key=PlanetCellKey.Root(3);
                    for(int depth=0;depth<40;depth++){key=key.Child(path[depth%path.Length]);}
                    Assert.That(PlanetCellFrame.TryCreate(in key,LogicalScalar.One,out PlanetCellFrame frame),Is.True);
                    double x=(key.X.High*18446744073709551616.0+key.X.Low)*System.Math.Pow(2,-key.Level);
                    double fraction=(2.0*x+System.Math.Pow(2,-key.Level)-1.0)*0.25;
                    Assert.That(frame.TangentU.TryToDouble(out double tangent),Is.True);
                    Assert.That(tangent,Is.EqualTo(System.Math.Tan(System.Math.PI*fraction)).Within(1e-6),key.ToString());
                }
            }
            {
                //原点の長さは R(半径 1 で |原点|²−1 が 2^-80 未満)
                foreach(int[] path in Paths){
                    PlanetCellKey key=PlanetCellKey.Root(5);
                    for(int depth=0;depth<60;depth++){
                        key=key.Child(path[depth%path.Length]);
                        Assert.That(PlanetCellFrame.TryCreate(in key,LogicalScalar.One,out PlanetCellFrame frame),Is.True);
                        Assert.That(LogicalVector3.TrySqrLength(frame.Origin,out LogicalScalar square),Is.True);
                        Assert.That(LogicalScalar.TrySubtract(in square,LogicalScalar.One,out LogicalScalar error),Is.True);
                        Assert.That(error.TryToDouble(out double value),Is.True);
                        Assert.That(System.Math.Abs(value),Is.LessThan(System.Math.Pow(2,-80)),key.ToString());
                    }
                }
            }
            {
                //深い階層でも、親と子の原点の差は Cell の大きさ程度で 0 にならない(float では潰れる大きさ)
                LogicalScalar radius=Scalar(6371000);
                PlanetCellKey key=PlanetCellKey.Root(0);
                for(int depth=0;depth<80;depth++){key=key.Child(depth%4);}
                Assert.That(PlanetCellFrame.TryCreate(in key,in radius,out PlanetCellFrame child),Is.True);
                PlanetCellKey parent=key.Parent();
                Assert.That(PlanetCellFrame.TryCreate(in parent,in radius,out PlanetCellFrame frame),Is.True);
                Assert.That(LogicalVector3.TrySubtract(child.Origin,frame.Origin,out LogicalVector3 difference),Is.True);
                Assert.That(difference.TryToFloat3(out Unity.Mathematics.float3 offset),Is.True);
                // 子の中心は親の中心から、面の角度で親の幅 (π/2)·2^-(L−1) の 1/4 ずつ両方向にずれる。
                double expected=6371000.0*System.Math.PI*0.5*System.Math.Pow(2,-key.Level)*0.5*System.Math.Sqrt(2);
                double length=Unity.Mathematics.math.length(offset);
                Assert.That(length,Is.GreaterThan(expected*0.5));
                Assert.That(length,Is.LessThan(expected*1.5));
            }
            {
                //表せない深さは失敗する
                PlanetCellKey key=PlanetCellKey.Root(0);
                for(int depth=0;depth<=PlanetCellFrame.MaximumLevel;depth++){key=key.Child(1);}
                Assert.That(PlanetCellFrame.TryCreate(in key,LogicalScalar.One,out PlanetCellFrame _),Is.False);
            }
        }

        [Test]
        public void TryNoiseLattice(){
            {
                //格子番号 + 端数は、原点 × 波長の逆数 × 2^octave(小さな惑星では float で確かめられる)
                LogicalScalar radius=Scalar(10000);
                LogicalScalar wavelength=Scalar(4000);
                Assert.That(LogicalScalar.TryReciprocal(in wavelength,out LogicalScalar inverse),Is.True);
                PlanetCellKey key=PlanetCellKey.Root(4).Child(1).Child(2).Child(3);
                Assert.That(PlanetCellFrame.TryCreate(in key,in radius,out PlanetCellFrame frame),Is.True);
                for(int octave=0;octave<13;octave++){
                    Assert.That(frame.TryNoiseLattice(in inverse,octave,out Vector3Int lattice,out Vector3 fraction),Is.True);
                    Vector3 expected=frame.ApproximateOrigin*(Mathf.Pow(2,octave)/4000f);
                    Vector3 actual=(Vector3)lattice+fraction;
                    Assert.That((actual-expected).magnitude,Is.LessThan(1e-3f*Mathf.Max(1,expected.magnitude*1e-3f)),"octave "+octave);
                    Assert.That(fraction.x,Is.InRange(0f,1f));Assert.That(fraction.y,Is.InRange(0f,1f));Assert.That(fraction.z,Is.InRange(0f,1f));
                }
            }
        }

        private static LogicalScalar Scalar(float value){
            Assert.That(LogicalScalar.TryFromFloat(value,out LogicalScalar result),Is.True);
            return result;
        }
    }
}
