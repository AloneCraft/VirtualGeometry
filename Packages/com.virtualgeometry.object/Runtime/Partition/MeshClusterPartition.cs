using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 粗いメッシュを、三角形のまとまり(cluster)に分ける。番号は cluster の番号で、子はその部分の詳細(番号のパラメータを持つ)。
    /// 分け方は三角形の重心の中央値で長い軸を 2 分割し続け、1 つの部分が TrianglesPerPart 以下になるまで(決定的)。
    /// メッシュは Mesh を指定するか、ノードのプレハブの MeshFilter の sharedMesh を使う。結果はメッシュと分割の大きさごとに cache する。
    /// </summary>
    [Serializable]
    public sealed class MeshClusterPartition:IVirtualObjectPartition{
        public Mesh Mesh;
        [Min(1)] public int TrianglesPerPart=64;

        /// <summary>分けた結果。Order は三角形の番号を部分ごとに並べたもの、Start と Count は部分ごとの Order の範囲。</summary>
        public sealed class Clusters{
            public int[] Order;
            public int[] Start;
            public int[] Count;
            public Vector3[] Center;
            public float[] Radius;
            public Vector3[] Normal;
        }

        private static readonly Dictionary<(EntityId mesh,int size),Clusters> cache=new Dictionary<(EntityId mesh,int size),Clusters>();

        public int GetPartCount(VirtualObjectNode node){
            Clusters clusters=this.Get(node);
            if(clusters==null){return 0;}
            return clusters.Start.Length;
        }

        public bool TryGetPart(VirtualObjectNode node,int number,out VirtualObjectPart part){
            part=default;
            Clusters clusters=this.Get(node);
            if(clusters==null||number<0||number>=clusters.Start.Length){return false;}
            Vector3 center=clusters.Center[number];
            if(!LogicalVector3.TryFromFloat3(center,out LogicalVector3 origin)){return false;}
            part=new VirtualObjectPart(in origin,center,clusters.Radius[number],clusters.Normal[number]);
            return true;
        }

        public object CreateChildState(VirtualObjectNode node,int number){
            return null;
        }

        public bool IsPartVisible(VirtualObjectNode node,int number,Vector3 viewerLocal){
            return true;
        }

        /// <summary>部分の三角形(元のメッシュの三角形の番号)を results に入れる(詳細の表現が、親の部分の形を使うため)。</summary>
        public bool TryGetTriangles(VirtualObjectNode node,int number,List<int> results){
            results.Clear();
            Clusters clusters=this.Get(node);
            if(clusters==null||number<0||number>=clusters.Start.Length){return false;}
            for(int i=0;i<clusters.Count[number];i++){results.Add(clusters.Order[clusters.Start[number]+i]);}
            return true;
        }

        /// <summary>このノードで使うメッシュ(Mesh、無ければプレハブの MeshFilter)。</summary>
        public Mesh ResolveMesh(VirtualObjectNode node){
            if(this.Mesh!=null){return this.Mesh;}
            if(node==null||node.Definition==null){return null;}
            MeshFilter filter=node.Definition.GetComponent<MeshFilter>();
            if(filter==null){return null;}
            return filter.sharedMesh;
        }

        public Clusters Get(VirtualObjectNode node){
            Mesh mesh=this.ResolveMesh(node);
            if(mesh==null){return null;}
            int size=Mathf.Max(1,this.TrianglesPerPart);
            var key=(mesh.GetEntityId(),size);
            if(cache.TryGetValue(key,out Clusters clusters)){return clusters;}
            clusters=Build(mesh,size);
            cache[key]=clusters;
            return clusters;
        }

        /// <summary>cache を捨てる(メッシュを変えたとき、Editor から呼ぶ)。</summary>
        public static void ClearCache(){
            cache.Clear();
        }

        public static Clusters Build(Mesh mesh,int trianglesPerPart){
            Vector3[] vertices=mesh.vertices;
            int[] indices=mesh.triangles;
            int triangles=indices.Length/3;
            var centroids=new Vector3[triangles];
            var order=new int[triangles];
            for(int t=0;t<triangles;t++){
                centroids[t]=(vertices[indices[t*3]]+vertices[indices[t*3+1]]+vertices[indices[t*3+2]])/3f;
                order[t]=t;
            }
            var ranges=new List<int2>();
            Split(centroids,order,0,triangles,trianglesPerPart,ranges);
            var result=new Clusters{
                Order=order,Start=new int[ranges.Count],Count=new int[ranges.Count],
                Center=new Vector3[ranges.Count],Radius=new float[ranges.Count],Normal=new Vector3[ranges.Count],
            };
            for(int p=0;p<ranges.Count;p++){
                int start=ranges[p].x;int count=ranges[p].y;
                result.Start[p]=start;result.Count[p]=count;
                Vector3 low=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue);
                Vector3 high=new Vector3(float.MinValue,float.MinValue,float.MinValue);
                Vector3 normal=Vector3.zero;
                for(int i=start;i<start+count;i++){
                    int t=order[i];
                    Vector3 a=vertices[indices[t*3]];Vector3 b=vertices[indices[t*3+1]];Vector3 c=vertices[indices[t*3+2]];
                    low=Vector3.Min(low,Vector3.Min(a,Vector3.Min(b,c)));high=Vector3.Max(high,Vector3.Max(a,Vector3.Max(b,c)));
                    normal+=Vector3.Cross(b-a,c-a);
                }
                Vector3 center=(low+high)*0.5f;
                float radius=0f;
                for(int i=start;i<start+count;i++){
                    int t=order[i];
                    for(int k=0;k<3;k++){radius=Mathf.Max(radius,(vertices[indices[t*3+k]]-center).magnitude);}
                }
                result.Center[p]=center;result.Radius[p]=radius;
                if(normal.sqrMagnitude>0f){result.Normal[p]=normal.normalized;}
            }
            return result;
        }

        // [start, start+count) を、重心の範囲が最も長い軸の中央値で 2 分割し続ける。
        private static void Split(Vector3[] centroids,int[] order,int start,int count,int limit,List<int2> ranges){
            if(count<=limit){
                if(count>0){ranges.Add(new int2(start,count));}
                return;
            }
            Vector3 low=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue);
            Vector3 high=new Vector3(float.MinValue,float.MinValue,float.MinValue);
            for(int i=start;i<start+count;i++){
                low=Vector3.Min(low,centroids[order[i]]);high=Vector3.Max(high,centroids[order[i]]);
            }
            Vector3 extent=high-low;
            int axis=0;
            if(extent.y>extent.x&&extent.y>=extent.z){axis=1;}
            if(extent.z>extent.x&&extent.z>extent.y){axis=2;}
            Array.Sort(order,start,count,new AxisComparer(centroids,axis));
            int half=count/2;
            Split(centroids,order,start,half,limit,ranges);
            Split(centroids,order,start+half,count-half,limit,ranges);
        }

        private sealed class AxisComparer:IComparer<int>{
            private readonly Vector3[] centroids;
            private readonly int axis;

            public AxisComparer(Vector3[] centroids,int axis){
                this.centroids=centroids;this.axis=axis;
            }

            public int Compare(int a,int b){
                int result=this.centroids[a][this.axis].CompareTo(this.centroids[b][this.axis]);
                if(result!=0){return result;}
                return a.CompareTo(b);
            }
        }
    }
}
