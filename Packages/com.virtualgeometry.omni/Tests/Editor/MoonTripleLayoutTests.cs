using NUnit.Framework;
using Unity.Mathematics;

namespace VirtualGeometry.Omni.Tests{
    public sealed class MoonTripleLayoutTests{
        [Test]
        public void FaceAt(){
            {
                //象限で面が決まる: (−,−) は VG、(−,+) は Standard100Km、(+,+) は Standard1000Km、(+,−) は手動分割
                Assert.That(MoonTripleLayout.FaceAt(-3,-4,MoonTripleFace.Vg),Is.EqualTo(MoonTripleFace.Vg));
                Assert.That(MoonTripleLayout.FaceAt(-3,4,MoonTripleFace.Vg),Is.EqualTo(MoonTripleFace.Standard100Km));
                Assert.That(MoonTripleLayout.FaceAt(3,4,MoonTripleFace.Vg),Is.EqualTo(MoonTripleFace.Standard1000Km));
                Assert.That(MoonTripleLayout.FaceAt(3,-4,MoonTripleFace.Vg),Is.EqualTo(MoonTripleFace.Manual));
            }
            {
                //x=0 または z=0 の境界の上では、渡した面(操作している面)になる
                Assert.That(MoonTripleLayout.FaceAt(0,5,MoonTripleFace.Manual),Is.EqualTo(MoonTripleFace.Manual));
                Assert.That(MoonTripleLayout.FaceAt(5,0,MoonTripleFace.Standard100Km),Is.EqualTo(MoonTripleFace.Standard100Km));
                Assert.That(MoonTripleLayout.FaceAt(0,0,MoonTripleFace.Standard1000Km),Is.EqualTo(MoonTripleFace.Standard1000Km));
            }
        }

        [Test]
        public void FaceSize(){
            {
                //既定: VG は 1000 km(10 m × 10^5)、Standard100Km は 100 km(10 m × 10^4)、Standard1000Km は 1000 km(10 m × 10^5)、手動分割は 1000 m(10 m × 100 枚)
                MoonTripleLayout layout=MoonTripleLayout.Default;
                Assert.That(layout.FaceSize(MoonTripleFace.Vg),Is.EqualTo(1e6));
                Assert.That(layout.FaceSize(MoonTripleFace.Standard100Km),Is.EqualTo(1e5));
                Assert.That(layout.FaceSize(MoonTripleFace.Standard1000Km),Is.EqualTo(1e6));
                Assert.That(layout.FaceSize(MoonTripleFace.Manual),Is.EqualTo(1000));
            }
            {
                //倍率 N を変えれば、大きさが N に比例して変わる(パラメーターで設定できる)
                MoonTripleLayout layout=MoonTripleLayout.Default;
                layout.ScaleStandard100Km=1e3;
                layout.ScaleVg=1e4;
                Assert.That(layout.FaceSize(MoonTripleFace.Standard100Km),Is.EqualTo(1e4));
                Assert.That(layout.FaceSize(MoonTripleFace.Vg),Is.EqualTo(1e5));
                Assert.That(layout.StandardScale(MoonTripleFace.Standard100Km),Is.EqualTo(1e3));
            }
        }

        [Test]
        public void VgMaximumLevel(){
            {
                //既定: 一辺 1e6 m。階層 16 では 15.3 m、階層 17 では 7.6 m。目標の 10 m 以下になる最小の階層は 17
                MoonTripleLayout layout=MoonTripleLayout.Default;
                Assert.That(layout.VgMaximumLevel,Is.EqualTo(17));
                Assert.That(layout.FaceSize(MoonTripleFace.Vg)/System.Math.Pow(2,17),Is.LessThanOrEqualTo(MoonTripleLayout.TargetCellMeters));
                Assert.That(layout.FaceSize(MoonTripleFace.Vg)/System.Math.Pow(2,16),Is.GreaterThan(MoonTripleLayout.TargetCellMeters));
            }
            {
                //一辺 1e5 m(倍率 10^4)なら、階層 14(7.6 m)。1e4 m なら階層 10(9.8 m)
                MoonTripleLayout layout=MoonTripleLayout.Default;
                layout.ScaleVg=1e4;
                Assert.That(layout.VgMaximumLevel,Is.EqualTo(14));
                layout.ScaleVg=1e3;
                Assert.That(layout.VgMaximumLevel,Is.EqualTo(10));
            }
        }

        [Test]
        public void FaceCenter(){
            {
                //4 面は角が原点で接する: VG は (−x,−z)、Standard100Km は (−x,+z)、Standard1000Km は (+x,+z)、手動分割は (+x,−z)
                MoonTripleLayout layout=MoonTripleLayout.Default;
                Assert.That(layout.FaceCenter(MoonTripleFace.Vg),Is.EqualTo(new double3(-5e5,0,-5e5)));
                Assert.That(layout.FaceCenter(MoonTripleFace.Standard100Km),Is.EqualTo(new double3(-5e4,0,5e4)));
                Assert.That(layout.FaceCenter(MoonTripleFace.Standard1000Km),Is.EqualTo(new double3(5e5,0,5e5)));
                Assert.That(layout.FaceCenter(MoonTripleFace.Manual),Is.EqualTo(new double3(500,0,-500)));
            }
            {
                //原点に近い角が (0, 0) に来る: 中心 + 物体座標の角 × 倍率 = 0(Standard と VG は倍率 N、手動分割の 1 枚は 1)
                MoonTripleLayout layout=MoonTripleLayout.Default;
                foreach(MoonTripleFace face in new[]{MoonTripleFace.Vg,MoonTripleFace.Standard100Km,MoonTripleFace.Standard1000Km}){
                    double scale=layout.FaceSize(face)/layout.BaseMeters;
                    double3 corner=layout.FaceCenter(face)+layout.CornerInObject(face)*scale;
                    Assert.That(corner.x,Is.EqualTo(0.0).Within(1e-6),face.ToString());
                    Assert.That(corner.z,Is.EqualTo(0.0).Within(1e-6),face.ToString());
                }
                double3 tile=layout.TileCenter(0,0)+layout.CornerInObject(MoonTripleFace.Manual);
                Assert.That(tile.x,Is.EqualTo(0.0).Within(1e-9),"手動分割の 1 枚(0,0)の角");
                Assert.That(tile.z,Is.EqualTo(0.0).Within(1e-9),"手動分割の 1 枚(0,0)の角");
            }
        }

        [Test]
        public void TileCenter(){
            {
                //手動分割は 100×100 枚、一辺 10 m。(0,0) は原点に最も近い枚、(99,99) は最も遠い角(1000, −1000)の枚
                MoonTripleLayout layout=MoonTripleLayout.Default;
                Assert.That(layout.TileCenter(0,0),Is.EqualTo(new double3(5,0,-5)));
                Assert.That(layout.TileCenter(99,99),Is.EqualTo(new double3(995,0,-995)));
                Assert.That(layout.TilesPerSide,Is.EqualTo(100));
            }
        }
    }
}
