using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace VirtualizedGeometry.Editor{
    public readonly struct MeshOptimizerSimplifyResult{
        public uint[] Indices{get;}
        public float Error{get;}
        public int ScratchVertexCount{get;}

        public MeshOptimizerSimplifyResult(uint[] indices,float error,int scratchVertexCount){
            this.Indices=indices??throw new ArgumentNullException(nameof(indices));
            this.Error=error;this.ScratchVertexCount=scratchVertexCount;
        }
    }

    /// <summary>meshoptimizer v1.1を決定的なEditor変換だけに使用する薄い境界。</summary>
    public static class MeshOptimizerSimplifier{
        public const byte VertexLock=1;
        public const int ExpectedVersion=1010;
        private const uint SimplifySparse=1u<<1;
        private const uint SimplifyErrorAbsolute=1u<<2;
        private const string LibraryName="VirtualGeometryMeshOptimizer";
        private static volatile bool versionVerified;

        public static int Version{
            get{
                int version=NativeMethods.Version();
                if(version!=ExpectedVersion){
                    throw new InvalidOperationException($"Unsupported meshoptimizer version {version}; expected {ExpectedVersion} (v1.1).");
                }
                return version;
            }
        }

        public static MeshOptimizerSimplifyResult Simplify(VirtualGeometryPackedVertex[] vertices,uint[] indices,byte[] vertexLocks,int targetIndexCount){
            if(vertices==null){throw new ArgumentNullException(nameof(vertices));}
            if(vertexLocks==null){throw new ArgumentNullException(nameof(vertexLocks));}
            if(vertexLocks.Length!=vertices.Length){throw new ArgumentException("Vertex lock count must equal vertex count.",nameof(vertexLocks));}
            return SimplifyCore(vertices,indices,index=>vertexLocks[index]!=0,targetIndexCount);
        }
        /// <summary>targetErrorは位置と同じ単位の誤差上限。超える縮約はせず、目標数に届かなくても止める。</summary>
        internal static MeshOptimizerSimplifyResult SimplifyLockedVertices(VirtualGeometryPackedVertex[] vertices,uint[] indices,ISet<uint> vertexLocks,int targetIndexCount,float targetError=float.MaxValue){
            if(vertexLocks==null){throw new ArgumentNullException(nameof(vertexLocks));}
            return SimplifyCore(vertices,indices,vertexLocks.Contains,targetIndexCount,targetError);
        }
        private static MeshOptimizerSimplifyResult SimplifyCore(VirtualGeometryPackedVertex[] vertices,uint[] indices,Func<uint,bool> isLocked,int targetIndexCount,float targetError=float.MaxValue){
            if(float.IsNaN(targetError)||targetError<0){throw new ArgumentOutOfRangeException(nameof(targetError));}
            if(vertices==null){throw new ArgumentNullException(nameof(vertices));}
            if(indices==null){throw new ArgumentNullException(nameof(indices));}

            if(vertices.Length==0){throw new ArgumentException("At least one vertex is required.",nameof(vertices));}
            if(indices.Length==0||indices.Length%3!=0){throw new ArgumentException("Indices must contain complete triangles.",nameof(indices));}

            if(targetIndexCount<3||targetIndexCount>indices.Length){throw new ArgumentOutOfRangeException(nameof(targetIndexCount));}
            targetIndexCount-=targetIndexCount%3;
            for(int i=0;i<indices.Length;i++){
                if(indices[i]>=vertices.Length){throw new ArgumentException("An index references a vertex outside the input array.",nameof(indices));}
            }

            // 版の確認は一度だけでよい。簡略化は1 meshで数万回、並列にも呼ばれるため、毎回native呼び出しをしない。
            if(!versionVerified){_=Version;versionVerified=true;}
            var referenced=new HashSet<uint>(indices);uint[] globalIndices=new uint[referenced.Count];referenced.CopyTo(globalIndices);Array.Sort(globalIndices);
            var remap=new Dictionary<uint,uint>(globalIndices.Length);byte[] compactLocks=new byte[globalIndices.Length];
            for(int i=0;i<globalIndices.Length;i++){remap.Add(globalIndices[i],(uint)i);compactLocks[i]=isLocked(globalIndices[i])?VertexLock:(byte)0;}
            uint[] localIndices=new uint[indices.Length];for(int i=0;i<indices.Length;i++){localIndices[i]=remap[indices[i]];}
            float[] positions=new float[globalIndices.Length*3];
            float[] attributes=new float[globalIndices.Length*5];
            for(int i=0;i<globalIndices.Length;i++){
                VirtualGeometryPackedVertex vertex=vertices[globalIndices[i]];
                int position=i*3;
                positions[position]=vertex.Position.x;
                positions[position+1]=vertex.Position.y;
                positions[position+2]=vertex.Position.z;
                int attribute=i*5;
                attributes[attribute]=vertex.Normal.x;
                attributes[attribute+1]=vertex.Normal.y;
                attributes[attribute+2]=vertex.Normal.z;
                attributes[attribute+3]=vertex.UV.x;
                attributes[attribute+4]=vertex.UV.y;
            }

            uint[] destination=new uint[indices.Length];
            float[] weights={0.5f,0.5f,0.5f,0.1f,0.1f};
            int resultCount=NativeMethods.SimplifyWithAttributes(destination,(uint)destination.Length,localIndices,(uint)localIndices.Length,
                positions,(uint)globalIndices.Length,3*sizeof(float),attributes,5*sizeof(float),weights,(uint)weights.Length,compactLocks,
                (uint)targetIndexCount,targetError,SimplifySparse|SimplifyErrorAbsolute,out float error);
            if(resultCount<0){throw new InvalidOperationException($"meshoptimizer rejected simplification input (native status {resultCount}).");}
            if(resultCount>destination.Length||resultCount%3!=0||!float.IsFinite(error)){
                throw new InvalidOperationException("meshoptimizer returned an invalid result.");
            }
            Array.Resize(ref destination,resultCount);
            for(int i=0;i<destination.Length;i++){if(destination[i]>=globalIndices.Length){throw new InvalidOperationException("Native simplifier returned an invalid compact index.");}destination[i]=globalIndices[destination[i]];}
            return new MeshOptimizerSimplifyResult(destination,error,positions.Length/3);
        }

        private static class NativeMethods{
            [DllImport(LibraryName,EntryPoint="vgMeshoptVersion",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
            public static extern int Version();

            [DllImport(LibraryName,EntryPoint="vgMeshoptSimplifyWithAttributes",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
            public static extern int SimplifyWithAttributes(
                [Out] uint[] destination,uint destinationCapacity,[In] uint[] indices,uint indexCount,[In] float[] vertexPositions,
                uint vertexCount,uint vertexPositionsStride,[In] float[] vertexAttributes,uint vertexAttributesStride,[In] float[] attributeWeights,
                uint attributeCount,[In] byte[] vertexLock,uint targetIndexCount,float targetError,uint options,out float resultError);
        }
    }
}
