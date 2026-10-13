// CosmicScale の画像のガイドを持つ星雲の「形の模型」（Docs/Plans/CosmicNebula3D.md）。観測で分かっている立体の形を、
// 画像の面の座標 v（単位半径の球、xy = 画像の横・縦、z = 奥行き。主役のカメラは −z の側）で表します。
// 色と明るさは画像（ガイド）が決め、ここは「各画素の光を視線の上のどこに置くか」だけを決めます。画像の光は 2 つに分けて置きます
// （2026-10-05 のユーザーの判断「粗い光は体積、細部だけ曲面」）:
// ・下地（画像を超えない滑らかな光）は、縁へなだらかに薄れる体積（CosmicNebulaVolumeDensity）。輪郭が出ず、光が滑らかなので斜めでも筋が目立たない。
// ・細部（画像 − 下地）は、厚さの無い曲面の組（以下）。斜めから見ても画像の細部が曲面に貼られたように縮んで見え、筋にならない。
// 曲面の種類: 楕円体（球を含む。殻・泡・ローブ・柱）、平らな環（環・円盤。重みは半径の分布）、手前へ開いた放物面（HII 領域の電離の面）。
// 曲面の点の放射 = 細部の色 × 曲面の重み × 見る向きの倍率（CosmicSurfaceSlant）÷ 主役の視線の上の同じ量の合計（CosmicVolumeBake.compute の
// BakeGuideColumn の R）。見る向きの倍率は cos² で、見かけの輪郭へなだらかに 0 になります（輪郭の線を出さない）。主役のカメラから見ると、
// 視線ごとの明るさの合計がちょうど画像になります。各画素の細部は曲面の上の点になるので、斜めから見ても画像が曲面に貼られたように
// 縮んで見え、筋にもぼけにもなりません（厚さのある体積だけに置くと、近く斜めに見たとき主役の視線の向きに伸びて筋になった）。
// 数値は文献と画像の計測から決め、どの関数も compute と描画（CosmicVolumeShading.hlsl）で同じものを使います。
// 純粋な数式だけで、他の include に依存しません。
#ifndef COSMIC_NEBULA_MODELS_INCLUDED
#define COSMIC_NEBULA_MODELS_INCLUDED

// 模型の番号（CosmicVolumeAtlas.ModelOf と同じ）。0 は模型なし（カメラへ回した 1 枚の面で描く）。
#define COSMIC_NEBULA_MODEL_NONE 0
#define COSMIC_NEBULA_MODEL_RING 1
#define COSMIC_NEBULA_MODEL_HELIX 2
#define COSMIC_NEBULA_MODEL_CATSEYE 3
#define COSMIC_NEBULA_MODEL_CRAB 4
#define COSMIC_NEBULA_MODEL_ORION 5
#define COSMIC_NEBULA_MODEL_EAGLE 6
#define COSMIC_NEBULA_MODEL_TARANTULA 7

#define COSMIC_SURFACE_ELLIPSOID 0
#define COSMIC_SURFACE_ANNULUS 1
#define COSMIC_SURFACE_PARABOLOID 2
// 1 つの模型の曲面の数の上限と、1 本の視線の交点の数の上限（楕円体と放物面は 2 つ、平らな環は 1 つ）。
#define COSMIC_SURFACE_MAX 10
#define COSMIC_SURFACE_HITS 20
// 下地（体積に置く光）= min(画像, COSMIC_NEBULA_BASE_FRACTION × 画像のミップ COSMIC_NEBULA_BASE_LOD 段目（1024² で 32²）)。
// 画像を超えないので、細部（画像 − 下地）は 0 以上になり、2 つを足すとちょうど画像になります。
#define COSMIC_NEBULA_BASE_LOD 5.0
#define COSMIC_NEBULA_BASE_FRACTION 0.7
// 主役の視線の上の下地の体積の積分の歩数（BakeGuideColumn）。
#define COSMIC_NEBULA_VOLUME_COLUMN_STEPS 256

// 塵の層を斜めに横切るときの濃さの倍率 1 / |法線・視線| の上限（2 倍）。
#define COSMIC_SURFACE_MIN_COSINE 0.5
// 主役のカメラ以外の向きから見た曲面の明るさを、主役のカメラから見た明るさの何倍までにするか。主役のカメラから寝て見える曲面の点は、
// 視線ごとの合計で割る値が小さいので、別の向きから正面に見ると何倍にも明るくなった（俯瞰の白く強い光）。
#define COSMIC_SURFACE_MAX_GAIN 3.0

struct CosmicNebulaSurface
{
    uint type;
    float3 center;
    // 正規直交の軸。楕円体は半径の向き、平らな環は面内の 2 軸と法線（axisZ）、放物面は使わない（z の向き）。
    float3 axisX, axisY, axisZ;
    // 楕円体: 3 軸の半径。平らな環: 面内の 2 軸の倍率（楕円の環）。
    float3 radii;
    // 平らな環: (半径、幅、倍率、-)。重み = gauss((楕円の半径 − 半径) × 倍率, 幅)。放物面: (中心の深さ、曲がり、-、-)。
    float4 profile;
    float weight;
    // 塊の細かさ（単位半径あたりの数。0 は塊なし）。殻の上の光を斑にします。
    float clumps;
};

// 分母に下限を入れます（使われない分岐を定数で畳み込むとき、幅 0 の曲面で「0 での割り算」の警告になったため）。
float CosmicModelGauss(float x, float sigma) { return exp(-(x * x) / max(sigma * sigma, 1e-12)); }

// 3 次元の値ノイズ（0..1）。描画も compute も同じ GPU の float で計算します。
float CosmicModelHash(float3 c) { return frac(sin(dot(c, float3(12.9898, 78.233, 37.719))) * 43758.5453); }

float CosmicModelNoise(float3 p)
{
    float3 i = floor(p), f = frac(p);
    float3 u = f * f * (3.0 - 2.0 * f);
    float a = lerp(lerp(CosmicModelHash(i), CosmicModelHash(i + float3(1, 0, 0)), u.x), lerp(CosmicModelHash(i + float3(0, 1, 0)), CosmicModelHash(i + float3(1, 1, 0)), u.x), u.y);
    float b = lerp(lerp(CosmicModelHash(i + float3(0, 0, 1)), CosmicModelHash(i + float3(1, 0, 1)), u.x), lerp(CosmicModelHash(i + float3(0, 1, 1)), CosmicModelHash(i + float3(1, 1, 1)), u.x), u.y);
    return lerp(a, b, u.z);
}

// 塊の濃さ（平均およそ 1）。
float CosmicModelClumps(float3 v, float frequency)
{
    float n = CosmicModelNoise(v * frequency + 7.1) * 0.65 + CosmicModelNoise(v * frequency * 2.3 + 3.7) * 0.35;
    return 0.15 + 2.4 * n * n;
}

// 主役のカメラ（画像の面の座標で eye）から、画像の面（z = 0）の点 p を通る視線。カメラが面の手前に無いとき
// （eye.z ≥ −1.2）は、真っすぐ奥へ（z の向き）の視線にします（CosmicVolumeBake.compute の BakeGuided と同じ規則）。
void CosmicGuideRay(float2 p, float3 eye, out float3 origin, out float3 direction)
{
    if (eye.z < -1.2)
    {
        origin = eye;
        direction = normalize(float3(p, 0.0) - eye);
    }
    else
    {
        origin = float3(p, -2.0);
        direction = float3(0.0, 0.0, 1.0);
    }
}

// 点 v を主役のカメラから画像の面へ投影した位置。
float2 CosmicGuideProject(float3 v, float3 eye)
{
    if (eye.z < -1.2) return eye.xy + (v.xy - eye.xy) * (-eye.z / max(v.z - eye.z, 1e-4));
    return v.xy;
}

// ── 曲面を作る ──
CosmicNebulaSurface CosmicSurfaceNone()
{
    CosmicNebulaSurface s = (CosmicNebulaSurface)0;
    s.axisX = float3(1, 0, 0); s.axisY = float3(0, 1, 0); s.axisZ = float3(0, 0, 1); s.radii = 1; s.weight = 0;
    return s;
}

// 楕円体。axisZ の向きの半径 c、それに垂直な side の向きの半径 b、残りの向きの半径 a。
CosmicNebulaSurface CosmicSurfaceEllipsoid(float3 center, float3 axisZ, float3 side, float a, float b, float c, float weight, float clumps)
{
    CosmicNebulaSurface s = CosmicSurfaceNone();
    s.type = COSMIC_SURFACE_ELLIPSOID; s.center = center;
    s.axisZ = normalize(axisZ); s.axisY = normalize(side - s.axisZ * dot(side, s.axisZ)); s.axisX = cross(s.axisY, s.axisZ);
    s.radii = float3(a, b, c); s.weight = weight; s.clumps = clumps;
    return s;
}

CosmicNebulaSurface CosmicSurfaceSphere(float3 center, float radius, float weight, float clumps)
{
    return CosmicSurfaceEllipsoid(center, float3(0, 0, 1), float3(0, 1, 0), radius, radius, radius, weight, clumps);
}

// 平らな環。法線 normal、面内の長軸 major の向きの倍率 a、それに垂直な倍率 b。重み = gauss((楕円の半径 − radius) × scale, width)。
CosmicNebulaSurface CosmicSurfaceAnnulus(float3 center, float3 normal, float3 major, float a, float b, float radius, float width, float scale, float weight, float clumps)
{
    CosmicNebulaSurface s = CosmicSurfaceNone();
    s.type = COSMIC_SURFACE_ANNULUS; s.center = center;
    s.axisZ = normalize(normal); s.axisX = normalize(major - s.axisZ * dot(major, s.axisZ)); s.axisY = cross(s.axisZ, s.axisX);
    s.radii = float3(a, b, 1); s.profile = float4(radius, width, scale, 0); s.weight = weight; s.clumps = clumps;
    return s;
}

// 手前（−z）へ開いた放物面 z = depth − curvature × |xy − center|²（HII 領域の、分子雲の手前の面にできた電離の面）。
CosmicNebulaSurface CosmicSurfaceParaboloid(float2 center, float depth, float curvature, float weight, float clumps)
{
    CosmicNebulaSurface s = CosmicSurfaceNone();
    s.type = COSMIC_SURFACE_PARABOLOID; s.center = float3(center, 0); s.profile = float4(depth, curvature, 0, 0); s.weight = weight; s.clumps = clumps;
    return s;
}

// 線分 a–b のまわりの半径 radius の細長い楕円体（柱）。
CosmicNebulaSurface CosmicSurfacePillar(float3 a, float3 b, float radius, float weight, float clumps)
{
    float3 axis = b - a;
    float halfLength = length(axis) * 0.5 + radius;
    float3 side = abs(axis.y) < 0.9 * length(axis) ? float3(0, 1, 0) : float3(1, 0, 0);
    return CosmicSurfaceEllipsoid((a + b) * 0.5, axis, side, radius, radius, halfLength, weight, clumps);
}

// ── 交点・法線・重み ──
// 視線 o + t·d（d は単位）と曲面の交点（t0 ≤ t1）。戻り値は交点の数（0〜2）。
uint CosmicSurfaceIntersect(CosmicNebulaSurface s, float3 o, float3 d, out float t0, out float t1)
{
    t0 = 0; t1 = 0;
    uint count = 0;
    if (s.type == COSMIC_SURFACE_ANNULUS)
    {
        float denominator = dot(d, s.axisZ);
        if (abs(denominator) > 1e-6)
        {
            t0 = dot(s.center - o, s.axisZ) / denominator; t1 = t0; count = 1;
        }
    }
    else
    {
        float a, b, c;
        if (s.type == COSMIC_SURFACE_ELLIPSOID)
        {
            float3 oc = o - s.center;
            float3 lo = float3(dot(oc, s.axisX), dot(oc, s.axisY), dot(oc, s.axisZ)) / s.radii;
            float3 ld = float3(dot(d, s.axisX), dot(d, s.axisY), dot(d, s.axisZ)) / s.radii;
            a = dot(ld, ld); b = 2.0 * dot(lo, ld); c = dot(lo, lo) - 1.0;
        }
        else
        {
            float k = s.profile.y;
            float2 oc = o.xy - s.center.xy;
            a = k * dot(d.xy, d.xy); b = d.z + 2.0 * k * dot(oc, d.xy); c = o.z - s.profile.x + k * dot(oc, oc);
        }
        if (abs(a) < 1e-8)
        {
            if (abs(b) > 1e-8) { t0 = -c / b; t1 = t0; count = 1; }
        }
        else
        {
            float disc = b * b - 4.0 * a * c;
            if (disc >= 0.0)
            {
                float root = sqrt(disc);
                float q = -0.5 * (b + (b >= 0.0 ? root : -root));
                float r0 = q / a, r1 = abs(q) > 1e-12 ? c / q : r0;
                t0 = min(r0, r1); t1 = max(r0, r1); count = 2;
            }
        }
    }
    return count;
}

float3 CosmicSurfaceNormal(CosmicNebulaSurface s, float3 x)
{
    float3 n = s.axisZ;
    if (s.type == COSMIC_SURFACE_PARABOLOID)
    {
        float2 d = x.xy - s.center.xy;
        n = normalize(float3(2.0 * s.profile.y * d, 1.0));
    }
    else if (s.type == COSMIC_SURFACE_ELLIPSOID)
    {
        float3 oc = x - s.center;
        float3 e = float3(dot(oc, s.axisX), dot(oc, s.axisY), dot(oc, s.axisZ)) / (s.radii * s.radii);
        n = normalize(s.axisX * e.x + s.axisY * e.y + s.axisZ * e.z);
    }
    return n;
}

// 曲面を見る向きによる明るさの倍率（cosine = |法線・視線|）。正面で 1、寝るほど cos² でなだらかに暗くなり、見かけの輪郭で 0 になります。
// ガスの雲に輪郭が出ないように（2026-10-05 のユーザーの指摘「星雲、ガス系に輪郭がでるのはいけない」）。以前の、斜めほど明るく輪郭の近くの
// 狭い帯だけで消す倍率では、近く大きく写ると、くっきりした縁に見えた。
// 主役の視線ごとの合計（BakeGuideColumn）にも同じ倍率を使うので、主役のカメラから見た明るさの合計は画像と一致したままです。
float CosmicSurfaceSlant(float cosine)
{
    return cosine * cosine;
}

// 曲面の点 x の重み（曲面の重み × 平らな環の半径の分布 × 塊）。
float CosmicSurfaceWeight(CosmicNebulaSurface s, float3 x)
{
    float w = s.weight;
    if (s.type == COSMIC_SURFACE_ANNULUS)
    {
        float3 local = x - s.center;
        float u = dot(local, s.axisX) / s.radii.x, v = dot(local, s.axisY) / s.radii.y;
        w *= CosmicModelGauss((sqrt(u * u + v * v) - s.profile.x) * s.profile.z, s.profile.y);
    }
    if (s.clumps > 0.0) w *= CosmicModelClumps(x, s.clumps);
    return w;
}

// 模型に乗らない画像の光（暗い外側の広がり、残った星など）を置く、外側の淡い球殻。
CosmicNebulaSurface CosmicSurfaceHaze() { return CosmicSurfaceSphere(0, 0.85, 0.01, 0.0); }

// ── 模型 ──
// 環状星雲（M57、NGC 6720）。O'Dell ほか 2013（AJ 145, 92）: 主な環は中央に空洞のある不規則な円盤で、それに垂直な淡いローブが
// ほぼ観測者の方向へ伸びる（2007 年の模型では半径 0.10・0.13・0.20 pc の三軸楕円体を、ほぼ極の方向から見ている）。
// 画像（heic1310a、視野 2.1′）の計測: 環の明るさの最大は半径 0.38（画像の半幅 = 1）、幅は約 0.25、長軸は横から −15°、短軸と長軸の比 0.86。
// 主な環は平らな環、ローブは極方向の半長 0.62 の楕円体（2007 年の模型の比 0.20 ÷ 0.13 ≈ 1.5）。
CosmicNebulaSurface CosmicRingSurface(uint i)
{
    float3 axis = normalize(float3(0.04, 0.10, 1.0));
    float3 major = float3(0.966, -0.259, 0.0);
    float3 minorAxis = cross(axis, normalize(major - axis * dot(major, axis)));
    CosmicNebulaSurface s = CosmicSurfaceNone();
    if (i == 0) s = CosmicSurfaceAnnulus(0, axis, major, 0.42, 0.36, 1.0, 0.085, 0.39, 1.0, 0.0);
    else if (i == 1) s = CosmicSurfaceEllipsoid(0, axis, minorAxis, 0.42, 0.36, 0.62, 0.3, 0.0);
    else if (i == 2) s = CosmicSurfaceSphere(0, 0.72, 0.06, 0.0);
    else if (i == 3) s = CosmicSurfaceHaze();
    return s;
}

// らせん星雲（NGC 7293）。O'Dell ほか 2004（AJ 128, 2339）: 直径 499″ の内側の円盤（軸の方向に淡いガス）と、その面に大きく傾いた
// 直径 742″ の外側の輪、さらに外の直径 1500″ の輪。画像（eso1205a、視野 37.5′ = 半幅 1125″）では内側の円盤の半径 0.22、外側の輪 0.33、
// 最も外側 0.67。傾きは文献で値が揃わない（両者の角は約 78° とする報告がある）ので、画像の楕円（長軸の向き (−0.76, −0.65)、
// 短軸 ÷ 長軸 ≈ 0.65）に合うように、内側の円盤は視線から 20°、外側の輪は反対へ 50° 傾けます（両者の角 70°）。
CosmicNebulaSurface CosmicHelixSurface(uint i)
{
    float3 major = normalize(float3(-0.76, -0.65, 0.0));
    float3 minor = float3(-major.y, major.x, 0.0);
    float3 innerAxis = normalize(float3(0, 0, 1) * cos(radians(20.0)) + minor * sin(radians(20.0)));
    float3 outerAxis = normalize(float3(0, 0, 1) * cos(radians(50.0)) - minor * sin(radians(50.0)));
    CosmicNebulaSurface s = CosmicSurfaceNone();
    if (i == 0) s = CosmicSurfaceAnnulus(0, innerAxis, major, 0.22, 0.22, 0.7, 0.45, 1.0, 1.0, 0.0);
    else if (i == 1) s = CosmicSurfaceEllipsoid(0, innerAxis, major, 0.22, 0.22, 0.5, 0.15, 0.0);
    else if (i == 2) s = CosmicSurfaceAnnulus(0, outerAxis, major, 1.0, 1.0, 0.33, 0.05, 1.0, 1.0, 0.0);
    else if (i == 3) s = CosmicSurfaceAnnulus(0, normalize(float3(0.0, 0.26, 1.0)), major, 1.0, 1.0, 0.67, 0.07, 1.0, 0.25, 0.0);
    else if (i == 4) s = CosmicSurfaceHaze();
    return s;
}

// キャッツアイ星雲（NGC 6543、中心部）。Reed ほか 1999（AJ 118, 2430）: 重なった細長い泡（楕円体の殻）が中心にあり、両端に結節（ジェット）、
// 外側を同心の淡い殻の環が囲む。画像（heic0414a、視野 1.17′）の計測: 中心部の長軸の向き (0.63, 0.78)、広がり（標準偏差）0.26 × 0.30。
// 泡の傾きは観測から一意に決まらないので、2 つの泡の長軸を視線から互いに反対へ傾け、正面から重なって見えるようにします。
CosmicNebulaSurface CosmicCatsEyeSurface(uint i)
{
    float3 major = normalize(float3(0.63, 0.78, 0.0));
    float3 minor = float3(-major.y, major.x, 0.0);
    float3 z = float3(0, 0, 1);
    float3 inPlaneB = major * cos(radians(35.0)) + minor * sin(radians(35.0));
    float3 knot = major * 0.6 + z * 0.15;
    CosmicNebulaSurface s = CosmicSurfaceNone();
    if (i == 0) s = CosmicSurfaceEllipsoid(0, major * cos(radians(30.0)) + z * sin(radians(30.0)), minor, 0.20, 0.20, 0.36, 1.0, 0.0);
    else if (i == 1) s = CosmicSurfaceEllipsoid(0, inPlaneB * cos(radians(25.0)) - z * sin(radians(25.0)), cross(z, inPlaneB), 0.16, 0.16, 0.30, 0.8, 0.0);
    else if (i == 2) s = CosmicSurfaceSphere(knot, 0.06, 0.6, 0.0);
    else if (i == 3) s = CosmicSurfaceSphere(-knot, 0.06, 0.6, 0.0);
    // 同心の淡い殻の環（外へ行くほど弱く）。最後は全体の淡い暈を兼ねる。
    else if (i <= 8) s = CosmicSurfaceSphere(0, 0.42 + 0.1 * (i - 4), 0.07 - 0.01 * (i - 4), 0.0);
    return s;
}

// かに星雲（M1）。Hester 2008（ARA&A 46, 127）: 長軸 4.4 pc・短軸 2.9 pc の楕円体で、中を満たす青白いシンクロトロンの光と、表面近くの殻に赤橙の繊維。
// 画像（heic0515a、視野 6.41′）の計測: 長軸の向き (−0.75, −0.66)、画像の中で半長 0.85・半短 0.6。奥行きの半径は短軸と同じとし、長軸を視線へ 15° 傾けます。
// 繊維もシンクロトロンも表面の殻（0.86 倍）1 枚に置き、塊で斑にして繊維の檻のように見せます（中に殻を足すと、斜めから見て
// 殻の中の卵や玉ねぎのような固い物体に見えた。2026-10-05 のユーザーの判断で作り直した）。
CosmicNebulaSurface CosmicCrabSurface(uint i)
{
    float3 sky = normalize(float3(-0.75, -0.66, 0.0));
    float3 axis = normalize(sky * cos(radians(15.0)) + float3(0, 0, 1) * sin(radians(15.0)));
    float3 side = float3(-sky.y, sky.x, 0.0);
    CosmicNebulaSurface s = CosmicSurfaceNone();
    if (i == 0) s = CosmicSurfaceEllipsoid(0, axis, side, 0.62 * 0.86, 0.60 * 0.86, 0.85 * 0.86, 1.0, 6.0);
    else if (i == 1) s = CosmicSurfaceHaze();
    return s;
}

// オリオン大星雲（M42）。O'Dell 2001（ARA&A 39, 99）: 明るい星雲は、分子雲 OMC-1 の手前の面にできた凹んだ薄い電離の層（ブリスター）で、
// その手前にトラペジウム（θ1 Ori C）があり、さらに手前を中性のガスと塵の「ベール」が覆う。
// 画像（heic0601a、視野 30′）の計測: 明るい中心核は p ≈ (−0.10, 0.08)、M43 は左上 (−0.41, 0.65) の小さな別の星雲。
CosmicNebulaSurface CosmicOrionSurface(uint i)
{
    CosmicNebulaSurface s = CosmicSurfaceNone();
    if (i == 0) s = CosmicSurfaceParaboloid(float2(-0.10, 0.08), 0.28, 0.5, 1.0, 4.0);
    else if (i == 1) s = CosmicSurfaceSphere(float3(-0.41, 0.65, 0.1), 0.10, 0.8, 0.0);
    else if (i == 2) s = CosmicSurfaceHaze();
    return s;
}

// わし星雲（M16）。星団 NGC 6611 が吹き払った空洞の奥の壁（ブリスター）と、壁から突き出す柱（創造の柱）。McLeod ほか 2015（MUSE）:
// 柱は星団の手前・星団・奥の 3 層に分かれ、柱 1 は北半分が星団の奥、南半分が手前の 2 本の重なり、柱 2・3 は星団と観測者の間にある。
// 画像（eso0926a、視野 32′）の計測: 明るい空洞の中心は p ≈ (0.0, −0.1)、柱は p ≈ (−0.12〜0.0, −0.36〜−0.22) の小さな暗い構造。
CosmicNebulaSurface CosmicEagleSurface(uint i)
{
    CosmicNebulaSurface s = CosmicSurfaceNone();
    if (i == 0) s = CosmicSurfaceParaboloid(float2(0.0, -0.1), 0.3, 0.45, 1.0, 4.0);
    // 柱は暗い塵の柱（画像の柱の所の明るさは、その奥の壁の光）なので、重みを小さくして光は奥の壁に置き、柱には手前の塵の層を載せます
    // （重みを大きくすると、奥の壁の光が柱に乗り、斜めから白く光る楕円として浮いて見えた）。
    else if (i == 1) s = CosmicSurfacePillar(float3(-0.12, -0.37, 0.22), float3(-0.11, -0.29, 0.14), 0.022, 0.15, 0.0);
    else if (i == 2) s = CosmicSurfacePillar(float3(-0.11, -0.29, 0.05), float3(-0.10, -0.22, -0.02), 0.020, 0.15, 0.0);
    else if (i == 3) s = CosmicSurfacePillar(float3(-0.05, -0.37, 0.02), float3(-0.05, -0.30, -0.06), 0.018, 0.15, 0.0);
    else if (i == 4) s = CosmicSurfacePillar(float3(0.00, -0.36, 0.0), float3(0.00, -0.31, -0.05), 0.014, 0.15, 0.0);
    else if (i == 5) s = CosmicSurfaceHaze();
    return s;
}

// タランチュラ星雲（大マゼラン雲の 30 Doradus）。Chu と Kennicutt 1994: 中心の星団 R136 のまわりに、大きさの違う複数の泡（殻）が重なる。
// R136 は画像（tarantula、視野 13.4′ × 13.7′）の中心の座標とほぼ同じ位置（p ≈ (−0.02, 0.06)）。R136 が吹き払った空洞を、奥の壁
// （手前へ開いた放物面）と手前のドーム（奥へ開いた放物面）の、開いた 2 枚の曲面ではさむ形です（閉じた泡の球にすると、斜めから見て
// ガラス玉の重なりのように見えた。2026-10-05 のユーザーの判断で作り直した）。
CosmicNebulaSurface CosmicTarantulaSurface(uint i)
{
    CosmicNebulaSurface s = CosmicSurfaceNone();
    if (i == 0) s = CosmicSurfaceParaboloid(float2(-0.02, 0.06), 0.38, 0.35, 1.0, 4.0);
    else if (i == 1) s = CosmicSurfaceParaboloid(float2(-0.02, 0.06), -0.18, -0.45, 0.6, 5.0);
    else if (i == 2) s = CosmicSurfaceHaze();
    return s;
}

// ── 下地の体積 ──
// 縁へなだらかに薄れる体積の密度（ガウス型の広がり）。主役の視線ごとの積分（BakeGuideColumn の B）で割るので、正面は画像どおりです。
// center を中心に、axis の向きの半径 c、side の向きの半径 b、残りの向きの半径 a の楕円体のガウス（半径の所で e^-1）。
float CosmicModelBlob(float3 v, float3 center, float3 axis, float3 side, float a, float b, float c)
{
    float3 z = normalize(axis);
    float3 y = normalize(side - z * dot(side, z));
    float3 x = cross(y, z);
    float3 d = v - center;
    float3 e = float3(dot(d, x) / a, dot(d, y) / b, dot(d, z) / c);
    return exp(-dot(e, e));
}

// 手前へ開いた放物面のまわりの厚い層（HII 領域の下地。奥の壁のまわりに光が厚く広がる）。
float CosmicModelThickBlister(float3 v, float2 center, float depth, float curvature, float thickness, float extent)
{
    float2 d = v.xy - center;
    float surface = depth - curvature * dot(d, d);
    return CosmicModelGauss(v.z - surface, thickness) * exp(-dot(d, d) / (extent * extent));
}

float CosmicNebulaVolumeDensity(uint model, float3 v)
{
    float density = 0.0;
    if (model == COSMIC_NEBULA_MODEL_RING)
    {
        float3 axis = normalize(float3(0.04, 0.10, 1.0));
        density = CosmicModelBlob(v, 0, axis, float3(-0.259, -0.966, 0.0), 0.50, 0.45, 0.60);
    }
    else if (model == COSMIC_NEBULA_MODEL_HELIX)
    {
        density = CosmicModelBlob(v, 0, float3(0, 0, 1), float3(0.65, -0.76, 0.0), 0.50, 0.42, 0.40);
    }
    else if (model == COSMIC_NEBULA_MODEL_CATSEYE)
    {
        density = CosmicModelBlob(v, 0, float3(0.63, 0.78, 0.25), float3(-0.78, 0.63, 0.0), 0.32, 0.32, 0.50);
    }
    else if (model == COSMIC_NEBULA_MODEL_CRAB)
    {
        float3 sky = normalize(float3(-0.75, -0.66, 0.0));
        density = CosmicModelBlob(v, 0, sky * cos(radians(15.0)) + float3(0, 0, 1) * sin(radians(15.0)), float3(-sky.y, sky.x, 0.0), 0.50, 0.48, 0.70);
    }
    else if (model == COSMIC_NEBULA_MODEL_ORION)
    {
        density = CosmicModelThickBlister(v, float2(-0.10, 0.08), 0.20, 0.5, 0.25, 0.9);
    }
    else if (model == COSMIC_NEBULA_MODEL_EAGLE)
    {
        density = CosmicModelThickBlister(v, float2(0.0, -0.1), 0.22, 0.45, 0.25, 0.9);
    }
    else if (model == COSMIC_NEBULA_MODEL_TARANTULA)
    {
        density = CosmicModelThickBlister(v, float2(-0.02, 0.06), 0.20, 0.35, 0.28, 0.9);
    }
    return density;
}

// 模型の i 番目の曲面（重みが 0 なら無い）。戻り値は 1 か所で返します（ループの中から呼ぶと、途中の return が
// 「初期化されていない可能性のある変数」の警告になったため）。
CosmicNebulaSurface CosmicNebulaSurfaceAt(uint model, uint i)
{
    CosmicNebulaSurface s = CosmicSurfaceNone();
    if (model == COSMIC_NEBULA_MODEL_RING) s = CosmicRingSurface(i);
    else if (model == COSMIC_NEBULA_MODEL_HELIX) s = CosmicHelixSurface(i);
    else if (model == COSMIC_NEBULA_MODEL_CATSEYE) s = CosmicCatsEyeSurface(i);
    else if (model == COSMIC_NEBULA_MODEL_CRAB) s = CosmicCrabSurface(i);
    else if (model == COSMIC_NEBULA_MODEL_ORION) s = CosmicOrionSurface(i);
    else if (model == COSMIC_NEBULA_MODEL_EAGLE) s = CosmicEagleSurface(i);
    else if (model == COSMIC_NEBULA_MODEL_TARANTULA) s = CosmicTarantulaSurface(i);
    return s;
}

// 塵の薄い層を載せる交点: 主役の視線の上で、重みの合計がこの割合に達する交点。惑星状星雲と超新星残骸は半分（塵は放射と混ざる）、
// HII 領域は手前（オリオンのベールは星雲の全体の手前、わし・タランチュラの暗い構造は手前寄り）。
float CosmicNebulaDustFraction(uint model)
{
    float fraction = 0.5;
    if (model == COSMIC_NEBULA_MODEL_ORION) fraction = 0.02;
    else if (model == COSMIC_NEBULA_MODEL_EAGLE) fraction = 0.2;
    else if (model == COSMIC_NEBULA_MODEL_TARANTULA) fraction = 0.3;
    return fraction;
}

// 交点の番号（曲面の番号 × 2 + 0 手前 / 1 奥。主役の視線の上で数えます）。塵の層を載せる交点の照合に使います。
float CosmicSurfaceHitCode(uint i, uint root) { return (float)(i * 2 + root); }

// 点 x が、主役の視線の上で曲面 s の手前（0）と奥（1）のどちらの交点か。
uint CosmicSurfaceHeroRoot(CosmicNebulaSurface s, float3 x, float3 eye)
{
    float3 origin, direction;
    CosmicGuideRay(CosmicGuideProject(x, eye), eye, origin, direction);
    float t0, t1;
    uint count = CosmicSurfaceIntersect(s, origin, direction, t0, t1);
    float t = dot(x - origin, direction);
    return count == 2 && abs(t - t1) < abs(t - t0) ? 1u : 0u;
}

#endif
