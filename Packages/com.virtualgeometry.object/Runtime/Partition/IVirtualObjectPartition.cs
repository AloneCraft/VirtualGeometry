using UnityEngine;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// ノードの中の、番号付きの部分の 1 つ。原点はノードの原点からの相対位置(ノードの局所の向き、ノードの拡大率を掛ける前)で、Logical で持つ。
    /// 半径は、部分の境界の球の半径(拡大率を掛ける前)。
    /// </summary>
    public readonly struct VirtualObjectPart{
        public readonly LogicalVector3 Origin;
        public readonly Vector3 ApproximateOrigin;
        public readonly float Radius;
        /// <summary>部分の外向きの向き(無ければ 0)。子の向きに使う。</summary>
        public readonly Vector3 Normal;

        public VirtualObjectPart(in LogicalVector3 origin,Vector3 approximateOrigin,float radius,Vector3 normal){
            this.Origin=origin;this.ApproximateOrigin=approximateOrigin;this.Radius=radius;this.Normal=normal;
        }
    }

    /// <summary>
    /// ノードの中を番号付きの部分に分ける方法(空間の分け方)。番号ごとの子は、割り当て規則(<see cref="IChildAssignmentRule"/>)で型を選ぶ。
    /// 組み込み: メッシュの cluster(<see cref="MeshClusterPartition"/>)、惑星の Cell(<see cref="PlanetCellPartition"/>)。
    /// 八分木(ダンジョン、洞窟、宇宙の領域)と、部屋と通路のグラフ(portal の可視判定)は、この interface で後から足す。
    /// </summary>
    public interface IVirtualObjectPartition{
        /// <summary>部分の数(番号は 0..数−1)。</summary>
        int GetPartCount(VirtualObjectNode node);
        bool TryGetPart(VirtualObjectNode node,int number,out VirtualObjectPart part);
        /// <summary>番号の子のノードに持たせる、分け方ごとの状態(惑星の Cell の key など。無ければ null)。</summary>
        object CreateChildState(VirtualObjectNode node,int number);
        /// <summary>番号の部分が、見る位置から見えるか(遮蔽、地平線など。分け方が判断できないときは true)。position はノードの局所の位置(拡大率を掛ける前)。</summary>
        bool IsPartVisible(VirtualObjectNode node,int number,Vector3 viewerLocal);
    }
}
