using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;
using VirtualGeometry.World;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 仮想オブジェクトを動かす。ルート(この GameObject)の局所の Logical 座標に、<see cref="Sets"/> の実体を置き、
    /// 毎 frame、見る camera(<see cref="VirtualObjectViewer"/>)からの画面上の大きさで、ノードごとに表現の帯を選ぶ。
    /// <list type="bullet">
    /// <item>穴を出さない: 望む表現が準備できるまで、準備済みの最も細かい粗い表現を見せる。細かい表現が見えてから、粗い表現を外す(重ねてから外す)。</item>
    /// <item>"children" の帯では、分け方の番号ごとに子のノードを作り、子が全部見えてから親を外す。粗い方へ戻るときは、親が見えてから子を外す。</item>
    /// <item>予算(<see cref="VirtualObjectBudget"/>)の範囲で準備と表示を行い、移動の予測で先読みする。</item>
    /// <item>安全速度(<see cref="VirtualObjectStatus.SafeSpeed"/>)を公開する(強制はしない)。</item>
    /// </list>
    /// 判断は LateUpdate(camera の移動の後)。検証では <see cref="ManualStep"/> にして <see cref="Step"/> を直接呼ぶ(Edit Mode でも動く)。
    /// 配置は同じ GameObject の <see cref="IVirtualObjectPlacement"/> を実装した部品(無ければ Unity の world)。
    /// 地面は <see cref="Ground"/>(<see cref="GroundTerrain"/> を設定すれば惑星の地形)。
    /// </summary>
    [DefaultExecutionOrder(1000)]
    [DisallowMultipleComponent]
    public sealed class VirtualObjectSystem:MonoBehaviour{
        [SerializeField] private VirtualObjectSet[] sets=Array.Empty<VirtualObjectSet>();
        [SerializeField] private VirtualObjectBudget budget=new VirtualObjectBudget();
        [Tooltip("惑星の地形を地面にする(設定しなければ地面なし)。")]
        [SerializeField] private PlanetTerrainSource groundTerrain;
        [Tooltip("true なら LateUpdate で進めない(Step を直接呼ぶ。検証用)。")]
        [SerializeField] private bool manualStep;

        private readonly List<VirtualObjectNode> roots=new List<VirtualObjectNode>();
        private readonly Dictionary<Camera,LogicalVector3> previousViewerPositions=new Dictionary<Camera,LogicalVector3>();
        private readonly Dictionary<Camera,Vector3> viewerVelocities=new Dictionary<Camera,Vector3>();
        private readonly List<Camera> extraCameras=new List<Camera>();
        private VirtualObjectScheduler scheduler;
        private VirtualObjectContext context;
        private GameObject pool;
        private bool initialized;
        private bool teleportRequested;
        private bool hasTeleportTarget;
        private VirtualObjectViewerState teleportTarget;
        private int teleportTargetFrame;
        private VirtualObjectStatus status;
        private float smoothedSafeSpeed=float.PositiveInfinity;
        private IVirtualObjectGround explicitGround;

        public VirtualObjectSet[] Sets{get=>this.sets;set=>this.sets=value;}
        public VirtualObjectBudget Budget{get=>this.budget;set=>this.budget=value;}
        public PlanetTerrainSource GroundTerrain{get=>this.groundTerrain;set=>this.groundTerrain=value;}
        public bool ManualStep{get=>this.manualStep;set=>this.manualStep=value;}
        public IVirtualObjectPlacement Placement{get;private set;}
        /// <summary>地面(<see cref="GroundTerrain"/> から作る。明示的に設定もできる)。</summary>
        public IVirtualObjectGround Ground{
            get{
                if(this.explicitGround!=null){return this.explicitGround;}
                if(this.groundTerrain!=null){return new PlanetObjectGround(this.groundTerrain);}
                return null;
            }
            set=>this.explicitGround=value;
        }
        public bool IsInitialized=>this.initialized;
        /// <summary>ルートの実体(<see cref="Sets"/> の記録ごとに 1 つ)。</summary>
        public IReadOnlyList<VirtualObjectNode> Roots=>this.roots;
        public VirtualObjectStatus Status=>this.status;
        /// <summary>安全速度(m/s)。<see cref="VirtualObjectStatus.SafeSpeed"/>。</summary>
        public float SafeSpeed=>this.status.SafeSpeed;
        public VirtualObjectContext Context=>this.context;
        internal Transform PoolTransform{
            get{
                if(this.pool==null){return null;}
                return this.pool.transform;
            }
        }
        /// <summary>表現の失敗の記録(最新の数件)。</summary>
        public IReadOnlyList<string> Errors=>this.errors;
        private readonly List<string> errors=new List<string>();

        /// <summary><see cref="VirtualObjectViewer"/> の他に、見る camera を足す(検証で使う)。</summary>
        public void AddCamera(Camera camera){
            if(camera!=null&&!this.extraCameras.Contains(camera)){this.extraCameras.Add(camera);}
        }

        public void RemoveCamera(Camera camera){
            this.extraCameras.Remove(camera);
        }

        /// <summary>瞬間移動を知らせる(次の Step で、速度と予測をやり直す)。<see cref="PrepareTeleport"/> の移動先の視点も外す。</summary>
        public void NotifyTeleport(){
            this.teleportRequested=true;
            this.hasTeleportTarget=false;
        }

        /// <summary>
        /// 瞬間移動の準備。移動先(ルートの局所の位置と向き)を見る camera がもう 1 つあるものとして評価し、移動先の近くの表現を準備して表示まで済ませる
        /// (遠くにいる間に表示しても見えないので害はない)。移動先から半径 radius の実体が全部覆われたら true。true になったら camera を動かし、<see cref="NotifyTeleport"/> を呼ぶ。
        /// これで、移動した直後の frame から穴が出ない。やめるときは <see cref="CancelTeleport"/>。
        /// </summary>
        public bool PrepareTeleport(in LogicalVector3 position,Quaternion rotation,float projectionScale,float radius){
            if(!this.initialized){this.Initialize();}
            // 移動先が変わったら、その移動先で評価した Step の後から判定し直す。
            if(!this.hasTeleportTarget||!this.teleportTarget.Position.Equals(position)){this.teleportTargetFrame=this.context.Frame;}
            this.teleportTarget=new VirtualObjectViewerState{Camera=null,Position=position,Rotation=rotation,ProjectionScale=projectionScale,Velocity=Vector3.zero};
            this.hasTeleportTarget=true;
            if(this.context.Frame<=this.teleportTargetFrame){return false;}
            for(int i=0;i<this.roots.Count;i++){
                VirtualObjectNode root=this.roots[i];
                if(!LogicalVector3.TrySubtract(root.Origin,position,out LogicalVector3 difference)||!difference.TryToFloat3(out float3 offset)){continue;}
                if(math.length(offset)>radius+root.Radius){continue;}
                // 移動先の視点で評価した後(次の Step の後)に、覆われているか。
                if(root.LastEvaluatedFrame!=this.context.Frame||!root.IsCovered){return false;}
            }
            return true;
        }

        /// <summary><see cref="PrepareTeleport"/> の移動先の視点を外す(瞬間移動をやめたとき)。</summary>
        public void CancelTeleport(){
            this.hasTeleportTarget=false;
        }

        private void OnEnable(){
            if(!Application.isPlaying){return;}
            this.Initialize();
        }

        private void OnDisable(){
            this.Shutdown();
        }

        private void LateUpdate(){
            if(this.manualStep||!Application.isPlaying){return;}
            this.Step(Time.deltaTime);
        }

        /// <summary>実体を作る(Play では OnEnable が呼ぶ。Edit Mode の検証では直接呼ぶ)。</summary>
        public void Initialize(){
            if(this.initialized){return;}
            this.initialized=true;
            this.Placement=this.GetComponent<IVirtualObjectPlacement>();
            if(this.Placement==null){this.Placement=new StandardObjectPlacement();}
            this.pool=new GameObject("Virtual Object Pool");
            this.pool.hideFlags=HideFlags.DontSave|HideFlags.NotEditable;
            this.pool.SetActive(false);
            this.context=new VirtualObjectContext(this);
            this.scheduler=new VirtualObjectScheduler(this.context);
            this.status=default;
            this.smoothedSafeSpeed=float.PositiveInfinity;
            this.roots.Clear();
            if(this.sets!=null){
                foreach(VirtualObjectSet set in this.sets){
                    if(set==null){continue;}
                    foreach(VirtualObjectPlacementRecord record in set.Placements){this.AddInstance(record);}
                }
            }
        }

        /// <summary>実体を 1 つ足す(記録の位置が読めなければ false)。</summary>
        public bool AddInstance(in VirtualObjectPlacementRecord record){
            if(record.Prefab==null||!record.TryGetPosition(out LogicalVector3 position)){return false;}
            float scale=record.Scale;
            if(!(scale>0f)){scale=1f;}
            Quaternion rotation=record.Rotation;
            if(rotation.x==0f&&rotation.y==0f&&rotation.z==0f&&rotation.w==0f){rotation=Quaternion.identity;}
            var node=new VirtualObjectNode(record.Prefab,null,-1,in position,rotation,scale,record.Seed);
            node.InstanceId=record.Id;
            if(string.IsNullOrEmpty(node.InstanceId)){node.InstanceId="instance"+this.roots.Count;}
            this.roots.Add(node);
            return true;
        }

        /// <summary>
        /// 全部の表現を解放し、作り置きの GameObject を破棄する。破棄は配置の決まり(<see cref="IVirtualObjectPlacement.RetireFrames"/>)の後
        /// (<see cref="VirtualObjectRetirement"/> が、このシステムを止めた後も数えて破棄する)。
        /// </summary>
        public void Shutdown(){
            if(!this.initialized){return;}
            this.initialized=false;
            this.context.ShuttingDown=true;
            foreach(VirtualObjectNode root in this.roots){this.scheduler.ReleaseSubtree(root,true);}
            this.context.ShuttingDown=false;
            this.roots.Clear();
            VirtualObjectRetirement.Retire(this.pool,this.Placement.RetireFrames);
            this.pool=null;
            this.previousViewerPositions.Clear();this.viewerVelocities.Clear();
            this.hasTeleportTarget=false;
        }

        internal void Retire(UnityEngine.Object instance){
            VirtualObjectRetirement.Retire(instance,this.Placement.RetireFrames);
        }

        internal void ReportError(IVirtualObjectRepresentation representation,Exception error){
            this.status.Failures++;
            this.errors.Add(representation+": "+error.Message);
            if(this.errors.Count>32){this.errors.RemoveAt(0);}
        }

        /// <summary>
        /// 1 frame 進める。dt は秒。順序: 見る camera → ノードの評価(望む帯と要求)→ 準備の開始と完了 → 見せる帯の決定 → 表示 → 解放 → 状態の公開。
        /// </summary>
        public void Step(float dt){
            if(!this.initialized){this.Initialize();}
            if(!(dt>0f)){dt=1f/60f;}
            this.context.Frame++;
            this.context.Time+=dt;
            this.UpdateViewers(dt);
            int failures=this.status.Failures;
            int starved=this.status.StarvedRequests;
            this.status=default;
            this.status.Failures=failures;
            this.scheduler.BeginFrame();
            if(this.context.Viewers.Count>0){
                for(int i=0;i<this.roots.Count;i++){this.Evaluate(this.roots[i],false);}
            }
            this.scheduler.Prepare();
            for(int i=0;i<this.roots.Count;i++){this.Present(this.roots[i]);}
            this.scheduler.Activate();
            for(int i=0;i<this.roots.Count;i++){this.UpdatePoses(this.roots[i]);}
            this.scheduler.ReleaseIdle();
            for(int i=0;i<this.roots.Count;i++){this.CollapseIdleChildren(this.roots[i]);}
            this.status.StarvedRequests=starved+this.scheduler.StarvedThisFrame;
            this.Publish(dt);
        }

        private void UpdateViewers(float dt){
            List<VirtualObjectViewerState> viewers=this.context.MutableViewers;
            viewers.Clear();
            var cameras=new List<Camera>();
            foreach(VirtualObjectViewer viewer in VirtualObjectViewer.Active){
                if(viewer!=null&&viewer.isActiveAndEnabled&&!cameras.Contains(viewer.Camera)){cameras.Add(viewer.Camera);}
            }
            foreach(Camera camera in this.extraCameras){
                if(camera!=null&&!cameras.Contains(camera)){cameras.Add(camera);}
            }
            bool teleport=this.teleportRequested;
            this.teleportRequested=false;
            foreach(Camera camera in cameras){
                if(!this.Placement.TryGetViewer(this.transform,camera,out LogicalVector3 position,out Quaternion rotation,out float projection)){continue;}
                Vector3 velocity=Vector3.zero;
                if(!teleport&&this.previousViewerPositions.TryGetValue(camera,out LogicalVector3 previous)&&
                    LogicalVector3.TrySubtract(position,previous,out LogicalVector3 moved)&&moved.TryToFloat3(out float3 delta)){
                    if(math.length(delta)<=this.budget.TeleportDistance){
                        Vector3 instant=(Vector3)delta/dt;
                        this.viewerVelocities.TryGetValue(camera,out Vector3 smoothed);
                        // 速度は 0.25 秒ほどで追う指数平滑(frame の揺れで予測が暴れないように)。
                        float blend=1f-Mathf.Exp(-dt/0.25f);
                        velocity=Vector3.Lerp(smoothed,instant,blend);
                    }
                }
                this.viewerVelocities[camera]=velocity;
                this.previousViewerPositions[camera]=position;
                viewers.Add(new VirtualObjectViewerState{Camera=camera,Position=position,Rotation=rotation,ProjectionScale=projection,Velocity=velocity});
            }
            // 瞬間移動の移動先(準備中だけ)。速度は 0。
            if(this.hasTeleportTarget){viewers.Add(this.teleportTarget);}
        }

        // 望む帯と要求を決める(子へも)。prefetchOnly は、親がまだ子の帯を望んでいない(予測で子の帯に入りそうなので先に準備する)子:
        // 要求は不足として扱わず、表示もしない。
        private void Evaluate(VirtualObjectNode node,bool prefetchOnly){
            node.LastEvaluatedFrame=this.context.Frame;
            node.PrefetchOnly=prefetchOnly;
            this.status.Nodes++;
            this.ResolveGround(node);
            RepresentationBand[] bands=node.Bands;
            if(bands.Length==0){return;}
            this.MeasurePixels(node);
            node.DesiredBand=RepresentationBands.Select(bands,node.Pixels,node.DesiredBand);
            node.PredictedBand=RepresentationBands.SelectWithoutHysteresis(bands,node.PredictedPixels);
            float importance=1f;
            if(node.Definition!=null){importance=node.Definition.Importance;}
            // 常駐の帯(粗い代替)は常に要求する。
            for(int b=0;b<bands.Length;b++){
                if(bands[b].Resident&&!bands[b].IsChildren){this.scheduler.Request(node,b,1e9f,false);}
            }
            int desired=node.DesiredBand;
            if(desired>=0&&!bands[desired].IsChildren){
                bool deficit=!prefetchOnly&&node.ShownBand!=desired;
                float priority=node.Pixels*importance*10f;
                if(prefetchOnly){priority=node.PredictedPixels*importance;}
                this.scheduler.Request(node,desired,priority,deficit);
            }
            // 先読み: 予測した帯が細かければ、その帯を準備する。
            if(node.PredictedBand>desired&&!bands[node.PredictedBand].IsChildren){
                this.scheduler.Request(node,node.PredictedBand,node.PredictedPixels*importance,false);
            }
            // 「なし」から最初の帯へ先に見せる帯(EarlyBand)も準備する。
            int early=EarlyBand(node);
            if(early>desired&&!bands[early].Resident){
                this.scheduler.Request(node,early,node.PredictedPixels*importance,false);
            }
            // 見せている帯と、切り替え中の帯は解放しない。
            if(node.ShownBand>=0){node.LastRequestedFrame[node.ShownBand]=this.context.Frame;}
            if(node.HandoffTarget>=0&&node.HandoffTarget<bands.Length){node.LastRequestedFrame[node.HandoffTarget]=this.context.Frame;}
            bool wantsChildren=desired>=0&&bands[desired].IsChildren;
            bool showsChildren=node.ShownBand>=0&&bands[node.ShownBand].IsChildren;
            // 予測で子の帯に入りそうなら、子を先に作って準備する(子の帯に入った時に、子が揃うまで粗い帯で待たないように)。
            bool predictsChildren=node.PredictedBand>=0&&node.PredictedBand<bands.Length&&bands[node.PredictedBand].IsChildren;
            if(wantsChildren||predictsChildren){this.EnsureChildren(node);}
            if((wantsChildren||showsChildren||predictsChildren)&&node.ChildList!=null){
                bool childPrefetch=prefetchOnly||(!wantsChildren&&!showsChildren);
                for(int i=0;i<node.ChildList.Count;i++){this.Evaluate(node.ChildList[i],childPrefetch);}
            }
        }

        // 見る camera ごとの画面上の大きさの最大と、先読みの予測。
        private void MeasurePixels(VirtualObjectNode node){
            float radius=node.Radius;
            Vector3 center=node.CenterOffset;
            float best=0f;float predicted=0f;float nearest=float.MaxValue;
            float lead=this.scheduler.AveragePrepareSeconds*this.budget.PredictionSafety+0.25f;
            IReadOnlyList<VirtualObjectViewerState> viewers=this.context.Viewers;
            for(int i=0;i<viewers.Count;i++){
                VirtualObjectViewerState viewer=viewers[i];
                if(!LogicalVector3.TrySubtract(node.Origin,viewer.Position,out LogicalVector3 difference)||!difference.TryToFloat3(out float3 offset)){continue;}
                Vector3 relative=(Vector3)offset+center;
                float distance=relative.magnitude;
                nearest=Mathf.Min(nearest,Mathf.Max(0f,distance-radius));
                best=Mathf.Max(best,ScreenPixels(radius,distance,viewer.ProjectionScale));
                // 先読みの時間に camera が進む線分の上で、最も近づく距離(通り過ぎた先の位置は使わない)。
                Vector3 travel=viewer.Velocity*lead;
                float along=0f;
                float travelSquared=travel.sqrMagnitude;
                if(travelSquared>1e-12f){along=Mathf.Clamp01(Vector3.Dot(relative,travel)/travelSquared);}
                Vector3 future=relative-travel*along;
                predicted=Mathf.Max(predicted,ScreenPixels(radius,future.magnitude,viewer.ProjectionScale));
            }
            node.Pixels=best;node.PredictedPixels=Mathf.Max(best,predicted);node.Distance=nearest;
        }

        /// <summary>境界の球(半径 radius)を、距離 distance から見たときの画面上の大きさ(px)。中に入っていれば非常に大きな値。</summary>
        public static float ScreenPixels(float radius,float distance,float projectionScale){
            if(!(distance>radius)){return 1e9f;}
            return radius*projectionScale/distance;
        }

        // ルートの実体を地面に置く(1 回だけ)。足元の中心と四隅の地面の点のうち、上向きで最も低い所から、基礎の深さだけ沈める。
        private void ResolveGround(VirtualObjectNode node){
            if(node.GroundResolved||node.Parent!=null){return;}
            if(node.Definition==null||node.Definition.Ground==VirtualObjectGround.None){node.GroundResolved=true;return;}
            IVirtualObjectGround ground=this.Ground;
            if(ground==null){node.GroundResolved=true;return;}
            if(!ground.TryProject(node.Origin,out LogicalVector3 groundPoint,out Vector3 up)){return;}
            Quaternion frame=Quaternion.FromToRotation(Vector3.up,up)*node.Rotation;
            Vector2 half=node.Definition.Footprint*0.5f*node.Scale;
            Vector3[] corners={Vector3.zero,new Vector3(half.x,0,half.y),new Vector3(-half.x,0,half.y),new Vector3(half.x,0,-half.y),new Vector3(-half.x,0,-half.y)};
            float lowest=0f;
            for(int i=0;i<corners.Length;i++){
                if(!LogicalVector3.TryFromFloat3((float3)(frame*corners[i]),out LogicalVector3 cornerOffset)||!LogicalVector3.TryAdd(groundPoint,cornerOffset,out LogicalVector3 sample)||
                    !ground.TryProject(sample,out LogicalVector3 cornerGround,out Vector3 _)||
                    !LogicalVector3.TrySubtract(cornerGround,groundPoint,out LogicalVector3 rise)||!rise.TryToFloat3(out float3 riseFloat)){
                    continue;
                }
                lowest=Mathf.Min(lowest,Vector3.Dot((Vector3)riseFloat,up));
            }
            float sink=lowest-node.Definition.FoundationDepth*node.Scale;
            if(!LogicalVector3.TryFromFloat3((float3)(up*sink),out LogicalVector3 shift)||!LogicalVector3.TryAdd(groundPoint,shift,out LogicalVector3 origin)){return;}
            node.Origin=origin;
            node.GroundedRotation=frame;
            node.GroundResolved=true;
        }

        // 子のノードを作る(番号ごとに割り当ての層で型を選ぶ)。
        private void EnsureChildren(VirtualObjectNode node){
            node.ChildrenLastUsedFrame=this.context.Frame;
            if(node.ChildList!=null){return;}
            node.ChildList=new List<VirtualObjectNode>();
            VirtualObject definition=node.Definition;
            if(definition==null||definition.Partition==null){return;}
            IVirtualObjectPartition partition=definition.Partition;
            int count=partition.GetPartCount(node);
            Quaternion rotation=node.EffectiveRotation;
            bool exact=rotation==Quaternion.identity&&node.Scale==1f;
            for(int number=0;number<count;number++){
                if(!partition.TryGetPart(node,number,out VirtualObjectPart part)){continue;}
                LogicalVector3 origin;
                if(exact){
                    if(!LogicalVector3.TryAdd(node.Origin,part.Origin,out origin)){continue;}
                }else{
                    if(!LogicalVector3.TryFromFloat3((float3)(rotation*(part.ApproximateOrigin*node.Scale)),out LogicalVector3 offset)||!LogicalVector3.TryAdd(node.Origin,offset,out origin)){continue;}
                }
                uint seed=ChildAssignments.Mix(node.Seed,number);
                var assignmentContext=new ChildAssignmentContext(node,number,in part,in origin,seed);
                ChildAssignment assignment=ChildAssignments.Evaluate(definition.Assignment,in assignmentContext);
                VirtualObject template=assignment.Prefab;
                if(template==null&&assignment.TemplateIndex>=0&&assignment.TemplateIndex<definition.ChildTemplates.Length){template=definition.ChildTemplates[assignment.TemplateIndex];}
                if(template==null){continue;}
                var child=new VirtualObjectNode(template,node,number,in origin,rotation,node.Scale,seed){
                    Parameters=assignment.Parameters,AssignmentSource=assignment.Source,PartitionState=partition.CreateChildState(node,number),
                };
                child.GroundResolved=true;
                child.GroundedRotation=rotation;
                node.ChildList.Add(child);
            }
        }

        // 見せる帯を決める(子から先に)。
        private void Present(VirtualObjectNode node){
            // 先に準備しているだけの子は見せない(親の表現と重ねて描かない)。
            if(node.LastEvaluatedFrame!=this.context.Frame||node.PrefetchOnly){return;}
            if(node.ChildList!=null){
                for(int i=0;i<node.ChildList.Count;i++){this.Present(node.ChildList[i]);}
            }
            RepresentationBand[] bands=node.Bands;
            int desired=node.DesiredBand;
            if(desired<0||bands.Length==0){return;}
            int early=EarlyBand(node);
            if(early>desired){desired=early;}
            int target=-1;
            for(int b=desired;b>=0;b--){
                if(this.IsAvailable(node,b)){target=b;break;}
            }
            if(target>=0&&target!=desired&&node.ShownBand>target&&node.ShownBand<=desired&&this.IsAvailable(node,node.ShownBand)){
                // 細かい方へ進む途中で、今見せている帯の方が望む帯に近ければ、それを保つ(粗い方へ戻さない)。
                target=node.ShownBand;
            }
            // 遅れは、本来望む帯(先に見せる帯でなく)より粗い帯で見せている数。
            bool lagging=node.ShownBand<node.DesiredBand&&target<node.DesiredBand;
            if(target<0||target==node.ShownBand){
                node.HandoffTarget=-1;node.HandoffCount=0;
                if(target>=0){this.EnsureActive(node,target);}
                if(lagging){this.status.Lagging++;}
                return;
            }
            if(lagging){this.status.Lagging++;}
            this.EnsureActive(node,target);
            if(!this.IsVisible(node,target)){return;}
            if(node.HandoffTarget!=target){node.HandoffTarget=target;node.HandoffCount=0;}
            node.HandoffCount++;
            // 何も描いていない(「なし」)状態からは、重ねる相手が無いので見えた frame に切り替える。
            bool immediate=node.ShownBand<0||bands[target].IsNone||bands[node.ShownBand].IsNone||!this.IsVisible(node,node.ShownBand);
            if(immediate||node.HandoffCount>this.budget.HandoffFrames){this.Switch(node,target);}
        }

        // 最初の帯を先に見せる、入口の px に対する割合(見せ始めと、見せるのをやめる所。ヒステリシス)。
        private const float EarlyEnterFraction=0.5f;
        private const float EarlyExitFraction=0.4f;

        // 「なし」から最初の帯へ進むとき、表現が実際に描かれるまでの数 frame は何も描かれない(穴)。
        // 画面上の大きさが最初の帯の入口の半分を超えるか、移動の予測で入口に届きそうなら、望む帯が「なし」の間に最初の帯を先に見せる
        // (まだ入口の px より小さいので、見えても差し支えない)。見せた後は、入口の 0.4 倍を下回るまで見せ続ける。
        private static int EarlyBand(VirtualObjectNode node){
            RepresentationBand[] bands=node.Bands;
            if(bands.Length<2||!bands[0].IsNone||bands[1].IsNone||bands[1].IsChildren){return -1;}
            if(node.DesiredBand!=0){return -1;}
            float enter=bands[1].EnterPixels;
            float threshold=enter*EarlyEnterFraction;
            if(node.ShownBand==1){threshold=enter*EarlyExitFraction;}
            if(node.Pixels>=threshold||node.PredictedPixels>=enter){return 1;}
            return -1;
        }

        // 帯が見せられる状態か(なし: 常に、子: 全部の子が覆われている、表現: 準備済みで、地面が揃っている)。
        private bool IsAvailable(VirtualObjectNode node,int band){
            RepresentationBand[] bands=node.Bands;
            if(band<0||band>=bands.Length){return false;}
            if(bands[band].IsNone){return true;}
            if(bands[band].IsChildren){return node.ChildList!=null&&node.ChildList.Count>0&&node.AreChildrenCovered();}
            IVirtualObjectRepresentation representation=node.Representations[band];
            if(representation==null){return false;}
            if(representation.State!=RepresentationState.Ready&&representation.State!=RepresentationState.Active){return false;}
            if(!bands[band].Resident&&!this.IsGroundReady(node)){return false;}
            return true;
        }

        private bool IsVisible(VirtualObjectNode node,int band){
            RepresentationBand[] bands=node.Bands;
            if(band<0||band>=bands.Length){return false;}
            if(bands[band].IsNone){return true;}
            if(bands[band].IsChildren){return node.AreChildrenCovered();}
            IVirtualObjectRepresentation representation=node.Representations[band];
            return representation!=null&&representation.IsVisible;
        }

        // 地面のある実体は、足元の地形が十分細かく出てから、常駐でない表現を見せる(粗い地形の上に浮かないように)。
        private bool IsGroundReady(VirtualObjectNode node){
            VirtualObjectNode root=node;
            while(root.Parent!=null){root=root.Parent;}
            if(root.Definition==null||root.Definition.Ground==VirtualObjectGround.None){return true;}
            IVirtualObjectGround ground=this.Ground;
            if(ground==null){return true;}
            if(!root.GroundResolved){return false;}
            float extent=Mathf.Min(root.Definition.Footprint.x,root.Definition.Footprint.y)*root.Scale*0.25f;
            return ground.IsReady(root.Origin,extent);
        }

        private void EnsureActive(VirtualObjectNode node,int band){
            RepresentationBand[] bands=node.Bands;
            if(bands[band].IsNone||bands[band].IsChildren){return;}
            IVirtualObjectRepresentation representation=node.Representations[band];
            if(representation!=null&&representation.State==RepresentationState.Ready){
                float priority=node.Pixels;
                if(node.Definition!=null){priority*=node.Definition.Importance;}
                this.scheduler.RequestActivation(node,band,priority);
            }
        }

        private void Switch(VirtualObjectNode node,int target){
            int previous=node.ShownBand;
            node.ShownBand=target;
            node.HandoffTarget=-1;node.HandoffCount=0;
            RepresentationBand[] bands=node.Bands;
            if(previous<0||previous>=bands.Length){return;}
            if(bands[previous].IsChildren){
                if(!bands[target].IsChildren){this.HideChildren(node);}
                return;
            }
            IVirtualObjectRepresentation representation=node.Representations[previous];
            if(representation!=null){representation.Deactivate(this.context);}
        }

        private void HideChildren(VirtualObjectNode node){
            if(node.ChildList==null){return;}
            foreach(VirtualObjectNode child in node.ChildList){
                RepresentationBand[] bands=child.Bands;
                if(child.ShownBand>=0&&child.ShownBand<bands.Length){
                    if(bands[child.ShownBand].IsChildren){
                        this.HideChildren(child);
                    }else{
                        IVirtualObjectRepresentation representation=child.Representations[child.ShownBand];
                        if(representation!=null){representation.Deactivate(this.context);}
                    }
                }
                child.ShownBand=-1;child.HandoffTarget=-1;child.HandoffCount=0;
            }
        }

        private void UpdatePoses(VirtualObjectNode node){
            RepresentationBand[] bands=node.Bands;
            for(int b=0;b<node.Representations.Length;b++){
                IVirtualObjectRepresentation representation=node.Representations[b];
                if(representation!=null&&representation.State==RepresentationState.Active){representation.UpdatePose(this.context);}
            }
            if(node.ChildList!=null){
                for(int i=0;i<node.ChildList.Count;i++){this.UpdatePoses(node.ChildList[i]);}
            }
        }

        // しばらく使っていない子(親が子の帯を望まず、見せてもいない)を捨てる。
        private void CollapseIdleChildren(VirtualObjectNode node){
            if(node.ChildList==null){return;}
            RepresentationBand[] bands=node.Bands;
            bool showsChildren=node.ShownBand>=0&&node.ShownBand<bands.Length&&bands[node.ShownBand].IsChildren;
            bool wantsChildren=node.DesiredBand>=0&&node.DesiredBand<bands.Length&&bands[node.DesiredBand].IsChildren;
            if(!showsChildren&&!wantsChildren&&this.context.Frame-node.ChildrenLastUsedFrame>this.budget.IdleReleaseFrames){
                foreach(VirtualObjectNode child in node.ChildList){this.scheduler.ReleaseSubtree(child,true);}
                node.ChildList=null;
                return;
            }
            for(int i=0;i<node.ChildList.Count;i++){this.CollapseIdleChildren(node.ChildList[i]);}
        }

        private void Publish(float dt){
            for(int i=0;i<this.roots.Count;i++){
                VirtualObjectNode root=this.roots[i];
                if(root.LastEvaluatedFrame==this.context.Frame&&!root.IsCovered){this.status.Uncovered++;}
            }
            this.scheduler.FillStatus(ref this.status);
            float speed=0f;
            IReadOnlyList<VirtualObjectViewerState> viewers=this.context.Viewers;
            for(int i=0;i<viewers.Count;i++){speed=Mathf.Max(speed,viewers[i].Velocity.magnitude);}
            this.status.Speed=speed;
            float estimate=this.scheduler.EstimateSafeSpeed(this.roots);
            // 下げるのはすぐ、上げるのは 1 秒ほどかけて(揺れで減速と加速を繰り返さないように)。
            if(!(estimate<this.smoothedSafeSpeed)||float.IsPositiveInfinity(this.smoothedSafeSpeed)){
                if(float.IsPositiveInfinity(estimate)||float.IsPositiveInfinity(this.smoothedSafeSpeed)){
                    this.smoothedSafeSpeed=estimate;
                }else{
                    this.smoothedSafeSpeed=Mathf.Lerp(this.smoothedSafeSpeed,estimate,1f-Mathf.Exp(-dt/1f));
                }
            }else{
                this.smoothedSafeSpeed=estimate;
            }
            this.status.SafeSpeed=this.smoothedSafeSpeed;
        }

        /// <summary>
        /// 位置(ルートの局所)の周り radius の実体の、望む表現が準備できているか(瞬間移動の前に、true になるまで呼ぶ)。
        /// 呼んだ frame から、その位置を見る camera があるものとして要求する(次の Step で準備が進む)。
        /// </summary>
        public bool Prefetch(in LogicalVector3 position,float radius,float projectionScale){
            if(!this.initialized){this.Initialize();}
            bool ready=true;
            for(int i=0;i<this.roots.Count;i++){
                VirtualObjectNode root=this.roots[i];
                if(!LogicalVector3.TrySubtract(root.Origin,position,out LogicalVector3 difference)||!difference.TryToFloat3(out float3 offset)){continue;}
                float distance=math.length(offset);
                if(distance>radius+root.Radius){continue;}
                RepresentationBand[] bands=root.Bands;
                int band=RepresentationBands.SelectWithoutHysteresis(bands,ScreenPixels(root.Radius,distance,projectionScale));
                if(band<0||bands[band].IsNone||bands[band].IsChildren){continue;}
                this.scheduler.Request(root,band,1e8f,false);
                IVirtualObjectRepresentation representation=root.Representations[band];
                if(representation==null||(representation.State!=RepresentationState.Ready&&representation.State!=RepresentationState.Active)){ready=false;}
            }
            return ready;
        }
    }
}
