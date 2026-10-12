using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace VirtualGeometry.VirtualObjects.Tests{
    public sealed class MeshClusterPartitionTests{
        private readonly List<Object> owned=new List<Object>();

        [SetUp]
        public void SetUp(){
            MeshClusterPartition.ClearCache();
        }

        [TearDown]
        public void TearDown(){
            for(int i=0;i<this.owned.Count;i++){
                if(this.owned[i]!=null){Object.DestroyImmediate(this.owned[i]);}
            }
            this.owned.Clear();
            MeshClusterPartition.ClearCache();
        }

        // n x n の格子を、波打たせたメッシュ(重心が重ならず、軸の選び方が偏らない)。
        private Mesh Wave(int n){
            var vertices=new List<Vector3>();
            for(int z=0;z<=n;z++){
                for(int x=0;x<=n;x++){vertices.Add(new Vector3(x,Mathf.Sin(x*1.3f)*Mathf.Cos(z*0.7f)*2f,z*1.5f));}
            }
            var triangles=new List<int>();
            for(int z=0;z<n;z++){
                for(int x=0;x<n;x++){
                    int a=z*(n+1)+x;int b=a+1;int c=a+n+1;int d=c+1;
                    triangles.Add(a);triangles.Add(c);triangles.Add(b);
                    triangles.Add(b);triangles.Add(c);triangles.Add(d);
                }
            }
            var mesh=new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles,0);
            this.owned.Add(mesh);
            return mesh;
        }

        private static VirtualObjectNode NewNode(VirtualObject definition){
            Omni.Logical.Vector3 origin=default;
            return new VirtualObjectNode(definition,null,-1,in origin,Quaternion.identity,1f,0u);
        }

        [Test]
        public void GetPartCount(){
            {
                // 三角形 72 個を 16 以下に 2 分割し続けると 8 部分(72 → 36 → 18 → 9)
                var partition=new MeshClusterPartition{Mesh=this.Wave(6),TrianglesPerPart=16};
                Assert.That(partition.GetPartCount(NewNode(null)),Is.EqualTo(8));
            }
            {
                // 三角形の数以上の大きさなら 1 部分
                var partition=new MeshClusterPartition{Mesh=this.Wave(6),TrianglesPerPart=72};
                Assert.That(partition.GetPartCount(NewNode(null)),Is.EqualTo(1));
            }
            {
                // メッシュが無ければ 0 部分
                var partition=new MeshClusterPartition{TrianglesPerPart=16};
                Assert.That(partition.GetPartCount(NewNode(null)),Is.EqualTo(0));
            }
            {
                // Mesh を指定しなければ、ノードのプレハブの MeshFilter の sharedMesh を使う
                var gameObject=new GameObject("MeshClusterPartitionTests Prefab");
                this.owned.Add(gameObject);
                gameObject.AddComponent<MeshFilter>().sharedMesh=this.Wave(6);
                VirtualObject definition=gameObject.AddComponent<VirtualObject>();
                var partition=new MeshClusterPartition{TrianglesPerPart=16};
                Assert.That(partition.GetPartCount(NewNode(definition)),Is.EqualTo(8));
            }
        }

        [Test]
        public void TryGetPart(){
            {
                // 部分の中心と半径は、その部分の三角形の頂点を全部含む
                Mesh mesh=this.Wave(6);
                var partition=new MeshClusterPartition{Mesh=mesh,TrianglesPerPart=16};
                VirtualObjectNode node=NewNode(null);
                MeshClusterPartition.Clusters clusters=MeshClusterPartition.Build(mesh,16);
                Vector3[] vertices=mesh.vertices;
                int[] indices=mesh.triangles;
                for(int p=0;p<partition.GetPartCount(node);p++){
                    Assert.That(partition.TryGetPart(node,p,out VirtualObjectPart part),Is.True);
                    Assert.That(part.ApproximateOrigin,Is.EqualTo(clusters.Center[p]));
                    Assert.That(part.Radius,Is.EqualTo(clusters.Radius[p]));
                    for(int i=0;i<clusters.Count[p];i++){
                        int t=clusters.Order[clusters.Start[p]+i];
                        for(int k=0;k<3;k++){
                            Assert.That((vertices[indices[t*3+k]]-part.ApproximateOrigin).magnitude,Is.LessThanOrEqualTo(part.Radius+1e-4f));
                        }
                    }
                }
            }
            {
                // 範囲外の番号は false
                var partition=new MeshClusterPartition{Mesh=this.Wave(6),TrianglesPerPart=16};
                VirtualObjectNode node=NewNode(null);
                Assert.That(partition.TryGetPart(node,-1,out VirtualObjectPart _),Is.False);
                Assert.That(partition.TryGetPart(node,8,out VirtualObjectPart _),Is.False);
            }
        }

        [Test]
        public void TryGetTriangles(){
            {
                // 部分の三角形は元の三角形の番号で、全部の部分を足すと全三角形が 1 回ずつ
                var partition=new MeshClusterPartition{Mesh=this.Wave(6),TrianglesPerPart=16};
                VirtualObjectNode node=NewNode(null);
                var seen=new List<int>();
                var results=new List<int>();
                for(int p=0;p<partition.GetPartCount(node);p++){
                    Assert.That(partition.TryGetTriangles(node,p,results),Is.True);
                    Assert.That(results.Count,Is.LessThanOrEqualTo(16));
                    seen.AddRange(results);
                }
                seen.Sort();
                Assert.That(seen.Count,Is.EqualTo(72));
                for(int t=0;t<72;t++){Assert.That(seen[t],Is.EqualTo(t));}
            }
            {
                // 範囲外の番号は false で、results は空になる
                var partition=new MeshClusterPartition{Mesh=this.Wave(6),TrianglesPerPart=16};
                var results=new List<int>{1,2,3};
                Assert.That(partition.TryGetTriangles(NewNode(null),99,results),Is.False);
                Assert.That(results.Count,Is.EqualTo(0));
            }
        }

        [Test]
        public void Get(){
            {
                // 同じメッシュと大きさなら cache を返し、ClearCache の後は作り直す(内容は同じ)
                var partition=new MeshClusterPartition{Mesh=this.Wave(6),TrianglesPerPart=16};
                VirtualObjectNode node=NewNode(null);
                MeshClusterPartition.Clusters first=partition.Get(node);
                Assert.That(partition.Get(node),Is.SameAs(first));
                MeshClusterPartition.ClearCache();
                MeshClusterPartition.Clusters second=partition.Get(node);
                Assert.That(second,Is.Not.SameAs(first));
                Assert.That(second.Order,Is.EqualTo(first.Order));
            }
            {
                // 分割の大きさが違えば別の結果
                Mesh mesh=this.Wave(6);
                var small=new MeshClusterPartition{Mesh=mesh,TrianglesPerPart=8};
                var large=new MeshClusterPartition{Mesh=mesh,TrianglesPerPart=32};
                VirtualObjectNode node=NewNode(null);
                Assert.That(small.GetPartCount(node),Is.GreaterThan(large.GetPartCount(node)));
            }
            {
                // Read/Write が無効なメッシュ(Play 中とビルドでは頂点を読めない)は、Edit Mode でも分けず、エラーを 1 回だけ出す
                Mesh mesh=this.Wave(6);
                mesh.UploadMeshData(true);
                var partition=new MeshClusterPartition{Mesh=mesh,TrianglesPerPart=16};
                VirtualObjectNode node=NewNode(null);
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("Read/Write が無効"));
                Assert.That(partition.Get(node),Is.Null);
                Assert.That(partition.GetPartCount(node),Is.EqualTo(0));
            }
        }

        [Test]
        public void ClearCache(){
            {
                // 同じメッシュを書き換えても、ClearCache するまでは古い分割が残り、ClearCache すると新しい分割になる
                Mesh mesh=this.Wave(6);
                var partition=new MeshClusterPartition{Mesh=mesh,TrianglesPerPart=16};
                VirtualObjectNode node=NewNode(null);
                Assert.That(partition.GetPartCount(node),Is.EqualTo(8));
                var triangles=new List<int>();
                mesh.GetTriangles(triangles,0);
                mesh.SetTriangles(triangles.GetRange(0,18*3),0);
                Assert.That(partition.GetPartCount(node),Is.EqualTo(8));
                MeshClusterPartition.ClearCache();
                // 三角形 18 個 → 9 + 9 の 2 部分
                Assert.That(partition.GetPartCount(node),Is.EqualTo(2));
            }
        }

        [Test]
        public void Build(){
            {
                // どの分割の大きさでも、全部の三角形がちょうど 1 回 Order に現れ、部分の大きさが上限以下
                Mesh mesh=this.Wave(7);
                int triangles=mesh.triangles.Length/3;
                int[] sizes={1,5,16,64,triangles,triangles+10};
                for(int s=0;s<sizes.Length;s++){
                    MeshClusterPartition.Clusters clusters=MeshClusterPartition.Build(mesh,sizes[s]);
                    Assert.That(clusters.Order.Length,Is.EqualTo(triangles),"size "+sizes[s]);
                    var counts=new int[triangles];
                    for(int i=0;i<clusters.Order.Length;i++){counts[clusters.Order[i]]++;}
                    for(int t=0;t<triangles;t++){Assert.That(counts[t],Is.EqualTo(1),"size "+sizes[s]+" triangle "+t);}
                    int total=0;
                    for(int p=0;p<clusters.Start.Length;p++){
                        Assert.That(clusters.Count[p],Is.GreaterThan(0),"size "+sizes[s]);
                        Assert.That(clusters.Count[p],Is.LessThanOrEqualTo(sizes[s]),"size "+sizes[s]);
                        Assert.That(clusters.Start[p],Is.EqualTo(total),"size "+sizes[s]);
                        total+=clusters.Count[p];
                    }
                    Assert.That(total,Is.EqualTo(triangles));
                }
            }
            {
                // 2 回作っても同じ結果(決定的)
                Mesh mesh=this.Wave(7);
                MeshClusterPartition.Clusters first=MeshClusterPartition.Build(mesh,16);
                MeshClusterPartition.Clusters second=MeshClusterPartition.Build(mesh,16);
                Assert.That(second.Order,Is.EqualTo(first.Order));
                Assert.That(second.Start,Is.EqualTo(first.Start));
                Assert.That(second.Count,Is.EqualTo(first.Count));
                Assert.That(second.Center,Is.EqualTo(first.Center));
                Assert.That(second.Radius,Is.EqualTo(first.Radius));
            }
            {
                // 三角形が 1 つなら 1 部分で、法線はその面の向き
                var mesh=new Mesh();
                this.owned.Add(mesh);
                mesh.SetVertices(new[]{new Vector3(0f,0f,0f),new Vector3(0f,0f,1f),new Vector3(1f,0f,0f)});
                mesh.SetTriangles(new[]{0,1,2},0);
                MeshClusterPartition.Clusters clusters=MeshClusterPartition.Build(mesh,16);
                Assert.That(clusters.Start.Length,Is.EqualTo(1));
                Assert.That(clusters.Count[0],Is.EqualTo(1));
                Assert.That(clusters.Normal[0].y,Is.EqualTo(1f).Within(1e-5f));
            }
        }
    }
}
