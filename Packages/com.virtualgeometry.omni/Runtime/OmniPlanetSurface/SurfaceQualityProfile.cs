using System;
using UnityEngine;

namespace Omni.PlanetSurface{
    /// <summary>描画・生成の予算です。seedや地形固有パラメータは含めません。</summary>
    [Serializable]
    public sealed class SurfaceQualityProfile{
        public double MinimumMeshSpacingMeters=0.25;
        public int NormalTileResolution=256;
        public int MaximumVisiblePatches=2048;
        public long CpuBudgetBytes=256L*1024*1024;
        public long GpuBudgetBytes=512L*1024*1024;
        public double UpdateBudgetMilliseconds=2;
        public double MorphDurationSeconds=0.12;
        public double TargetCellPixels=6;
        public int TargetFramesPerSecond=60;
        public int MaximumVisibleRocks=1024;
        public double RockDrawDistanceMeters=80;

        /// <summary>Windows標準の初期予算を返します。</summary>
        public static SurfaceQualityProfile Windows()=>new();

        /// <summary>Windows Chrome/EdgeのWebGPU標準予算を返します。</summary>
        public static SurfaceQualityProfile WebGpu()=>new(){
            MinimumMeshSpacingMeters=0.5,NormalTileResolution=128,MaximumVisiblePatches=512,
            CpuBudgetBytes=128L*1024*1024,GpuBudgetBytes=128L*1024*1024,TargetFramesPerSecond=30,
            MaximumVisibleRocks=256,RockDrawDistanceMeters=40
        };

        /// <summary>プラットフォームで予算だけを選択します。地形の正本は変更しません。</summary>
        public static SurfaceQualityProfile Current(){
            if(Application.platform==RuntimePlatform.WebGLPlayer){return WebGpu();}
            return Windows();
        }

        /// <summary>更新ループへ不正な予算が入ることを防ぎます。</summary>
        public void Validate(){
            if(!double.IsFinite(this.MinimumMeshSpacingMeters)||this.MinimumMeshSpacingMeters<=0||
                this.NormalTileResolution<16||this.MaximumVisiblePatches<6||
                this.CpuBudgetBytes<=0||this.GpuBudgetBytes<=0||
                !double.IsFinite(this.UpdateBudgetMilliseconds)||this.UpdateBudgetMilliseconds<=0||
                !double.IsFinite(this.MorphDurationSeconds)||this.MorphDurationSeconds<0||
                !double.IsFinite(this.TargetCellPixels)||this.TargetCellPixels<=0||
                this.MaximumVisibleRocks<0||this.MaximumVisibleRocks>1024||
                !double.IsFinite(this.RockDrawDistanceMeters)||this.RockDrawDistanceMeters<=0){
                throw new ArgumentOutOfRangeException(nameof(SurfaceQualityProfile));
            }
        }
    }

    /// <summary>正本の識別情報を含む派生タイルキーです。旧設定のキャッシュを再利用しません。</summary>
    public readonly struct SurfaceDerivedKey:IEquatable<SurfaceDerivedKey>{
        public readonly string SourceHash;
        public readonly string GenerationHash;
        public readonly int Seed;
        public readonly int Version;
        public readonly SurfaceTileKey Region;
        public readonly int Resolution;
        public readonly int Band;
        /// <summary>GenerationHashには半径・生成設定の正規化ハッシュを渡します。</summary>
        public SurfaceDerivedKey(string sourceHash,string generationHash,int seed,int version,
            SurfaceTileKey region,int resolution,int band){
            if(string.IsNullOrWhiteSpace(sourceHash)||string.IsNullOrWhiteSpace(generationHash)){
                throw new ArgumentException("正本と生成設定のハッシュが必要です。");
            }
            this.SourceHash=sourceHash;this.GenerationHash=generationHash;this.Seed=seed;
            this.Version=version;this.Region=region;this.Resolution=resolution;this.Band=band;
        }
        public bool Equals(SurfaceDerivedKey other)=>this.SourceHash==other.SourceHash&&
            this.GenerationHash==other.GenerationHash&&this.Seed==other.Seed&&this.Version==other.Version&&
            this.Region.Equals(other.Region)&&this.Resolution==other.Resolution&&this.Band==other.Band;
        public override bool Equals(object obj)=>obj is SurfaceDerivedKey other&&this.Equals(other);
        public override int GetHashCode()=>HashCode.Combine(this.SourceHash,this.GenerationHash,this.Seed,
            this.Version,this.Region,this.Resolution,this.Band);
    }
}
