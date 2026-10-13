using System;
using Omni.BatchRendering;
using UnityEngine;

namespace Omni.PlanetSurface{
    public sealed partial class PlanetSurfaceLod{
        /// <summary>準備中は旧表示を維持し、共通時刻で子と隣接辺を変形させます。</summary>
        private sealed class PendingTransition{
            internal Node Parent;
            internal Node[] Children;
            internal SurfacePatchTransition Preparation;
            internal bool Merge,Published;
            internal float EndTime;
        }
        private PendingTransition pendingTransition;
        private SurfacePatchTransitionWorkspace transitionWorkspace;
        public int CompletedSplitTransitions{get;private set;}
        public int CompletedMergeTransitions{get;private set;}
        public bool IsTransitioning=>this.pendingTransition!=null;
        public double LastTransitionPublishMilliseconds{get;private set;}
        public double LastTransitionStepMilliseconds{get;private set;}
        public string LastTransitionStepStage{get;private set;}="idle";

        private void BeginTransition(Node parent,Node[] children,bool merge){
            var geometry=new SurfacePatchBuilder[4];
            for(int i=0;i<4;i++){geometry[i]=children[i].Geometry;}
            this.pendingTransition=new PendingTransition{
                Parent=parent,Children=children,Merge=merge,
                Preparation=new SurfacePatchTransition(this.topology,parent.Geometry,geometry,
                    key=>this.FindNode(key).Geometry,merge,this.transitionWorkspace)
            };
        }

        private void AdvanceTransition(){
            PendingTransition work=this.pendingTransition;
            if(work.Published){
                if(Time.time>=work.EndTime){this.FinishTransition(work);}
                return;
            }
            while(this.HasWorkBudget()&&!work.Preparation.IsComplete){
                double beforeStep=this.updateTimer.Elapsed.TotalMilliseconds;
                work.Preparation.Step();
                double stepMilliseconds=this.updateTimer.Elapsed.TotalMilliseconds-beforeStep;
                if(stepMilliseconds>this.LastTransitionStepMilliseconds){
                    this.LastTransitionStepMilliseconds=stepMilliseconds;
                    this.LastTransitionStepStage=work.Preparation.LastStepStage;
                }
            }
            if(!work.Preparation.IsComplete||
                this.updateTimer.Elapsed.TotalMilliseconds+AtomicWorkReserveMilliseconds>
                    this.m_Profile.UpdateBudgetMilliseconds){return;}

            double before=this.updateTimer.Elapsed.TotalMilliseconds;
            float start=Time.time,duration=(float)this.m_Profile.MorphDurationSeconds;
            // 旧EarthToMoon材質は互換表示を保ち、専用材質へ移行したときだけGPU補間を有効化します。
            if(!this.m_Material.HasProperty("_SurfaceMorphEnabled")||this.m_Material.GetFloat("_SurfaceMorphEnabled")<0.5f){
                duration=0;
            }
            // 速い細分化では補間せず、最終形状のまま公開します(補間時間0の端点をGPUへ渡さないため、補間自体を無効にします)。
            if(this.FastRefinement){duration=0;}
            foreach(SurfacePatchTransition.Patch patch in work.Preparation.Patches){
                Node node=this.ResolveTransitionNode(work,patch.Geometry.Key);
                node.Mesh.vertices=patch.Geometry.Vertices;
                node.Mesh.normals=patch.Geometry.Normals;
                node.Mesh.tangents=patch.Geometry.Tangents;
                if(patch.Morph!=null&&!this.FastRefinement){
                    float signed=duration;if(patch.Reverse){signed=-duration;}
                    patch.Morph.Upload(node.Mesh,start,signed);
                }else{
                    SurfacePatchBuilder.SetMorphDisabled(node.Mesh,patch.Geometry.Vertices.Length);
                    node.Mesh.RecalculateBounds();
                }
                // Mesh/SubMesh/材質は同じです。モーフ端点を含むBoundsだけをBRGへ通知します。
                RadialBatchRendererRegistry.MarkDirty(node.Handle,RadialBatchDirtyFlags.MeshVertexData);
            }
            if(!work.Merge){
                // 全メッシュの転送後だけ表示を交換します。4子は開始時点で親の実三角形と一致します。
                work.Parent.Children=work.Children;
                this.topology=work.Preparation.FutureTopology;
                foreach(Node child in work.Children){SetNodeVisible(child,true);}
                SetNodeVisible(work.Parent,false);this.m_LeafCount+=3;
            }
            work.EndTime=start+duration;work.Published=true;
            this.LastTransitionPublishMilliseconds=this.updateTimer.Elapsed.TotalMilliseconds-before;
            if(duration==0){this.FinishTransition(work);}
        }

        private Node ResolveTransitionNode(PendingTransition work,SurfaceTileKey key){
            if(key.Equals(work.Parent.Key)){return work.Parent;}
            foreach(Node child in work.Children){if(key.Equals(child.Key)){return child;}}
            return this.FindNode(key);
        }

        private void FinishTransition(PendingTransition work){
            if(work.Merge){
                // 子が親形状へ到達するまで、親の表示と4子の破棄を遅らせます。
                foreach(Node child in work.Children){SetNodeVisible(child,false);this.retiredNodes.Enqueue(child);}
                work.Parent.Children=null;this.topology=work.Preparation.FutureTopology;
                SetNodeVisible(work.Parent,true);this.m_LeafCount-=3;this.CompletedMergeTransitions++;
            }else{this.CompletedSplitTransitions++;}
            work.Preparation.Release();
            this.pendingTransition=null;
        }

        private void CancelTransition(){
            if(this.pendingTransition==null){return;}
            // 分割準備中の子だけはまだ木へ所属していません。それ以外は通常のShutdownが解放します。
            if(!this.pendingTransition.Merge&&!this.pendingTransition.Published){
                foreach(Node child in this.pendingTransition.Children){this.DestroyNode(child);}
            }
            this.pendingTransition.Preparation.Release();
            this.pendingTransition=null;
        }
    }
}
