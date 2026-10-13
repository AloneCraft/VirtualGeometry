using System;
using Unity.Mathematics;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// MoonTriple デモの配置。同じ半径 R の 3 つの月を y 軸(上向き)に積み、隣どうしを 1 点で接触させる(隙間 Gap で離せる)。
    /// 下から、普通の月(単位球のメッシュを直径 2R に拡大した 1 つの物体)、VG の月 A、VG の月 B。普通の月は回転なしで、メッシュの極(+y の頂点)が接触点 0 になる
    /// (メッシュは辺が約 21 km の多面体なので、接触点が頂点でないと面が球より数十 m 内側に入る)。
    /// 接触点 0(普通 - VG A)は、普通の月の上端と A の下端の中間。接触点 1(VG A - VG B)は、A の上端と B の下端の中間。
    /// 各月の中心は Logical で置く(座標は y だけが 0 でない)。接触点では 2 つの面が接平面(y 一定)に接する。
    /// </summary>
    [Serializable]
    public struct MoonTripleLayout{
        /// <summary>月の半径 R(m)。</summary>
        public double Radius;
        /// <summary>普通の月と VG の月 A の隙間(m)。0 で 1 点接触、負なら食い込む。</summary>
        public double Gap01;
        /// <summary>VG の月 A と B の隙間(m)。</summary>
        public double Gap12;

        /// <summary>月の半径(1,737.4 km)、隙間なし。</summary>
        public static MoonTripleLayout Default=>new MoonTripleLayout{Radius=1737400,Gap01=0,Gap12=0};

        /// <summary>サッカーボールの半径(m)。</summary>
        public const double BallRadius=0.11;

        /// <summary>原子サイズの VG の球の半径(m)。巨大な VG の月 A(半径 1,737.4 km)と同じ処理で描くための物。</summary>
        public const double AtomRadius=1e-10;

        /// <summary>
        /// 原子の中心(接触点 0 のすぐ近く、x = z = 0.5 m)。原点の近くに置くのは、camera の位置(倍精度)の刻みを原子の大きさより十分小さくするため
        /// (座標が 1 m 前後なら刻み 1e-16 m、1,500 m だと 2e-13 m)。この位置の、普通の月と VG の月 A の隙間は約 3e-7 m で、原子(2e-10 m)より十分広い。
        /// y は接触点 0 の隙間の中央(<see cref="ContactY"/>)なので、隙間を変えても、原子は 2 つの月の間にある。
        /// </summary>
        public double3 AtomCenter=>new double3(0.5,this.ContactY(0),0.5);

        public double CenterPlain=>-this.Radius;
        public double CenterA=>this.Radius+this.Gap01;
        public double CenterB=>3*this.Radius+this.Gap01+this.Gap12;
        /// <summary>接触点 0、1 の y(m)。</summary>
        public double ContactY(int index){
            if(index==0){return this.Gap01*0.5;}
            return this.Gap01+2*this.Radius+this.Gap12*0.5;
        }
        public double Gap(int index){
            if(index==0){return this.Gap01;}
            return this.Gap12;
        }

        // 接触点から横に distance(m)離れた所で、球の面が接平面(y 一定)から離れる量(m)= R − √(R²−d²)。
        private double Sag(double distance){
            double r=this.Radius;
            return r-Math.Sqrt(Math.Max(0.0,r*r-distance*distance));
        }

        /// <summary>接触点 index の下の月の上面の y(m)。理想の球で、接触点から横に distance(m)離れた所。</summary>
        public double LowerSurfaceY(int index,double distance){
            return this.ContactY(index)-this.Gap(index)*0.5-this.Sag(distance);
        }

        /// <summary>接触点 index の上の月の下面の y(m)。理想の球で、接触点から横に distance(m)離れた所。</summary>
        public double UpperSurfaceY(int index,double distance){
            return this.ContactY(index)+this.Gap(index)*0.5+this.Sag(distance);
        }

        /// <summary>
        /// 接触点から接平面(y 一定)に沿って距離 d(m)離れた所の、2 つの球面の間の幅(m)= 隙間 + 2·(R − √(R²−d²))。球面は接触点で y 軸に垂直。
        /// </summary>
        public double WidthAt(int index,double distance){
            double r=this.Radius;
            double sag=r-Math.Sqrt(Math.Max(0.0,r*r-distance*distance));
            return this.Gap(index)+2*sag;
        }

        /// <summary>
        /// 幅が width(m)になる、接触点からの距離(m)。width が隙間より小さければ 0(その幅は接触点でもう超えている)。
        /// 直径 2·BallRadius を渡すと、ボールが両側の面にちょうど接する(ボールの中心が接平面の上にあるとき)距離になる。
        /// </summary>
        public double DistanceForWidth(int index,double width){
            double sag=(width-this.Gap(index))*0.5;
            if(sag<=0){return 0;}
            return Math.Sqrt(2*this.Radius*sag-sag*sag);
        }

        /// <summary>ボールが両側の面にちょうど接する、接触点からの距離(m)。</summary>
        public double TouchDistance(int index){
            return this.DistanceForWidth(index,2*BallRadius);
        }
    }
}
