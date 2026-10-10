using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualizedGeometry{
    /// <summary>instance/cameraごとに可視index、indirect args、LOD履歴、統計、streaming feedbackを保持する。</summary>
    internal sealed class VirtualGeometrySelectionState:IDisposable{
        public readonly GraphicsBuffer VisibleIndexBuffer;public readonly GraphicsBuffer ArgumentsBuffer;public readonly GraphicsBuffer RefinementStateBuffer;
        public readonly GraphicsBuffer SelectionFlagsBuffer;public readonly GraphicsBuffer CullingStatisticsBuffer;
        // 共有pageのbufferと同時readback上限を全instance×cameraで共有すると、後に描画するcameraの要求が恒常的に捨てられる。
        public readonly GraphicsBuffer FeedbackBuffer;public int PendingFeedback;public bool Disposed{get;private set;}
        public readonly Camera Camera;private readonly long baseBytes;public GraphicsBuffer DebugBuffer{get;private set;}
        public long Bytes=>this.baseBytes+(this.DebugBuffer!=null?(long)this.DebugBuffer.count*16:0);
        private readonly int nodeCount;private readonly int materialCount;private readonly int capacity;
        private Matrix4x4 feedbackView,feedbackProjection;private bool cameraKnown;
        /// <summary>cameraが動いた回数。feedbackは出した時点の値を持ち、返ってきた時点で動いていれば「静止して要求なし」の判定に使わない。</summary>
        public int MoveStamp{get;private set;}
        /// <summary>直近の完了したfeedbackが、新しい要求も品質目標の未達も含まなかったか。cameraが動くとfalseに戻る。</summary>
        public bool FeedbackQuiet;
        /// <summary>
        /// このinstance×cameraでGPU feedbackのreadbackを今frame出すか。readbackは1回ごとにD3D12のstaging resourceを作るため、部品数×camera数で毎frame出すと描画時間の大半になる。
        /// cameraが止まり、直近のfeedbackが空で、読込中のpageもなければ出さない。動いている間や読込中は、instanceごとにframeをずらして FeedbackInterval frameに1回出す
        /// (RequestTimeoutFrames=120、優先度の窓=8 frameに対して十分短い)。
        /// </summary>
        public bool ShouldRequestFeedback(int stagger,bool streamingActive){
            Camera camera=this.Camera;
            if(camera==null){return true;}
            Matrix4x4 view=camera.worldToCameraMatrix;Matrix4x4 projection=camera.projectionMatrix;
            bool moved=!this.cameraKnown||view!=this.feedbackView||projection!=this.feedbackProjection;
            this.feedbackView=view;this.feedbackProjection=projection;this.cameraKnown=true;
            if(moved){this.MoveStamp++;this.FeedbackQuiet=false;}
            if(!moved&&this.FeedbackQuiet&&!streamingActive){return false;}
            // 間隔はこの状態が描かれた回数で数える(Time.frameCountは検証や複数cameraの描画で進み方が揃わない)。instanceごとにずらすと、同じframeに全部品が一斉に出さない。
            this.feedbackTicks++;
            return (this.feedbackTicks+stagger)%FeedbackInterval==0;
        }
        private const int FeedbackInterval=4;
        private int feedbackTicks;
        public VirtualGeometrySelectionState(VirtualGeometryGpuPage page,Camera camera){
            this.Camera=camera;this.nodeCount=page.NodeCount;this.materialCount=page.MaterialCount;this.capacity=Math.Max(1,page.VisibleIndexCapacity);
            try{
                this.VisibleIndexBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,this.capacity,4);
                this.ArgumentsBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Raw|GraphicsBuffer.Target.IndirectArguments,this.materialCount*4,4);
                this.RefinementStateBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,this.nodeCount,4);
                this.SelectionFlagsBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,this.nodeCount,4);
                this.CullingStatisticsBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,4,4);
                int feedbackWords=4;if(page.IsStreaming){feedbackWords=checked(4+page.RequestCapacity*4+page.PageCount*2);}
                this.FeedbackBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,feedbackWords,4);this.FeedbackBuffer.SetData(new uint[feedbackWords]);
                this.RefinementStateBuffer.SetData(new uint[this.nodeCount]);this.SelectionFlagsBuffer.SetData(new uint[this.nodeCount]);
                this.ArgumentsBuffer.SetData(new uint[this.materialCount*4]);this.CullingStatisticsBuffer.SetData(new uint[4]);
                this.DebugBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,1,16);
                this.baseBytes=(long)this.capacity*4+(long)this.materialCount*16+(long)this.nodeCount*8+16+(long)feedbackWords*4;
            }catch{this.Dispose();throw;}
        }
        public void SetDebug(bool enabled){int count=enabled?this.capacity:1;if(this.DebugBuffer.count!=count){this.DebugBuffer.Dispose();this.DebugBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,count,16);}}
        /// <summary>Monitor用の非同期readback。失敗・破棄時はnullを渡し、通常描画へGPU同期を入れない。</summary>
        public bool RequestReadback(Action<VirtualGeometryGpuSelectionSnapshot> completed){
            if(this.Disposed||!SystemInfo.supportsAsyncGPUReadback){return false;}
            uint[][] parts=new uint[3][];int remaining=3;bool failed=false;
            void Receive(int part,AsyncGPUReadbackRequest request){
                if(request.hasError||this.Disposed){failed=true;}else{parts[part]=request.GetData<uint>().ToArray();}
                remaining--;
                if(remaining!=0){return;}
                if(failed){completed(null);}else{completed(new VirtualGeometryGpuSelectionSnapshot(parts[0],parts[1],Array.Empty<uint>(),parts[2]));}
            }
            AsyncGPUReadback.Request(this.SelectionFlagsBuffer,request=>Receive(0,request));
            AsyncGPUReadback.Request(this.ArgumentsBuffer,request=>Receive(1,request));
            AsyncGPUReadback.Request(this.CullingStatisticsBuffer,request=>Receive(2,request));
            return true;
        }
        public VirtualGeometryGpuSelectionSnapshot Readback(){
            uint[] flags=new uint[this.nodeCount];uint[] arguments=new uint[this.materialCount*4];uint[] visible=new uint[this.capacity];uint[] statistics=new uint[4];
            this.SelectionFlagsBuffer.GetData(flags);this.ArgumentsBuffer.GetData(arguments);this.VisibleIndexBuffer.GetData(visible);this.CullingStatisticsBuffer.GetData(statistics);
            return new VirtualGeometryGpuSelectionSnapshot(flags,arguments,visible,statistics);
        }
        public void Dispose(){this.Disposed=true;this.FeedbackBuffer?.Dispose();this.DebugBuffer?.Dispose();this.DebugBuffer=null;this.VisibleIndexBuffer?.Dispose();this.ArgumentsBuffer?.Dispose();this.RefinementStateBuffer?.Dispose();this.SelectionFlagsBuffer?.Dispose();this.CullingStatisticsBuffer?.Dispose();}
    }

    /// <summary>asset/content/streaming設定が同じinstance間でgeometryとI/O/cacheを共有する。</summary>
    internal static class VirtualGeometryGpuResourceCache{
        private static readonly VirtualGeometryReferenceCache<VirtualGeometryGpuPage> cache=new VirtualGeometryReferenceCache<VirtualGeometryGpuPage>();
        public static int Count=>cache.ResourceCount;
        public static string Key(VirtualGeometryMeshAsset asset,VirtualGeometryStreamingMode mode,VirtualGeometryStreamingSettings settings){
            return asset.PageRelativePath+"|"+asset.ContentHash+"|"+mode+"|"+JsonUtility.ToJson(settings);
        }
        public static VirtualGeometryReferenceCache<VirtualGeometryGpuPage>.Lease Acquire(VirtualGeometryMeshAsset asset,VirtualGeometryStreamingMode mode,VirtualGeometryStreamingSettings settings){
            return cache.Acquire(Key(asset,mode,settings),()=>VirtualGeometryGpuPage.Load(asset,mode,settings));
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset(){cache.Reset();}
    }
}