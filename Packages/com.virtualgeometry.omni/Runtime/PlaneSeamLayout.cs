using System;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// PlaneSeam デモの配置。Logical の原点を、左右の面の継ぎ目(x=0)に置く。両方の面は同じ高さ y=0(右は <c>PlaneSeamDemo</c> が δ だけ上下に動かせる)。
    /// 左: 単位の XZ 平面を一様に S 倍した 1 つの物体(単純な拡大)。右端が x=0、左の試験点は x=−EdgeMargin、面の原点は試験点から OriginDistance だけ左。
    /// 右: 一辺 RightExtent の <c>PlanarTerrainSource</c>。左の端が x=0(中心は x=RightExtent/2)、右の試験点は x=+EdgeMargin。
    /// </summary>
    [Serializable]
    public struct PlaneSeamLayout{
        /// <summary>左の試験点から、左の面の原点までの距離 D(m)。拡大した面の誤差は、D が大きいほど増える(目安は D·2^-24。内訳は未測定)。</summary>
        public double OriginDistance;
        /// <summary>試験点から継ぎ目までの距離(m)。左の試験点は x=−EdgeMargin、右の試験点は x=+EdgeMargin。</summary>
        public double EdgeMargin;
        /// <summary>右の面(<c>PlanarTerrainSource</c>)の一辺(m)。</summary>
        public double RightExtent;

        public static PlaneSeamLayout Default=>new PlaneSeamLayout{OriginDistance=1e6,EdgeMargin=3,RightExtent=33554432};

        /// <summary>左の面の一様な倍率 S。左の面は x∈[−2D−2m、0]、z∈±(D+m) を覆う(試験点は右端から m の所)。</summary>
        public double PlaneScale=>2*(this.OriginDistance+this.EdgeMargin);
        public double LeftOriginX=>-this.EdgeMargin-this.OriginDistance;
        public double LeftSpotX=>-this.EdgeMargin;
        public double RightSpotX=>this.EdgeMargin;
        /// <summary>右の面の中心(局所原点)の x。左端が x=0 になる。</summary>
        public double RightCenterX=>this.RightExtent*0.5;

        /// <summary>
        /// 左の試験点の、物体空間(単位の平面。座標は D/S)の float の刻み(m)= ulp(D/S)·S。
        /// 物体空間の float の座標が量子化される大きさで、拡大した面の深度・位置の誤差の目安になる(推測)。
        /// </summary>
        public double LeftQuantum(){
            float unit=(float)(this.OriginDistance/this.PlaneScale);
            float next=BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(unit)+1);
            return ((double)next-unit)*this.PlaneScale;
        }
    }
}
