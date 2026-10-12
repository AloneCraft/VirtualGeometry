using System;
using UnityEngine;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 仮想オブジェクトの実体を 1 つ置く記録。位置はルートの局所(m)で、10 進の文字列で書く(double を通さず Logical にする)。
    /// </summary>
    [Serializable]
    public struct VirtualObjectPlacementRecord{
        /// <summary>実体の id(セットの中で一意)。</summary>
        public string Id;
        /// <summary>種類(VirtualObject を付けたプレハブ)。</summary>
        public VirtualObject Prefab;
        public string X;
        public string Y;
        public string Z;
        /// <summary>向き(ルートの局所)。地面に置く種類では、地面の上向きを基準にした向き(上向きの周りの回転と、傾き)。</summary>
        public Quaternion Rotation;
        public float Scale;
        public uint Seed;

        public bool TryGetPosition(out LogicalVector3 position){
            position=default;
            if(!LogicalScalar.TryParse(this.X,out LogicalScalar x)||!LogicalScalar.TryParse(this.Y,out LogicalScalar y)||!LogicalScalar.TryParse(this.Z,out LogicalScalar z)){return false;}
            position=new LogicalVector3(x,y,z);
            return true;
        }
    }

    /// <summary>仮想オブジェクトの実体の一覧(都市、施設の配置など)。<see cref="VirtualObjectSystem.Sets"/> に入れる。</summary>
    [CreateAssetMenu(menuName="Virtual Object/Placement Set",fileName="VirtualObjectSet")]
    public sealed class VirtualObjectSet:ScriptableObject{
        public VirtualObjectPlacementRecord[] Placements=Array.Empty<VirtualObjectPlacementRecord>();
    }
}
