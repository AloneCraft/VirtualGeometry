using NUnit.Framework;
using UnityEngine;

namespace VirtualizedGeometry.Tests{
    public sealed class VirtualGeometryCullingTests{
        [Test]
        public void FrustumSphere_KeepsIntersectionsAndRejectsOnlyFullyOutside(){
            Plane[] planes=CreateUnitBoxPlanes();
            Assert.That(VirtualGeometryCulling.IsSphereInsideFrustum(Vector3.zero,0.1f,planes),Is.True);
            Assert.That(VirtualGeometryCulling.IsSphereInsideFrustum(new Vector3(1.5f,0,0),0.5f,planes),Is.True);
            Assert.That(VirtualGeometryCulling.IsSphereInsideFrustum(new Vector3(1.51f,0,0),0.5f,planes),Is.False);
            Assert.That(VirtualGeometryCulling.IsSphereInsideFrustum(new Vector3(0,0,-1),0,planes),Is.True);
        }

        [Test]
        public void TransformSphere_UsesMaximumAbsoluteScaleForNegativeNonUniformTransforms(){
            Matrix4x4 matrix=Matrix4x4.TRS(new Vector3(4,5,6),Quaternion.Euler(10,20,30),new Vector3(-2,3,0.5f));
            VirtualGeometryWorldSphere sphere=VirtualGeometryCulling.TransformSphere(new Vector3(1,2,3),2,matrix);
            Assert.That(sphere.Center,Is.EqualTo(matrix.MultiplyPoint3x4(new Vector3(1,2,3))));
            Assert.That(sphere.Radius,Is.EqualTo(6).Within(1e-5f));
        }

        [Test]
        public void HzbReductionAndComparison_AreConservativeForNormalAndReversedZ(){
            Assert.That(VirtualGeometryCulling.ReduceDepth(0.1f,0.4f,0.3f,1,false),Is.EqualTo(1));
            Assert.That(VirtualGeometryCulling.ReduceDepth(0.9f,0.6f,0.7f,0,true),Is.EqualTo(0));
            Assert.That(VirtualGeometryCulling.IsOccludedDepth(0.8f,0.4f,false,0.001f),Is.True);
            Assert.That(VirtualGeometryCulling.IsOccludedDepth(0.2f,0.6f,true,0.001f),Is.True);
            Assert.That(VirtualGeometryCulling.IsOccludedDepth(0.8f,1,false,0.001f),Is.False);
            Assert.That(VirtualGeometryCulling.IsOccludedDepth(0.2f,0,true,0.001f),Is.False);
            Assert.That(VirtualGeometryCulling.IsOccludedDepth(0.4005f,0.4f,false,0.001f),Is.False);
            Assert.That(VirtualGeometryCulling.IsOccludedDepth(0.5995f,0.6f,true,0.001f),Is.False);
        }

        private static Plane[] CreateUnitBoxPlanes(){
            return new[]{
                new Plane(Vector3.right,new Vector3(-1,0,0)),
                new Plane(Vector3.left,new Vector3(1,0,0)),
                new Plane(Vector3.up,new Vector3(0,-1,0)),
                new Plane(Vector3.down,new Vector3(0,1,0)),
                new Plane(Vector3.forward,new Vector3(0,0,-1)),
                new Plane(Vector3.back,new Vector3(0,0,1))
            };
        }
    }
}
