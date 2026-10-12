using System;
using UnityEngine;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>地面への置き方。</summary>
    public enum VirtualObjectGround{
        /// <summary>置いた位置のまま。</summary>
        None,
        /// <summary>足元の中心と四隅の地面の高さの最小値に置き、<see cref="VirtualObject.FoundationDepth"/> だけ沈める。</summary>
        FootprintMinimum,
    }

    /// <summary>
    /// 仮想オブジェクトの定義。プレハブの根に付ける(このプレハブが、ノードの型のアセット)。
    /// ノードは、画面上の大きさに応じて <see cref="Bands"/> から表現を選ぶ。"children" の帯では、<see cref="Partition"/> で分けた番号ごとに、
    /// <see cref="Assignment"/> の層が子の型(<see cref="ChildTemplates"/>、それ自身も VirtualObject を付けたプレハブ)を選ぶ。
    /// 子は「型 + 番号」で、アセットの数は型の数だけ(番号の数に比例しない)。例外(火山、海など)も層で選ぶ型の 1 つ。
    /// 実行時は、プレハブのこの部品を定義として読むだけで、プレハブそのものは置かない(表現が必要に応じて作る)。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VirtualObject:MonoBehaviour{
        [Tooltip("種類の id(Validator と JSON で使う)。")]
        public string Id="";
        [Tooltip("境界の球の中心(局所)。")]
        public Vector3 BoundsCenter;
        [Tooltip("境界の球の半径(m、局所)。画面上の大きさはこの半径を画面へ投影した値。")]
        [Min(0.001f)] public float BoundingRadius=1f;
        [Tooltip("足元の大きさ(m、局所の x と z)。地面の標本化に使う。")]
        public Vector2 Footprint=Vector2.one;
        public VirtualObjectGround Ground=VirtualObjectGround.None;
        [Tooltip("地面から沈める深さ(m)。")]
        [Min(0f)] public float FoundationDepth;
        [Tooltip("優先度の重み。")]
        [Min(0f)] public float Importance=1f;
        [Tooltip("表現の帯(粗い順。入口の px が単調に増える)。")]
        public RepresentationBand[] Bands=Array.Empty<RepresentationBand>();
        [Tooltip("\"children\" の帯で、番号付きの部分に分ける方法。")]
        [SerializeReference] public IVirtualObjectPartition Partition;
        [Tooltip("子の型(VirtualObject を付けたプレハブ)。数個程度。")]
        public VirtualObject[] ChildTemplates=Array.Empty<VirtualObject>();
        [Tooltip("割り当て規則の層(既定 → 場 → 領域 → 番号ごとの上書き の順に評価し、後の層が上書きする)。")]
        [SerializeReference] public IChildAssignmentRule[] Assignment=Array.Empty<IChildAssignmentRule>();

        /// <summary>"children" の帯があるか。</summary>
        public bool HasChildrenBand{
            get{
                for(int i=0;i<this.Bands.Length;i++){
                    if(this.Bands[i]!=null&&this.Bands[i].IsChildren){return true;}
                }
                return false;
            }
        }
    }
}
