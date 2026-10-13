using System;
using System.Collections.Generic;
using Omni.BatchRendering;
using UnityEngine;
using UnityEngine.Rendering;
using L = Omni.Logical;
using V = UnityEngine.Vector3;

namespace Omni.PlanetSurface
{
    /// <summary>
    /// カメラ高度に追従してCube-sphereを分割・統合する惑星共通LODです。
    /// EarthToMoonではMoonRootへ付き、宇宙から自由カメラまで同じLogical表面を描きます。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class PlanetSurfaceLod : MonoBehaviour
    {
        private sealed class Node
        {
            internal SurfaceTileKey Key;
            internal Node[] Children;
            internal GameObject Owner;
            internal Mesh Mesh;
            internal MeshRenderer Renderer;
            internal RadialBatchInstanceHandle Handle;
            internal SurfaceVector Origin;
            internal SurfacePatchBuilder Geometry;
            internal SurfaceDetailBinding Detail;
            internal long CpuReservedBytes;
            internal long GpuReservedBytes;
        }

        [SerializeField] private PlanetSurfaceDefinition m_Definition;
        [SerializeField] private Material m_Material;
        [SerializeField] private L.Transform m_Viewer;
        [SerializeField] private MeshRenderer[] m_LegacyRenderers = Array.Empty<MeshRenderer>();
        private readonly Node[] m_Roots = new Node[6];
        private IPlanetSurfaceHeightSource m_HeightSource;
        private L.Transform m_PlanetPose;
        private Camera m_Camera;
        private bool m_Initialized;
        private bool m_HasPrewarmedDirection;
        private SurfaceTileKey[] m_PrewarmedMaximumKeys = Array.Empty<SurfaceTileKey>();
        private const int PrewarmLockLevel = 8;
        private PlanetSurfaceDiagnostics m_Diagnostics;
        private SurfaceQualityProfile m_Profile;
        private int m_LeafCount;

        /// <summary>実測した直近のLOD更新時間です。1パッチ生成の予算超過も隠しません。</summary>
        public double LastUpdateMilliseconds { get; private set; }

        /// <summary>現在のLOD状態をHUDや自動検証から読み取ります。</summary>
        public PlanetSurfaceDiagnostics Diagnostics => m_Diagnostics;

        /// <summary>
        /// trueなら、Scene BRGから登録解除したパッチのGameObjectとMeshの破棄を30フレーム遅らせます(opt-in、既定はfalseで従来どおり)。
        /// 登録解除はBRGの次の更新(RadialSceneBatchRendererService.Tick)まで保留されますが、カメラ描画の前の常駐データ更新は
        /// 同じフレームのうちに走ります。同じフレームで破棄すると、その更新が破棄済みのRendererに当たり、BRGが停止します
        /// (観測: EarthToMoonで静止軌道へシークした直後の "Logical resident Renderer was destroyed after registration")。
        /// 待つ間はRendererを無効にしておき、通常の描画経路にも出しません。Initializeより前に設定します。
        /// </summary>
        public bool DeferRendererDestruction { get; set; }
        // 大量の統合でBRGの削除処理が全体の作り直しへ切り替わると、処理まで数フレーム以上かかります
        // (観測: 3フレームでは、地球付近から月面へ移ったときにFatal Faultが残った)。
        private const int DeferredDestructionFrames = 30;
        private readonly Queue<(int Frame, GameObject Owner, Mesh Mesh)> m_DeferredDestruction = new();

        /// <summary>定義、材質、視点、標高正本と旧表面を設定して初期化します。</summary>
        public void Initialize(
            PlanetSurfaceDefinition definition,
            Material material,
            L.Transform viewer,
            IPlanetSurfaceHeightSource heightSource,
            MeshRenderer[] legacyRenderers,
            V? prewarmDirection = null,
            bool lockPrewarmedSurface = false,
            SurfaceQualityProfile profile = null,
            ComputeShader detailShader = null,
            long additionalBaseDataCpuBytes = 0)
        {
            if (m_Initialized)
            {
                Shutdown();
            }
            m_Definition = definition != null ? definition : throw new ArgumentNullException(nameof(definition));
            m_Material = material != null ? material : throw new ArgumentNullException(nameof(material));
            m_Viewer = viewer != null ? viewer : throw new ArgumentNullException(nameof(viewer));
            m_HeightSource = heightSource ?? throw new ArgumentNullException(nameof(heightSource));
            m_Profile = profile ?? SurfaceQualityProfile.Current();
            m_Profile.Validate();
            if(additionalBaseDataCpuBytes<0){throw new ArgumentOutOfRangeException(nameof(additionalBaseDataCpuBytes));}
            m_LeafCount = 6;
            // 旧引数は呼出し互換だけを保持します。地形は常に現在の視点へ追従します。
            m_LegacyRenderers = legacyRenderers ?? Array.Empty<MeshRenderer>();
            m_PlanetPose = GetComponent<L.Transform>();
            if (m_PlanetPose == null)
            {
                throw new InvalidOperationException("PlanetSurfaceLodは天体中心のLogical Transformを必要とします。");
            }
            m_Camera = m_Viewer.GetComponent<Camera>();
            if (m_Camera == null)
            {
                m_Camera = Camera.main;
            }
            this.InitializeMemoryBudget(additionalBaseDataCpuBytes);
            int transitionVertices=(m_Definition.GridResolution+1)*(m_Definition.GridResolution+1)+
                4*(m_Definition.GridResolution+1);
            this.transitionWorkspace=new SurfacePatchTransitionWorkspace(transitionVertices,
                SurfacePatchTransitionWorkspace.RequiredPatchCapacity(m_Profile.MaximumVisiblePatches));
            this.InitializeDetail(detailShader);
            this.InitializeRocks();
            for (int face = 0; face < 6; face++)
            {
                m_Roots[face] = CreateNode(new SurfaceTileKey(face, 0, 0, 0));
            }
            this.topology = new SurfaceLodTopology(m_Profile.MaximumVisiblePatches);
            SetLegacyVisible(false);
            m_Initialized = true;
            UpdateDiagnostics(0.0, 0);
        }

        private void Update()
        {
            this.Tick();
        }

        /// <summary>
        /// 1 フレーム分の更新（遅らせた破棄と、分割・結合の作業）。Play では Update が呼びます。
        /// Edit Mode で使う側（この Component の Update は Edit Mode では呼ばれない）は、自分の更新から呼びます。
        /// </summary>
        public void Tick()
        {
            this.FlushDeferredDestruction(false);
            this.UpdateIncremental();
        }

        /// <summary>
        /// パッチ・メッシュなど作った物を全て片付け、旧来の表示へ戻します（Play では OnDisable・OnDestroy が呼びます。
        /// Edit Mode ではこの Component の OnDisable・OnDestroy が呼ばれないので、使う側が呼びます）。
        /// </summary>
        public void Release()
        {
            if (m_Initialized)
            {
                Shutdown();
            }
        }

        /// <summary>作った物を破棄します（Play は Destroy、Edit Mode は DestroyImmediate。Edit Mode では Destroy を使えない）。</summary>
        public static void DestroyOwned(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }

        /// <summary>遅らせた破棄のうち、待ち時間を過ぎたもの(forceなら全部)を破棄します。</summary>
        private void FlushDeferredDestruction(bool force)
        {
            while (m_DeferredDestruction.Count > 0 &&
                (force || m_DeferredDestruction.Peek().Frame <= Time.frameCount))
            {
                var pending = m_DeferredDestruction.Dequeue();
                if (pending.Owner != null)
                {
                    DestroyOwned(pending.Owner);
                }
                if (pending.Mesh != null)
                {
                    DestroyOwned(pending.Mesh);
                }
            }
        }

        private void Prewarm(V direction)
        {
            DirectionToFaceUv(direction, out int face, out double u, out double v);
            int level = m_Definition.MaximumLevel;
            int scale = 1 << level;
            int x = Math.Clamp((int)(u * scale), 0, scale - 1);
            int y = Math.Clamp((int)(v * scale), 0, scale - 1);
            var keys = new List<SurfaceTileKey>(9);
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int tileX = x + offsetX;
                    int tileY = y + offsetY;
                    if (tileX < 0 || tileY < 0 || tileX >= scale || tileY >= scale)
                    {
                        continue;
                    }
                    var key = new SurfaceTileKey(face, level, tileX, tileY);
                    keys.Add(key);
                    while (SplitOneTargetBranch(face, level, tileX, tileY))
                    {
                    }
                }
            }
            m_PrewarmedMaximumKeys = keys.ToArray();
            m_HasPrewarmedDirection = true;
        }

        private bool TryUsePrewarmedBranch(
            V viewerDirection,
            int requestedLevel,
            out SurfaceTileKey key)
        {
            key = default;
            if (!m_HasPrewarmedDirection)
            {
                return false;
            }
            if (requestedLevel <= Math.Min(PrewarmLockLevel, m_Definition.MaximumLevel))
            {
                key = m_PrewarmedMaximumKeys[0];
                return true;
            }
            DirectionToFaceUv(viewerDirection, out int face, out double u, out double v);
            int level = m_Definition.MaximumLevel;
            int scale = 1 << level;
            int x = Math.Clamp((int)(u * scale), 0, scale - 1);
            int y = Math.Clamp((int)(v * scale), 0, scale - 1);
            var viewerKey = new SurfaceTileKey(face, level, x, y);
            foreach (SurfaceTileKey prewarmed in m_PrewarmedMaximumKeys)
            {
                if (prewarmed.Equals(viewerKey))
                {
                    key = prewarmed;
                    return true;
                }
            }
            return false;
        }

        private void OnDisable()
        {
            if (m_Initialized)
            {
                Shutdown();
            }
        }

        private void OnDestroy()
        {
            if (m_Initialized)
            {
                Shutdown();
            }
        }

        private bool SplitOneTargetBranch(int face, int level, int x, int y)
        {
            Node node = m_Roots[face];
            while (node.Key.Level < level)
            {
                if (node.Children == null)
                {
                    if (m_LeafCount + 3 > m_Profile.MaximumVisiblePatches) return false;
                    Split(node);
                    return true;
                }
                int shift = level - node.Key.Level - 1;
                node = node.Children[(((y >> shift) & 1) * 2) + ((x >> shift) & 1)];
            }
            return false;
        }

        private bool CollapseOneStaleBranch(int face, int level, int x, int y)
        {
            foreach (Node root in m_Roots)
            {
                if (CollapseOneStaleBranch(root, face, level, x, y))
                {
                    return true;
                }
            }
            return false;
        }

        private bool CollapseOneStaleBranch(Node node, int face, int level, int x, int y)
        {
            if (node.Children == null)
            {
                return false;
            }
            if (!Contains(node.Key, face, level, x, y) || node.Key.Level >= level)
            {
                Merge(node);
                return true;
            }
            int shift = level - node.Key.Level - 1;
            int targetChild = (((y >> shift) & 1) * 2) + ((x >> shift) & 1);
            for (int index = 0; index < 4; index++)
            {
                if (index != targetChild && node.Children[index].Children != null)
                {
                    Merge(node.Children[index]);
                    return true;
                }
            }
            return CollapseOneStaleBranch(node.Children[targetChild], face, level, x, y);
        }

        private static bool Contains(SurfaceTileKey key, int face, int level, int x, int y)
        {
            if (key.Face != face || key.Level > level)
            {
                return false;
            }
            int shift = level - key.Level;
            return (x >> shift) == key.X && (y >> shift) == key.Y;
        }

        private void Split(Node parent)
        {
            parent.Children = new Node[4];
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    parent.Children[y * 2 + x] = CreateNode(parent.Key.Child(x, y));
                }
            }
            SetNodeVisible(parent, false);
            m_LeafCount += 3;
        }

        private void Merge(Node parent)
        {
            if (parent.Children == null)
            {
                return;
            }
            int leaves = 0, renderers = 0, deepest = 0;
            Count(parent, ref leaves, ref renderers, ref deepest);
            foreach (Node child in parent.Children)
            {
                DestroyNode(child);
            }
            parent.Children = null;
            SetNodeVisible(parent, true);
            m_LeafCount -= leaves - 1;
        }

        private Node CreateNode(SurfaceTileKey key)
        {
            // 初期の6面だけは粗いメッシュを直ちに用意します。子は分割スケジューラで生成します。
            var geometry = new SurfacePatchBuilder(key, m_Definition.RadiusMeters,
                m_Definition.GridResolution, m_HeightSource, m_Definition.SkirtDepthScale);
            while (geometry.Step()) { }
            return this.CreateNode(key, geometry.CreateMesh(), geometry.Origin, true, geometry);
        }

        private Node CreateNode(SurfaceTileKey key, Mesh mesh, SurfaceVector center, bool visible,
            SurfacePatchBuilder geometry)
        {
            var owner = new GameObject($"PlanetSurface {key}") { hideFlags = HideFlags.DontSave };
            owner.transform.SetParent(transform, false);
            var logical = owner.AddComponent<L.Transform>();
            logical.LocalPosition = Logical(center.X, center.Y, center.Z);
            logical.SyncUnityTransform();
            owner.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = owner.AddComponent<MeshRenderer>();
            renderer.enabled = visible;
            renderer.sharedMaterial = m_Material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            var node = new Node
            {
                Key = key,
                Origin = center,
                Owner = owner,
                Mesh = mesh,
                Renderer = renderer,
                Handle = RadialBatchRendererRegistry.Register(renderer),
                Geometry = geometry
            };
            this.RegisterNodeMemory(node,geometry);
            return node;
        }

        private Mesh BuildMesh(SurfaceTileKey key, out SurfaceVector center)
        {
            int resolution = m_Definition.GridResolution;
            int surfaceVertexCount = (resolution + 1) * (resolution + 1);
            int skirtVertexCount = (resolution + 1) * 4;
            var vertices = new V[surfaceVertexCount + skirtVertexCount];
            var normals = new V[vertices.Length];
            var tangents = new Vector4[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var directions = new V[vertices.Length];
            int scale = 1 << key.Level;
            double minU = -1.0 + 2.0 * key.X / scale;
            double meshSpacing = 2.0 * m_Definition.RadiusMeters / scale / resolution;
            double maxU = -1.0 + 2.0 * (key.X + 1) / scale;
            double minV = -1.0 + 2.0 * key.Y / scale;
            double maxV = -1.0 + 2.0 * (key.Y + 1) / scale;
            SurfaceVector preciseCenterDirection = SurfaceGeometry.CubeDirection(key.Face, (minU + maxU) * 0.5, (minV + maxV) * 0.5);
            V centerDirection = Float(preciseCenterDirection);
            double centerHeight = SampleMeshHeight(preciseCenterDirection, meshSpacing);
            center = preciseCenterDirection * (m_Definition.RadiusMeters + centerHeight);
            double centerTextureU = LongitudeU(centerDirection);

            for (int row = 0; row <= resolution; row++)
            {
                double cubeV = Lerp(minV, maxV, row / (double)resolution);
                for (int column = 0; column <= resolution; column++)
                {
                    double cubeU = Lerp(minU, maxU, column / (double)resolution);
                    SurfaceVector preciseDirection = SurfaceGeometry.CubeDirection(key.Face, cubeU, cubeV);
                    V direction = Float(preciseDirection);
                    double height = SampleMeshHeight(preciseDirection, meshSpacing);
                    int index = row * (resolution + 1) + column;
                    // 惑星半径を含む絶対値をfloatへ変換せず、パッチ差分だけを渡します。
                    vertices[index] = Float(preciseDirection *
                        (m_Definition.RadiusMeters + height) - center);
                    directions[index] = direction;
                    tangents[index] = LongitudeTangent(direction);
                    double textureU = Math.Atan2(preciseDirection.X, preciseDirection.Y) / (2.0 * Math.PI) + 0.5;
                    if (textureU - centerTextureU > 0.5)
                    {
                        textureU -= 1.0;
                    }
                    else if (textureU - centerTextureU < -0.5)
                    {
                        textureU += 1.0;
                    }
                    uv[index] = new Vector2(
                        (float)textureU,
                        (float)(Math.Asin(Math.Clamp(preciseDirection.Z, -1.0, 1.0)) / Math.PI + 0.5));
                }
            }

            // タイル外側も同じ天体ローカル評価器で中央差分を取り、片側差分の照明筋を避けます。
            for (int row = 0; row <= resolution; row++)
            {
                for (int column = 0; column <= resolution; column++)
                {
                    int index = row * (resolution + 1) + column;
                    SurfaceVector direction = SurfaceGeometry.CubeDirection(key.Face,
                        Lerp(minU, maxU, column / (double)resolution),
                        Lerp(minV, maxV, row / (double)resolution));
                    normals[index] = SampleMeshNormal(direction, meshSpacing);
                }
            }

            double patchSpan = 2.0 * m_Definition.RadiusMeters / scale;
            float skirtDepth = (float)Math.Max(
                2.0,
                patchSpan / resolution * m_Definition.SkirtDepthScale);
            int skirtCursor = surfaceVertexCount;
            for (int edge = 0; edge < 4; edge++)
            {
                for (int step = 0; step <= resolution; step++)
                {
                    int source = EdgeVertexIndex(edge, step, resolution);
                    vertices[skirtCursor] = vertices[source] - directions[source] * skirtDepth;
                    directions[skirtCursor] = directions[source];
                    normals[skirtCursor] = normals[source];
                    tangents[skirtCursor] = tangents[source];
                    uv[skirtCursor] = uv[source];
                    skirtCursor++;
                }
            }

            var triangles = new List<int>(
                resolution * resolution * 6 + resolution * 48);
            for (int row = 0; row < resolution; row++)
            {
                for (int column = 0; column < resolution; column++)
                {
                    int a = row * (resolution + 1) + column;
                    int b = a + 1;
                    int c = a + resolution + 1;
                    int d = c + 1;
                    if (V.Dot(
                            V.Cross(vertices[c] - vertices[a], vertices[b] - vertices[a]),
                            centerDirection) > 0.0f)
                    {
                        AddQuad(triangles, a, c, b, d);
                    }
                    else
                    {
                        AddQuad(triangles, a, b, c, d);
                    }
                }
            }

            for (int edge = 0; edge < 4; edge++)
            {
                int skirtStart = surfaceVertexCount + edge * (resolution + 1);
                for (int step = 0; step < resolution; step++)
                {
                    int topA = EdgeVertexIndex(edge, step, resolution);
                    int topB = EdgeVertexIndex(edge, step + 1, resolution);
                    int lowA = skirtStart + step;
                    int lowB = skirtStart + step + 1;
                    AddQuad(triangles, topA, topB, lowA, lowB);
                    AddQuad(triangles, topA, lowA, topB, lowB);
                }
            }

            var mesh = new Mesh
            {
                name = $"PlanetSurface_{key}",
                indexFormat = vertices.Length > ushort.MaxValue
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16,
                hideFlags = HideFlags.DontSave
            };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.uv = uv;
            mesh.SetTriangles(triangles, 0, true);
            mesh.RecalculateBounds();
            Bounds bounds = mesh.bounds;
            bounds.Expand((float)(
                m_HeightSource.MaximumAbsoluteHeightMeters * 2.0 + skirtDepth * 2.0));
            mesh.bounds = bounds;
            return mesh;
        }

        private double SampleMeshHeight(SurfaceVector direction, double spacing)
        {
            if (m_HeightSource is IBandLimitedSurfaceHeightSource filtered)
                return filtered.SampleHeightMeters(direction, spacing);
            return m_HeightSource.SampleHeightMeters(direction.X, direction.Y, direction.Z);
        }

        private V SampleMeshNormal(SurfaceVector direction, double spacing)
        {
            SurfaceVector reference = new(0, 0, 1);
            if (Math.Abs(direction.Z) > 0.9) reference = new SurfaceVector(0, 1, 0);
            SurfaceVector tangent = SurfaceVector.Cross(reference, direction).Normalized;
            SurfaceVector bitangent = SurfaceVector.Cross(direction, tangent);
            double delta = Math.Max(0.01, spacing * 0.5);
            double angle = delta / m_Definition.RadiusMeters;
            double dx = (SampleMeshHeight((direction + tangent * angle).Normalized, spacing) -
                SampleMeshHeight((direction - tangent * angle).Normalized, spacing)) / (2 * delta);
            double dy = (SampleMeshHeight((direction + bitangent * angle).Normalized, spacing) -
                SampleMeshHeight((direction - bitangent * angle).Normalized, spacing)) / (2 * delta);
            double radialCorrection = m_Definition.RadiusMeters /
                (m_Definition.RadiusMeters + SampleMeshHeight(direction, spacing));
            return Float((direction - (tangent * dx + bitangent * dy) * radialCorrection).Normalized);
        }

        private static int EdgeVertexIndex(int edge, int step, int resolution)
        {
            return edge switch
            {
                0 => step,
                1 => step * (resolution + 1) + resolution,
                2 => resolution * (resolution + 1) + resolution - step,
                _ => (resolution - step) * (resolution + 1)
            };
        }

        private static void AddQuad(List<int> triangles, int a, int b, int c, int d)
        {
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
            triangles.Add(c);
            triangles.Add(b);
            triangles.Add(d);
        }

        private void DestroyNode(Node node)
        {
            this.ReleaseNodeMemory(node);
            this.ReleaseDetail(node);
            if (node.Children != null)
            {
                foreach (Node child in node.Children)
                {
                    DestroyNode(child);
                }
            }
            if (node.Handle.IsValid)
            {
                RadialBatchRendererRegistry.Unregister(node.Handle);
            }
            if (DeferRendererDestruction)
            {
                if (node.Renderer != null)
                {
                    node.Renderer.enabled = false;
                }
                m_DeferredDestruction.Enqueue((Time.frameCount + DeferredDestructionFrames, node.Owner, node.Mesh));
                return;
            }
            if (node.Owner != null)
            {
                DestroyOwned(node.Owner);
            }
            if (node.Mesh != null)
            {
                DestroyOwned(node.Mesh);
            }
        }

        private void SetNodeVisible(Node node, bool visible)
        {
            if (node.Renderer == null || node.Renderer.enabled == visible)
            {
                return;
            }
            this.SetDetailVisible(node, visible);
            node.Renderer.enabled = visible;
            RadialBatchRendererRegistry.MarkDirty(node.Handle, RadialBatchDirtyFlags.Visibility);
        }

        private void Shutdown()
        {
            this.CancelIncrementalWork();
            this.transitionWorkspace=null;
            for (int index = 0; index < m_Roots.Length; index++)
            {
                if (m_Roots[index] != null)
                {
                    DestroyNode(m_Roots[index]);
                    m_Roots[index] = null;
                }
            }
            this.ShutdownRocks();
            this.ShutdownDetail();
            this.ResetMemoryBudget();
            this.FlushDeferredDestruction(true);
            SetLegacyVisible(true);
            m_Initialized = false;
            m_HasPrewarmedDirection = false;
            m_PrewarmedMaximumKeys = Array.Empty<SurfaceTileKey>();
            m_HeightSource = null;
        }

        private void SetLegacyVisible(bool visible)
        {
            foreach (MeshRenderer renderer in m_LegacyRenderers)
            {
                if (renderer != null)
                {
                    renderer.enabled = visible;
                }
            }
        }

        private bool TryGetViewerLocal(out SurfaceVector viewerLocal)
        {
            viewerLocal = default;
            if (m_Viewer == null || m_PlanetPose == null ||
                !m_Viewer.TryGetWorld(out L.Position3 viewerWorld, out _, out _) ||
                !m_PlanetPose.TryGetWorld(
                    out L.Position3 planetWorld,
                    out L.Quaternion planetRotation,
                    out _) ||
                !L.Vector3.TrySubtract(viewerWorld.Value, planetWorld.Value, out L.Vector3 delta) ||
                !TryRead(delta.X, out double x) ||
                !TryRead(delta.Y, out double y) ||
                !TryRead(delta.Z, out double z))
            {
                return false;
            }
            if (!L.Quaternion.TryConjugate(planetRotation, out L.Quaternion inverse) ||
                !L.Quaternion.TryRotate(inverse, delta, out L.Vector3 local) ||
                !TryRead(local.X, out x) || !TryRead(local.Y, out y) || !TryRead(local.Z, out z))
                return false;
            viewerLocal = new SurfaceVector(x, y, z);
            return viewerLocal.Length > 0.0;
        }

        private int DesiredLevel(double altitude)
        {
            double pixels = Math.Max(1, m_Camera != null ? m_Camera.pixelHeight : 1080);
            double fov = (m_Camera != null ? m_Camera.fieldOfView : 55.0) * Math.PI / 180.0;
            double metersPerPixel = 2.0 * Math.Max(altitude, 1.0) *
                Math.Tan(fov * 0.5) / pixels;
            double targetCell = Math.Max(m_Profile.MinimumMeshSpacingMeters, metersPerPixel * m_Profile.TargetCellPixels);
            double rootCell = 2.0 * m_Definition.RadiusMeters / m_Definition.GridResolution;
            int level = (int)Math.Ceiling(Math.Log(rootCell / targetCell, 2.0));
            return Math.Clamp(level, 0, m_Definition.MaximumLevel);
        }

        private void UpdateDiagnostics(double altitude, int desiredLevel)
        {
            int leaves = 0;
            int renderers = 0;
            int deepest = 0;
            foreach (Node root in m_Roots)
            {
                Count(root, ref leaves, ref renderers, ref deepest);
            }
            m_Diagnostics = new PlanetSurfaceDiagnostics(
                leaves, renderers, deepest, altitude, desiredLevel);
        }

        private static void Count(Node node, ref int leaves, ref int renderers, ref int deepest)
        {
            if (node == null)
            {
                return;
            }
            renderers++;
            deepest = Math.Max(deepest, node.Key.Level);
            if (node.Children == null)
            {
                leaves++;
                return;
            }
            foreach (Node child in node.Children)
            {
                Count(child, ref leaves, ref renderers, ref deepest);
            }
        }

        private static V CubeDirection(int face, double a, double b)
        {
            V value = face switch
            {
                0 => new V((float)a, 1.0f, (float)b),
                1 => new V((float)a, -1.0f, (float)b),
                2 => new V(1.0f, (float)a, (float)b),
                3 => new V(-1.0f, (float)a, (float)b),
                4 => new V((float)a, (float)b, 1.0f),
                _ => new V((float)a, (float)b, -1.0f)
            };
            return value.normalized;
        }

        private static void DirectionToFaceUv(V direction, out int face, out double u, out double v)
        {
            double ax = Math.Abs(direction.x);
            double ay = Math.Abs(direction.y);
            double az = Math.Abs(direction.z);
            double a;
            double b;
            if (ay >= ax && ay >= az)
            {
                face = direction.y >= 0.0f ? 0 : 1;
                a = direction.x / ay;
                b = direction.z / ay;
            }
            else if (ax >= az)
            {
                face = direction.x >= 0.0f ? 2 : 3;
                a = direction.y / ax;
                b = direction.z / ax;
            }
            else
            {
                face = direction.z >= 0.0f ? 4 : 5;
                a = direction.x / az;
                b = direction.y / az;
            }
            u = Math.Clamp(a * 0.5 + 0.5, 0.0, 1.0 - double.Epsilon);
            v = Math.Clamp(b * 0.5 + 0.5, 0.0, 1.0 - double.Epsilon);
        }

        private static double LongitudeU(V direction) =>
            Math.Atan2(direction.x, direction.y) / (2.0 * Math.PI) + 0.5;

        private static Vector4 LongitudeTangent(V direction)
        {
            V tangent = V.Cross(V.forward, direction);
            if (tangent.sqrMagnitude < 1.0e-8f)
            {
                tangent = V.right;
            }
            else
            {
                tangent.Normalize();
            }
            return new Vector4(tangent.x, tangent.y, tangent.z, 1.0f);
        }

        private static L.Vector3 Logical(double x, double y, double z)
        {
            if (!TryWrite(x, out L.Scalar lx) ||
                !TryWrite(y, out L.Scalar ly) ||
                !TryWrite(z, out L.Scalar lz))
            {
                throw new ArithmeticException("惑星パッチのLogical座標変換に失敗しました。");
            }
            return new L.Vector3(lx, ly, lz);
        }

        private static Quaternion ToUnity(L.Quaternion rotation)
        {
            if (!L.Quaternion.TryRotate(rotation, Logical(0, 0, 1), out L.Vector3 forward) ||
                !L.Quaternion.TryRotate(rotation, Logical(0, 1, 0), out L.Vector3 up) ||
                !TryRead(forward.X, out double fx) ||
                !TryRead(forward.Y, out double fy) ||
                !TryRead(forward.Z, out double fz) ||
                !TryRead(up.X, out double ux) ||
                !TryRead(up.Y, out double uy) ||
                !TryRead(up.Z, out double uz))
            {
                return Quaternion.identity;
            }
            return Quaternion.LookRotation(
                new V((float)fx, (float)fy, (float)fz),
                new V((float)ux, (float)uy, (float)uz));
        }

        private static bool TryWrite(double value, out L.Scalar result)
        {
            return L.Scalar.TryFromDouble(value, out result);
        }

        private static bool TryRead(L.Scalar value, out double result) =>
            value.TryToDouble(out result);

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
        private static V Float(SurfaceVector value) => new((float)value.X, (float)value.Y, (float)value.Z);
    }
}
