using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace VirtualGeometry.VirtualObjects.Tests{
    public sealed class PartMeshRepresentationTests{
        [Test]
        public void BuildMesh(){
            {
                // 選んだ三角形だけを、原点を引いて shift を足した位置で、三角形ごとの頂点にして作る(法線と UV も写す)
                var source=new Mesh();
                source.vertices=new[]{new Vector3(0f,0f,0f),new Vector3(1f,0f,0f),new Vector3(0f,0f,1f),new Vector3(1f,0f,1f)};
                source.normals=new[]{Vector3.up,Vector3.up,Vector3.up,Vector3.up};
                source.uv=new[]{new Vector2(0f,0f),new Vector2(1f,0f),new Vector2(0f,1f),new Vector2(1f,1f)};
                source.triangles=new[]{0,2,1,1,2,3};
                Mesh result=PartMeshRepresentation.BuildMesh(source,new List<int>{1},new Vector3(1f,0f,1f),new Vector3(0f,0.5f,0f));
                try{
                    Assert.That(result.vertexCount,Is.EqualTo(3));
                    Assert.That(result.triangles,Is.EqualTo(new[]{0,1,2}));
                    Vector3[] vertices=result.vertices;
                    Assert.That(vertices[0],Is.EqualTo(new Vector3(0f,0.5f,-1f)));
                    Assert.That(vertices[1],Is.EqualTo(new Vector3(-1f,0.5f,0f)));
                    Assert.That(vertices[2],Is.EqualTo(new Vector3(0f,0.5f,0f)));
                    Assert.That(result.normals[0],Is.EqualTo(Vector3.up));
                    Assert.That(result.uv[2],Is.EqualTo(new Vector2(1f,1f)));
                }finally{
                    Object.DestroyImmediate(result);
                    Object.DestroyImmediate(source);
                }
            }
            {
                // 全部の部分の三角形を足すと、元の三角形の数になる(部分が親を覆う)
                var source=new Mesh();
                var vertices=new List<Vector3>();
                var triangles=new List<int>();
                for(int z=0;z<=8;z++){
                    for(int x=0;x<=8;x++){vertices.Add(new Vector3(x,0f,z));}
                }
                for(int z=0;z<8;z++){
                    for(int x=0;x<8;x++){
                        int a=z*9+x;int b=a+1;int c=a+9;int d=c+1;
                        triangles.Add(a);triangles.Add(c);triangles.Add(b);triangles.Add(b);triangles.Add(c);triangles.Add(d);
                    }
                }
                source.SetVertices(vertices);source.SetTriangles(triangles,0);
                MeshClusterPartition.Clusters clusters=MeshClusterPartition.Build(source,20);
                int total=0;
                for(int p=0;p<clusters.Start.Length;p++){
                    var part=new List<int>();
                    for(int i=0;i<clusters.Count[p];i++){part.Add(clusters.Order[clusters.Start[p]+i]);}
                    Mesh mesh=PartMeshRepresentation.BuildMesh(source,part,clusters.Center[p],Vector3.zero);
                    total+=mesh.triangles.Length/3;
                    Object.DestroyImmediate(mesh);
                }
                Assert.That(total,Is.EqualTo(triangles.Count/3));
                Object.DestroyImmediate(source);
            }
        }
    }
}
