using System;
using NUnit.Framework;
using UnityEngine;

namespace VirtualGeometry.Omni.Tests{
    public sealed class ScaledPlaneMeshTests{
        [Test]
        public void Axis(){
            {
                //端は ±half ちょうど、昇順で重なりなし、focus の ±fineHalf は fineStep 刻み
                double[] axis=ScaledPlaneMesh.Axis(1000000.0,1000003.0-1000000.0,32,0.5,1.25);
                Assert.That(axis[0],Is.EqualTo(-1000000.0));
                Assert.That(axis[axis.Length-1],Is.EqualTo(1000000.0));
                for(int i=1;i<axis.Length;i++){Assert.That(axis[i],Is.GreaterThan(axis[i-1]),"i="+i);}
                // focus(=3)の前後 32m に 0.5m 刻みの点がある。
                for(int k=-64;k<=64;k++){
                    double expected=3.0+k*0.5;
                    Assert.That(Array.BinarySearch(axis,expected),Is.GreaterThanOrEqualTo(0),"focus+"+k*0.5);
                }
            }
            {
                //細かい区間が端までかかっても、端の点は落ちない。fineStep が 2 進で表せない値(0.1)でも focus+fineHalf の点がある
                double[] axis=ScaledPlaneMesh.Axis(10.0,0.0,3.0,0.1,1.5);
                Assert.That(axis[0],Is.EqualTo(-10.0));
                Assert.That(axis[axis.Length-1],Is.EqualTo(10.0));
                Assert.That(Nearest(axis,3.0),Is.LessThan(1e-9));
                Assert.That(Nearest(axis,-3.0),Is.LessThan(1e-9));
            }
            {
                //不正な入力は例外(無限ループやメモリ枯渇にしない)
                Assert.Throws<ArgumentOutOfRangeException>(()=>ScaledPlaneMesh.Axis(10.0,0.0,3.0,0.0,1.5));
                Assert.Throws<ArgumentOutOfRangeException>(()=>ScaledPlaneMesh.Axis(10.0,0.0,3.0,-0.5,1.5));
                Assert.Throws<ArgumentOutOfRangeException>(()=>ScaledPlaneMesh.Axis(10.0,0.0,3.0,0.5,1.0));
                Assert.Throws<ArgumentOutOfRangeException>(()=>ScaledPlaneMesh.Axis(0.0,0.0,3.0,0.5,1.5));
                Assert.Throws<ArgumentOutOfRangeException>(()=>ScaledPlaneMesh.Axis(10.0,0.0,-1.0,0.5,1.5));
            }
        }

        // 列の中で value に最も近い点との差。
        private static double Nearest(double[] axis,double value){
            double best=double.MaxValue;
            foreach(double item in axis){best=Math.Min(best,Math.Abs(item-value));}
            return best;
        }

        [Test]
        public void ToObjectSpace(){
            {
                //量子化で同じ float になった座標は 1 つになり、昇順を保つ。端は ±0.5 ちょうど
                double scale=2000006.0;
                double[] axis=ScaledPlaneMesh.Axis(scale*0.5,1000000.0,32,0.5,1.25);
                float[] values=ScaledPlaneMesh.ToObjectSpace(axis,scale);
                Assert.That(values[0],Is.EqualTo(-0.5f));
                Assert.That(values[values.Length-1],Is.EqualTo(0.5f));
                for(int i=1;i<values.Length;i++){Assert.That(values[i],Is.GreaterThan(values[i-1]),"i="+i);}
                // 試験点の付近(座標 ≈ 0.5)の頂点は、約 S·2^-25(6cm)の刻みに載るので、0.5m 刻みの点の数は減らない(0.5m > 6cm)が、float の刻みの整数倍になる。
                Assert.That(values.Length,Is.LessThanOrEqualTo(axis.Length));
            }
        }

        [Test]
        public void Build(){
            {
                //単位の平面: y=0 ちょうど、法線 +Y、端 ±0.5、index が範囲内、三角形は上から見て時計回り(表が +Y)
                Mesh mesh=ScaledPlaneMesh.Build(2000006.0,1000000.0,0.0,32,0.5,1.25);
                try{
                    Vector3[] vertices=mesh.vertices;
                    int[] indices=mesh.GetIndices(0);
                    Assert.That(indices.Length%3,Is.EqualTo(0));
                    float minimumX=float.MaxValue,maximumX=float.MinValue,minimumZ=float.MaxValue,maximumZ=float.MinValue;
                    foreach(Vector3 vertex in vertices){
                        Assert.That(vertex.y,Is.EqualTo(0f));
                        minimumX=Mathf.Min(minimumX,vertex.x);maximumX=Mathf.Max(maximumX,vertex.x);
                        minimumZ=Mathf.Min(minimumZ,vertex.z);maximumZ=Mathf.Max(maximumZ,vertex.z);
                    }
                    Assert.That(minimumX,Is.EqualTo(-0.5f));Assert.That(maximumX,Is.EqualTo(0.5f));
                    Assert.That(minimumZ,Is.EqualTo(-0.5f));Assert.That(maximumZ,Is.EqualTo(0.5f));
                    foreach(Vector3 normal in mesh.normals){Assert.That(normal,Is.EqualTo(Vector3.up));}
                    // 全三角形を、真上から見て(x を右、z を上)の符号付き面積で調べる: 時計回りは負。
                    for(int i=0;i<indices.Length;i+=3){
                        Assert.That(indices[i],Is.LessThan(vertices.Length));Assert.That(indices[i+1],Is.LessThan(vertices.Length));Assert.That(indices[i+2],Is.LessThan(vertices.Length));
                        Vector3 a=vertices[indices[i]],b=vertices[indices[i+1]],c=vertices[indices[i+2]];
                        double area=((double)b.x-a.x)*((double)c.z-a.z)-((double)c.x-a.x)*((double)b.z-a.z);
                        Assert.That(area,Is.LessThan(0.0),"triangle "+i/3);
                    }
                }finally{
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
        }

        [Test]
        public void BuildWall(){
            {
                //壁: x=0、y∈[0,1]、z∈±1000 を細かく分け、法線 +X、右(+X)から見て時計回り(+z が右、y が上)
                Mesh mesh=ScaledPlaneMesh.BuildWall(1.0,1000.0,0.0,16,0.5,1.25);
                try{
                    Vector3[] vertices=mesh.vertices;
                    int[] indices=mesh.GetIndices(0);
                    Assert.That(vertices.Length,Is.GreaterThan(4),"1 枚の四角形ではなく、z 方向に分ける");
                    float minimumZ=float.MaxValue,maximumZ=float.MinValue,maximumY=float.MinValue;
                    foreach(Vector3 vertex in vertices){
                        Assert.That(vertex.x,Is.EqualTo(0f));
                        Assert.That(vertex.y==0f||vertex.y==1f);
                        minimumZ=Mathf.Min(minimumZ,vertex.z);maximumZ=Mathf.Max(maximumZ,vertex.z);maximumY=Mathf.Max(maximumY,vertex.y);
                    }
                    Assert.That(minimumZ,Is.EqualTo(-1000f));Assert.That(maximumZ,Is.EqualTo(1000f));Assert.That(maximumY,Is.EqualTo(1f));
                    foreach(Vector3 normal in mesh.normals){Assert.That(normal,Is.EqualTo(Vector3.right));}
                    for(int i=0;i<indices.Length;i+=3){
                        Vector3 a=vertices[indices[i]],b=vertices[indices[i+1]],c=vertices[indices[i+2]];
                        // 右から見た画面の座標 (z, y)。時計回りは符号付き面積が負。
                        double area=((double)b.z-a.z)*((double)c.y-a.y)-((double)c.z-a.z)*((double)b.y-a.y);
                        Assert.That(area,Is.LessThan(0.0),"triangle "+i/3);
                    }
                }finally{
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
            {
                Assert.Throws<ArgumentOutOfRangeException>(()=>ScaledPlaneMesh.BuildWall(0.0,1000.0,0.0,16,0.5,1.25));
            }
        }
    }
}
