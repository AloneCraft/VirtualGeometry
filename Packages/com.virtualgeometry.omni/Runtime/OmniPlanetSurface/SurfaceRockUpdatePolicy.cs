using System;

namespace Omni.PlanetSurface{
    /// <summary>歩行中の生成を完了させつつ、急移動で不要になった近傍だけを取り消します。</summary>
    public static class SurfaceRockUpdatePolicy{
        public static bool ShouldRestart(bool hasRequest,bool isWorking,double distanceMeters,double drawDistanceMeters){
            if(!double.IsFinite(distanceMeters)||distanceMeters<0||!double.IsFinite(drawDistanceMeters)||drawDistanceMeters<=0){
                throw new ArgumentOutOfRangeException(nameof(distanceMeters));
            }
            if(!hasRequest||distanceMeters>drawDistanceMeters*0.5){return true;}
            return !isWorking&&distanceMeters>4;
        }
    }
}
