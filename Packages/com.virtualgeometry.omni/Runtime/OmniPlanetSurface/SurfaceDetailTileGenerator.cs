using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Omni.PlanetSurface{
    /// <summary>GPUへ渡す整数位置IDと小さなセル内座標です。GPUのInt64を必要としません。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SurfaceDetailPoint{
        public int CellX,CellY,CellZ;
        public float LocalX,LocalY,LocalZ;
        public float Correction,Amplitude,Weight;
        public uint Seed;
    }

    /// <summary>法線用残差高さとタイルを所有します。キャッシュからの解放時にDisposeします。</summary>
    public sealed class SurfaceDetailTile:IDisposable{
        public RenderTexture Height{get;internal set;}
        public RenderTexture NormalRoughness{get;internal set;}
        public long GpuBytes{get;internal set;}
        public int CoreResolution{get;internal set;}
        public int NormalHaloPixels{get;internal set;}
        public bool NormalsInPlanetSpace{get;internal set;}
        /// <summary>生成済みGPUデータだけを解放します。基礎データには触れません。</summary>
        public void Dispose(){
            if(this.Height!=null){this.Height.Release();DestroyTransient(this.Height);}
            if(this.NormalRoughness!=null){this.NormalRoughness.Release();DestroyTransient(this.NormalRoughness);}
            this.Height=null;this.NormalRoughness=null;
        }
        internal static void DestroyTransient(UnityEngine.Object value){
            if(Application.isPlaying){UnityEngine.Object.Destroy(value);}
            else{UnityEngine.Object.DestroyImmediate(value);}
        }
    }

    /// <summary>
    /// 月面検証用のGPU残差高さ・法線生成です。通常のGraphics queueを使い、同期読み戻しは行いません。
    /// CPUによる入力準備と描画へのキャッシュ接続は呼び出し側が時間・容量を管理します。
    /// </summary>
    public sealed partial class SurfaceDetailTileGenerator{
        private readonly ComputeShader shader;
        /// <summary>SurfaceDetail.computeを渡します。Resourcesロードへ暗黙依存しません。</summary>
        public SurfaceDetailTileGenerator(ComputeShader shader){
            this.shader=shader??throw new ArgumentNullException(nameof(shader));
        }

        /// <summary>
        /// 入力は(resolution+2)^2画素×bandCountです。1画素のhaloを含み、法線の端も中央差分で評価します。
        /// spacingMetersは隣接画素間の実寸です。生成結果はメッシュに足さず法線へだけ使用します。
        /// </summary>
        public SurfaceDetailTile Generate(SurfaceDetailPoint[] points,int resolution,int bandCount,
            double spacingMeters,long availableGpuBytes){
            if(resolution<16||resolution>512||bandCount<1||bandCount>8||
                !double.IsFinite(spacingMeters)||spacingMeters<=0){throw new ArgumentOutOfRangeException(nameof(resolution));}
            int size=resolution+2;
            if(points==null||points.Length!=size*size*bandCount){throw new ArgumentException("halo付きGPU入力寸法が不正です。");}
            long outputBytes=(long)size*size*4+(long)resolution*resolution*16;
            long inputBytes=(long)points.Length*40;
            if(inputBytes+outputBytes>availableGpuBytes){throw new InvalidOperationException("GPU生成作業領域の予算が不足しています。");}
            if(!SystemInfo.supportsComputeShaders||
                !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RFloat)||
                !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBFloat)){
                throw new NotSupportedException("RFloat/ARGBFloat storage textureの対応確認が必要です。");
            }
            var tile=new SurfaceDetailTile{GpuBytes=outputBytes};
            try{
                tile.Height=Create(size,RenderTextureFormat.RFloat,"Surface residual height");
                tile.NormalRoughness=Create(resolution,RenderTextureFormat.ARGBFloat,"Surface residual normal roughness");
                using(var buffer=new ComputeBuffer(points.Length,40,ComputeBufferType.Structured)){
                    buffer.SetData(points);
                    this.shader.SetInt("_Size",size);
                    this.shader.SetInt("_Bands",bandCount);
                    this.shader.SetFloat("_Spacing",(float)spacingMeters);
                    int heightKernel=this.shader.FindKernel("Height");
                    this.shader.SetBuffer(heightKernel,"_Points",buffer);
                    this.shader.SetTexture(heightKernel,"_Height",tile.Height);
                    this.shader.Dispatch(heightKernel,(size+7)/8,(size+7)/8,1);
                    int normalKernel=this.shader.FindKernel("Normal");
                    this.shader.SetTexture(normalKernel,"_HeightInput",tile.Height);
                    this.shader.SetTexture(normalKernel,"_NormalRoughness",tile.NormalRoughness);
                    this.shader.Dispatch(normalKernel,(resolution+7)/8,(resolution+7)/8,1);
                }
                return tile;
            }catch{
                tile.Dispose();throw;
            }
        }

        private static RenderTexture Create(int size,RenderTextureFormat format,string name){
            var texture=new RenderTexture(size,size,0,format,RenderTextureReadWrite.Linear){
                name=name,enableRandomWrite=true,wrapMode=TextureWrapMode.Clamp,
                filterMode=FilterMode.Bilinear,hideFlags=HideFlags.DontSave
            };
            if(!texture.Create()){SurfaceDetailTile.DestroyTransient(texture);throw new InvalidOperationException("GPUタイル作成に失敗しました。");}
            return texture;
        }
    }
}
