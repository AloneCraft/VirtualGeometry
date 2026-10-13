using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Omni.PlanetSurface{
    /// <summary>WebGPUでも16バイト境界が揃う、帯域ごとの整数原点と小座標です。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SurfaceDetailBand{
        public int CellX,CellY,CellZ;
        public uint Seed;
        public Vector4 LocalAmplitude;
        public Vector4 Parameters;
    }

    /// <summary>近傍タイルの球面位置を、double原点とGPU用の小さな2次差分で表します。</summary>
    public sealed class SurfaceDetailPatchFrame{
        public SurfaceTileKey Key{get;}
        public double RadiusMeters{get;}
        public double Span{get;}
        public SurfaceVector Origin{get;}
        public Vector3 Du{get;}
        public Vector3 Dv{get;}
        public Vector3 Duu{get;}
        public Vector3 Duv{get;}
        public Vector3 Dvv{get;}
        public SurfaceDetailPatchFrame(SurfaceTileKey key,double radius){
            if(key.Face<0||key.Face>5||key.Level<0||key.Level>20||key.X<0||key.Y<0||
                key.X>=(1<<key.Level)||key.Y>=(1<<key.Level)||!double.IsFinite(radius)||radius<=0){
                throw new ArgumentOutOfRangeException(nameof(key));
            }
            this.Key=key;this.RadiusMeters=radius;this.Span=2.0/(1<<key.Level);
            if(this.Span>0.001||this.Span*radius>64){throw new ArgumentOutOfRangeException(nameof(key),"近傍タイルだけに使用してください。");}
            this.Origin=this.Direction(0.5,0.5)*radius;
            double u=-1+this.Span*(key.X+0.5),v=-1+this.Span*(key.Y+0.5);
            SurfaceVector raw,eu,ev;
            switch(key.Face){
                case 0:raw=new SurfaceVector(u,1,v);eu=new SurfaceVector(1,0,0);ev=new SurfaceVector(0,0,1);break;
                case 1:raw=new SurfaceVector(u,-1,v);eu=new SurfaceVector(1,0,0);ev=new SurfaceVector(0,0,1);break;
                case 2:raw=new SurfaceVector(1,u,v);eu=new SurfaceVector(0,1,0);ev=new SurfaceVector(0,0,1);break;
                case 3:raw=new SurfaceVector(-1,u,v);eu=new SurfaceVector(0,1,0);ev=new SurfaceVector(0,0,1);break;
                case 4:raw=new SurfaceVector(u,v,1);eu=new SurfaceVector(1,0,0);ev=new SurfaceVector(0,1,0);break;
                default:raw=new SurfaceVector(u,v,-1);eu=new SurfaceVector(1,0,0);ev=new SurfaceVector(0,1,0);break;
            }
            double length=raw.Length;
            SurfaceVector n=raw/length;
            double a=n.X*eu.X+n.Y*eu.Y+n.Z*eu.Z,b=n.X*ev.X+n.Y*ev.Y+n.Z*ev.Z;
            double first=radius*this.Span/length,second=radius*this.Span*this.Span/(length*length);
            this.Du=Float((eu-n*a)*first);this.Dv=Float((ev-n*b)*first);
            this.Duu=Float((eu*(-2*a)+n*(3*a*a-1))*(second*0.5));
            this.Duv=Float((eu*(-b)+ev*(-a)+n*(3*a*b))*second);
            this.Dvv=Float((ev*(-2*b)+n*(3*b*b-1))*(second*0.5));
        }
        /// <summary>タイル内[0,1]座標。haloのため範囲外も同じ球面上で評価します。</summary>
        public SurfaceVector Direction(double u,double v)=>SurfaceGeometry.CubeDirection(this.Key.Face,
            -1+this.Span*(this.Key.X+u),-1+this.Span*(this.Key.Y+v));
        /// <summary>GPUと同じfloat差分です。絶対位置はGPUへ渡しません。</summary>
        public Vector3 RelativePosition(float u,float v){
            u-=0.5f;v-=0.5f;
            return this.Du*u+this.Dv*v+this.Duu*(u*u)+this.Duv*(u*v)+this.Dvv*(v*v);
        }
        private static Vector3 Float(SurfaceVector p)=>new((float)p.X,(float)p.Y,(float)p.Z);
    }

    /// <summary>正本の低周波補正だけを1画素ずつ準備します。微細な地形はGPUで生成します。</summary>
    public sealed class SurfaceDetailPatchRequest{
        /// <summary>
        /// 約16m領域内で滑らかな基準補間と造成フェザーを表すCPU格子の最大区間数です。
        /// 描画法線の解像度は下げず、Compute Shaderで出力画素へ連続補間します。
        /// </summary>
        public const int MaximumCorrectionResolution=64;
        private readonly Func<SurfaceVector,double> sampleCorrection;
        private readonly IProceduralSurfaceHeightSource source;
        private int cursor;
        public SurfaceDetailPatchFrame Frame{get;}
        public SurfaceDetailBand[] Bands{get;}
        public float[] Corrections{get;}
        /// <summary>造成がある場合だけ確保する自然地形の重みです。自然地形だけなら追加メモリを使いません。</summary>
        public float[] NaturalWeights{get;}
        public int Resolution{get;}
        public int HeightSize=>this.Resolution+4;
        public int CorrectionSize=>GetCorrectionSize(this.Resolution);
        public double MeshSpacingMeters{get;}
        public double NormalSpacingMeters{get;}
        /// <summary>trueならメッシュ法線へ加算せず、天体ローカルの最終法線を生成します。</summary>
        public bool FullSurfaceNormals{get;}
        /// <summary>RFloat局所高さの基準標高メートルです。残差モードでは0です。</summary>
        public double ReferenceHeightMeters{get;}
        public bool IsComplete=>this.cursor==this.Corrections.Length;
        public int CompletedPixels=>this.cursor;
        /// <summary>GPUへ転送するpayloadです。CPU補間係数や管理オブジェクトは別途予算化します。</summary>
        public long InputBytes=>this.Corrections.Length*4L+this.Bands.Length*48L+(this.NaturalWeights?.Length??0)*4L;
        /// <summary>resolutionは有効法線解像度、meshSpacingはメッシュ間隔メートルです。</summary>
        public SurfaceDetailPatchRequest(IProceduralSurfaceHeightSource source,SurfaceTileKey key,double radius,
            int resolution,double meshSpacing,bool fullSurfaceNormals=false){
            if(source==null||resolution<16||resolution>512||!double.IsFinite(meshSpacing)||meshSpacing<=0||
                radius!=source.NaturalSource.RadiusMeters){throw new ArgumentOutOfRangeException(nameof(resolution));}
            this.source=source;
            var natural=source.NaturalSource;
            this.Frame=new SurfaceDetailPatchFrame(key,radius);this.Resolution=resolution;
            this.FullSurfaceNormals=fullSurfaceNormals;
            if(fullSurfaceNormals){this.ReferenceHeightMeters=source.SampleHeightBlend(this.Frame.Origin).Apply(natural.SampleBaseHeightMeters(this.Frame.Origin));}
            this.MeshSpacingMeters=meshSpacing;this.NormalSpacingMeters=radius*this.Frame.Span/resolution;
            if(this.NormalSpacingMeters>meshSpacing){throw new ArgumentOutOfRangeException(nameof(meshSpacing));}
            this.sampleCorrection=natural.CreateDetailCorrectionSampler(meshSpacing,this.NormalSpacingMeters,
                fullSurfaceNormals,this.ReferenceHeightMeters);
            this.Bands=new SurfaceDetailBand[natural.BandCount];
            this.Corrections=new float[this.CorrectionSize*this.CorrectionSize];
            if(source.HasHeightOverrides){this.NaturalWeights=new float[this.Corrections.Length];}
            for(int i=0;i<this.Bands.Length;i++){
                this.Bands[i]=natural.PrepareDetailBand(this.Frame.Origin,i,meshSpacing,this.NormalSpacingMeters,fullSurfaceNormals);
            }
        }
        /// <summary>補正1画素だけを準備します。完成前のGPU生成は拒否されます。</summary>
        public bool Step(){
            if(this.IsComplete){return false;}
            SurfaceVector p=this.CorrectionDirection(
                this.cursor%this.CorrectionSize,
                this.cursor/this.CorrectionSize);
            if(this.NaturalWeights==null){this.Corrections[this.cursor]=(float)this.sampleCorrection(p);}
            else{
                var blend=this.source.SampleHeightBlend(p);
                double correction=0;
                if(blend.NaturalWeight>0){correction=blend.NaturalWeight*this.sampleCorrection(p);}
                // GPUで -correction+w*P を計算し、合成標高から局所基準を引いた高さを得ます。
                if(this.FullSurfaceNormals){correction+=(1-blend.NaturalWeight)*this.ReferenceHeightMeters-blend.OffsetHeightMeters;}
                this.Corrections[this.cursor]=(float)correction;
                this.NaturalWeights[this.cursor]=(float)blend.NaturalWeight;
            }
            this.cursor++;return true;
        }
        /// <summary>高さテクスチャ画素の中心方向です。法線フィルタ用の外側2画素を含みます。</summary>
        public SurfaceVector PixelDirection(int x,int y)=>this.Frame.Direction((x-1.5)/this.Resolution,(y-1.5)/this.Resolution);

        /// <summary>縮小したCPU補正格子を、出力高さテクスチャと同じhalo端点へ対応付けます。</summary>
        public SurfaceVector CorrectionDirection(int x,int y){
            if(x<0||y<0||x>=this.CorrectionSize||y>=this.CorrectionSize){
                throw new ArgumentOutOfRangeException(nameof(x));
            }
            double scale=(this.HeightSize-1.0)/(this.CorrectionSize-1.0);
            return this.Frame.Direction((x*scale-1.5)/this.Resolution,(y*scale-1.5)/this.Resolution);
        }

        /// <summary>小さい検証タイルは等倍、高解像度タイルだけ低周波CPU入力を制限します。</summary>
        public static int GetCorrectionSize(int resolution){
            if(resolution<16||resolution>512){throw new ArgumentOutOfRangeException(nameof(resolution));}
            return Math.Min(resolution+4,MaximumCorrectionResolution+4);
        }
    }
}
