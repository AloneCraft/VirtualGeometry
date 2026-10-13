using System;
using System.Collections.Generic;
using Omni.BatchRendering;
using UnityEngine;

namespace Omni.PlanetSurface{
    public sealed partial class PlanetSurfaceLod{
        private SurfaceDetailAtlas detailAtlas;
        private Material detailMaterial;
        private readonly HashSet<Node> detailNodes=new();
        private int detailBoundCount;
        public int DetailBoundPatchCount=>this.detailBoundCount;
        public int DetailReadyRegionCount=>this.detailAtlas?.ReadyCount??0;
        public long DetailGpuReservedBytes=>this.detailAtlas?.GpuReservedBytes??0;
        public long DetailCpuReservedBytes=>this.detailAtlas?.CpuReservedBytes??0;

        /// <summary>BRGへ最初のパッチを登録する前に、全パッチで共有するatlas材質を作ります。</summary>
        private void InitializeDetail(ComputeShader shader){
            if(shader==null){return;}
            if(this.m_HeightSource is not IProceduralSurfaceHeightSource source||!this.m_Material.HasProperty("_SurfaceNormalAtlas")){
                throw new InvalidOperationException("自然地形評価器と専用法線材質が必要です。");
            }
            this.detailAtlas=new SurfaceDetailAtlas(source,shader,this.m_Definition.BaseDataHash,
                this.m_Profile.NormalTileResolution,128,this.m_Profile.CpuBudgetBytes/8,this.m_Profile.GpuBudgetBytes/4);
            this.detailMaterial=new Material(this.m_Material){name=this.m_Material.name+" Runtime Detail",hideFlags=HideFlags.DontSave};
            this.detailMaterial.SetTexture("_SurfaceNormalAtlas",this.detailAtlas.Texture);
            this.detailMaterial.SetFloat("_SurfaceMicroSeed",this.m_Definition.Generation.Seed);
            this.detailMaterial.SetFloat("_SurfaceMicroStrength",(float)this.m_Definition.Generation.ReliefRatio);
            this.m_Material=this.detailMaterial;
        }

        private void SetDetailVisible(Node node,bool visible){
            if(this.detailAtlas==null||node.Key.Level<this.detailAtlas.Regions.Level){return;}
            if(node.Detail==null){node.Detail=new SurfaceDetailBinding(node.Mesh,node.Key,this.m_Definition.GridResolution,this.detailAtlas);}
            bool wasBound=node.Detail.IsBound;
            // UVだけの変更は登録済みMeshに反映され、BRGのbindingやBoundsは変えません。
            node.Detail.SetVisible(visible);
            if(visible){this.detailNodes.Add(node);}
            else{
                this.detailNodes.Remove(node);
                if(wasBound){this.detailBoundCount--;}
            }
        }

        /// <summary>LODと同じStopwatch予算内で入力を進め、公開するメッシュとGPU転送は各1件に制限します。</summary>
        private void UpdateDetailWork(){
            if(this.detailAtlas==null){return;}
            double end=Math.Min(this.updateTimer.Elapsed.TotalMilliseconds+0.25,
                this.m_Profile.UpdateBudgetMilliseconds-AtomicWorkReserveMilliseconds);
            foreach(Node node in this.detailNodes){
                if(this.updateTimer.Elapsed.TotalMilliseconds>=end){break;}
                if(node.Detail.TryPublish(Time.time)){
                    this.detailBoundCount++;
                    break;
                }
            }
            while(this.updateTimer.Elapsed.TotalMilliseconds<end&&this.HasWorkBudget()){
                if(this.detailAtlas.IsUploadPending){
                    if(this.updateTimer.Elapsed.TotalMilliseconds+AtomicWorkReserveMilliseconds<
                        this.m_Profile.UpdateBudgetMilliseconds){this.detailAtlas.Step();}
                    return;
                }
                if(!this.detailAtlas.Step()){return;}
            }
        }

        private void ReleaseDetail(Node node){
            if(node.Detail==null){return;}
            if(node.Detail.IsBound){this.detailBoundCount--;}
            node.Detail.Dispose();node.Detail=null;this.detailNodes.Remove(node);
        }
        private void ShutdownDetail(){
            this.detailNodes.Clear();this.detailBoundCount=0;
            this.detailAtlas?.Dispose();this.detailAtlas=null;
            if(this.detailMaterial!=null){DestroyOwned(this.detailMaterial);this.detailMaterial=null;}
        }
    }
}
