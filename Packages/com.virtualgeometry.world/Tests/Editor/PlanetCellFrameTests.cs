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
                //半径 10^19 m の深い階層(L64、64 ビット目まで使う番号)で、隣り合う Cell の原点の間隔が一定(1e-6 m 以内)。
                //Omni の Scalar.TryDivide の桁を 1 つ落とす不具合(推定商のクランプ)が、番号の下位桁が繰り上がる所の中心の正接を 2^-64 ほどずらし、
                //x=12646113105388128352 の Cell が 0.5m ずれていた(PlanetCellFrame.TryTangent は BigInteger で割る)。
                Assert.That(LogicalScalar.TryFromFloat(1e19f,out LogicalScalar huge),Is.True);
                foreach(ulong start in new[]{12646113105388128256UL,9223372036854775700UL,18446744073709551000UL}){
                    double minimum=double.MaxValue;double maximum=double.MinValue;
                    LogicalVector3 previous=default;
                    for(int k=0;k<200;k++){
                        var key=new PlanetCellKey(2,64,new PlanetCellCoordinate(start+(ulong)k,0),new PlanetCellCoordinate(11541499068720415387UL,0));
                        Assert.That(PlanetCellFrame.TryCreate(in key,in huge,out PlanetCellFrame frame),Is.True);
                        if(k>0){
                            Assert.That(LogicalVector3.TrySubtract(frame.Origin,previous,out LogicalVector3 difference),Is.True);
                            Assert.That(LogicalVector3.TryLength(in difference,out LogicalScalar length),Is.True);
                            Assert.That(length.TryToDouble(out double spacing),Is.True);
                            minimum=System.Math.Min(minimum,spacing);maximum=System.Math.Max(maximum,spacing);
                        }
                        previous=frame.Origin;
                    }
                    Assert.That(maximum-minimum,Is.LessThan(1e-6),"start "+start+": 間隔が "+minimum+" から "+maximum+" まで揺れています。");
                    Assert.That(minimum,Is.GreaterThan(0.1));
                }
            }
            {
                //表せない深さは失敗する
                PlanetCellKey key=PlanetCellKey.Root(0);
                for(int depth=0;depth<=PlanetCellFrame.MaximumLevel;depth++){key=key.Child(1);}
                Assert.That(PlanetCellFrame.TryCreate(in key,LogicalScalar.One,out PlanetCellFrame _),Is.False);
            }
        }

        [Test]
        public void TryWithHeightShift(){
            PlanetCellKey key=PlanetCellKey.Root(2).Child(1).Child(3).Child(0).Child(2);
            {
                //半径 1・持ち上げ 0.25 で、|Origin| = 1.25(|Origin|²−1.5625 が 2^-80 未満)。SphereOrigin は持ち上げ前の Origin とビット単位で同じ
                Assert.That(PlanetCellFrame.TryCreate(in key,LogicalScalar.One,out PlanetCellFrame frame),Is.True);
                Assert.That(frame.HeightShift,Is.EqualTo(0f));
                Assert.That(frame.TryWithHeightShift(0.25f,out PlanetCellFrame lifted),Is.True);
                Assert.That(lifted.HeightShift,Is.EqualTo(0.25f));
                Assert.That(LogicalVector3.TrySqrLength(lifted.Origin,out LogicalScalar square),Is.True);
                Assert.That(LogicalScalar.TrySubtract(in square,Scalar(1.5625f),out LogicalScalar error),Is.True);
                Assert.That(error.TryToDouble(out double value),Is.True);
                Assert.That(System.Math.Abs(value),Is.LessThan(System.Math.Pow(2,-80)));
                Assert.That(Distance(lifted.SphereOrigin,frame.Origin),Is.EqualTo(0.0));
                Assert.That(Distance(frame.SphereOrigin,frame.Origin),Is.EqualTo(0.0),"持ち上げない Cell は Origin == SphereOrigin");
            }
            {
                //月(R=1,737.4km)の起伏 +9 km と −7.3 km、半径 10^20 m の +9 km で、持ち上げた距離が float の値のまま Logical に入り、向きは変わらない
                foreach(float radius in new[]{1737400f,1e20f}){
                    foreach(float shift in new[]{9000.0009765625f,-7304.3779296875f,0.001f}){
                        Assert.That(PlanetCellFrame.TryCreate(in key,Scalar(radius),out PlanetCellFrame frame),Is.True);
                        Assert.That(frame.TryWithHeightShift(shift,out PlanetCellFrame lifted),Is.True);
                        // 単位方向 × shift で作るので、R によらず 2^-96 の桁で一致する(Origin·(shift/R) の比で作ると、R=10^20 m で 1e-9 m の誤差が出る)
                        Assert.That(Distance(lifted.Origin,lifted.SphereOrigin),Is.EqualTo(System.Math.Abs((double)shift)).Within(5e-12),"R="+radius+" shift="+shift);
                        Assert.That(Distance(lifted.SphereOrigin,frame.SphereOrigin),Is.EqualTo(0.0));
                        Assert.That(lifted.ApproximateSphereOrigin,Is.EqualTo(frame.ApproximateOrigin));
                        Assert.That(((lifted.ApproximateOrigin/radius).normalized-(frame.ApproximateOrigin/radius).normalized).magnitude,Is.LessThan(1e-6f),"向きは変わらない");
                        Assert.That(lifted.HeightShift,Is.EqualTo(shift));
                        Assert.That(lifted.Key,Is.EqualTo(frame.Key));
                        Assert.That(lifted.ApproximateTangent,Is.EqualTo(frame.ApproximateTangent));
                    }
                }
            }
            {
                //ノイズの格子と波の位相は、高さ 0 の球面の原点で決まる(持ち上げの有無で同じ。持ち上げた原点から求めると、持ち上げが違う隣の Cell と食い違う)
                Assert.That(PlanetCellFrame.TryCreate(in key,Scalar(1737400f),out PlanetCellFrame frame),Is.True);
                Assert.That(frame.TryWithHeightShift(8123.5f,out PlanetCellFrame lifted),Is.True);
                Assert.That(LogicalScalar.TryReciprocal(Scalar(4000f),out LogicalScalar inverse),Is.True);
                for(int octave=0;octave<20;octave++){
                    Assert.That(frame.TryNoiseLattice(in inverse,octave,out Vector3Int latticeA,out Vector3 fractionA),Is.True);
                    Assert.That(lifted.TryNoiseLattice(in inverse,octave,out Vector3Int latticeB,out Vector3 fractionB),Is.True);
                    Assert.That(latticeB,Is.EqualTo(latticeA),"octave "+octave);
                    Assert.That(fractionB,Is.EqualTo(fractionA),"octave "+octave);
                }
                Assert.That(LogicalScalar.TryReciprocal(Scalar(30f),out LogicalScalar inverseWavelength),Is.True);
                Assert.That(frame.TryWavePhase(in frame.Direction,in inverseWavelength,out float phaseA),Is.True);
                Assert.That(lifted.TryWavePhase(in frame.Direction,in inverseWavelength,out float phaseB),Is.True);
                Assert.That(phaseB,Is.EqualTo(phaseA));
            }
            {
                //有限でない距離は受け付けない
                Assert.That(PlanetCellFrame.TryCreate(in key,LogicalScalar.One,out PlanetCellFrame frame),Is.True);
                Assert.That(frame.TryWithHeightShift(float.NaN,out PlanetCellFrame _),Is.False);
                Assert.That(frame.TryWithHeightShift(float.PositiveInfinity,out PlanetCellFrame _),Is.False);
                Assert.That(frame.TryWithHeightShift(float.NegativeInfinity,out PlanetCellFrame _),Is.False);
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

        // 2 点の距離(m)。
        private static double Distance(in LogicalVector3 a,in LogicalVector3 b){
            Assert.That(LogicalVector3.TrySubtract(in a,in b,out LogicalVector3 difference),Is.True);
            Assert.That(LogicalVector3.TryLength(in difference,out LogicalScalar length),Is.True);
            Assert.That(length.TryToDouble(out double value),Is.True);
            return value;
        }

        private static LogicalScalar Scalar(float value){
            Assert.That(LogicalScalar.TryFromFloat(value,out LogicalScalar result),Is.True);
            return result;
        }
    }
}
