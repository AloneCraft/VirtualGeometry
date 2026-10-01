using NUnit.Framework;
using UnityEngine;

namespace VirtualizedGeometry.Samples.SpaceElevator.Editor.Tests{
    /// <summary>
    /// space_elevator.fbx の座標規約(Unity Mesh 座標 → Blender のオブジェクト座標)を、Blender 側の既知の寸法で確かめる。
    /// 手続きシェーダーの模様(パネル目地・雪線など)は Blender の m 単位で計算するため、ここがずれると模様の大きさや向きが変わる。
    /// 既知の寸法は FBX の頂点(Blender のオブジェクト座標)から求めた値。
    /// </summary>
    public sealed class SpaceElevatorPatternSpaceTests{
        [Test]
        public void MeshToBlender(){
            {
                //X は反転し、Unity Mesh の1単位は Blender の 100 m
                Vector3 blender=SpaceElevatorPatternSpace.MeshToBlender(new Vector3(0.01f,0.02f,0.03f));
                Assert.That(Vector3.Distance(blender,new Vector3(-1f,2f,3f)),Is.LessThan(1e-5f));
            }
        }

        [Test]
        public void BlenderToWorld(){
            {
                //Blender の Z 上は Unity の Y 上、Blender の +Y(奥)は Unity の -Z
                Vector3 world=SpaceElevatorPatternSpace.BlenderToWorld(new Vector3(1f,2f,3f));
                Assert.That(Vector3.Distance(world,new Vector3(-1f,3f,-2f)),Is.LessThan(1e-6f));
            }
        }

        [Test]
        public void SourceMesh(){
            {
                //VG 変換前の元 Mesh がサブアセットとして残っている
                Assert.That(SpaceElevatorPatternSpace.SourceMesh("SE_GA_AnchorTower"),Is.Not.Null);
            }
            {
                //無い名前は null
                Assert.That(SpaceElevatorPatternSpace.SourceMesh("SE_GA_DoesNotExist"),Is.Null);
            }
        }

        [Test]
        public void BlenderBounds(){
            {
                //アンカータワー: y は -84.0〜85.85 m の非対称、高さ 15〜300 m(倍率と Y・Z の向き)
                Bounds tower=SpaceElevatorPatternSpace.BlenderBounds(SpaceElevatorPatternSpace.SourceMesh("SE_GA_AnchorTower"));
                Assert.That(tower.min.y,Is.EqualTo(-84.0f).Within(0.01f));
                Assert.That(tower.max.y,Is.EqualTo(85.85f).Within(0.01f));
                Assert.That(tower.min.z,Is.EqualTo(15f).Within(0.01f));
                Assert.That(tower.max.z,Is.EqualTo(300f).Within(0.01f));
            }
            {
                //現場事務所: x は -1.50〜2.90 m の非対称(X の反転)
                Bounds office=SpaceElevatorPatternSpace.BlenderBounds(SpaceElevatorPatternSpace.SourceMesh("SE_GA_SiteOffice"));
                Assert.That(office.min.x,Is.EqualTo(-1.5f).Within(0.01f));
                Assert.That(office.max.x,Is.EqualTo(2.9f).Within(0.01f));
            }
        }
    }
}
