using System;

namespace Omni.PlanetSurface{
    /// <summary>月面LODと検証で共有する、float丸め前のCube-sphere座標です。</summary>
    public static class SurfaceGeometry{
        /// <summary>面内の[-1,1]座標を天体ローカルの倍精度単位方向へ変換します。</summary>
        public static SurfaceVector CubeDirection(int face,double a,double b){
            SurfaceVector value=face switch{
                0=>new SurfaceVector(a,1,b),
                1=>new SurfaceVector(a,-1,b),
                2=>new SurfaceVector(1,a,b),
                3=>new SurfaceVector(-1,a,b),
                4=>new SurfaceVector(a,b,1),
                5=>new SurfaceVector(a,b,-1),
                _=>throw new ArgumentOutOfRangeException(nameof(face))
            };
            return value.Normalized;
        }
    }
}
