using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VirtualizedGeometry.Tests{
    /// <summary>v3 pageの原子性と、実I/Oを差し替えた予算・退避state遷移を検証する。</summary>
    public sealed class VirtualGeometryStreamingTests{
        [Test]
        public void LegacyV1GoldenArchiveRemainsReadableWithoutStreamingHierarchy(){
            // M1時点で保存したv1 bytesを使用し、現在のwriterをoracleにしない。
            TextAsset fixture=UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.virtualgeometry.core/Tests/Editor/Fixtures/LegacyV1.bytes");
            Assert.That(fixture,Is.Not.Null);
            VirtualGeometryPage page=VirtualGeometryPageCodec.Read(fixture.bytes);
            Assert.That(page.Vertices.Length,Is.EqualTo(825));Assert.That(page.Indices.Length,Is.EqualTo(4608));
            Assert.That(page.Clusters.Length,Is.EqualTo(24));Assert.That(page.MaterialCount,Is.EqualTo(1));
            Assert.That(page.Nodes.Length,Is.Zero);
        }

        [Test]
        public void Archive_PartitionsRepresentationsPinsRootsAndPreservesLegacyReaders(){
            VirtualGeometryPage source=CreatePage();
            byte[] legacy=VirtualGeometryPageCodec.Write(source);
            Assert.That(VirtualGeometryPageCodec.Read(legacy).Nodes.Length,Is.EqualTo(3));
            byte[] bytes=VirtualGeometryStreamCodec.Write(source);
            VirtualGeometryStreamMetadata metadata=VirtualGeometryStreamCodec.ReadMetadata(new MemoryStream(bytes));
            Assert.That(metadata.Pages.Length,Is.EqualTo(3));
            Assert.That(metadata.Nodes[metadata.Roots[0]].PageId,Is.Zero);
            foreach(VirtualGeometryHierarchyNode node in metadata.Nodes){
                for(uint c=0;c<node.ClusterCount;c++){Assert.That(metadata.Clusters[node.FirstCluster+c].PageId,Is.EqualTo(node.PageId));}
            }
            VirtualGeometryPage restored=VirtualGeometryPageCodec.Read(bytes);
            Assert.That(restored.Indices.Length,Is.EqualTo(source.Indices.Length));
            for(int c=0;c<source.Clusters.Length;c++){
                for(uint i=0;i<source.Clusters[c].IndexCount;i++){
                    Vector3 a=source.Vertices[source.Indices[source.Clusters[c].FirstIndex+i]].Position;
                    Vector3 b=restored.Vertices[restored.Indices[restored.Clusters[c].FirstIndex+i]].Position;
                    Assert.That(a,Is.EqualTo(b));
                }
            }
            Assert.That(bytes,Is.EqualTo(VirtualGeometryStreamCodec.Write(source)));
            bytes[bytes.Length-1]^=0x40;
            Assert.Throws<InvalidDataException>(()=>VirtualGeometryPageCodec.Read(bytes));
        }

        [Test]
        public void Scheduler_DeduplicatesPrioritizesAndBoundsQueue(){
            using(Harness h=new Harness(3,1,64)){
                h.Controller.Tick(1,0,0,h.Uploader);
                h.Controller.Request(1,2,0);h.Controller.Request(1,3,0);h.Controller.Request(2,8,0);
                Assert.That(h.Controller.Statistics.Deduplicated,Is.GreaterThanOrEqualTo(1));
                Assert.That(h.Controller.Statistics.QueueOverflow,Is.GreaterThan(0));
                h.Controller.Tick(1,0,0,h.Uploader);
                Assert.That(h.Store.Requested,Is.EqualTo(new[]{0}));
                h.Store.Complete(0);h.Controller.Tick(2,0,0,h.Uploader);
                h.Controller.Tick(3,0,0,h.Uploader);
                Assert.That(h.Store.Requested,Does.Contain(2));
                Assert.That(h.Controller.Statistics.Loading,Is.LessThanOrEqualTo(1));
            }
        }

        [Test]
        public void BudgetsRejectRootAndChunkUploadNeverPublishesPartialPage(){
            VirtualGeometryStreamMetadata metadata=Metadata();
            VirtualGeometryStreamingSettings invalid=Settings(metadata,1,4,32);
            invalid.GpuGeometryBudgetBytes=1;
            Assert.Throws<InvalidOperationException>(()=>new VirtualGeometryStreamingController(metadata,invalid,new ControlledStore(metadata)));
            invalid=Settings(metadata,1,4,32);invalid.CpuCacheBudgetBytes=1;
            Assert.Throws<InvalidOperationException>(()=>new VirtualGeometryStreamingController(metadata,invalid,new ControlledStore(metadata)));
            using(Harness h=new Harness(2,4,32)){
                h.Controller.Tick(1,0,0,h.Uploader);h.Store.Complete(0);
                for(int frame=2;frame<=4;frame++){
                    h.Controller.Tick(frame,0,0,h.Uploader);
                    Assert.That(h.Controller.Statistics.TransferBytesThisFrame,Is.LessThanOrEqualTo(32));
                    Assert.That(h.Uploader.Resident.Contains(0),Is.False);
                }
                h.Controller.Tick(5,0,0,h.Uploader);
                Assert.That(h.Uploader.Resident.Contains(0),Is.True);
                Assert.That(h.Controller.Statistics.CpuBytes,Is.LessThanOrEqualTo(h.Settings.CpuCacheBudgetBytes));
                Assert.That(h.Controller.Statistics.GpuAllocatedBytes,Is.LessThanOrEqualTo(h.Settings.GpuGeometryBudgetBytes));
            }
        }

        [Test]
        public void EvictionInvalidatesBeforeGpuCompletionAndGenerationReuse(){
            using(Harness h=new Harness(2,4,4096)){
                h.Load(0,1);
                h.Controller.Request(1,1,2);h.Load(1,2);
                int generation=h.Uploader.Generations[1];
                h.Controller.Request(2,20,5);h.Controller.Tick(5,10,9,h.Uploader);h.Store.Complete(2);
                h.Controller.Tick(6,10,9,h.Uploader);
                Assert.That(h.Uploader.Resident.Contains(1),Is.False);
                Assert.That(h.Uploader.Resident.Contains(0),Is.True);
                Assert.That(h.Uploader.Resident.Contains(2),Is.False);
                h.Controller.Tick(7,11,10,h.Uploader);
                Assert.That(h.Uploader.Resident.Contains(2),Is.False);
                h.Controller.Tick(8,11,11,h.Uploader);
                Assert.That(h.Uploader.Resident.Contains(2),Is.True);
                Assert.That(h.Uploader.Generations[2],Is.GreaterThan(generation));
                Assert.That(h.Controller.Statistics.UnsafeSlotReuses,Is.Zero);
                h.Controller.Request(1,30,9);
                Assert.That(h.Controller.Statistics.ReRequests,Is.EqualTo(1));
                Assert.That(h.Controller.Statistics.Requests,Is.GreaterThan(2));
            }
        }

        [Test]
        public void PagesOfDifferentSizesUseContiguousSlotRuns(){
            VirtualGeometryStreamMetadata metadata=VirtualGeometryStreamCodec.ReadMetadata(new MemoryStream(VirtualGeometryStreamCodec.Write(CreateUnevenPage())));
            {
                //slotは最大のpageではなく典型的なpageの大きさで、大きなpageは要る数の連続slotを使う
                Assert.That(metadata.SlotGeometryBytes,Is.LessThan(metadata.MaxPageBytes));
                Assert.That(metadata.PageSlots(1),Is.EqualTo(1));Assert.That(metadata.PageSlots(2),Is.EqualTo(3));
                Assert.That(metadata.TotalPageSlots,Is.EqualTo(5));
            }
            {
                //全pageが入る予算では、各pageの連続slotが重ならずに並ぶ
                using(Harness h=new Harness(metadata,5,4,4096)){
                    h.Load(0,1);h.Controller.Request(1,1,3);h.Load(1,3);h.Controller.Request(2,1,5);h.Load(2,5);
                    Assert.That(h.Uploader.Resident,Is.EquivalentTo(new[]{0,1,2}));
                    AssertSlotRunsDisjoint(metadata,h);
                }
            }
            {
                //連続した空きが足りないときは使われていないpageを退避して空ける。GPU完了まで退避したslotは再利用しない
                using(Harness h=new Harness(metadata,4,4,4096)){
                    h.Load(0,1);h.Controller.Request(1,1,3);h.Load(1,3);
                    h.Controller.Request(2,20,20);h.Controller.Tick(20,30,29,h.Uploader);h.Store.Complete(2);
                    h.Controller.Tick(21,30,29,h.Uploader);
                    Assert.That(h.Uploader.Resident.Contains(1),Is.False);Assert.That(h.Uploader.Resident.Contains(2),Is.False);
                    h.Controller.Tick(22,31,31,h.Uploader);
                    Assert.That(h.Uploader.Resident.Contains(2),Is.True);Assert.That(h.Uploader.Resident.Contains(0),Is.True);
                    AssertSlotRunsDisjoint(metadata,h);
                    Assert.That(h.Controller.Statistics.UnsafeSlotReuses,Is.Zero);
                    Assert.That(h.Controller.Statistics.GpuAllocatedBytes,Is.EqualTo(4L*metadata.SlotGeometryBytes));
                }
            }
            {
                //予算の全slotを使っても置けないpageは失敗として報告し、rootは使い続ける
                using(Harness h=new Harness(metadata,3,4,4096)){
                    h.Load(0,1);h.Controller.Request(2,5,5);h.Load(2,5);
                    Assert.That(h.Controller.GetState(2),Is.EqualTo(VirtualGeometryPageState.Failed));
                    Assert.That(h.Controller.Statistics.LastError,Does.Contain("GPU geometry slots"));
                    Assert.That(h.Uploader.Resident.Contains(0),Is.True);
                }
            }
        }

        // 常駐pageの連続slotが互いに重ならず、確保したslotの中に収まる。
        private static void AssertSlotRunsDisjoint(VirtualGeometryStreamMetadata metadata,Harness h){
            var used=new bool[h.Controller.SlotCount];
            foreach(int page in h.Uploader.Resident){
                int first=h.Uploader.Slots[page];
                for(int slot=first;slot<first+metadata.PageSlots(page);slot++){
                    Assert.That(slot,Is.LessThan(used.Length));Assert.That(used[slot],Is.False,"slot "+slot+" is shared");used[slot]=true;
                }
            }
        }

        [Test]
        public void CancellationFailureAndRerequestAreObservedWithoutLeakingExceptions(){
            using(Harness h=new Harness(2,4,4096)){
                h.Load(0,1);h.Controller.Request(1,2,2);h.Controller.Tick(3,0,0,h.Uploader);
                h.Controller.Cancel(1);h.Controller.Tick(4,0,0,h.Uploader);
                Assert.That(h.Controller.Statistics.Cancellations,Is.EqualTo(1));
                h.Controller.Request(1,5,5);h.Controller.Tick(5,0,0,h.Uploader);h.Store.Fail(1);
                h.Controller.Tick(6,0,0,h.Uploader);
                Assert.That(h.Controller.Statistics.Failed,Is.EqualTo(1));
                Assert.That(h.Uploader.Resident.Contains(0),Is.True);
                h.Controller.RetryFailures();h.Controller.Request(1,6,7);h.Controller.Tick(7,0,0,h.Uploader);
                h.Store.Complete(1);h.Controller.Tick(8,0,0,h.Uploader);
                Assert.That(h.Uploader.Resident.Contains(1),Is.True);
            }
        }

        [Test]
        public void CancelledPartialUploadReleasesItsSlotAndCanBeRequestedAgain(){
            using(Harness h=new Harness(2,4,32)){
                h.Controller.Tick(1,0,0,h.Uploader);h.Store.Complete(0);
                for(int frame=2;frame<=5;frame++){h.Controller.Tick(frame,0,0,h.Uploader);}
                h.Controller.Request(1,5,6);h.Controller.Tick(6,0,0,h.Uploader);h.Store.Complete(1);
                h.Controller.Tick(7,0,0,h.Uploader);
                Assert.That(h.Controller.GetState(1),Is.EqualTo(VirtualGeometryPageState.Uploading));
                h.Controller.Cancel(1);
                Assert.That(h.Controller.GetState(1),Is.EqualTo(VirtualGeometryPageState.Cancelled));
                Assert.That(h.Uploader.Resident.Contains(1),Is.False);
                h.Controller.Request(2,10,8);h.Controller.Tick(8,0,0,h.Uploader);h.Store.Complete(2);
                for(int frame=9;frame<=12;frame++){h.Controller.Tick(frame,0,0,h.Uploader);}
                Assert.That(h.Uploader.Resident.Contains(2),Is.True);
                Assert.That(h.Uploader.Resident.Contains(0),Is.True);
                h.Controller.Request(1,20,13);
                Assert.That(h.Controller.GetState(1),Is.EqualTo(VirtualGeometryPageState.Queued));
            }
        }

        [Test]
        public void FeedbackLoopKeepsSelectedCutStableUnderBudgetAndAsyncFeedback(){
            // Practical sampleと同じv3 archive(43 pages / 2,048 leaf triangles)。
            TextAsset fixture=UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.virtualgeometry.core/Tests/Editor/Fixtures/PracticalWaveV3.bytes");
            Assert.That(fixture,Is.Not.Null);
            VirtualGeometryStreamMetadata metadata=VirtualGeometryStreamCodec.ReadMetadata(new MemoryStream(fixture.bytes));
            Vector3 far=new Vector3(0,1,-19);Vector3 near=new Vector3(-3,0,-3);
            {
                //旧sample予算(最大のpage 6個分)で2 instance×Game/Scene cameraのfeedbackが遅延・欠落しても、静止cameraのcutが振動せず退避も止まる
                FeedbackLoop loop=new FeedbackLoop(metadata,metadata.MaxPageBytes*6L,new[]{
                    new FeedbackView(-5,_=>far),new FeedbackView(5,_=>far),new FeedbackView(-5,_=>near),new FeedbackView(5,_=>near)});
                loop.Run(1500,900);
                foreach(FeedbackView view in loop.Views){Assert.That(view.CutChanges,Is.Zero);}
                Assert.That(loop.EvictionsAfterWarmup,Is.Zero);
                Assert.That(loop.Controller.Statistics.UnsafeSlotReuses,Is.Zero);
            }
            {
                //全page予算では、後に描画されるScene cameraのfeedbackも捨てられず近接側がleafまで詳細化される
                FeedbackLoop loop=new FeedbackLoop(metadata,metadata.TotalPageSlots*metadata.SlotGeometryBytes,new[]{
                    new FeedbackView(-5,_=>far),new FeedbackView(5,_=>far),new FeedbackView(-5,_=>near),new FeedbackView(5,_=>near)});
                loop.Run(1500,900);
                Assert.That(loop.Views[2].MinTriangles,Is.EqualTo(2048));Assert.That(loop.Views[3].MinTriangles,Is.EqualTo(2048));
                Assert.That(loop.Views[0].MaxTriangles,Is.LessThan(2048));
                foreach(FeedbackView view in loop.Views){Assert.That(view.CutChanges,Is.Zero);}
            }
            {
                //予算不足のままcameraが別instanceへ移動しても、移動後のcutは振動しない
                Func<int,Vector3> moving=frame=>{if(frame<600){return new Vector3(-5,0,-2.5f);}return new Vector3(5,0,-2.5f);};
                FeedbackLoop loop=new FeedbackLoop(metadata,metadata.TotalPageSlots/2*metadata.SlotGeometryBytes,new[]{new FeedbackView(-5,moving),new FeedbackView(5,moving)});
                loop.Run(1500,900);
                foreach(FeedbackView view in loop.Views){Assert.That(view.CutChanges,Is.Zero);}
                Assert.That(loop.EvictionsAfterWarmup,Is.Zero);
            }
        }

        [Test]
        public void PinnedPagesCoverRootToProxyLevelAndAreNeverEvicted(){
            VirtualGeometryStreamMetadata metadata=VirtualGeometryStreamCodec.ReadMetadata(new MemoryStream(VirtualGeometryStreamCodec.Write(CreateChainPage())));
            int proxyPage=(int)metadata.Nodes[VirtualGeometryHierarchy.ProxyNode(metadata.Nodes,metadata.Roots[0])].PageId;
            Assert.That(proxyPage,Is.Not.Zero);
            {
                //rootに加えProxyと同じ段のpageも固定し、両方がGPUへ公開されるまで専用描画へ切り替えない。要求queueが1でも固定pageは要求し直される
                using(Harness h=new Harness(metadata,4,1,4096)){
                    Assert.That(h.Controller.IsPinned(0),Is.True);Assert.That(h.Controller.IsPinned(proxyPage),Is.True);
                    h.Controller.Tick(1,0,0,h.Uploader);h.Store.Complete(0);h.Controller.Tick(2,0,0,h.Uploader);
                    Assert.That(h.Uploader.Resident.Contains(0),Is.True);Assert.That(h.Controller.RootReady,Is.False);
                    h.Controller.Tick(3,0,0,h.Uploader);h.Store.Complete(proxyPage);h.Controller.Tick(4,0,0,h.Uploader);
                    Assert.That(h.Controller.RootReady,Is.True);
                }
            }
            {
                //固定pageだけで予算を埋めると詳細を読めないため、1 nodeを細分化するのに要る子の数(この階層では2)だけslotを空ける。
                //3 slotではProxyの段を固定せず詳細用に空け、詳細pageの要求が続いてもrootは退避されない
                using(Harness h=new Harness(metadata,3,4,4096)){
                    Assert.That(h.Controller.IsPinned(proxyPage),Is.False);
                    int completed=0;
                    for(int frame=1;frame<=40;frame++){
                        if(frame>=6){h.Controller.Request(1+frame%3,100+frame,frame);}
                        h.Controller.Tick(frame,frame,frame,h.Uploader);
                        while(completed<h.Store.Requested.Count){h.Store.Complete(h.Store.Requested[completed++]);}
                        if(frame>=3){Assert.That(h.Uploader.Resident.Contains(0),Is.True,"frame "+frame);}
                    }
                    Assert.That(h.Controller.Statistics.Evictions,Is.GreaterThan(0));
                    Assert.That(h.Controller.RootReady,Is.True);
                }
            }
            {
                //root用slotしか無い予算では固定をrootだけにし、root常駐で専用描画へ切り替える
                using(Harness h=new Harness(metadata,1,4,4096)){
                    Assert.That(h.Controller.IsPinned(proxyPage),Is.False);
                    h.Controller.Tick(1,0,0,h.Uploader);h.Store.Complete(0);h.Controller.Tick(2,0,0,h.Uploader);
                    Assert.That(h.Controller.RootReady,Is.True);
                }
            }
        }

        [Test]
        public void ArchiveRejectsBadHeaderMetadataAndTruncatedExtent(){
            byte[] valid=VirtualGeometryStreamCodec.Write(CreatePage());
            byte[] badHeader=(byte[])valid.Clone();badHeader[12]^=0x40;
            Assert.Throws<InvalidDataException>(()=>VirtualGeometryStreamCodec.ReadMetadata(new MemoryStream(badHeader)));
            byte[] badMetadata=(byte[])valid.Clone();badMetadata[VirtualGeometryStreamCodec.HeaderBytes]^=0x40;
            Assert.Throws<InvalidDataException>(()=>VirtualGeometryStreamCodec.ReadMetadata(new MemoryStream(badMetadata)));
            Array.Resize(ref valid,valid.Length-1);
            Assert.Throws<InvalidDataException>(()=>VirtualGeometryStreamCodec.ReadMetadata(new MemoryStream(valid)));
        }

        [UnityTest]
        public IEnumerator FileStoreReadsOnlyRequestedRangeAndObservesCorruptionMissingAndCancel(){
            string path=Path.Combine(Path.GetTempPath(),"vg-stream-"+Guid.NewGuid().ToString("N")+".vgpage");
            byte[] bytes=VirtualGeometryStreamCodec.Write(CreatePage());
            VirtualGeometryStreamMetadata metadata=VirtualGeometryStreamCodec.ReadMetadata(new MemoryStream(bytes));
            try{
                File.WriteAllBytes(path,bytes);
                var store=new VirtualGeometryFilePageStore(path,metadata);
                Task<VirtualGeometryPagePayload> pending=store.ReadAsync(2,CancellationToken.None);
                while(!pending.IsCompleted){yield return null;}
                VirtualGeometryPagePayload payload=pending.GetAwaiter().GetResult();
                Assert.That(payload.ByteCount,Is.EqualTo(metadata.Pages[2].ByteCount));
                Assert.That(store.BytesRead,Is.EqualTo(metadata.Pages[2].ByteCount));
                bytes[bytes.Length-1]^=0x40;File.WriteAllBytes(path,bytes);
                pending=store.ReadAsync(2,CancellationToken.None);while(!pending.IsCompleted){yield return null;}
                Assert.Throws<InvalidDataException>(()=>pending.GetAwaiter().GetResult());
                using(var file=new FileStream(path,FileMode.Open,FileAccess.Write)){file.SetLength(metadata.Pages[1].Offset);}
                pending=store.ReadAsync(2,CancellationToken.None);while(!pending.IsCompleted){yield return null;}
                Assert.Throws<EndOfStreamException>(()=>pending.GetAwaiter().GetResult());
                var delayed=new VirtualGeometryFilePageStore(path,metadata,1000);
                using(var cancellation=new CancellationTokenSource()){
                    pending=delayed.ReadAsync(0,cancellation.Token);cancellation.Cancel();
                    while(!pending.IsCompleted){yield return null;}
                    Assert.Catch<OperationCanceledException>(()=>pending.GetAwaiter().GetResult());
                }
            }finally{File.Delete(path);}
        }
        private static VirtualGeometryStreamMetadata Metadata(){
            return VirtualGeometryStreamCodec.ReadMetadata(new MemoryStream(VirtualGeometryStreamCodec.Write(CreatePage())));
        }

        private static VirtualGeometryStreamingSettings Settings(VirtualGeometryStreamMetadata metadata,int slots,int queue,int transfer){
            return new VirtualGeometryStreamingSettings{
                GpuGeometryBudgetBytes=metadata.SlotGeometryBytes*slots,CpuCacheBudgetBytes=4096,IoConcurrency=1,
                TransferBytesPerFrame=transfer,RequestQueueCapacity=queue,RequestTimeoutFrames=1000
            };
        }

        private sealed class Harness:IDisposable{
            public VirtualGeometryStreamingSettings Settings;public ControlledStore Store;
            public FakeUploader Uploader=new FakeUploader();public VirtualGeometryStreamingController Controller;
            public Harness(int slots,int queue,int transfer):this(Metadata(),slots,queue,transfer){}
            public Harness(VirtualGeometryStreamMetadata metadata,int slots,int queue,int transfer){
                this.Settings=VirtualGeometryStreamingTests.Settings(metadata,slots,queue,transfer);
                this.Store=new ControlledStore(metadata);this.Controller=new VirtualGeometryStreamingController(metadata,this.Settings,this.Store);
            }
            public void Load(int page,int frame){
                this.Controller.Tick(frame,0,0,this.Uploader);this.Store.Complete(page);this.Controller.Tick(frame+1,0,0,this.Uploader);
            }
            public void Dispose(){this.Controller.Dispose();}
        }

        private sealed class ControlledStore:IVirtualGeometryPageStore{
            private readonly Dictionary<int,TaskCompletionSource<VirtualGeometryPagePayload>> pending=new Dictionary<int,TaskCompletionSource<VirtualGeometryPagePayload>>();
            private readonly VirtualGeometryStreamMetadata metadata;
            public readonly List<int> Requested=new List<int>();
            public ControlledStore(VirtualGeometryStreamMetadata metadata){this.metadata=metadata;}
            public Task<VirtualGeometryPagePayload> ReadAsync(int page,CancellationToken token){
                this.Requested.Add(page);var completion=new TaskCompletionSource<VirtualGeometryPagePayload>();this.pending[page]=completion;
                token.Register(()=>completion.TrySetCanceled());return completion.Task;
            }
            public void Complete(int page){
                VirtualGeometryPageDescriptor descriptor=this.metadata.Pages[page];
                this.pending[page].SetResult(new VirtualGeometryPagePayload(new VirtualGeometryPackedVertex[descriptor.VertexCount],new uint[descriptor.IndexCount]));
            }
            public void Fail(int page){this.pending[page].SetException(new InvalidDataException("Injected corrupt page."));}
        }

        private sealed class FeedbackView{
            public readonly float InstanceX;public readonly Func<int,Vector3> Camera;public readonly HashSet<int> Refined=new HashSet<int>();
            public int Pending;public string LastCut;public int CutChanges;public int MinTriangles=int.MaxValue;public int MaxTriangles;
            public FeedbackView(float instanceX,Func<int,Vector3> camera){this.InstanceX=instanceX;this.Camera=camera;}
        }

        /// <summary>CPU参照選択→非同期feedback→controllerの閉ループ。GPU readbackと同じくinstance×cameraごとに同時2件までとし、2〜4 frame遅れで届ける。</summary>
        private sealed class FeedbackLoop{
            private sealed class Pending{public long Arrival;public long Frame;public FeedbackView View;public VirtualGeometryPageFeedback[] Feedback;}
            public readonly FeedbackView[] Views;public readonly VirtualGeometryStreamingController Controller;public long EvictionsAfterWarmup;
            private readonly VirtualGeometryStreamMetadata metadata;private readonly VirtualGeometryPage page;private readonly FakeUploader uploader=new FakeUploader();
            public FeedbackLoop(VirtualGeometryStreamMetadata metadata,long gpuBudgetBytes,FeedbackView[] views){
                this.metadata=metadata;this.Views=views;
                this.page=new VirtualGeometryPage(Array.Empty<VirtualGeometryPackedVertex>(),Array.Empty<uint>(),metadata.Clusters,metadata.Nodes,metadata.Roots,metadata.Roots.Length,metadata.Bounds);
                var settings=new VirtualGeometryStreamingSettings{GpuGeometryBudgetBytes=gpuBudgetBytes,CpuCacheBudgetBytes=metadata.MaxPageBytes*4L,
                    IoConcurrency=2,TransferBytesPerFrame=1024,RequestQueueCapacity=16,RequestTimeoutFrames=120};
                this.Controller=new VirtualGeometryStreamingController(metadata,settings,new ImmediateStore(metadata));
            }
            public void Run(int frames,int warmup){
                var random=new System.Random(1);var queue=new List<Pending>();long submitted=0;long evictionsAtWarmup=0;
                for(int frame=1;frame<=frames;frame++){
                    this.Controller.Tick(frame,submitted,Math.Max(0,submitted-1),this.uploader);
                    foreach(FeedbackView view in this.Views){
                        if(!this.uploader.Resident.Contains(0)){continue;}
                        var context=new VirtualGeometryLodSelectionContext(Matrix4x4.Translate(new Vector3(view.InstanceX,0,0)),view.Camera(frame),false,60,5,1080,0.1f,2,0.1f);
                        VirtualGeometryLodSelectionResult result=VirtualGeometryLodSelector.Select(this.page,context,page=>this.uploader.Resident.Contains((int)page),view.Refined);
                        foreach(int node in result.RefinedNodes){view.Refined.Add(node);}
                        foreach(int node in result.SelectedNodes){view.Refined.Remove(node);}
                        var sorted=new List<int>(result.SelectedNodes);sorted.Sort();string cut=string.Join(",",sorted);
                        int triangles=0;foreach(int cluster in result.SelectedClusters){triangles+=(int)this.metadata.Clusters[cluster].IndexCount/3;}
                        if(frame>warmup){
                            if(view.LastCut!=null&&view.LastCut!=cut){view.CutChanges++;}
                            view.MinTriangles=Math.Min(view.MinTriangles,triangles);view.MaxTriangles=Math.Max(view.MaxTriangles,triangles);
                        }
                        view.LastCut=cut;
                        if(view.Pending<2){view.Pending++;queue.Add(new Pending{Arrival=frame+random.Next(2,5),Frame=frame,View=view,Feedback=result.Feedback});}
                    }
                    submitted++;
                    for(int i=0;i<queue.Count;i++){
                        Pending item=queue[i];if(item.Arrival>frame){continue;}
                        queue.RemoveAt(i--);item.View.Pending--;
                        // GPUと同じく不足要求はqueue容量で切り詰め、touchはpageごとの最大優先度で返す。
                        int requests=0;var touches=new Dictionary<int,float>();
                        foreach(VirtualGeometryPageFeedback feedback in item.Feedback){
                            if(feedback.Missing){
                                if(requests++<16){this.Controller.Request((int)feedback.PageId,feedback.Priority,item.Frame);}
                            }else if(!touches.TryGetValue((int)feedback.PageId,out float priority)||priority<feedback.Priority){
                                touches[(int)feedback.PageId]=feedback.Priority;
                            }
                        }
                        foreach(var touch in touches){this.Controller.Touch(touch.Key,touch.Value,item.Frame);}
                    }
                    if(frame==warmup){evictionsAtWarmup=this.Controller.Statistics.Evictions;}
                }
                this.EvictionsAfterWarmup=this.Controller.Statistics.Evictions-evictionsAtWarmup;
                this.Controller.Dispose();
            }
        }

        private sealed class ImmediateStore:IVirtualGeometryPageStore{
            private readonly VirtualGeometryStreamMetadata metadata;
            public ImmediateStore(VirtualGeometryStreamMetadata metadata){this.metadata=metadata;}
            public Task<VirtualGeometryPagePayload> ReadAsync(int page,CancellationToken token){
                VirtualGeometryPageDescriptor descriptor=this.metadata.Pages[page];
                return Task.FromResult(new VirtualGeometryPagePayload(new VirtualGeometryPackedVertex[descriptor.VertexCount],new uint[descriptor.IndexCount]));
            }
        }

        private sealed class FakeUploader:IVirtualGeometryPageUploader{
            public readonly HashSet<int> Resident=new HashSet<int>();public readonly Dictionary<int,int> Generations=new Dictionary<int,int>();public readonly Dictionary<int,int> Slots=new Dictionary<int,int>();
            public void UploadVertices(int slot,int offset,VirtualGeometryPackedVertex[] vertices,int sourceOffset,int count){}
            public void UploadIndices(int slot,int offset,uint[] indices,int sourceOffset,int count){}
            public void Publish(int page,int slot,int generation){this.Resident.Add(page);this.Generations[page]=generation;this.Slots[page]=slot;}
            public void Invalidate(int page){this.Resident.Remove(page);}
        }

        /// <summary>rootと2 leaf。片方のleafは3 clusterを持ち、他のpageの3倍の添字数になる(大きさの違うpage)。</summary>
        private static VirtualGeometryPage CreateUnevenPage(){
            var vertices=new[]{
                new VirtualGeometryPackedVertex(Vector3.zero,Vector3.back,Vector2.zero),
                new VirtualGeometryPackedVertex(Vector3.right,Vector3.back,Vector2.right),
                new VirtualGeometryPackedVertex(Vector3.up,Vector3.back,Vector2.up)};
            var clusters=new VirtualGeometryCluster[5];
            for(int i=0;i<clusters.Length;i++){clusters[i]=new VirtualGeometryCluster{BoundsCenter=Vector3.zero,BoundsRadius=1,FirstIndex=(uint)(i*3),IndexCount=3,MaterialSlot=0};}
            var nodes=new VirtualGeometryHierarchyNode[3];
            nodes[0]=new VirtualGeometryHierarchyNode{BoundsCenter=Vector3.zero,BoundsRadius=1,FirstCluster=0,ClusterCount=1,FirstChild=1,ChildCount=2,LeafCount=2,Level=1,GeometricError=1};
            nodes[1]=new VirtualGeometryHierarchyNode{BoundsCenter=Vector3.zero,BoundsRadius=1,FirstCluster=1,ClusterCount=1,LeafStart=0,LeafCount=1};
            nodes[2]=new VirtualGeometryHierarchyNode{BoundsCenter=Vector3.zero,BoundsRadius=1,FirstCluster=2,ClusterCount=3,LeafStart=1,LeafCount=1};
            var indices=new uint[15];
            for(int i=0;i<indices.Length;i++){indices[i]=(uint)(i%3);}
            return new VirtualGeometryPage(vertices,indices,clusters,nodes,new uint[]{0},1,new Bounds(Vector3.zero,Vector3.one*2));
        }

        /// <summary>root→単一子の段→2 leafの階層。単一子の段がProxyと同じ段になる。</summary>
        private static VirtualGeometryPage CreateChainPage(){
            var vertices=new[]{
                new VirtualGeometryPackedVertex(Vector3.zero,Vector3.back,Vector2.zero),
                new VirtualGeometryPackedVertex(Vector3.right,Vector3.back,Vector2.right),
                new VirtualGeometryPackedVertex(Vector3.up,Vector3.back,Vector2.up)};
            var clusters=new VirtualGeometryCluster[4];var nodes=new VirtualGeometryHierarchyNode[4];
            for(int i=0;i<4;i++){
                clusters[i]=new VirtualGeometryCluster{BoundsCenter=Vector3.zero,BoundsRadius=1,FirstIndex=(uint)(i*3),IndexCount=3,MaterialSlot=0};
                nodes[i]=new VirtualGeometryHierarchyNode{BoundsCenter=Vector3.zero,BoundsRadius=1,FirstCluster=(uint)i,ClusterCount=1,LeafCount=1};
            }
            nodes[0].FirstChild=1;nodes[0].ChildCount=1;nodes[0].LeafCount=2;nodes[0].Level=2;nodes[0].GeometricError=2;
            nodes[1].FirstChild=2;nodes[1].ChildCount=2;nodes[1].LeafCount=2;nodes[1].Level=1;nodes[1].GeometricError=1;
            nodes[2].LeafStart=0;nodes[3].LeafStart=1;
            return new VirtualGeometryPage(vertices,new uint[]{0,1,2,0,1,2,0,1,2,0,1,2},clusters,nodes,new uint[]{0},1,new Bounds(Vector3.zero,Vector3.one*2));
        }

        private static VirtualGeometryPage CreatePage(){
            var vertices=new[]{
                new VirtualGeometryPackedVertex(Vector3.zero,Vector3.back,Vector2.zero),
                new VirtualGeometryPackedVertex(Vector3.right,Vector3.back,Vector2.right),
                new VirtualGeometryPackedVertex(Vector3.up,Vector3.back,Vector2.up)};
            var clusters=new VirtualGeometryCluster[3];var nodes=new VirtualGeometryHierarchyNode[3];
            for(int i=0;i<3;i++){
                clusters[i]=new VirtualGeometryCluster{BoundsCenter=Vector3.zero,BoundsRadius=1,FirstIndex=(uint)(i*3),IndexCount=3,MaterialSlot=0};
                nodes[i]=new VirtualGeometryHierarchyNode{BoundsCenter=Vector3.zero,BoundsRadius=1,FirstCluster=(uint)i,ClusterCount=1,LeafCount=1,LeafStart=(uint)Mathf.Max(0,i-1)};
            }
            nodes[0].FirstChild=1;nodes[0].ChildCount=2;nodes[0].LeafCount=2;nodes[0].Level=1;nodes[0].GeometricError=1;
            return new VirtualGeometryPage(vertices,new uint[]{0,1,2,0,1,2,0,1,2},clusters,nodes,new uint[]{0},1,new Bounds(Vector3.zero,Vector3.one*2));
        }
    }
}
