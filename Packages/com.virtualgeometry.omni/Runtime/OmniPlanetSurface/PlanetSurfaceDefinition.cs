using System;
using UnityEngine;

namespace Omni.PlanetSurface
{
    /// <summary>
    /// 球形天体の表面LOD予算を保持します。
    /// EarthToMoonではMoonRootの動的表面へ使用し、後の惑星でも同じ契約を再利用します。
    /// </summary>
    [CreateAssetMenu(menuName = "Omni/Planet Surface Definition")]
    public sealed class PlanetSurfaceDefinition : ScriptableObject
    {
        // 共通型の既定値に特定天体を埋め込まない。天体アセット生成時に必ず明示設定する。
        [SerializeField] private string m_BodyId = "Body";
        [SerializeField] private double m_RadiusMeters = 1.0;
        [SerializeField] private int m_GridResolution = 16;
        [SerializeField] private int m_MaximumLevel = 17;
        [SerializeField] private int m_UpdatesPerFrame = 1;
        [SerializeField] private float m_TargetCellPixels = 6.0f;
        [SerializeField] private float m_MergeHysteresis = 0.55f;
        [SerializeField] private float m_SkirtDepthScale = 2.0f;
        [SerializeField] private TextAsset m_BaseHeight;
        [SerializeField] private Texture2D m_BaseAlbedo;
        [SerializeField] private int m_HeightWidth;
        [SerializeField] private int m_HeightHeight;
        [SerializeField] private string m_BaseDataHash;
        [SerializeField] private SurfaceGenerationSettings m_Generation = new();
        [SerializeField] private SurfaceRockSettings m_Rocks = new();

        /// <summary>共有する低周波標高のfloat32メートルデータです。</summary>
        public TextAsset BaseHeight => m_BaseHeight;
        /// <summary>陰影を焼き込まない反射色の基礎データです。</summary>
        public Texture2D BaseAlbedo => m_BaseAlbedo;
        public int HeightWidth => m_HeightWidth;
        public int HeightHeight => m_HeightHeight;
        public string BaseDataHash => m_BaseDataHash;
        public SurfaceGenerationSettings Generation => m_Generation;
        /// <summary>品質プロファイルから独立した岩屑の生成設定です。</summary>
        public SurfaceRockSettings Rocks => m_Rocks;

        /// <summary>Editor生成器で、正本とその同一性を定義へ登録します。</summary>
        public void ConfigureBaseData(TextAsset height, Texture2D albedo, int width, int rows,
            string dataHash, SurfaceGenerationSettings generation)
        {
            if (height == null || albedo == null || width < 4 || width % 2 != 0 || rows < 2 ||
                string.IsNullOrWhiteSpace(dataHash) || generation == null)
                throw new ArgumentException("惑星基礎データの指定が不正です。");
            generation.Validate();
            m_BaseHeight = height;
            m_BaseAlbedo = albedo;
            m_HeightWidth = width;
            m_HeightHeight = rows;
            m_BaseDataHash = dataHash;
            m_Generation = generation;
        }

        /// <summary>共有天体カタログと照合する識別子です。</summary>
        public string BodyId => m_BodyId;

        /// <summary>標高ゼロの基準となる平均半径です。単位はメートルです。</summary>
        public double RadiusMeters => m_RadiusMeters;

        /// <summary>1パッチ辺のquad数です。</summary>
        public int GridResolution => m_GridResolution;

        /// <summary>Cube-sphereの最大四分木階層です。</summary>
        public int MaximumLevel => m_MaximumLevel;

        /// <summary>1フレームで許可する分割または統合回数です。</summary>
        public int UpdatesPerFrame => m_UpdatesPerFrame;

        /// <summary>地表セルがこの画素数を超えると細分化します。</summary>
        public float TargetCellPixels => m_TargetCellPixels;

        /// <summary>統合閾値を分割閾値より下げる係数です。</summary>
        public float MergeHysteresis => m_MergeHysteresis;

        /// <summary>異なるLODの境界を覆うskirt深さのセル幅倍率です。</summary>
        public float SkirtDepthScale => m_SkirtDepthScale;

        /// <summary>
        /// Editor生成器から、天体とLOD予算を冪等に設定します。
        /// </summary>
        /// <param name="bodyId">天体カタログのIDです。空文字は拒否します。</param>
        /// <param name="radiusMeters">標高ゼロの半径メートルです。</param>
        /// <param name="maximumLevel">四分木の最大階層です。</param>
        /// <param name="targetCellPixels">許容する地表セルの画面画素数です。</param>
        public void Configure(
            string bodyId,
            double radiusMeters,
            int maximumLevel,
            float targetCellPixels)
        {
            if (string.IsNullOrWhiteSpace(bodyId))
            {
                throw new ArgumentException("天体IDが必要です。", nameof(bodyId));
            }

            if (!double.IsFinite(radiusMeters) || radiusMeters <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(radiusMeters));
            }

            m_BodyId = bodyId;
            m_RadiusMeters = radiusMeters;
            m_GridResolution = 16;
            m_MaximumLevel = Mathf.Clamp(maximumLevel, 0, 20);
            m_UpdatesPerFrame = 1;
            m_TargetCellPixels = Mathf.Clamp(targetCellPixels, 1.0f, 32.0f);
            m_MergeHysteresis = 0.55f;
            m_SkirtDepthScale = 2.0f;
        }
    }

    /// <summary>Cube-sphere上の一意なパッチを表します。</summary>
    public readonly struct SurfaceTileKey : IEquatable<SurfaceTileKey>
    {
        /// <summary>面・階層・階層内座標からキーを作成します。</summary>
        public SurfaceTileKey(int face, int level, int x, int y)
        {
            Face = face;
            Level = level;
            X = x;
            Y = y;
        }

        /// <summary>Cube-sphere面番号（0～5）です。</summary>
        public int Face { get; }

        /// <summary>四分木階層です。</summary>
        public int Level { get; }

        /// <summary>階層内の横座標です。</summary>
        public int X { get; }

        /// <summary>階層内の縦座標です。</summary>
        public int Y { get; }

        /// <summary>指定した子象限のキーを返します。</summary>
        public SurfaceTileKey Child(int childX, int childY) =>
            new(Face, Level + 1, X * 2 + childX, Y * 2 + childY);

        /// <inheritdoc />
        public bool Equals(SurfaceTileKey other) =>
            Face == other.Face && Level == other.Level && X == other.X && Y == other.Y;

        /// <inheritdoc />
        public override bool Equals(object obj) =>
            obj is SurfaceTileKey other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Face, Level, X, Y);

        /// <inheritdoc />
        public override string ToString() => $"F{Face}/L{Level}/{X}/{Y}";
    }

    /// <summary>
    /// 惑星表面の標高を供給します。方向は天体ローカルの単位ベクトル、
    /// 戻り値は基準半径からのメートルです。
    /// </summary>
    public interface IPlanetSurfaceHeightSource
    {
        /// <summary>データが表現する最大絶対標高です。Mesh boundsの安全余白へ使います。</summary>
        double MaximumAbsoluteHeightMeters { get; }

        /// <summary>天体ローカル方向に対応する標高メートルを返します。</summary>
        double SampleHeightMeters(double unitX, double unitY, double unitZ);
    }

    /// <summary>実行中のLOD状態を読み取る診断値です。</summary>
    public readonly struct PlanetSurfaceDiagnostics
    {
        /// <summary>現在の値から診断スナップショットを作成します。</summary>
        public PlanetSurfaceDiagnostics(
            int leafCount,
            int rendererCount,
            int deepestLevel,
            double viewerAltitudeMeters,
            int desiredLevel)
        {
            LeafCount = leafCount;
            RendererCount = rendererCount;
            DeepestLevel = deepestLevel;
            ViewerAltitudeMeters = viewerAltitudeMeters;
            DesiredLevel = desiredLevel;
        }

        /// <summary>表示中の葉パッチ数です。</summary>
        public int LeafCount { get; }

        /// <summary>生成済みRenderer数です。</summary>
        public int RendererCount { get; }

        /// <summary>現在生成済みの最深階層です。</summary>
        public int DeepestLevel { get; }

        /// <summary>基準半径からの視点高度メートルです。</summary>
        public double ViewerAltitudeMeters { get; }

        /// <summary>現在の画面条件が要求する階層です。</summary>
        public int DesiredLevel { get; }
    }
}
