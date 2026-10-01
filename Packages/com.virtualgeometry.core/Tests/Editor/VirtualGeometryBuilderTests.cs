using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using VirtualizedGeometry.Editor;

namespace VirtualizedGeometry.Tests{
    /// <summary>M1変換の外部挙動とページ不変条件を検証する。</summary>
    public sealed class VirtualGeometryBuilderTests{
        [Test]
        public void Build_DeterministicClustersRespectLimitAndMaterialBoundaries(){
            VirtualGeometryMeshInput input=CreateGrid(8,4);
            VirtualGeometryBuildSettings settings=new VirtualGeometryBuildSettings{MaxTrianglesPerCluster=7};

            VirtualGeometryBuildOutput first=VirtualGeometryBuilder.Build(input,settings);
            VirtualGeometryBuildOutput second=VirtualGeometryBuilder.Build(input,settings);

            Assert.That(first.PageBytes,Is.EqualTo(second.PageBytes));
            Assert.That(first.ContentHash,Is.EqualTo(second.ContentHash));
            Assert.That(first.Clusters.Length,Is.GreaterThan(1));
            foreach(VirtualGeometryCluster cluster in first.Clusters){
                Assert.That(cluster.IndexCount%3u,Is.Zero);
                Assert.That(cluster.IndexCount/3u,Is.LessThanOrEqualTo(7u));
                Assert.That(cluster.MaterialSlot,Is.LessThan(2u));
            }
        }

        [Test]
        public void Build_InvalidValuesAreRejectedAndDegeneratesAreDiagnosed(){
            VirtualGeometryMeshInput nan=CreateGrid(1,1);
            nan.Positions[0]=new Vector3(float.NaN,0,0);
            Assert.Throws<ArgumentException>(()=>VirtualGeometryBuilder.Build(nan,new VirtualGeometryBuildSettings()));

            VirtualGeometryMeshInput degenerate=new VirtualGeometryMeshInput(
                new[]{Vector3.zero,Vector3.right,Vector3.up},
                Array.Empty<Vector3>(),Array.Empty<Vector2>(),
                new[]{new[]{0,0,1}});
            VirtualGeometryBuildOutput output=VirtualGeometryBuilder.Build(degenerate,new VirtualGeometryBuildSettings());
            Assert.That(output.Indices,Is.Empty);
            Assert.That(output.Diagnostics,Has.Some.Matches<VirtualGeometryBuildDiagnostic>(x=>x.Code=="VG1003"));
        }

        [Test]
        public void PageCodec_RoundTripsAndRejectsCorruption(){
            VirtualGeometryBuildOutput output=VirtualGeometryBuilder.Build(CreateGrid(3,2),new VirtualGeometryBuildSettings{MaxTrianglesPerCluster=4});
            VirtualGeometryPage page=VirtualGeometryPageCodec.Read(output.PageBytes);

            Assert.That(page.Vertices.Length,Is.EqualTo(output.Vertices.Length));
            Assert.That(page.Indices,Is.EqualTo(output.Indices));
            Assert.That(page.Clusters.Length,Is.EqualTo(output.Clusters.Length));
            Assert.That(VirtualGeometryPackedVertex.Stride,Is.EqualTo(32));
            Assert.That(VirtualGeometryCluster.Stride,Is.EqualTo(48));

            byte[] corrupt=(byte[])output.PageBytes.Clone();
            corrupt[^1]^=0x40;
            Assert.Throws<InvalidDataException>(()=>VirtualGeometryPageCodec.Read(corrupt));
        }

        private static VirtualGeometryMeshInput CreateGrid(int width,int height){
            Vector3[] vertices=new Vector3[(width+1)*(height+1)];
            Vector3[] normals=new Vector3[vertices.Length];
            Vector2[] uv=new Vector2[vertices.Length];
            for(int y=0;y<=height;y++){
                for(int x=0;x<=width;x++){
                    int vertex=y*(width+1)+x;
                    vertices[vertex]=new Vector3(x-width*0.5f,Mathf.Sin(x*0.7f)*0.1f,y-height*0.5f);
                    normals[vertex]=Vector3.up;
                    uv[vertex]=new Vector2((float)x/width,(float)y/height);
                }
            }
            int[][] submeshes={new int[width*height*3],new int[width*height*3]};
            int[] offsets={0,0};
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    int a=y*(width+1)+x;
                    int b=a+1;
                    int c=a+width+1;
                    int d=c+1;
                    int material=(x+y)&1;
                    int offset=offsets[material];
                    submeshes[material][offset]=a;
                    submeshes[material][offset+1]=c;
                    submeshes[material][offset+2]=b;
                    offsets[material]+=3;
                    material^=1;
                    offset=offsets[material];
                    submeshes[material][offset]=b;
                    submeshes[material][offset+1]=c;
                    submeshes[material][offset+2]=d;
                    offsets[material]+=3;
                }
            }
            return new VirtualGeometryMeshInput(vertices,normals,uv,submeshes);
        }
    }
}
