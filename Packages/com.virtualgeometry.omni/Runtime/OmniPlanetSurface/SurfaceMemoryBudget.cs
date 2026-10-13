using System;

namespace Omni.PlanetSurface{
    /// <summary>惑星表面サブシステム全体の保守的なCPU/GPU予約量です。</summary>
    public readonly struct SurfaceMemoryBudgetEstimate{
        public SurfaceMemoryBudgetEstimate(long cpuBytes,long gpuBytes,long cpuBudgetBytes,long gpuBudgetBytes,
            long maximumNodeCount,long patchCpuBytes,long patchGpuBytes){
            this.CpuBytes=cpuBytes;this.GpuBytes=gpuBytes;
            this.CpuBudgetBytes=cpuBudgetBytes;this.GpuBudgetBytes=gpuBudgetBytes;
            this.MaximumNodeCount=maximumNodeCount;this.PatchCpuBytes=patchCpuBytes;this.PatchGpuBytes=patchGpuBytes;
        }
        public long CpuBytes{get;}
        public long GpuBytes{get;}
        public long CpuBudgetBytes{get;}
        public long GpuBudgetBytes{get;}
        public long MaximumNodeCount{get;}
        public long PatchCpuBytes{get;}
        public long PatchGpuBytes{get;}
        public bool FitsCpu=>this.CpuBytes<=this.CpuBudgetBytes;
        public bool FitsGpu=>this.GpuBytes<=this.GpuBudgetBytes;
        public bool Fits=>this.FitsCpu&&this.FitsGpu;
        public void ThrowIfExceeded(string bodyId){
            if(this.Fits){return;}
            throw new InvalidOperationException(FormattableString.Invariant(
                $"{bodyId} surface memory budget exceeded: CPU {this.CpuBytes}/{this.CpuBudgetBytes}, GPU {this.GpuBytes}/{this.GpuBudgetBytes}."));
        }
    }

    /// <summary>
    /// 基礎データ、LODの表示中・非表示親・遷移中パッチ、法線atlas、岩、補正cacheを同じ予算へ合算します。
    /// GPU値は圧縮textureもRGBA32相当、Mesh indexも32bitとして数える上限側の予約値です。
    /// </summary>
    public static class SurfaceMemoryBudget{
        public const long AnchorCacheBudgetBytes=4L*1024*1024;
        private const int DetailCapacity=128;
        private const long ManagerCpuBytes=1024L*1024;
        private const long ManagerGpuBytes=256L*1024;
        private const long PatchManagedOverheadBytes=4096;
        private const long PatchGpuOverheadBytes=4096;

        public static SurfaceMemoryBudgetEstimate EstimateWorstCase(SurfaceQualityProfile profile,int gridResolution,
            int heightWidth,int heightRows,int albedoWidth,int albedoHeight,long additionalBaseCpuBytes,
            int bandCount,bool hasHeightOverrides){
            if(profile==null){throw new ArgumentNullException(nameof(profile));}
            profile.Validate();
            if(heightWidth<1||heightRows<1||albedoWidth<1||albedoHeight<1||additionalBaseCpuBytes<0||
                bandCount<1||bandCount>8){throw new ArgumentOutOfRangeException(nameof(heightWidth));}
            GetPatchReservation(gridResolution,out long patchCpu,out long patchGpu);
            long splits=(profile.MaximumVisiblePatches-6L)/3L;
            // 親は統合まで保持します。さらに分割準備4子と遅延破棄4子を同時に予約します。
            long maximumNodes=6L+splits*4L+8L;
            int vertices=checked((gridResolution+1)*(gridResolution+1)+4*(gridResolution+1));
            long transitionCpu=SurfacePatchTransitionWorkspace.EstimateCpuBytes(vertices,
                SurfacePatchTransitionWorkspace.RequiredPatchCapacity(profile.MaximumVisiblePatches));
            GetDetailReservation(profile.NormalTileResolution,DetailCapacity,bandCount,hasHeightOverrides,
                profile.MaximumVisiblePatches,out long detailCpu,out long detailGpu);
            GetRockReservation(profile,out long rockCpu,out long rockGpu);
            long baseCpu=checked((long)heightWidth*heightRows*4L+additionalBaseCpuBytes);
            // CPU非ReadableであってもGPU側は非圧縮RGBA32＋mipmap 1/3を上限として予約します。
            long albedoGpu=checked(((long)albedoWidth*albedoHeight*16L+2L)/3L);
            long cpu=checked(ManagerCpuBytes+baseCpu+maximumNodes*patchCpu+transitionCpu+
                detailCpu+rockCpu+AnchorCacheBudgetBytes);
            long gpu=checked(ManagerGpuBytes+albedoGpu+maximumNodes*patchGpu+detailGpu+rockGpu);
            return new SurfaceMemoryBudgetEstimate(cpu,gpu,profile.CpuBudgetBytes,profile.GpuBudgetBytes,
                maximumNodes,patchCpu,patchGpu);
        }

        /// <summary>保持するCPU配列と、微細座標UV1およびUV2/UV3/UV4を含む最大GPU頂点layoutを返します。</summary>
        public static void GetPatchReservation(int resolution,out long cpuBytes,out long gpuBytes){
            if(resolution<2||resolution>128||resolution%2!=0){throw new ArgumentOutOfRangeException(nameof(resolution));}
            long vertices=(resolution+1L)*(resolution+1)+4L*(resolution+1);
            long indices=(long)resolution*resolution*6L+resolution*48L;
            long edges=4L*(resolution+1);
            cpuBytes=checked(vertices*60L+indices*4L+edges*52L+PatchManagedOverheadBytes);
            gpuBytes=checked(vertices*108L+indices*4L+PatchGpuOverheadBytes);
        }

        private static void GetDetailReservation(int resolution,int capacity,int bandCount,bool hasHeightOverrides,
            int maximumLeases,out long cpuBytes,out long gpuBytes){
            int correctionSize=SurfaceDetailPatchRequest.GetCorrectionSize(resolution);
            long correction=(long)correctionSize*correctionSize*4L;
            long input=correction+bandCount*48L+(hasHeightOverrides?correction:0L);
            long normal=(resolution+2L)*(resolution+2L)*8L;
            long output=(resolution+4L)*(resolution+4L)*4L+normal;
            cpuBytes=checked(4096L+capacity*512L+input+8192L+maximumLeases*96L);
            gpuBytes=checked(normal*capacity+input+output);
        }

        private static void GetRockReservation(SurfaceQualityProfile profile,out long cpuBytes,out long gpuBytes){
            const long cpuFixed=102400L;
            int capacity=(int)Math.Min(profile.MaximumVisibleRocks,
                Math.Min(Math.Max(0L,profile.CpuBudgetBytes/8L-cpuFixed)/32768L,
                    Math.Max(0L,profile.GpuBudgetBytes/16L-4096L)/16384L));
            if(capacity<=0){cpuBytes=0;gpuBytes=0;return;}
            cpuBytes=cpuFixed+32768L*capacity;
            gpuBytes=4096L+16384L*capacity;
        }
    }
}
