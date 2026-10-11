using System;
using NUnit.Framework;
using UnityEngine;

namespace VirtualGeometry.World.Tests{
    public sealed class PlanetOceanSourceTests{
        [Test]
        public void TimePhase(){
            {
                // 0 以上 2π 未満で、小さい時刻では ω·t そのもの(2π を超えた分だけ巻く)
                Assert.That(PlanetOceanSource.TimePhase(2f,0.5),Is.EqualTo(1.0f).Within(1e-6f));
                Assert.That(PlanetOceanSource.TimePhase(2f,0.0),Is.EqualTo(0f));
                float wrapped=PlanetOceanSource.TimePhase(2f,4.0);
                Assert.That(wrapped,Is.EqualTo(8.0-2.0*Math.PI).Within(1e-5));
                // 負の時刻も [0, 2π)
                float negative=PlanetOceanSource.TimePhase(2f,-0.5);
                Assert.That(negative,Is.EqualTo(2.0*Math.PI-1.0).Within(1e-5));
            }
            {
                // 長時間(10^3〜6×10^8 秒、約 19 年)でも、倍精度の参照(ω は float の値、積と剰余は倍精度)と 1e-5 rad 以内で、常に [0, 2π)。
                // float の積 ω·t を float の Repeat で巻く式は、10^5 秒で 0.05 rad 以上、10^7 秒でほぼ無意味な値になる(下の旧式)。
                float[] omegas={0.7f,3.3f,7.1f,11.0f};
                double[] times={1e3,1e5,1e6,1e7,6e8};
                double worstOld=0;
                foreach(float omega in omegas){
                    foreach(double time in times){
                        float actual=PlanetOceanSource.TimePhase(omega,time);
                        Assert.That(actual,Is.GreaterThanOrEqualTo(0f).And.LessThan((float)(2.0*Math.PI)+1e-6f),"範囲 "+omega+" "+time);
                        double product=(double)omega*time;
                        double expected=product-2.0*Math.PI*Math.Floor(product/(2.0*Math.PI));
                        double difference=Math.Abs(actual-expected);
                        difference=Math.Min(difference,2.0*Math.PI-difference);
                        Assert.That(difference,Is.LessThan(1e-5),"ω="+omega+" t="+time);
                        // 旧式: float の時刻と積
                        float oldPhase=Mathf.Repeat(omega*(float)time,2f*Mathf.PI);
                        double oldDifference=Math.Abs(oldPhase-expected);
                        oldDifference=Math.Min(oldDifference,2.0*Math.PI-oldDifference);
                        if(time>=1e5){worstOld=Math.Max(worstOld,oldDifference);}
                    }
                }
                Assert.That(worstOld,Is.GreaterThan(0.05),"旧式(float の積)が長時間で狂うこと(この検査が旧式で失敗する根拠)");
            }
        }

        [Test]
        public void WaveTimeAccumulation(){
            // 毎 frame 約 1/60 秒を足し続けた時刻。倍精度なら 10^8 回(約 19 日)でも誤差が 1e-3 秒未満。float の加算は 2^19 秒(約 6 日)で丸めに消えて止まる。
            const int frames=100000000;
            const float step=1f/60f;
            double accumulated=0;
            for(int i=0;i<frames;i++){accumulated+=step;}
            Assert.That(accumulated,Is.EqualTo((double)step*frames).Within(1e-3));
            float floatTime=0;
            for(int i=0;i<frames;i++){floatTime+=step;}
            Assert.That((double)floatTime,Is.LessThan(0.5*accumulated),"float の加算が止まること(この検査が旧式で失敗する根拠)");
        }
    }
}
