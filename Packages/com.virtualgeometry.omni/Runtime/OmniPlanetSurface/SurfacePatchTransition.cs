using System;
using System.Collections.Generic;
using UnityEngine;

namespace Omni.PlanetSurface{
    /// <summary>惑星表面の分割・統合で変わる子と隣接辺を、共通時刻で補間するための準備処理です。</summary>
    public sealed class SurfacePatchTransition{
        /// <summary>公開時に更新するメッシュと補間差分です。Reverseは統合される子を表します。</summary>
        public sealed class Patch{
            public SurfacePatchBuilder Geometry{get;internal set;}
            public SurfacePatchMorph Morph{get;internal set;}
            public bool Reverse{get;internal set;}
            internal bool Child;
            internal Vector3[] Positions,Normals;
        }
        private readonly SurfacePatchBuilder parent;
        private readonly SurfacePatchBuilder[] children;
        private readonly Func<SurfaceTileKey,SurfacePatchBuilder> lookup;
        private readonly List<Patch> patches=new();
        private readonly HashSet<SurfaceTileKey> keys=new();
        private readonly bool merge;
        private readonly int rebuildCount;
        private readonly SurfacePatchTransitionWorkspace workspace;
        private int phase,patchCursor,vertexCursor,edgeCursor;
        private bool released;
        public SurfaceLodTopology FutureTopology{get;}
        public IReadOnlyList<Patch> Patches=>this.patches;
        public bool IsComplete=>this.phase==4;
        public int CompletedMorphVertices{get;private set;}
        public string LastStepStage{get;private set;}="idle";

        /// <summary>表示中のトポロジーと直接の親子、既存パッチの参照関数を渡します。</summary>
        public SurfacePatchTransition(SurfaceLodTopology topology,SurfacePatchBuilder parent,
            SurfacePatchBuilder[] children,Func<SurfaceTileKey,SurfacePatchBuilder> lookup,bool merge):
            this(topology,parent,children,lookup,merge,CreateWorkspace(parent)){}

        /// <summary>実行時は共有ワークスペースを渡し、遷移間で大きな配列を再利用します。</summary>
        public SurfacePatchTransition(SurfaceLodTopology topology,SurfacePatchBuilder parent,
            SurfacePatchBuilder[] children,Func<SurfaceTileKey,SurfacePatchBuilder> lookup,bool merge,
            SurfacePatchTransitionWorkspace workspace){
            if(topology==null||parent==null||!parent.IsComplete||children==null||children.Length!=4||lookup==null){
                throw new ArgumentException("表示トポロジーと完成済み親子パッチが必要です。");
            }
            if(workspace==null||workspace.VertexCount!=parent.Vertices.Length){
                throw new ArgumentException("パッチ頂点数に対応するLOD遷移ワークスペースが必要です。",nameof(workspace));
            }
            for(int i=0;i<4;i++){
                if(children[i]==null||!children[i].IsComplete||!children[i].Key.Equals(parent.Key.Child(i%2,i/2))||
                    children[i].Resolution!=parent.Resolution){throw new ArgumentException("直接の4子が必要です。");}
            }
            this.parent=parent;this.children=children;this.lookup=lookup;this.merge=merge;this.workspace=workspace;
            this.FutureTopology=topology.Copy();
            bool valid;
            if(merge){valid=this.FutureTopology.TryMerge(parent.Key);}
            else{valid=this.FutureTopology.TrySplit(parent.Key);}
            if(!valid){throw new InvalidOperationException("隣接契約を守れないLOD遷移です。");}

            if(merge){this.Add(parent.Key);}
            else{foreach(var child in children){this.Add(child.Key);}}
            int changed=this.patches.Count;
            for(int i=0;i<changed;i++){
                for(int edge=0;edge<4;edge++){
                    for(int segment=0;segment<4;segment++){
                        this.Add(this.FutureTopology.Neighbour(this.patches[i].Geometry.Key,edge,segment));
                    }
                }
            }
            // 粗辺を参照する細葉への変形だけを伝播させ、無関係な全パッチは更新しません。
            for(int i=0;i<this.patches.Count;i++){
                SurfaceTileKey key=this.patches[i].Geometry.Key;
                for(int edge=0;edge<4;edge++){
                    for(int segment=0;segment<4;segment++){
                        SurfaceTileKey next=this.FutureTopology.Neighbour(key,edge,segment);
                        if(next.Level>key.Level){this.Add(next);}
                    }
                }
            }
            this.patches.Sort((a,b)=>a.Geometry.Key.Level.CompareTo(b.Geometry.Key.Level));
            this.rebuildCount=this.patches.Count;
            if(merge){
                foreach(var child in children){this.Append(new Patch{Geometry=child,Child=true,Reverse=true});}
            }
        }

        /// <summary>1頂点の保存/補間、1パッチの復元、または1辺の接続だけを進めます。</summary>
        public bool Step(){
            if(this.released){throw new InvalidOperationException("解放済みのLOD遷移は再開できません。");}
            if(this.IsComplete){this.LastStepStage="complete";return false;}
            if(this.phase==0){
                if(this.patchCursor==this.patches.Count){this.LastStepStage="phase";this.NextPhase();return true;}
                Patch patch=this.patches[this.patchCursor];
                if(patch.Child||ReferenceEquals(patch.Geometry,this.parent)){
                    this.LastStepStage="snapshot-skip";this.patchCursor++;return true;
                }
                if(patch.Positions==null){
                    this.LastStepStage="snapshot-allocate";
                    patch.Positions=this.workspace.RentVector3();
                    try{patch.Normals=this.workspace.RentVector3();}
                    catch{this.workspace.Return(patch.Positions);patch.Positions=null;throw;}
                }else{this.LastStepStage="snapshot-copy";}
                patch.Positions[this.vertexCursor]=patch.Geometry.Vertices[this.vertexCursor];
                patch.Normals[this.vertexCursor]=patch.Geometry.Normals[this.vertexCursor];
                this.vertexCursor++;
                if(this.vertexCursor==patch.Positions.Length){this.patchCursor++;this.vertexCursor=0;}
                return true;
            }
            if(this.phase==1){
                if(this.patchCursor==this.rebuildCount){this.LastStepStage="phase";this.NextPhase();return true;}
                this.LastStepStage="restore-edges";
                this.patches[this.patchCursor++].Geometry.RestoreEdges();return true;
            }
            if(this.phase==2){
                if(this.patchCursor==this.rebuildCount){this.LastStepStage="phase";this.NextPhase();return true;}
                SurfacePatchBuilder patch=this.patches[this.patchCursor].Geometry;
                SurfaceTileKey neighbour=this.FutureTopology.Neighbour(patch.Key,this.edgeCursor,0);
                this.LastStepStage="stitch-skip";
                if(neighbour.Level<patch.Key.Level){
                    this.LastStepStage="stitch-edge";SurfacePatchSampler.StitchEdge(patch,this.Resolve(neighbour),this.edgeCursor);
                }
                this.edgeCursor++;
                if(this.edgeCursor==4){this.edgeCursor=0;this.patchCursor++;}
                return true;
            }
            if(this.patchCursor==this.patches.Count){this.LastStepStage="phase";this.NextPhase();return true;}
            Patch current=this.patches[this.patchCursor];
            if(this.merge&&ReferenceEquals(current.Geometry,this.parent)){
                this.LastStepStage="morph-skip";this.patchCursor++;return true;
            }
            if(current.Morph==null){
                this.LastStepStage="morph-create";
                Vector4[] positionDelta=this.workspace.RentVector4();
                Vector4[] normalDelta=null;
                try{
                    normalDelta=this.workspace.RentVector4();
                    if(current.Child){current.Morph=new SurfacePatchMorph(this.parent,current.Geometry,positionDelta,normalDelta);}
                    else{current.Morph=new SurfacePatchMorph(current.Geometry,current.Positions,current.Normals,
                        positionDelta,normalDelta);}
                }catch{
                    if(normalDelta!=null){this.workspace.Return(normalDelta);}
                    this.workspace.Return(positionDelta);throw;
                }
            }else{this.LastStepStage="morph-step";}
            if(current.Morph.Step()){this.CompletedMorphVertices++;}
            if(current.Morph.IsComplete){
                this.ReturnSnapshots(current);this.patchCursor++;
            }
            return true;
        }

        /// <summary>GPUへのアップロード終了後、またはキャンセル時に全作業領域を返します。再呼出しは安全です。</summary>
        public void Release(){
            if(this.released){return;}
            foreach(Patch patch in this.patches){
                this.ReturnSnapshots(patch);
                if(patch.Morph!=null){
                    this.workspace.Return(patch.Morph.PositionDelta);
                    this.workspace.Return(patch.Morph.NormalDelta);
                    patch.Morph=null;
                }
            }
            this.released=true;
        }

        private void NextPhase(){this.phase++;this.patchCursor=0;this.vertexCursor=0;this.edgeCursor=0;}
        private SurfacePatchBuilder Resolve(SurfaceTileKey key){
            if(key.Equals(this.parent.Key)){return this.parent;}
            foreach(var child in this.children){if(key.Equals(child.Key)){return child;}}
            return this.lookup(key);
        }
        private void Add(SurfaceTileKey key){
            if(!this.keys.Add(key)){return;}
            SurfacePatchBuilder geometry=this.Resolve(key);
            if(geometry==null||!geometry.IsComplete||!geometry.Key.Equals(key)){
                throw new InvalidOperationException("遷移対象の生成済みパッチがありません。");
            }
            bool child=false;
            foreach(var candidate in this.children){if(ReferenceEquals(geometry,candidate)){child=true;break;}}
            this.Append(new Patch{Geometry=geometry,Child=child});
        }
        private void Append(Patch patch){
            if(this.patches.Count>=this.workspace.MaximumPatchCount){
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"LOD遷移対象が予約済みの最大{this.workspace.MaximumPatchCount}パッチを超えました。"));
            }
            this.patches.Add(patch);
        }
        private void ReturnSnapshots(Patch patch){
            if(patch.Positions!=null){this.workspace.Return(patch.Positions);patch.Positions=null;}
            if(patch.Normals!=null){this.workspace.Return(patch.Normals);patch.Normals=null;}
        }
        private static SurfacePatchTransitionWorkspace CreateWorkspace(SurfacePatchBuilder parent){
            return new SurfacePatchTransitionWorkspace(parent?.Vertices?.Length??1);
        }
    }
}
