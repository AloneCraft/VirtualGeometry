using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Omni.PlanetSurface{
    public sealed partial class PlanetSurfaceLod{
        /// <summary>全子が完成するまで親を表示し続けるトランザクションです。</summary>
        private sealed class PendingSplit{
            internal Node Parent;
            internal readonly Node[] Children=new Node[4];
            internal SurfacePatchBuilder Builder;
            internal int ChildIndex;
        }

        private SurfaceLodTopology topology;
        private PendingSplit pendingSplit;
        private readonly Queue<Node> retiredNodes=new();
        private readonly Stopwatch updateTimer=new();
        // Mesh公開、BRG更新、GPU転送など打ち切れない原子操作の実測上限を先に予約します。
        // Edge WebGPUの実測で公開後の診断まで最大1.325msを要したため、
        // 2ms標準では0.60msまで準備を進め、残り1.40msを原子公開と診断へ残します。
        private const double AtomicWorkReserveMilliseconds=1.40;
        private Node splitCandidate,mergeCandidate;
        private double splitScore,mergeScore;
        private double pixelScale;
        private SurfaceVector selectionViewer;
        private double selectionAltitude;
        public int PreparedStepsDuringMorph{get;private set;}
        public double LastDetailWorkMilliseconds{get;private set;}
        public double LastViewerWorkMilliseconds{get;private set;}
        public double LastTerrainWorkMilliseconds{get;private set;}
        public double LastDiagnosticWorkMilliseconds{get;private set;}
        public string LastTerrainWorkStage{get;private set;}="idle";
        /// <summary>
        /// trueの間は、分割・統合の形状補間をせずにすぐ公開し、更新予算が残る限り同じフレームで次の作業を続けます(opt-in、既定false)。
        /// 画面を覆った準備中など、表示の滑らかさより細分化の速さを優先する間だけ使います。falseでは1フレーム1段・補間ありの従来どおりです。
        /// </summary>
        public bool FastRefinement{get;set;}

        /// <summary>専用月面シーンとEarthToMoonの双方から使用する、予算付きLOD更新です。</summary>
        private void UpdateIncremental(){
            if(!this.m_Initialized){return;}
            this.updateTimer.Restart();
            this.LastRockWorkMilliseconds=0;this.LastDetailWorkMilliseconds=0;
            this.LastViewerWorkMilliseconds=0;this.LastTerrainWorkMilliseconds=0;this.LastDiagnosticWorkMilliseconds=0;
            this.LastTerrainWorkStage="idle";
            this.LastTransitionStepMilliseconds=0;this.LastTransitionStepStage="idle";
            try{
                if(!this.TryGetViewerLocal(out SurfaceVector viewer)){return;}
                SurfaceVector direction=viewer.Normalized;
                double height=this.m_HeightSource.SampleHeightMeters(direction.X,direction.Y,direction.Z);
                double altitude=Math.Max(0,viewer.Length-this.m_Definition.RadiusMeters-height);
                int desired=this.DesiredLevel(altitude);
                this.selectionViewer=viewer;this.selectionAltitude=altitude;
                double pixels=1080,fov=55;
                if(this.m_Camera!=null){pixels=Math.Max(1,this.m_Camera.pixelHeight);fov=this.m_Camera.fieldOfView;}
                this.pixelScale=pixels/(2*Math.Tan(fov*Math.PI/360));
                // 岩の完成メッシュを転送したフレームには地形の転送を重ねません。
                double rockStart=this.updateTimer.Elapsed.TotalMilliseconds;
                this.LastViewerWorkMilliseconds=rockStart;
                bool rockPublished=this.UpdateRockWork(direction,altitude);
                this.LastRockWorkMilliseconds=this.updateTimer.Elapsed.TotalMilliseconds-rockStart;
                if(rockPublished){
                    this.MeasureUpdateDiagnostics(altitude,desired);return;
                }
                double detailStart=this.updateTimer.Elapsed.TotalMilliseconds;
                this.UpdateDetailWork();
                this.LastDetailWorkMilliseconds=this.updateTimer.Elapsed.TotalMilliseconds-detailStart;

                // 解放も1個ずつ行い、統合した4子の破棄を同じフレームへ集中させません。
                double terrainStart=this.updateTimer.Elapsed.TotalMilliseconds;
                bool progressed=this.AdvanceTerrain(desired);
                while(this.FastRefinement&&progressed&&this.HasWorkBudget()){progressed=this.AdvanceTerrain(desired);}
                this.LastTerrainWorkMilliseconds=this.updateTimer.Elapsed.TotalMilliseconds-terrainStart;
                this.MeasureUpdateDiagnostics(altitude,desired);
            }finally{
                this.updateTimer.Stop();
                this.LastUpdateMilliseconds=this.updateTimer.Elapsed.TotalMilliseconds;
                this.UpdateMemoryPeak();
            }
        }

        /// <summary>
        /// 地形の作業を1段進めます(解放、遷移、分割、選択の順)。戻り値は、補間の完了待ち以外の作業をしたかどうかで、
        /// FastRefinementのときだけ同じフレームで繰り返す判断に使います。
        /// </summary>
        private bool AdvanceTerrain(int desired){
            if(this.retiredNodes.Count>0){this.LastTerrainWorkStage="retire";this.DestroyNode(this.retiredNodes.Dequeue());return true;}
            if(this.pendingTransition!=null){
                this.LastTerrainWorkStage="transition";
                bool waiting=this.pendingTransition.Published;
                this.AdvanceTransition();
                // GPUが既に公開済みデータを補間している間、次の子のCPU生成だけを先行します。
                // 次の境界変更と公開は、前の補間が終わるまで行いません。
                if(this.pendingTransition!=null&&this.pendingTransition.Published&&!this.pendingTransition.Merge&&this.HasWorkBudget()){
                    if(this.pendingSplit==null){this.SelectNextWork(desired,false);}
                    else{this.AdvanceSplit();}
                }
                return !waiting;
            }
            if(this.pendingSplit!=null){this.LastTerrainWorkStage="split";this.AdvanceSplit();return true;}
            if(this.HasWorkBudget()){
                this.LastTerrainWorkStage="select";this.SelectNextWork(desired,true);
                return this.pendingSplit!=null||this.pendingTransition!=null;
            }
            return false;
        }

        /// <summary>診断自体の走査も更新予算に含め、同じフレームの内訳として公開します。</summary>
        private void MeasureUpdateDiagnostics(double altitude,int desired){
            double started=this.updateTimer.Elapsed.TotalMilliseconds;
            this.UpdateDiagnostics(altitude,desired);
            this.LastDiagnosticWorkMilliseconds=this.updateTimer.Elapsed.TotalMilliseconds-started;
        }

        private bool HasWorkBudget()=>this.updateTimer.Elapsed.TotalMilliseconds<
            Math.Max(0.1,this.m_Profile.UpdateBudgetMilliseconds-AtomicWorkReserveMilliseconds);

        private void SelectNextWork(int desired,bool allowMerge){
            this.splitCandidate=null;this.mergeCandidate=null;
            this.splitScore=this.m_Profile.TargetCellPixels;
            this.mergeScore=this.m_Profile.TargetCellPixels*this.m_Definition.MergeHysteresis;
            foreach(Node root in this.m_Roots){this.SelectCandidates(root,desired);}
            if(this.mergeCandidate!=null){
                if(allowMerge){this.CommitMerge(this.mergeCandidate);}
            }else if(this.splitCandidate!=null&&this.m_LeafCount+3<=this.m_Profile.MaximumVisiblePatches){
                SurfaceTileKey dependency=this.topology.FirstSplitDependency(this.splitCandidate.Key);
                this.pendingSplit=new PendingSplit{Parent=this.FindNode(dependency)};
            }
        }

        private void AdvanceSplit(){
            PendingSplit work=this.pendingSplit;
            if(work.ChildIndex==4){
                if(this.pendingTransition==null){this.BeginTransition(work.Parent,work.Children,false);this.pendingSplit=null;}
                return;
            }
            // 1頂点単位で打ち切ります。生成途中の高さ・メッシュを描画へ公開しません。
            while(this.HasWorkBudget()){
                if(work.Builder==null){
                    int index=work.ChildIndex;
                    work.Builder=new SurfacePatchBuilder(work.Parent.Key.Child(index%2,index/2),
                        this.m_Definition.RadiusMeters,this.m_Definition.GridResolution,this.m_HeightSource,
                        this.m_Definition.SkirtDepthScale);
                    continue;
                }
                if(!work.Builder.IsComplete){
                    work.Builder.Step();
                    if(this.pendingTransition!=null){this.PreparedStepsDuringMorph++;}
                    continue;
                }
                if(this.updateTimer.Elapsed.TotalMilliseconds+AtomicWorkReserveMilliseconds>
                    this.m_Profile.UpdateBudgetMilliseconds){return;}
                SurfacePatchBuilder geometry=work.Builder;
                Node child=this.CreateNode(geometry.Key,geometry.CreateMesh(),geometry.Origin,false,geometry);
                work.Children[work.ChildIndex]=child;
                work.ChildIndex++;work.Builder=null;
                if(work.ChildIndex==4&&this.pendingTransition==null){
                    this.BeginTransition(work.Parent,work.Children,false);
                    this.pendingSplit=null;
                }
                // Unityオブジェクト生成とGPU転送は1フレームに1子だけに制限します。
                return;
            }
        }

        private void SelectCandidates(Node node,int desired){
            double score=this.ProjectedCellPixels(node);
            if(node.Children==null){
                if(node.Key.Level<desired&&score>this.splitScore){this.splitScore=score;this.splitCandidate=node;}
                return;
            }
            bool childrenAreLeaves=true;
            foreach(Node child in node.Children){if(child.Children!=null){childrenAreLeaves=false;}}
            if(childrenAreLeaves&&score<this.mergeScore&&this.topology.CanMerge(node.Key)){
                this.mergeScore=score;this.mergeCandidate=node;
            }
            foreach(Node child in node.Children){this.SelectCandidates(child,desired);}
        }

        private double ProjectedCellPixels(Node node){
            double span=2.0/(1<<node.Key.Level),radius=this.m_Definition.RadiusMeters;
            SurfaceVector direction=node.Origin.Normalized,viewerDirection=this.selectionViewer.Normalized;
            double dot=direction.X*viewerDirection.X+direction.Y*viewerDirection.Y+direction.Z*viewerDirection.Z;
            // 面をまたぐパッチを早期に消さない保守的な地平線判定です。
            if(dot<radius/Math.Max(radius,this.selectionViewer.Length)-span*1.5){return 0;}
            double patchRadius=radius*span*0.8;
            double distance=Math.Max(Math.Max(this.selectionAltitude,1),
                (this.selectionViewer-node.Origin).Length-patchRadius);
            return radius*span/this.m_Definition.GridResolution*this.pixelScale/distance;
        }

        private Node FindNode(SurfaceTileKey key){
            Node node=this.m_Roots[key.Face];
            while(node.Key.Level<key.Level){
                int shift=key.Level-node.Key.Level-1;
                node=node.Children[((key.Y>>shift)&1)*2+((key.X>>shift)&1)];
            }
            return node;
        }

        private void CommitMerge(Node parent){
            if(!this.topology.CanMerge(parent.Key)){return;}
            this.BeginTransition(parent,parent.Children,true);
        }

        private void CancelIncrementalWork(){
            this.CancelTransition();
            if(this.pendingSplit!=null){
                foreach(Node child in this.pendingSplit.Children){if(child!=null){this.DestroyNode(child);}}
                this.pendingSplit=null;
            }
            while(this.retiredNodes.Count>0){this.DestroyNode(this.retiredNodes.Dequeue());}
            this.topology=null;
        }
    }
}
