using System;
using NUnit.Framework;
using UnityEngine;

namespace VirtualGeometry.World.Tests{
    public sealed class PlanetHeightFieldTests{
        // 基準の波長 4,000 m、32 octave。格子の基点は 0(sphere は Cell の原点からの相対位置 m)。
        private const float BaseWavelength=4000f;
        private const int Octaves=32;
        private static readonly float InverseWavelength=1f/BaseWavelength;

        private static PlanetHeightLattice ZeroLattice(){
            var lattice=new PlanetHeightLattice();
            for(int i=0;i<PlanetHeightField.MaximumOctaves;i++){lattice.Lattice[i]=Vector3Int.zero;lattice.Fraction[i]=Vector3.zero;}
            return lattice;
        }

        private static double Log2(double value){
            return Math.Log(value)/Math.Log(2.0);
        }

        // 乱数の点 count 個の増幅の項の、平均と二乗平均の平方根。
        private static void Statistics(float amplitude,float hurst,float cutoffLog2,float quadLength,int count,out double mean,out double rms){
            PlanetHeightLattice lattice=ZeroLattice();
            var random=new System.Random(5);
            double sum=0;double sumSquare=0;
            for(int i=0;i<count;i++){
                var sphere=new Vector3((float)(random.NextDouble()*2000-1000),(float)(random.NextDouble()*2000-1000),(float)(random.NextDouble()*2000-1000));
                float value=PlanetHeightField.Amplified(amplitude,hurst,cutoffLog2,InverseWavelength,Octaves,lattice,sphere,quadLength);
                sum+=value;sumSquare+=value*value;
            }
            mean=sum/count;rms=Math.Sqrt(sumSquare/count);
        }

        [Test]
        public void Amplified(){
            // 遮断波長 = 画素 2 つ(ここでは 1,000 m)。
            float cutoffLog2=(float)Log2(1000.0);
            {
                // 振幅 0 なら 0。
                Statistics(0f,0.87f,cutoffLog2,0f,200,out double mean,out double rms);
                Assert.That(rms,Is.EqualTo(0.0));
            }
            {
                // 遮断波長が最も細かい octave(4,000 m / 2^31 = 1.9 µm)の波長より短ければ、足す octave が無いので 0。
                float tooFine=(float)Log2(1e-7);
                Statistics(30f,0.87f,tooFine,0f,200,out double mean,out double rms);
                Assert.That(rms,Is.EqualTo(0.0));
            }
            {
                // 帯域制限: quad の長さが遮断波長の半分(500 m)以上の Cell には入らない(波長が 2·quadLength 以上の octave は、遮断波長を超える)。
                Statistics(30f,0.87f,cutoffLog2,501f,200,out double mean,out double rms);
                Assert.That(rms,Is.EqualTo(0.0),"粗い Cell");
                Statistics(30f,0.87f,cutoffLog2,0.05f,200,out double fineMean,out double fineRms);
                Assert.That(fineRms,Is.GreaterThan(0.1),"細かい Cell には入る");
            }
            {
                // 振幅に比例する(線形)。平均は 0 に近い(gradient noise は平均 0)。
                Statistics(10f,0.87f,cutoffLog2,0f,4000,out double mean1,out double rms1);
                Statistics(30f,0.87f,cutoffLog2,0f,4000,out double mean3,out double rms3);
                Assert.That(rms3/rms1,Is.EqualTo(3.0).Within(1e-3));
                Assert.That(Math.Abs(mean3),Is.LessThan(0.1*rms3),"平均 "+mean3+" 二乗平均の平方根 "+rms3);
            }
            {
                // Hurst 指数が大きいほど細かい octave の振幅が小さく、全体の二乗平均の平方根が小さい。
                Statistics(30f,0.5f,cutoffLog2,0f,4000,out double meanLow,out double rmsLow);
                Statistics(30f,1.0f,cutoffLog2,0f,4000,out double meanHigh,out double rmsHigh);
                Assert.That(rmsHigh,Is.LessThan(rmsLow));
            }
            {
                // 二乗平均の平方根の大きさ: 振幅 A の最初の octave(遮断波長 1,000 m の隣、x ∈ (0, 1])の gradient noise は、二乗平均の平方根がおよそ A·0.3〜0.5。
                // 合計は、各 octave の振幅 A·2^(−H·x) の二乗和の平方根にほぼ比例する(octave の noise は独立)。上界 1.5·A/(1−2^−H) を超えない。
                Statistics(30f,0.87f,cutoffLog2,0f,4000,out double mean,out double rms);
                Assert.That(rms,Is.GreaterThan(30.0*0.1));
                Assert.That(rms,Is.LessThan(PlanetHeightField.AmplifiedMaximum(30f,0.87f)));
            }
        }

        [Test]
        public void AmplifiedTaper(){
            // 遮断波長より短い octave は 1 octave かけて入る: 遮断波長を連続に振ると、増幅の項は連続に変わる(重み saturate(x))。
            PlanetHeightLattice lattice=ZeroLattice();
            var sphere=new Vector3(123.4f,-55.5f,77.7f);
            float previous=PlanetHeightField.Amplified(30f,0.87f,(float)Log2(1000.0),InverseWavelength,Octaves,lattice,sphere,0f);
            float largestStep=0;
            for(int i=1;i<=100;i++){
                float cutoff=(float)Log2(1000.0)+i*0.01f;
                float value=PlanetHeightField.Amplified(30f,0.87f,cutoff,InverseWavelength,Octaves,lattice,sphere,0f);
                largestStep=Mathf.Max(largestStep,Mathf.Abs(value-previous));
                previous=value;
            }
            // 遮断波長の log2 が 0.01 動くと、重みは 0.01、振幅は 0.6% 変わる。1 step の変化は小さい(30 m × 1.5 × 0.01 × octave 数 の桁)。
            Assert.That(largestStep,Is.LessThan(2.0f),"連続に変わる");
        }

        [Test]
        public void AmplifiedMaximum(){
            Assert.That(PlanetHeightField.AmplifiedMaximum(0f,0.87f),Is.EqualTo(0f));
            Assert.That(PlanetHeightField.AmplifiedMaximum(30f,0.87f),Is.EqualTo(1.5f*30f/(1f-Mathf.Pow(2f,-0.87f))).Within(1e-3f));
        }
    }
}
