using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualizedGeometry;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World{
    /// <summary>
    /// 惑星の Layer(地形、海など)を、cube-sphere の Cell の実行時生成 cluster で描く共通部分。惑星の中心はこの GameObject の原点。
    /// Cell の選択は <see cref="PlanetCellSelector"/>、頂点の生成は派生クラスの compute(<see cref="GeneratorResource"/>)が行う。
    /// Cell と pool の slot の対応を持ち、pool が足りないときは使っていない Cell を下(葉)から退避する。
    /// 頂点は Cell の原点(Cell の中心の方向の半径 R の点、惑星のローカル座標)からの相対位置で持ち、Cell ごとに draw する。
    /// 惑星を置く空間は <see cref="IPlanetPlacement"/> が決める(OnEnable の時に同じ GameObject にある実装。無い、または無効なら Unity の world 空間)。
    /// Cell の原点と GPU に渡す値は Logical 精度で求める(<see cref="PlanetCellFrame"/>)。頂点の誤差は惑星の大きさではなく Cell の大きさに比例する。
    /// </summary>
    [ExecuteAlways]
    public abstract class PlanetLayerSource:VirtualGeometryClusterSource,IPlanetCellResidency{
        public const int CellQuads=64;
        public const int ClusterQuads=8;
        /// <summary>選べる最大の階層。Cell の中心の値を Logical で正確に作れる範囲(<see cref="PlanetCellFrame.MaximumLevel"/>)。</summary>
        public const int GpuMaximumLevel=PlanetCellFrame.MaximumLevel;
        // 先読みで点を含む Cell を面の座標の float で探す階層の上限(2^-24 の分解能で Cell を区別できる範囲)。
        private const int PrefetchLevelLimit=20;

        [SerializeField,Min(0.1f)] private float targetPixels=2;
        [SerializeField,Range(0,0.5f)] private float hysteresis=0.1f;
        [SerializeField,Range(0,GpuMaximumLevel)] private int maximumLevel=16;
        [SerializeField,Min(0)] private float skirtQuads=2;
        [SerializeField] private bool horizonCulling=true;

        private PlanetCellSelector selector;
        private ComputeShader generator;
        private int kernel=-1;
        private readonly Dictionary<PlanetCellKey,int> slotByKey=new Dictionary<PlanetCellKey,int>();
        private PlanetCellKey[] keyBySlot;
        private bool[] used;
        private int[] touchedFrame;
        // 生成した頂点から求めた実際の bounds(Cell の原点からの中心 xyz、半径 w)。slot を別の Cell に使い直すと version が変わり、古い readback を捨てる。
        private Vector4[] actualBounds;
        private bool[] actualValid;
        private int[] slotVersion;
        // slot の Cell の中心の値(原点は惑星のローカル座標、Logical 精度)。頂点は原点からの相対位置。
        private PlanetCellFrame[] cellFrames;
        private LogicalVector3 referencePoint;
        private bool hasReference;
        private static readonly int PlanetCellUpId=Shader.PropertyToID("_PlanetCellUp");
        private static readonly int PlanetCellQuadId=Shader.PropertyToID("_PlanetCellQuad");
        private static readonly int PlanetCellRefId=Shader.PropertyToID("_PlanetCellRef");
        private IPlanetPlacement placement;
        private readonly StandardPlanetPlacement standardPlacement=new StandardPlanetPlacement();
        private readonly Dictionary<Camera,List<MaterialPropertyBlock>> drawProperties=new Dictionary<Camera,List<MaterialPropertyBlock>>();
        private readonly Dictionary<Camera,HashSet<PlanetCellKey>> history=new Dictionary<Camera,HashSet<PlanetCellKey>>();
        private readonly Dictionary<PlanetCellKey,float> requests=new Dictionary<PlanetCellKey,float>();
        private readonly List<KeyValuePair<PlanetCellKey,float>> requestOrder=new List<KeyValuePair<PlanetCellKey,float>>();
        private readonly List<PlanetCellKey> output=new List<PlanetCellKey>();
        private HashSet<PlanetCellKey> refinedScratch=new HashSet<PlanetCellKey>();

        /// <summary>惑星の半径 R(Cell の原点はこの半径の球面上)。</summary>
        public abstract float Radius{get;}
        /// <summary>頂点が半径 R から離れうる最大の距離(選択の bounds に含める)。</summary>
        protected abstract float SelectionAmplitude{get;}
        /// <summary>頂点を生成する compute の Resources のパス(kernel 名は Generate)。</summary>
        protected abstract string GeneratorResource{get;}

        public float TargetPixels{get=>this.targetPixels;set{this.targetPixels=value;if(this.selector!=null){this.selector.TargetPixels=value;}}}
        public int MaximumLevel=>this.maximumLevel;
        public PlanetCellSelector Selector=>this.selector;
        /// <summary>直近の選択で描いた Cell。</summary>
        public IReadOnlyList<PlanetCellKey> LastSelected=>this.output;
        public int UsedSlots=>this.slotByKey.Count;
        /// <summary>slot が足りず、退避できる Cell も無かったために生成できなかった要求の累計。</summary>
        public int StarvedRequests{get;private set;}
        public int Evictions{get;private set;}

        /// <summary>選択の設定を変える。有効なまま変えた場合は無効化して作り直す。</summary>
        protected void ConfigureSelection(int maximumLevel,float targetPixels){
            this.maximumLevel=Mathf.Clamp(maximumLevel,0,GpuMaximumLevel);this.targetPixels=targetPixels;
        }

        /// <summary>有効なら無効化して作り直す(形の設定を変えたとき)。</summary>
        protected void Rebuild(){
            if(this.isActiveAndEnabled){this.OnDisable();this.OnEnable();}
        }

        protected override VirtualGeometryGridLayout CreateLayout(){
            return new VirtualGeometryGridLayout(CellQuads,ClusterQuads);
        }

        protected override void OnEnable(){
            base.OnEnable();
            if(this.Layout==null){return;}
            this.selector=new PlanetCellSelector(this.Radius,this.SelectionAmplitude,CellQuads,Mathf.Min(this.maximumLevel,GpuMaximumLevel),this.skirtQuads){
                TargetPixels=this.targetPixels,Hysteresis=this.hysteresis,
            };
            this.keyBySlot=new PlanetCellKey[this.PoolCapacity];this.used=new bool[this.PoolCapacity];this.touchedFrame=new int[this.PoolCapacity];
            this.actualBounds=new Vector4[this.PoolCapacity];this.actualValid=new bool[this.PoolCapacity];this.slotVersion=new int[this.PoolCapacity];
            this.cellFrames=new PlanetCellFrame[this.PoolCapacity];
            this.placement=this.GetComponent<IPlanetPlacement>();
            this.slotByKey.Clear();this.history.Clear();this.requests.Clear();this.output.Clear();
            this.StarvedRequests=0;this.Evictions=0;
            for(int face=0;face<CubeSphere.FaceCount;face++){this.Request(PlanetCellKey.Root(face),float.MaxValue);}
            this.FlushRequests();
        }

        protected override void OnDisable(){
            this.slotByKey.Clear();this.history.Clear();this.requests.Clear();this.output.Clear();
            this.keyBySlot=null;this.used=null;this.touchedFrame=null;this.selector=null;
            this.actualBounds=null;this.actualValid=null;this.slotVersion=null;this.cellFrames=null;
            this.placement=null;this.drawProperties.Clear();
            base.OnDisable();
        }

        protected override void SelectDraws(in VirtualGeometryClusterView view,VirtualGeometryClusterDraws draws){
            if(this.selector==null){return;}
            IPlanetPlacement current=this.placement;
            // 配置の component が無い、破棄された、または無効なら Unity の world 空間に置く。
            if(current is Behaviour behaviour&&(behaviour==null||!behaviour.isActiveAndEnabled)){current=null;}
            if(current==null){current=this.standardPlacement;}
            if(!current.TryBeginFrame(in view,this.transform,out PlanetPlacementFrame frame)){return;}
            draws.CullSpace=frame.CullSpace;
            var selectionView=new PlanetSelectionView(in frame.CameraLocal,this.selector.LogicalRadius,frame.LocalToCull,frame.CameraCull,frame.LocalToCullScale,
                frame.CullSpace.Planes,view.ProjectionScale,frame.CullSpace.NearPlane,view.Orthographic,this.horizonCulling);
            if(!this.history.TryGetValue(view.Camera,out HashSet<PlanetCellKey> previous)){
                previous=new HashSet<PlanetCellKey>();this.history.Add(view.Camera,previous);
            }
            this.selector.Select(in selectionView,this,previous,this.output,this.refinedScratch);
            // 今回の細分化を次の frame の履歴にする(2つの集合を入れ替えて使い回す)。
            this.history[view.Camera]=this.refinedScratch;this.refinedScratch=previous;
            this.FlushRequests();
            List<MaterialPropertyBlock> blocks=null;
            if((current.UsesProperties||this.UsesCellProperties)&&!this.drawProperties.TryGetValue(view.Camera,out blocks)){
                blocks=new List<MaterialPropertyBlock>();this.drawProperties.Add(view.Camera,blocks);
            }
            int used=0;
            for(int i=0;i<this.output.Count;i++){
                if(!this.slotByKey.TryGetValue(this.output[i],out int slot)||!this.IsReady(slot)){continue;}
                MaterialPropertyBlock block=null;
                if(blocks!=null){
                    if(used==blocks.Count){blocks.Add(new MaterialPropertyBlock());}
                    block=blocks[used++];
                }
                if(!current.Describe(in frame,in this.cellFrames[slot],out Matrix4x4 objectToWorld,out Matrix4x4 objectToCull,block)){continue;}
                if(block!=null){this.WriteCellProperties(block,slot,in this.cellFrames[slot]);}
                draws.Begin(objectToWorld,objectToCull,block);
                draws.Add(slot);
                this.OnCellDrawn(slot,in this.cellFrames[slot]);
            }
        }

        /// <summary>
        /// Cell ごとの MaterialPropertyBlock を作るか(配置が MaterialPropertyBlock を使わなくても作る)。既定は true。
        /// fragment の shader が使う値は <see cref="WriteCellProperties"/> が書く。
        /// </summary>
        protected virtual bool UsesCellProperties=>true;

        /// <summary>
        /// Cell の draw の MaterialPropertyBlock に、この Layer の shader が使う値を書く(配置が書く値の後に呼ぶ)。
        /// _PlanetCellUp: xyz=Cell の原点の外向きの単位方向(object 空間)、w=惑星の半径。
        /// _PlanetCellQuad: x=この階層の quad の弧長。
        /// _PlanetCellRef: xyz=Cell の原点 − 基準点(<see cref="SetReferencePoint"/>)を Logical で引いてから float にしたもの(object 空間)、w=基準点があれば 1。
        /// 派生クラスは base を呼んでから、自分の値を足す。
        /// </summary>
        protected virtual void WriteCellProperties(MaterialPropertyBlock block,int slot,in PlanetCellFrame frame){
            Vector3 up=frame.ApproximateOrigin.normalized;
            block.SetVector(PlanetCellUpId,new Vector4(up.x,up.y,up.z,this.Radius));
            block.SetVector(PlanetCellQuadId,new Vector4(this.selector.QuadArc(frame.Key.Level),0,0,0));
            Vector4 reference=Vector4.zero;
            if(this.hasReference&&LogicalVector3.TrySubtract(frame.Origin,this.referencePoint,out LogicalVector3 difference)&&difference.TryToFloat3(out Unity.Mathematics.float3 value)){
                reference=new Vector4(value.x,value.y,value.z,1f);
            }
            block.SetVector(PlanetCellRefId,reference);
        }

        /// <summary>
        /// 基準点(惑星のローカル座標、Logical)を決める。shader は Cell の原点からの差を、基準点からの差(_PlanetCellRef)として受け取れる。
        /// 基準点の近くの模様(目盛りなど)を、惑星の大きさによらず mm の精度で描くために使う。
        /// </summary>
        public void SetReferencePoint(in LogicalVector3 planetLocalPoint){
            this.referencePoint=planetLocalPoint;this.hasReference=true;
        }

        public void ClearReferencePoint(){
            this.hasReference=false;
        }

        /// <summary>この frame に描く Cell ごとに呼ぶ(時間で動く Layer が作り直しを予約するなど)。既定では何もしない。</summary>
        protected virtual void OnCellDrawn(int slot,in PlanetCellFrame frame){
        }

        /// <summary>検証用。生成済みの Cell の頂点(Cell の原点からの相対位置)と、Cell の中心の値を返す。</summary>
        public bool TryReadbackCellForDiagnostics(in PlanetCellKey key,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame frame){
            vertices=null;frame=default;
            if(!this.slotByKey.TryGetValue(key,out int slot)||!this.IsReady(slot)){return false;}
            vertices=this.ReadbackSlotVerticesForDiagnostics(slot);frame=this.cellFrames[slot];
            return true;
        }

        /// <summary>
        /// point(惑星のローカル座標、Logical)の真下の Cell とその周りの 3×3 を、quad が eyeHeight 以下になる階層まで生成するよう要求する。
        /// すべて生成済みなら true。瞬間移動の前に true になるまで呼び、それから移すと、生成が追いつかない間に描く粗い Cell の面が
        /// カメラより上に来ない(粗い面は細かい地形をならすので、谷では本当の地表より高い)。
        /// </summary>
        public bool Prefetch(in LogicalVector3 point,float eyeHeight){
            if(this.selector==null||!point.TryToFloat3(out Unity.Mathematics.float3 approximate)){return false;}
            Vector3 direction=((Vector3)approximate).normalized;
            if(direction==Vector3.zero){return false;}
            int target=0;
            while(target<Mathf.Min(this.maximumLevel,PrefetchLevelLimit)&&this.selector.QuadArc(target)>eyeHeight){target++;}
            bool ready=true;
            for(int level=0;level<=target;level++){
                PlanetCellKey center=PlanetCellGeometry.KeyAt(direction,level);
                ready&=this.RequestBlock(in center);
            }
            return ready;
        }

        // center を中心とする 3×3(x の −・中・+ の列の、それぞれ y の −・中・+)を、生成済みなら使用中にし、まだなら要求する。
        // Neighbor の方向は 0:−x、1:+x、2:−y、3:+y。
        private bool RequestBlock(in PlanetCellKey center){
            bool ready=true;
            PlanetCellKey[] columns={center.Neighbor(0),center,center.Neighbor(1)};
            foreach(PlanetCellKey column in columns){
                ready&=this.RequestOne(in column);
                ready&=this.RequestOne(column.Neighbor(2));
                ready&=this.RequestOne(column.Neighbor(3));
            }
            return ready;
        }

        private bool RequestOne(in PlanetCellKey key){
            if(this.IsReady(in key)){
                this.Touch(in key);
                return true;
            }
            this.Request(in key,float.MaxValue*0.5f);
            return false;
        }

        public bool IsReady(in PlanetCellKey key){
            return this.slotByKey.TryGetValue(key,out int slot)&&this.IsReady(slot);
        }

        public void Request(in PlanetCellKey key,float priority){
            if(this.slotByKey.ContainsKey(key)){return;}
            if(this.requests.TryGetValue(key,out float existing)&&existing>=priority){return;}
            this.requests[key]=priority;
        }

        public bool TryGetBounds(in PlanetCellKey key,out Vector3 offset,out float radius){
            offset=default;radius=0;
            if(!this.slotByKey.TryGetValue(key,out int slot)||!this.actualValid[slot]){return false;}
            Vector4 bounds=this.actualBounds[slot];
            offset=new Vector3(bounds.x,bounds.y,bounds.z);radius=bounds.w;
            return true;
        }

        /// <summary>生成した slot の cluster の bounds を非同期で読み戻し、Cell の bounds にする(数 frame 後に届く)。</summary>
        protected override void OnSlotsGenerated(IReadOnlyList<int> slots){
            GraphicsBuffer bounds=this.ClusterBoundsBuffer;
            if(bounds==null||!SystemInfo.supportsAsyncGPUReadback){return;}
            int clusters=this.Layout.SlotClusterCount;
            for(int i=0;i<slots.Count;i++){
                int slot=slots[i];
                int version=this.slotVersion[slot];
                AsyncGPUReadback.Request(bounds,clusters*16,slot*clusters*16,request=>this.ReceiveBounds(request,slot,version));
            }
        }

        private void ReceiveBounds(AsyncGPUReadbackRequest request,int slot,int version){
            if(request.hasError||this==null||this.slotVersion==null||this.slotVersion[slot]!=version){return;}
            Unity.Collections.NativeArray<Vector4> data=request.GetData<Vector4>();
            Vector3 low=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue);
            Vector3 high=new Vector3(float.MinValue,float.MinValue,float.MinValue);
            for(int i=0;i<data.Length;i++){
                Vector4 cluster=data[i];
                Vector3 center=new Vector3(cluster.x,cluster.y,cluster.z);
                Vector3 extent=new Vector3(cluster.w,cluster.w,cluster.w);
                low=Vector3.Min(low,center-extent);high=Vector3.Max(high,center+extent);
            }
            // cluster の bounds は Cell の原点からの相対位置。選択もこの相対位置のまま使う(大きな惑星で float の原点を足さない)。
            Vector3 middle=(low+high)*0.5f;
            this.actualBounds[slot]=new Vector4(middle.x,middle.y,middle.z,((high-low)*0.5f).magnitude);
            this.actualValid[slot]=true;
        }

        public void Touch(in PlanetCellKey key){
            if(this.slotByKey.TryGetValue(key,out int slot)){this.touchedFrame[slot]=Time.frameCount;}
        }

        // 要求を優先度の高い順に slot へ割り当て、生成を予約する。1 frame に割り当てる数は生成予算の数倍まで。
        private void FlushRequests(){
            if(this.requests.Count==0||this.keyBySlot==null){return;}
            this.requestOrder.Clear();this.requestOrder.AddRange(this.requests);this.requests.Clear();
            this.requestOrder.Sort((a,b)=>b.Value.CompareTo(a.Value));
            int limit=this.GenerationBudget*4;
            for(int i=0;i<this.requestOrder.Count&&i<limit;i++){
                PlanetCellKey key=this.requestOrder[i].Key;
                if(this.slotByKey.ContainsKey(key)){continue;}
                PlanetCellFrame frame=this.selector.Frame(in key);
                if(!this.TryAllocateSlot(out int slot)){
                    if(!this.TryEvict()||!this.TryAllocateSlot(out slot)){
                        this.StarvedRequests+=this.requestOrder.Count-i;
                        break;
                    }
                }
                this.slotByKey.Add(key,slot);this.keyBySlot[slot]=key;this.used[slot]=true;this.touchedFrame[slot]=Time.frameCount;
                this.actualValid[slot]=false;this.slotVersion[slot]++;
                this.cellFrames[slot]=frame;
                this.RequestGeneration(slot);
            }
        }

        // 今 frame に使っておらず、子の slot も持たない Cell のうち、最も長く使っていないものを返す。root は返さない。
        private bool TryEvict(){
            int best=-1;int oldest=int.MaxValue;int frame=Time.frameCount;
            for(int slot=0;slot<this.used.Length;slot++){
                if(!this.used[slot]||this.touchedFrame[slot]>=frame){continue;}
                PlanetCellKey key=this.keyBySlot[slot];
                if(key.Level==0||this.HasAllocatedChild(in key)){continue;}
                if(this.touchedFrame[slot]<oldest){oldest=this.touchedFrame[slot];best=slot;}
            }
            if(best<0){return false;}
            this.slotByKey.Remove(this.keyBySlot[best]);this.used[best]=false;
            this.ReleaseSlot(best);
            this.Evictions++;
            return true;
        }

        private bool HasAllocatedChild(in PlanetCellKey key){
            if(key.Level>=PlanetCellCoordinate.Bits){return false;}
            for(int child=0;child<4;child++){
                if(this.slotByKey.ContainsKey(key.Child(child))){return true;}
            }
            return false;
        }

        /// <summary>生成の全 slot に共通の値を設定する(PlanetCell.hlsl の値は設定済み)。</summary>
        protected abstract void BindGeneration(CommandBuffer commands,ComputeShader shader,int kernel);

        /// <summary>Cell ごとの値を設定する(PlanetCell.hlsl の Cell ごとの値は設定済み)。</summary>
        protected abstract void BindCell(CommandBuffer commands,ComputeShader shader,int slot,in PlanetCellFrame frame);

        protected override void RecordGeneration(CommandBuffer commands,GraphicsBuffer poolVertices,IReadOnlyList<int> slots){
            if(this.generator==null){
                this.generator=Resources.Load<ComputeShader>(this.GeneratorResource);
                if(this.generator==null){throw new InvalidOperationException(this.GeneratorResource+".compute が見つかりません。");}
                this.kernel=this.generator.FindKernel("Generate");
            }
            VirtualGeometryGridLayout layout=this.Layout;
            ComputeShader shader=this.generator;
            commands.SetComputeIntParam(shader,"_CellQuads",layout.CellQuads);
            commands.SetComputeIntParam(shader,"_SlotVertexCount",layout.SlotVertexCount);
            commands.SetComputeIntParam(shader,"_GridVertexCount",layout.GridVertexCount);
            commands.SetComputeFloatParam(shader,"_Radius",this.Radius);
            commands.SetComputeBufferParam(shader,this.kernel,"_Vertices",poolVertices);
            this.BindGeneration(commands,shader,this.kernel);
            for(int i=0;i<slots.Count;i++){
                int slot=slots[i];
                PlanetCellKey key=this.keyBySlot[slot];
                PlanetCellFrame frame=this.cellFrames[slot];
                PlanetCellSelector.FaceRange(in key,out float s0,out float t0,out float size);
                commands.SetComputeIntParam(shader,"_Slot",slot);
                commands.SetComputeIntParam(shader,"_Face",key.Face);
                commands.SetComputeFloatParam(shader,"_QuadAngle",Mathf.PI*0.5f*MathF.Pow(2f,-key.Level)/layout.CellQuads);
                commands.SetComputeVectorParam(shader,"_CenterTangent",frame.ApproximateTangent);
                commands.SetComputeVectorParam(shader,"_FaceRange",new Vector4(s0,t0,size,0));
                commands.SetComputeFloatParam(shader,"_SkirtDepth",this.selector.SkirtDepth(key.Level));
                commands.SetComputeFloatParam(shader,"_QuadLength",this.selector.QuadArc(key.Level));
                this.BindCell(commands,shader,slot,in frame);
                commands.DispatchCompute(shader,this.kernel,(layout.SlotVertexCount+63)/64,1,1);
            }
        }
    }
}
