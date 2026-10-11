using System;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// PlaneSink デモの配置。Logical の原点を、左右の面の継ぎ目(x=0)に置く。
    /// 左: 単位の XZ 平面を一様に S 倍した 1 つの物体(単純な拡大)。高さ y=StepHeight、回転なし、右端が x=0。
    /// 右: 半径 R の <c>PlanetTerrainSource</c>(高さ 0)の上端。中心 (EdgeMargin, −R, 0)、上端の試験点 (EdgeMargin, 0, 0)。
    /// 左の面は右より StepHeight だけ高いので、右の球は左の面の下のどこでも StepHeight 以上低く、重ならない。
    /// </summary>
    [Serializable]
    public struct PlaneSinkLayout{
        /// <summary>左の試験点から、左の面の原点までの距離 D(m)。拡大した面の誤差は、D が大きいほど増える(目安は D·2^-24。内訳は未測定。Requirements/PlanetWorld.md §20)。</summary>
        public double OriginDistance;
        /// <summary>試験点から継ぎ目までの距離(m)。左の試験点は x=−EdgeMargin、右の試験点は x=+EdgeMargin。</summary>
        public double EdgeMargin;
        /// <summary>左の面の高さ(右の面の上端より高い分、m)。</summary>
        public double StepHeight;
        /// <summary>右の天体の半径 R(m)。</summary>
        public double RightRadius;

        public static PlaneSinkLayout Default=>new PlaneSinkLayout{OriginDistance=1e6,EdgeMargin=3,StepHeight=1,RightRadius=2e6};

        /// <summary>左の面の一様な倍率 S。左の面は x∈[−2D−2m、0]、z∈±(D+m) を覆う(試験点は右端から m の所)。</summary>
        public double PlaneScale=>2*(this.OriginDistance+this.EdgeMargin);
        public double LeftOriginX=>-this.EdgeMargin-this.OriginDistance;
        public double LeftSpotX=>-this.EdgeMargin;
        public double RightSpotX=>this.EdgeMargin;

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
