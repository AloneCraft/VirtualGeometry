using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualizedGeometry{
    /// <summary>
    /// 通常のMeshFilter/MeshRendererとVirtual Geometryの描画をつなぐ非表示の部品。モデルのインポートで自動で付き、Inspectorには表示しない。
    /// MeshFilterには低密度の代替Mesh(UE5のFallback Mesh相当)が入り、影・当たり判定・カリング・VGが使えないときの表示を受け持つ。
    /// </summary>
    [ExecuteAlways,DisallowMultipleComponent,RequireComponent(typeof(MeshFilter),typeof(MeshRenderer)),AddComponentMenu("")]
    public sealed partial class VirtualGeometryRenderer:MonoBehaviour{
        [SerializeField,Tooltip("変換済みの仮想ジオメトリ。モデルのInspectorで「Virtual Geometry」にチェックを入れるとモデル内に作られます。")] private VirtualGeometryMeshAsset meshAsset;
        [SerializeField,Min(0),Tooltip("許容する画面上の誤差(pixel)。大きいほど早く粗いLOD段へ切り替わり、描画三角形が減ります。")] private float lodErrorThresholdPixels=2;
        [SerializeField,Range(0,0.95f),Tooltip("細かい段と粗い段の切替閾値に持たせる余裕の割合。0.1で±10%。境界付近での往復を防ぎます。")] private float lodHysteresis=0.1f;
        [SerializeField] private VirtualGeometryDebugView debugView;
        public VirtualGeometryDebugView DebugView=>this.debugView;
        public void SetDebugView(VirtualGeometryDebugView view){this.debugView=view;}
        [SerializeField] private bool enableFrustumCulling=true;
        [SerializeField] private bool enableOcclusionCulling=true;
        public bool EnableFrustumCulling=>this.enableFrustumCulling;
        public bool EnableOcclusionCulling=>this.enableOcclusionCulling;

        /// <summary>M3検証と比較表示でカリングを個別に切り替える。</summary>
        public void SetCullingSettings(bool frustum,bool occlusion){
            this.enableFrustumCulling=frustum;this.enableOcclusionCulling=occlusion;
        }

        [SerializeField,Tooltip("Streaming: 必要なpageだけ読み込みます。AllResident: 全geometryを常駐させます(比較用)。")] private VirtualGeometryStreamingMode streamingMode=VirtualGeometryStreamingMode.Streaming;
        [SerializeField] private VirtualGeometryStreamingSettings streamingSettings=new VirtualGeometryStreamingSettings();
        public VirtualGeometryStreamingStatistics StreamingStatistics=>this.gpuPage?.StreamingController?.Statistics;
        public long DiagnosticGeometryAllocatedBytes{
            get{if(this.gpuPage==null){return 0;}return (long)this.gpuPage.VertexBuffer.count*this.gpuPage.VertexBuffer.stride+(long)this.gpuPage.IndexBuffer.count*this.gpuPage.IndexBuffer.stride;}
        }
        public bool IsStreamingRootReady=>this.gpuPage!=null&&(this.gpuPage.StreamingController==null||this.gpuPage.StreamingController.RootReady);
        public void SetStreamingConfiguration(VirtualGeometryStreamingMode mode,VirtualGeometryStreamingSettings settings){
            this.streamingMode=mode;this.streamingSettings=settings.Clone();this.Reload();
        }
        public void RetryStreamingFailures(){this.gpuPage?.StreamingController?.RetryFailures();}
        private VirtualGeometryGpuPage gpuPage;
        private VirtualGeometryReferenceCache<VirtualGeometryGpuPage>.Lease resourceLease;
        private readonly Dictionary<Camera,VirtualGeometrySelectionState> cameraSelections=new Dictionary<Camera,VirtualGeometrySelectionState>();
        private VirtualGeometrySelectionState lastSelection;
        public string SharedResourceKey=>this.meshAsset==null?string.Empty:VirtualGeometryGpuResourceCache.Key(this.meshAsset,this.streamingMode,this.streamingSettings);
        public static int SharedResourceCount=>VirtualGeometryGpuResourceCache.Count;
        public long DiagnosticMetadataAllocatedBytes=>this.gpuPage?.MetadataAllocatedBytes??0;
        /// <summary>明示的な診断用同期readback。通常の描画/streamingからは呼ばない。</summary>
        public VirtualGeometryInstanceMetrics ReadMetricsForDiagnostics(Camera camera=null){
            var result=new VirtualGeometryInstanceMetrics{GeometryKey=this.meshAsset?.ContentHash,ResourceKey=this.SharedResourceKey,InputTriangles=this.meshAsset?.InputTriangleCount??0,UniqueTriangles=this.meshAsset?.LeafTriangleCount??0,
                GpuGeometryBytes=this.DiagnosticGeometryAllocatedBytes,GpuMetadataBytes=this.DiagnosticMetadataAllocatedBytes,GpuSelectionBytes=this.DiagnosticSelectionAllocatedBytes,CpuCacheBytes=this.StreamingStatistics?.CpuBytes??0};
            var state=camera==null?this.lastSelection:(this.cameraSelections.TryGetValue(camera,out var selected)?selected:null);
            if(state!=null&&!this.IsFallbackVisible){var snapshot=state.Readback();result.SelectedClusters=snapshot.CullingStatistics[0];for(int i=0;i<snapshot.Arguments.Length;i+=4){result.DrawnTriangles+=snapshot.Arguments[i]/3;if(snapshot.Arguments[i]>0){result.DrawCalls++;}}}
            else if(!this.IsFallbackVisible&&this.gpuPage?.ArgumentsBuffer!=null){
                var arguments=new uint[this.gpuPage.ArgumentsBuffer.count];this.gpuPage.ArgumentsBuffer.GetData(arguments);
                result.SelectedClusters=(uint)this.gpuPage.ClusterBuffer.count;
                for(int i=0;i<arguments.Length;i+=4){result.DrawnTriangles+=arguments[i]/3;if(arguments[i]>0){result.DrawCalls++;}}
            }
            else if(this.IsFallbackVisible&&this.meshAsset!=null){result.DrawnTriangles=this.meshAsset.ProxyTriangleCount;result.DrawCalls=this.meshAsset.MaterialCount;}
            return result;
        }
        public long DiagnosticSelectionAllocatedBytes{get{long bytes=0;foreach(var state in this.cameraSelections.Values){bytes+=state.Bytes;}return bytes;}}
        internal VirtualGeometrySelectionState GetSelection(Camera camera){
            Camera key=camera;
            if(!this.cameraSelections.TryGetValue(key,out var state)){
                state=new VirtualGeometrySelectionState(this.gpuPage,camera);this.cameraSelections.Add(key,state);
            }
            state.SetDebug(this.debugView>=VirtualGeometryDebugView.Clusters&&this.debugView<=VirtualGeometryDebugView.ResidentPages);this.lastSelection=state;return state;
        }
        private string lastError;
        private bool reloadPending;

        public VirtualGeometryMeshAsset MeshAsset=>this.meshAsset;
        public float LodErrorThresholdPixels=>this.lodErrorThresholdPixels;
        public float LodHysteresis=>this.lodHysteresis;
        public Bounds WorldBounds{
            get{
                if(this.meshAsset==null){return new Bounds(this.transform.position,Vector3.zero);}
                Bounds local=this.meshAsset.Bounds;
                Vector3 center=this.transform.localToWorldMatrix.MultiplyPoint3x4(local.center);
                Vector3 extents=local.extents;
                Matrix4x4 matrix=this.transform.localToWorldMatrix;
                Vector3 worldExtents=new Vector3(
                    Mathf.Abs(matrix.m00)*extents.x+Mathf.Abs(matrix.m01)*extents.y+Mathf.Abs(matrix.m02)*extents.z,
                    Mathf.Abs(matrix.m10)*extents.x+Mathf.Abs(matrix.m11)*extents.y+Mathf.Abs(matrix.m12)*extents.z,
                    Mathf.Abs(matrix.m20)*extents.x+Mathf.Abs(matrix.m21)*extents.y+Mathf.Abs(matrix.m22)*extents.z);
                return new Bounds(center,worldExtents*2);
            }
        }

        public void RefreshAsset(){if(this.isActiveAndEnabled){this.Reload();}}
        public void SetMeshAsset(VirtualGeometryMeshAsset asset){
            if(this.meshAsset==asset&&this.gpuPage!=null){return;}
            VirtualGeometryMeshAsset previous=this.meshAsset;
            this.meshAsset=asset;
            // 代替Meshを通常MeshFilterへ入れ、影・当たり判定・カリング・代替表示を通常の仕組みで行う。
            MeshFilter filter=this.GetComponent<MeshFilter>();
            if(filter!=null&&asset!=null&&filter.sharedMesh!=asset.ProxyMesh){filter.sharedMesh=asset.ProxyMesh;}
            // 変換データを外したときに、前の代替Meshが通常のMeshとして残って見え続けないようにする。利用者が入れたMeshは触らない。
            if(filter!=null&&asset==null&&previous!=null&&filter.sharedMesh==previous.ProxyMesh){filter.sharedMesh=null;}
            this.EnsureMaterialSlots();this.Reload();
        }

        public void SetLodSettings(float errorThresholdPixels,float hysteresis){
            if(!float.IsFinite(errorThresholdPixels)||errorThresholdPixels<0){throw new ArgumentOutOfRangeException(nameof(errorThresholdPixels));}
            if(!float.IsFinite(hysteresis)||hysteresis<0||hysteresis>=1){throw new ArgumentOutOfRangeException(nameof(hysteresis));}
            this.lodErrorThresholdPixels=errorThresholdPixels;this.lodHysteresis=hysteresis;
        }


        public VirtualGeometryGpuSelectionSnapshot ReadbackSelectionForDiagnostics(){
            if(this.gpuPage==null){throw new InvalidOperationException("Virtual Geometry page is not loaded.");}
            if(this.lastSelection==null){throw new InvalidOperationException("No camera has selected this instance yet.");}
            return this.lastSelection.Readback();
        }

        public VirtualGeometryGpuSelectionSnapshot ReadbackSelectionForDiagnostics(Camera camera){
            if(!this.cameraSelections.TryGetValue(camera,out var state)){throw new InvalidOperationException("This camera has no selection state.");}
            return state.Readback();
        }
        /// <summary>Monitor用。cameraの直近選択を非同期readbackする。未描画ならfalse。</summary>
        internal bool RequestSelectionReadback(Camera camera,Action<VirtualGeometryGpuSelectionSnapshot> completed){
            if(camera==null||!this.cameraSelections.TryGetValue(camera,out var state)){return false;}
            return state.RequestReadback(completed);
        }
        /// <summary>node indexごとの階層Level。0が最詳細leaf。</summary>
        internal uint[] NodeLevels=>this.gpuPage?.NodeLevels;
        public Vector4[] ReadDebugVerticesForDiagnostics(Camera camera){if(!this.cameraSelections.TryGetValue(camera,out var state)){throw new InvalidOperationException("Camera has not rendered.");}var values=new Vector4[state.DebugBuffer.count];state.DebugBuffer.GetData(values);return values;}
        public int DiagnosticCameraStateCount=>this.cameraSelections.Count;
        public bool SharesStreamingResourceWith(VirtualGeometryRenderer other)=>other!=null&&this.gpuPage!=null&&ReferenceEquals(this.gpuPage,other.gpuPage);

        internal bool TryGetGpuPage(out VirtualGeometryGpuPage page){
            if(this.isActiveAndEnabled&&((this.gpuPage==null&&this.lastError==null)||(this.resourceLease!=null&&!this.resourceLease.IsValid))){this.Reload();}
            this.gpuPage?.TickStreaming(Time.frameCount);page=this.gpuPage;return page!=null;
        }

        internal float MaximumWorldScale{
            get{
                Matrix4x4 matrix=this.transform.localToWorldMatrix;
                float x=new Vector3(matrix.m00,matrix.m10,matrix.m20).magnitude;
                float y=new Vector3(matrix.m01,matrix.m11,matrix.m21).magnitude;
                float z=new Vector3(matrix.m02,matrix.m12,matrix.m22).magnitude;
                return Mathf.Max(x,Mathf.Max(y,z));
            }
        }

        private void OnDrawGizmos(){if(this.debugView==VirtualGeometryDebugView.Loading||this.debugView==VirtualGeometryDebugView.Culling){Gizmos.color=this.IsStreamingRootReady?Color.green:Color.yellow;Bounds bounds=this.WorldBounds;Gizmos.DrawWireCube(bounds.center,bounds.size);}}
        private void Update(){
            if(this.reloadPending){this.ApplySerializedChanges();}
            this.gpuPage?.TickStreaming(Time.frameCount);
            List<Camera> expired=null;foreach(var pair in this.cameraSelections){if(pair.Key==null){expired??=new List<Camera>();expired.Add(pair.Key);}}
            if(expired!=null){foreach(Camera camera in expired){var state=this.cameraSelections[camera];if(ReferenceEquals(this.lastSelection,state)){this.lastSelection=null;}state.Dispose();this.cameraSelections.Remove(camera);}}
            List<Camera> expiredDecisions=null;foreach(var pair in this.cameraDecisions){if(pair.Key==null){expiredDecisions??=new List<Camera>();expiredDecisions.Add(pair.Key);}}
            if(expiredDecisions!=null){foreach(Camera camera in expiredDecisions){this.cameraDecisions.Remove(camera);}}
        }
        /// <summary>Domain Reload前と検証の再初期化で、instance/camera資源を先に解放する。</summary>
        public static void ReleaseResourcesForReload(){
            foreach(var renderer in VirtualGeometryRenderRegistry.Renderers){if(renderer!=null){renderer.Release();}}
            VirtualGeometryGpuResourceCache.Reset();VirtualGeometryDiagnostics.Reset();
        }
        // インポート中のモデルやPrefab assetのGameObjectはsceneに属さない。そこではGPU資源・Proxy・camera購読を作らない。
        private bool InScene=>this.gameObject.scene.IsValid();
        // 部品は利用者が触るものではないため、追加された時点でInspectorから隠す。
        private void Reset(){this.hideFlags|=HideFlags.HideInInspector;}
        private void Awake(){this.hideFlags|=HideFlags.HideInInspector;}
        private void OnEnable(){
            if(!this.InScene){return;}
            VirtualGeometryRenderRegistry.Add(this);RenderPipelineManager.beginCameraRendering+=this.BeforeCamera;this.Reload();
        }
        private void OnDisable(){RenderPipelineManager.beginCameraRendering-=this.BeforeCamera;VirtualGeometryRenderRegistry.Remove(this);this.Release();this.DestroyProxy();}

        // OnValidate中のGameObject破棄/生成は禁止されるため、安全なUpdateで再構築する。
        private void OnValidate(){
            this.lodErrorThresholdPixels=Mathf.Max(0,this.lodErrorThresholdPixels);
            this.lodHysteresis=Mathf.Clamp(this.lodHysteresis,0,0.95f);
            this.hideFlags|=HideFlags.HideInInspector;
            this.reloadPending=true;
        }

        /// <summary>
        /// Inspector等のserialized変更を反映する。LOD閾値等の変更だけなら影専用の子のMaterialと有効状態を更新する。
        /// Mesh Assetやstreaming設定が変わったときだけGPU資源とProxyを作り直す。毎回作り直すと、slider操作のたびにProxy表示へ戻り読込もやり直しになる。
        /// </summary>
        private void ApplySerializedChanges(){
            this.reloadPending=false;
            bool loaded=this.gpuPage!=null||this.lastError!=null;
            if(this.isActiveAndEnabled&&loaded&&this.shadowRoot!=null&&this.meshAsset==this.loadedAsset&&this.SharedResourceKey==this.loadedKey){
                this.UpdateShadowCaster();
                return;
            }
            this.Reload();
        }

        private VirtualGeometryMeshAsset loadedAsset;private string loadedKey;

        private void Reload(){
            this.reloadPending=false;
            this.Release();this.lastError=null;this.loadedAsset=null;this.loadedKey=null;
            if(!this.isActiveAndEnabled||!this.InScene){this.DestroyProxy();return;}this.CreateProxy();
            if(this.meshAsset==null){return;}
            this.loadedAsset=this.meshAsset;this.loadedKey=this.SharedResourceKey;
            try{
                this.resourceLease=VirtualGeometryGpuResourceCache.Acquire(this.meshAsset,this.streamingMode,this.streamingSettings);
                this.gpuPage=this.resourceLease.Value;this.lastError=null;
            }catch(Exception error){
                this.lastError=error.Message;
                // 読込拒否はInspector/VG Statsへ表示し、生成済みProxyへ退避する。
            }
        }

        private void Release(){
            foreach(var state in this.cameraSelections.Values){state.Dispose();}this.cameraSelections.Clear();this.lastSelection=null;
            this.resourceLease?.Dispose();this.resourceLease=null;this.gpuPage=null;
        }
    }

    public sealed class VirtualGeometryGpuSelectionSnapshot{
        public uint[] SelectionFlags{get;}
        public uint[] Arguments{get;}
        public uint[] VisibleIndices{get;}
        /// <summary>直近の描画で考慮した数、Frustum棄却数、HZB棄却数、可視数の順。</summary>
        public uint[] CullingStatistics{get;}

        internal VirtualGeometryGpuSelectionSnapshot(uint[] selectionFlags,uint[] arguments,uint[] visibleIndices,uint[] cullingStatistics){
            this.SelectionFlags=selectionFlags;this.Arguments=arguments;this.VisibleIndices=visibleIndices;this.CullingStatistics=cullingStatistics;
        }
    }

    internal static class VirtualGeometryRenderRegistry{
        private static readonly List<VirtualGeometryRenderer> renderers=new List<VirtualGeometryRenderer>();
        public static IReadOnlyList<VirtualGeometryRenderer> Renderers=>renderers;
        public static void Add(VirtualGeometryRenderer renderer){if(renderer!=null&&!renderers.Contains(renderer)){renderers.Add(renderer);}}
        public static void Remove(VirtualGeometryRenderer renderer){renderers.Remove(renderer);}
    }

    internal sealed partial class VirtualGeometryGpuPage:IDisposable{
        public GraphicsBuffer VertexBuffer{get;private set;}
        public GraphicsBuffer IndexBuffer{get;private set;}
        public GraphicsBuffer ClusterBuffer{get;private set;}public GraphicsBuffer LegacyDebugBuffer{get;private set;}
        public GraphicsBuffer NodeBuffer{get;private set;}
        public GraphicsBuffer MaterialRangeBuffer{get;private set;}
        public GraphicsBuffer PageResidencyBuffer{get;private set;}
        public GraphicsBuffer VisibleIndexBuffer{get;private set;}
        public GraphicsBuffer RefinementStateBuffer{get;private set;}
        public GraphicsBuffer SelectionFlagsBuffer{get;private set;}
        public GraphicsBuffer ArgumentsBuffer{get;private set;}
        public GraphicsBuffer CullingStatisticsBuffer{get;private set;}
        public int MaterialCount{get;private set;}
        public int NodeCount{get;private set;}
        public int PageCount{get;private set;}
        public int VisibleIndexCapacity{get;private set;}
        public uint[] NodeLevels{get;private set;}
        public bool HasHierarchy=>this.NodeCount>0;
        public long MetadataAllocatedBytes=>BufferBytes(this.ClusterBuffer)+BufferBytes(this.NodeBuffer)+BufferBytes(this.MaterialRangeBuffer)+BufferBytes(this.PageResidencyBuffer)+BufferBytes(this.PageTableBuffer)+BufferBytes(this.LegacyDebugBuffer);
        private static long BufferBytes(GraphicsBuffer buffer)=>buffer!=null?(long)buffer.count*buffer.stride:0;

        public static VirtualGeometryGpuPage Load(VirtualGeometryMeshAsset asset,VirtualGeometryStreamingMode mode,VirtualGeometryStreamingSettings settings){
            if(asset==null){throw new ArgumentNullException(nameof(asset));}
            string fullPath=VirtualGeometryPagePaths.Resolve(asset.PageRelativePath);
            if(!File.Exists(fullPath)){throw new FileNotFoundException("変換データ(page)が見つかりません。元のモデルを再インポートすると作り直されます。",fullPath);}
            using(var header=new BinaryReader(File.OpenRead(fullPath))){
                header.ReadUInt32();uint version=header.ReadUInt32();
                if(version==VirtualGeometryStreamCodec.Version&&mode==VirtualGeometryStreamingMode.Streaming){return LoadStreaming(fullPath,settings);}
            }
            VirtualGeometryPage page=VirtualGeometryPageCodec.Read(File.ReadAllBytes(fullPath));
            if(page.Indices.Length==0||page.Clusters.Length==0){throw new InvalidDataException("Page has no drawable triangles.");}
            VirtualGeometryGpuPage result=new VirtualGeometryGpuPage();
            try{
                result.MaterialCount=page.MaterialCount;
                result.NodeCount=page.Nodes.Length;result.NodeLevels=ReadLevels(page.Nodes);
                result.VertexBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,page.Vertices.Length,VirtualGeometryPackedVertex.Stride);
                result.IndexBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,page.Indices.Length,sizeof(uint));
                result.ClusterBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,page.Clusters.Length,VirtualGeometryCluster.Stride);
                result.ArgumentsBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Raw|GraphicsBuffer.Target.IndirectArguments,Mathf.Max(1,page.MaterialCount*4),sizeof(uint));
                result.VertexBuffer.SetData(page.Vertices);
                result.IndexBuffer.SetData(page.Indices);
                result.ClusterBuffer.SetData(page.Clusters);
                if(page.Nodes.Length>0){
                    VirtualGeometryMaterialRange[] ranges=BuildMaterialRanges(page,out int visibleCapacity);
                    result.VisibleIndexCapacity=visibleCapacity;
                    result.PageCount=1;
                    foreach(var node in page.Nodes){result.PageCount=Math.Max(result.PageCount,checked((int)node.PageId+1));}
                    result.NodeBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,page.Nodes.Length,VirtualGeometryHierarchyNode.Stride);
                    result.MaterialRangeBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,Mathf.Max(1,ranges.Length),VirtualGeometryMaterialRange.Stride);
                    result.PageResidencyBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,result.PageCount,sizeof(uint));
                    result.NodeBuffer.SetData(page.Nodes);
                    result.MaterialRangeBuffer.SetData(ranges);
                    uint[] residentPages=new uint[result.PageCount];for(int i=0;i<residentPages.Length;i++){residentPages[i]=1;}
                    result.PageResidencyBuffer.SetData(residentPages);
                    result.PageTableBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,result.PageCount,16);
                    result.PageTableBuffer.SetData(new VirtualGeometryPhysicalPage[result.PageCount]);
                    result.ArgumentsBuffer.Dispose();result.ArgumentsBuffer=null;
                }else{
                    result.LegacyDebugBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,1,16);result.ArgumentsBuffer.SetData(BuildLegacyArguments(page));
                }
                return result;
            }catch{
                result.Dispose();throw;
            }
        }

        private static uint[] ReadLevels(VirtualGeometryHierarchyNode[] nodes){
            uint[] levels=new uint[nodes.Length];for(int i=0;i<nodes.Length;i++){levels[i]=nodes[i].Level;}return levels;
        }

        private static VirtualGeometryMaterialRange[] BuildMaterialRanges(VirtualGeometryPage page,out int visibleCapacity){
            VirtualGeometryMaterialRange[] ranges=new VirtualGeometryMaterialRange[page.MaterialCount];
            uint outputStart=0;
            for(int material=0;material<page.MaterialCount;material++){
                uint capacity=0;
                for(int nodeIndex=0;nodeIndex<page.Nodes.Length;nodeIndex++){
                    VirtualGeometryHierarchyNode node=page.Nodes[nodeIndex];
                    if(node.MaterialSlot!=material||node.ChildCount!=0){continue;}
                    for(uint clusterOffset=0;clusterOffset<node.ClusterCount;clusterOffset++){
                        capacity=checked(capacity+page.Clusters[node.FirstCluster+clusterOffset].IndexCount);
                    }
                }
                uint root=page.RootNodeIndices[material];
                if((root==uint.MaxValue)!=(capacity==0)){throw new InvalidDataException("Hierarchy root and leaf capacity disagree.");}
                ranges[material]=new VirtualGeometryMaterialRange{OutputStart=outputStart,OutputCapacity=capacity,RootNode=root};
                outputStart=checked(outputStart+capacity);
            }
            for(int nodeIndex=0;nodeIndex<page.Nodes.Length;nodeIndex++){
                VirtualGeometryHierarchyNode node=page.Nodes[nodeIndex];
                uint representationCount=0;
                for(uint clusterOffset=0;clusterOffset<node.ClusterCount;clusterOffset++){
                    representationCount=checked(representationCount+page.Clusters[node.FirstCluster+clusterOffset].IndexCount);
                }
                if(representationCount>ranges[node.MaterialSlot].OutputCapacity){throw new InvalidDataException("Hierarchy representation exceeds its material output capacity.");}
            }
            visibleCapacity=checked((int)outputStart);
            return ranges;
        }

        private static uint[] BuildInitialArguments(VirtualGeometryMaterialRange[] ranges){
            uint[] arguments=new uint[Mathf.Max(1,ranges.Length*4)];
            for(int material=0;material<ranges.Length;material++){
                int offset=material*4;
                arguments[offset]=0;arguments[offset+1]=1;arguments[offset+2]=ranges[material].OutputStart;arguments[offset+3]=0;
            }
            return arguments;
        }

        private static uint[] BuildLegacyArguments(VirtualGeometryPage page){
            uint[] arguments=new uint[Mathf.Max(1,page.MaterialCount*4)];
            uint[] starts=new uint[page.MaterialCount];uint[] ends=new uint[page.MaterialCount];bool[] seen=new bool[page.MaterialCount];
            foreach(VirtualGeometryCluster cluster in page.Clusters){
                int material=(int)cluster.MaterialSlot;
                if(!seen[material]){starts[material]=cluster.FirstIndex;ends[material]=cluster.FirstIndex+cluster.IndexCount;seen[material]=true;}
                else{
                    if(cluster.FirstIndex!=ends[material]){throw new InvalidDataException("Material cluster ranges are not contiguous.");}
                    ends[material]+=cluster.IndexCount;
                }
            }
            for(int material=0;material<page.MaterialCount;material++){
                int offset=material*4;
                arguments[offset]=seen[material]?ends[material]-starts[material]:0;arguments[offset+1]=1;
                arguments[offset+2]=seen[material]?starts[material]:0;arguments[offset+3]=0;
            }
            return arguments;
        }

        public VirtualGeometryGpuSelectionSnapshot ReadbackSelection(){
            if(!this.HasHierarchy){throw new InvalidOperationException("This page has no GPU hierarchy selection state.");}
            uint[] flags=new uint[this.NodeCount];
            uint[] arguments=new uint[Mathf.Max(1,this.MaterialCount*4)];
            uint[] visible=new uint[Mathf.Max(1,this.VisibleIndexCapacity)];
            this.SelectionFlagsBuffer.GetData(flags);this.ArgumentsBuffer.GetData(arguments);this.VisibleIndexBuffer.GetData(visible);
            uint[] statistics=new uint[4];this.CullingStatisticsBuffer.GetData(statistics);
            return new VirtualGeometryGpuSelectionSnapshot(flags,arguments,visible,statistics);
        }

        public void Dispose(){
            this.DisposeStreaming();
            this.VertexBuffer?.Dispose();this.VertexBuffer=null;
            this.IndexBuffer?.Dispose();this.IndexBuffer=null;
            this.LegacyDebugBuffer?.Dispose();this.LegacyDebugBuffer=null;this.ClusterBuffer?.Dispose();this.ClusterBuffer=null;
            this.NodeBuffer?.Dispose();this.NodeBuffer=null;
            this.MaterialRangeBuffer?.Dispose();this.MaterialRangeBuffer=null;
            this.PageResidencyBuffer?.Dispose();this.PageResidencyBuffer=null;
            this.VisibleIndexBuffer?.Dispose();this.VisibleIndexBuffer=null;
            this.RefinementStateBuffer?.Dispose();this.RefinementStateBuffer=null;
            this.SelectionFlagsBuffer?.Dispose();this.SelectionFlagsBuffer=null;
            this.ArgumentsBuffer?.Dispose();this.ArgumentsBuffer=null;
            this.CullingStatisticsBuffer?.Dispose();this.CullingStatisticsBuffer=null;
        }
    }
}
