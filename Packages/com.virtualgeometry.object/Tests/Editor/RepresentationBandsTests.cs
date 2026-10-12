using NUnit.Framework;

namespace VirtualGeometry.VirtualObjects.Tests{
    public sealed class RepresentationBandsTests{
        private static RepresentationBand[] Building(){
            return new[]{
                new RepresentationBand("none",0f,0f),
                new RepresentationBand("billboard",2f,1.5f),
                new RepresentationBand("vgmesh",40f,32f),
                new RepresentationBand("prefab",300f,250f),
            };
        }

        [Test]
        public void Select(){
            RepresentationBand[] bands=Building();
            {
                // 入口の px 以上で細かい帯へ進む
                Assert.That(RepresentationBands.Select(bands,1f,-1),Is.EqualTo(0));
                Assert.That(RepresentationBands.Select(bands,2f,-1),Is.EqualTo(1));
                Assert.That(RepresentationBands.Select(bands,40f,1),Is.EqualTo(2));
                Assert.That(RepresentationBands.Select(bands,1e6f,0),Is.EqualTo(3));
            }
            {
                // 粗い方へは、今の帯の出口の px を下回るまで留まる(ヒステリシス)
                Assert.That(RepresentationBands.Select(bands,35f,2),Is.EqualTo(2));
                Assert.That(RepresentationBands.Select(bands,31f,2),Is.EqualTo(1));
                Assert.That(RepresentationBands.Select(bands,260f,3),Is.EqualTo(3));
                Assert.That(RepresentationBands.Select(bands,1f,3),Is.EqualTo(0));
            }
        }

        [Test]
        public void Validate(){
            {
                // 正しい並び
                Assert.That(RepresentationBands.Validate(Building()),Is.Null);
            }
            {
                // 入口の px が単調に増えない
                RepresentationBand[] bands=Building();
                bands[2].EnterPixels=1f;
                Assert.That(RepresentationBands.Validate(bands),Is.Not.Null);
            }
            {
                // 出口が入口より大きい
                RepresentationBand[] bands=Building();
                bands[2].ExitPixels=50f;
                Assert.That(RepresentationBands.Validate(bands),Is.Not.Null);
            }
            {
                // 最初の帯の入口が 0 でない
                RepresentationBand[] bands=Building();
                bands[0].EnterPixels=1f;
                Assert.That(RepresentationBands.Validate(bands),Is.Not.Null);
            }
        }
    }
}
