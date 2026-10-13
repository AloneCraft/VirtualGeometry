using System;
using UnityEngine;

namespace Omni.PlanetSurface{
    public sealed partial class SurfaceDetailTileGenerator{
        /// <summary>同一解像度のタイル生成で再利用するGPU作業領域です。毎回の確保・遅延解放を避けます。</summary>
        public sealed class CompactWorkspace:IDisposable{
            private readonly SurfaceDetailTileGenerator owner;
            private readonly int resolution,bandCount;
            private ComputeBuffer bands,corrections,naturalWeights;
            private SurfaceDetailTile tile;
            private bool disposed;
            public long GpuBytes{get;}
            internal CompactWorkspace(SurfaceDetailTileGenerator owner,int resolution,int bandCount,long budget,bool hasHeightOverrides){
                if(resolution<16||resolution>512||bandCount<1||bandCount>8){throw new ArgumentOutOfRangeException(nameof(resolution));}
                int heightSize=resolution+4,normalSize=resolution+2;
                int correctionSize=SurfaceDetailPatchRequest.GetCorrectionSize(resolution);
                long outputBytes=heightSize*(long)heightSize*4+normalSize*(long)normalSize*8;
                this.GpuBytes=outputBytes+correctionSize*(long)correctionSize*4+bandCount*48L;
                if(hasHeightOverrides){this.GpuBytes+=correctionSize*(long)correctionSize*4;}
                if(this.GpuBytes>budget){throw new InvalidOperationException("GPUタイルと入力の作業予算が不足しています。");}
                if(!SystemInfo.supportsComputeShaders||
                    !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RFloat)||
                    !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf)){
                    throw new NotSupportedException("RFloat/ARGBHalf storage textureが必要です。");
                }
                this.owner=owner;this.resolution=resolution;this.bandCount=bandCount;
                this.tile=new SurfaceDetailTile{GpuBytes=outputBytes,CoreResolution=resolution,NormalHaloPixels=1};
                try{
                    this.tile.Height=Create(heightSize,RenderTextureFormat.RFloat,"Surface local height");
                    this.tile.NormalRoughness=Create(normalSize,RenderTextureFormat.ARGBHalf,"Surface halo normal roughness");
                    this.bands=new ComputeBuffer(bandCount,48,ComputeBufferType.Structured);
                    this.corrections=new ComputeBuffer(correctionSize*correctionSize,4,ComputeBufferType.Structured);
                    if(hasHeightOverrides){this.naturalWeights=new ComputeBuffer(correctionSize*correctionSize,4,ComputeBufferType.Structured);}
                }catch{this.Dispose();throw;}
            }

            /// <summary>返り値はworkspace所有です。次回生成で上書きされるため、保持する場合はGPU上でコピーします。</summary>
            public SurfaceDetailTile Generate(SurfaceDetailPatchRequest request){
                if(this.disposed){throw new ObjectDisposedException(nameof(CompactWorkspace));}
                if(request==null){throw new ArgumentNullException(nameof(request));}
                if(!request.IsComplete){throw new InvalidOperationException("低周波補正の準備が完了していません。");}
                if(request.Resolution!=this.resolution||request.Bands.Length!=this.bandCount||
                    (request.NaturalWeights!=null)!=(this.naturalWeights!=null)){
                    throw new ArgumentException("GPU作業領域の寸法が一致しません。",nameof(request));
                }
                this.tile.NormalsInPlanetSpace=request.FullSurfaceNormals;
                this.owner.DispatchCompact(request,this.tile,this.bands,this.corrections,this.naturalWeights);
                return this.tile;
            }
            internal SurfaceDetailTile DetachTile(){
                SurfaceDetailTile result=this.tile;this.tile=null;return result;
            }
            public void Dispose(){
                if(this.disposed){return;}this.disposed=true;
                this.bands?.Dispose();this.corrections?.Dispose();this.naturalWeights?.Dispose();this.tile?.Dispose();
                this.bands=null;this.corrections=null;this.naturalWeights=null;this.tile=null;
            }
        }

        /// <summary>atlasが寿命中保持する、入力buffer・高さ・法線の固定作業領域を確保します。</summary>
        public CompactWorkspace CreateCompactWorkspace(int resolution,int bandCount,long availableGpuBytes,bool hasHeightOverrides=false)=>
            new(this,resolution,bandCount,availableGpuBytes,hasHeightOverrides);

        /// <summary>独立タイルが必要な検証や呼出し向けです。atlasはCompactWorkspaceを再利用します。</summary>
        public SurfaceDetailTile Generate(SurfaceDetailPatchRequest request,long availableGpuBytes){
            if(request==null){throw new ArgumentNullException(nameof(request));}
            if(!request.IsComplete){throw new InvalidOperationException("低周波補正の準備が完了していません。");}
            using(var workspace=this.CreateCompactWorkspace(request.Resolution,request.Bands.Length,availableGpuBytes,request.NaturalWeights!=null)){
                workspace.Generate(request);return workspace.DetachTile();
            }
        }

        private void DispatchCompact(SurfaceDetailPatchRequest request,SurfaceDetailTile tile,ComputeBuffer bands,ComputeBuffer corrections,ComputeBuffer naturalWeights){
            bands.SetData(request.Bands);corrections.SetData(request.Corrections);
            if(naturalWeights!=null){naturalWeights.SetData(request.NaturalWeights);}
            this.shader.SetInt("_HasHeightBlend",Convert.ToInt32(naturalWeights!=null));
            this.shader.SetInt("_Resolution",request.Resolution);this.shader.SetInt("_Bands",request.Bands.Length);
            this.shader.SetInt("_CorrectionSize",request.CorrectionSize);
            this.shader.SetVector("_PatchDu",request.Frame.Du);this.shader.SetVector("_PatchDv",request.Frame.Dv);
            this.shader.SetVector("_PatchDuu",request.Frame.Duu);this.shader.SetVector("_PatchDuv",request.Frame.Duv);
            this.shader.SetVector("_PatchDvv",request.Frame.Dvv);
            this.shader.SetInt("_FullSurfaceNormals",Convert.ToInt32(request.FullSurfaceNormals));
            SurfaceVector radial=request.Frame.Origin/request.Frame.RadiusMeters;
            this.shader.SetVector("_PatchRadial",new Vector4((float)radial.X,(float)radial.Y,(float)radial.Z,
                (float)(1/request.Frame.RadiusMeters)));
            this.shader.SetFloat("_ReferenceHeight",(float)request.ReferenceHeightMeters);
            int heightKernel=this.shader.FindKernel("HeightCompact");
            this.shader.SetBuffer(heightKernel,"_BandOrigins",bands);
            this.shader.SetBuffer(heightKernel,"_CorrectionValues",corrections);
            // 自然地形だけなら読み取らない既存bufferを束縛し、追加確保を避けます。
            this.shader.SetBuffer(heightKernel,"_NaturalWeights",naturalWeights??corrections);
            this.shader.SetTexture(heightKernel,"_Height",tile.Height);
            this.shader.Dispatch(heightKernel,(request.HeightSize+7)/8,(request.HeightSize+7)/8,1);
            int normalKernel=this.shader.FindKernel("NormalCompact");
            this.shader.SetTexture(normalKernel,"_HeightInput",tile.Height);
            this.shader.SetTexture(normalKernel,"_NormalRoughness",tile.NormalRoughness);
            this.shader.Dispatch(normalKernel,(request.Resolution+9)/8,(request.Resolution+9)/8,1);
        }
    }
}
