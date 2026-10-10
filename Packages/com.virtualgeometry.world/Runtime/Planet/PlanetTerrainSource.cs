using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualizedGeometry;

namespace VirtualGeometry.World{
    /// <summary>
    /// 手続き生成の地形の惑星を、実行時生成 cluster で描く。惑星の中心はこの GameObject の原点。
    /// Cell の選択は <see cref="PlanetCellSelector"/>、頂点の生成は PlanetTerrainGenerate.compute が行う。
    /// Cell と pool の slot の対応を持ち、pool が足りないときは使っていない Cell を下(葉)から退避する。
    /// 座標は float(P3。半径は float の精度で足りる大きさに限る。Logical 精度は P4)。
    /// </summary>
    [ExecuteAlways]
    public sealed class PlanetTerrainSource:VirtualGeometryClusterSource,IPlanetCellResidency{
        public const int CellQuads=64;
        public const int ClusterQuads=8;
        /// <summary>
        /// GPU は面の通し番号 g=x·CellQuads+i を float で扱う。法線に g±1 を使うので、g が float で正確に表せる 2^24 以下に限る
        /// (CellQuads=64 なら階層 18 まで)。これより細かい階層は P4 で Cell の局所原点に置き換える。
        /// </summary>
        public const int GpuMaximumLevel=18;
        private const string GeneratorResource="VirtualGeometryWorld/PlanetTerrainGenerate";

        [SerializeField,Min(1)] private float radius=10000;
        [SerializeField,Min(0)] private float heightAmplitude=300;
        [SerializeField,Min(0.1f)] private float targetPixels=2;
        [SerializeField,Range(0,0.5f)] private float hysteresis=0.1f;
        [SerializeField,Range(0,GpuMaximumLevel)] private int maximumLevel=16;
        [SerializeField,Min(0.01f)] private float baseWavelength=4000;
        [SerializeField,Range(1,20)] private int octaves=13;
        [SerializeField,Min(0)] private float skirtQuads=2;
        [SerializeField] private bool horizonCulling=true;

        private PlanetCellSelector selector;
        private ComputeShader generator;
        private int kernel=-1;
        private readonly Dictionary<PlanetCellKey,int> slotByKey=new Dictionary<PlanetCellKey,int>();
        private PlanetCellKey[] keyBySlot;
        private bool[] used;
        private int[] touchedFrame;
        // 生成した頂点から求めた実際の bounds(中心 xyz、半径 w)。slot を別の Cell に使い直すと version が変わり、古い readback を捨てる。
        private Vector4[] actualBounds;
        private bool[] actualValid;
        private int[] slotVersion;
        private readonly Dictionary<Camera,HashSet<PlanetCellKey>> history=new Dictionary<Camera,HashSet<PlanetCellKey>>();
        private readonly Dictionary<PlanetCellKey,float> requests=new Dictionary<PlanetCellKey,float>();
        private readonly List<KeyValuePair<PlanetCellKey,float>> requestOrder=new List<KeyValuePair<PlanetCellKey,float>>();
        private readonly List<PlanetCellKey> output=new List<PlanetCellKey>();
        private HashSet<PlanetCellKey> refinedScratch=new HashSet<PlanetCellKey>();

        public float Radius=>this.radius;
        public float HeightAmplitude=>this.heightAmplitude;
        public float TargetPixels{get=>this.targetPixels;set{this.targetPixels=value;if(this.selector!=null){this.selector.TargetPixels=value;}}}
        public int MaximumLevel=>this.maximumLevel;
        public PlanetCellSelector Selector=>this.selector;
        /// <summary>直近の選択で描いた Cell。</summary>
        public IReadOnlyList<PlanetCellKey> LastSelected=>this.output;
        public int UsedSlots=>this.slotByKey.Count;
        /// <summary>slot が足りず、退避できる Cell も無かったために生成できなかった要求の累計。</summary>
        public int StarvedRequests{get;private set;}
        public int Evictions{get;private set;}

        /// <summary>検証用。形の設定を変える。有効なまま変えた場合は無効化して作り直す。</summary>
        public void Configure(float radius,float heightAmplitude,int maximumLevel,float targetPixels){
            this.radius=radius;this.heightAmplitude=heightAmplitude;this.maximumLevel=Mathf.Clamp(maximumLevel,0,GpuMaximumLevel);this.targetPixels=targetPixels;
            if(this.isActiveAndEnabled){this.OnDisable();this.OnEnable();}
        }

        protected override VirtualGeometryGridLayout CreateLayout(){
            return new VirtualGeometryGridLayout(CellQuads,ClusterQuads);
        }

        protected override void OnEnable(){
            base.OnEnable();
            if(this.Layout==null){return;}
            this.selector=new PlanetCellSelector(this.radius,this.heightAmplitude,CellQuads,Mathf.Min(this.maximumLevel,GpuMaximumLevel),this.skirtQuads){
                TargetPixels=this.targetPixels,Hysteresis=this.hysteresis,
            };
            this.keyBySlot=new PlanetCellKey[this.PoolCapacity];this.used=new bool[this.PoolCapacity];this.touchedFrame=new int[this.PoolCapacity];
            this.actualBounds=new Vector4[this.PoolCapacity];this.actualValid=new bool[this.PoolCapacity];this.slotVersion=new int[this.PoolCapacity];
            this.slotByKey.Clear();this.history.Clear();this.requests.Clear();this.output.Clear();
            this.StarvedRequests=0;this.Evictions=0;
            for(int face=0;face<CubeSphere.FaceCount;face++){this.Request(PlanetCellKey.Root(face),float.MaxValue);}
            this.FlushRequests();
        }

        protected override void OnDisable(){
            this.slotByKey.Clear();this.history.Clear();this.requests.Clear();this.output.Clear();
            this.keyBySlot=null;this.used=null;this.touchedFrame=null;this.selector=null;
            this.actualBounds=null;this.actualValid=null;this.slotVersion=null;
            base.OnDisable();
        }

        protected override void SelectSlots(in VirtualGeometryClusterView view,List<int> drawSlots){
            if(this.selector==null){return;}
            Matrix4x4 localToWorld=this.transform.localToWorldMatrix;
            float scale=Mathf.Max(localToWorld.lossyScale.x,Mathf.Max(localToWorld.lossyScale.y,localToWorld.lossyScale.z));
            var selectionView=new PlanetSelectionView(this.transform.InverseTransformPoint(view.CameraPosition),localToWorld,Mathf.Abs(scale),
                view.FrustumPlanes,view.ProjectionScale,view.NearPlane,view.Orthographic,this.horizonCulling);
            if(!this.history.TryGetValue(view.Camera,out HashSet<PlanetCellKey> previous)){
                previous=new HashSet<PlanetCellKey>();this.history.Add(view.Camera,previous);
            }
            this.selector.Select(in selectionView,this,previous,this.output,this.refinedScratch);
            // 今回の細分化を次の frame の履歴にする(2つの集合を入れ替えて使い回す)。
            this.history[view.Camera]=this.refinedScratch;this.refinedScratch=previous;
            this.FlushRequests();
            for(int i=0;i<this.output.Count;i++){
                if(this.slotByKey.TryGetValue(this.output[i],out int slot)){drawSlots.Add(slot);}
            }
        }

        public bool IsReady(in PlanetCellKey key){
            return this.slotByKey.TryGetValue(key,out int slot)&&this.IsReady(slot);
        }

        public void Request(in PlanetCellKey key,float priority){
            if(this.slotByKey.ContainsKey(key)){return;}
            if(this.requests.TryGetValue(key,out float existing)&&existing>=priority){return;}
            this.requests[key]=priority;
        }

        public bool TryGetBounds(in PlanetCellKey key,out Vector3 center,out float radius){
            center=default;radius=0;
            if(!this.slotByKey.TryGetValue(key,out int slot)||!this.actualValid[slot]){return false;}
            Vector4 bounds=this.actualBounds[slot];
            center=new Vector3(bounds.x,bounds.y,bounds.z);radius=bounds.w;
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
                if(!this.TryAllocateSlot(out int slot)){
                    if(!this.TryEvict()||!this.TryAllocateSlot(out slot)){
                        this.StarvedRequests+=this.requestOrder.Count-i;
                        break;
                    }
                }
                this.slotByKey.Add(key,slot);this.keyBySlot[slot]=key;this.used[slot]=true;this.touchedFrame[slot]=Time.frameCount;
                this.actualValid[slot]=false;this.slotVersion[slot]++;
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

        protected override void RecordGeneration(CommandBuffer commands,GraphicsBuffer poolVertices,IReadOnlyList<int> slots){
            if(this.generator==null){
                this.generator=Resources.Load<ComputeShader>(GeneratorResource);
                if(this.generator==null){throw new InvalidOperationException(GeneratorResource+".compute が見つかりません。");}
                this.kernel=this.generator.FindKernel("Generate");
            }
            VirtualGeometryGridLayout layout=this.Layout;
            ComputeShader shader=this.generator;
            commands.SetComputeIntParam(shader,"_CellQuads",layout.CellQuads);
            commands.SetComputeIntParam(shader,"_SlotVertexCount",layout.SlotVertexCount);
            commands.SetComputeIntParam(shader,"_GridVertexCount",layout.GridVertexCount);
            commands.SetComputeFloatParam(shader,"_Radius",this.radius);
            commands.SetComputeFloatParam(shader,"_HeightAmplitude",this.heightAmplitude);
            commands.SetComputeFloatParam(shader,"_BaseWavelength",this.baseWavelength);
            commands.SetComputeIntParam(shader,"_Octaves",this.octaves);
            commands.SetComputeBufferParam(shader,this.kernel,"_Vertices",poolVertices);
            for(int i=0;i<slots.Count;i++){
                int slot=slots[i];
                PlanetCellKey key=this.keyBySlot[slot];
                commands.SetComputeIntParam(shader,"_Slot",slot);
                commands.SetComputeIntParam(shader,"_Face",key.Face);
                commands.SetComputeIntParam(shader,"_Level",key.Level);
                commands.SetComputeIntParam(shader,"_CellX",(int)(uint)key.X.Low);
                commands.SetComputeIntParam(shader,"_CellY",(int)(uint)key.Y.Low);
                commands.SetComputeFloatParam(shader,"_SkirtDepth",this.selector.SkirtDepth(key.Level));
                commands.DispatchCompute(shader,this.kernel,(layout.SlotVertexCount+63)/64,1,1);
            }
        }
    }
}
