using System;

namespace Omni.PlanetSurface{
    public sealed partial class PlanetSurfaceLod{
        private long nodeCpuReservedBytes,nodeGpuReservedBytes,baseCpuReservedBytes,baseGpuReservedBytes;
        private long peakCpuReservedBytes,peakGpuReservedBytes;
        private SurfaceMemoryBudgetEstimate worstCaseMemoryBudget;

        /// <summary>現在の基礎データ・cache・LOD・法線・岩を合算したCPU予約量です。</summary>
        public long SurfaceCpuReservedBytes=>checked(this.baseCpuReservedBytes+this.nodeCpuReservedBytes+
            this.DetailCpuReservedBytes+this.RockCpuReservedBytes+this.CurrentAnchorCacheBytes);
        /// <summary>現在の基礎カラー・LOD・法線・岩を合算したGPU予約量です。</summary>
        public long SurfaceGpuReservedBytes=>checked(this.baseGpuReservedBytes+this.nodeGpuReservedBytes+
            this.DetailGpuReservedBytes+this.RockGpuReservedBytes);
        public long PeakSurfaceCpuReservedBytes=>this.peakCpuReservedBytes;
        public long PeakSurfaceGpuReservedBytes=>this.peakGpuReservedBytes;
        public SurfaceMemoryBudgetEstimate WorstCaseMemoryBudget=>this.worstCaseMemoryBudget;
        public bool SurfaceMemoryWithinBudget=>this.SurfaceCpuReservedBytes<=this.m_Profile.CpuBudgetBytes&&
            this.SurfaceGpuReservedBytes<=this.m_Profile.GpuBudgetBytes;

        private long CurrentAnchorCacheBytes=>this.m_HeightSource is IProceduralSurfaceHeightSource procedural
            ?procedural.NaturalSource.AnchorCacheBytes:0L;

        private void InitializeMemoryBudget(long additionalBaseCpuBytes){
            var procedural=this.m_HeightSource as IProceduralSurfaceHeightSource;
            bool overrides=procedural?.HasHeightOverrides??false;
            int albedoWidth=this.m_Definition.BaseAlbedo!=null?this.m_Definition.BaseAlbedo.width:1;
            int albedoHeight=this.m_Definition.BaseAlbedo!=null?this.m_Definition.BaseAlbedo.height:1;
            int bands=procedural?.NaturalSource.BandCount??this.m_Definition.Generation.BandCount;
            // 基礎の標高データを持たない天体（標高 0 の ZeroSurfaceHeightSource など）は、1×1 として数えます。
            int heightWidth=Math.Max(1,this.m_Definition.HeightWidth),heightRows=Math.Max(1,this.m_Definition.HeightHeight);
            this.worstCaseMemoryBudget=SurfaceMemoryBudget.EstimateWorstCase(this.m_Profile,
                this.m_Definition.GridResolution,heightWidth,heightRows,
                albedoWidth,albedoHeight,additionalBaseCpuBytes,bands,overrides);
            this.worstCaseMemoryBudget.ThrowIfExceeded(this.m_Definition.BodyId);
            this.baseCpuReservedBytes=checked((long)heightWidth*heightRows*4L+
                additionalBaseCpuBytes+1024L*1024L+
                SurfacePatchTransitionWorkspace.EstimateCpuBytes(
                    (this.m_Definition.GridResolution+1)*(this.m_Definition.GridResolution+1)+
                    4*(this.m_Definition.GridResolution+1),
                    SurfacePatchTransitionWorkspace.RequiredPatchCapacity(this.m_Profile.MaximumVisiblePatches)));
            this.baseGpuReservedBytes=checked(((long)albedoWidth*albedoHeight*16L+2L)/3L+256L*1024L);
            this.nodeCpuReservedBytes=0;this.nodeGpuReservedBytes=0;
            this.peakCpuReservedBytes=0;this.peakGpuReservedBytes=0;
            this.UpdateMemoryPeak();
        }

        private void RegisterNodeMemory(Node node,SurfacePatchBuilder geometry){
            if(node==null||geometry==null){throw new ArgumentNullException(nameof(node));}
            SurfaceMemoryBudget.GetPatchReservation(geometry.Resolution,out node.CpuReservedBytes,out node.GpuReservedBytes);
            this.nodeCpuReservedBytes=checked(this.nodeCpuReservedBytes+node.CpuReservedBytes);
            this.nodeGpuReservedBytes=checked(this.nodeGpuReservedBytes+node.GpuReservedBytes);
            this.UpdateMemoryPeak();
        }

        private void ReleaseNodeMemory(Node node){
            if(node==null||(node.CpuReservedBytes==0&&node.GpuReservedBytes==0)){return;}
            this.nodeCpuReservedBytes-=node.CpuReservedBytes;this.nodeGpuReservedBytes-=node.GpuReservedBytes;
            node.CpuReservedBytes=0;node.GpuReservedBytes=0;
        }

        private void UpdateMemoryPeak(){
            if(this.m_Profile==null){return;}
            long cpu=this.SurfaceCpuReservedBytes,gpu=this.SurfaceGpuReservedBytes;
            this.peakCpuReservedBytes=Math.Max(this.peakCpuReservedBytes,cpu);
            this.peakGpuReservedBytes=Math.Max(this.peakGpuReservedBytes,gpu);
            if(cpu>this.m_Profile.CpuBudgetBytes||gpu>this.m_Profile.GpuBudgetBytes){
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Runtime surface memory budget exceeded: CPU {cpu}/{this.m_Profile.CpuBudgetBytes}, GPU {gpu}/{this.m_Profile.GpuBudgetBytes}."));
            }
        }

        private void ResetMemoryBudget(){
            this.nodeCpuReservedBytes=0;this.nodeGpuReservedBytes=0;
            this.baseCpuReservedBytes=0;this.baseGpuReservedBytes=0;
        }
    }
}
