using System;
using System.Threading;
using System.Threading.Tasks;

namespace VirtualizedGeometry.Editor{
    /// <summary>snapshotからUnityオブジェクトへ触れずに変換する。Cancel後にasset commitしない。</summary>
    public sealed class VirtualGeometryConversionJob:IDisposable{
        private readonly CancellationTokenSource cancellation;private readonly Task<VirtualGeometryBuildOutput> task;private float progress;
        public bool IsCompleted=>this.task.IsCompleted;public bool IsCancelled=>this.task.IsCanceled||this.cancellation.IsCancellationRequested;
        public float Progress=>Volatile.Read(ref this.progress);
        private VirtualGeometryConversionJob(VirtualGeometryMeshInput input,VirtualGeometryBuildSettings settings,CancellationToken token){
            this.cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
            this.task=Task.Run(()=>VirtualGeometryBuilder.Build(input,settings,this.cancellation.Token,value=>Volatile.Write(ref this.progress,value)),this.cancellation.Token);
        }
        public static VirtualGeometryConversionJob StartSnapshot(VirtualGeometryMeshInput input,VirtualGeometryBuildSettings settings,CancellationToken token=default){
            return new VirtualGeometryConversionJob(input,settings.Clone(),token);
        }
        public void Cancel(){this.cancellation.Cancel();}
        public VirtualGeometryBuildOutput GetOutput(){
            this.cancellation.Token.ThrowIfCancellationRequested();
            if(!this.task.IsCompleted){throw new InvalidOperationException("Conversion is still running.");}
            return this.task.GetAwaiter().GetResult();
        }
        public void Dispose(){
            this.Cancel();this.task.ContinueWith(task=>{if(task.IsFaulted){var observed=task.Exception;}this.cancellation.Dispose();},
                CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
    }
}