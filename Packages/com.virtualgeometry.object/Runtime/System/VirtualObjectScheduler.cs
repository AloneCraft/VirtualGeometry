using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 表現の準備・表示・解放を、予算(<see cref="VirtualObjectBudget"/>)の範囲で行う。安全速度の推定もここで行う。
    /// <list type="bullet">
    /// <item>準備: 要求を優先度の高い順に、同時の数と memory の上限の範囲で始める。memory が足りなければ、要求されていない準備済みの表現を古い順に解放する。</item>
    /// <item>表示: 1 frame の数と main thread の時間の上限の範囲で、優先度の高い順に Activate する。</item>
    /// <item>解放: しばらく要求されていない準備済みの表現(常駐の帯を除く)を解放する。準備中で要求が途絶えたものは取り消す。</item>
    /// </list>
    /// </summary>
    internal sealed class VirtualObjectScheduler{
        private struct Key:IEquatable<Key>{
            public VirtualObjectNode Node;
            public int Band;

            public bool Equals(Key other){
                return ReferenceEquals(this.Node,other.Node)&&this.Band==other.Band;
            }

            public override bool Equals(object other){
                return other is Key key&&this.Equals(key);
            }

            public override int GetHashCode(){
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this.Node)*31+this.Band;
            }
        }

        private struct Pending{
            public Key Key;
            public float Priority;
            public bool Deficit;
        }

        private sealed class Live{
            public VirtualObjectNode Node;
            public int Band;
            public IVirtualObjectRepresentation Representation;
            public RepresentationCost Cost;
            public int LastUsedFrame;
        }

        // 準備中の表現の要求が途絶えてから取り消すまでの frame 数。
        private const int CancelFrames=60;

        private readonly VirtualObjectContext context;
        private readonly Dictionary<Key,Pending> requests=new Dictionary<Key,Pending>();
        private readonly Dictionary<Key,Pending> carried=new Dictionary<Key,Pending>();
        private readonly Dictionary<Key,float> activations=new Dictionary<Key,float>();
        private readonly Dictionary<Key,Live> live=new Dictionary<Key,Live>();
        private readonly List<Pending> order=new List<Pending>();
        private readonly List<Key> scratch=new List<Key>();
        private float averagePrepareSeconds=0.25f;
        private bool measured;
        private int activationsThisFrame;
        private float activationMilliseconds;

        public VirtualObjectScheduler(VirtualObjectContext context){
            this.context=context;
        }

        /// <summary>準備の時間の実測の平滑(秒。実測が無ければ 0.25)。</summary>
        public float AveragePrepareSeconds=>this.averagePrepareSeconds;
        public int StarvedThisFrame{get;private set;}

        public void BeginFrame(){
            this.requests.Clear();
            this.activations.Clear();
            this.StarvedThisFrame=0;
            this.activationsThisFrame=0;this.activationMilliseconds=0f;
            foreach(KeyValuePair<Key,Pending> pair in this.carried){this.requests[pair.Key]=pair.Value;}
            this.carried.Clear();
        }

        /// <summary>帯の表現を要求する(無ければ作る)。deficit は「見せている帯が望む帯より粗い」要求。</summary>
        public void Request(VirtualObjectNode node,int band,float priority,bool deficit){
            if(band<0||band>=node.Representations.Length){return;}
            RepresentationBand definition=node.Bands[band];
            if(definition.IsChildren){return;}
            if(node.Representations[band]==null){
                try{
                    node.Representations[band]=VirtualObjectRepresentations.Create(new RepresentationRequest(node,band,definition));
                }catch(Exception error){
                    this.context.System.ReportError(null,new InvalidOperationException(node+" の帯 "+band+"("+definition.Representation+")を作れません: "+error.Message,error));
                    return;
                }
            }
            node.LastRequestedFrame[band]=this.context.Frame;
            var key=new Key{Node=node,Band=band};
            if(this.requests.TryGetValue(key,out Pending existing)&&existing.Priority>=priority&&(existing.Deficit||!deficit)){return;}
            var pending=new Pending{Key=key,Priority=priority,Deficit=deficit};
            if(existing.Key.Node!=null){pending.Deficit=existing.Deficit||deficit;pending.Priority=Mathf.Max(existing.Priority,priority);}
            this.requests[key]=pending;
            // Step の外(瞬間移動の前の先読み)で要求されたら、次の frame にも持ち越す。
            this.carried[key]=pending;
        }

        public void RequestActivation(VirtualObjectNode node,int band,float priority){
            var key=new Key{Node=node,Band=band};
            if(this.activations.TryGetValue(key,out float existing)&&existing>=priority){return;}
            this.activations[key]=priority;
        }

        /// <summary>準備を始め、準備中のものを進める。</summary>
        public void Prepare(){
            this.carried.Clear();
            VirtualObjectBudget budget=this.context.Budget;
            int preparing=0;
            foreach(Live entry in this.live.Values){
                if(entry.Representation.State==RepresentationState.Preparing){preparing++;}
            }
            // 新しい準備を、優先度の高い順に(不足を先に)。
            this.order.Clear();
            foreach(Pending pending in this.requests.Values){
                IVirtualObjectRepresentation representation=pending.Key.Node.Representations[pending.Key.Band];
                if(representation!=null&&representation.State==RepresentationState.Missing){this.order.Add(pending);}
            }
            this.order.Sort(ComparePending);
            for(int i=0;i<this.order.Count;i++){
                if(preparing>=budget.MaxConcurrentPrepares){break;}
                Key key=this.order[i].Key;
                IVirtualObjectRepresentation representation=key.Node.Representations[key.Band];
                RepresentationCost cost=representation.EstimateCost();
                if(!this.MakeRoom(cost)){
                    this.StarvedThisFrame++;
                    continue;
                }
                var cancellation=new CancellationTokenSource();
                key.Node.Cancellations[key.Band]=cancellation;
                key.Node.PrepareStartTime[key.Band]=this.context.Time;
                this.live[key]=new Live{Node=key.Node,Band=key.Band,Representation=representation,Cost=cost,LastUsedFrame=this.context.Frame};
                representation.BeginPrepare(this.context,cancellation.Token);
                preparing++;
            }
            // 準備中のものを進める。要求が途絶えたものは取り消す。
            this.scratch.Clear();
            foreach(KeyValuePair<Key,Live> pair in this.live){this.scratch.Add(pair.Key);}
            for(int i=0;i<this.scratch.Count;i++){
                Key key=this.scratch[i];
                Live entry=this.live[key];
                if(this.requests.ContainsKey(key)){entry.LastUsedFrame=this.context.Frame;}
                IVirtualObjectRepresentation representation=entry.Representation;
                if(representation.State==RepresentationState.Preparing){
                    if(this.context.Frame-key.Node.LastRequestedFrame[key.Band]>CancelFrames){
                        CancellationTokenSource cancellation=key.Node.Cancellations[key.Band];
                        if(cancellation!=null){cancellation.Cancel();}
                    }
                    float elapsed=this.context.Time-key.Node.PrepareStartTime[key.Band];
                    if(elapsed<budget.InjectedPrepareDelaySeconds){continue;}
                    if(representation.Poll(this.context)){
                        this.Measure(elapsed);
                    }
                }
                if(representation.State==RepresentationState.Failed||representation.State==RepresentationState.Released){
                    this.Forget(key);
                    // 失敗・取り消しの表現は捨て、次に要求されたら作り直す(失敗は Failures に数える)。
                    if(representation.State==RepresentationState.Released){key.Node.Representations[key.Band]=null;}
                }
            }
        }

        private void Measure(float elapsed){
            if(!this.measured){
                this.averagePrepareSeconds=elapsed;this.measured=true;
                return;
            }
            this.averagePrepareSeconds=Mathf.Lerp(this.averagePrepareSeconds,elapsed,0.2f);
        }

        private static int ComparePending(Pending a,Pending b){
            if(a.Deficit!=b.Deficit){
                if(a.Deficit){return -1;}
                return 1;
            }
            return b.Priority.CompareTo(a.Priority);
        }

        // cost を足しても memory の上限を超えないように、要求されていない準備済みの表現を古い順に解放する。
        private bool MakeRoom(RepresentationCost cost){
            VirtualObjectBudget budget=this.context.Budget;
            this.Totals(out long gpu,out long cpu);
            if(gpu+cost.GpuBytes<=budget.GpuBytes&&cpu+cost.CpuBytes<=budget.CpuBytes){return true;}
            var candidates=new List<KeyValuePair<Key,Live>>();
            foreach(KeyValuePair<Key,Live> pair in this.live){
                if(this.IsEvictable(pair.Key,pair.Value)){candidates.Add(pair);}
            }
            candidates.Sort((a,b)=>a.Value.LastUsedFrame.CompareTo(b.Value.LastUsedFrame));
            for(int i=0;i<candidates.Count;i++){
                if(gpu+cost.GpuBytes<=budget.GpuBytes&&cpu+cost.CpuBytes<=budget.CpuBytes){break;}
                gpu-=candidates[i].Value.Cost.GpuBytes;cpu-=candidates[i].Value.Cost.CpuBytes;
                this.ReleaseOne(candidates[i].Key);
            }
            return gpu+cost.GpuBytes<=budget.GpuBytes&&cpu+cost.CpuBytes<=budget.CpuBytes;
        }

        private bool IsEvictable(Key key,Live entry){
            if(entry.Representation.State!=RepresentationState.Ready){return false;}
            if(key.Node.Bands[key.Band].Resident){return false;}
            if(key.Node.ShownBand==key.Band||key.Node.HandoffTarget==key.Band){return false;}
            return !this.requests.ContainsKey(key);
        }

        private void Totals(out long gpu,out long cpu){
            gpu=0;cpu=0;
            foreach(Live entry in this.live.Values){gpu+=entry.Cost.GpuBytes;cpu+=entry.Cost.CpuBytes;}
        }

        /// <summary>表示を予算の範囲で行う。</summary>
        public void Activate(){
            VirtualObjectBudget budget=this.context.Budget;
            var list=new List<KeyValuePair<Key,float>>(this.activations);
            list.Sort((a,b)=>b.Value.CompareTo(a.Value));
            long start=Stopwatch.GetTimestamp();
            for(int i=0;i<list.Count;i++){
                if(this.activationsThisFrame>=budget.MaxActivationsPerFrame){break;}
                float elapsed=(float)((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency);
                if(elapsed>=budget.MainThreadMillisecondsPerFrame&&this.activationsThisFrame>0){break;}
                Key key=list[i].Key;
                IVirtualObjectRepresentation representation=key.Node.Representations[key.Band];
                if(representation==null||representation.State!=RepresentationState.Ready){continue;}
                representation.Activate(this.context);
                this.activationsThisFrame++;
            }
            this.activationMilliseconds=(float)((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency);
        }

        /// <summary>しばらく要求されていない準備済みの表現を解放する。</summary>
        public void ReleaseIdle(){
            int limit=this.context.Budget.IdleReleaseFrames;
            this.scratch.Clear();
            foreach(KeyValuePair<Key,Live> pair in this.live){
                if(!this.IsEvictable(pair.Key,pair.Value)){continue;}
                if(this.context.Frame-pair.Value.LastUsedFrame>limit){this.scratch.Add(pair.Key);}
            }
            for(int i=0;i<this.scratch.Count;i++){this.ReleaseOne(this.scratch[i]);}
        }

        private void ReleaseOne(Key key){
            IVirtualObjectRepresentation representation=key.Node.Representations[key.Band];
            if(representation!=null){representation.Release(this.context);}
            CancellationTokenSource cancellation=key.Node.Cancellations[key.Band];
            if(cancellation!=null){cancellation.Dispose();key.Node.Cancellations[key.Band]=null;}
            key.Node.Representations[key.Band]=null;
            this.Forget(key);
        }

        private void Forget(Key key){
            this.live.Remove(key);
        }

        /// <summary>ノードとその子の、全部の表現を解放する。</summary>
        public void ReleaseSubtree(VirtualObjectNode node,bool includeSelf){
            if(node.ChildList!=null){
                foreach(VirtualObjectNode child in node.ChildList){this.ReleaseSubtree(child,true);}
                if(includeSelf){node.ChildList=null;}
            }
            if(!includeSelf){return;}
            for(int b=0;b<node.Representations.Length;b++){
                CancellationTokenSource cancellation=node.Cancellations[b];
                if(cancellation!=null){cancellation.Cancel();}
                var key=new Key{Node=node,Band=b};
                if(node.Representations[b]!=null){this.ReleaseOne(key);}
            }
            node.ShownBand=-1;node.HandoffTarget=-1;node.HandoffCount=0;
        }

        public void FillStatus(ref VirtualObjectStatus status){
            foreach(Live entry in this.live.Values){
                RepresentationState state=entry.Representation.State;
                if(state==RepresentationState.Preparing){status.Preparing++;}
                if(state==RepresentationState.Ready){status.Ready++;}
                if(state==RepresentationState.Active){status.Active++;}
                status.GpuBytes+=entry.Cost.GpuBytes;status.CpuBytes+=entry.Cost.CpuBytes;
            }
            status.Activations=this.activationsThisFrame;
            status.ActivationMilliseconds=this.activationMilliseconds;
            status.AveragePrepareSeconds=this.averagePrepareSeconds;
        }

        /// <summary>
        /// 安全速度の推定(締切順の実行可能性)。未完了の要求 i の締切 t_i = (今の距離 − 帯の入口の距離) / 接近の速さ(望む帯の不足は 0)、
        /// 必要な作業 w_i = 残りの準備の時間 / 同時の数。締切順に並べた累積 W_k について、v_safe = v_now × min_k (t_k / W_k)。
        /// 締切は速さに反比例するので、この倍率なら全部が締切に間に合う。止まっているときは、camera の前方へ 1 m/s で進むと仮定する。締切が無ければ正の無限大。
        /// </summary>
        public float EstimateSafeSpeed(IReadOnlyList<VirtualObjectNode> roots){
            IReadOnlyList<VirtualObjectViewerState> viewers=this.context.Viewers;
            if(viewers.Count==0){return float.PositiveInfinity;}
            var items=new List<float2>();
            float baseSpeed=0f;
            for(int i=0;i<viewers.Count;i++){baseSpeed=Mathf.Max(baseSpeed,viewers[i].Velocity.magnitude);}
            bool assumed=baseSpeed<0.01f;
            if(assumed){baseSpeed=1f;}
            int concurrency=Mathf.Max(1,this.context.Budget.MaxConcurrentPrepares);
            foreach(Pending pending in this.requests.Values){
                VirtualObjectNode node=pending.Key.Node;
                IVirtualObjectRepresentation representation=node.Representations[pending.Key.Band];
                if(representation==null){continue;}
                RepresentationState state=representation.State;
                if(state!=RepresentationState.Missing&&state!=RepresentationState.Preparing){continue;}
                if(node.Bands[pending.Key.Band].Resident&&!pending.Deficit){continue;}
                float remaining=this.averagePrepareSeconds;
                if(state==RepresentationState.Preparing){
                    remaining=Mathf.Max(0f,this.averagePrepareSeconds-(this.context.Time-node.PrepareStartTime[pending.Key.Band]));
                }
                remaining=Mathf.Max(remaining,this.context.Budget.InjectedPrepareDelaySeconds-(this.context.Time-node.PrepareStartTime[pending.Key.Band]));
                float work=remaining/concurrency;
                if(!(work>0f)){continue;}
                float deadline;
                if(pending.Deficit){
                    deadline=0f;
                }else if(!this.TryDeadline(node,pending.Key.Band,assumed,out deadline)){
                    continue;
                }
                items.Add(new float2(deadline,work));
            }
            if(items.Count==0){return float.PositiveInfinity;}
            items.Sort((a,b)=>a.x.CompareTo(b.x));
            float cumulative=0f;float ratio=float.PositiveInfinity;
            for(int i=0;i<items.Count;i++){
                cumulative+=items[i].y;
                ratio=Mathf.Min(ratio,items[i].x/cumulative);
            }
            return baseSpeed*ratio;
        }

        // 帯が要る時刻までの時間(今の速度、止まっていれば前方へ 1 m/s)。近づいていなければ false。
        // 親が子の帯を望む前に先に準備している子(PrefetchOnly)は、親が子の帯に入る時刻を締切にする。
        private bool TryDeadline(VirtualObjectNode node,int band,bool assumed,out float deadline){
            deadline=0f;
            if(node.PrefetchOnly&&node.Parent!=null){
                int childrenBand=ChildrenBand(node.Parent);
                if(childrenBand<0){return false;}
                return this.TryDeadline(node.Parent,childrenBand,assumed,out deadline);
            }
            float enter=node.Bands[band].EnterPixels;
            if(!(enter>0f)){return false;}
            float best=float.PositiveInfinity;
            Vector3 center=node.CenterOffset;
            IReadOnlyList<VirtualObjectViewerState> viewers=this.context.Viewers;
            for(int i=0;i<viewers.Count;i++){
                VirtualObjectViewerState viewer=viewers[i];
                if(!LogicalVector3.TrySubtract(node.Origin,viewer.Position,out LogicalVector3 difference)||!difference.TryToFloat3(out float3 offset)){continue;}
                // 画面上の大きさ(VirtualObjectSystem.ScreenPixels)と同じく、境界の球の中心で測り、球の中では入口を越えたとする。
                Vector3 relative=(Vector3)offset+center;
                float distance=relative.magnitude;
                if(!(distance>0f)){continue;}
                Vector3 velocity=viewer.Velocity;
                if(assumed){velocity=viewer.Rotation*Vector3.forward;}
                float closing=Vector3.Dot(velocity,relative/distance);
                float bandDistance=Mathf.Max(node.Radius*viewer.ProjectionScale/enter,node.Radius);
                if(distance<=bandDistance){best=0f;break;}
                if(!(closing>1e-4f)){continue;}
                best=Mathf.Min(best,(distance-bandDistance)/closing);
            }
            if(float.IsPositiveInfinity(best)){return false;}
            deadline=best;
            return true;
        }

        private static int ChildrenBand(VirtualObjectNode node){
            RepresentationBand[] bands=node.Bands;
            for(int i=0;i<bands.Length;i++){
                if(bands[i].IsChildren){return i;}
            }
            return -1;
        }
    }
}
