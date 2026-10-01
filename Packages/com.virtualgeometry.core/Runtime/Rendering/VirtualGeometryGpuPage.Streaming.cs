using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualizedGeometry{
    public enum VirtualGeometryStreamingMode{Streaming,AllResident}

    [StructLayout(LayoutKind.Sequential,Pack=4)]
    internal struct VirtualGeometryPhysicalPage{
        public uint VertexStart;public uint IndexStart;public uint Resident;public uint Generation;
    }

    /// <summary>固定geometry poolとGPU完了submission番号でcontrollerを実描画へ接続する。</summary>
    internal sealed partial class VirtualGeometryGpuPage:IVirtualGeometryPageUploader{
        public VirtualGeometryStreamingController StreamingController{get;private set;}
        public GraphicsBuffer PageTableBuffer{get;private set;}
        public int RequestCapacity{get;private set;}
        public bool IsStreaming=>this.StreamingController!=null;
        private VirtualGeometryStreamMetadata streamMetadata;
        private VirtualGeometryPhysicalPage[] physicalPages;
        private uint[] residency;
        private bool pageTableDirty;
        private long lastSubmitted;
        private long completedSubmission;
        private long streamFrame;
        private bool streamingDisposed;

        private static VirtualGeometryGpuPage LoadStreaming(string path,VirtualGeometryStreamingSettings settings){
            VirtualGeometryStreamMetadata metadata;
            using(var stream=File.OpenRead(path)){metadata=VirtualGeometryStreamCodec.ReadMetadata(stream);}
            var result=new VirtualGeometryGpuPage();
            try{
                result.streamMetadata=metadata;
                result.StreamingController=new VirtualGeometryStreamingController(metadata,settings,
                    new VirtualGeometryFilePageStore(path,metadata,settings.IoDelayMilliseconds));
                result.MaterialCount=metadata.Roots.Length;result.NodeCount=metadata.Nodes.Length;result.PageCount=metadata.Pages.Length;result.NodeLevels=ReadLevels(metadata.Nodes);
                int slots=result.StreamingController.SlotCount;
                result.VertexBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,checked(slots*metadata.SlotVertices),32);
                result.IndexBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,checked(slots*metadata.SlotIndices),4);
                result.ClusterBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,metadata.Clusters.Length,48);result.ClusterBuffer.SetData(metadata.Clusters);
                result.NodeBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,metadata.Nodes.Length,64);result.NodeBuffer.SetData(metadata.Nodes);
                var page=new VirtualGeometryPage(Array.Empty<VirtualGeometryPackedVertex>(),Array.Empty<uint>(),metadata.Clusters,metadata.Nodes,metadata.Roots,result.MaterialCount,metadata.Bounds);
                VirtualGeometryMaterialRange[] ranges=BuildMaterialRanges(page,out int capacity);result.VisibleIndexCapacity=capacity;
                result.MaterialRangeBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,result.MaterialCount,16);result.MaterialRangeBuffer.SetData(ranges);
                result.residency=new uint[result.PageCount];result.physicalPages=new VirtualGeometryPhysicalPage[result.PageCount];
                result.PageResidencyBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,result.PageCount,4);result.PageResidencyBuffer.SetData(result.residency);
                result.PageTableBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,result.PageCount,16);result.PageTableBuffer.SetData(result.physicalPages);
                result.RequestCapacity=settings.RequestQueueCapacity;
                result.StreamingController.Statistics.GpuMetadataBytes=(long)metadata.Clusters.Length*48+(long)result.NodeCount*64+
                    (long)result.PageCount*20+(long)result.MaterialCount*16;
                return result;
            }catch{result.Dispose();throw;}
        }

        public void TickStreaming(long frame){
            if(this.StreamingController==null||this.streamingDisposed){return;}
            this.streamFrame=frame;
            this.StreamingController.Tick(frame,this.lastSubmitted,this.completedSubmission,this);
            if(this.pageTableDirty){
                this.PageResidencyBuffer.SetData(this.residency);this.PageTableBuffer.SetData(this.physicalPages);this.pageTableDirty=false;
            }
        }

        public void RecordFeedback(UnsafeCommandBuffer command,VirtualGeometrySelectionState selection){
            if(this.StreamingController==null||this.streamingDisposed||selection.Disposed||selection.PendingFeedback>=2){return;}
            long submission=++this.lastSubmitted;long frame=this.streamFrame;selection.PendingFeedback++;
            // 描画後の同一GPU command streamに置いたreadbackの完了は、それ以前のdraw完了も保証する。
            command.RequestAsyncReadback(selection.FeedbackBuffer,request=>{
                selection.PendingFeedback--;this.completedSubmission=Math.Max(this.completedSubmission,submission);
                if(this.streamingDisposed||selection.Disposed){return;}
                if(request.hasError){this.StreamingController.Statistics.LastError="GPU request feedback readback failed.";return;}
                var words=request.GetData<uint>();int count=(int)Math.Min(words[0],(uint)this.RequestCapacity);
                this.StreamingController.Statistics.QueueOverflow+=words[1];
                this.StreamingController.Statistics.QualityTargetMisses=words[2];
                for(int i=0;i<count;i++){
                    int offset=4+i*4;float priority=BitConverter.Int32BitsToSingle((int)words[offset+1]);
                    this.StreamingController.Request((int)words[offset],priority,frame);
                }
                // touchはpageごとの(最大優先度, generation)。0は未touch。slot再利用後の古いtouchはgenerationで捨てる。
                int touchStart=4+this.RequestCapacity*4;
                for(int page=0;page<this.physicalPages.Length&&touchStart+page*2+1<words.Length;page++){
                    uint bits=words[touchStart+page*2];if(bits==0){continue;}
                    if(this.physicalPages[page].Generation!=words[touchStart+page*2+1]){continue;}
                    this.StreamingController.Touch(page,BitConverter.Int32BitsToSingle((int)bits),frame);
                }
            });
        }

        // pageは連続slotを使うため、先頭slotの位置からpageの頂点・添字が途切れずに並ぶ。
        public void UploadVertices(int slot,int offset,VirtualGeometryPackedVertex[] vertices,int sourceOffset,int count){
            this.VertexBuffer.SetData(vertices,sourceOffset,checked(slot*this.streamMetadata.SlotVertices+offset),count);
        }
        public void UploadIndices(int slot,int offset,uint[] indices,int sourceOffset,int count){
            this.IndexBuffer.SetData(indices,sourceOffset,checked(slot*this.streamMetadata.SlotIndices+offset),count);
        }
        public void Publish(int page,int slot,int generation){
            this.physicalPages[page]=new VirtualGeometryPhysicalPage{
                VertexStart=checked((uint)(slot*this.streamMetadata.SlotVertices)),IndexStart=checked((uint)(slot*this.streamMetadata.SlotIndices)),
                Resident=1,Generation=(uint)generation
            };
            this.residency[page]=1;this.pageTableDirty=true;
        }
        public void Invalidate(int page){this.residency[page]=0;this.physicalPages[page].Resident=0;this.pageTableDirty=true;}

        private void DisposeStreaming(){
            this.streamingDisposed=true;this.StreamingController?.Dispose();
            this.PageTableBuffer?.Dispose();this.PageTableBuffer=null;
        }
    }
}
