using System;

namespace Omni.PlanetSurface
{
    /// <summary>
    /// 北から南へ並ぶfloat32正距円筒標高と、任意の局所標高を合成します。
    /// 月ではLOLA全球標高と0N/0EのSLDEM2015を同じ地理座標で評価します。
    /// </summary>
    public sealed class EquirectangularHeightSource : IPlanetSurfaceHeightSource
    {
        private readonly float[] m_Global;
        private readonly int m_GlobalWidth;
        private readonly int m_GlobalHeight;
        private readonly float[] m_Regional;
        private readonly int m_RegionalWidth;
        private readonly int m_RegionalHeight;
        private readonly double m_RegionalPixelsPerDegree;
        private readonly double m_RegionalHalfExtentMeters;
        private readonly double m_RegionalBlendMeters;
        private readonly double m_RadiusMeters;
        private readonly double m_AnchorElevationMeters;
        private readonly IPlanetSurfaceHeightSource m_NaturalSource;

        /// <summary>
        /// 全球標高と、0N/0Eを中心とする局所標高を構築します。
        /// </summary>
        /// <param name="global">北から南、0度経線中心のfloat32標高メートルです。</param>
        /// <param name="globalWidth">全球データの横画素数です。</param>
        /// <param name="globalHeight">全球データの縦画素数です。</param>
        /// <param name="regional">0N/0E周辺のfloat32標高メートルです。nullなら局所合成を行いません。</param>
        /// <param name="regionalWidth">局所データの横画素数です。</param>
        /// <param name="regionalHeight">局所データの縦画素数です。</param>
        /// <param name="regionalPixelsPerDegree">局所データのpixels/degreeです。</param>
        /// <param name="radiusMeters">標高ゼロの基準半径メートルです。</param>
        /// <param name="anchorElevationMeters">0N/0Eの実測標高メートルです。</param>
        public EquirectangularHeightSource(
            float[] global,
            int globalWidth,
            int globalHeight,
            float[] regional,
            int regionalWidth,
            int regionalHeight,
            double regionalPixelsPerDegree,
            double radiusMeters,
            double anchorElevationMeters,
            IPlanetSurfaceHeightSource naturalSource = null)
        {
            if (global == null || global.Length != globalWidth * globalHeight)
            {
                throw new ArgumentException("全球標高の寸法が不正です。", nameof(global));
            }

            if (regional != null && regional.Length != regionalWidth * regionalHeight)
            {
                throw new ArgumentException("局所標高の寸法が不正です。", nameof(regional));
            }

            m_Global = global;
            m_GlobalWidth = globalWidth;
            m_GlobalHeight = globalHeight;
            m_NaturalSource = naturalSource ?? new SphericalHeightGrid(global, globalWidth, globalHeight);
            m_Regional = regional;
            m_RegionalWidth = regionalWidth;
            m_RegionalHeight = regionalHeight;
            m_RegionalPixelsPerDegree = regionalPixelsPerDegree;
            m_RadiusMeters = radiusMeters;
            m_AnchorElevationMeters = anchorElevationMeters;
            m_RegionalHalfExtentMeters = 256.0;
            m_RegionalBlendMeters = 64.0;

            double maximum = Math.Abs(anchorElevationMeters) + 20000.0;
            for (int index = 0; index < global.Length; index++)
            {
                maximum = Math.Max(maximum, Math.Abs(global[index]));
            }

            MaximumAbsoluteHeightMeters = Math.Max(maximum, m_NaturalSource.MaximumAbsoluteHeightMeters);
        }

        /// <inheritdoc />
        public double MaximumAbsoluteHeightMeters { get; }

        /// <inheritdoc />
        public double SampleHeightMeters(double unitX, double unitY, double unitZ)
        {
            double longitude = Math.Atan2(unitX, unitY);
            double latitude = Math.Atan2(unitZ, Math.Sqrt(unitX * unitX + unitY * unitY));
            // 施設造成・局所実測は自然地形の外側の明示的な上書きです。
            double global = m_NaturalSource.SampleHeightMeters(unitX, unitY, unitZ);
            if (m_Regional == null)
            {
                return global;
            }

            double localX = longitude * m_RadiusMeters;
            double localZ = latitude * m_RadiusMeters;
            double edgeDistance = m_RegionalHalfExtentMeters -
                Math.Max(Math.Abs(localX), Math.Abs(localZ));
            if (edgeDistance <= 0.0)
            {
                return global;
            }

            double regional = SampleRegional(longitude, latitude);
            // 到着施設の直下は実測アンカー標高で造成し、周囲20mで観測形状へ戻します。
            double facilityDistance = Math.Max(Math.Abs(localX) - 35.0, Math.Abs(localZ) - 25.0);
            double facilityWeight = Smooth01(facilityDistance / 20.0);
            regional = Lerp(m_AnchorElevationMeters, regional, facilityWeight);
            double regionalWeight = Smooth01(edgeDistance / m_RegionalBlendMeters);
            return Lerp(global, regional, regionalWeight);
        }

        private double SampleGlobal(double longitude, double latitude)
        {
            double column = PositiveModulo(longitude / (2.0 * Math.PI) + 0.5, 1.0) *
                m_GlobalWidth - 0.5;
            double row = (0.5 - latitude / Math.PI) * m_GlobalHeight - 0.5;
            return Bilinear(m_Global, m_GlobalWidth, m_GlobalHeight, column, row, true);
        }

        private double SampleRegional(double longitude, double latitude)
        {
            double column = (m_RegionalWidth - 1) * 0.5 +
                longitude * 180.0 / Math.PI * m_RegionalPixelsPerDegree;
            double row = (m_RegionalHeight - 1) * 0.5 -
                latitude * 180.0 / Math.PI * m_RegionalPixelsPerDegree;
            return Bilinear(
                m_Regional,
                m_RegionalWidth,
                m_RegionalHeight,
                column,
                row,
                false);
        }

        private static double Bilinear(
            float[] data,
            int width,
            int height,
            double column,
            double row,
            bool wrapX)
        {
            row = Math.Clamp(row, 0.0, height - 1.0);
            if (!wrapX) column = Math.Clamp(column, 0.0, width - 1.0);
            int y0 = Math.Clamp((int)Math.Floor(row), 0, height - 1);
            int y1 = Math.Min(y0 + 1, height - 1);
            double fy = Math.Clamp(row - Math.Floor(row), 0.0, 1.0);
            int rawX0 = (int)Math.Floor(column);
            int x0 = wrapX ? PositiveModulo(rawX0, width) : Math.Clamp(rawX0, 0, width - 1);
            int x1 = wrapX ? PositiveModulo(rawX0 + 1, width) : Math.Min(x0 + 1, width - 1);
            double fx = Math.Clamp(column - Math.Floor(column), 0.0, 1.0);
            double a = Lerp(data[y0 * width + x0], data[y0 * width + x1], fx);
            double b = Lerp(data[y1 * width + x0], data[y1 * width + x1], fx);
            return Lerp(a, b, fy);
        }

        private static int PositiveModulo(int value, int divisor)
        {
            int result = value % divisor;
            return result < 0 ? result + divisor : result;
        }

        private static double PositiveModulo(double value, double divisor)
        {
            double result = value % divisor;
            return result < 0.0 ? result + divisor : result;
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static double Smooth01(double value)
        {
            value = Math.Clamp(value, 0.0, 1.0);
            return value * value * (3.0 - 2.0 * value);
        }
    }
}
