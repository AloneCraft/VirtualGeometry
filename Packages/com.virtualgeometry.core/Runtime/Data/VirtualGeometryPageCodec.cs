using System;
using System.IO;
using UnityEngine;

namespace VirtualizedGeometry{
    /// <summary>バージョン・範囲・CRC・階層不変条件を検証し、破損ページをGPUへ渡さない。</summary>
    public static class VirtualGeometryPageCodec{
        public const uint Magic=0x47504756;
        public const uint FormatVersion=2;
        public const int HeaderSize=112;
        public const uint BuilderVersion=2;
        private const uint LegacyFormatVersion=1;
        private const int LegacyHeaderSize=80;

        public static byte[] Write(VirtualGeometryPage page){
            if(page==null){throw new ArgumentNullException(nameof(page));}
            ValidateForWrite(page);
            int vertexOffset=HeaderSize;
            int indexOffset=checked(vertexOffset+page.Vertices.Length*VirtualGeometryPackedVertex.Stride);
            int clusterOffset=checked(indexOffset+page.Indices.Length*sizeof(uint));
            int nodeOffset=checked(clusterOffset+page.Clusters.Length*VirtualGeometryCluster.Stride);
            int rootOffset=checked(nodeOffset+page.Nodes.Length*VirtualGeometryHierarchyNode.Stride);
            int totalBytes=checked(rootOffset+page.RootNodeIndices.Length*sizeof(uint));
            byte[] data=new byte[totalBytes];
            using(MemoryStream stream=new MemoryStream(data,true))
            using(BinaryWriter writer=new BinaryWriter(stream)){
                stream.Position=vertexOffset;
                WriteVertices(writer,page.Vertices);
                stream.Position=indexOffset;
                foreach(uint index in page.Indices){writer.Write(index);}
                stream.Position=clusterOffset;
                WriteClusters(writer,page.Clusters);
                stream.Position=nodeOffset;
                WriteNodes(writer,page.Nodes);
                stream.Position=rootOffset;
                foreach(uint root in page.RootNodeIndices){writer.Write(root);}
                uint crc=ComputeCrc32(data,HeaderSize,totalBytes-HeaderSize);
                stream.Position=0;
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(HeaderSize);
                writer.Write(0u);
                writer.Write(totalBytes-HeaderSize);
                writer.Write(page.Vertices.Length);
                writer.Write(page.Indices.Length);
                writer.Write(page.Clusters.Length);
                writer.Write(page.Nodes.Length);
                writer.Write(page.RootNodeIndices.Length);
                writer.Write(page.MaterialCount);
                writer.Write(vertexOffset);
                writer.Write(indexOffset);
                writer.Write(clusterOffset);
                writer.Write(nodeOffset);
                writer.Write(rootOffset);
                writer.Write(crc);
                writer.Write(BuilderVersion);
                WriteVector3(writer,page.Bounds.min);
                WriteVector3(writer,page.Bounds.max);
                writer.Write(0u);writer.Write(0u);writer.Write(0u);writer.Write(0u);
            }
            return data;
        }

        public static VirtualGeometryPage Read(byte[] data){
            if(data==null){throw new ArgumentNullException(nameof(data));}
            if(data.Length<12){throw new InvalidDataException("Virtual Geometry page is shorter than its version header.");}
            using(MemoryStream stream=new MemoryStream(data,false))
            using(BinaryReader reader=new BinaryReader(stream)){
                uint magic=reader.ReadUInt32();
                uint version=reader.ReadUInt32();
                int headerSize=reader.ReadInt32();
                if(magic!=Magic){throw new InvalidDataException("Virtual Geometry page magic is invalid.");}
                stream.Position=0;
                if(version==VirtualGeometryStreamCodec.Version){return VirtualGeometryStreamCodec.ReadAll(data);}
                if(version==LegacyFormatVersion&&headerSize==LegacyHeaderSize){return ReadLegacy(data,reader,stream);}
                if(version==FormatVersion&&headerSize==HeaderSize){return ReadCurrent(data,reader,stream);}
                throw new InvalidDataException("Virtual Geometry page format version is unsupported.");
            }
        }

        private static VirtualGeometryPage ReadCurrent(byte[] data,BinaryReader reader,MemoryStream stream){
            if(data.Length<HeaderSize){throw new InvalidDataException("Virtual Geometry v2 page is shorter than its fixed header.");}
            uint magic=reader.ReadUInt32();
            uint version=reader.ReadUInt32();
            int headerSize=reader.ReadInt32();
            reader.ReadUInt32();
            int payloadBytes=reader.ReadInt32();
            int vertexCount=reader.ReadInt32();
            int indexCount=reader.ReadInt32();
            int clusterCount=reader.ReadInt32();
            int nodeCount=reader.ReadInt32();
            int rootCount=reader.ReadInt32();
            int materialCount=reader.ReadInt32();
            int vertexOffset=reader.ReadInt32();
            int indexOffset=reader.ReadInt32();
            int clusterOffset=reader.ReadInt32();
            int nodeOffset=reader.ReadInt32();
            int rootOffset=reader.ReadInt32();
            uint crc=reader.ReadUInt32();
            uint builderVersion=reader.ReadUInt32();
            Vector3 min=ReadVector3(reader);
            Vector3 max=ReadVector3(reader);
            reader.ReadUInt32();reader.ReadUInt32();reader.ReadUInt32();reader.ReadUInt32();
            if(magic!=Magic||version!=FormatVersion||headerSize!=HeaderSize||builderVersion>BuilderVersion){
                throw new InvalidDataException("Virtual Geometry v2 header or builder version is unsupported.");
            }
            ValidateCounts(payloadBytes,data.Length-HeaderSize,vertexCount,indexCount,clusterCount,nodeCount,rootCount,materialCount);
            long expectedIndexOffset=(long)HeaderSize+(long)vertexCount*VirtualGeometryPackedVertex.Stride;
            long expectedClusterOffset=expectedIndexOffset+(long)indexCount*sizeof(uint);
            long expectedNodeOffset=expectedClusterOffset+(long)clusterCount*VirtualGeometryCluster.Stride;
            long expectedRootOffset=expectedNodeOffset+(long)nodeCount*VirtualGeometryHierarchyNode.Stride;
            long expectedLength=expectedRootOffset+(long)rootCount*sizeof(uint);
            if(vertexOffset!=HeaderSize||indexOffset!=expectedIndexOffset||clusterOffset!=expectedClusterOffset||nodeOffset!=expectedNodeOffset||
                rootOffset!=expectedRootOffset||expectedLength!=data.Length){
                throw new InvalidDataException("Virtual Geometry v2 offsets are inconsistent.");
            }
            if(rootCount!=materialCount){throw new InvalidDataException("Virtual Geometry root table must have one entry per material.");}
            ValidateCrc(data,HeaderSize,crc);
            ValidateBounds(min,max);
            VirtualGeometryPackedVertex[] vertices=ReadVertices(reader,stream,vertexOffset,vertexCount);
            uint[] indices=ReadIndices(reader,stream,indexOffset,indexCount,vertexCount);
            VirtualGeometryCluster[] clusters=ReadClusters(reader,stream,clusterOffset,clusterCount,indexCount,materialCount);
            VirtualGeometryHierarchyNode[] nodes=ReadNodes(reader,stream,nodeOffset,nodeCount);
            uint[] roots=new uint[rootCount];
            stream.Position=rootOffset;
            for(int i=0;i<roots.Length;i++){roots[i]=reader.ReadUInt32();}
            Bounds bounds=new Bounds();bounds.SetMinMax(min,max);
            VirtualGeometryPage page=new VirtualGeometryPage(vertices,indices,clusters,nodes,roots,materialCount,bounds);
            ValidateHierarchy(page);
            return page;
        }

        private static VirtualGeometryPage ReadLegacy(byte[] data,BinaryReader reader,MemoryStream stream){
            if(data.Length<LegacyHeaderSize){throw new InvalidDataException("Virtual Geometry v1 page is shorter than its fixed header.");}
            uint magic=reader.ReadUInt32();
            uint version=reader.ReadUInt32();
            int headerSize=reader.ReadInt32();
            reader.ReadUInt32();
            int payloadBytes=reader.ReadInt32();
            int vertexCount=reader.ReadInt32();
            int indexCount=reader.ReadInt32();
            int clusterCount=reader.ReadInt32();
            int materialCount=reader.ReadInt32();
            int vertexOffset=reader.ReadInt32();
            int indexOffset=reader.ReadInt32();
            int clusterOffset=reader.ReadInt32();
            uint crc=reader.ReadUInt32();
            uint builderVersion=reader.ReadUInt32();
            Vector3 min=ReadVector3(reader);
            Vector3 max=ReadVector3(reader);
            if(magic!=Magic||version!=LegacyFormatVersion||headerSize!=LegacyHeaderSize||builderVersion>1){
                throw new InvalidDataException("Virtual Geometry v1 header is unsupported.");
            }
            ValidateCounts(payloadBytes,data.Length-LegacyHeaderSize,vertexCount,indexCount,clusterCount,0,materialCount,materialCount);
            long expectedIndexOffset=(long)LegacyHeaderSize+(long)vertexCount*VirtualGeometryPackedVertex.Stride;
            long expectedClusterOffset=expectedIndexOffset+(long)indexCount*sizeof(uint);
            long expectedLength=expectedClusterOffset+(long)clusterCount*VirtualGeometryCluster.Stride;
            if(vertexOffset!=LegacyHeaderSize||indexOffset!=expectedIndexOffset||clusterOffset!=expectedClusterOffset||expectedLength!=data.Length){
                throw new InvalidDataException("Virtual Geometry v1 offsets are inconsistent.");
            }
            ValidateCrc(data,LegacyHeaderSize,crc);
            ValidateBounds(min,max);
            VirtualGeometryPackedVertex[] vertices=ReadVertices(reader,stream,vertexOffset,vertexCount);
            uint[] indices=ReadIndices(reader,stream,indexOffset,indexCount,vertexCount);
            VirtualGeometryCluster[] clusters=ReadClusters(reader,stream,clusterOffset,clusterCount,indexCount,materialCount);
            Bounds bounds=new Bounds();bounds.SetMinMax(min,max);
            return new VirtualGeometryPage(vertices,indices,clusters,materialCount,bounds);
        }

        private static VirtualGeometryPackedVertex[] ReadVertices(BinaryReader reader,MemoryStream stream,int offset,int count){
            VirtualGeometryPackedVertex[] vertices=new VirtualGeometryPackedVertex[count];
            stream.Position=offset;
            for(int i=0;i<vertices.Length;i++){
                vertices[i]=new VirtualGeometryPackedVertex(ReadVector3(reader),ReadVector3(reader),new Vector2(reader.ReadSingle(),reader.ReadSingle()));
                ValidateFinite(vertices[i].Position,"vertex position");
                ValidateFinite(vertices[i].Normal,"vertex normal");
                if(!float.IsFinite(vertices[i].UV.x)||!float.IsFinite(vertices[i].UV.y)){throw new InvalidDataException("Virtual Geometry page has a non-finite vertex UV.");}
            }
            return vertices;
        }

        private static uint[] ReadIndices(BinaryReader reader,MemoryStream stream,int offset,int count,int vertexCount){
            uint[] indices=new uint[count];
            stream.Position=offset;
            for(int i=0;i<indices.Length;i++){
                indices[i]=reader.ReadUInt32();
                if(indices[i]>=vertexCount){throw new InvalidDataException("Virtual Geometry page contains an out-of-range vertex index.");}
            }
            return indices;
        }

        private static VirtualGeometryCluster[] ReadClusters(BinaryReader reader,MemoryStream stream,int offset,int count,int indexCount,int materialCount){
            VirtualGeometryCluster[] clusters=new VirtualGeometryCluster[count];
            stream.Position=offset;
            for(int i=0;i<clusters.Length;i++){
                clusters[i]=new VirtualGeometryCluster{
                    BoundsCenter=ReadVector3(reader),BoundsRadius=reader.ReadSingle(),FirstIndex=reader.ReadUInt32(),IndexCount=reader.ReadUInt32(),
                    MaterialSlot=reader.ReadUInt32(),PageId=reader.ReadUInt32(),GeometricError=reader.ReadSingle(),Parent=reader.ReadInt32(),
                    ChildStart=reader.ReadUInt32(),ChildCount=reader.ReadUInt32()
                };
                VirtualGeometryCluster cluster=clusters[i];
                ValidateFinite(cluster.BoundsCenter,"cluster bounds");
                if(!float.IsFinite(cluster.BoundsRadius)||cluster.BoundsRadius<0||!float.IsFinite(cluster.GeometricError)||cluster.GeometricError<0||
                    cluster.IndexCount==0||cluster.IndexCount%3!=0||(ulong)cluster.FirstIndex+cluster.IndexCount>(ulong)indexCount||
                    cluster.MaterialSlot>=materialCount){
                    throw new InvalidDataException("Virtual Geometry page contains an invalid cluster.");
                }
            }
            return clusters;
        }

        private static VirtualGeometryHierarchyNode[] ReadNodes(BinaryReader reader,MemoryStream stream,int offset,int count){
            VirtualGeometryHierarchyNode[] nodes=new VirtualGeometryHierarchyNode[count];
            stream.Position=offset;
            for(int i=0;i<nodes.Length;i++){
                nodes[i]=new VirtualGeometryHierarchyNode{
                    BoundsCenter=ReadVector3(reader),BoundsRadius=reader.ReadSingle(),GeometricError=reader.ReadSingle(),
                    FirstCluster=reader.ReadUInt32(),ClusterCount=reader.ReadUInt32(),FirstChild=reader.ReadUInt32(),ChildCount=reader.ReadUInt32(),
                    PageId=reader.ReadUInt32(),MaterialSlot=reader.ReadUInt32(),Level=reader.ReadUInt32(),LeafStart=reader.ReadUInt32(),
                    LeafCount=reader.ReadUInt32(),Reserved0=reader.ReadUInt32(),Reserved1=reader.ReadUInt32()
                };
            }
            return nodes;
        }

        internal static void WriteVertices(BinaryWriter writer,VirtualGeometryPackedVertex[] vertices){
            foreach(VirtualGeometryPackedVertex vertex in vertices){
                WriteVector3(writer,vertex.Position);
                WriteVector3(writer,vertex.Normal);
                writer.Write(vertex.UV.x);writer.Write(vertex.UV.y);
            }
        }

        internal static void WriteClusters(BinaryWriter writer,VirtualGeometryCluster[] clusters){
            foreach(VirtualGeometryCluster cluster in clusters){
                WriteVector3(writer,cluster.BoundsCenter);
                writer.Write(cluster.BoundsRadius);writer.Write(cluster.FirstIndex);writer.Write(cluster.IndexCount);writer.Write(cluster.MaterialSlot);
                writer.Write(cluster.PageId);writer.Write(cluster.GeometricError);writer.Write(cluster.Parent);writer.Write(cluster.ChildStart);writer.Write(cluster.ChildCount);
            }
        }

        internal static void WriteNodes(BinaryWriter writer,VirtualGeometryHierarchyNode[] nodes){
            foreach(VirtualGeometryHierarchyNode node in nodes){
                WriteVector3(writer,node.BoundsCenter);
                writer.Write(node.BoundsRadius);writer.Write(node.GeometricError);writer.Write(node.FirstCluster);writer.Write(node.ClusterCount);
                writer.Write(node.FirstChild);writer.Write(node.ChildCount);writer.Write(node.PageId);writer.Write(node.MaterialSlot);writer.Write(node.Level);
                writer.Write(node.LeafStart);writer.Write(node.LeafCount);writer.Write(node.Reserved0);writer.Write(node.Reserved1);
            }
        }

        private static void ValidateForWrite(VirtualGeometryPage page){
            if(page.MaterialCount<0||page.RootNodeIndices.Length!=page.MaterialCount){throw new InvalidDataException("Page material/root counts are invalid.");}
            ValidateBounds(page.Bounds.min,page.Bounds.max);
            for(int i=0;i<page.Vertices.Length;i++){
                ValidateFinite(page.Vertices[i].Position,"vertex position");
                ValidateFinite(page.Vertices[i].Normal,"vertex normal");
                if(!float.IsFinite(page.Vertices[i].UV.x)||!float.IsFinite(page.Vertices[i].UV.y)){throw new InvalidDataException("Cannot write a non-finite vertex UV.");}
            }
            for(int i=0;i<page.Indices.Length;i++){
                if(page.Indices[i]>=page.Vertices.Length){throw new InvalidDataException("Cannot write an out-of-range vertex index.");}
            }
            foreach(VirtualGeometryCluster cluster in page.Clusters){
                if(cluster.IndexCount==0||cluster.IndexCount%3!=0||(ulong)cluster.FirstIndex+cluster.IndexCount>(ulong)page.Indices.Length||
                    cluster.MaterialSlot>=page.MaterialCount||!float.IsFinite(cluster.BoundsRadius)||cluster.BoundsRadius<0||
                    !float.IsFinite(cluster.GeometricError)||cluster.GeometricError<0){
                    throw new InvalidDataException("Cannot write an invalid cluster.");
                }
            }
            ValidateHierarchy(page);
        }

        internal static void ValidateHierarchy(VirtualGeometryPage page){
            if(page.Nodes.Length==0){
                foreach(uint root in page.RootNodeIndices){if(root!=uint.MaxValue){throw new InvalidDataException("Hierarchy-free page contains a root node.");}}
                return;
            }
            byte[] state=new byte[page.Nodes.Length];
            uint expectedLeafStart=0;
            for(int material=0;material<page.RootNodeIndices.Length;material++){
                uint rootIndex=page.RootNodeIndices[material];
                if(rootIndex==uint.MaxValue){continue;}
                if(rootIndex>=page.Nodes.Length){throw new InvalidDataException("Hierarchy root index is out of range.");}
                VirtualGeometryHierarchyNode root=page.Nodes[rootIndex];
                if(root.MaterialSlot!=material||root.LeafStart!=expectedLeafStart){throw new InvalidDataException("Hierarchy root material or leaf range is inconsistent.");}
                ValidateNode(page,checked((int)rootIndex),material,state);
                expectedLeafStart=checked(expectedLeafStart+root.LeafCount);
            }
            for(int i=0;i<state.Length;i++){if(state[i]!=2){throw new InvalidDataException("Hierarchy contains an unreachable node.");}}
        }

        private static void ValidateNode(VirtualGeometryPage page,int nodeIndex,int material,byte[] state){
            if(state[nodeIndex]==1){throw new InvalidDataException("Hierarchy contains a cycle.");}
            if(state[nodeIndex]==2){throw new InvalidDataException("Hierarchy node has multiple parents or roots.");}
            state[nodeIndex]=1;
            VirtualGeometryHierarchyNode node=page.Nodes[nodeIndex];
            ValidateFinite(node.BoundsCenter,"hierarchy bounds");
            if(!float.IsFinite(node.BoundsRadius)||node.BoundsRadius<0||!float.IsFinite(node.GeometricError)||node.GeometricError<0||
                node.MaterialSlot!=material||node.ClusterCount==0||(ulong)node.FirstCluster+node.ClusterCount>(ulong)page.Clusters.Length||node.LeafCount==0){
                throw new InvalidDataException("Hierarchy node fields are invalid.");
            }
            for(uint i=0;i<node.ClusterCount;i++){
                if(page.Clusters[node.FirstCluster+i].MaterialSlot!=node.MaterialSlot){throw new InvalidDataException("Hierarchy representation crosses a material boundary.");}
            }
            if(node.ChildCount==0){
                if(node.Level!=0||node.LeafCount!=1){throw new InvalidDataException("Hierarchy leaf metadata is invalid.");}
            }else{
                if((ulong)node.FirstChild+node.ChildCount>(ulong)page.Nodes.Length){throw new InvalidDataException("Hierarchy child range is out of bounds.");}
                uint expected=node.LeafStart;
                uint covered=0;
                for(uint i=0;i<node.ChildCount;i++){
                    int childIndex=checked((int)(node.FirstChild+i));
                    VirtualGeometryHierarchyNode child=page.Nodes[childIndex];
                    if(child.LeafStart!=expected||child.Level>=node.Level||child.GeometricError>node.GeometricError+1e-6f){
                        throw new InvalidDataException("Hierarchy child coverage, level, or error is inconsistent.");
                    }
                    ValidateNode(page,childIndex,material,state);
                    expected=checked(expected+child.LeafCount);
                    covered=checked(covered+child.LeafCount);
                }
                if(covered!=node.LeafCount){throw new InvalidDataException("Hierarchy children do not exactly cover their parent leaf range.");}
            }
            state[nodeIndex]=2;
        }

        private static void ValidateCounts(int payloadBytes,int expectedPayload,int vertexCount,int indexCount,int clusterCount,int nodeCount,int rootCount,int materialCount){
            if(payloadBytes!=expectedPayload||vertexCount<0||indexCount<0||clusterCount<0||nodeCount<0||rootCount<0||materialCount<0){
                throw new InvalidDataException("Virtual Geometry page counts or payload size are invalid.");
            }
        }

        private static void ValidateCrc(byte[] data,int headerSize,uint expected){
            if(ComputeCrc32(data,headerSize,data.Length-headerSize)!=expected){throw new InvalidDataException("Virtual Geometry page CRC does not match its payload.");}
        }

        // 標準CRC-32(多項式0xEDB88320)。数百MBのpageを書き出し・検証するため、1 bitずつではなく8 byteずつ表引きする(slicing-by-8)。
        private static readonly uint[] crcTable=CreateCrcTable();
        private static uint[] CreateCrcTable(){
            var table=new uint[8*256];
            for(uint value=0;value<256;value++){
                uint crc=value;
                for(int bit=0;bit<8;bit++){uint mask=(uint)-(int)(crc&1u);crc=(crc>>1)^(0xEDB88320u&mask);}
                table[value]=crc;
            }
            for(int slice=1;slice<8;slice++){
                for(int value=0;value<256;value++){uint previous=table[(slice-1)*256+value];table[slice*256+value]=(previous>>8)^table[previous&0xFF];}
            }
            return table;
        }

        internal static uint ComputeCrc32(byte[] data,int offset,int count){
            uint[] table=crcTable;
            uint crc=uint.MaxValue;int i=offset;int end=offset+count;
            while(end-i>=8){
                uint low=crc^(uint)(data[i]|data[i+1]<<8|data[i+2]<<16|data[i+3]<<24);
                uint high=(uint)(data[i+4]|data[i+5]<<8|data[i+6]<<16|data[i+7]<<24);
                crc=table[7*256+(low&0xFF)]^table[6*256+((low>>8)&0xFF)]^table[5*256+((low>>16)&0xFF)]^table[4*256+(low>>24)]^
                    table[3*256+(high&0xFF)]^table[2*256+((high>>8)&0xFF)]^table[256+((high>>16)&0xFF)]^table[high>>24];
                i+=8;
            }
            while(i<end){crc=(crc>>8)^table[(crc^data[i])&0xFF];i++;}
            return ~crc;
        }

        private static void WriteVector3(BinaryWriter writer,Vector3 value){
            writer.Write(value.x);writer.Write(value.y);writer.Write(value.z);
        }

        private static Vector3 ReadVector3(BinaryReader reader){
            return new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
        }

        private static void ValidateBounds(Vector3 min,Vector3 max){
            ValidateFinite(min,"bounds minimum");ValidateFinite(max,"bounds maximum");
            if(min.x>max.x||min.y>max.y||min.z>max.z){throw new InvalidDataException("Virtual Geometry bounds are inverted.");}
        }

        private static void ValidateFinite(Vector3 value,string label){
            if(!float.IsFinite(value.x)||!float.IsFinite(value.y)||!float.IsFinite(value.z)){throw new InvalidDataException("Virtual Geometry page has a non-finite "+label+".");}
        }
    }
}
