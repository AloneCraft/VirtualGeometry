using NUnit.Framework;
using UnityEngine;
using LogicalScalar=Omni.Logical.Scalar;

namespace VirtualGeometry.World.Tests{
    public sealed class PlanetCellGeometryTests{
        private const double Radius=1737400.0;

        // 階層 22(一辺 約 41 m、quad 0.64 m)の Cell。
        private static PlanetCellKey DeepKey(){
            PlanetCellKey key=PlanetCellKey.Root(2);
            for(int depth=0;depth<22;depth++){key=key.Child((depth*5+1)%4);}
            return key;
        }

        [Test]
        public void VertexPosition(){
            {
                //持ち上げなし(shift=0)は、球面上の点 + 方向 × 高さ
                Vector3 sphere=new Vector3(0.31f,-1.7f,0.02f);Vector3 direction=new Vector3(0.6f,0.0f,0.8f);
                Assert.That(PlanetCellGeometry.VertexPosition(sphere,direction,123.25f,0f,(float)Radius),Is.EqualTo(sphere+direction*123.25f));
            }
            {
                //持ち上げた原点(Origin)からの相対位置に Origin を足すと、頂点の絶対位置 (R+h)·n になる(H によらない)。
                //sphere·(H/R) の項が無いと、sphere が 0.6m で H/R=0.0047 なら 3 mm ずれる。
                Assert.That(LogicalScalar.TryFromDouble(Radius,out LogicalScalar radius),Is.True);
                PlanetCellKey key=DeepKey();
                Assert.That(PlanetCellFrame.TryCreate(in key,in radius,out PlanetCellFrame sphereFrame),Is.True);
                foreach(float shift in new[]{8123.5f,-7304.375f}){
                    Assert.That(sphereFrame.TryWithHeightShift(shift,out PlanetCellFrame frame),Is.True);
                    Assert.That(frame.Origin.X.TryToDouble(out double ox),Is.True);
                    Assert.That(frame.Origin.Y.TryToDouble(out double oy),Is.True);
                    Assert.That(frame.Origin.Z.TryToDouble(out double oz),Is.True);
                    Assert.That(frame.SphereOrigin.X.TryToDouble(out double sx),Is.True);
                    Assert.That(frame.SphereOrigin.Y.TryToDouble(out double sy),Is.True);
                    Assert.That(frame.SphereOrigin.Z.TryToDouble(out double sz),Is.True);
                    foreach(int a in new[]{0,16,32,48,64}){
                        foreach(int b in new[]{0,24,64}){
                            Vector3 sphere=PlanetCellGeometry.SpherePoint(in frame,PlanetLayerSource.CellQuads,a,b,(float)Radius,out Vector3 direction);
                            // Cell の中の起伏(h − H)は ±1 m
                            float relative=(a*0.013f-b*0.007f)-0.4f;
                            Vector3 position=PlanetCellGeometry.VertexPosition(sphere,direction,relative,shift,(float)Radius);
                            // R·n = SphereOrigin + sphere、高さ h = H + relative なので、頂点 = (1 + h/R)·(SphereOrigin + sphere)
                            double scale=1.0+(shift+(double)relative)/Radius;
                            double ex=scale*(sx+sphere.x),ey=scale*(sy+sphere.y),ez=scale*(sz+sphere.z);
                            double error=System.Math.Sqrt(System.Math.Pow(ox+position.x-ex,2)+System.Math.Pow(oy+position.y-ey,2)+System.Math.Pow(oz+position.z-ez,2));
                            Assert.That(error,Is.LessThan(1e-5),"shift="+shift+" ("+a+","+b+")");
                        }
                    }
                }
            }
        }
    }
}
