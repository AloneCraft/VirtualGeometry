using UnityEngine;

namespace VirtualizedGeometry{
    /// <summary>
    /// 変換済みpageの小さな索引。モデルのInspectorで「Virtual Geometry」を有効にすると、インポート時にモデルのサブアセットとして作られる。
    /// 高密度の頂点配列はpage archive(VirtualGeometryPagePaths)に置く。
    /// </summary>
    public sealed class VirtualGeometryMeshAsset:ScriptableObject{
        [SerializeField] private string sourceGuid;
        [SerializeField] private long sourceLocalId;
        public long SourceLocalId=>this.sourceLocalId;
        [SerializeField] private string sourceHash;
        [SerializeField] private string settingsHash;
        [SerializeField] private string contentHash;
        [SerializeField] private string pageRelativePath;
        [SerializeField] private Bounds bounds;
        [SerializeField] private int inputTriangleCount;
        [SerializeField] private int triangleCount;
        [SerializeField] private int leafTriangleCount;
        [SerializeField] private int clusterCount;
        [SerializeField] private int nodeCount;
        [SerializeField] private int levelCount;
        [SerializeField] private int materialCount;
        [SerializeField] private int formatVersion;
        [SerializeField] private int builderVersion;

        [SerializeField] private Mesh proxyMesh;
        [SerializeField] private Material proxyLitMaterial;
        [SerializeField] private Material proxyUnlitMaterial;
        [SerializeField] private int proxyTriangleCount;
        [SerializeField] private float proxyGeometricError;
        public Mesh ProxyMesh=>this.proxyMesh;
        public Material ProxyLitMaterial=>this.proxyLitMaterial;
        public Material ProxyUnlitMaterial=>this.proxyUnlitMaterial;
        public int ProxyTriangleCount=>this.proxyTriangleCount;
        public float ProxyGeometricError=>this.proxyGeometricError;
        [SerializeField] private int streamingPageCount;
        [SerializeField] private long geometryBytes;
        [SerializeField] private int metadataBytes;
        [SerializeField] private int slotGeometryBytes;
        public int StreamingPageCount=>this.streamingPageCount;
        public long GeometryBytes=>this.geometryBytes;
        public int MetadataBytes=>this.metadataBytes;
        public int SlotGeometryBytes=>this.slotGeometryBytes;
        public string SourceGuid=>this.sourceGuid;
        public string SourceHash=>this.sourceHash;
        public string SettingsHash=>this.settingsHash;
        public string ContentHash=>this.contentHash;
        public string PageRelativePath=>this.pageRelativePath;
        public Bounds Bounds=>this.bounds;
        public int InputTriangleCount=>this.inputTriangleCount;
        public int TriangleCount=>this.triangleCount;
        public int LeafTriangleCount=>this.leafTriangleCount;
        public int ClusterCount=>this.clusterCount;
        public int NodeCount=>this.nodeCount;
        /// <summary>LODの段数。1段目が最も細かい元の形状。</summary>
        public int LevelCount=>this.levelCount;
        public int MaterialCount=>this.materialCount;
        public int FormatVersion=>this.formatVersion;
        public int BuilderVersion=>this.builderVersion;
    }
}
