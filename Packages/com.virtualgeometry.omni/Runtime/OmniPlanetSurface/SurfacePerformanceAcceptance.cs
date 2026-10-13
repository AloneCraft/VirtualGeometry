using System;

namespace Omni.PlanetSurface{
    /// <summary>連続1000フレームの地表歩行を、描画・更新・容量の固定条件で評価した結果です。</summary>
    public readonly struct SurfacePerformanceAcceptanceResult{
        public readonly int FrameCount,Width,Height,OverBudgetFrameCount;
        public readonly double DurationSeconds,AverageFramesPerSecond,MaximumUpdateMilliseconds,UpdateBudgetMilliseconds;
        public readonly long PeakCpuBytes,PeakGpuBytes,CpuBudgetBytes,GpuBudgetBytes;
        public readonly bool ResolutionPassed,FrameRatePassed,UpdateBudgetPassed,MemoryPassed,StaticBudgetPassed;
        public bool Passed=>this.ResolutionPassed&&this.FrameRatePassed&&this.UpdateBudgetPassed&&
            this.MemoryPassed&&this.StaticBudgetPassed;
        internal SurfacePerformanceAcceptanceResult(int frames,int width,int height,int overBudget,double duration,
            double maximumUpdate,long peakCpu,long peakGpu,long cpuBudget,long gpuBudget,double updateBudget,
            int targetFramesPerSecond,bool memoryPassed,bool staticBudgetPassed){
            this.FrameCount=frames;this.Width=width;this.Height=height;this.OverBudgetFrameCount=overBudget;
            this.DurationSeconds=duration;this.AverageFramesPerSecond=frames/Math.Max(duration,1e-9);
            this.MaximumUpdateMilliseconds=maximumUpdate;this.UpdateBudgetMilliseconds=updateBudget;
            this.PeakCpuBytes=peakCpu;this.PeakGpuBytes=peakGpu;this.CpuBudgetBytes=cpuBudget;this.GpuBudgetBytes=gpuBudget;
            this.ResolutionPassed=width==1920&&height==1080;
            this.FrameRatePassed=this.AverageFramesPerSecond>=targetFramesPerSecond*0.95;
            this.UpdateBudgetPassed=overBudget==0;this.MemoryPassed=memoryPassed;
            this.StaticBudgetPassed=staticBudgetPassed;
        }
    }

    /// <summary>開始条件成立後の最初の1000フレームだけを記録し、後続値で結果を書き換えません。</summary>
    public sealed class SurfacePerformanceAcceptance{
        public const int RequiredFrameCount=1000;
        private readonly int width,height,targetFramesPerSecond;
        private readonly long cpuBudget,gpuBudget;
        private readonly double updateBudget;
        private readonly bool staticBudgetPassed;
        private int frames,overBudget;
        private double duration,maximumUpdate;
        private long peakCpu,peakGpu;
        private bool memoryPassed=true;

        public int FrameCount=>this.frames;
        public bool IsComplete=>this.frames==RequiredFrameCount;

        public SurfacePerformanceAcceptance(SurfaceQualityProfile profile,int width,int height,bool staticBudgetPassed){
            if(profile==null){throw new ArgumentNullException(nameof(profile));}
            profile.Validate();
            if(width<1||height<1){throw new ArgumentOutOfRangeException(nameof(width));}
            this.width=width;this.height=height;this.targetFramesPerSecond=profile.TargetFramesPerSecond;
            this.cpuBudget=profile.CpuBudgetBytes;this.gpuBudget=profile.GpuBudgetBytes;
            this.updateBudget=profile.UpdateBudgetMilliseconds;this.staticBudgetPassed=staticBudgetPassed;
        }

        /// <summary>trueは、この呼出しでちょうど1000フレームに達したことを示します。</summary>
        public bool AddFrame(double deltaSeconds,double updateMilliseconds,long cpuBytes,long gpuBytes,bool withinBudget){
            if(this.IsComplete){return false;}
            if(!double.IsFinite(deltaSeconds)||deltaSeconds<=0||!double.IsFinite(updateMilliseconds)||updateMilliseconds<0||
                cpuBytes<0||gpuBytes<0){throw new ArgumentOutOfRangeException(nameof(deltaSeconds));}
            this.frames++;this.duration+=deltaSeconds;
            this.maximumUpdate=Math.Max(this.maximumUpdate,updateMilliseconds);
            if(updateMilliseconds>this.updateBudget){this.overBudget++;}
            this.peakCpu=Math.Max(this.peakCpu,cpuBytes);this.peakGpu=Math.Max(this.peakGpu,gpuBytes);
            this.memoryPassed&=withinBudget&&cpuBytes<=this.cpuBudget&&gpuBytes<=this.gpuBudget;
            return this.IsComplete;
        }

        public bool TryGetResult(out SurfacePerformanceAcceptanceResult result){
            if(!this.IsComplete){result=default;return false;}
            result=new SurfacePerformanceAcceptanceResult(this.frames,this.width,this.height,this.overBudget,
                this.duration,this.maximumUpdate,this.peakCpu,this.peakGpu,this.cpuBudget,this.gpuBudget,
                this.updateBudget,this.targetFramesPerSecond,this.memoryPassed,this.staticBudgetPassed);
            return true;
        }
    }
}
