using System;
using UnityEngine;

namespace Omni.PlanetSurface{
    /// <summary>LOD遷移のスナップショットとモーフ差分を再利用し、移動中の大きな配列確保を防ぎます。</summary>
    public sealed class SurfacePatchTransitionWorkspace{
        public const int DefaultMaximumPatchCount=64;
        private const long ManagedOverheadBytes=16L*1024;
        private readonly Vector3[][] vector3Buffers;
        private readonly Vector4[][] vector4Buffers;
        private int availableVector3,availableVector4;

        public SurfacePatchTransitionWorkspace(int vertexCount,int maximumPatchCount=DefaultMaximumPatchCount){
            if(vertexCount<1){throw new ArgumentOutOfRangeException(nameof(vertexCount));}
            if(maximumPatchCount<1){
                throw new ArgumentOutOfRangeException(nameof(maximumPatchCount));
            }
            this.VertexCount=vertexCount;this.MaximumPatchCount=maximumPatchCount;
            int count=checked(maximumPatchCount*2);
            this.vector3Buffers=new Vector3[count][];this.vector4Buffers=new Vector4[count][];
            for(int i=0;i<count;i++){
                this.vector3Buffers[i]=new Vector3[vertexCount];
                this.vector4Buffers[i]=new Vector4[vertexCount];
            }
            this.availableVector3=count;this.availableVector4=count;
        }

        public int VertexCount{get;}
        public int MaximumPatchCount{get;}
        public int AvailableVector3BufferCount=>this.availableVector3;
        public int AvailableVector4BufferCount=>this.availableVector4;
        public long CpuBytes=>EstimateCpuBytes(this.VertexCount,this.MaximumPatchCount);

        /// <summary>
        /// 遷移対象は将来トポロジーの葉を上限とし、統合時だけ表示を終える旧4子を追加で保持します。
        /// </summary>
        public static int RequiredPatchCapacity(int maximumVisiblePatches){
            if(maximumVisiblePatches<6){throw new ArgumentOutOfRangeException(nameof(maximumVisiblePatches));}
            return checked(maximumVisiblePatches+4);
        }

        /// <summary>2個のVector3配列と2個のVector4配列を各遷移パッチへ予約した上限値です。</summary>
        public static long EstimateCpuBytes(int vertexCount,int maximumPatchCount=DefaultMaximumPatchCount){
            if(vertexCount<1){throw new ArgumentOutOfRangeException(nameof(vertexCount));}
            if(maximumPatchCount<1){
                throw new ArgumentOutOfRangeException(nameof(maximumPatchCount));
            }
            return checked(ManagedOverheadBytes+(long)vertexCount*56L*maximumPatchCount);
        }

        internal Vector3[] RentVector3(){
            if(this.availableVector3==0){throw new InvalidOperationException("LOD遷移のVector3作業領域が不足しています。");}
            Vector3[] result=this.vector3Buffers[--this.availableVector3];
            this.vector3Buffers[this.availableVector3]=null;return result;
        }
        internal Vector4[] RentVector4(){
            if(this.availableVector4==0){throw new InvalidOperationException("LOD遷移のVector4作業領域が不足しています。");}
            Vector4[] result=this.vector4Buffers[--this.availableVector4];
            this.vector4Buffers[this.availableVector4]=null;return result;
        }
        internal void Return(Vector3[] buffer){
            if(buffer==null||buffer.Length!=this.VertexCount||this.availableVector3==this.vector3Buffers.Length){
                throw new InvalidOperationException("返却されたLOD遷移Vector3作業領域が不正です。");
            }
            this.vector3Buffers[this.availableVector3++]=buffer;
        }
        internal void Return(Vector4[] buffer){
            if(buffer==null||buffer.Length!=this.VertexCount||this.availableVector4==this.vector4Buffers.Length){
                throw new InvalidOperationException("返却されたLOD遷移Vector4作業領域が不正です。");
            }
            this.vector4Buffers[this.availableVector4++]=buffer;
        }
    }
}
