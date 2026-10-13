using System;

namespace Omni.PlanetSurface{
    /// <summary>描画と同じ標高正本から、天体ローカル位置の最低半径を求めます。</summary>
    public static class SurfaceHeightContact{
        /// <summary>地表より上の位置はそのまま返します。位置・半径・余裕はメートルで指定します。</summary>
        public static SurfaceVector ClampRadius(SurfaceVector position,IPlanetSurfaceHeightSource source,
            double radius,double clearance){
            if(!double.IsFinite(clearance)||clearance<0){throw new ArgumentOutOfRangeException(nameof(clearance));}
            double currentClearance=GetClearanceMeters(position,source,radius);
            if(currentClearance>=clearance){return position;}
            return SetClearanceMeters(position,source,radius,clearance);
        }

        /// <summary>描画と同じ標高正本から、位置と地表との符号付き距離をメートルで返します。</summary>
        public static double GetClearanceMeters(SurfaceVector position,IPlanetSurfaceHeightSource source,double radius){
            if(source==null){throw new ArgumentNullException(nameof(source));}
            if(!double.IsFinite(radius)||radius<=0){throw new ArgumentOutOfRangeException(nameof(radius));}
            var direction=LocalSurfaceHeightOverride.NormalizeDirection(position);
            double surfaceRadius=radius+source.SampleHeightMeters(direction.X,direction.Y,direction.Z);
            if(!double.IsFinite(surfaceRadius)||surfaceRadius<=0){throw new InvalidOperationException("地表半径が不正です。");}
            return position.Length-surfaceRadius;
        }

        /// <summary>入力方向を維持し、描画と同じ地表から指定距離の位置へ投影します。</summary>
        public static SurfaceVector SetClearanceMeters(SurfaceVector position,IPlanetSurfaceHeightSource source,
            double radius,double clearance){
            if(!double.IsFinite(clearance)||clearance<0){throw new ArgumentOutOfRangeException(nameof(clearance));}
            var direction=LocalSurfaceHeightOverride.NormalizeDirection(position);
            double targetRadius=position.Length+clearance-GetClearanceMeters(position,source,radius);
            if(!double.IsFinite(targetRadius)||targetRadius<=0){throw new InvalidOperationException("地表半径が不正です。");}
            return direction*targetRadius;
        }
    }
}
