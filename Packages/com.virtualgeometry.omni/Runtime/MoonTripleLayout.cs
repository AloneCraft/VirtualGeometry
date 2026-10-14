using System;
using Unity.Mathematics;
using VirtualGeometry.World;

namespace VirtualGeometry.Omni{
    /// <summary>MoonTriple の 4 枚の面。象限は、VG(−x,−z)、Standard100Km(−x,+z)、Standard1000Km(+x,+z)、手動分割(+x,−z)。</summary>
    public enum MoonTripleFace{Vg=0,Standard100Km=1,Standard1000Km=2,Manual=3}

    /// <summary>
    /// MoonTriple デモの配置。4 枚の平面を y=0 に置き、4 つの角を原点(x=0, z=0)で接する。各面は原点から外側へ広がる(PlaneSeam と同じ考え方)。
    /// 大きさは名前どおり: Standard100Km = 100 km、Standard1000Km = 1000 km、VG = 1000 km、手動分割 = 1000 m(10 m の正方形を 100×100 枚)。
    /// 10 m の基準の面に倍率 N を掛けて大きさを作る(Standard は 1 枚を N 倍、VG は一辺を N 倍して <see cref="PlanarTerrainSource"/> に渡す)。
    /// 手動分割の枚数(<see cref="TilesPerSide"/>)と倍率 N は、この構造体の値で変えられる。
    /// </summary>
    [Serializable]
    public struct MoonTripleLayout{
        /// <summary>基準の面の一辺(m)。Unity の標準 Plane と同じ 10 m。手動分割の 1 枚もこの大きさ。</summary>
        public double BaseMeters;
        /// <summary>Standard100Km の倍率 N(大きさ = <see cref="BaseMeters"/> × N)。</summary>
        public double ScaleStandard100Km;
        /// <summary>Standard1000Km の倍率 N。</summary>
        public double ScaleStandard1000Km;
        /// <summary>VG の倍率 N(一辺 = <see cref="BaseMeters"/> × N)。</summary>
        public double ScaleVg;
        /// <summary>手動分割の、縦横それぞれの枚数(10 m の面を TilesPerSide × TilesPerSide 枚並べる)。</summary>
        public int TilesPerSide;

        /// <summary>VG の Cell の目標の大きさ(m)。最大階層は、Cell がこれ以下になる最小の階層(自動)。</summary>
        public const double TargetCellMeters=10;

        /// <summary>既定: 10 m の基準、Standard100Km = 10^4、Standard1000Km = 10^5、VG = 10^5(一辺 1000 km)、手動分割 100×100 枚。</summary>
        public static MoonTripleLayout Default=>new MoonTripleLayout{BaseMeters=10,ScaleStandard100Km=1e4,ScaleStandard1000Km=1e5,ScaleVg=1e5,TilesPerSide=100};

        /// <summary>サッカーボールの半径(m)。</summary>
        public const double BallRadius=0.11;

        /// <summary>象限の符号 (sx, sz)。VG(−,−)、Standard100Km(−,+)、Standard1000Km(+,+)、手動分割(+,−)。</summary>
        public static double2 Quadrant(MoonTripleFace face){
            switch(face){
                case MoonTripleFace.Vg:return new double2(-1,-1);
                case MoonTripleFace.Standard100Km:return new double2(-1,1);
                case MoonTripleFace.Standard1000Km:return new double2(1,1);
                default:return new double2(1,-1);
            }
        }

        /// <summary>
        /// 位置 (x, z) の下にある面(象限で決める。面の広がりは見ない)。x=0 または z=0 の境界の上では、boundary を返す。
        /// </summary>
        public static MoonTripleFace FaceAt(double x,double z,MoonTripleFace boundary){
            if(x==0||z==0){return boundary;}
            for(int f=0;f<4;f++){
                double2 quadrant=Quadrant((MoonTripleFace)f);
                if(Math.Sign(x)==quadrant.x&&Math.Sign(z)==quadrant.y){return (MoonTripleFace)f;}
            }
            return boundary;
        }

        /// <summary>面の一辺(m)。</summary>
        public double FaceSize(MoonTripleFace face){
            switch(face){
                case MoonTripleFace.Vg:return this.BaseMeters*this.ScaleVg;
                case MoonTripleFace.Standard100Km:return this.BaseMeters*this.ScaleStandard100Km;
                case MoonTripleFace.Standard1000Km:return this.BaseMeters*this.ScaleStandard1000Km;
                default:return this.BaseMeters*this.TilesPerSide;
            }
        }

        /// <summary>
        /// Standard の面の倍率 N(Unity の Plane の mesh を、Logical の倍率と物体空間の grid の倍率に使う)。VG と手動分割では使わない(0 を返す)。
        /// </summary>
        public double StandardScale(MoonTripleFace face){
            switch(face){
                case MoonTripleFace.Standard100Km:return this.ScaleStandard100Km;
                case MoonTripleFace.Standard1000Km:return this.ScaleStandard1000Km;
                default:return 0;
            }
        }

        /// <summary>面の中心(Logical の位置、y = 0)。角が原点に接するように、象限の側へ一辺の半分ずらす。</summary>
        public double3 FaceCenter(MoonTripleFace face){
            double2 q=Quadrant(face);
            double half=this.FaceSize(face)*0.5;
            return new double3(q.x*half,0,q.y*half);
        }

        /// <summary>手動分割の 1 枚の中心(i、j は 0 から TilesPerSide − 1)。角が原点に近いほど i、j が小さい。</summary>
        public double3 TileCenter(int i,int j){
            double2 q=Quadrant(MoonTripleFace.Manual);
            return new double3(q.x*this.BaseMeters*(i+0.5),0,q.y*this.BaseMeters*(j+0.5));
        }

        /// <summary>
        /// 面(または手動分割の 1 枚)の、原点に近い角の物体座標(mesh の座標系。±BaseMeters/2 の範囲)。
        /// Standard は、物体座標 p の点が世界の 中心 + p × N にあるので、原点の角は p = −q × BaseMeters/2(N によらない)。
        /// 格子の原点(RadialScaledPlane の _GridOriginOS)に使う。
        /// </summary>
        public double3 CornerInObject(MoonTripleFace face){
            double2 q=Quadrant(face);
            double half=this.BaseMeters*0.5;
            return new double3(-q.x*half,0,-q.y*half);
        }

        /// <summary>VG の Cell の最大階層: 一辺 ÷ 2^階層 が <see cref="TargetCellMeters"/> 以下になる最小の階層(自動)。</summary>
        public int VgMaximumLevel{
            get{
                double ratio=this.FaceSize(MoonTripleFace.Vg)/TargetCellMeters;
                int level=(int)Math.Ceiling(Math.Log(ratio,2));
                return Math.Max(0,Math.Min(PlanetLayerSource.GpuMaximumLevel,level));
            }
        }
    }
}
