using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 実行時のノード(仮想オブジェクトの 1 つの実体、またはその番号の子)。定義はプレハブの <see cref="VirtualObject"/>(アセット)で、ノードはそれを読むだけ。
    /// 位置はルートの局所の Logical(<see cref="Origin"/>)。子は親の部分の原点に置かれ、親の向きと拡大率を受け継ぐ。
    /// </summary>
    public sealed class VirtualObjectNode{
        internal IVirtualObjectRepresentation[] Representations;
        internal CancellationTokenSource[] Cancellations;
        internal int[] LastRequestedFrame;
        internal float[] PrepareStartTime;
        internal List<VirtualObjectNode> ChildList;
        internal int HandoffCount;
        internal int HandoffTarget=-1;
        internal bool GroundResolved;
        internal Quaternion GroundedRotation;
        internal int ChildrenLastUsedFrame;

        public VirtualObjectNode(VirtualObject definition,VirtualObjectNode parent,int number,in LogicalVector3 origin,Quaternion rotation,float scale,uint seed){
            this.Definition=definition;this.Parent=parent;this.Number=number;this.Origin=origin;this.Rotation=rotation;this.Scale=scale;this.Seed=seed;
            this.GroundedRotation=rotation;
            int count=0;
            if(definition!=null&&definition.Bands!=null){count=definition.Bands.Length;}
            this.Representations=new IVirtualObjectRepresentation[count];
            this.Cancellations=new CancellationTokenSource[count];
            this.LastRequestedFrame=new int[count];
            this.PrepareStartTime=new float[count];
            if(parent!=null){this.Depth=parent.Depth+1;this.InstanceId=parent.InstanceId;}
        }

        /// <summary>定義(プレハブの VirtualObject)。</summary>
        public VirtualObject Definition{get;}
        public VirtualObjectNode Parent{get;}
        /// <summary>親の分け方の中の番号(ルートの実体は −1)。</summary>
        public int Number{get;}
        public int Depth{get;}
        /// <summary>ルートの実体の id(<see cref="VirtualObjectPlacementRecord.Id"/>)。子は親と同じ。</summary>
        public string InstanceId{get;internal set;}
        /// <summary>原点(ルートの局所、Logical)。地面に置いた後の値。</summary>
        public LogicalVector3 Origin{get;internal set;}
        /// <summary>記録の向き(ルートの局所。地面に置く種類では、地面の上向きを基準にした向き)。</summary>
        public Quaternion Rotation{get;}
        /// <summary>実際の向き(ルートの局所)。地面に置いた後は、地面の上向きに合わせた向き。</summary>
        public Quaternion EffectiveRotation=>this.GroundedRotation;
        public float Scale{get;}
        public uint Seed{get;}
        /// <summary>割り当ての層が渡したパラメータ。</summary>
        public Vector4 Parameters{get;internal set;}
        /// <summary>割り当てを決めた層の名前。</summary>
        public string AssignmentSource{get;internal set;}
        /// <summary>分け方ごとの状態(惑星の Cell の key など)。</summary>
        public object PartitionState{get;internal set;}

        /// <summary>境界の球の半径(拡大率を掛けた値)。</summary>
        public float Radius{
            get{
                if(this.Definition==null){return 0f;}
                return this.Definition.BoundingRadius*this.Scale;
            }
        }

        public RepresentationBand[] Bands{
            get{
                if(this.Definition==null){return System.Array.Empty<RepresentationBand>();}
                return this.Definition.Bands;
            }
        }

        /// <summary>この frame の画面上の大きさ(px、見る camera の最大)。</summary>
        public float Pixels{get;internal set;}
        /// <summary>先読みで予測した画面上の大きさ(px)。</summary>
        public float PredictedPixels{get;internal set;}
        /// <summary>見る camera までの最短の距離(m)。</summary>
        public float Distance{get;internal set;}
        /// <summary>望む帯(ヒステリシス付き)。</summary>
        public int DesiredBand{get;internal set;}=-1;
        /// <summary>先読みで予測した帯。</summary>
        public int PredictedBand{get;internal set;}=-1;
        /// <summary>いま見せている帯(−1 は何も見せていない)。</summary>
        public int ShownBand{get;internal set;}=-1;
        public int LastEvaluatedFrame{get;internal set;}

        /// <summary>展開した子(展開していなければ空)。</summary>
        public IReadOnlyList<VirtualObjectNode> Children{
            get{
                if(this.ChildList==null){return System.Array.Empty<VirtualObjectNode>();}
                return this.ChildList;
            }
        }

        /// <summary>帯の表現(作っていなければ null。"children" の帯は常に null)。</summary>
        public IVirtualObjectRepresentation RepresentationAt(int band){
            if(band<0||band>=this.Representations.Length){return null;}
            return this.Representations[band];
        }

        /// <summary>
        /// 画面に何かが出ているか(穴でないか)。見せている帯の表現が見えているか、子で見せているなら全部の子が覆われている。望む帯が「なし」なら覆われている扱い。
        /// </summary>
        public bool IsCovered{
            get{
                RepresentationBand[] bands=this.Bands;
                if(this.DesiredBand>=0&&this.DesiredBand<bands.Length&&bands[this.DesiredBand].IsNone){return true;}
                if(this.ShownBand<0||this.ShownBand>=bands.Length){return false;}
                if(bands[this.ShownBand].IsChildren){return this.AreChildrenCovered();}
                IVirtualObjectRepresentation representation=this.Representations[this.ShownBand];
                return representation!=null&&representation.IsVisible;
            }
        }

        internal bool AreChildrenCovered(){
            if(this.ChildList==null){return false;}
            for(int i=0;i<this.ChildList.Count;i++){
                if(!this.ChildList[i].IsCovered){return false;}
            }
            return true;
        }

        /// <summary>表示用の名前(id と番号の並び)。</summary>
        public string Path{
            get{
                if(this.Parent==null){return this.InstanceId;}
                return this.Parent.Path+"/"+this.Number;
            }
        }

        public override string ToString(){
            string id="(none)";
            if(this.Definition!=null){id=this.Definition.Id;}
            return this.Path+" ["+id+"]";
        }
    }
}
