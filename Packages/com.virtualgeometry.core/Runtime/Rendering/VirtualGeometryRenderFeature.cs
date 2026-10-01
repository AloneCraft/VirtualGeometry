using System.Collections.Generic;
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
        }
        public override void AddRenderPasses(ScriptableRenderer renderer,ref RenderingData renderingData){
            if(this.pass!=null&&VirtualGeometryRenderRegistry.Renderers.Count>0){this.pass.Setup(renderingData.cameraData.camera);renderer.EnqueuePass(this.pass);}
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
            private sealed class DrawData{
                public Material Material;public int Pass;
                public BufferHandle Vertices;
                public BufferHandle Indices;
                public BufferHandle Arguments;
                public Matrix4x4 ObjectToWorld;public bool InvertCulling;
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

            public override void RecordRenderGraph(RenderGraph graph,ContextContainer frameData){
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
                bool drewVirtual=false;
                Plane[] planes=GeometryUtility.CalculateFrustumPlanes(this.camera);
                Vector4[] planeVectors=new Vector4[6];
                for(int i=0;i<planes.Length;i++){planeVectors[i]=new Vector4(planes[i].normal.x,planes[i].normal.y,planes[i].normal.z,planes[i].distance);}
                for(int rendererIndex=0;rendererIndex<renderers.Count;rendererIndex++){
                    VirtualGeometryRenderer instance=renderers[rendererIndex];
                    if(instance==null||!instance.isActiveAndEnabled||(this.camera.cullingMask&(1<<instance.gameObject.layer))==0||
                        !instance.TryGetGpuPage(out VirtualGeometryGpuPage page)){continue;}
                    if(!instance.CanDrawVirtual(this.camera)){continue;}
                    BufferHandle vertices=graph.ImportBuffer(page.VertexBuffer);
                    BufferHandle sourceIndices=graph.ImportBuffer(page.IndexBuffer);
                    VirtualGeometrySelectionState selection=page.HasHierarchy?instance.GetSelection(this.camera):null;
                    BufferHandle arguments=graph.ImportBuffer(selection!=null?selection.ArgumentsBuffer:page.ArgumentsBuffer);
                    BufferHandle drawIndices=sourceIndices;
                    if(page.HasHierarchy){
                        if(this.selectionShader==null){
                            if(!this.reportedMissingSelector){
                                Debug.LogError("Virtual Geometry hierarchy page requires VirtualGeometrySelect.compute on the renderer feature.");
                                this.reportedMissingSelector=true;
                            }
                            continue;
                        }
                        BufferHandle clusters=graph.ImportBuffer(page.ClusterBuffer);
                        BufferHandle nodes=graph.ImportBuffer(page.NodeBuffer);
                        BufferHandle ranges=graph.ImportBuffer(page.MaterialRangeBuffer);
                        BufferHandle residency=graph.ImportBuffer(page.PageResidencyBuffer);
                        BufferHandle visible=graph.ImportBuffer(selection.VisibleIndexBuffer);
                        BufferHandle refinement=graph.ImportBuffer(selection.RefinementStateBuffer);
                        BufferHandle flags=graph.ImportBuffer(selection.SelectionFlagsBuffer);
                        BufferHandle statistics=graph.ImportBuffer(selection.CullingStatisticsBuffer);
                        BufferHandle pageTable=graph.ImportBuffer(page.PageTableBuffer);
                        BufferHandle requests=graph.ImportBuffer(selection.FeedbackBuffer);
                        using(IComputeRenderGraphBuilder builder=graph.AddComputePass<SelectionPassData>("Virtual Geometry Select and Cull",out SelectionPassData data,VirtualGeometryDiagnostics.Select)){
                            data.PageTable=pageTable;data.Requests=requests;data.Streaming=page.IsStreaming?1:0;data.RequestCapacity=page.RequestCapacity;
                            builder.UseBuffer(pageTable,AccessFlags.Read);builder.UseBuffer(requests,AccessFlags.ReadWrite);
                            data.Shader=this.selectionShader;data.ResetKernel=this.resetKernel;data.SelectKernel=this.selectKernel;
                            data.Debug=graph.ImportBuffer(selection.DebugBuffer);data.DebugMode=(int)instance.DebugView;builder.UseBuffer(data.Debug,AccessFlags.Write);
                            data.CullingStatistics=statistics;data.Hzb=hzb;data.ScreenSize=screenSize;data.HzbSize=hzbSize;
                            data.ViewProjection=GL.GetGPUProjectionMatrix(this.camera.projectionMatrix,true)*this.camera.worldToCameraMatrix;
                            data.FrustumPlanes=planeVectors;data.FrustumEnabled=instance.EnableFrustumCulling?1:0;
                            data.OcclusionEnabled=instance.EnableOcclusionCulling?1:0;data.ReversedZ=SystemInfo.usesReversedZBuffer?1:0;
                            data.TextureYSign=SystemInfo.graphicsUVStartsAtTop?-1:1;
                            builder.UseBuffer(statistics,AccessFlags.ReadWrite);builder.UseTexture(hzb,AccessFlags.Read);
                            data.SourceIndices=sourceIndices;data.Clusters=clusters;data.Nodes=nodes;data.MaterialRanges=ranges;data.PageResidency=residency;
                            data.VisibleIndices=visible;data.RefinementState=refinement;data.SelectionFlags=flags;data.Arguments=arguments;
                            data.ObjectToWorld=instance.transform.localToWorldMatrix;data.CameraPosition=this.camera.transform.position;
                            data.MaximumScale=instance.MaximumWorldScale;
                            int pixelHeight=Mathf.Max(1,this.camera.pixelHeight);
                            data.ProjectionScale=this.camera.orthographic?pixelHeight/(2f*this.camera.orthographicSize):
                                0.5f*pixelHeight/Mathf.Tan(this.camera.fieldOfView*Mathf.Deg2Rad*0.5f);
                            data.NearPlane=Mathf.Max(1e-5f,this.camera.nearClipPlane);
                            data.TargetErrorPixels=instance.LodErrorThresholdPixels;data.Hysteresis=instance.LodHysteresis;
                            data.Orthographic=this.camera.orthographic?1:0;data.MaterialCount=page.MaterialCount;data.NodeCount=page.NodeCount;data.PageCount=page.PageCount;
                            builder.UseBuffer(sourceIndices,AccessFlags.Read);builder.UseBuffer(clusters,AccessFlags.Read);builder.UseBuffer(nodes,AccessFlags.Read);
                            builder.UseBuffer(ranges,AccessFlags.Read);builder.UseBuffer(residency,AccessFlags.Read);builder.UseBuffer(visible,AccessFlags.Write);
                            builder.UseBuffer(refinement,AccessFlags.ReadWrite);builder.UseBuffer(flags,AccessFlags.Write);builder.UseBuffer(arguments,AccessFlags.Write);
                            builder.SetRenderFunc(static(SelectionPassData input,ComputeGraphContext context)=>{
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
                            });
                        }
                        drawIndices=visible;
                    }
                    PerObjectLighting lighting=CollectLights(lightData,instance.gameObject.layer);
                    Matrix4x4 objectToWorld=instance.transform.localToWorldMatrix;
                    for(int materialSlot=0;materialSlot<page.MaterialCount;materialSlot++){
                        int debugMode=0;if(selection!=null){debugMode=(int)instance.DebugView;}
                        Material drawMaterial=this.material;int drawPass=0;
                        if(debugMode==0&&!VirtualGeometryMaterials.TryResolve(instance.GetMaterial(materialSlot),out drawMaterial,out drawPass,out string _)){continue;}
                        using(IRasterRenderGraphBuilder builder=graph.AddRasterRenderPass<DrawData>("Virtual Geometry Draw",out DrawData data,VirtualGeometryDiagnostics.Draw)){
                            drewVirtual=true;
                            data.Material=drawMaterial;data.Pass=drawPass;data.Lighting=lighting;data.DebugMode=debugMode;data.TargetError=instance.LodErrorThresholdPixels;
                            data.Loading=0;
                            if(instance.StreamingStatistics!=null){data.Loading=instance.StreamingStatistics.Queued+instance.StreamingStatistics.Loading+instance.StreamingStatistics.Uploading;}
                            GraphicsBuffer debugBuffer=page.LegacyDebugBuffer;if(selection!=null){debugBuffer=selection.DebugBuffer;}
                            data.Debug=graph.ImportBuffer(debugBuffer);builder.UseBuffer(data.Debug,AccessFlags.Read);
                            // 利用者shaderは影map・SSAO・light cookie等のglobal textureを読むため、すべてのglobal textureの利用を宣言する。
                            builder.UseAllGlobalTextures(true);
                            data.Vertices=vertices;data.Indices=drawIndices;data.Arguments=arguments;
                            data.ObjectToWorld=objectToWorld;data.InvertCulling=objectToWorld.determinant<0;
                            data.ArgumentsOffset=materialSlot*4*sizeof(uint);
                            builder.UseBuffer(vertices,AccessFlags.Read);builder.UseBuffer(drawIndices,AccessFlags.Read);builder.UseBuffer(arguments,AccessFlags.Read);
                            builder.SetRenderAttachment(resources.activeColorTexture,0,AccessFlags.ReadWrite);
                            builder.SetRenderAttachmentDepth(resources.activeDepthTexture,AccessFlags.ReadWrite);
                            builder.SetRenderFunc(static(DrawData input,RasterGraphContext context)=>{
                                MaterialPropertyBlock properties=context.renderGraphPool.GetTempMaterialPropertyBlock();
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
                                }else{
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
                            });
                        }
                    }
                    if(page.IsStreaming&&selection!=null){
                        using(IUnsafeRenderGraphBuilder builder=graph.AddUnsafePass<FeedbackPassData>("Virtual Geometry Async Page Requests",out FeedbackPassData feedback)){
                            feedback.Page=page;feedback.Selection=selection;
                            builder.UseBuffer(graph.ImportBuffer(selection.FeedbackBuffer),AccessFlags.Read);
                            builder.UseTexture(resources.activeColorTexture,AccessFlags.Read);
                            builder.AllowPassCulling(false);
                            builder.SetRenderFunc(static(FeedbackPassData input,UnsafeGraphContext context)=>input.Page.RecordFeedback(context.cmd,input.Selection));
                        }
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