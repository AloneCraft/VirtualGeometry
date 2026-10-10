using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualizedGeometry{
    /// <summary>
    /// 実行時生成clusterの1 slot(cell)の頂点とclusterの配置。全slotが同じ配置を使い、index値はslot内の頂点番号。
    /// 頂点はgrid((N+1)×(N+1)、行優先 v=y*(N+1)+x)の後に、4辺のskirt(辺ごとにN+1個。辺0:y=0、1:x=N、2:y=N、3:x=0、辺に沿ってx・yの増える順)を置く。
    /// clusterはM×M quadの格子で、cell境界に接するclusterは、その辺のskirt(両面)も含む。
    /// 供給元はgridのu(x方向)・v(y方向)を、外向き法線がcross(v,u)になる向きで置く(Unityの左手系で時計回りが表)。
    /// </summary>
    public sealed class VirtualGeometryGridLayout{
        public int CellQuads{get;}
        public int ClusterQuads{get;}
        public int GridVertexCount{get;}
        public int SlotVertexCount{get;}
        public int SlotClusterCount{get;}
        public int SlotIndexCount{get;}
        internal uint[] TemplateRanges{get;}
        internal uint[] TemplateIndices{get;}

        public VirtualGeometryGridLayout(int cellQuads,int clusterQuads){
            if(clusterQuads<1||cellQuads<clusterQuads||cellQuads%clusterQuads!=0){throw new ArgumentException("cellQuadsはclusterQuadsの正の倍数にしてください。");}
            this.CellQuads=cellQuads;this.ClusterQuads=clusterQuads;
            int side=cellQuads+1;
            this.GridVertexCount=side*side;
            this.SlotVertexCount=this.GridVertexCount+4*side;
            int clustersPerSide=cellQuads/clusterQuads;
            this.SlotClusterCount=clustersPerSide*clustersPerSide;
            this.TemplateRanges=new uint[this.SlotClusterCount*2];
            List<uint> indices=new List<uint>();
            for(int cy=0;cy<clustersPerSide;cy++){
                for(int cx=0;cx<clustersPerSide;cx++){
                    int cluster=cy*clustersPerSide+cx;
                    this.TemplateRanges[cluster*2]=(uint)indices.Count;
                    for(int y=cy*clusterQuads;y<(cy+1)*clusterQuads;y++){
                        for(int x=cx*clusterQuads;x<(cx+1)*clusterQuads;x++){
                            uint a=this.Grid(x,y);uint b=this.Grid(x+1,y);uint c=this.Grid(x,y+1);uint d=this.Grid(x+1,y+1);
                            indices.Add(a);indices.Add(c);indices.Add(b);
                            indices.Add(b);indices.Add(c);indices.Add(d);
                        }
                    }
                    if(cy==0){this.AddSkirt(indices,0,cx*clusterQuads,clusterQuads);}
                    if(cx==clustersPerSide-1){this.AddSkirt(indices,1,cy*clusterQuads,clusterQuads);}
                    if(cy==clustersPerSide-1){this.AddSkirt(indices,2,cx*clusterQuads,clusterQuads);}
                    if(cx==0){this.AddSkirt(indices,3,cy*clusterQuads,clusterQuads);}
                    this.TemplateRanges[cluster*2+1]=(uint)indices.Count-this.TemplateRanges[cluster*2];
                }
            }
            this.TemplateIndices=indices.ToArray();
            int maximum=0;
            for(int cluster=0;cluster<this.SlotClusterCount;cluster++){maximum+=(int)this.TemplateRanges[cluster*2+1];}
            this.SlotIndexCount=maximum;
        }

        /// <summary>grid頂点(x,y)のslot内番号。</summary>
        public uint Grid(int x,int y){
            return (uint)(y*(this.CellQuads+1)+x);
        }

        /// <summary>辺edgeのt番目のskirt頂点のslot内番号。</summary>
        public uint Skirt(int edge,int t){
            return (uint)(this.GridVertexCount+edge*(this.CellQuads+1)+t);
        }

        /// <summary>辺edgeのt番目に対応するgrid頂点。</summary>
        public uint EdgeGrid(int edge,int t){
            int n=this.CellQuads;
            if(edge==0){return this.Grid(t,0);}
            if(edge==1){return this.Grid(n,t);}
            if(edge==2){return this.Grid(t,n);}
            return this.Grid(0,t);
        }

        private void AddSkirt(List<uint> indices,int edge,int start,int count){
            for(int t=start;t<start+count;t++){
                uint g0=this.EdgeGrid(edge,t);uint g1=this.EdgeGrid(edge,t+1);uint s0=this.Skirt(edge,t);uint s1=this.Skirt(edge,t+1);
                // 視点が辺のどちら側にあっても隙間を塞ぐため、両面を出す。
                indices.Add(g0);indices.Add(s0);indices.Add(g1);
                indices.Add(g1);indices.Add(s0);indices.Add(s1);
                indices.Add(g0);indices.Add(g1);indices.Add(s0);
                indices.Add(g1);indices.Add(s1);indices.Add(s0);
            }
        }
    }

    /// <summary>実行時生成clusterの頂点・bounds・cluster配置を持つGPU pool。slotは供給元が割り当てる。</summary>
    internal sealed class VirtualGeometryClusterPool:IDisposable{
        public readonly VirtualGeometryGridLayout Layout;
        public readonly int Capacity;
        public readonly GraphicsBuffer Vertices;
        public readonly GraphicsBuffer ClusterBounds;
        public readonly GraphicsBuffer TemplateRanges;
        public readonly GraphicsBuffer TemplateIndices;
        public readonly GraphicsBuffer DebugDummy;
        private readonly Stack<int> free=new Stack<int>();

        public VirtualGeometryClusterPool(VirtualGeometryGridLayout layout,int capacity){
            this.Layout=layout;this.Capacity=capacity;
            try{
                this.Vertices=new GraphicsBuffer(GraphicsBuffer.Target.Structured,checked(capacity*layout.SlotVertexCount),VirtualGeometryPackedVertex.Stride);
                this.ClusterBounds=new GraphicsBuffer(GraphicsBuffer.Target.Structured,checked(capacity*layout.SlotClusterCount),16);
                this.TemplateRanges=new GraphicsBuffer(GraphicsBuffer.Target.Structured,layout.SlotClusterCount,8);
                this.TemplateIndices=new GraphicsBuffer(GraphicsBuffer.Target.Structured,layout.TemplateIndices.Length,4);
                this.DebugDummy=new GraphicsBuffer(GraphicsBuffer.Target.Structured,1,16);
                this.TemplateRanges.SetData(layout.TemplateRanges);this.TemplateIndices.SetData(layout.TemplateIndices);
            }catch{
                this.Dispose();
                throw;
            }
            for(int slot=capacity-1;slot>=0;slot--){this.free.Push(slot);}
        }

        public int FreeCount=>this.free.Count;
        public long Bytes=>(long)this.Capacity*this.Layout.SlotVertexCount*VirtualGeometryPackedVertex.Stride+(long)this.Capacity*this.Layout.SlotClusterCount*16;

        public bool TryAllocate(out int slot){
            if(this.free.Count==0){
                slot=-1;
                return false;
            }
            slot=this.free.Pop();
            return true;
        }

        public void Release(int slot){
            this.free.Push(slot);
        }

        public void Dispose(){
            this.Vertices?.Dispose();this.ClusterBounds?.Dispose();this.TemplateRanges?.Dispose();this.TemplateIndices?.Dispose();this.DebugDummy?.Dispose();
        }
    }

    /// <summary>cull 空間への変換(アフィンの3行)、可視 index の出力先、bounds の拡大率。GPU の VGClusterDrawRecord と同じ並び。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VirtualGeometryClusterDrawRecord{
        public const int Stride=64;
        public Vector4 Row0;
        public Vector4 Row1;
        public Vector4 Row2;
        public uint OutputStart;
        public float Scale;
        public uint Padding0;
        public uint Padding1;
    }

    /// <summary>実行時生成clusterの供給元×cameraの描画状態(候補slot、draw、可視index、indirect args、統計)。</summary>
    internal sealed class VirtualGeometryClusterCameraState:IDisposable{
        public readonly GraphicsBuffer Candidates;
        public readonly GraphicsBuffer DrawRecords;
        public readonly GraphicsBuffer VisibleIndices;
        public readonly GraphicsBuffer Arguments;
        public readonly GraphicsBuffer Statistics;
        /// <summary>候補ごとに (slot, draw)。</summary>
        public readonly uint[] CandidateData;
        public readonly VirtualGeometryClusterDrawRecord[] RecordData;
        public readonly Matrix4x4[] DrawObjectToWorld;
        public readonly MaterialPropertyBlock[] DrawProperties;
        public readonly Vector4[] PlaneData=new Vector4[6];
        public readonly VirtualGeometryClusterDraws Draws=new VirtualGeometryClusterDraws();
        public int CandidateCount;
        public int DrawCount;
        public VirtualGeometryClusterCullSpace CullSpace;

        public VirtualGeometryClusterCameraState(VirtualGeometryGridLayout layout,int maximumDrawSlots){
            try{
                this.Candidates=new GraphicsBuffer(GraphicsBuffer.Target.Structured,maximumDrawSlots,8);
                this.DrawRecords=new GraphicsBuffer(GraphicsBuffer.Target.Structured,maximumDrawSlots,VirtualGeometryClusterDrawRecord.Stride);
                this.VisibleIndices=new GraphicsBuffer(GraphicsBuffer.Target.Structured,checked(maximumDrawSlots*layout.SlotIndexCount),4);
                this.Arguments=new GraphicsBuffer(GraphicsBuffer.Target.Raw|GraphicsBuffer.Target.IndirectArguments,checked(maximumDrawSlots*4),4);
                this.Statistics=new GraphicsBuffer(GraphicsBuffer.Target.Structured,4,4);
                var arguments=new uint[maximumDrawSlots*4];
                for(int i=0;i<maximumDrawSlots;i++){arguments[i*4+1]=1;}
                this.Arguments.SetData(arguments);this.Statistics.SetData(new uint[4]);
            }catch{
                this.Dispose();
                throw;
            }
            this.CandidateData=new uint[maximumDrawSlots*2];
            this.RecordData=new VirtualGeometryClusterDrawRecord[maximumDrawSlots];
            this.DrawObjectToWorld=new Matrix4x4[maximumDrawSlots];
            this.DrawProperties=new MaterialPropertyBlock[maximumDrawSlots];
        }

        public void Dispose(){
            this.Candidates?.Dispose();this.DrawRecords?.Dispose();this.VisibleIndices?.Dispose();this.Arguments?.Dispose();this.Statistics?.Dispose();
        }
    }

    /// <summary>
    /// cluster の cull を行う空間。world とは限らない(Omni の対数描画では、カメラ相対の物理空間)。
    /// カメラの位置・視錐台の6平面・HZB 用の ViewProjection はすべてこの空間で表す。
    /// </summary>
    public readonly struct VirtualGeometryClusterCullSpace{
        public readonly Vector3 CameraPosition;
        public readonly Plane[] Planes;
        public readonly Matrix4x4 ViewProjection;
        public readonly float NearPlane;
        /// <summary>HZB の遮蔽判定を使ってよいか。深度が ViewProjection と同じ写像でないとき(対数描画など)は false。</summary>
        public readonly bool OcclusionAllowed;

        public VirtualGeometryClusterCullSpace(Vector3 cameraPosition,Plane[] planes,Matrix4x4 viewProjection,float nearPlane,bool occlusionAllowed){
            if(planes==null||planes.Length!=6){throw new ArgumentException("Six planes are required.",nameof(planes));}
            this.CameraPosition=cameraPosition;this.Planes=planes;this.ViewProjection=viewProjection;this.NearPlane=nearPlane;this.OcclusionAllowed=occlusionAllowed;
        }
    }

    /// <summary>
    /// 供給元が camera ごとに組み立てる draw の一覧。draw ごとに、描画行列(unity_ObjectToWorld)、cull 空間への行列、
    /// MaterialPropertyBlock(null なら共有)を持ち、その draw で描く slot を足す。
    /// </summary>
    public sealed class VirtualGeometryClusterDraws{
        internal readonly List<int> Slots=new List<int>();
        internal readonly List<int> SlotDraw=new List<int>();
        internal readonly List<Matrix4x4> ObjectToWorld=new List<Matrix4x4>();
        internal readonly List<Matrix4x4> ObjectToCull=new List<Matrix4x4>();
        internal readonly List<MaterialPropertyBlock> Properties=new List<MaterialPropertyBlock>();

        /// <summary>cull を行う空間。既定は world(この camera の値)。</summary>
        public VirtualGeometryClusterCullSpace CullSpace{get;set;}
        public int DrawCount=>this.ObjectToWorld.Count;

        /// <summary>新しい draw を始める。以後の <see cref="Add"/> はこの draw に入る。</summary>
        public void Begin(Matrix4x4 objectToWorld,Matrix4x4 objectToCull,MaterialPropertyBlock properties){
            this.ObjectToWorld.Add(objectToWorld);this.ObjectToCull.Add(objectToCull);this.Properties.Add(properties);
        }

        public void Add(int slot){
            if(this.ObjectToWorld.Count==0){throw new InvalidOperationException("Begin must be called before Add.");}
            this.Slots.Add(slot);this.SlotDraw.Add(this.ObjectToWorld.Count-1);
        }

        internal void Reset(VirtualGeometryClusterCullSpace space){
            this.Slots.Clear();this.SlotDraw.Clear();this.ObjectToWorld.Clear();this.ObjectToCull.Clear();this.Properties.Clear();
            this.CullSpace=space;
        }
    }

    /// <summary>実行時生成clusterの供給元がcameraごとの選択で受け取る視点の情報。座標はworld空間。</summary>
    public readonly struct VirtualGeometryClusterView{
        public readonly Camera Camera;
        public readonly Vector3 CameraPosition;
        public readonly Plane[] FrustumPlanes;
        /// <summary>透視投影では画面誤差 = world誤差×ProjectionScale/距離。正射影では world誤差×ProjectionScale。</summary>
        public readonly float ProjectionScale;
        public readonly float NearPlane;
        public readonly bool Orthographic;
        /// <summary>world 空間の GPU 用 ViewProjection(HZB の判定に使う)。</summary>
        public readonly Matrix4x4 ViewProjection;

        public VirtualGeometryClusterView(Camera camera,Vector3 cameraPosition,Plane[] frustumPlanes,float projectionScale,float nearPlane,bool orthographic,Matrix4x4 viewProjection){
            this.Camera=camera;this.CameraPosition=cameraPosition;this.FrustumPlanes=frustumPlanes;
            this.ProjectionScale=projectionScale;this.NearPlane=nearPlane;this.Orthographic=orthographic;this.ViewProjection=viewProjection;
        }
    }

    /// <summary>
    /// 実行時にclusterを生成して描く供給元の基底。派生クラスはcameraごとに描くslot(cell)を選び、slotの頂点をGPUで書く。
    /// clusterのbounds計算、Frustum・HZBのcull、可視indexの詰め込み、描画はVirtualGeometryRenderFeatureが行う。
    /// slotの頂点はframeごとに1回(beginContextRendering)、予算の範囲で生成する。生成済み(ready)のslotだけを描く。
    /// </summary>
    [ExecuteAlways]
    public abstract class VirtualGeometryClusterSource:MonoBehaviour{
        [SerializeField] private Material material;
        [SerializeField,Min(1)] private int poolCapacity=1024;
        [SerializeField,Min(1)] private int maximumDrawSlots=1024;
        [SerializeField,Min(1)] private int generationBudget=32;
        [SerializeField] private bool enableFrustumCulling=true;
        [SerializeField] private bool enableOcclusionCulling=true;

        private VirtualGeometryClusterPool pool;
        private bool[] ready;
        private readonly List<int> pending=new List<int>();
        private readonly HashSet<int> pendingSet=new HashSet<int>();
        private readonly List<int> generating=new List<int>();
        private readonly Dictionary<Camera,VirtualGeometryClusterCameraState> cameraStates=new Dictionary<Camera,VirtualGeometryClusterCameraState>();
        private readonly Dictionary<Camera,string> unsupported=new Dictionary<Camera,string>();
        private readonly List<int> drawScratch=new List<int>();
        private GraphicsBuffer boundsSlots;
        private CommandBuffer generationCommands;
        private int lastGenerationFrame=-1;
        private string lastError;

        public Material Material{get=>this.material;set=>this.material=value;}
        public bool EnableFrustumCulling{get=>this.enableFrustumCulling;set=>this.enableFrustumCulling=value;}
        public bool EnableOcclusionCulling{get=>this.enableOcclusionCulling;set=>this.enableOcclusionCulling=value;}
        public int PoolCapacity=>this.poolCapacity;
        public int MaximumDrawSlots=>this.maximumDrawSlots;
        public int GenerationBudget{get=>this.generationBudget;set=>this.generationBudget=Mathf.Max(1,value);}
        /// <summary>生成を待っているslot数。</summary>
        public int PendingGeneration=>this.pending.Count;
        /// <summary>空いているslot数。poolがまだ無いときは0。</summary>
        public int FreeSlots{get{if(this.pool==null){return 0;}return this.pool.FreeCount;}}
        /// <summary>直近の生成・描画で起きた設定エラー。無ければnull。</summary>
        public string LastError=>this.lastError;
        /// <summary>直近の描画で、このcameraが描けなかった理由。描けたならnull。</summary>
        public string UnsupportedReason(Camera camera){
            if(camera!=null&&this.unsupported.TryGetValue(camera,out string reason)){return reason;}
            return null;
        }

        /// <summary>全slotが使うgridとclusterの配置。poolを作るときに1回だけ読む。</summary>
        protected abstract VirtualGeometryGridLayout CreateLayout();

        /// <summary>
        /// このcameraで描くslotを選ぶ(既定の <see cref="SelectDraws"/> が使う。この GameObject の行列で1つの draw にする)。
        /// readyでないslotはRequestGenerationで生成を要求し、代わりに描けるslotを選ぶ。
        /// </summary>
        protected virtual void SelectSlots(in VirtualGeometryClusterView view,List<int> drawSlots){
        }

        /// <summary>
        /// このcameraで描く draw と slot を組み立てる。既定は world 空間で cull し、この GameObject の行列で1つの draw にする。
        /// slot ごとに行列が違う(Cell ごとの原点など)ときや、world 以外の空間で cull するときに上書きする。
        /// </summary>
        protected virtual void SelectDraws(in VirtualGeometryClusterView view,VirtualGeometryClusterDraws draws){
            Matrix4x4 matrix=this.transform.localToWorldMatrix;
            draws.Begin(matrix,matrix,null);
            this.drawScratch.Clear();
            this.SelectSlots(in view,this.drawScratch);
            for(int i=0;i<this.drawScratch.Count;i++){draws.Add(this.drawScratch[i]);}
        }

        /// <summary>slotの頂点(VirtualGeometryPackedVertex、slot内配置はLayout)をpoolVerticesへ書くGPU処理を記録する。</summary>
        protected abstract void RecordGeneration(CommandBuffer commands,GraphicsBuffer poolVertices,IReadOnlyList<int> slots);

        protected VirtualGeometryGridLayout Layout{get{if(this.pool==null){return null;}return this.pool.Layout;}}

        protected bool TryAllocateSlot(out int slot){
            slot=-1;
            if(this.pool==null||!this.pool.TryAllocate(out slot)){return false;}
            this.ready[slot]=false;
            return true;
        }

        protected void ReleaseSlot(int slot){
            if(this.pool==null||slot<0||slot>=this.pool.Capacity){return;}
            this.ready[slot]=false;
            if(this.pendingSet.Remove(slot)){this.pending.Remove(slot);}
            this.pool.Release(slot);
        }

        protected void RequestGeneration(int slot){
            if(this.pool==null||slot<0||slot>=this.pool.Capacity||this.pendingSet.Contains(slot)){return;}
            this.ready[slot]=false;this.pendingSet.Add(slot);this.pending.Add(slot);
        }

        protected bool IsReady(int slot){
            return this.pool!=null&&slot>=0&&slot<this.pool.Capacity&&this.ready[slot];
        }

        protected virtual void OnEnable(){
            this.lastError=null;
            try{
                this.pool=new VirtualGeometryClusterPool(this.CreateLayout(),this.poolCapacity);
            }catch(Exception error){
                this.lastError="cluster poolを作れません: "+error.Message;Debug.LogError(this.lastError,this);
                this.pool=null;
                return;
            }
            this.ready=new bool[this.pool.Capacity];
            this.boundsSlots=new GraphicsBuffer(GraphicsBuffer.Target.Structured,Mathf.Max(1,this.generationBudget),4);
            this.generationCommands=new CommandBuffer{name="Virtual Geometry Cluster Generation"};
            VirtualGeometryClusterSourceRegistry.Add(this);
            RenderPipelineManager.beginContextRendering+=this.OnBeginContextRendering;
        }

        protected virtual void OnDisable(){
            RenderPipelineManager.beginContextRendering-=this.OnBeginContextRendering;
            VirtualGeometryClusterSourceRegistry.Remove(this);
            foreach(VirtualGeometryClusterCameraState state in this.cameraStates.Values){state.Dispose();}
            this.cameraStates.Clear();this.unsupported.Clear();
            this.pending.Clear();this.pendingSet.Clear();this.generating.Clear();
            this.boundsSlots?.Dispose();this.boundsSlots=null;
            this.generationCommands?.Release();this.generationCommands=null;
            this.pool?.Dispose();this.pool=null;this.ready=null;
        }

        private void OnBeginContextRendering(ScriptableRenderContext context,List<Camera> cameras){
            if(this.pool==null||this.pending.Count==0||Time.frameCount==this.lastGenerationFrame){return;}
            ComputeShader shader=VirtualGeometryClusterShaders.Cull;
            if(shader==null){this.lastError="VirtualGeometryClusterCull.computeが見つかりません。";return;}
            this.lastGenerationFrame=Time.frameCount;
            int count=Mathf.Min(this.pending.Count,Mathf.Min(this.generationBudget,this.boundsSlots.count));
            this.generating.Clear();
            for(int i=0;i<count;i++){this.generating.Add(this.pending[i]);this.pendingSet.Remove(this.pending[i]);}
            this.pending.RemoveRange(0,count);
            CommandBuffer commands=this.generationCommands;commands.Clear();
            try{
                this.RecordGeneration(commands,this.pool.Vertices,this.generating);
            }catch(Exception error){
                this.lastError="cluster生成の記録に失敗しました: "+error.Message;Debug.LogException(error,this);
                return;
            }
            uint[] slots=new uint[count];
            for(int i=0;i<count;i++){slots[i]=(uint)this.generating[i];}
            commands.SetBufferData(this.boundsSlots,slots);
            int kernel=VirtualGeometryClusterShaders.BoundsKernel;
            VirtualGeometryGridLayout layout=this.pool.Layout;
            commands.SetComputeIntParam(shader,VirtualGeometryClusterShaders.SlotVertexCountId,layout.SlotVertexCount);
            commands.SetComputeIntParam(shader,VirtualGeometryClusterShaders.SlotClusterCountId,layout.SlotClusterCount);
            commands.SetComputeIntParam(shader,VirtualGeometryClusterShaders.BoundsSlotCountId,count);
            commands.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.PoolVerticesId,this.pool.Vertices);
            commands.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.TemplateRangesId,this.pool.TemplateRanges);
            commands.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.TemplateIndicesId,this.pool.TemplateIndices);
            commands.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.ClusterBoundsId,this.pool.ClusterBounds);
            commands.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.BoundsSlotsId,this.boundsSlots);
            commands.DispatchCompute(shader,kernel,(count*layout.SlotClusterCount+63)/64,1,1);
            Graphics.ExecuteCommandBuffer(commands);
            // 同じqueueで後に描くので、この後のcameraの描画では生成済みとして扱える。
            for(int i=0;i<count;i++){this.ready[this.generating[i]]=true;}
            this.OnSlotsGenerated(this.generating);
        }

        /// <summary>
        /// slot の頂点と cluster の bounds を生成する GPU 処理を予約した直後に呼ぶ。GPU の処理はまだ終わっていない
        /// (この後の描画と readback は同じ queue で後に並ぶので、生成後の値を読む)。既定では何もしない。
        /// </summary>
        protected virtual void OnSlotsGenerated(IReadOnlyList<int> slots){
        }

        /// <summary>cluster の bounds(float4: 中心 xyz、半径 w。slot ごとに SlotClusterCount 個)。readback に使う。</summary>
        protected GraphicsBuffer ClusterBoundsBuffer{get{if(this.pool==null){return null;}return this.pool.ClusterBounds;}}

        internal VirtualGeometryClusterPool Pool=>this.pool;

        internal void SetCameraSupport(Camera camera,string reason){
            if(camera==null){return;}
            if(reason==null){this.unsupported.Remove(camera);}else{this.unsupported[camera]=reason;}
        }

        internal bool CanDraw(Camera camera){
            return this.pool!=null&&camera!=null&&!this.unsupported.ContainsKey(camera);
        }

        internal VirtualGeometryClusterCameraState GetCameraState(Camera camera){
            if(!this.cameraStates.TryGetValue(camera,out VirtualGeometryClusterCameraState state)){
                state=new VirtualGeometryClusterCameraState(this.pool.Layout,this.maximumDrawSlots);
                this.cameraStates.Add(camera,state);
            }
            return state;
        }

        /// <summary>
        /// RenderFeatureがcameraごとに呼ぶ。描く ready slot を (slot, draw) の候補へ写し、draw ごとの出力先を決める。
        /// slot の合計は最大 MaximumDrawSlots。ready の slot が無い draw は捨てる。候補の数を返す。
        /// </summary>
        internal int CollectCandidates(in VirtualGeometryClusterView view,VirtualGeometryClusterCameraState state){
            VirtualGeometryClusterDraws draws=state.Draws;
            draws.Reset(new VirtualGeometryClusterCullSpace(view.CameraPosition,view.FrustumPlanes,view.ViewProjection,view.NearPlane,true));
            this.SelectDraws(in view,draws);
            int capacity=state.RecordData.Length;
            int slotIndexCount=this.pool.Layout.SlotIndexCount;
            int candidates=0;int drawCount=0;int skipped=0;
            int[] compact=new int[draws.DrawCount];
            for(int d=0;d<compact.Length;d++){compact[d]=-1;}
            for(int i=0;i<draws.Slots.Count;i++){
                int slot=draws.Slots[i];
                if(!this.IsReady(slot)){continue;}
                if(candidates>=capacity){skipped++;continue;}
                int source=draws.SlotDraw[i];
                int target=compact[source];
                if(target<0){
                    target=drawCount++;compact[source]=target;
                    Matrix4x4 cull=draws.ObjectToCull[source];
                    state.RecordData[target]=new VirtualGeometryClusterDrawRecord{
                        Row0=cull.GetRow(0),Row1=cull.GetRow(1),Row2=cull.GetRow(2),OutputStart=0,Scale=MaximumScale(cull),
                    };
                    state.DrawObjectToWorld[target]=draws.ObjectToWorld[source];
                    state.DrawProperties[target]=draws.Properties[source];
                }
                state.CandidateData[candidates*2]=(uint)slot;state.CandidateData[candidates*2+1]=(uint)target;
                candidates++;
            }
            // draw ごとの出力先は、その draw の候補の数×slot の index 数の区間(合計は容量以内)。
            var counts=new int[drawCount];
            for(int i=0;i<candidates;i++){counts[state.CandidateData[i*2+1]]++;}
            uint start=0;
            for(int d=0;d<drawCount;d++){
                state.RecordData[d].OutputStart=start;
                start+=(uint)(counts[d]*slotIndexCount);
            }
            if(skipped>0){this.lastError="描くslotが上限("+capacity+")を超え、"+skipped+"個を描けませんでした。";}
            state.CandidateCount=candidates;state.DrawCount=drawCount;state.CullSpace=draws.CullSpace;
            Plane[] planes=draws.CullSpace.Planes;
            for(int i=0;i<6;i++){state.PlaneData[i]=new Vector4(planes[i].normal.x,planes[i].normal.y,planes[i].normal.z,planes[i].distance);}
            return candidates;
        }

        private static float MaximumScale(Matrix4x4 matrix){
            float x=new Vector3(matrix.m00,matrix.m10,matrix.m20).magnitude;
            float y=new Vector3(matrix.m01,matrix.m11,matrix.m21).magnitude;
            float z=new Vector3(matrix.m02,matrix.m12,matrix.m22).magnitude;
            return Mathf.Max(x,Mathf.Max(y,z));
        }

        /// <summary>検証用。直近のcullの統計(候補cluster、Frustum棄却、HZB棄却、可視)を同期readbackで返す。</summary>
        public uint[] ReadbackStatisticsForDiagnostics(Camera camera){
            uint[] result=new uint[4];
            if(camera!=null&&this.cameraStates.TryGetValue(camera,out VirtualGeometryClusterCameraState state)){state.Statistics.GetData(result);}
            return result;
        }

        /// <summary>検証用。直近の描画の indirect args を同期 readback し、全 draw の index 数の合計を [0] に、draw 数を [1] に返す。</summary>
        public uint[] ReadbackArgumentsForDiagnostics(Camera camera){
            uint[] result=new uint[4];
            if(camera!=null&&this.cameraStates.TryGetValue(camera,out VirtualGeometryClusterCameraState state)&&state.DrawCount>0){
                var arguments=new uint[state.DrawCount*4];
                state.Arguments.GetData(arguments,0,0,arguments.Length);
                for(int d=0;d<state.DrawCount;d++){result[0]+=arguments[d*4];}
                result[1]=(uint)state.DrawCount;
            }
            return result;
        }
    }

    internal static class VirtualGeometryClusterSourceRegistry{
        private static readonly List<VirtualGeometryClusterSource> sources=new List<VirtualGeometryClusterSource>();
        public static IReadOnlyList<VirtualGeometryClusterSource> Sources=>sources;
        public static void Add(VirtualGeometryClusterSource source){if(source!=null&&!sources.Contains(source)){sources.Add(source);}}
        public static void Remove(VirtualGeometryClusterSource source){sources.Remove(source);}
    }

    internal static class VirtualGeometryClusterShaders{
        public static readonly int SlotVertexCountId=Shader.PropertyToID("_VGSlotVertexCount");
        public static readonly int SlotClusterCountId=Shader.PropertyToID("_VGSlotClusterCount");
        public static readonly int PoolVerticesId=Shader.PropertyToID("_VGPoolVertices");
        public static readonly int TemplateRangesId=Shader.PropertyToID("_VGClusterTemplateRanges");
        public static readonly int TemplateIndicesId=Shader.PropertyToID("_VGClusterTemplateIndices");
        public static readonly int ClusterBoundsId=Shader.PropertyToID("_VGClusterBounds");
        public static readonly int BoundsSlotsId=Shader.PropertyToID("_VGBoundsSlots");
        public static readonly int BoundsSlotCountId=Shader.PropertyToID("_VGBoundsSlotCount");
        public static readonly int CandidateSlotsId=Shader.PropertyToID("_VGCandidateSlots");
        public static readonly int CandidateCountId=Shader.PropertyToID("_VGCandidateCount");
        public static readonly int DrawRecordsId=Shader.PropertyToID("_VGDrawRecords");
        public static readonly int DrawCountId=Shader.PropertyToID("_VGDrawCount");
        public static readonly int FrustumEnabledId=Shader.PropertyToID("_VGFrustumEnabled");
        public static readonly int OcclusionEnabledId=Shader.PropertyToID("_VGOcclusionEnabled");
        public static readonly int VisibleIndicesId=Shader.PropertyToID("_VGVisibleIndices");
        public static readonly int ArgumentsId=Shader.PropertyToID("_VGArguments");
        public static readonly int StatisticsId=Shader.PropertyToID("_VGCullingStatistics");
        private static ComputeShader cull;private static bool loaded;
        public static int BoundsKernel{get;private set;}
        public static int ResetKernel{get;private set;}
        public static int CullKernel{get;private set;}

        public static ComputeShader Cull{
            get{
                if(!loaded||cull==null){
                    loaded=true;
                    cull=Resources.Load<ComputeShader>("VirtualGeometryCompute/VirtualGeometryClusterCull");
                    if(cull!=null){
                        BoundsKernel=cull.FindKernel("ComputeBounds");ResetKernel=cull.FindKernel("ResetArguments");CullKernel=cull.FindKernel("CullClusters");
                    }
                }
                return cull;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset(){
            cull=null;loaded=false;
        }
    }
}
