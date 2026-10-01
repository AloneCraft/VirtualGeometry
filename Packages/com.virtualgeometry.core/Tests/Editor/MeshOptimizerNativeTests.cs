using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VirtualizedGeometry.Editor;

namespace VirtualizedGeometry.Tests{
    public sealed class MeshOptimizerNativeTests{
        [Test]
        public void Version_IsPinnedToMeshoptimizer1_1(){
            Assert.That(MeshOptimizerSimplifier.Version,Is.EqualTo(1010));
        }

        [Test]
        public void Simplify_IsDeterministicAndPreservesLockedBoundary(){
            CreateGrid(12,10,out VirtualGeometryPackedVertex[] vertices,out uint[] indices,out byte[] locks,out HashSet<ulong> boundaryEdges);

            MeshOptimizerSimplifyResult first=MeshOptimizerSimplifier.Simplify(vertices,indices,locks,indices.Length/2);
            MeshOptimizerSimplifyResult second=MeshOptimizerSimplifier.Simplify(vertices,indices,locks,indices.Length/2);

            Assert.That(first.Indices.Length,Is.LessThan(indices.Length));
            Assert.That(first.Indices.Length%3,Is.Zero);
            Assert.That(float.IsFinite(first.Error),Is.True);
            Assert.That(first.Indices,Is.EqualTo(second.Indices));
            Assert.That(first.Error,Is.EqualTo(second.Error));
            Assert.That(CollectBoundaryEdges(first.Indices),Is.EquivalentTo(boundaryEdges));
        }

        private static void CreateGrid(int width,int height,out VirtualGeometryPackedVertex[] vertices,out uint[] indices,out byte[] locks,out HashSet<ulong> boundaryEdges){
            vertices=new VirtualGeometryPackedVertex[width*height];
            locks=new byte[vertices.Length];
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    int index=y*width+x;
                    float z=Mathf.Sin(x*0.47f)*Mathf.Cos(y*0.39f)*0.15f;
                    vertices[index]=new VirtualGeometryPackedVertex(new Vector3(x,y,z),Vector3.forward,new Vector2(x/(float)(width-1),y/(float)(height-1)));
                    if(x==0||y==0||x==width-1||y==height-1){locks[index]=MeshOptimizerSimplifier.VertexLock;}
                }
            }
            List<uint> output=new List<uint>((width-1)*(height-1)*6);
            for(int y=0;y<height-1;y++){
                for(int x=0;x<width-1;x++){
                    uint a=(uint)(y*width+x);
                    uint b=a+1;
                    uint c=a+(uint)width;
                    uint d=c+1;
                    output.Add(a);output.Add(c);output.Add(b);
                    output.Add(b);output.Add(c);output.Add(d);
                }
            }
            indices=output.ToArray();
            boundaryEdges=CollectBoundaryEdges(indices);
        }

        private static HashSet<ulong> CollectBoundaryEdges(uint[] indices){
            Dictionary<ulong,int> counts=new Dictionary<ulong,int>();
            for(int i=0;i<indices.Length;i+=3){
                Increment(counts,Edge(indices[i],indices[i+1]));
                Increment(counts,Edge(indices[i+1],indices[i+2]));
                Increment(counts,Edge(indices[i+2],indices[i]));
            }
            HashSet<ulong> result=new HashSet<ulong>();
            foreach(KeyValuePair<ulong,int> pair in counts){
                if(pair.Value==1){result.Add(pair.Key);}
            }
            return result;
        }

        private static void Increment(Dictionary<ulong,int> counts,ulong edge){
            counts.TryGetValue(edge,out int count);
            counts[edge]=count+1;
        }

        private static ulong Edge(uint a,uint b){
            if(a>b){(a,b)=(b,a);}
            return ((ulong)a<<32)|b;
        }
    }
}
