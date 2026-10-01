using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VirtualizedGeometry.Diagnostics{
    /// <summary>M0.unity専用。GPUで生成した頂点と引数をURPの間接描画へ接続する検証Feature。</summary>
    public sealed class IndirectTriangleFeature:ScriptableRendererFeature{
        [SerializeField] private ComputeShader generator;
        [SerializeField] private Shader surfaceShader;
        private Material material;
        private ProbePass pass;
        private string lastDiagnostic;

        public override void Create(){
            CoreUtils.Destroy(this.material);
            this.material=null;
            this.pass=null;
            if(this.generator==null||this.surfaceShader==null){
                return;
            }
            this.material=CoreUtils.CreateEngineMaterial(this.surfaceShader);
            this.pass=new ProbePass(this.generator,this.material);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer,ref RenderingData renderingData){
            string reason=GetUnsupportedReason(renderingData.cameraData);
            if(reason!=null){
                if(this.lastDiagnostic!=reason){
                    Debug.LogWarning("Virtualized Geometry M0: "+reason);
                    this.lastDiagnostic=reason;
                }
                return;
            }
            this.lastDiagnostic=null;
            if(this.pass!=null){
                renderer.EnqueuePass(this.pass);
            }
        }

        private static string GetUnsupportedReason(CameraData camera){
            if(!SystemInfo.supportsComputeShaders||!SystemInfo.supportsIndirectArgumentsBuffer||SystemInfo.graphicsShaderLevel<50){
                return "Compute/indirect drawing/Shader Model 5 is unavailable.";
            }
            if(camera.renderType!=CameraRenderType.Base||camera.xr.enabled){
                return "Camera Stack and XR are outside the M0 validation scope.";
            }
            if(camera.cameraTargetDescriptor.msaaSamples>1){
                return "MSAA is outside the M0 validation scope.";
            }
            return null;
        }

        protected override void Dispose(bool disposing){
            CoreUtils.Destroy(this.material);
            this.material=null;
            this.pass=null;
        }

        /// <summary>カメラごとに一時バッファを確保し、共有履歴や解放待ち資源を持たないM0パス。</summary>
        private sealed class ProbePass:ScriptableRenderPass{
            private readonly ComputeShader generator;
            private readonly Material material;
            private readonly int kernel;
            private static readonly int VerticesId=Shader.PropertyToID("_VGVertices");
            private static readonly int ArgumentsId=Shader.PropertyToID("_VGArguments");

            private sealed class GenerateData{
                public ComputeShader Generator;
                public int Kernel;
                public BufferHandle Vertices;
                public BufferHandle Arguments;
            }

            private sealed class DrawData{
                public Material Material;
                public BufferHandle Vertices;
                public BufferHandle Arguments;
            }

            public ProbePass(ComputeShader generator,Material material){
                this.generator=generator;
                this.material=material;
                this.kernel=generator.FindKernel("Generate");
                // 通常opaqueの後、URPのdepth copyより前に深度を書き込む。
                this.renderPassEvent=(RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques-1);
            }

            public override void RecordRenderGraph(RenderGraph graph,ContextContainer frameData){
                UniversalResourceData resources=frameData.Get<UniversalResourceData>();
                BufferHandle vertices=graph.CreateBuffer(new BufferDesc(3,16){name="VG M0 vertices"});
                BufferHandle arguments=graph.CreateBuffer(new BufferDesc(4,4){
                    name="VG M0 indirect arguments",
                    target=GraphicsBuffer.Target.Raw|GraphicsBuffer.Target.IndirectArguments
                });
                using(IComputeRenderGraphBuilder builder=graph.AddComputePass<GenerateData>("VG M0 Generate",out GenerateData data)){
                    data.Generator=this.generator;
                    data.Kernel=this.kernel;
                    data.Vertices=vertices;
                    data.Arguments=arguments;
                    builder.UseBuffer(vertices,AccessFlags.Write);
                    builder.UseBuffer(arguments,AccessFlags.Write);
                    builder.SetRenderFunc(static(GenerateData input,ComputeGraphContext context)=>{
                        context.cmd.SetComputeBufferParam(input.Generator,input.Kernel,VerticesId,input.Vertices);
                        context.cmd.SetComputeBufferParam(input.Generator,input.Kernel,ArgumentsId,input.Arguments);
                        context.cmd.DispatchCompute(input.Generator,input.Kernel,1,1,1);
                    });
                }
                using(IRasterRenderGraphBuilder builder=graph.AddRasterRenderPass<DrawData>("VG M0 Draw Indirect",out DrawData data)){
                    data.Material=this.material;
                    data.Vertices=vertices;
                    data.Arguments=arguments;
                    builder.UseBuffer(vertices,AccessFlags.Read);
                    builder.UseBuffer(arguments,AccessFlags.Read);
                    builder.SetRenderAttachment(resources.activeColorTexture,0,AccessFlags.ReadWrite);
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture,AccessFlags.ReadWrite);
                    builder.SetRenderFunc(static(DrawData input,RasterGraphContext context)=>{
                        MaterialPropertyBlock properties=context.renderGraphPool.GetTempMaterialPropertyBlock();
                        properties.SetBuffer(VerticesId,(GraphicsBuffer)input.Vertices);
                        context.cmd.DrawProceduralIndirect(Matrix4x4.identity,input.Material,0,MeshTopology.Triangles,(GraphicsBuffer)input.Arguments,0,properties);
                    });
                }
            }
        }
    }
}
