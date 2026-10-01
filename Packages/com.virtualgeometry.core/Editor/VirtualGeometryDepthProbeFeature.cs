using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VirtualizedGeometry.Editor{
    /// <summary>M4検証専用。描画後active depthをreadback用bufferへコピーする。</summary>
    public sealed class VirtualGeometryDepthProbeFeature:ScriptableRendererFeature,IVirtualGeometryCompatibleRenderFeature{
        public bool RequiresEarlyGeometry=>false;
        internal static GraphicsBuffer Target;
        internal static int Width;
        internal static int Height;
        private ProbePass pass;
        public override void Create(){this.pass=new ProbePass();}
        public override void AddRenderPasses(ScriptableRenderer renderer,ref RenderingData data){
            if(Target!=null){renderer.EnqueuePass(this.pass);}
        }
        private sealed class ProbePass:ScriptableRenderPass{
            private sealed class Data{
                public ComputeShader Shader;public TextureHandle Depth;public BufferHandle Output;public int Width;public int Height;
            }
            public ProbePass(){this.renderPassEvent=RenderPassEvent.AfterRenderingTransparents;}
            public override void RecordRenderGraph(RenderGraph graph,ContextContainer frameData){
                if(Target==null){return;}
                var resources=frameData.Get<UniversalResourceData>();
                using(var builder=graph.AddComputePass<Data>("Virtual Geometry Validation Depth",out Data data)){
                    data.Shader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Editor/Shaders/VirtualGeometryDepthProbe.compute");
                    data.Depth=resources.activeDepthTexture;data.Output=graph.ImportBuffer(Target);data.Width=Width;data.Height=Height;
                    builder.UseTexture(data.Depth,AccessFlags.Read);builder.UseBuffer(data.Output,AccessFlags.Write);builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static(Data input,ComputeGraphContext context)=>{
                        context.cmd.SetComputeIntParam(input.Shader,"_Width",input.Width);context.cmd.SetComputeIntParam(input.Shader,"_Height",input.Height);
                        context.cmd.SetComputeTextureParam(input.Shader,0,"_Depth",input.Depth,0,RenderTextureSubElement.Depth);
                        context.cmd.SetComputeBufferParam(input.Shader,0,"_Output",input.Output);
                        context.cmd.DispatchCompute(input.Shader,0,(input.Width+7)/8,(input.Height+7)/8,1);
                    });
                }
            }
        }
    }
}