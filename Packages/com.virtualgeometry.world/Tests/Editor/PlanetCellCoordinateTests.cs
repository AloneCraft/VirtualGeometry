using NUnit.Framework;

namespace VirtualGeometry.World.Tests{
    public sealed class PlanetCellCoordinateTests{
        [Test]
        public void Last(){
            {
                //64ビットを超える階層の最後の番号は、上位ビットへ続く
                PlanetCellCoordinate last=PlanetCellCoordinate.Last(70);
                Assert.That(last.Low,Is.EqualTo(ulong.MaxValue));
                Assert.That(last.High,Is.EqualTo(63u));
            }
            {
                //階層0の最後の番号は0
                Assert.That(PlanetCellCoordinate.Last(0).IsZero,Is.True);
            }
        }

        [Test]
        public void ChildAndParent(){
            {
                //64ビット目をまたいで子と親が往復する
                PlanetCellCoordinate value=new PlanetCellCoordinate(0x8000000000000001UL,5);
                PlanetCellCoordinate child=value.Child(1);
                Assert.That(child.Low,Is.EqualTo(3UL));
                Assert.That(child.High,Is.EqualTo(11u));
                Assert.That(child.Parent(),Is.EqualTo(value));
                Assert.That(child.LowestBit,Is.EqualTo(1));
            }
            {
                //96ビットを超える子は作れない
                Assert.Throws<System.OverflowException>(()=>new PlanetCellCoordinate(0,0x80000000u).Child(0));
            }
        }

        [Test]
        public void IncrementAndDecrement(){
            {
                //下位64ビットの桁上がり
                PlanetCellCoordinate value=new PlanetCellCoordinate(ulong.MaxValue,2);
                Assert.That(value.Increment(),Is.EqualTo(new PlanetCellCoordinate(0,3)));
                Assert.That(value.Increment().Decrement(),Is.EqualTo(value));
            }
            {
                //0から減らせない
                Assert.Throws<System.OverflowException>(()=>PlanetCellCoordinate.Zero.Decrement());
            }
        }

        [Test]
        public void Complement(){
            {
                //64ビット以下
                Assert.That(new PlanetCellCoordinate(1,0).Complement(3),Is.EqualTo(new PlanetCellCoordinate(6,0)));
            }
            {
                //上位ビットを含む階層で、2回取ると元に戻る
                PlanetCellCoordinate value=new PlanetCellCoordinate(12345,7);
                Assert.That(value.Complement(80).Complement(80),Is.EqualTo(value));
                Assert.That(PlanetCellCoordinate.Zero.Complement(80),Is.EqualTo(PlanetCellCoordinate.Last(80)));
            }
        }

        [Test]
        public void IsWithin(){
            {
                //範囲の端
                Assert.That(PlanetCellCoordinate.Last(70).IsWithin(70),Is.True);
                Assert.That(PlanetCellCoordinate.Last(70).Increment().IsWithin(70),Is.False);
            }
        }

        [Test]
        public void Fraction(){
            {
                //階層の中の位置を0〜1で返す
                Assert.That(new PlanetCellCoordinate(3,0).Fraction(2),Is.EqualTo(0.75f));
                Assert.That(PlanetCellCoordinate.Zero.Fraction(90),Is.EqualTo(0f));
            }
        }
    }
}
