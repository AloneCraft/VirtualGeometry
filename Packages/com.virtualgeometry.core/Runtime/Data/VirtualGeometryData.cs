using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VirtualizedGeometry{
    /// <summary>C#とHLSLで共有するM1頂点。UVシーム等の元頂点IDを統合しない。</summary>
    [Serializable,StructLayout(LayoutKind.Sequential,Pack=4)]
    public struct VirtualGeometryPackedVertex{
        public const int Stride=32;
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;

        public VirtualGeometryPackedVertex(Vector3 position,Vector3 normal,Vector2 uv){
            this.Position=position;
            this.Normal=normal;
            this.UV=uv;
        }
    }

    /// <summary>1クラスタの描画範囲。階層の置換単位はVirtualGeometryHierarchyNodeが保持する。</summary>
    [Serializable,StructLayout(LayoutKind.Sequential,Pack=4)]
    public struct VirtualGeometryCluster{
        public const int Stride=48;
        public Vector3 BoundsCenter;
        public float BoundsRadius;
        public uint FirstIndex;
        public uint IndexCount;
        public uint MaterialSlot;
        public uint PageId;
        public float GeometricError;
        public int Parent;
        public uint ChildStart;
        public uint ChildCount;
    }

    [Serializable,StructLayout(LayoutKind.Sequential,Pack=4)]
    public struct VirtualGeometryMaterialRange{
        public const int Stride=16;
        public uint OutputStart;
        public uint OutputCapacity;
        public uint RootNode;
        public uint Reserved;
    }
    /// <summary>同じleaf領域を粗表現または全子表現のどちらかで置換する階層ノード。</summary>
    [Serializable,StructLayout(LayoutKind.Sequential,Pack=4)]
    public struct VirtualGeometryHierarchyNode{
        public const int Stride=64;
        public Vector3 BoundsCenter;
        public float BoundsRadius;
        public float GeometricError;
        public uint FirstCluster;
        public uint ClusterCount;
        public uint FirstChild;
        public uint ChildCount;
        public uint PageId;
        public uint MaterialSlot;
        public uint Level;
        public uint LeafStart;
        public uint LeafCount;
        public uint Reserved0;
        public uint Reserved1;
    }

    /// <summary>階層から導く共通規則。変換時のProxy生成と、streamingの常駐page決定で同じ段を使う。</summary>
    public static class VirtualGeometryHierarchy{
        /// <summary>rootから単一子の段を降り、group束ねで1 nodeへまとまった段(低密度Proxyと同じ段)のnode。</summary>
        public static uint ProxyNode(VirtualGeometryHierarchyNode[] nodes,uint root){
            uint node=root;
            while(nodes[node].ChildCount==1){node=nodes[node].FirstChild;}
            return node;
        }
    }

    /// <summary>検証済みページのCPU表現。ランタイムは必要ページだけをこの単位で読み込む。</summary>
    public sealed class VirtualGeometryPage{
        public VirtualGeometryPackedVertex[] Vertices{get;}
        public uint[] Indices{get;}
        public VirtualGeometryCluster[] Clusters{get;}
        public VirtualGeometryHierarchyNode[] Nodes{get;}
        public uint[] RootNodeIndices{get;}
        public int MaterialCount{get;}
        public Bounds Bounds{get;}

        public VirtualGeometryPage(VirtualGeometryPackedVertex[] vertices,uint[] indices,VirtualGeometryCluster[] clusters,int materialCount,Bounds bounds){
            this.Vertices=vertices??throw new ArgumentNullException(nameof(vertices));
            this.Indices=indices??throw new ArgumentNullException(nameof(indices));
            this.Clusters=clusters??throw new ArgumentNullException(nameof(clusters));
            this.Nodes=Array.Empty<VirtualGeometryHierarchyNode>();
            this.RootNodeIndices=CreateEmptyRoots(materialCount);
            this.MaterialCount=materialCount;
            this.Bounds=bounds;
        }

        public VirtualGeometryPage(VirtualGeometryPackedVertex[] vertices,uint[] indices,VirtualGeometryCluster[] clusters,VirtualGeometryHierarchyNode[] nodes,uint[] rootNodeIndices,int materialCount,Bounds bounds){
            this.Vertices=vertices??throw new ArgumentNullException(nameof(vertices));
            this.Indices=indices??throw new ArgumentNullException(nameof(indices));
            this.Clusters=clusters??throw new ArgumentNullException(nameof(clusters));
            this.Nodes=nodes??throw new ArgumentNullException(nameof(nodes));
            this.RootNodeIndices=rootNodeIndices??throw new ArgumentNullException(nameof(rootNodeIndices));
            this.MaterialCount=materialCount;
            this.Bounds=bounds;
        }

        private static uint[] CreateEmptyRoots(int materialCount){
            if(materialCount<0){throw new ArgumentOutOfRangeException(nameof(materialCount));}
            uint[] roots=new uint[materialCount];
            for(int i=0;i<roots.Length;i++){roots[i]=uint.MaxValue;}
            return roots;
        }
    }
}
