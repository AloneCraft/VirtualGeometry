using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Omni.PlanetSurface{
    /// <summary>
    /// 月面の固定領域を共有する法線atlasです。保持中の領域は追い出さず、未使用だけをLRUで再利用します。
    /// GPU生成とコピーはGraphics queue上で実行し、公開にCPU読み戻しを要求しません。
    /// </summary>
    public sealed class SurfaceDetailAtlas:IDisposable{
        internal sealed class Entry{
            internal SurfaceDerivedKey Key;
            internal int Slot,References;
            internal bool Ready;
            internal float ReadyTime;
            internal LinkedListNode<Entry> Node;
        }

        /// <summary>描画側が保持する参照です。Dispose後は同じslotが再利用されても公開情報を取得できません。</summary>
        public sealed class Lease:IDisposable{
            private SurfaceDetailAtlas owner;
            private readonly Entry entry;
            internal Lease(SurfaceDetailAtlas owner,Entry entry){this.owner=owner;this.entry=entry;}
            public bool TryGetBinding(out int layer,out float readyTime){
                layer=-1;readyTime=0;
                if(this.owner==null||this.owner.disposed||!this.entry.Ready){return false;}
                layer=this.entry.Slot;readyTime=this.entry.ReadyTime;return true;
            }
            public void Dispose(){
                if(this.owner==null){return;}
                if(!this.owner.disposed){this.entry.References--;this.owner.leaseCount--;}
                this.owner=null;
            }
        }

        private readonly IProceduralSurfaceHeightSource source;
        private readonly SurfaceDetailTileGenerator generator;
        private readonly string sourceHash;
        private readonly int resolution;
        private readonly long cpuBudget,gpuBudget,cpuFixed,gpuFixed;
        private readonly Entry[] slots;
        private readonly Dictionary<SurfaceDerivedKey,Entry> entries=new();
        private readonly LinkedList<Entry> order=new();
        private SurfaceDetailPatchRequest request;
        private SurfaceDetailTileGenerator.CompactWorkspace workspace;
        private Entry work;
        private int leaseCount;
        private bool disposed;
        public RenderTexture Texture{get;private set;}
        public SurfaceDetailRegions Regions{get;}
        /// <summary>このcacheの管理予約量です。基礎データ・LODメッシュ・sourceのanchor cacheは呼出し側で別計上します。</summary>
        public long CpuReservedBytes{
            get{if(this.disposed){return 0;}return this.cpuFixed+this.leaseCount*96L;}
        }
        /// <summary>atlasと最大1件の生成作業領域の予約量です。ドライバー実測の値ではありません。</summary>
        public long GpuReservedBytes{
            get{if(this.disposed){return 0;}return this.gpuFixed;}
        }
        public int Count=>this.entries.Count;
        public int ReadyCount{get;private set;}
        public int PreparedPixels{get;private set;}
        public bool IsUploadPending=>this.request!=null&&this.request.IsComplete;
        /// <summary>接近フレームより前に固定GPU作業領域とkernelを準備済みかを示します。</summary>
        public bool IsWorkspacePrepared=>this.workspace!=null&&!this.disposed;

        /// <summary>予算はこのatlasへ割り当てた上限bytesです。正本のhashとsourceは寿命中変更しません。</summary>
        public SurfaceDetailAtlas(IProceduralSurfaceHeightSource source,ComputeShader shader,string sourceHash,
            int resolution,int capacity,long cpuBudget,long gpuBudget){
            if(source==null){throw new ArgumentNullException(nameof(source));}
            if(shader==null){throw new ArgumentNullException(nameof(shader));}
            if(string.IsNullOrWhiteSpace(sourceHash)){throw new ArgumentException("基礎データhashが必要です。",nameof(sourceHash));}
            if(resolution<16||resolution>512||capacity<1||capacity>256){
                throw new ArgumentOutOfRangeException(nameof(resolution));
            }
            int correctionSize=SurfaceDetailPatchRequest.GetCorrectionSize(resolution);
            long input=correctionSize*(long)correctionSize*4+source.NaturalSource.BandCount*48L;
            if(source.HasHeightOverrides){input+=correctionSize*(long)correctionSize*4;}
            long normal=(resolution+2L)*(resolution+2)*8;
            long output=(resolution+4L)*(resolution+4)*4+normal;
            // 管理領域は保守的に予約。多数の同一領域参照にも独立の上限を設けます。
            this.cpuFixed=4096+capacity*512L+input+8192;
            this.gpuFixed=normal*capacity+input+output;
            if(cpuBudget<this.cpuFixed+96||gpuBudget<this.gpuFixed){
                throw new ArgumentOutOfRangeException(nameof(cpuBudget),"atlasと生成作業領域の予算が不足しています。");
            }
            if(!SystemInfo.supports2DArrayTextures){throw new NotSupportedException("法線Texture2DArrayが必要です。");}
            this.source=source;this.generator=new SurfaceDetailTileGenerator(shader);this.sourceHash=sourceHash;
            this.resolution=resolution;this.cpuBudget=cpuBudget;this.gpuBudget=gpuBudget;
            this.Regions=new SurfaceDetailRegions(source.NaturalSource.RadiusMeters);this.slots=new Entry[capacity];
            this.Texture=new RenderTexture(resolution+2,resolution+2,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear){
                name="Surface normal atlas",dimension=TextureDimension.Tex2DArray,volumeDepth=capacity,
                wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear,useMipMap=false,
                hideFlags=HideFlags.DontSave
            };
            if(!this.Texture.Create()){
                SurfaceDetailTile.DestroyTransient(this.Texture);this.Texture=null;
                throw new InvalidOperationException("法線atlasを作成できません。");
            }
            try{
                this.PrepareWorkspace();
                // 初回Dispatchのshader/pipeline準備をLOD更新へ持ち込まないよう、結果を公開せずに1回だけ生成します。
                var prewarm=new SurfaceDetailPatchRequest(this.source,
                    new SurfaceTileKey(0,this.Regions.Level,0,0),this.source.NaturalSource.RadiusMeters,
                    this.resolution,this.Regions.SpanMeters/this.resolution,true);
                while(prewarm.Step()){}
                this.workspace.Generate(prewarm);
            }catch{
                this.workspace?.Dispose();this.workspace=null;
                this.Texture.Release();SurfaceDetailTile.DestroyTransient(this.Texture);this.Texture=null;
                throw;
            }
        }

        /// <summary>既存領域を共有します。満杯で全領域が保持中ならfalseで、既存描画を破壊しません。</summary>
        public bool TryAcquire(SurfaceTileKey region,out Lease lease){
            this.ThrowIfDisposed();lease=null;
            if(!this.Regions.TryMap(region,out var mapped,out _)||!mapped.Equals(region)){
                throw new ArgumentOutOfRangeException(nameof(region),"固定法線領域のキーが必要です。");
            }
            if(this.CpuReservedBytes+96>this.cpuBudget){return false;}
            var key=this.source.CreateDerivedKey(this.sourceHash,region,this.resolution);
            if(!this.entries.TryGetValue(key,out Entry entry)){
                int slot=Array.IndexOf(this.slots,null);
                if(slot<0){
                    var candidate=this.order.Last;
                    while(candidate!=null&&candidate.Value.References>0){candidate=candidate.Previous;}
                    if(candidate==null){return false;}
                    slot=candidate.Value.Slot;this.Remove(candidate.Value);
                }
                entry=new Entry{Key=key,Slot=slot};
                entry.Node=this.order.AddFirst(entry);this.entries.Add(key,entry);this.slots[slot]=entry;
            }else{
                this.order.Remove(entry.Node);this.order.AddFirst(entry.Node);
            }
            entry.References++;this.leaseCount++;lease=new Lease(this,entry);return true;
        }

        /// <summary>
        /// 1画素のCPU準備、または完成した1タイルのGPU転送を実行します。
        /// 呼出し側はフレーム予算を共有し、IsUploadPending時の単一転送時間も実測します。
        /// </summary>
        public bool Step(){
            this.ThrowIfDisposed();
            if(this.work!=null&&this.work.References==0){this.work=null;this.request=null;}
            if(this.work==null){
                var node=this.order.Last;
                while(node!=null&&(node.Value.Ready||node.Value.References==0)){node=node.Previous;}
                if(node==null){return false;}
                this.work=node.Value;
                this.request=new SurfaceDetailPatchRequest(this.source,this.work.Key.Region,this.source.NaturalSource.RadiusMeters,
                    this.resolution,this.Regions.SpanMeters/this.resolution,true);
            }
            if(!this.request.IsComplete){this.request.Step();this.PreparedPixels++;return true;}
            this.PrepareWorkspace();
            SurfaceDetailTile tile=this.workspace.Generate(this.request);
            Graphics.CopyTexture(tile.NormalRoughness,0,0,this.Texture,this.work.Slot,0);
            this.work.Ready=true;this.work.ReadyTime=Time.time;this.ReadyCount++;
            this.work=null;this.request=null;return true;
        }

        /// <summary>未完成領域も含めて解放します。残っているLeaseは即座に無効になります。</summary>
        public void Dispose(){
            if(this.disposed){return;}
            this.disposed=true;this.request=null;this.work=null;
            this.workspace?.Dispose();this.workspace=null;
            this.entries.Clear();this.order.Clear();Array.Clear(this.slots,0,this.slots.Length);
            this.leaseCount=0;this.ReadyCount=0;
            if(this.Texture!=null){this.Texture.Release();SurfaceDetailTile.DestroyTransient(this.Texture);this.Texture=null;}
        }

        private void Remove(Entry entry){
            if(entry.References!=0){throw new InvalidOperationException("保持中の法線領域を解放できません。");}
            if(this.work==entry){this.work=null;this.request=null;}
            this.entries.Remove(entry.Key);this.order.Remove(entry.Node);this.slots[entry.Slot]=null;
            if(entry.Ready){this.ReadyCount--;}
        }
        private void PrepareWorkspace(){
            if(this.workspace!=null){return;}
            long atlasBytes=(this.resolution+2L)*(this.resolution+2)*8*this.slots.Length;
            this.workspace=this.generator.CreateCompactWorkspace(this.resolution,this.source.NaturalSource.BandCount,
                this.gpuBudget-atlasBytes,this.source.HasHeightOverrides);
        }
        private void ThrowIfDisposed(){if(this.disposed){throw new ObjectDisposedException(nameof(SurfaceDetailAtlas));}}
    }
}
