using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VirtualizedGeometry{
    /// <summary>M4の独立予算。上限を実行時に自動拡大しない。</summary>
    [Serializable]
    public sealed class VirtualGeometryStreamingSettings{
        [Tooltip("GPUに置くgeometryの上限(byte)。同じassetを使うinstance間で共有します。")] public long GpuGeometryBudgetBytes=64L*1024*1024;
        [Tooltip("読み込んだpageをCPU側に保持する上限(byte)。")] public long CpuCacheBudgetBytes=64L*1024*1024;
        [Tooltip("同時に読み込むpage数。")] public int IoConcurrency=2;
        [Tooltip("1 frameにGPUへ転送する量の上限(byte)。")] public int TransferBytesPerFrame=1024*1024;
        [Tooltip("読み込み待ちの要求の上限。")] public int RequestQueueCapacity=256;
        [Tooltip("要求されなくなったpageの読み込みを取り消すまでのframe数。")] public int RequestTimeoutFrames=120;
        public int IoDelayMilliseconds;
        public VirtualGeometryStreamingSettings Clone(){return (VirtualGeometryStreamingSettings)this.MemberwiseClone();}
    }

    public enum VirtualGeometryPageState{Missing,Queued,Loading,Cached,Uploading,Resident,Retiring,Failed,Cancelled}

    /// <summary>geometry予算とmetadataを区別したM4実測値。</summary>
    public sealed class VirtualGeometryStreamingStatistics{
        public long GpuAllocatedBytes;public long GpuResidentBytes;public long PeakGpuResidentBytes;public long GpuMetadataBytes;
        public long CpuBytes;public long PeakCpuBytes;public long CpuMetadataBytes;public long IoBytes;
        public long Requests;public long ReRequests;public long Deduplicated;public long Misses;public long QueueOverflow;public long Evictions;public long Cancellations;
        public long QualityTargetMisses;public long UnsafeSlotReuses;public long CompletedSubmission;public long LastRetirementSubmission;public long LastReuseRetirementSubmission;public long LastReuseCompletedSubmission;public int LastReuseGeneration;
        public int Queued;public int Loading;public int Uploading;public int Resident;public int Failed;
        public int TransferBytesThisFrame;public int PeakTransferBytesPerFrame;public int PeakIoConcurrency;
        public string LastError;
    }

    public interface IVirtualGeometryPageStore{
        Task<VirtualGeometryPagePayload> ReadAsync(int page,CancellationToken token);
    }

    /// <summary>
    /// 実GPU転送とstate-machine試験の境界。公開前のpartial slotは描画対象にしない。
    /// slotはpageが使う連続slotの先頭。offsetはそこからの頂点・添字の位置で、pageの全体が連続した範囲に入る。
    /// </summary>
    public interface IVirtualGeometryPageUploader{
        void UploadVertices(int slot,int offset,VirtualGeometryPackedVertex[] vertices,int sourceOffset,int count);
        void UploadIndices(int slot,int offset,uint[] indices,int sourceOffset,int count);
        void Publish(int page,int slot,int generation);
        void Invalidate(int page);
    }

    /// <summary>指定offset/sizeだけを非同期FileStreamで読む。遅延注入は検証用。</summary>
    public sealed class VirtualGeometryFilePageStore:IVirtualGeometryPageStore{
        private readonly string path;private readonly VirtualGeometryStreamMetadata metadata;private readonly int delayMilliseconds;
        private long bytesRead;
        public long BytesRead=>Interlocked.Read(ref this.bytesRead);
        public VirtualGeometryFilePageStore(string path,VirtualGeometryStreamMetadata metadata,int delayMilliseconds=0){
            this.path=path;this.metadata=metadata;this.delayMilliseconds=Math.Max(0,delayMilliseconds);
        }
        public async Task<VirtualGeometryPagePayload> ReadAsync(int page,CancellationToken token){
            if(page<0||page>=this.metadata.Pages.Length){throw new InvalidDataException("Requested page is outside metadata.");}
            var descriptor=this.metadata.Pages[page];
            if(this.delayMilliseconds>0){await Task.Delay(this.delayMilliseconds,token).ConfigureAwait(false);}
            byte[] bytes=new byte[descriptor.ByteCount];
            using(var stream=new FileStream(this.path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.Asynchronous|FileOptions.SequentialScan)){
                if(descriptor.Offset<0||descriptor.Offset>stream.Length-descriptor.ByteCount){throw new EndOfStreamException("Requested page range is missing.");}
                stream.Seek(descriptor.Offset,SeekOrigin.Begin);int read=0;
                while(read<bytes.Length){
                    int count=await stream.ReadAsync(bytes,read,bytes.Length-read,token).ConfigureAwait(false);
                    if(count==0){throw new EndOfStreamException("Requested geometry page is truncated.");}
                    read+=count;Interlocked.Add(ref this.bytesRead,count);
                }
            }
            token.ThrowIfCancellationRequested();
            return VirtualGeometryStreamCodec.DecodePayload(bytes,descriptor);
        }
    }

    /// <summary>
    /// 小容量要求queue、実async I/O、CPU cache、chunk転送、GPU完了後のslot再利用を管理する。
    /// GPU poolはslot(metadataの割り当て単位)に区切り、pageは自分の大きさに要る数だけ連続したslotを使う。root(page 0)は先頭のslotに置く。
    /// </summary>
    public sealed class VirtualGeometryStreamingController:IDisposable{
        private sealed class Entry{
            public VirtualGeometryPageState State;public float Priority;public long LastRequested;public int Slot=-1;public int SlotCount;
            public bool Retiring;public long RetireAfter;public int Generation;
            public long PriorityWindow=-1;public float CurrentPriority;public float PreviousPriority;
            public VirtualGeometryPagePayload Payload;public Task<VirtualGeometryPagePayload> Task;public CancellationTokenSource Cancellation;
            public int VerticesUploaded;public int IndicesUploaded;public bool CancellationRequested;public bool Wanted;public bool EverResident;
        }
        private readonly VirtualGeometryStreamMetadata metadata;private readonly VirtualGeometryStreamingSettings settings;
        // 最初から常駐させて退避しないpage。root(page 0)と、rootからProxyと同じ段までの単一子の段のpage。
        // 読込直後の専用描画をProxyと同じ形にし、Proxy→より粗いrootへの切替による見た目の後退を防ぐ。
        private readonly bool[] pinned;
        private readonly IVirtualGeometryPageStore store;private readonly Entry[] entries;
        // slotごとに使っているpage(-1は空き)と、最後に退避したpageのGPU完了待ちsubmission(再利用の安全確認に使う)。
        private readonly int[] slotPages;private readonly long[] slotRetireAfter;
        private readonly int[] pageSlots;private readonly int rootSlots;private int generation;
        private long transferFrame=-1;private bool disposed;
        public VirtualGeometryStreamingStatistics Statistics{get;}=new VirtualGeometryStreamingStatistics();
        public int SlotCount=>this.slotPages.Length;
        /// <summary>root と、Proxyと同じ段までの常駐pageがすべてGPUへ公開済みか。専用描画への切替条件。</summary>
        // 全部品×cameraで毎frame何度も読まれるため、状態が変わるとき(RefreshStatistics)に求めた値を返す。
        public bool RootReady=>this.rootReady;
        private bool rootReady;
        // 読込・転送・退避・要求し直しなど、Tickで進める作業が残っているか。無ければTickは何もしないので、全pageの走査を省く。RefreshStatisticsが更新する。
        private bool needsTick=true;
        /// <summary>
        /// 全pageがGPUへ公開済みか。そうなると新しい要求も退避も起きないため、GPU feedbackのreadbackを描画ごとに発行する必要がない。
        /// 予算がpage全体より小さい間は常にfalse。値はTickごとに更新される統計から取る。
        /// </summary>
        public bool IsFullyResident=>this.entries.Length>0&&this.Statistics.Resident==this.entries.Length;
        public bool IsPinned(int page)=>page>=0&&page<this.pinned.Length&&this.pinned[page];
        public VirtualGeometryPageState GetState(int page)=>this.entries[page].State;

        public VirtualGeometryStreamingController(VirtualGeometryStreamMetadata metadata,VirtualGeometryStreamingSettings settings,IVirtualGeometryPageStore store){
            this.metadata=metadata??throw new ArgumentNullException(nameof(metadata));
            this.settings=settings?.Clone()??throw new ArgumentNullException(nameof(settings));this.store=store??throw new ArgumentNullException(nameof(store));
            if(settings.GpuGeometryBudgetBytes<(long)metadata.PageSlots(0)*metadata.SlotGeometryBytes||settings.CpuCacheBudgetBytes<2L*metadata.Pages[0].ByteCount||
                settings.IoConcurrency<1||settings.TransferBytesPerFrame<32||settings.RequestQueueCapacity<1||settings.RequestTimeoutFrames<1){
                throw new InvalidOperationException("Streaming budgets cannot hold the pinned root, minimum slot, or transfer element.");
            }
            int slotCount=SlotCountFor(metadata,settings);
            this.slotPages=new int[slotCount];this.slotRetireAfter=new long[slotCount];
            for(int i=0;i<slotCount;i++){this.slotPages[i]=-1;}
            this.pageSlots=new int[metadata.Pages.Length];
            for(int page=0;page<this.pageSlots.Length;page++){this.pageSlots[page]=metadata.PageSlots(page);}
            this.rootSlots=this.pageSlots[0];
            this.entries=new Entry[metadata.Pages.Length];for(int i=0;i<this.entries.Length;i++){this.entries[i]=new Entry();}
            this.Statistics.GpuAllocatedBytes=(long)slotCount*metadata.SlotGeometryBytes;
            this.Statistics.CpuMetadataBytes=metadata.MetadataBytes;
            this.pinned=SelectPinnedPages(metadata,slotCount);
            for(int page=0;page<this.entries.Length;page++){
                if(this.pinned[page]){this.Request(page,float.MaxValue,0);}
            }
        }

        /// <summary>予算から確保するslot数。全pageを同時に置ける数より多くは確保しない。</summary>
        public static int SlotCountFor(VirtualGeometryStreamMetadata metadata,VirtualGeometryStreamingSettings settings){
            if(metadata==null){throw new ArgumentNullException(nameof(metadata));}
            if(settings==null){throw new ArgumentNullException(nameof(settings));}
            return (int)Math.Min(metadata.TotalPageSlots,settings.GpuGeometryBudgetBytes/metadata.SlotGeometryBytes);
        }

        /// <summary>1 nodeを細分化するのに要るslot数の最大。細分化には全ての子のpageが要るため、子のpageのslot数の和で数える。</summary>
        public static int RefinementReserveSlots(VirtualGeometryStreamMetadata metadata){
            if(metadata==null){throw new ArgumentNullException(nameof(metadata));}
            int reserve=1;
            foreach(VirtualGeometryHierarchyNode node in metadata.Nodes){
                int slots=0;
                for(uint child=0;child<node.ChildCount;child++){slots+=metadata.PageSlots((int)metadata.Nodes[node.FirstChild+child].PageId);}
                reserve=Math.Max(reserve,slots);
            }
            return reserve;
        }

        /// <summary>
        /// 起動時から常駐させ退避しないpage。rootのpageと、rootから単一子の段を降りてProxyと同じ段までのpage。
        /// 固定pageだけで予算を埋めると詳細を読めず粗いまま止まるため、1 nodeを細分化するのに要るslotを空け、入る分だけrootに近い段から固定する。
        /// </summary>
        public static bool[] SelectPinnedPages(VirtualGeometryStreamMetadata metadata,int slotCount){
            if(metadata==null){throw new ArgumentNullException(nameof(metadata));}
            var pinned=new bool[metadata.Pages.Length];pinned[0]=true;
            int available=Math.Max(0,slotCount-metadata.PageSlots(0)-RefinementReserveSlots(metadata));
            foreach(uint root in metadata.Roots){
                if(root==uint.MaxValue){continue;}
                uint node=root;
                while(metadata.Nodes[node].ChildCount==1){
                    node=metadata.Nodes[node].FirstChild;int page=(int)metadata.Nodes[node].PageId;
                    if(pinned[page]){continue;}
                    int cost=metadata.PageSlots(page);
                    if(cost>available){break;}
                    pinned[page]=true;available-=cost;
                }
            }
            return pinned;
        }

        public void Request(int page,float priority,long frame){
            if(this.disposed||page<0||page>=this.entries.Length){return;}
            this.Statistics.Requests++;Entry entry=this.entries[page];
            if(entry.EverResident&&!entry.Wanted){this.Statistics.ReRequests++;}
            entry.Wanted=true;
            // 読み込み済みで未要求だったpage(Cached)が要求されると転送が始まる。状態が変わらないまま戻る経路もあるため、次のTickを必ず走らせる。
            this.needsTick=true;
            if(!float.IsFinite(priority)||priority<0){priority=0;}
            if(this.pinned[page]){priority=float.MaxValue;}
            ObservePriority(entry,priority,frame);
            if(entry.State!=VirtualGeometryPageState.Missing&&entry.State!=VirtualGeometryPageState.Cancelled){
                this.Statistics.Deduplicated++;return;
            }
            this.Statistics.Misses++;
            int queued=0;int worst=-1;
            for(int i=0;i<this.entries.Length;i++){
                if(this.entries[i].State!=VirtualGeometryPageState.Queued){continue;}
                queued++;
                if(!this.pinned[i]&&(worst<0||this.entries[i].Priority<this.entries[worst].Priority)){worst=i;}
            }
            if(queued>=this.settings.RequestQueueCapacity){
                this.Statistics.QueueOverflow++;
                if(worst<0||this.entries[worst].Priority>=priority){return;}
                this.entries[worst].State=VirtualGeometryPageState.Missing;
            }
            entry.State=VirtualGeometryPageState.Queued;this.RefreshStatistics();
        }

        public void Touch(int page,float priority,long frame){
            if(this.disposed||page<0||page>=this.entries.Length){return;}
            Entry entry=this.entries[page];
            if(!float.IsFinite(priority)||priority<0){priority=0;}
            if(this.pinned[page]){priority=float.MaxValue;}
            ObservePriority(entry,priority,frame);
        }

        // feedbackはinstance×cameraごとに別のframeで非同期に届く。
        // 1件ごとに上書きすると、同じpageを使う別cameraの高い優先度が消え、利用中pageの退避と再要求が振動する。
        // InUseFrames幅の窓ごとに最大値を取り、直前の窓も含めた最大値を優先度とする。
        private static void ObservePriority(Entry entry,float priority,long frame){
            long window=frame/InUseFrames;
            if(window>entry.PriorityWindow){
                if(window==entry.PriorityWindow+1){entry.PreviousPriority=entry.CurrentPriority;}else{entry.PreviousPriority=0;}
                entry.CurrentPriority=priority;entry.PriorityWindow=window;
            }else if(window==entry.PriorityWindow){
                entry.CurrentPriority=Math.Max(entry.CurrentPriority,priority);
            }else if(window==entry.PriorityWindow-1){
                entry.PreviousPriority=Math.Max(entry.PreviousPriority,priority);
            }
            entry.Priority=Math.Max(entry.CurrentPriority,entry.PreviousPriority);
            entry.LastRequested=Math.Max(entry.LastRequested,frame);
        }

        public void Cancel(int page){
            if(this.disposed||page<0||page>=this.entries.Length||this.pinned[page]){return;}
            Entry entry=this.entries[page];entry.Wanted=false;
            if(entry.State==VirtualGeometryPageState.Loading&&!entry.CancellationRequested){
                entry.CancellationRequested=true;entry.Cancellation.Cancel();this.Statistics.Cancellations++;
            }else if(entry.State==VirtualGeometryPageState.Queued||entry.State==VirtualGeometryPageState.Cached||entry.State==VirtualGeometryPageState.Uploading){
                // 未公開slotはどのdrawも参照しないため、取消時に直ちに返却できる。
                if(entry.Slot>=0){this.FreeSlots(entry);}
                if(entry.Payload!=null){this.Statistics.CpuBytes-=entry.Payload.ByteCount;entry.Payload=null;}
                entry.State=VirtualGeometryPageState.Cancelled;this.Statistics.Cancellations++;
            }
            this.RefreshStatistics();
        }

        public void RetryFailures(){
            foreach(Entry entry in this.entries){if(entry.State==VirtualGeometryPageState.Failed){entry.State=VirtualGeometryPageState.Missing;}}
            this.RefreshStatistics();
        }

        public void Tick(long frame,long lastSubmitted,long completedSubmission,IVirtualGeometryPageUploader uploader){
            if(this.disposed){return;}
            if(this.transferFrame!=frame){this.transferFrame=frame;this.Statistics.TransferBytesThisFrame=0;}
            this.Statistics.CompletedSubmission=completedSubmission;
            if(!this.needsTick){return;}
            // 固定pageはGPU feedbackを待たずに要求し直す。専用描画はそれらの常駐後に始まるため、feedbackだけに頼ると要求queueが溢れたときに永久にProxyのままになる。
            for(int page=0;page<this.entries.Length;page++){
                VirtualGeometryPageState state=this.entries[page].State;
                if(this.pinned[page]&&(state==VirtualGeometryPageState.Missing||state==VirtualGeometryPageState.Cancelled)){this.Request(page,float.MaxValue,frame);}
            }
            for(int page=1;page<this.entries.Length;page++){
                Entry entry=this.entries[page];
                if(entry.Wanted&&frame-entry.LastRequested>this.settings.RequestTimeoutFrames&&
                    (entry.State==VirtualGeometryPageState.Queued||entry.State==VirtualGeometryPageState.Cached||entry.State==VirtualGeometryPageState.Uploading)){this.Cancel(page);}
            }
            this.PollIo(frame);
            for(int page=1;page<this.entries.Length;page++){
                Entry old=this.entries[page];
                if(!old.Retiring||completedSubmission<old.RetireAfter){continue;}
                for(int s=old.Slot;s<old.Slot+old.SlotCount;s++){this.slotRetireAfter[s]=old.RetireAfter;}
                this.FreeSlots(old);old.Retiring=false;
                if(old.Payload!=null){old.State=VirtualGeometryPageState.Cached;}else{old.State=VirtualGeometryPageState.Missing;}
            }
            this.StartIo(frame);
            for(int iteration=0;iteration<this.entries.Length;iteration++){
                int page=this.FindUploadCandidate(frame);
                if(page<0){break;}
                Entry entry=this.entries[page];
                if(entry.Slot<0){
                    // 予算の全slotを使っても置けないpageは読めない。CPU予算を超えるpageと同じく失敗として報告する。
                    if(page!=0&&this.pageSlots[page]>this.slotPages.Length-this.rootSlots){
                        this.Statistics.CpuBytes-=entry.Payload.ByteCount;entry.Payload=null;entry.State=VirtualGeometryPageState.Failed;
                        this.Statistics.LastError="Page requires more GPU geometry slots than configured.";continue;
                    }
                    int slot=this.FindFreeSlots(page);
                    if(slot<0){this.BeginEviction(page,frame,lastSubmitted,uploader);break;}
                    long retired=0;
                    for(int s=slot;s<slot+this.pageSlots[page];s++){retired=Math.Max(retired,this.slotRetireAfter[s]);}
                    if(retired>0){
                        this.Statistics.LastReuseRetirementSubmission=retired;this.Statistics.LastReuseCompletedSubmission=completedSubmission;
                        if(completedSubmission<retired){this.Statistics.UnsafeSlotReuses++;throw new InvalidOperationException("GPU completion has not reached slot retirement.");}
                    }
                    for(int s=slot;s<slot+this.pageSlots[page];s++){this.slotPages[s]=page;}
                    entry.Slot=slot;entry.SlotCount=this.pageSlots[page];entry.Generation=++this.generation;this.Statistics.LastReuseGeneration=entry.Generation;
                    entry.VerticesUploaded=0;entry.IndicesUploaded=0;entry.State=VirtualGeometryPageState.Uploading;
                }
                int remaining=this.settings.TransferBytesPerFrame-this.Statistics.TransferBytesThisFrame;
                int vertexCount=Math.Min(entry.Payload.Vertices.Length-entry.VerticesUploaded,remaining/32);
                if(vertexCount>0){
                    uploader.UploadVertices(entry.Slot,entry.VerticesUploaded,entry.Payload.Vertices,entry.VerticesUploaded,vertexCount);
                    entry.VerticesUploaded+=vertexCount;this.Statistics.TransferBytesThisFrame+=vertexCount*32;remaining-=vertexCount*32;
                }
                if(entry.VerticesUploaded<entry.Payload.Vertices.Length){break;}
                int indexCount=Math.Min(entry.Payload.Indices.Length-entry.IndicesUploaded,remaining/4);
                if(indexCount>0){
                    uploader.UploadIndices(entry.Slot,entry.IndicesUploaded,entry.Payload.Indices,entry.IndicesUploaded,indexCount);
                    entry.IndicesUploaded+=indexCount;this.Statistics.TransferBytesThisFrame+=indexCount*4;
                }
                if(entry.IndicesUploaded<entry.Payload.Indices.Length){break;}
                uploader.Publish(page,entry.Slot,entry.Generation);entry.State=VirtualGeometryPageState.Resident;entry.EverResident=true;
                if(this.Statistics.TransferBytesThisFrame>=this.settings.TransferBytesPerFrame){break;}
            }
            this.Statistics.PeakTransferBytesPerFrame=Math.Max(this.Statistics.PeakTransferBytesPerFrame,this.Statistics.TransferBytesThisFrame);
            if(this.store is VirtualGeometryFilePageStore fileStore){this.Statistics.IoBytes=fileStore.BytesRead;}
            this.RefreshStatistics();
        }

        private void PollIo(long frame){
            for(int page=0;page<this.entries.Length;page++){
                Entry entry=this.entries[page];if(entry.State!=VirtualGeometryPageState.Loading){continue;}
                if(!this.pinned[page]&&frame-entry.LastRequested>this.settings.RequestTimeoutFrames&&!entry.CancellationRequested){this.Cancel(page);}
                if(!entry.Task.IsCompleted){continue;}
                this.Statistics.CpuBytes-=2L*this.metadata.Pages[page].ByteCount;
                try{
                    VirtualGeometryPagePayload payload=entry.Task.GetAwaiter().GetResult();
                    var descriptor=this.metadata.Pages[page];
                    if(payload.Vertices.Length!=descriptor.VertexCount||payload.Indices.Length!=descriptor.IndexCount){throw new InvalidDataException("Loaded page size differs from metadata.");}
                    if(entry.CancellationRequested){entry.State=VirtualGeometryPageState.Cancelled;}
                    else{entry.Payload=payload;this.Statistics.CpuBytes+=payload.ByteCount;entry.State=VirtualGeometryPageState.Cached;}
                    if(!(this.store is VirtualGeometryFilePageStore)){this.Statistics.IoBytes+=payload.ByteCount;}
                }catch(OperationCanceledException){entry.State=VirtualGeometryPageState.Cancelled;}
                catch(Exception error){entry.State=VirtualGeometryPageState.Failed;this.Statistics.LastError=error.GetType().Name+": "+error.Message;}
                entry.Task=null;entry.Cancellation.Dispose();entry.Cancellation=null;entry.CancellationRequested=false;
            }
        }

        private void StartIo(long frame){
            int loading=0;foreach(Entry entry in this.entries){if(entry.State==VirtualGeometryPageState.Loading){loading++;}}
            while(loading<this.settings.IoConcurrency){
                int next=-1;
                for(int page=0;page<this.entries.Length;page++){
                    Entry entry=this.entries[page];if(entry.State!=VirtualGeometryPageState.Queued){continue;}
                    if(!this.pinned[page]&&frame-entry.LastRequested>this.settings.RequestTimeoutFrames){this.Cancel(page);continue;}
                    if(next<0||entry.Priority>this.entries[next].Priority){next=page;}
                }
                if(next<0){break;}
                Entry candidate=this.entries[next];long reservation=2L*this.metadata.Pages[next].ByteCount;
                if(reservation>this.settings.CpuCacheBudgetBytes){
                    candidate.State=VirtualGeometryPageState.Failed;this.Statistics.LastError="Page requires more CPU decode/cache bytes than configured.";continue;
                }
                this.TrimCache(reservation);
                if(this.Statistics.CpuBytes+reservation>this.settings.CpuCacheBudgetBytes){break;}
                candidate.Cancellation=new CancellationTokenSource();candidate.CancellationRequested=false;candidate.State=VirtualGeometryPageState.Loading;
                this.Statistics.CpuBytes+=reservation;this.Statistics.PeakCpuBytes=Math.Max(this.Statistics.PeakCpuBytes,this.Statistics.CpuBytes);
                try{candidate.Task=this.store.ReadAsync(next,candidate.Cancellation.Token);}
                catch(Exception error){candidate.Task=Task.FromException<VirtualGeometryPagePayload>(error);}
                loading++;this.Statistics.PeakIoConcurrency=Math.Max(this.Statistics.PeakIoConcurrency,loading);
            }
        }

        private void TrimCache(long required){
            while(this.Statistics.CpuBytes+required>this.settings.CpuCacheBudgetBytes){
                int oldest=-1;
                for(int page=0;page<this.entries.Length;page++){
                    Entry entry=this.entries[page];
                    if(entry.Payload==null||entry.State==VirtualGeometryPageState.Uploading||
                        (entry.State==VirtualGeometryPageState.Cached&&entry.Wanted)){continue;}
                    if(oldest<0||entry.LastRequested<this.entries[oldest].LastRequested){oldest=page;}
                }
                if(oldest<0){return;}
                Entry victim=this.entries[oldest];this.Statistics.CpuBytes-=victim.Payload.ByteCount;victim.Payload=null;if(victim.State==VirtualGeometryPageState.Cached){victim.State=VirtualGeometryPageState.Missing;}
            }
        }

        private int FindUploadCandidate(long frame){
            int next=-1;
            for(int page=0;page<this.entries.Length;page++){
                Entry entry=this.entries[page];
                if(entry.State!=VirtualGeometryPageState.Cached&&entry.State!=VirtualGeometryPageState.Uploading){continue;}
                if(entry.Payload==null||!entry.Wanted||(!this.pinned[page]&&frame-entry.LastRequested>this.settings.RequestTimeoutFrames)){continue;}
                if(entry.State==VirtualGeometryPageState.Uploading){return page;}
                if(next<0||entry.Priority>this.entries[next].Priority){next=page;}
            }
            return next;
        }

        // pageが要る数の連続した空きslotの先頭。rootは先頭のslotに置き、他のpageはその後ろから探す。
        private int FindFreeSlots(int page){
            int count=this.pageSlots[page];
            if(page==0){
                for(int s=0;s<count;s++){if(this.slotPages[s]>=0){return -1;}}
                return 0;
            }
            int run=0;
            for(int s=this.rootSlots;s<this.slotPages.Length;s++){
                if(this.slotPages[s]>=0){run=0;continue;}
                run++;
                if(run==count){return s-count+1;}
            }
            return -1;
        }

        private void FreeSlots(Entry entry){
            for(int s=entry.Slot;s<entry.Slot+entry.SlotCount;s++){this.slotPages[s]=-1;}
            entry.Slot=-1;entry.SlotCount=0;
        }

        // GPU feedbackは描画frameで刻印され、非同期readback完了まで数frame遅れて届く。
        // 直前1 frameだけで利用中を判定すると、現在のcutが使うpageまで古く見えて退避対象になる。
        private const int InUseFrames=8;

        // wantedが要る数の連続slotを空ける。区間の中のpageがすべて退避してよい(固定でなく、常駐済みで、使用中でない)区間のうち、
        // 区間で最も最近使われたpageが最も古い区間を選び、その中のpageをすべて退避する。
        // 空きと退避中のslotだけで足りる区間があれば、退避の完了を待つ(余計に退避しない)。
        private void BeginEviction(int wanted,long frame,long lastSubmitted,IVirtualGeometryPageUploader uploader){
            int count=this.pageSlots[wanted];Entry desire=this.entries[wanted];
            int best=-1;long bestNewest=long.MaxValue;float bestPriority=float.MaxValue;
            for(int start=this.rootSlots;start+count<=this.slotPages.Length;start++){
                bool usable=true;bool hasVictim=false;long newest=long.MinValue;float priority=0;
                for(int s=start;s<start+count;s++){
                    int occupant=this.slotPages[s];
                    if(occupant<0){continue;}
                    Entry entry=this.entries[occupant];
                    if(entry.Retiring){continue;}
                    if(this.pinned[occupant]||entry.State!=VirtualGeometryPageState.Resident||
                        (entry.LastRequested>=frame-InUseFrames&&entry.Priority>=desire.Priority)){usable=false;break;}
                    hasVictim=true;newest=Math.Max(newest,entry.LastRequested);priority=Math.Max(priority,entry.Priority);
                }
                if(!usable){continue;}
                if(!hasVictim){return;}
                if(best<0||newest<bestNewest||(newest==bestNewest&&priority<bestPriority)){best=start;bestNewest=newest;bestPriority=priority;}
            }
            if(best<0){return;}
            var victims=new List<int>();
            for(int s=best;s<best+count;s++){
                int occupant=this.slotPages[s];
                if(occupant>=0&&!this.entries[occupant].Retiring&&!victims.Contains(occupant)){victims.Add(occupant);}
            }
            foreach(int victim in victims){
                Entry entry=this.entries[victim];uploader.Invalidate(victim);
                entry.State=VirtualGeometryPageState.Retiring;entry.Wanted=false;entry.Retiring=true;entry.RetireAfter=lastSubmitted+1;
                this.Statistics.LastRetirementSubmission=entry.RetireAfter;this.Statistics.Evictions++;
            }
        }

        private void RefreshStatistics(){
            this.Statistics.Queued=0;this.Statistics.Loading=0;this.Statistics.Uploading=0;this.Statistics.Resident=0;this.Statistics.Failed=0;this.Statistics.GpuResidentBytes=0;
            bool ready=true;bool pending=false;
            for(int page=0;page<this.entries.Length;page++){
                Entry current=this.entries[page];
                VirtualGeometryPageState state=current.State;
                if(this.pinned[page]&&state!=VirtualGeometryPageState.Resident){ready=false;}
                // Tickが進める作業: 読込待ち・読込中・転送中・退避中、要求されて読み込み済みの転送待ち、固定pageの要求し直し。
                if(state==VirtualGeometryPageState.Queued||state==VirtualGeometryPageState.Loading||state==VirtualGeometryPageState.Uploading||current.Retiring||
                    (state==VirtualGeometryPageState.Cached&&current.Wanted)||
                    (this.pinned[page]&&(state==VirtualGeometryPageState.Missing||state==VirtualGeometryPageState.Cancelled))){pending=true;}
                switch(this.entries[page].State){
                    case VirtualGeometryPageState.Queued:this.Statistics.Queued++;break;
                    case VirtualGeometryPageState.Loading:this.Statistics.Loading++;break;
                    case VirtualGeometryPageState.Uploading:this.Statistics.Uploading++;break;
                    case VirtualGeometryPageState.Resident:this.Statistics.Resident++;this.Statistics.GpuResidentBytes+=this.metadata.Pages[page].ByteCount;break;
                    case VirtualGeometryPageState.Failed:this.Statistics.Failed++;break;
                }
            }
            this.rootReady=ready;this.needsTick=pending;
            this.Statistics.PeakGpuResidentBytes=Math.Max(this.Statistics.PeakGpuResidentBytes,this.Statistics.GpuResidentBytes);
            this.Statistics.PeakCpuBytes=Math.Max(this.Statistics.PeakCpuBytes,this.Statistics.CpuBytes);
        }

        public void Dispose(){
            if(this.disposed){return;}this.disposed=true;
            for(int page=0;page<this.entries.Length;page++){
                Entry entry=this.entries[page];
                if(entry.Payload!=null){this.Statistics.CpuBytes-=entry.Payload.ByteCount;entry.Payload=null;}
                if(entry.Task!=null){
                    if(!entry.Task.IsCompleted&&!entry.CancellationRequested){entry.Cancellation.Cancel();this.Statistics.Cancellations++;}
                    long reservation=2L*this.metadata.Pages[page].ByteCount;
                    // I/Oの取消が完了するまで予約bytesを保持し、遅延faultも必ず観測する。
                    entry.Task.ContinueWith(task=>{
                        if(task.IsFaulted){var observed=task.Exception;}
                        entry.Task=null;entry.Cancellation.Dispose();entry.Cancellation=null;
                        Interlocked.Add(ref this.Statistics.CpuBytes,-reservation);
                        Interlocked.Decrement(ref this.Statistics.Loading);
                    },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                }else if(entry.State==VirtualGeometryPageState.Queued||entry.State==VirtualGeometryPageState.Uploading||entry.State==VirtualGeometryPageState.Cached){
                    this.Statistics.Cancellations++;
                }
                entry.State=VirtualGeometryPageState.Cancelled;entry.Wanted=false;
            }
            this.Statistics.Queued=0;this.Statistics.Uploading=0;this.Statistics.Resident=0;
            this.Statistics.GpuAllocatedBytes=0;this.Statistics.GpuResidentBytes=0;
        }
    }
}
