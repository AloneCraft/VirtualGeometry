using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VirtualizedGeometry.Editor{
    /// <summary>後続depth consumerと先行DepthNormalsの双方を実測する検証専用feature。</summary>
    public sealed class M5DepthConsumerFeature:ScriptableRendererFeature,IVirtualGeometryCompatibleRenderFeature{
        public bool NormalPrepass;public bool RequiresEarlyGeometry=>this.NormalPrepass;
        internal static GraphicsBuffer Output;internal static int Width;internal static int Height;
        private Probe pass;
        public override void Create(){this.pass=new Probe(this.NormalPrepass);}
        public override void AddRenderPasses(ScriptableRenderer renderer,ref RenderingData data){if(Output!=null){renderer.EnqueuePass(this.pass);}}
        private sealed class Probe:ScriptableRenderPass{
            private readonly bool normals;
            private sealed class Data{public ComputeShader Shader;public TextureHandle Active,Camera,Normals;public BufferHandle Output;public int Width,Height;public bool HasNormals,CameraIsDepth;}
            public Probe(bool normals){this.normals=normals;this.renderPassEvent=RenderPassEvent.BeforeRenderingTransparents;this.ConfigureInput(ScriptableRenderPassInput.Depth|(normals?ScriptableRenderPassInput.Normal:ScriptableRenderPassInput.None));}
            public override void RecordRenderGraph(RenderGraph graph,ContextContainer frameData){
                var resources=frameData.Get<UniversalResourceData>();
                using(var builder=graph.AddComputePass<Data>("M5 camera depth consumer",out var data)){
                    data.Shader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Editor/Shaders/M5DepthCompare.compute");
                    data.Active=resources.activeDepthTexture;data.Camera=resources.cameraDepthTexture;data.Normals=this.normals?resources.cameraNormalsTexture:resources.activeColorTexture;
                    data.CameraIsDepth=GraphicsFormatUtility.IsDepthFormat(graph.GetTextureDesc(data.Camera).format);
                    data.Output=graph.ImportBuffer(Output);data.Width=Width;data.Height=Height;data.HasNormals=this.normals;
                    builder.UseTexture(data.Active,AccessFlags.Read);builder.UseTexture(data.Camera,AccessFlags.Read);builder.UseTexture(data.Normals,AccessFlags.Read);builder.UseBuffer(data.Output,AccessFlags.Write);builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static(Data input,ComputeGraphContext context)=>{
                        context.cmd.SetComputeIntParam(input.Shader,"_Width",input.Width);context.cmd.SetComputeIntParam(input.Shader,"_Height",input.Height);context.cmd.SetComputeIntParam(input.Shader,"_HasNormals",input.HasNormals?1:0);
                        context.cmd.SetComputeTextureParam(input.Shader,0,"_ActiveDepth",input.Active,0,RenderTextureSubElement.Depth);
                        context.cmd.SetComputeTextureParam(input.Shader,0,"_CameraDepth",input.Camera,0,input.CameraIsDepth?RenderTextureSubElement.Depth:RenderTextureSubElement.Default);
                        context.cmd.SetComputeTextureParam(input.Shader,0,"_Normals",input.Normals);context.cmd.SetComputeBufferParam(input.Shader,0,"_Results",input.Output);
                        context.cmd.DispatchCompute(input.Shader,0,(input.Width+7)/8,(input.Height+7)/8,1);
                    });
                }
            }
        }
    }
}