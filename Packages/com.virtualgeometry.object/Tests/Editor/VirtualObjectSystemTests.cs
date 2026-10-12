using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using LogicalVector3=Omni.Logical.Vector3;
using Object=UnityEngine.Object;

namespace VirtualGeometry.VirtualObjects.Tests{
    /// <summary>テスト用の表現の設定(準備に何回 Poll が要るか、費用)。</summary>
    [System.Serializable]
    internal sealed class FakeSettings:IRepresentationSettings{
        public int ReadyAfterPolls=1;
        public long GpuBytes;
        public long CpuBytes;
        public float PrepareSeconds;
    }

    /// <summary>テスト用の表現の記録(キーは "ノードの Path#帯の index")。テストの SetUp で Reset する。</summary>
    internal static class FakeLog{
        public static readonly List<string> Events=new List<string>();
        public static readonly Dictionary<string,int> BeginFrames=new Dictionary<string,int>();
        public static readonly Dictionary<string,int> ReadyFrames=new Dictionary<string,int>();
        public static readonly Dictionary<string,int> ActivateFrames=new Dictionary<string,int>();

        public static void Reset(){
            Events.Clear();BeginFrames.Clear();ReadyFrames.Clear();ActivateFrames.Clear();
        }

        public static void Add(string kind,string key){
            Events.Add(kind+"|"+key);
        }

        public static int Count(string kind,string key){
            string target=kind+"|"+key;
            int count=0;
            for(int i=0;i<Events.Count;i++){
                if(Events[i]==target){count++;}
            }
            return count;
        }
    }

    /// <summary>
    /// テスト用の表現。<see cref="FakeSettings.ReadyAfterPolls"/> 回目の Poll で準備が終わり、Activate の次の frame から見える。
    /// </summary>
    internal sealed class FakeRepresentation:VirtualObjectRepresentationBase{
        private readonly FakeSettings settings;
        private readonly string key;
        private int polls;
        private int activatedFrame;
        private VirtualObjectContext context;

        public FakeRepresentation(in RepresentationRequest request):base(in request){
            this.settings=request.Band.Settings as FakeSettings;
            if(this.settings==null){this.settings=new FakeSettings();}
            this.key=request.Node.Path+"#"+request.BandIndex;
            FakeLog.Add("create",this.key);
        }

        public override RepresentationCost EstimateCost(){
            return new RepresentationCost(this.settings.GpuBytes,this.settings.CpuBytes,this.settings.PrepareSeconds,0f);
        }

        protected override void OnBeginPrepare(VirtualObjectContext context,System.Threading.CancellationToken cancellation){
            FakeLog.Add("begin",this.key);
            FakeLog.BeginFrames[this.key]=context.Frame;
        }

        protected override bool OnPoll(VirtualObjectContext context){
            this.polls++;
            if(this.polls<this.settings.ReadyAfterPolls){return false;}
            FakeLog.Add("ready",this.key);
            FakeLog.ReadyFrames[this.key]=context.Frame;
            return true;
        }

        protected override void OnActivate(VirtualObjectContext context){
            this.context=context;
            this.activatedFrame=context.Frame;
            FakeLog.Add("activate",this.key);
            FakeLog.ActivateFrames[this.key]=context.Frame;
        }

        protected override bool OnIsVisible(){
            return this.context!=null&&this.context.Frame>this.activatedFrame;
        }

        protected override void OnDeactivate(VirtualObjectContext context){
            FakeLog.Add("deactivate",this.key);
        }

        protected override void OnRelease(VirtualObjectContext context){
            FakeLog.Add("release",this.key);
        }
    }

    public sealed class VirtualObjectSystemTests{
        // 検証の舞台: VirtualObjectSystem(手動 Step)と、640x480 の RenderTexture に描く camera(画角 60 度)。
        // 投影の倍率 = 480/(2*tan(30度)) = 415.69。camera は (0,0,-距離) から +z を向き、実体は原点に置く。
        // 半径 r の実体の画面上の大きさ(px) = r*415.69/距離。r=10 のとき、粗い帯(入口 2px)は距離 2078 m 以内、細かい帯(入口 40px)は距離 103.9 m 以内、
        // 子の帯(入口 300px)は距離 13.86 m 以内。
        private sealed class Harness{
            private readonly List<Object> owned=new List<Object>();
            private readonly GameObject systemObject;
            private readonly GameObject cameraObject;
            private readonly RenderTexture target;

            public Harness(bool initialize){
                this.systemObject=new GameObject("VirtualObjectSystemTests System");
                this.Sys=this.systemObject.AddComponent<VirtualObjectSystem>();
                this.Sys.ManualStep=true;
                this.cameraObject=new GameObject("VirtualObjectSystemTests Camera");
                this.Camera=this.cameraObject.AddComponent<Camera>();
                this.Camera.fieldOfView=60f;
                this.target=new RenderTexture(640,480,16);
                this.Camera.targetTexture=this.target;
                this.Sys.AddCamera(this.Camera);
                if(initialize){this.Sys.Initialize();}
            }

            public VirtualObjectSystem Sys{get;}
            public Camera Camera{get;}

            public T Own<T>(T instance) where T:Object{
                this.owned.Add(instance);
                return instance;
            }

            public VirtualObject Define(string id,float radius,params RepresentationBand[] bands){
                var gameObject=new GameObject("Definition "+id);
                this.owned.Add(gameObject);
                VirtualObject definition=gameObject.AddComponent<VirtualObject>();
                definition.Id=id;
                definition.BoundingRadius=radius;
                definition.Bands=bands;
                return definition;
            }

            public bool Add(VirtualObject definition,string id,int x){
                var record=new VirtualObjectPlacementRecord{
                    Id=id,Prefab=definition,
                    X=x.ToString(CultureInfo.InvariantCulture),Y="0",Z="0",
                };
                return this.Sys.AddInstance(in record);
            }

            public void MoveTo(float distance,bool teleport){
                this.cameraObject.transform.position=new Vector3(0f,0f,-distance);
                if(teleport){this.Sys.NotifyTeleport();}
            }

            public void Step(){
                this.Sys.Step(1f/60f);
            }

            public void Advance(int frames){
                for(int i=0;i<frames;i++){this.Step();}
            }

            public void Dispose(){
                if(this.systemObject!=null){
                    this.Sys.Shutdown();
                    Object.DestroyImmediate(this.systemObject);
                }
                if(this.cameraObject!=null){
                    this.Camera.targetTexture=null;
                    Object.DestroyImmediate(this.cameraObject);
                }
                if(this.target!=null){Object.DestroyImmediate(this.target);}
                for(int i=0;i<this.owned.Count;i++){
                    if(this.owned[i]!=null){Object.DestroyImmediate(this.owned[i]);}
                }
                this.owned.Clear();
            }
        }

        private readonly List<Harness> harnesses=new List<Harness>();

        [SetUp]
        public void SetUp(){
            FakeLog.Reset();
            VirtualObjectRepresentations.Register("fake",request=>new FakeRepresentation(in request),typeof(FakeSettings));
            MeshClusterPartition.ClearCache();
        }

        [TearDown]
        public void TearDown(){
            for(int i=0;i<this.harnesses.Count;i++){this.harnesses[i].Dispose();}
            this.harnesses.Clear();
            VirtualObjectRepresentations.Unregister("fake");
            MeshClusterPartition.ClearCache();
        }

        private Harness NewHarness(bool initialize=true){
            // 記録は 1 つの舞台ごと(テスト内の複数のケースで同じキーを使うため)
            FakeLog.Reset();
            var harness=new Harness(initialize);
            this.harnesses.Add(harness);
            return harness;
        }

        private static RepresentationBand NoneBand(){
            return new RepresentationBand("none",0f,0f);
        }

        // 常駐の粗い代替(入口 2px)。
        private static RepresentationBand Coarse(int polls){
            return new RepresentationBand("fake",2f,1.5f,new FakeSettings{ReadyAfterPolls=polls}){Resident=true};
        }

        // 細かい表現(入口 40px)。
        private static RepresentationBand Fine(int polls){
            return new RepresentationBand("fake",40f,32f,new FakeSettings{ReadyAfterPolls=polls});
        }

        private static RepresentationBand ChildrenBand(){
            return new RepresentationBand("children",300f,250f);
        }

        // n x n の格子の平面(三角形は 2*n*n 個)。
        private static Mesh Grid(int n){
            var vertices=new List<Vector3>();
            for(int z=0;z<=n;z++){
                for(int x=0;x<=n;x++){vertices.Add(new Vector3(x,0f,z));}
            }
            var triangles=new List<int>();
            for(int z=0;z<n;z++){
                for(int x=0;x<n;x++){
                    int a=z*(n+1)+x;int b=a+1;int c=a+n+1;int d=c+1;
                    triangles.Add(a);triangles.Add(c);triangles.Add(b);
                    triangles.Add(b);triangles.Add(c);triangles.Add(d);
                }
            }
            var mesh=new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles,0);
            return mesh;
        }

        // 親(none / 常駐の粗い / 子)と、子の型(準備に childPolls 回かかる 1 つの帯)を作る。
        private static VirtualObject DefineParent(Harness harness,int childPolls,int trianglesPerPart,IChildAssignmentRule[] assignment){
            VirtualObject tile=harness.Define("tile",1f,new RepresentationBand("fake",0f,0f,new FakeSettings{ReadyAfterPolls=childPolls}));
            Mesh mesh=harness.Own(Grid(6));
            VirtualObject parent=harness.Define("parent",10f,NoneBand(),Coarse(1),ChildrenBand());
            parent.Partition=new MeshClusterPartition{Mesh=mesh,TrianglesPerPart=trianglesPerPart};
            parent.ChildTemplates=new[]{tile};
            parent.Assignment=assignment;
            return parent;
        }

        [Test]
        public void SafeSpeed(){
            {
                // 待っている準備が無ければ、締切が無いので正の無限大
                Harness harness=this.NewHarness();
                VirtualObject definition=harness.Define("building",10f,NoneBand(),Coarse(1),Fine(1));
                harness.Add(definition,"a",0);
                harness.MoveTo(300f,true);
                harness.Advance(30);
                Assert.That(harness.Sys.Roots[0].ShownBand,Is.EqualTo(1));
                Assert.That(float.IsPositiveInfinity(harness.Sys.SafeSpeed),Is.True);
            }
            {
                // 細かい帯の準備が 5 秒かかる(InjectedPrepareDelaySeconds)状態で、距離 400 m から 30 m/s で近づく。
                // 先読みは camera の 0.25 秒先(準備時間の実測は 0 秒なので 0*2+0.25)の大きさで判断するので、
                // 距離が約 111 m 以内(入口の距離 103.9 m + 30*0.25 m)で細かい帯を要求する。
                // そのときの締切 = (111-103.9)/30 = 約 0.25 秒、必要な作業 = 5 秒/同時 4 = 1.25 秒 → 安全速度 = 30*0.25/1.25 = 約 6 m/s で、今の速さ 30 m/s より小さい。
                Harness harness=this.NewHarness();
                // 数値は同時の数 4 と予測の安全係数 2 を前提にする(既定値を変えても、この場合の数値が変わらないように明示する)。
                harness.Sys.Budget.MaxConcurrentPrepares=4;
                harness.Sys.Budget.PredictionSafety=2f;
                VirtualObject definition=harness.Define("building",10f,NoneBand(),Coarse(1),Fine(1));
                harness.Add(definition,"a",0);
                harness.MoveTo(400f,true);
                harness.Advance(10);
                VirtualObjectNode node=harness.Sys.Roots[0];
                Assert.That(node.ShownBand,Is.EqualTo(1));
                Assert.That(float.IsPositiveInfinity(harness.Sys.SafeSpeed),Is.True);
                harness.Sys.Budget.InjectedPrepareDelaySeconds=5f;
                float distance=400f;
                bool foundFinite=false;
                bool recovered=false;
                for(int i=0;i<2000&&!recovered;i++){
                    distance=Mathf.Max(50f,distance-30f/60f);
                    harness.MoveTo(distance,false);
                    harness.Step();
                    float safe=harness.Sys.SafeSpeed;
                    if(!foundFinite){
                        if(!float.IsPositiveInfinity(safe)){
                            foundFinite=true;
                            Assert.That(harness.Sys.Status.Speed,Is.GreaterThan(20f));
                            Assert.That(safe,Is.GreaterThanOrEqualTo(0f));
                            Assert.That(safe,Is.LessThan(harness.Sys.Status.Speed));
                            Assert.That(distance,Is.LessThan(125f).And.GreaterThan(100f));
                            // 接近の速さは今の速さと同じなので、安全速度 = 速さ×((距離−入口の距離)/速さ)/(5 秒/4) = (距離−入口の距離)/1.25(約 6 m/s)
                            float bandDistance=10f*StandardObjectPlacement.ProjectionScale(harness.Camera)/40f;
                            Assert.That(safe,Is.EqualTo((distance-bandDistance)/1.25f).Within(0.05f));
                            Assert.That(safe,Is.EqualTo(6f).Within(1f));
                        }
                    }else{
                        if(FakeLog.Count("ready","a#2")==0){
                            // 細かい帯が準備できるまでは、有限で今の速さより小さいまま
                            Assert.That(float.IsPositiveInfinity(safe),Is.False,"frame "+i);
                            Assert.That(safe,Is.LessThanOrEqualTo(harness.Sys.Status.Speed),"frame "+i);
                        }else if(float.IsPositiveInfinity(safe)){
                            recovered=true;
                        }
                    }
                }
                Assert.That(foundFinite,Is.True);
                // 準備が終われば締切が無くなり、無限大へ戻る
                Assert.That(recovered,Is.True);
                harness.Advance(10);
                Assert.That(node.ShownBand,Is.EqualTo(2));
                Assert.That(float.IsPositiveInfinity(harness.Sys.SafeSpeed),Is.True);
            }
        }

        [Test]
        public void AddCamera(){
            {
                // camera が無ければ何も評価しない(Nodes が 0、穴の数えもしない)
                Harness harness=this.NewHarness();
                harness.Sys.RemoveCamera(harness.Camera);
                VirtualObject definition=harness.Define("a",10f,NoneBand(),Coarse(1));
                harness.Add(definition,"a",0);
                harness.MoveTo(1000f,true);
                harness.Advance(3);
                Assert.That(harness.Sys.Status.Nodes,Is.EqualTo(0));
                Assert.That(harness.Sys.Roots[0].ShownBand,Is.EqualTo(-1));
            }
            {
                // 同じ camera を 2 回足しても 1 つとして扱う(評価は 1 回)
                Harness harness=this.NewHarness();
                harness.Sys.AddCamera(harness.Camera);
                VirtualObject definition=harness.Define("a",10f,NoneBand(),Coarse(1));
                harness.Add(definition,"a",0);
                harness.MoveTo(1000f,true);
                harness.Step();
                Assert.That(harness.Sys.Context.Viewers.Count,Is.EqualTo(1));
                Assert.That(harness.Sys.Status.Nodes,Is.EqualTo(1));
                Assert.That(harness.Sys.Context.Viewers[0].ProjectionScale,Is.EqualTo(415.69f).Within(0.01f));
                // 重ねて足していないので、1 回外せば見る camera は無くなる
                harness.Sys.RemoveCamera(harness.Camera);
                harness.Step();
                Assert.That(harness.Sys.Context.Viewers.Count,Is.EqualTo(0));
                Assert.That(harness.Sys.Status.Nodes,Is.EqualTo(0));
            }
        }

        [Test]
        public void Initialize(){
            {
                // 2 回呼んでも 1 回だけ初期化される(ルートが増えない)
                Harness harness=this.NewHarness(false);
                VirtualObject definition=harness.Define("a",10f,NoneBand(),Coarse(1));
                Assert.That(harness.Sys.IsInitialized,Is.False);
                harness.Sys.Initialize();
                harness.Add(definition,"a",0);
                harness.Sys.Initialize();
                Assert.That(harness.Sys.IsInitialized,Is.True);
                Assert.That(harness.Sys.Roots.Count,Is.EqualTo(1));
                Assert.That(harness.Sys.Placement,Is.TypeOf<StandardObjectPlacement>());
            }
            {
                // Sets の記録がルートになる
                Harness harness=this.NewHarness(false);
                VirtualObject definition=harness.Define("a",10f,NoneBand(),Coarse(1));
                VirtualObjectSet set=harness.Own(ScriptableObject.CreateInstance<VirtualObjectSet>());
                set.Placements=new[]{
                    new VirtualObjectPlacementRecord{Id="p",Prefab=definition,X="0",Y="0",Z="0"},
                    new VirtualObjectPlacementRecord{Id="q",Prefab=definition,X="5",Y="0",Z="0"},
                };
                harness.Sys.Sets=new[]{set};
                harness.Sys.Initialize();
                Assert.That(harness.Sys.Roots.Count,Is.EqualTo(2));
                Assert.That(harness.Sys.Roots[1].InstanceId,Is.EqualTo("q"));
            }
        }

        [Test]
        public void AddInstance(){
            {
                // 種類が無い記録は足せない
                Harness harness=this.NewHarness();
                var record=new VirtualObjectPlacementRecord{Id="x",Prefab=null,X="0",Y="0",Z="0"};
                Assert.That(harness.Sys.AddInstance(in record),Is.False);
                Assert.That(harness.Sys.Roots.Count,Is.EqualTo(0));
            }
            {
                // 位置が読めない記録は足せない
                Harness harness=this.NewHarness();
                VirtualObject definition=harness.Define("a",10f,NoneBand(),Coarse(1));
                var record=new VirtualObjectPlacementRecord{Id="x",Prefab=definition,X="abc",Y="0",Z="0"};
                Assert.That(harness.Sys.AddInstance(in record),Is.False);
                Assert.That(harness.Sys.Roots.Count,Is.EqualTo(0));
            }
            {
                // id が空なら "instance番号"、拡大率 0 は 1、向きが全部 0 なら identity
                Harness harness=this.NewHarness();
                VirtualObject definition=harness.Define("a",10f,NoneBand(),Coarse(1));
                var record=new VirtualObjectPlacementRecord{Prefab=definition,X="0",Y="0",Z="0",Rotation=new Quaternion(0f,0f,0f,0f),Scale=0f,Seed=7u};
                Assert.That(harness.Sys.AddInstance(in record),Is.True);
                VirtualObjectNode node=harness.Sys.Roots[0];
                Assert.That(node.InstanceId,Is.EqualTo("instance0"));
                Assert.That(node.Scale,Is.EqualTo(1f));
                Assert.That(node.Rotation,Is.EqualTo(Quaternion.identity));
                Assert.That(node.Seed,Is.EqualTo(7u));
                Assert.That(node.Number,Is.EqualTo(-1));
                Assert.That(node.Radius,Is.EqualTo(10f));
            }
        }

        [Test]
        public void Shutdown(){
            {
                // 全部の表現を解放し、ルートを空にする(作り置きの GameObject の破棄は internal なので、ここでは確かめない)
                Harness harness=this.NewHarness();
                VirtualObject definition=harness.Define("building",10f,NoneBand(),Coarse(1),Fine(1));
                harness.Add(definition,"a",0);
                harness.MoveTo(50f,true);
                harness.Advance(10);
                Assert.That(FakeLog.Count("release","a#1"),Is.EqualTo(0));
                harness.Sys.Shutdown();
                Assert.That(harness.Sys.IsInitialized,Is.False);
                Assert.That(harness.Sys.Roots.Count,Is.EqualTo(0));
                Assert.That(FakeLog.Count("release","a#1"),Is.EqualTo(1));
                Assert.That(FakeLog.Count("release","a#2"),Is.EqualTo(1));
                // 2 回呼んでも壊れない
                harness.Sys.Shutdown();
                Assert.That(FakeLog.Count("release","a#1"),Is.EqualTo(1));
            }
        }

        [Test]
        public void Step(){
            {
                // 穴を出さない: 細かい帯が遅い間は常駐の粗い帯を見せ続け(Uncovered==0)、細かい帯が見えてから HandoffFrames 待って粗い帯を外す
                Harness harness=this.NewHarness();
                VirtualObject definition=harness.Define("building",10f,NoneBand(),Coarse(1),Fine(40));
                harness.Add(definition,"a",0);
                harness.MoveTo(1000f,true);
                harness.Advance(5);
                VirtualObjectNode node=harness.Sys.Roots[0];
                Assert.That(node.ShownBand,Is.EqualTo(1));
                Assert.That(harness.Sys.Status.Uncovered,Is.EqualTo(0));
                harness.MoveTo(50f,true);
                int switchFrame=-1;
                for(int i=0;i<200&&switchFrame<0;i++){
                    harness.Step();
                    Assert.That(harness.Sys.Status.Uncovered,Is.EqualTo(0),"frame "+i);
                    if(node.ShownBand==2){
                        switchFrame=harness.Sys.Context.Frame;
                    }else{
                        Assert.That(node.ShownBand,Is.EqualTo(1),"frame "+i);
                        Assert.That(node.DesiredBand,Is.EqualTo(2),"frame "+i);
                        Assert.That(FakeLog.Count("deactivate","a#1"),Is.EqualTo(0),"frame "+i);
                        if(FakeLog.Count("ready","a#2")==0){
                            // 準備中は、望む帯より粗い表現で見せているノードとして数える
                            Assert.That(harness.Sys.Status.Lagging,Is.EqualTo(1),"frame "+i);
                            Assert.That(FakeLog.Count("activate","a#2"),Is.EqualTo(0),"frame "+i);
                        }
                    }
                }
                Assert.That(switchFrame,Is.GreaterThan(0));
                int begin=FakeLog.BeginFrames["a#2"];
                int ready=FakeLog.ReadyFrames["a#2"];
                int activate=FakeLog.ActivateFrames["a#2"];
                Assert.That(ready-begin,Is.EqualTo(39));
                Assert.That(activate,Is.EqualTo(ready));
                // activate の次の frame から見え、そこから HandoffFrames 回数えた次の frame で切り替わる
                Assert.That(switchFrame,Is.EqualTo(activate+1+harness.Sys.Budget.HandoffFrames));
                Assert.That(FakeLog.Count("deactivate","a#1"),Is.EqualTo(1));
                Assert.That(harness.Sys.Status.Uncovered,Is.EqualTo(0));
            }
            {
                // 粗い方へ戻る: 常駐の粗い帯が再び見えてから細かい帯を外す(その間 Uncovered==0)
                Harness harness=this.NewHarness();
                VirtualObject definition=harness.Define("building",10f,NoneBand(),Coarse(1),Fine(1));
                harness.Add(definition,"a",0);
                harness.MoveTo(1000f,true);
                harness.Advance(5);
                VirtualObjectNode node=harness.Sys.Roots[0];
                harness.MoveTo(50f,true);
                for(int i=0;i<30&&node.ShownBand!=2;i++){harness.Step();}
                Assert.That(node.ShownBand,Is.EqualTo(2));
                Assert.That(FakeLog.Count("deactivate","a#2"),Is.EqualTo(0));
                harness.MoveTo(1000f,true);
                int switchFrame=-1;
                for(int i=0;i<60&&switchFrame<0;i++){
                    harness.Step();
                    Assert.That(harness.Sys.Status.Uncovered,Is.EqualTo(0),"frame "+i);
                    if(node.ShownBand==1){
                        switchFrame=harness.Sys.Context.Frame;
                    }else{
                        Assert.That(node.ShownBand,Is.EqualTo(2),"frame "+i);
                        Assert.That(FakeLog.Count("deactivate","a#2"),Is.EqualTo(0),"frame "+i);
                    }
                }
                Assert.That(switchFrame,Is.GreaterThan(0));
                // 粗い帯は最初の表示と戻りの表示で 2 回 Activate され、切り替わった frame に細かい帯が 1 回 Deactivate される
                Assert.That(FakeLog.Count("activate","a#1"),Is.EqualTo(2));
                Assert.That(switchFrame,Is.EqualTo(FakeLog.ActivateFrames["a#1"]+1+harness.Sys.Budget.HandoffFrames));
                Assert.That(FakeLog.Count("deactivate","a#2"),Is.EqualTo(1));
            }
            {
                // 子の帯: 番号ごとに子のノードを作り(三角形 72 個 / 部分 16 以下 = 8 部分)、全部の子が見えてから親が粗い帯を外して子へ切り替える
                Harness harness=this.NewHarness();
                VirtualObject parent=DefineParent(harness,5,16,new IChildAssignmentRule[]{new DefaultTemplateRule{TemplateIndex=0}});
                harness.Add(parent,"a",0);
                harness.MoveTo(1000f,true);
                harness.Advance(5);
                VirtualObjectNode node=harness.Sys.Roots[0];
                Assert.That(node.ShownBand,Is.EqualTo(1));
                harness.MoveTo(12f,true);
                harness.Step();
                Assert.That(node.DesiredBand,Is.EqualTo(2));
                Assert.That(node.Children.Count,Is.EqualTo(8));
                Assert.That(node.Children.Count,Is.EqualTo(MeshClusterPartition.Build(((MeshClusterPartition)parent.Partition).Mesh,16).Start.Length));
                for(int i=0;i<node.Children.Count;i++){
                    Assert.That(node.Children[i].Number,Is.EqualTo(i));
                    Assert.That(node.Children[i].Parent,Is.SameAs(node));
                    Assert.That(node.Children[i].AssignmentSource,Is.EqualTo("default"));
                }
                int switchFrame=-1;
                bool sawPartial=false;
                for(int i=0;i<300&&switchFrame<0;i++){
                    if(i>0){harness.Step();}
                    Assert.That(harness.Sys.Status.Uncovered,Is.EqualTo(0),"frame "+i);
                    int covered=0;
                    for(int c=0;c<node.Children.Count;c++){
                        if(node.Children[c].IsCovered){covered++;}
                    }
                    if(node.ShownBand==2){
                        switchFrame=harness.Sys.Context.Frame;
                        Assert.That(covered,Is.EqualTo(8));
                    }else{
                        // 子が全部覆うまで、親は粗い帯を見せ続ける
                        Assert.That(node.ShownBand,Is.EqualTo(1),"frame "+i);
                        Assert.That(FakeLog.Count("deactivate","a#1"),Is.EqualTo(0),"frame "+i);
                        if(covered>0&&covered<8){sawPartial=true;}
                    }
                }
                Assert.That(switchFrame,Is.GreaterThan(0));
                Assert.That(sawPartial,Is.True);
                Assert.That(FakeLog.Count("deactivate","a#1"),Is.EqualTo(1));
                Assert.That(harness.Sys.Status.Uncovered,Is.EqualTo(0));
            }
            {
                // 子の帯: OverrideTableRule の TemplateIndex=-1 の行は、その番号の子を作らない
                Harness harness=this.NewHarness();
                var table=new OverrideTableRule{Rows=new[]{new OverrideTableRule.Row{Number=3,TemplateIndex=-1}}};
                VirtualObject parent=DefineParent(harness,1,16,new IChildAssignmentRule[]{new DefaultTemplateRule{TemplateIndex=0},table});
                harness.Add(parent,"a",0);
                harness.MoveTo(1000f,true);
                harness.Advance(5);
                VirtualObjectNode node=harness.Sys.Roots[0];
                harness.MoveTo(12f,true);
                harness.Step();
                Assert.That(node.Children.Count,Is.EqualTo(7));
                var numbers=new List<int>();
                for(int i=0;i<node.Children.Count;i++){numbers.Add(node.Children[i].Number);}
                Assert.That(numbers,Is.EqualTo(new[]{0,1,2,4,5,6,7}));
                for(int i=0;i<60&&node.ShownBand!=2;i++){
                    harness.Step();
                    Assert.That(harness.Sys.Status.Uncovered,Is.EqualTo(0),"frame "+i);
                }
                Assert.That(node.ShownBand,Is.EqualTo(2));
            }
            {
                // 予算: MaxConcurrentPrepares が同時に準備する数(Status.Preparing)を制限する
                int[] limits={1,3};
                for(int l=0;l<limits.Length;l++){
                    FakeLog.Reset();
                    Harness harness=this.NewHarness();
                    harness.Sys.Budget.MaxConcurrentPrepares=limits[l];
                    VirtualObject definition=harness.Define("a",10f,NoneBand(),Coarse(10));
                    for(int i=0;i<5;i++){harness.Add(definition,"i"+i,i);}
                    harness.MoveTo(1000f,true);
                    int peak=0;
                    for(int i=0;i<300;i++){
                        harness.Step();
                        peak=Mathf.Max(peak,harness.Sys.Status.Preparing);
                        Assert.That(harness.Sys.Status.Preparing,Is.LessThanOrEqualTo(limits[l]),"limit "+limits[l]+" frame "+i);
                    }
                    Assert.That(peak,Is.EqualTo(limits[l]));
                    // 上限があっても、最後は全部が見える
                    for(int i=0;i<5;i++){Assert.That(harness.Sys.Roots[i].ShownBand,Is.EqualTo(1));}
                }
            }
            {
                // 予算: MaxActivationsPerFrame が 1 frame の表示(Status.Activations)を制限し、全部は数 frame に分かれて表示される
                int[] limits={1,2};
                for(int l=0;l<limits.Length;l++){
                    FakeLog.Reset();
                    Harness harness=this.NewHarness();
                    harness.Sys.Budget.MaxConcurrentPrepares=16;
                    harness.Sys.Budget.MaxActivationsPerFrame=limits[l];
                    harness.Sys.Budget.MainThreadMillisecondsPerFrame=1000f;
                    VirtualObject definition=harness.Define("a",10f,NoneBand(),Coarse(1));
                    for(int i=0;i<6;i++){harness.Add(definition,"i"+i,i);}
                    harness.MoveTo(1000f,true);
                    int peak=0;int total=0;
                    for(int i=0;i<30;i++){
                        harness.Step();
                        int activations=harness.Sys.Status.Activations;
                        peak=Mathf.Max(peak,activations);
                        total+=activations;
                        Assert.That(activations,Is.LessThanOrEqualTo(limits[l]),"limit "+limits[l]+" frame "+i);
                    }
                    Assert.That(peak,Is.EqualTo(limits[l]));
                    Assert.That(total,Is.EqualTo(6));
                    Assert.That(harness.Sys.Status.Active,Is.EqualTo(6));
                }
            }
            {
                // 表現が見つからない帯は失敗として数え(Failures)、粗い帯のまま見せ続ける
                Harness harness=this.NewHarness();
                VirtualObject definition=harness.Define("a",10f,NoneBand(),Coarse(1),new RepresentationBand("missing-representation",40f,32f));
                harness.Add(definition,"a",0);
                harness.MoveTo(1000f,true);
                harness.Advance(5);
                harness.MoveTo(50f,true);
                harness.Advance(10);
                Assert.That(harness.Sys.Status.Failures,Is.GreaterThan(0));
                Assert.That(harness.Sys.Roots[0].ShownBand,Is.EqualTo(1));
                Assert.That(harness.Sys.Status.Uncovered,Is.EqualTo(0));
            }
        }

        [Test]
        public void ScreenPixels(){
            {
                // 半径 × 倍率 ÷ 距離
                Assert.That(VirtualObjectSystem.ScreenPixels(10f,100f,415.69f),Is.EqualTo(41.569f).Within(0.001f));
                Assert.That(VirtualObjectSystem.ScreenPixels(10f,1000f,415.69f),Is.EqualTo(4.1569f).Within(0.0001f));
            }
            {
                // 境界の球の中(距離が半径以下)は非常に大きな値
                Assert.That(VirtualObjectSystem.ScreenPixels(10f,10f,415.69f),Is.EqualTo(1e9f));
                Assert.That(VirtualObjectSystem.ScreenPixels(10f,0f,415.69f),Is.EqualTo(1e9f));
            }
            {
                // 標準の倍率: RenderTexture の高さ 480 と画角 60 度 → 480/(2*tan(30度))
                Harness harness=this.NewHarness();
                Assert.That(StandardObjectPlacement.ProjectionScale(harness.Camera),Is.EqualTo(480f/(2f*Mathf.Tan(30f*Mathf.Deg2Rad))).Within(0.001f));
            }
        }

        [Test]
        public void Prefetch(){
            {
                // 近くに実体が無ければ、準備するものが無いので true
                Harness harness=this.NewHarness();
                VirtualObject definition=harness.Define("building",10f,NoneBand(),Coarse(1),Fine(3));
                harness.Add(definition,"a",0);
                harness.MoveTo(5000f,true);
                LogicalVector3.TryFromFloat3(new float3(10000f,0f,0f),out LogicalVector3 far);
                Assert.That(harness.Sys.Prefetch(in far,1f,415.69f),Is.True);
                Assert.That(FakeLog.Count("create","a#2"),Is.EqualTo(0));
            }
            {
                // 近くの実体の望む帯は、準備できるまで false。Step で準備が進み(Poll 3 回)、準備できたら true
                Harness harness=this.NewHarness();
                VirtualObject definition=harness.Define("building",10f,NoneBand(),Coarse(1),Fine(3));
                harness.Add(definition,"a",0);
                harness.MoveTo(5000f,true);
                LogicalVector3.TryFromFloat3(new float3(0f,0f,-50f),out LogicalVector3 position);
                Assert.That(harness.Sys.Prefetch(in position,100f,415.69f),Is.False);
                int steps=0;
                while(!harness.Sys.Prefetch(in position,100f,415.69f)&&steps<20){
                    harness.Step();
                    steps++;
                }
                Assert.That(steps,Is.EqualTo(3));
                Assert.That(FakeLog.Count("ready","a#2"),Is.EqualTo(1));
            }
        }
    }
}
