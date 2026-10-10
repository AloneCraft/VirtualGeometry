using NUnit.Framework;
using UnityEngine;

namespace VirtualGeometry.World.Tests{
    public sealed class CubeSphereTests{
        [Test]
        public void FaceAxes(){
            {
                //外向き法線は cross(v,u)(格子の三角形が外から時計回りに見える向き)
                for(int face=0;face<CubeSphere.FaceCount;face++){
                    Vector3 cross=Vector3.Cross(CubeSphere.V(face),CubeSphere.U(face));
                    Assert.That(cross,Is.EqualTo((Vector3)CubeSphere.Normal(face)),"face "+face);
                }
            }
            {
                //6つの法線はすべて異なり、各面の法線から面を引ける
                for(int face=0;face<CubeSphere.FaceCount;face++){
                    Assert.That(CubeSphere.FaceWithNormal(CubeSphere.Normal(face)),Is.EqualTo(face));
                }
            }
        }

        [Test]
        public void Direction(){
            {
                //面の中心は法線の方向
                for(int face=0;face<CubeSphere.FaceCount;face++){
                    Vector3 center=CubeSphere.Direction(face,0.5f,0.5f);
                    Assert.That(Vector3.Distance(center,(Vector3)CubeSphere.Normal(face)),Is.LessThan(1e-6f));
                }
            }
            {
                //u の方向へ進むほど、面の中心からの角度が単調に増え、間隔はほぼ均等(等角写像)
                float previous=-1;
                for(int i=0;i<=10;i++){
                    float angle=Vector3.Angle((Vector3)CubeSphere.Normal(2),CubeSphere.Direction(2,0.5f+0.05f*i,0.5f));
                    Assert.That(angle,Is.GreaterThan(previous-0.05f));
                    // Vector3.Angle は acos を float で求めるので、0° の近くで約0.02°の誤差がある。
                    Assert.That(angle,Is.EqualTo(4.5f*i).Within(0.05f));
                    previous=angle;
                }
            }
        }
    }
}
