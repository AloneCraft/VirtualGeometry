using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;

namespace VirtualizedGeometry{
    /// <summary>GPU階層選択とvertex pulling間接描画をURP RenderGraphへ接続する。</summary>
    public sealed class VirtualGeometryRenderFeature:ScriptableRendererFeature{
        [SerializeField] private ComputeShader selectionShader;
        [SerializeField] private Shader surfaceShader;
        [SerializeField] private Shader depthCopyShader;
        private CopyDepthPass depthCopy;
        private Material material;
        private DrawPass pass;
        private UniversalRendererData ownerData;
        // Profiler / 計測用。VGのC#側の区間(部品ごとの判定、RenderGraphの記録)を分けて測る。
        private static readonly ProfilerMarker PreCullMarker=new ProfilerMarker("VG.OnCameraPreCull");
        private static readonly ProfilerMarker RecordMarker=new ProfilerMarker("VG.RecordRenderGraph");
        private static readonly ProfilerMarker RecordGateMarker=new ProfilerMarker("VG.Record.Gate");
        private static readonly ProfilerMarker RecordSelectMarker=new ProfilerMarker("VG.Record.Select");
        private static readonly ProfilerMarker RecordDrawMarker=new ProfilerMarker("VG.Record.Draw");
        private static readonly ProfilerMarker RecordLightsMarker=new ProfilerMarker("VG.Record.Lights");
        private static readonly ProfilerMarker RecordFeedbackMarker=new ProfilerMarker("VG.Record.Feedback");

        public override void Create(){
            this.ownerData=null;
            foreach(var data in Resources.FindObjectsOfTypeAll<UniversalRendererData>()){if(data.rendererFeatures.Contains(this)){this.ownerData=data;break;}}
            CoreUtils.Destroy(this.material);this.depthCopy?.Dispose();
            this.material=null;this.depthCopy=null;this.pass=null;
            if(this.surfaceShader==null){return;}
            this.material=CoreUtils.CreateEngineMaterial(this.surfaceShader);
            if(this.depthCopyShader==null){this.depthCopyShader=Shader.Find("Hidden/Universal Render Pipeline/CopyDepth");}
            this.depthCopy?.Dispose();
            this.depthCopy=this.depthCopyShader!=null?new CopyDepthPass(RenderPassEvent.AfterRenderingOpaques,this.depthCopyShader):null;
            this.pass=new DrawPass(this.selectionShader,this.material,this.depthCopy);
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer,in CameraData cameraData){
            using(PreCullMarker.Auto()){this.PreCull(cameraData);}
        }
        private void PreCull(in CameraData cameraData){
            string reason=GetUnsupportedReason(cameraData);
            if(reason==null&&(this.pass==null||this.material==null||this.depthCopy==null||this.selectionShader==null)){reason="専用描画のshader/compute/depth-copy資源が設定されていません。";}
            if(reason==null&&this.ownerData==null){reason="Renderer設定を確認できません。";}
            if(reason==null&&this.ownerData.renderingMode!=RenderingMode.Forward&&this.ownerData.renderingMode!=RenderingMode.ForwardPlus){reason="専用描画はURPのForward / Forward+に対応します(Deferredは非対応)。";}
            if(reason==null&&cameraData.renderType==CameraRenderType.Base&&cameraData.camera.TryGetComponent<UniversalAdditionalCameraData>(out var additional)&&additional.cameraStack!=null&&additional.cameraStack.Count>0){reason="Camera StackのBase cameraは通常Proxyを使用します。";}
            if(reason==null&&this.ownerData.depthPrimingMode!=DepthPrimingMode.Disabled){reason="Depth Primingでは通常Proxyへ切り替えます。";}
            if(reason==null&&this.ownerData.copyDepthMode==CopyDepthMode.ForcePrepass){reason="Depth Prepassでは通常Proxyへ切り替えます。";}
            if(reason==null){foreach(var feature in this.ownerData.rendererFeatures){
                if(feature==null||!feature.isActive||feature==this){continue;}
                if(!(feature is IVirtualGeometryCompatibleRenderFeature compatible)||compatible.RequiresEarlyGeometry){reason="先行depth/normalを必要とする、または互換未確認のRendererFeature: "+feature.GetType().Name;break;}
            }}
            foreach(var instance in VirtualGeometryRenderRegistry.Renderers){if(instance!=null&&instance.isActiveAndEnabled){instance.SetCameraSupport(cameraData.camera,reason);}}
            foreach(var source in VirtualGeometryClusterSourceRegistry.Sources){if(source!=null&&source.isActiveAndEnabled){source.SetCameraSupport(cameraData.camera,reason);}}
        }
        public override void AddRenderPasses(ScriptableRenderer renderer,ref RenderingData renderingData){
            if(this.pass!=null&&(VirtualGeometryRenderRegistry.Renderers.Count>0||VirtualGeometryClusterSourceRegistry.Sources.Count>0)){this.pass.Setup(renderingData.cameraData.camera);renderer.EnqueuePass(this.pass);}
        }
        private static string GetUnsupportedReason(CameraData camera){
            if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D12){return "v0.1の専用描画はWindows DX12を対象とします。";}
            if(!SystemInfo.supportsComputeShaders||!SystemInfo.supportsIndirectArgumentsBuffer||SystemInfo.graphicsShaderLevel<50){
                return "Compute shaders, indirect arguments, or Shader Model 5 are unavailable.";
            }
            if(camera.cameraType!=CameraType.Game&&camera.cameraType!=CameraType.SceneView){return "Game/Scene View以外のcameraは通常Proxyを使用します。";}
            if(camera.renderType!=CameraRenderType.Base||camera.xr.enabled){return "Camera Stack and XR are outside the current scope.";}
            if(camera.cameraTargetDescriptor.msaaSamples>1){return "MSAA is outside the current scope.";}
            return null;
        }

        protected override void Dispose(bool disposing){
            CoreUtils.Destroy(this.material);this.depthCopy?.Dispose();this.depthCopy=null;this.material=null;this.pass=null;
        }

        private sealed class DrawPass:ScriptableRenderPass{
            private static readonly int VerticesId=Shader.PropertyToID("_VGVertices");
            private static readonly int IndicesId=Shader.PropertyToID("_VGIndices");
            private static readonly int SourceIndicesId=Shader.PropertyToID("_VGSourceIndices");
            private static readonly int ClustersId=Shader.PropertyToID("_VGClusters");
            private static readonly int NodesId=Shader.PropertyToID("_VGNodes");
            private static readonly int MaterialRangesId=Shader.PropertyToID("_VGMaterialRanges");
            private static readonly int PageResidencyId=Shader.PropertyToID("_VGPageResidency");
            private static readonly int VisibleIndicesId=Shader.PropertyToID("_VGVisibleIndices");
            private static readonly int RefinementStateId=Shader.PropertyToID("_VGRefinementState");
            private static readonly int SelectionFlagsId=Shader.PropertyToID("_VGSelectionFlags");
            private static readonly int ArgumentsId=Shader.PropertyToID("_VGArguments");
            private static readonly int ObjectToWorldId=Shader.PropertyToID("_VGObjectToWorld");
            private static readonly int BaseColorId=Shader.PropertyToID("_VGBaseColor");
            private static readonly int CameraPositionId=Shader.PropertyToID("_VGCameraPosition");
            private static readonly int MaximumScaleId=Shader.PropertyToID("_VGMaximumScale");
            private static readonly int ProjectionScaleId=Shader.PropertyToID("_VGProjectionScale");
            private static readonly int NearPlaneId=Shader.PropertyToID("_VGNearPlane");
            private static readonly int TargetErrorPixelsId=Shader.PropertyToID("_VGTargetErrorPixels");
            private static readonly int HysteresisId=Shader.PropertyToID("_VGHysteresis");
            private static readonly int OrthographicId=Shader.PropertyToID("_VGOrthographic");
            private static readonly int MaterialCountId=Shader.PropertyToID("_VGMaterialCount");
            private static readonly int NodeCountId=Shader.PropertyToID("_VGNodeCount");
            private static readonly int PageCountId=Shader.PropertyToID("_VGPageCount");

            private static readonly int StreamingId=Shader.PropertyToID("_VGStreaming");
            private static readonly int PageTableId=Shader.PropertyToID("_VGPageTable");
            private static readonly int RequestsId=Shader.PropertyToID("_VGRequests");
            private static readonly int RequestCapacityId=Shader.PropertyToID("_VGRequestCapacity");
            private static readonly int CullingStatisticsId=Shader.PropertyToID("_VGCullingStatistics");
            private static readonly int HzbId=Shader.PropertyToID("_VGHzb");
            private static readonly int HzbSourceId=Shader.PropertyToID("_VGHzbSource");
            private static readonly int HzbDestinationId=Shader.PropertyToID("_VGHzbDestination");
            private static readonly int DepthSourceId=Shader.PropertyToID("_VGDepthSource");
            private static readonly int ScreenSizeId=Shader.PropertyToID("_VGScreenSize");
            private static readonly int HzbSizeId=Shader.PropertyToID("_VGHzbSize");
            private static readonly int SourceMipId=Shader.PropertyToID("_VGSourceMip");
            private static readonly int DestinationMipId=Shader.PropertyToID("_VGDestinationMip");
            private static readonly int ReversedZId=Shader.PropertyToID("_VGReversedZ");
            private static readonly int FrustumEnabledId=Shader.PropertyToID("_VGFrustumEnabled");
            private static readonly int OcclusionEnabledId=Shader.PropertyToID("_VGOcclusionEnabled");
            private static readonly int DepthBiasId=Shader.PropertyToID("_VGDepthBias");
            private static readonly int TextureYSignId=Shader.PropertyToID("_VGTextureYSign");
            private static readonly int ViewProjectionId=Shader.PropertyToID("_VGViewProjection");
            private static readonly int FrustumPlanesId=Shader.PropertyToID("_VGFrustumPlanes");
            private readonly int copyDepthKernel;
            private readonly int downsampleKernel;
            private readonly ComputeShader selectionShader;
            // debug view(Clusters/LOD/Triangles等)専用のVG Unlit。通常の見た目は利用者MaterialのVG passで描く。
            private readonly Material material;private readonly CopyDepthPass depthCopy;
            private readonly int resetKernel;
            private readonly int selectKernel;
            private Camera camera;
            private bool reportedMissingSelector;

            private sealed class HzbPassData{
                public ComputeShader Shader;
                public int CopyKernel;
                public int DownsampleKernel;
                public TextureHandle Depth;
                public TextureHandle Hzb;
                public Vector4 ScreenSize;
                public Vector4 HzbSize;
                public int ReversedZ;
            }

            private sealed class SelectionPassData{
                public VirtualGeometryGpuPage Page;
                public BufferHandle PageTable;
                public BufferHandle Requests;
                public int Streaming;
                public int RequestCapacity;
                public BufferHandle CullingStatistics;public BufferHandle Debug;public int DebugMode;

                public TextureHandle Hzb;
                public Vector4 ScreenSize;
                public Vector4 HzbSize;
                public Matrix4x4 ViewProjection;
                public Vector4[] FrustumPlanes;
                public int FrustumEnabled;
                public int OcclusionEnabled;
                public int ReversedZ;
                public float TextureYSign;
                public ComputeShader Shader;
                public int ResetKernel;
                public int SelectKernel;
                public BufferHandle SourceIndices;
                public BufferHandle Clusters;
                public BufferHandle Nodes;
                public BufferHandle MaterialRanges;
                public BufferHandle PageResidency;
                public BufferHandle VisibleIndices;
                public BufferHandle RefinementState;
                public BufferHandle SelectionFlags;
                public BufferHandle Arguments;
                public Matrix4x4 ObjectToWorld;
                public Vector3 CameraPosition;
                public float MaximumScale;
                public float ProjectionScale;
                public float NearPlane;
                public float TargetErrorPixels;
                public float Hysteresis;
                public int Orthographic;
                public int MaterialCount;
                public int NodeCount;
                public int PageCount;
            }

            private sealed class FeedbackPassData{
                public VirtualGeometryGpuPage Page;
                public VirtualGeometrySelectionState Selection;
            }
            private sealed class ClusterCullPassData{
                public ComputeShader Shader;public int ResetKernel;public int CullKernel;
                public VirtualGeometryClusterCameraState State;public int CandidateCount;public int DrawCount;
                public BufferHandle Candidates;public BufferHandle DrawRecords;public BufferHandle Bounds;public BufferHandle TemplateRanges;public BufferHandle TemplateIndices;
                public BufferHandle VisibleIndices;public BufferHandle Arguments;public BufferHandle Statistics;
                public int SlotVertexCount;public int SlotClusterCount;
                public TextureHandle Hzb;public Vector4 ScreenSize;public Vector4 HzbSize;public Matrix4x4 ViewProjection;public Vector4[] FrustumPlanes;
                public int FrustumEnabled;public int OcclusionEnabled;public int ReversedZ;public float TextureYSign;
                public Vector3 CameraPosition;public float NearPlane;
            }
            private sealed class DrawData{
                // 同じbufferを複数の項目で使うとき、passに1回だけ宣言するための所有者(ビルド済みはpage、実行時生成は供給元×cameraの状態)。
                public object Owner;
                public VirtualGeometryGpuPage Page;public VirtualGeometrySelectionState Selection;
                public Material Material;public int Pass;
                public BufferHandle Vertices;
                public BufferHandle Indices;
                public BufferHandle Arguments;
                public Matrix4x4 ObjectToWorld;public bool InvertCulling;
                // draw 専用の値(Omni の物体ごとの値など)。null なら共有の MaterialPropertyBlock を使う。
                public MaterialPropertyBlock Properties;
                public PerObjectLighting Lighting;public BufferHandle Debug;public int DebugMode;public float TargetError;public int Loading;
                public int ArgumentsOffset;
            }

            /// <summary>
            /// procedural描画にはUnityのper-object light list・light probeが付かないため、URPのshaderが読むper-draw値を明示して渡す。
            /// 環境光はRenderSettingsのSH、Forwardの追加lightはvisible light順から主光源を除いたURPの追加light番号(最大8個)。Forward+はclusterから読むので不要。
            /// </summary>
            private sealed class PerObjectLighting{public Vector4[] Ambient;public Vector4 LightData;public Vector4 PackedLightIndices;}
            private static readonly int[] AmbientIds={Shader.PropertyToID("unity_SHAr"),Shader.PropertyToID("unity_SHAg"),Shader.PropertyToID("unity_SHAb"),
                Shader.PropertyToID("unity_SHBr"),Shader.PropertyToID("unity_SHBg"),Shader.PropertyToID("unity_SHBb"),Shader.PropertyToID("unity_SHC")};
            private static readonly int LightDataId=Shader.PropertyToID("unity_LightData");
            private static readonly int PackedLightIndicesId=Shader.PropertyToID("unity_PackedLightIndices");
            private static readonly int WorldTransformParamsId=Shader.PropertyToID("unity_WorldTransformParams");
            // 環境反射。通常Rendererは描画ごとにUnityが設定するが、procedural描画には設定されず黒になって暗く見える。既定(skybox等)のcubemapを渡す。
            private static readonly int SpecCubeId=Shader.PropertyToID("unity_SpecCube0");
            private static readonly int SpecCubeHdrId=Shader.PropertyToID("unity_SpecCube0_HDR");
            // 部品×cameraごとにpassを作ると、RenderGraphの記録・compile・実行が部品数に比例して重くなるため、
            // Select・Draw・feedbackはそれぞれcameraにつき1 passへまとめ、中の項目(部品×material slot)を1つずつ処理する。
            // 項目は使い回す。graphの実行は同じcameraのRecordの直後で、次のcameraのRecordより前に終わる。
            // 1 passにまとめる項目数。1にすると部品ごとにpassができ、RenderGraphの記録・実行が部品数に比例して重い。
            // 全部を1 passにすると逆に遅い(passが巨大になり、GPUでも遅くなる)。space_elevator(419部品)の実測で、8〜16が最小だった(静止67 ms、移動中96 ms。1では124 ms、全部では162 ms)。
            private const int PassBatchSize=12;
            private sealed class SelectionBatch{public List<SelectionPassData> Items;public int Start,End;}
            private sealed class DrawBatch{public List<DrawData> Items;public int Start,End;}
            private sealed class FeedbackBatch{public List<FeedbackPassData> Items;public int Start,End;}
            private readonly List<SelectionPassData> selectItems=new List<SelectionPassData>();private int selectCount;
            private readonly List<DrawData> drawItems=new List<DrawData>();private int drawCount;
            private readonly List<FeedbackPassData> feedbackItems=new List<FeedbackPassData>();private int feedbackCount;
            // 同じpageのbufferは複数instanceが共有するため、passごとに1回だけ宣言する。BufferHandleはIEquatableを持たず、HashSetのキーにするとbox化で遅いので参照で数える。
            private readonly HashSet<VirtualGeometryGpuPage> declaredPages=new HashSet<VirtualGeometryGpuPage>();
            private readonly HashSet<object> declaredOwners=new HashSet<object>();
            private readonly HashSet<VirtualGeometrySelectionState> declaredSelections=new HashSet<VirtualGeometrySelectionState>();
            private readonly Dictionary<int,PerObjectLighting> lightingByLayer=new Dictionary<int,PerObjectLighting>();
            private readonly Vector4[] planeVectors=new Vector4[6];

            // 描く対象か。無効・layer外・page未準備・専用描画を使わないcamera・視錐台の外のinstanceは、選択・描画・feedbackのpassを作らない。
            // GPUのcullingでも視錐台の外は全clusterが棄却されるが、部品ごとの記録・実行がframeの大半を占める。
            private bool PassesGate(VirtualGeometryRenderer instance,Plane[] planes,out VirtualGeometryGpuPage page){
                page=null;
                if(instance==null||!instance.isActiveAndEnabled||(this.camera.cullingMask&(1<<instance.gameObject.layer))==0||!instance.TryGetGpuPage(out page)){return false;}
                if(!instance.CanDrawVirtual(this.camera)){return false;}
                if(instance.EnableFrustumCulling&&!GeometryUtility.TestPlanesAABB(planes,instance.WorldBounds)){return false;}
                return true;
            }
            private SelectionPassData AcquireSelect(){
                if(this.selectCount==this.selectItems.Count){this.selectItems.Add(new SelectionPassData());}
                return this.selectItems[this.selectCount++];
            }
            private DrawData AcquireDraw(){
                if(this.drawCount==this.drawItems.Count){this.drawItems.Add(new DrawData());}
                return this.drawItems[this.drawCount++];
            }
            private FeedbackPassData AcquireFeedback(){
                if(this.feedbackCount==this.feedbackItems.Count){this.feedbackItems.Add(new FeedbackPassData());}
                return this.feedbackItems[this.feedbackCount++];
            }

            private static void ExecuteSelect(SelectionPassData input,ComputeGraphContext context){
                ComputeShader shader=input.Shader;
                context.cmd.SetComputeIntParam(shader,"_VGDebugMode",input.DebugMode);context.cmd.SetComputeBufferParam(shader,input.SelectKernel,"_VGVisibleDebug",input.Debug);
                context.cmd.SetComputeIntParam(shader,StreamingId,input.Streaming);
                context.cmd.SetComputeIntParam(shader,RequestCapacityId,input.RequestCapacity);
                context.cmd.SetComputeBufferParam(shader,input.ResetKernel,RequestsId,input.Requests);
                context.cmd.SetComputeBufferParam(shader,input.SelectKernel,RequestsId,input.Requests);
                context.cmd.SetComputeBufferParam(shader,input.SelectKernel,PageTableId,input.PageTable);

                context.cmd.SetComputeBufferParam(shader,input.ResetKernel,CullingStatisticsId,input.CullingStatistics);
                context.cmd.SetComputeBufferParam(shader,input.SelectKernel,CullingStatisticsId,input.CullingStatistics);
                context.cmd.SetComputeTextureParam(shader,input.SelectKernel,HzbId,input.Hzb);
                context.cmd.SetComputeVectorParam(shader,ScreenSizeId,input.ScreenSize);
                context.cmd.SetComputeVectorParam(shader,HzbSizeId,input.HzbSize);
                context.cmd.SetComputeMatrixParam(shader,ViewProjectionId,input.ViewProjection);
                context.cmd.SetComputeVectorArrayParam(shader,FrustumPlanesId,input.FrustumPlanes);
                context.cmd.SetComputeIntParam(shader,FrustumEnabledId,input.FrustumEnabled);
                context.cmd.SetComputeIntParam(shader,OcclusionEnabledId,input.OcclusionEnabled);
                context.cmd.SetComputeIntParam(shader,ReversedZId,input.ReversedZ);
                context.cmd.SetComputeFloatParam(shader,DepthBiasId,0.00001f);
                context.cmd.SetComputeFloatParam(shader,TextureYSignId,input.TextureYSign);
                context.cmd.SetComputeIntParam(shader,MaterialCountId,input.MaterialCount);
                context.cmd.SetComputeIntParam(shader,NodeCountId,input.NodeCount);
                context.cmd.SetComputeBufferParam(shader,input.ResetKernel,MaterialRangesId,input.MaterialRanges);
                context.cmd.SetComputeBufferParam(shader,input.ResetKernel,SelectionFlagsId,input.SelectionFlags);
                context.cmd.SetComputeBufferParam(shader,input.ResetKernel,ArgumentsId,input.Arguments);
                context.cmd.SetComputeIntParam(shader,PageCountId,input.PageCount);
                int resetGroups=(Mathf.Max(input.MaterialCount,Mathf.Max(input.NodeCount,input.PageCount))+63)/64;
                context.cmd.DispatchCompute(shader,input.ResetKernel,Mathf.Max(1,resetGroups),1,1);

                context.cmd.SetComputeBufferParam(shader,input.SelectKernel,SourceIndicesId,input.SourceIndices);
                context.cmd.SetComputeBufferParam(shader,input.SelectKernel,ClustersId,input.Clusters);
                context.cmd.SetComputeBufferParam(shader,input.SelectKernel,NodesId,input.Nodes);
                context.cmd.SetComputeBufferParam(shader,input.SelectKernel,MaterialRangesId,input.MaterialRanges);
                context.cmd.SetComputeBufferParam(shader,input.SelectKernel,PageResidencyId,input.PageResidency);
                context.cmd.SetComputeBufferParam(shader,input.SelectKernel,VisibleIndicesId,input.VisibleIndices);
                context.cmd.SetComputeBufferParam(shader,input.SelectKernel,RefinementStateId,input.RefinementState);
                context.cmd.SetComputeBufferParam(shader,input.SelectKernel,SelectionFlagsId,input.SelectionFlags);
                context.cmd.SetComputeBufferParam(shader,input.SelectKernel,ArgumentsId,input.Arguments);
                context.cmd.SetComputeMatrixParam(shader,ObjectToWorldId,input.ObjectToWorld);
                context.cmd.SetComputeVectorParam(shader,CameraPositionId,input.CameraPosition);
                context.cmd.SetComputeFloatParam(shader,MaximumScaleId,input.MaximumScale);
                context.cmd.SetComputeFloatParam(shader,ProjectionScaleId,input.ProjectionScale);
                context.cmd.SetComputeFloatParam(shader,NearPlaneId,input.NearPlane);
                context.cmd.SetComputeFloatParam(shader,TargetErrorPixelsId,input.TargetErrorPixels);
                context.cmd.SetComputeFloatParam(shader,HysteresisId,input.Hysteresis);
                context.cmd.SetComputeIntParam(shader,OrthographicId,input.Orthographic);
                int selectGroups=input.MaterialCount;
                context.cmd.DispatchCompute(shader,input.SelectKernel,Mathf.Max(1,selectGroups),1,1);
            }

            // properties: 項目ごとに値が変わるbuffer類を毎回上書きする。applyShared: 光源・環境光・反射・巻き順など、同じ値の間は再設定が要らない値を今回設定するか。
            private static void ExecuteDraw(DrawData input,RasterGraphContext context,MaterialPropertyBlock properties,bool applyShared){
                properties.SetBuffer(VerticesId,(GraphicsBuffer)input.Vertices);
                properties.SetBuffer(IndicesId,(GraphicsBuffer)input.Indices);
                properties.SetBuffer("_VGDrawArguments",(GraphicsBuffer)input.Arguments);properties.SetInt("_VGDrawCommand",input.ArgumentsOffset/16);
                Matrix4x4 matrix=input.ObjectToWorld;
                if(input.DebugMode!=0){
                    // debug用VG Unlitは_VGObjectToWorldで自前に変換する。
                    matrix=Matrix4x4.identity;
                    properties.SetMatrix(ObjectToWorldId,input.ObjectToWorld);properties.SetColor(BaseColorId,Color.white);
                    properties.SetInt("_VGDebugMode",input.DebugMode);properties.SetFloat("_VGDebugTargetError",input.TargetError);properties.SetFloat("_VGLoadingCount",input.Loading);
                    if(input.Debug.IsValid()){properties.SetBuffer("_VGVisibleDebug",(GraphicsBuffer)input.Debug);}
                }else if(applyShared){
                    for(int i=0;i<AmbientIds.Length;i++){properties.SetVector(AmbientIds[i],input.Lighting.Ambient[i]);}
                    properties.SetVector(LightDataId,input.Lighting.LightData);properties.SetVector(PackedLightIndicesId,input.Lighting.PackedLightIndices);
                    float handedness=1;if(input.InvertCulling){handedness=-1;}
                    properties.SetVector(WorldTransformParamsId,new Vector4(0,0,0,handedness));
                    if(ReflectionProbe.defaultTexture!=null){
                        properties.SetTexture(SpecCubeId,ReflectionProbe.defaultTexture);properties.SetVector(SpecCubeHdrId,ReflectionProbe.defaultTextureHDRDecodeValues);
                    }
                }
                // procedural描画は負のscaleで裏表が反転しても自動で反転されないため、通常Rendererと同じ面を残すよう明示する。
                context.cmd.SetInvertCulling(input.InvertCulling&&input.DebugMode==0);
                context.cmd.DrawProceduralIndirect(matrix,input.Material,input.Pass,MeshTopology.Triangles,(GraphicsBuffer)input.Arguments,input.ArgumentsOffset,properties);
                context.cmd.SetInvertCulling(false);
            }

            public DrawPass(ComputeShader selectionShader,Material material,CopyDepthPass depthCopy){
                this.depthCopy=depthCopy;
                this.selectionShader=selectionShader;this.material=material;
                if(selectionShader!=null){
                    this.resetKernel=selectionShader.FindKernel("Reset");
                    this.selectKernel=selectionShader.FindKernel("Select");
                    this.copyDepthKernel=selectionShader.FindKernel("CopyDepth");
                    this.downsampleKernel=selectionShader.FindKernel("Downsample");
                }else{
                    this.resetKernel=-1;this.selectKernel=-1;
                }
                this.renderPassEvent=RenderPassEvent.AfterRenderingOpaques;
                // HZB samplingとVG後のdepth copyにはdescriptor付きの中間depthが必要。
                this.ConfigureInput(ScriptableRenderPassInput.Depth);this.requiresIntermediateTexture=true;
                // URPはイベント区間で注入するため、AfterRenderingOpaques-1は通常opaqueの前になる。
            }

            private static PerObjectLighting CollectLights(UniversalLightData data,int layer){
                var result=new PerObjectLighting();var ambient=new SHCoefficients(RenderSettings.ambientProbe);
                result.Ambient=new[]{ambient.SHAr,ambient.SHAg,ambient.SHAb,ambient.SHBr,ambient.SHBg,ambient.SHBb,ambient.SHC};
                var indices=new uint[8];int count=0;float mainIncluded=0;
                for(int lightIndex=0;lightIndex<data.visibleLights.Length;lightIndex++){
                    VisibleLight light=data.visibleLights[lightIndex];if(light.light!=null&&(light.light.cullingMask&(1<<layer))==0){continue;}
                    if(lightIndex==data.mainLightIndex){mainIncluded=1;continue;}
                    if(count>=indices.Length||data.additionalLightsCount==0){continue;}
                    int additional=lightIndex;
                    if(data.mainLightIndex>=0&&lightIndex>data.mainLightIndex){additional--;}
                    indices[count++]=(uint)additional;
                }
                result.LightData=new Vector4(0,count,mainIncluded,0);
                result.PackedLightIndices=new Vector4(Pack(indices[0],indices[1]),Pack(indices[2],indices[3]),Pack(indices[4],indices[5]),Pack(indices[6],indices[7]));
                return result;
            }
            // URPのUBO経路は16bitの追加light番号を2個ずつfloatのbit列へ詰める(RealtimeLights.hlsl GetPerObjectLightIndex)。
            private static float Pack(uint low,uint high){return System.BitConverter.Int32BitsToSingle((int)((low&0xFFFF)|((high&0xFFFF)<<16)));}
            public void Setup(Camera camera){this.camera=camera;}

            // 実行時生成clusterの供給元ごとに、候補slotのcull passを出し、drawごとの描画項目を足す。供給元はcameraあたり数個なのでpassはまとめない。
            // cull は供給元が決めた空間(既定は world、Omni ではカメラ相対の物理空間)で行う。
            private void RecordClusterSources(RenderGraph graph,TextureHandle hzb,Vector4 screenSize,Vector4 hzbSize,Matrix4x4 viewProjection,Plane[] planes,
                float projectionScale,float nearPlane,int reversedZ,float textureYSign,Vector3 cameraPosition,UniversalLightData lightData){
                IReadOnlyList<VirtualGeometryClusterSource> sources=VirtualGeometryClusterSourceRegistry.Sources;
                if(sources.Count==0){return;}
                ComputeShader shader=VirtualGeometryClusterShaders.Cull;
                if(shader==null||!hzb.IsValid()){return;}
                var view=new VirtualGeometryClusterView(this.camera,cameraPosition,planes,projectionScale,nearPlane,this.camera.orthographic,viewProjection);
                for(int sourceIndex=0;sourceIndex<sources.Count;sourceIndex++){
                    VirtualGeometryClusterSource source=sources[sourceIndex];
                    if(source==null||!source.isActiveAndEnabled||(this.camera.cullingMask&(1<<source.gameObject.layer))==0||!source.CanDraw(this.camera)){continue;}
                    if(!VirtualGeometryMaterials.TryResolve(source.Material,out Material drawMaterial,out int drawPass,out string _)){continue;}
                    VirtualGeometryClusterPool pool=source.Pool;
                    VirtualGeometryClusterCameraState state=source.GetCameraState(this.camera);
                    int candidates=source.CollectCandidates(in view,state);
                    if(candidates==0){continue;}
                    BufferHandle vertices=graph.ImportBuffer(pool.Vertices);
                    BufferHandle visible=graph.ImportBuffer(state.VisibleIndices);
                    BufferHandle arguments=graph.ImportBuffer(state.Arguments);
                    VirtualGeometryClusterCullSpace space=state.CullSpace;
                    using(IComputeRenderGraphBuilder builder=graph.AddComputePass<ClusterCullPassData>("Virtual Geometry Cluster Cull",out ClusterCullPassData data,VirtualGeometryDiagnostics.Select)){
                        data.Shader=shader;data.ResetKernel=VirtualGeometryClusterShaders.ResetKernel;data.CullKernel=VirtualGeometryClusterShaders.CullKernel;
                        data.State=state;data.CandidateCount=candidates;data.DrawCount=state.DrawCount;
                        data.Candidates=graph.ImportBuffer(state.Candidates);data.DrawRecords=graph.ImportBuffer(state.DrawRecords);data.Bounds=graph.ImportBuffer(pool.ClusterBounds);
                        data.TemplateRanges=graph.ImportBuffer(pool.TemplateRanges);data.TemplateIndices=graph.ImportBuffer(pool.TemplateIndices);
                        data.VisibleIndices=visible;data.Arguments=arguments;data.Statistics=graph.ImportBuffer(state.Statistics);
                        data.SlotVertexCount=pool.Layout.SlotVertexCount;data.SlotClusterCount=pool.Layout.SlotClusterCount;
                        data.Hzb=hzb;data.ScreenSize=screenSize;data.HzbSize=hzbSize;data.ViewProjection=space.ViewProjection;data.FrustumPlanes=state.PlaneData;
                        data.FrustumEnabled=0;if(source.EnableFrustumCulling){data.FrustumEnabled=1;}
                        data.OcclusionEnabled=0;if(source.EnableOcclusionCulling&&space.OcclusionAllowed){data.OcclusionEnabled=1;}
                        data.ReversedZ=reversedZ;data.TextureYSign=textureYSign;
                        data.CameraPosition=space.CameraPosition;data.NearPlane=space.NearPlane;
                        builder.UseTexture(hzb,AccessFlags.Read);
                        builder.UseBuffer(data.Candidates,AccessFlags.ReadWrite);builder.UseBuffer(data.DrawRecords,AccessFlags.ReadWrite);builder.UseBuffer(data.Bounds,AccessFlags.Read);
                        builder.UseBuffer(data.TemplateRanges,AccessFlags.Read);builder.UseBuffer(data.TemplateIndices,AccessFlags.Read);
                        builder.UseBuffer(data.VisibleIndices,AccessFlags.Write);builder.UseBuffer(data.Arguments,AccessFlags.ReadWrite);builder.UseBuffer(data.Statistics,AccessFlags.ReadWrite);
                        builder.SetRenderFunc(static(ClusterCullPassData input,ComputeGraphContext context)=>ExecuteClusterCull(input,context));
                    }
                    PerObjectLighting lighting;
                    int layer=source.gameObject.layer;
                    if(!this.lightingByLayer.TryGetValue(layer,out lighting)){
                        lighting=CollectLights(lightData,layer);this.lightingByLayer.Add(layer,lighting);
                    }
                    BufferHandle debug=graph.ImportBuffer(pool.DebugDummy);
                    for(int d=0;d<state.DrawCount;d++){
                        DrawData draw=this.AcquireDraw();
                        draw.Owner=state;draw.Page=null;draw.Selection=null;draw.Properties=state.DrawProperties[d];
                        draw.Material=drawMaterial;draw.Pass=drawPass;draw.Lighting=lighting;draw.DebugMode=0;draw.TargetError=0;draw.Loading=0;
                        draw.Debug=debug;
                        draw.Vertices=vertices;draw.Indices=visible;draw.Arguments=arguments;
                        Matrix4x4 objectToWorld=state.DrawObjectToWorld[d];
                        draw.ObjectToWorld=objectToWorld;draw.InvertCulling=objectToWorld.determinant<0;draw.ArgumentsOffset=d*4*sizeof(uint);
                    }
                }
            }

            private static void ExecuteClusterCull(ClusterCullPassData input,ComputeGraphContext context){
                ComputeShader shader=input.Shader;
                ComputeCommandBuffer cmd=context.cmd;
                cmd.SetBufferData((GraphicsBuffer)input.Candidates,input.State.CandidateData,0,0,input.CandidateCount*2);
                cmd.SetBufferData((GraphicsBuffer)input.DrawRecords,input.State.RecordData,0,0,input.DrawCount);
                cmd.SetComputeIntParam(shader,VirtualGeometryClusterShaders.DrawCountId,input.DrawCount);
                cmd.SetComputeBufferParam(shader,input.ResetKernel,VirtualGeometryClusterShaders.DrawRecordsId,input.DrawRecords);
                cmd.SetComputeBufferParam(shader,input.ResetKernel,VirtualGeometryClusterShaders.ArgumentsId,input.Arguments);
                cmd.SetComputeBufferParam(shader,input.ResetKernel,VirtualGeometryClusterShaders.StatisticsId,input.Statistics);
                cmd.DispatchCompute(shader,input.ResetKernel,(input.DrawCount+63)/64,1,1);
                cmd.SetComputeIntParam(shader,VirtualGeometryClusterShaders.SlotVertexCountId,input.SlotVertexCount);
                cmd.SetComputeIntParam(shader,VirtualGeometryClusterShaders.SlotClusterCountId,input.SlotClusterCount);
                cmd.SetComputeIntParam(shader,VirtualGeometryClusterShaders.CandidateCountId,input.CandidateCount);
                cmd.SetComputeIntParam(shader,VirtualGeometryClusterShaders.FrustumEnabledId,input.FrustumEnabled);
                cmd.SetComputeIntParam(shader,VirtualGeometryClusterShaders.OcclusionEnabledId,input.OcclusionEnabled);
                cmd.SetComputeVectorParam(shader,ScreenSizeId,input.ScreenSize);
                cmd.SetComputeVectorParam(shader,HzbSizeId,input.HzbSize);
                cmd.SetComputeMatrixParam(shader,ViewProjectionId,input.ViewProjection);
                cmd.SetComputeVectorArrayParam(shader,FrustumPlanesId,input.FrustumPlanes);
                cmd.SetComputeIntParam(shader,ReversedZId,input.ReversedZ);
                cmd.SetComputeFloatParam(shader,DepthBiasId,0.00001f);
                cmd.SetComputeFloatParam(shader,TextureYSignId,input.TextureYSign);
                cmd.SetComputeVectorParam(shader,CameraPositionId,input.CameraPosition);
                cmd.SetComputeFloatParam(shader,NearPlaneId,input.NearPlane);
                int kernel=input.CullKernel;
                cmd.SetComputeTextureParam(shader,kernel,HzbId,input.Hzb);
                cmd.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.CandidateSlotsId,input.Candidates);
                cmd.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.DrawRecordsId,input.DrawRecords);
                cmd.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.ClusterBoundsId,input.Bounds);
                cmd.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.TemplateRangesId,input.TemplateRanges);
                cmd.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.TemplateIndicesId,input.TemplateIndices);
                cmd.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.VisibleIndicesId,input.VisibleIndices);
                cmd.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.ArgumentsId,input.Arguments);
                cmd.SetComputeBufferParam(shader,kernel,VirtualGeometryClusterShaders.StatisticsId,input.Statistics);
                int groups=input.CandidateCount*input.SlotClusterCount;
                int groupsX=Mathf.Min(groups,65535);int groupsY=(groups+65534)/65535;
                cmd.DispatchCompute(shader,kernel,groupsX,groupsY,1);
            }

            public override void RecordRenderGraph(RenderGraph graph,ContextContainer frameData){
                using(RecordMarker.Auto()){this.Record(graph,frameData);}
            }
            private void Record(RenderGraph graph,ContextContainer frameData){
                if(this.camera==null){return;}
                UniversalResourceData resources=frameData.Get<UniversalResourceData>();
                IReadOnlyList<VirtualGeometryRenderer> renderers=VirtualGeometryRenderRegistry.Renderers;
                RenderTextureDescriptor cameraDescriptor=frameData.Get<UniversalCameraData>().cameraTargetDescriptor;
                int width=cameraDescriptor.width;int height=cameraDescriptor.height;
                int hzbWidth=Mathf.NextPowerOfTwo(width);int hzbHeight=Mathf.NextPowerOfTwo(height);
                int mipCount=1+(int)Mathf.Log(Mathf.Max(hzbWidth,hzbHeight),2);
                Vector4 screenSize=new Vector4(width,height,0,0);
                Vector4 hzbSize=new Vector4(hzbWidth,hzbHeight,mipCount,0);
                TextureHandle hzb=default;
                if(this.selectionShader!=null){
                    // カメラごとの現フレーム深度だけを使用し、履歴や別カメラの遮蔽物を流用しない。
                    TextureDesc descriptor=new TextureDesc(hzbWidth,hzbHeight){
                        name="Virtual Geometry Current HZB",colorFormat=GraphicsFormat.R32_SFloat,
                        enableRandomWrite=true,useMipMap=true,autoGenerateMips=false,filterMode=FilterMode.Point
                    };
                    hzb=graph.CreateTexture(descriptor);
                    using(IComputeRenderGraphBuilder builder=graph.AddComputePass<HzbPassData>("Virtual Geometry Current HZB",out HzbPassData data,VirtualGeometryDiagnostics.Hzb)){
                        data.Shader=this.selectionShader;data.CopyKernel=this.copyDepthKernel;data.DownsampleKernel=this.downsampleKernel;
                        data.Depth=resources.activeDepthTexture;data.Hzb=hzb;data.ScreenSize=screenSize;data.HzbSize=hzbSize;
                        data.ReversedZ=SystemInfo.usesReversedZBuffer?1:0;
                        builder.UseTexture(data.Depth,AccessFlags.Read);builder.UseTexture(hzb,AccessFlags.ReadWrite);
                        builder.SetRenderFunc(static(HzbPassData input,ComputeGraphContext context)=>{
                            context.cmd.SetComputeVectorParam(input.Shader,ScreenSizeId,input.ScreenSize);
                            context.cmd.SetComputeVectorParam(input.Shader,HzbSizeId,input.HzbSize);
                            context.cmd.SetComputeIntParam(input.Shader,ReversedZId,input.ReversedZ);
                            context.cmd.SetComputeTextureParam(input.Shader,input.CopyKernel,DepthSourceId,input.Depth,0,RenderTextureSubElement.Depth);
                            context.cmd.SetComputeTextureParam(input.Shader,input.CopyKernel,HzbDestinationId,input.Hzb,0);
                            context.cmd.DispatchCompute(input.Shader,input.CopyKernel,((int)input.HzbSize.x+7)/8,((int)input.HzbSize.y+7)/8,1);
                            for(int mip=1;mip<(int)input.HzbSize.z;mip++){
                                context.cmd.SetComputeIntParam(input.Shader,SourceMipId,mip-1);
                                context.cmd.SetComputeIntParam(input.Shader,DestinationMipId,mip);
                                context.cmd.SetComputeTextureParam(input.Shader,input.DownsampleKernel,HzbSourceId,input.Hzb);
                                context.cmd.SetComputeTextureParam(input.Shader,input.DownsampleKernel,HzbDestinationId,input.Hzb,mip);
                                int mipWidth=Mathf.Max(1,(int)input.HzbSize.x>>mip);int mipHeight=Mathf.Max(1,(int)input.HzbSize.y>>mip);
                                context.cmd.DispatchCompute(input.Shader,input.DownsampleKernel,(mipWidth+7)/8,(mipHeight+7)/8,1);
                            }
                        });
                    }
                }
                UniversalLightData lightData=frameData.Get<UniversalLightData>();
                Plane[] planes=GeometryUtility.CalculateFrustumPlanes(this.camera);
                for(int i=0;i<planes.Length;i++){this.planeVectors[i]=new Vector4(planes[i].normal.x,planes[i].normal.y,planes[i].normal.z,planes[i].distance);}
                this.selectCount=0;this.drawCount=0;this.feedbackCount=0;this.lightingByLayer.Clear();
                Matrix4x4 viewProjection=GL.GetGPUProjectionMatrix(this.camera.projectionMatrix,true)*this.camera.worldToCameraMatrix;
                int pixelHeight=Mathf.Max(1,this.camera.pixelHeight);
                float projectionScale=0.5f*pixelHeight/Mathf.Tan(this.camera.fieldOfView*Mathf.Deg2Rad*0.5f);
                if(this.camera.orthographic){projectionScale=pixelHeight/(2f*this.camera.orthographicSize);}
                int reversedZ=0;if(SystemInfo.usesReversedZBuffer){reversedZ=1;}
                float textureYSign=1;if(SystemInfo.graphicsUVStartsAtTop){textureYSign=-1;}
                int orthographic=0;if(this.camera.orthographic){orthographic=1;}
                Vector3 cameraPosition=this.camera.transform.position;
                float nearPlane=Mathf.Max(1e-5f,this.camera.nearClipPlane);
                // 手順1: 描く部品ごとに、選択・描画・feedbackの項目を集める(passはまだ作らない)。bufferのimportと宣言は、passを作るときにまとめて行う。
                for(int rendererIndex=0;rendererIndex<renderers.Count;rendererIndex++){
                    VirtualGeometryRenderer instance=renderers[rendererIndex];
                    VirtualGeometryGpuPage page=null;bool gate;
                    using(RecordGateMarker.Auto()){gate=this.PassesGate(instance,planes,out page);}
                    if(!gate){continue;}
                    BufferHandle vertices=graph.ImportBuffer(page.VertexBuffer);
                    BufferHandle sourceIndices=graph.ImportBuffer(page.IndexBuffer);
                    VirtualGeometrySelectionState selection=null;
                    if(page.HasHierarchy){selection=instance.GetSelection(this.camera);}
                    GraphicsBuffer argumentsBuffer=page.ArgumentsBuffer;
                    if(selection!=null){argumentsBuffer=selection.ArgumentsBuffer;}
                    BufferHandle arguments=graph.ImportBuffer(argumentsBuffer);
                    BufferHandle drawIndices=sourceIndices;
                    Matrix4x4 objectToWorld=instance.transform.localToWorldMatrix;
                    if(page.HasHierarchy){
                        if(this.selectionShader==null){
                            if(!this.reportedMissingSelector){
                                Debug.LogError("Virtual Geometry hierarchy page requires VirtualGeometrySelect.compute on the renderer feature.");
                                this.reportedMissingSelector=true;
                            }
                            continue;
                        }
                        using(RecordSelectMarker.Auto()){
                            SelectionPassData data=this.AcquireSelect();
                            data.Page=page;
                            data.PageTable=graph.ImportBuffer(page.PageTableBuffer);data.Requests=graph.ImportBuffer(selection.FeedbackBuffer);
                            data.Streaming=0;if(page.IsStreaming){data.Streaming=1;}
                            data.RequestCapacity=page.RequestCapacity;
                            data.Shader=this.selectionShader;data.ResetKernel=this.resetKernel;data.SelectKernel=this.selectKernel;
                            data.Debug=graph.ImportBuffer(selection.DebugBuffer);data.DebugMode=(int)instance.DebugView;
                            data.CullingStatistics=graph.ImportBuffer(selection.CullingStatisticsBuffer);data.Hzb=hzb;data.ScreenSize=screenSize;data.HzbSize=hzbSize;
                            data.ViewProjection=viewProjection;
                            data.FrustumPlanes=this.planeVectors;data.FrustumEnabled=0;if(instance.EnableFrustumCulling){data.FrustumEnabled=1;}
                            data.OcclusionEnabled=0;if(instance.EnableOcclusionCulling){data.OcclusionEnabled=1;}
                            data.ReversedZ=reversedZ;data.TextureYSign=textureYSign;
                            data.SourceIndices=sourceIndices;data.Clusters=graph.ImportBuffer(page.ClusterBuffer);data.Nodes=graph.ImportBuffer(page.NodeBuffer);
                            data.MaterialRanges=graph.ImportBuffer(page.MaterialRangeBuffer);data.PageResidency=graph.ImportBuffer(page.PageResidencyBuffer);
                            data.VisibleIndices=graph.ImportBuffer(selection.VisibleIndexBuffer);data.RefinementState=graph.ImportBuffer(selection.RefinementStateBuffer);
                            data.SelectionFlags=graph.ImportBuffer(selection.SelectionFlagsBuffer);data.Arguments=arguments;
                            data.ObjectToWorld=objectToWorld;data.CameraPosition=cameraPosition;
                            data.MaximumScale=instance.MaximumWorldScale;
                            data.ProjectionScale=projectionScale;data.NearPlane=nearPlane;
                            data.TargetErrorPixels=instance.LodErrorThresholdPixels;data.Hysteresis=instance.LodHysteresis;
                            data.Orthographic=orthographic;data.MaterialCount=page.MaterialCount;data.NodeCount=page.NodeCount;data.PageCount=page.PageCount;
                            drawIndices=data.VisibleIndices;
                        }
                    }
                    PerObjectLighting lighting;
                    using(RecordLightsMarker.Auto()){
                        int layer=instance.gameObject.layer;
                        if(!this.lightingByLayer.TryGetValue(layer,out lighting)){
                            lighting=CollectLights(lightData,layer);this.lightingByLayer.Add(layer,lighting);
                        }
                    }
                    using(RecordDrawMarker.Auto()){
                        GraphicsBuffer debugBuffer=page.LegacyDebugBuffer;if(selection!=null){debugBuffer=selection.DebugBuffer;}
                        BufferHandle debugHandle=graph.ImportBuffer(debugBuffer);
                        for(int materialSlot=0;materialSlot<page.MaterialCount;materialSlot++){
                            int debugMode=0;if(selection!=null){debugMode=(int)instance.DebugView;}
                            Material drawMaterial=this.material;int drawPass=0;
                            if(debugMode==0&&!instance.TryResolveDrawMaterial(materialSlot,out drawMaterial,out drawPass)){continue;}
                            DrawData data=this.AcquireDraw();
                            data.Owner=page;data.Page=page;data.Selection=selection;data.Properties=null;
                            data.Material=drawMaterial;data.Pass=drawPass;data.Lighting=lighting;data.DebugMode=debugMode;data.TargetError=instance.LodErrorThresholdPixels;
                            data.Loading=0;
                            if(instance.StreamingStatistics!=null){data.Loading=instance.StreamingStatistics.Queued+instance.StreamingStatistics.Loading+instance.StreamingStatistics.Uploading;}
                            data.Debug=debugHandle;
                            data.Vertices=vertices;data.Indices=drawIndices;data.Arguments=arguments;
                            data.ObjectToWorld=objectToWorld;data.InvertCulling=objectToWorld.determinant<0;
                            data.ArgumentsOffset=materialSlot*4*sizeof(uint);
                        }
                    }
                    // 全pageが常駐済み、またはcameraが静止して要求がない間はfeedbackのreadbackを出さない(間引きの規則はShouldRequestFeedback)。
                    bool feedbackNeeded=false;
                    if(page.IsStreaming&&selection!=null&&!page.StreamingController.IsFullyResident){
                        VirtualGeometryStreamingStatistics streaming=page.StreamingController.Statistics;
                        feedbackNeeded=selection.ShouldRequestFeedback(rendererIndex,streaming.Queued+streaming.Loading+streaming.Uploading>0);
                    }
                    if(feedbackNeeded){
                        FeedbackPassData feedback=this.AcquireFeedback();
                        feedback.Page=page;feedback.Selection=selection;
                    }
                }
                this.RecordClusterSources(graph,hzb,screenSize,hzbSize,viewProjection,planes,projectionScale,nearPlane,reversedZ,textureYSign,cameraPosition,lightData);
                // 手順2: 集めた項目をcameraにつき1 passずつにまとめて出す。順序は 選択 → 描画 → feedback(描画は選択の結果を、feedbackは選択のrequest bufferを読む)。
                VirtualGeometryDiagnostics.LastSelectItems=this.selectCount;VirtualGeometryDiagnostics.LastDrawItems=this.drawCount;VirtualGeometryDiagnostics.LastFeedbackItems=this.feedbackCount;
                int batchSize=PassBatchSize;
                for(int start=0;start<this.selectCount;start+=batchSize){
                    int end=Mathf.Min(this.selectCount,start+batchSize);
                    this.declaredPages.Clear();
                    using(IComputeRenderGraphBuilder builder=graph.AddComputePass<SelectionBatch>("Virtual Geometry Select and Cull",out SelectionBatch batch,VirtualGeometryDiagnostics.Select)){
                        batch.Items=this.selectItems;batch.Start=start;batch.End=end;
                        builder.UseTexture(hzb,AccessFlags.Read);
                        for(int i=start;i<end;i++){
                            SelectionPassData item=this.selectItems[i];
                            if(this.declaredPages.Add(item.Page)){
                                builder.UseBuffer(item.PageTable,AccessFlags.Read);builder.UseBuffer(item.SourceIndices,AccessFlags.Read);builder.UseBuffer(item.Clusters,AccessFlags.Read);
                                builder.UseBuffer(item.Nodes,AccessFlags.Read);builder.UseBuffer(item.MaterialRanges,AccessFlags.Read);builder.UseBuffer(item.PageResidency,AccessFlags.Read);
                            }
                            // 以下はinstance×cameraごとの選択状態で、項目ごとに別のbuffer。
                            builder.UseBuffer(item.Requests,AccessFlags.ReadWrite);builder.UseBuffer(item.Debug,AccessFlags.Write);builder.UseBuffer(item.CullingStatistics,AccessFlags.ReadWrite);
                            builder.UseBuffer(item.VisibleIndices,AccessFlags.Write);builder.UseBuffer(item.RefinementState,AccessFlags.ReadWrite);
                            builder.UseBuffer(item.SelectionFlags,AccessFlags.Write);builder.UseBuffer(item.Arguments,AccessFlags.Write);
                        }
                        builder.SetRenderFunc(static(SelectionBatch input,ComputeGraphContext context)=>{
                            for(int i=input.Start;i<input.End;i++){ExecuteSelect(input.Items[i],context);}
                        });
                    }
                }
                bool drewVirtual=this.drawCount>0;
                for(int start=0;start<this.drawCount;start+=batchSize){
                    int end=Mathf.Min(this.drawCount,start+batchSize);
                    this.declaredOwners.Clear();this.declaredSelections.Clear();
                    using(IRasterRenderGraphBuilder builder=graph.AddRasterRenderPass<DrawBatch>("Virtual Geometry Draw",out DrawBatch batch,VirtualGeometryDiagnostics.Draw)){
                        batch.Items=this.drawItems;batch.Start=start;batch.End=end;
                        // 利用者shaderは影map・SSAO・light cookie等のglobal textureを読むため、すべてのglobal textureの利用を宣言する。
                        builder.UseAllGlobalTextures(true);
                        for(int i=start;i<end;i++){
                            DrawData item=this.drawItems[i];
                            if(this.declaredOwners.Add(item.Owner)){
                                builder.UseBuffer(item.Vertices,AccessFlags.Read);
                                if(item.Selection==null){
                                    builder.UseBuffer(item.Indices,AccessFlags.Read);builder.UseBuffer(item.Arguments,AccessFlags.Read);builder.UseBuffer(item.Debug,AccessFlags.Read);
                                }
                            }
                            if(item.Selection!=null&&this.declaredSelections.Add(item.Selection)){
                                builder.UseBuffer(item.Indices,AccessFlags.Read);builder.UseBuffer(item.Arguments,AccessFlags.Read);builder.UseBuffer(item.Debug,AccessFlags.Read);
                            }
                        }
                        builder.SetRenderAttachment(resources.activeColorTexture,0,AccessFlags.ReadWrite);
                        builder.SetRenderAttachmentDepth(resources.activeDepthTexture,AccessFlags.ReadWrite);
                        builder.SetRenderFunc(static(DrawBatch input,RasterGraphContext context)=>{
                            // 描画命令はMaterialPropertyBlockの値を記録時にコピーするため、1つを使い回し、光源などが変わったときだけ設定し直す。
                            MaterialPropertyBlock shared=context.renderGraphPool.GetTempMaterialPropertyBlock();
                            PerObjectLighting lighting=null;bool inverted=false;
                            for(int i=input.Start;i<input.End;i++){
                                DrawData item=input.Items[i];
                                if(item.DebugMode!=0){ExecuteDraw(item,context,context.renderGraphPool.GetTempMaterialPropertyBlock(),true);continue;}
                                if(item.Properties!=null){ExecuteDraw(item,context,item.Properties,true);continue;}
                                bool applyShared=lighting==null||!ReferenceEquals(lighting,item.Lighting)||inverted!=item.InvertCulling;
                                ExecuteDraw(item,context,shared,applyShared);
                                if(applyShared){lighting=item.Lighting;inverted=item.InvertCulling;}
                            }
                        });
                    }
                }
                for(int start=0;start<this.feedbackCount;start+=batchSize){
                    int end=Mathf.Min(this.feedbackCount,start+batchSize);
                    using(RecordFeedbackMarker.Auto())
                    using(IUnsafeRenderGraphBuilder builder=graph.AddUnsafePass<FeedbackBatch>("Virtual Geometry Async Page Requests",out FeedbackBatch batch)){
                        batch.Items=this.feedbackItems;batch.Start=start;batch.End=end;
                        for(int i=start;i<end;i++){
                            builder.UseBuffer(graph.ImportBuffer(this.feedbackItems[i].Selection.FeedbackBuffer),AccessFlags.Read);
                        }
                        builder.UseTexture(resources.activeColorTexture,AccessFlags.Read);
                        builder.AllowPassCulling(false);
                        builder.SetRenderFunc(static(FeedbackBatch input,UnsafeGraphContext context)=>{
                            for(int i=input.Start;i<input.End;i++){input.Items[i].Page.RecordFeedback(context.cmd,input.Items[i].Selection);}
                        });
                    }
                }
                // URP may have copied depth before this injection point. Refresh the public texture after VG writes.
                if(drewVirtual&&this.depthCopy!=null&&resources.cameraDepthTexture.IsValid()&&resources.cameraDepthTexture!=resources.activeDepthTexture){
                    this.depthCopy.Render(graph,frameData,resources.cameraDepthTexture,resources.activeDepthTexture,true,"Virtual Geometry Camera Depth Sync");
                }
            }
        }
    }
}