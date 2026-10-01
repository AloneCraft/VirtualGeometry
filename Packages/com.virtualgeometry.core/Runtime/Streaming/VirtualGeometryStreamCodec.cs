using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VirtualizedGeometry{
    /// <summary>独立I/O範囲とpage-local geometryのサイズ/CRCを保持するv3 descriptor。</summary>
    public struct VirtualGeometryPageDescriptor{
        public long Offset;
        public int ByteCount;
        public int VertexCount;
        public int IndexCount;
        public uint Crc;
        public bool Pinned;
    }

    /// <summary>GPU転送に使用する単一page。配列の所有者はstreaming controller。</summary>
    public sealed class VirtualGeometryPagePayload{
        public VirtualGeometryPackedVertex[] Vertices{get;}
        public uint[] Indices{get;}
        public int ByteCount=>checked(this.Vertices.Length*32+this.Indices.Length*4);
        public VirtualGeometryPagePayload(VirtualGeometryPackedVertex[] vertices,uint[] indices){this.Vertices=vertices;this.Indices=indices;}
    }

    /// <summary>geometryを常駐させず走査できるarchive metadata。</summary>
    public sealed class VirtualGeometryStreamMetadata{
        public VirtualGeometryPageDescriptor[] Pages;
        public VirtualGeometryCluster[] Clusters;
        public VirtualGeometryHierarchyNode[] Nodes;
        public uint[] Roots;
        public Bounds Bounds;
        public int MaxPageVertices;
        public int MaxPageIndices;
        public int MetadataBytes;
        public long FileBytes;
        public int SlotGeometryBytes=>checked(this.MaxPageVertices*32+this.MaxPageIndices*4);
        public long UniqueGeometryBytes{
            get{long sum=0;foreach(var page in this.Pages){sum+=page.ByteCount;}return sum;}
        }
    }

    /// <summary>v3のmetadataを先に検証し、payloadをpage単位で独立検証する。</summary>
    public static class VirtualGeometryStreamCodec{
        public const uint Version=3;
        public const int HeaderBytes=96;

        public static byte[] Write(VirtualGeometryPage source){
            if(source==null||source.Nodes.Length==0){throw new InvalidDataException("Streaming conversion requires a hierarchy.");}
            VirtualGeometryPageCodec.ValidateHierarchy(source);
            var metadata=new VirtualGeometryStreamMetadata{
                Nodes=(VirtualGeometryHierarchyNode[])source.Nodes.Clone(),Clusters=(VirtualGeometryCluster[])source.Clusters.Clone(),
                Roots=(uint[])source.RootNodeIndices.Clone(),Bounds=source.Bounds
            };
            var groups=new List<List<int>>{new List<int>()};var roots=new HashSet<int>();
            foreach(uint root in metadata.Roots){if(root!=uint.MaxValue){groups[0].Add((int)root);roots.Add((int)root);}}
            for(int node=0;node<metadata.Nodes.Length;node++){if(!roots.Contains(node)){groups.Add(new List<int>{node});}}
            metadata.Pages=new VirtualGeometryPageDescriptor[groups.Count];
            byte[][] bodies=new byte[groups.Count][];
            bool[] clusterOwned=new bool[metadata.Clusters.Length];
            for(int pageId=0;pageId<groups.Count;pageId++){
                var vertices=new List<VirtualGeometryPackedVertex>();var indices=new List<uint>();var map=new Dictionary<uint,uint>();
                foreach(int nodeId in groups[pageId]){
                    VirtualGeometryHierarchyNode node=metadata.Nodes[nodeId];node.PageId=(uint)pageId;metadata.Nodes[nodeId]=node;
                    for(uint ci=0;ci<node.ClusterCount;ci++){
                        int clusterId=checked((int)(node.FirstCluster+ci));
                        if(clusterOwned[clusterId]){throw new InvalidDataException("Each representation must own its cluster range.");}
                        clusterOwned[clusterId]=true;
                        VirtualGeometryCluster cluster=metadata.Clusters[clusterId];uint first=(uint)indices.Count;
                        for(uint i=0;i<cluster.IndexCount;i++){
                            uint sourceVertex=source.Indices[cluster.FirstIndex+i];
                            if(!map.TryGetValue(sourceVertex,out uint local)){
                                local=(uint)vertices.Count;map.Add(sourceVertex,local);vertices.Add(source.Vertices[sourceVertex]);
                            }
                            indices.Add(local);
                        }
                        cluster.FirstIndex=first;cluster.PageId=(uint)pageId;metadata.Clusters[clusterId]=cluster;
                    }
                }
                var payload=new VirtualGeometryPagePayload(vertices.ToArray(),indices.ToArray());
                bodies[pageId]=EncodePayload(payload);
                metadata.MaxPageVertices=Math.Max(metadata.MaxPageVertices,payload.Vertices.Length);
                metadata.MaxPageIndices=Math.Max(metadata.MaxPageIndices,payload.Indices.Length);
                metadata.Pages[pageId]=new VirtualGeometryPageDescriptor{
                    VertexCount=payload.Vertices.Length,IndexCount=payload.Indices.Length,ByteCount=bodies[pageId].Length,
                    Crc=VirtualGeometryPageCodec.ComputeCrc32(bodies[pageId],0,bodies[pageId].Length),Pinned=pageId==0
                };
            }
            foreach(bool owned in clusterOwned){if(!owned){throw new InvalidDataException("Unowned cluster in streaming conversion.");}}
            metadata.MetadataBytes=checked(HeaderBytes+groups.Count*32+metadata.Clusters.Length*48+metadata.Nodes.Length*64+metadata.Roots.Length*4);
            long offset=metadata.MetadataBytes;
            for(int page=0;page<metadata.Pages.Length;page++){
                var descriptor=metadata.Pages[page];descriptor.Offset=offset;metadata.Pages[page]=descriptor;offset+=descriptor.ByteCount;
            }
            metadata.FileBytes=offset;
            using(var stream=new MemoryStream())
            using(var writer=new BinaryWriter(stream)){
                writer.Write(new byte[HeaderBytes]);
                foreach(var descriptor in metadata.Pages){
                    writer.Write(descriptor.Offset);writer.Write(descriptor.ByteCount);writer.Write(descriptor.VertexCount);writer.Write(descriptor.IndexCount);
                    writer.Write(descriptor.Crc);writer.Write(descriptor.Pinned?1u:0u);writer.Write(0u);
                }
                VirtualGeometryPageCodec.WriteClusters(writer,metadata.Clusters);VirtualGeometryPageCodec.WriteNodes(writer,metadata.Nodes);
                foreach(uint root in metadata.Roots){writer.Write(root);}
                byte[] metadataBytes=stream.ToArray();uint crc=VirtualGeometryPageCodec.ComputeCrc32(metadataBytes,HeaderBytes,metadataBytes.Length-HeaderBytes);
                stream.Position=0;
                writer.Write(VirtualGeometryPageCodec.Magic);writer.Write(Version);writer.Write(HeaderBytes);writer.Write(metadata.MetadataBytes);
                writer.Write(metadata.FileBytes);writer.Write(metadata.Pages.Length);writer.Write(metadata.Clusters.Length);writer.Write(metadata.Nodes.Length);
                writer.Write(metadata.Roots.Length);writer.Write(metadata.MaxPageVertices);writer.Write(metadata.MaxPageIndices);writer.Write(crc);writer.Write(Version);
                WriteVector(writer,metadata.Bounds.min);WriteVector(writer,metadata.Bounds.max);writer.Write(new byte[16]);
                stream.Position=metadata.MetadataBytes;foreach(byte[] body in bodies){writer.Write(body);}
                return stream.ToArray();
            }
        }

        public static VirtualGeometryStreamMetadata ReadMetadata(Stream stream){
            if(stream==null||!stream.CanSeek||stream.Length<HeaderBytes){throw new InvalidDataException("Invalid v3 archive stream.");}
            using(var reader=new BinaryReader(stream,System.Text.Encoding.UTF8,true)){
                stream.Position=0;
                if(reader.ReadUInt32()!=VirtualGeometryPageCodec.Magic||reader.ReadUInt32()!=Version||reader.ReadInt32()!=HeaderBytes){throw new InvalidDataException("Unsupported v3 archive header.");}
                int metadataSize=reader.ReadInt32();long fileBytes=reader.ReadInt64();
                int pageCount=reader.ReadInt32();int clusterCount=reader.ReadInt32();int nodeCount=reader.ReadInt32();int materialCount=reader.ReadInt32();
                int maxVertices=reader.ReadInt32();int maxIndices=reader.ReadInt32();uint crc=reader.ReadUInt32();uint builder=reader.ReadUInt32();
                Vector3 min=ReadVector(reader);Vector3 max=ReadVector(reader);
                long expected=(long)HeaderBytes+(long)pageCount*32+(long)clusterCount*48+(long)nodeCount*64+(long)materialCount*4;
                if(pageCount<=0||clusterCount<=0||nodeCount<=0||materialCount<=0||maxVertices<=0||maxIndices<=0||builder>Version||
                    metadataSize!=expected||metadataSize>stream.Length||fileBytes<metadataSize||fileBytes!=stream.Length){
                    throw new InvalidDataException("v3 counts, offsets, or file size are invalid.");
                }
                stream.Position=HeaderBytes;byte[] bytes=reader.ReadBytes(metadataSize-HeaderBytes);
                if(VirtualGeometryPageCodec.ComputeCrc32(bytes,0,bytes.Length)!=crc){throw new InvalidDataException("v3 metadata CRC failed.");}
                var result=new VirtualGeometryStreamMetadata{
                    Pages=new VirtualGeometryPageDescriptor[pageCount],Clusters=new VirtualGeometryCluster[clusterCount],Nodes=new VirtualGeometryHierarchyNode[nodeCount],
                    Roots=new uint[materialCount],MaxPageVertices=maxVertices,MaxPageIndices=maxIndices,MetadataBytes=metadataSize,FileBytes=fileBytes
                };
                if(!Finite(min)||!Finite(max)||min.x>max.x||min.y>max.y||min.z>max.z){throw new InvalidDataException("v3 bounds are invalid.");}
                result.Bounds.SetMinMax(min,max);stream.Position=HeaderBytes;long next=metadataSize;int actualMaxVertices=0;int actualMaxIndices=0;
                for(int page=0;page<pageCount;page++){
                    var descriptor=new VirtualGeometryPageDescriptor{Offset=reader.ReadInt64(),ByteCount=reader.ReadInt32(),VertexCount=reader.ReadInt32(),IndexCount=reader.ReadInt32(),Crc=reader.ReadUInt32(),Pinned=reader.ReadUInt32()!=0};
                    reader.ReadUInt32();
                    if(descriptor.Offset!=next||descriptor.VertexCount<=0||descriptor.IndexCount<=0||descriptor.IndexCount%3!=0||
                        descriptor.ByteCount!=(long)descriptor.VertexCount*32+(long)descriptor.IndexCount*4||descriptor.Pinned!=(page==0)){
                        throw new InvalidDataException("v3 page descriptor is invalid.");
                    }
                    next=checked(next+descriptor.ByteCount);if(next>fileBytes){throw new InvalidDataException("v3 page range exceeds archive.");}
                    actualMaxVertices=Math.Max(actualMaxVertices,descriptor.VertexCount);actualMaxIndices=Math.Max(actualMaxIndices,descriptor.IndexCount);result.Pages[page]=descriptor;
                }
                if(next!=fileBytes||actualMaxVertices!=maxVertices||actualMaxIndices!=maxIndices){throw new InvalidDataException("v3 geometry extent mismatch.");}
                for(int c=0;c<clusterCount;c++){
                    var cluster=new VirtualGeometryCluster{BoundsCenter=ReadVector(reader),BoundsRadius=reader.ReadSingle(),FirstIndex=reader.ReadUInt32(),IndexCount=reader.ReadUInt32(),
                        MaterialSlot=reader.ReadUInt32(),PageId=reader.ReadUInt32(),GeometricError=reader.ReadSingle(),Parent=reader.ReadInt32(),ChildStart=reader.ReadUInt32(),ChildCount=reader.ReadUInt32()};
                    if(cluster.PageId>=pageCount||cluster.MaterialSlot>=materialCount||cluster.IndexCount==0||cluster.IndexCount%3!=0||
                        (ulong)cluster.FirstIndex+cluster.IndexCount>(ulong)result.Pages[cluster.PageId].IndexCount||!Finite(cluster.BoundsCenter)||
                        !float.IsFinite(cluster.BoundsRadius)||cluster.BoundsRadius<0||!float.IsFinite(cluster.GeometricError)||cluster.GeometricError<0){throw new InvalidDataException("v3 cluster is invalid.");}
                    result.Clusters[c]=cluster;
                }
                for(int n=0;n<nodeCount;n++){
                    result.Nodes[n]=new VirtualGeometryHierarchyNode{
                        BoundsCenter=ReadVector(reader),BoundsRadius=reader.ReadSingle(),GeometricError=reader.ReadSingle(),FirstCluster=reader.ReadUInt32(),ClusterCount=reader.ReadUInt32(),
                        FirstChild=reader.ReadUInt32(),ChildCount=reader.ReadUInt32(),PageId=reader.ReadUInt32(),MaterialSlot=reader.ReadUInt32(),Level=reader.ReadUInt32(),
                        LeafStart=reader.ReadUInt32(),LeafCount=reader.ReadUInt32(),Reserved0=reader.ReadUInt32(),Reserved1=reader.ReadUInt32()};
                }
                for(int r=0;r<materialCount;r++){result.Roots[r]=reader.ReadUInt32();}
                var metadataPage=new VirtualGeometryPage(Array.Empty<VirtualGeometryPackedVertex>(),Array.Empty<uint>(),result.Clusters,result.Nodes,result.Roots,materialCount,result.Bounds);
                VirtualGeometryPageCodec.ValidateHierarchy(metadataPage);
                bool[] owners=new bool[clusterCount];
                foreach(var node in result.Nodes){
                    if(node.PageId>=pageCount){throw new InvalidDataException("Node page is invalid.");}
                    for(uint c=0;c<node.ClusterCount;c++){
                        int id=(int)(node.FirstCluster+c);
                        if(owners[id]||result.Clusters[id].PageId!=node.PageId){throw new InvalidDataException("Node representation is not atomic.");}
                        owners[id]=true;
                    }
                }
                foreach(bool owned in owners){if(!owned){throw new InvalidDataException("Unowned v3 cluster.");}}
                foreach(uint root in result.Roots){if(root!=uint.MaxValue&&result.Nodes[root].PageId!=0){throw new InvalidDataException("Root page must be pinned page 0.");}}
                return result;
            }
        }

        public static VirtualGeometryPagePayload DecodePayload(byte[] bytes,VirtualGeometryPageDescriptor descriptor){
            if(bytes==null||bytes.Length!=descriptor.ByteCount||VirtualGeometryPageCodec.ComputeCrc32(bytes,0,bytes.Length)!=descriptor.Crc){throw new InvalidDataException("Geometry page size/CRC failed.");}
            var vertices=new VirtualGeometryPackedVertex[descriptor.VertexCount];var indices=new uint[descriptor.IndexCount];
            using(var reader=new BinaryReader(new MemoryStream(bytes,false))){
                for(int i=0;i<vertices.Length;i++){
                    Vector3 position=ReadVector(reader);Vector3 normal=ReadVector(reader);Vector2 uv=new Vector2(reader.ReadSingle(),reader.ReadSingle());
                    if(!Finite(position)||!Finite(normal)||!float.IsFinite(uv.x)||!float.IsFinite(uv.y)){throw new InvalidDataException("Non-finite page vertex.");}
                    vertices[i]=new VirtualGeometryPackedVertex(position,normal,uv);
                }
                for(int i=0;i<indices.Length;i++){indices[i]=reader.ReadUInt32();if(indices[i]>=vertices.Length){throw new InvalidDataException("Page-local index out of bounds.");}}
            }
            return new VirtualGeometryPagePayload(vertices,indices);
        }

        public static VirtualGeometryPage ReadAll(byte[] bytes){
            using(var stream=new MemoryStream(bytes,false)){
                VirtualGeometryStreamMetadata metadata=ReadMetadata(stream);
                var vertices=new List<VirtualGeometryPackedVertex>();var indices=new List<uint>();
                var clusters=(VirtualGeometryCluster[])metadata.Clusters.Clone();
                for(int p=0;p<metadata.Pages.Length;p++){
                    var descriptor=metadata.Pages[p];byte[] body=new byte[descriptor.ByteCount];Array.Copy(bytes,descriptor.Offset,body,0,body.Length);
                    VirtualGeometryPagePayload payload=DecodePayload(body,descriptor);uint vertexBase=(uint)vertices.Count;uint indexBase=(uint)indices.Count;
                    vertices.AddRange(payload.Vertices);foreach(uint index in payload.Indices){indices.Add(vertexBase+index);}
                    for(int c=0;c<clusters.Length;c++){if(clusters[c].PageId==p){clusters[c].FirstIndex+=indexBase;}}
                }
                return new VirtualGeometryPage(vertices.ToArray(),indices.ToArray(),clusters,metadata.Nodes,metadata.Roots,metadata.Roots.Length,metadata.Bounds);
            }
        }

        private static byte[] EncodePayload(VirtualGeometryPagePayload payload){
            using(var stream=new MemoryStream())
            using(var writer=new BinaryWriter(stream)){
                VirtualGeometryPageCodec.WriteVertices(writer,payload.Vertices);foreach(uint index in payload.Indices){writer.Write(index);}return stream.ToArray();
            }
        }

        private static bool Finite(Vector3 value){return float.IsFinite(value.x)&&float.IsFinite(value.y)&&float.IsFinite(value.z);}
        private static void WriteVector(BinaryWriter writer,Vector3 value){writer.Write(value.x);writer.Write(value.y);writer.Write(value.z);}
        private static Vector3 ReadVector(BinaryReader reader){return new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());}
    }
}