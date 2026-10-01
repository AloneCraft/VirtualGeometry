using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// fallback/影/物理/選択用の低密度Meshを階層から生成し、元Mesh参照を残さない。
    /// rootから単一子の段を降りた、group束ねで1 nodeへまとまった段を使う。最粗root(1 cluster以下)では影やcolliderが粗すぎるため。
    /// </summary>
    public static class VirtualGeometryProxyBuilder{
        public static Mesh Create(VirtualGeometryBuildOutput source,out int triangleCount,out float error){
            var positions=new List<Vector3>();var normals=new List<Vector3>();var uv=new List<Vector2>();var remap=new Dictionary<uint,int>();
            var submeshes=new List<int>[source.RootNodeIndices.Length];triangleCount=0;error=0;
            for(int material=0;material<submeshes.Length;material++){
                submeshes[material]=new List<int>();uint root=source.RootNodeIndices[material];
                if(root==uint.MaxValue){continue;}
                if(source.Nodes.Length==0){
                    foreach(var cluster in source.Clusters){if(cluster.MaterialSlot==material){Append(cluster,source,remap,positions,normals,uv,submeshes[material]);}}
                }else{
                    var node=source.Nodes[ProxyNode(source,root)];error=Mathf.Max(error,node.GeometricError);
                    for(uint c=0;c<node.ClusterCount;c++){Append(source.Clusters[node.FirstCluster+c],source,remap,positions,normals,uv,submeshes[material]);}
                }
                triangleCount+=submeshes[material].Count/3;
            }
            // hierarchyなしの旧形式ではroot tableがinvalidなので、全clusterが通常fallbackになる。
            if(source.Nodes.Length==0){
                for(int material=0;material<submeshes.Length;material++){
                    foreach(var cluster in source.Clusters){if(cluster.MaterialSlot==material){Append(cluster,source,remap,positions,normals,uv,submeshes[material]);}}
                }
                triangleCount=source.Indices.Length/3;
            }
            var mesh=new Mesh{name="Virtual Geometry Root Proxy",indexFormat=positions.Count>65535?IndexFormat.UInt32:IndexFormat.UInt16};
            mesh.SetVertices(positions);mesh.SetNormals(normals);mesh.SetUVs(0,uv);mesh.subMeshCount=submeshes.Length;
            for(int material=0;material<submeshes.Length;material++){mesh.SetTriangles(submeshes[material],material,false);}
            mesh.RecalculateBounds();mesh.RecalculateTangents();return mesh;
        }
        private static uint ProxyNode(VirtualGeometryBuildOutput source,uint root){
            return VirtualGeometryHierarchy.ProxyNode(source.Nodes,root);
        }
        private static void Append(VirtualGeometryCluster cluster,VirtualGeometryBuildOutput source,Dictionary<uint,int> remap,
            List<Vector3> positions,List<Vector3> normals,List<Vector2> uv,List<int> indices){
            for(uint i=0;i<cluster.IndexCount;i++){
                uint vertex=source.Indices[cluster.FirstIndex+i];
                if(!remap.TryGetValue(vertex,out int local)){
                    var input=source.Vertices[vertex];local=positions.Count;remap.Add(vertex,local);positions.Add(input.Position);normals.Add(input.Normal);uv.Add(input.UV);
                }
                indices.Add(local);
            }
        }
    }
}