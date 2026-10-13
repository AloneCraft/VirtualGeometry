namespace Omni.PlanetSurface
{
    /// <summary>
    /// 全ての方向で標高 0（基準半径の滑らかな球）を返す標高の正本です。
    /// 地形の起伏は持たず、分割した地面（<see cref="PlanetSurfaceLod"/>）の精度（パッチの中心からの差の頂点）だけを使いたい
    /// 天体に使います（例: CosmicScale の冒頭の月。見た目は画像と照明で描き、近くでも float の桁落ちで面が揺れない）。
    /// 頂点の法線は天体の局所の方向そのものになります（<see cref="PlanetSurfaceLod"/> の法線は標高の勾配から求めるため）。
    /// </summary>
    public sealed class ZeroSurfaceHeightSource : IPlanetSurfaceHeightSource
    {
        /// <summary>共有のインスタンス（状態を持たない）。</summary>
        public static readonly ZeroSurfaceHeightSource Instance = new ZeroSurfaceHeightSource();

        private ZeroSurfaceHeightSource()
        {
        }

        public double MaximumAbsoluteHeightMeters => 0.0;

        public double SampleHeightMeters(double unitX, double unitY, double unitZ) => 0.0;
    }
}
