using NUnit.Framework;

namespace VirtualGeometry.Omni.Tests{
    public sealed class PlaneSinkLayoutTests{
        [Test]
        public void Positions(){
            {
                //既定: S=2(D+3)、左の面は x∈[−2D−6, 0]、試験点は左 x=−3、右 x=+3
                PlaneSinkLayout layout=PlaneSinkLayout.Default;
                Assert.That(layout.PlaneScale,Is.EqualTo(2000006.0));
                Assert.That(layout.LeftOriginX,Is.EqualTo(-1000003.0));
                Assert.That(layout.LeftOriginX+layout.PlaneScale*0.5,Is.EqualTo(0.0),"右端が継ぎ目 x=0");
                Assert.That(layout.LeftOriginX-layout.PlaneScale*0.5,Is.EqualTo(-2000006.0));
                Assert.That(layout.LeftSpotX,Is.EqualTo(-3.0));
                Assert.That(layout.RightSpotX,Is.EqualTo(3.0));
            }
            {
                //D を変えても、右端は継ぎ目のまま
                foreach(double distance in new[]{1e4,1e5,1e6,1e7}){
                    PlaneSinkLayout layout=PlaneSinkLayout.Default;
                    layout.OriginDistance=distance;
                    Assert.That(layout.LeftOriginX+layout.PlaneScale*0.5,Is.EqualTo(0.0).Within(1e-6),"D="+distance);
                    Assert.That(layout.LeftSpotX-layout.LeftOriginX,Is.EqualTo(distance).Within(1e-6),"試験点は面の原点から D");
                }
            }
        }

        [Test]
        public void LeftQuantum(){
            {
                //物体空間の float の刻み ulp(D/S)·S は、D に比例して増える(D=10^4 から 10^7 で 0.6 mm、6 mm、6 cm、0.6 m の桁)
                var expected=new[]{(1e4,0.6e-3),(1e5,6e-3),(1e6,6e-2),(1e7,0.6)};
                double previous=0;
                foreach((double distance,double quantum) in expected){
                    PlaneSinkLayout layout=PlaneSinkLayout.Default;
                    layout.OriginDistance=distance;
                    double value=layout.LeftQuantum();
                    Assert.That(value,Is.GreaterThan(quantum*0.5).And.LessThan(quantum*2.0),"D="+distance+" quantum="+value);
                    Assert.That(value,Is.GreaterThan(previous));
                    previous=value;
                }
            }
        }
    }
}
