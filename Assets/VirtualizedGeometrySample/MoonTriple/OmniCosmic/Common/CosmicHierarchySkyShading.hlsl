// CosmicHierarchy の空（架空の星をやめ、データと一致させる。2026-10-05 のユーザー判断）。
// ・天の川の分解できない淡い光: 天の川の点群と同じ密度の式（指数円盤・4 本腕・棒状バルジ）を視線に沿って積分し、塵で減光します。
//   式と定数は Runtime/Hierarchy/CosmicMilkyWayLight.cs と同じ（塵の塊のノイズはシェーダーだけ。天の川の座標に固定なので、動くと視差で動く）。
// ・データの星の地図: いて座A* から見た天の川の点群と恒星のカタログを、立方体の 6 面 × N² のセルに 1 個ずつ焼いたテクスチャ（6N × N、
//   r,g = セルの中のずれ −0.5..0.5、b = 明るさ（点群と同じ尺度の光束）、a = 表面温度 / 10000）。レンズの画面外と突入の背景に使います。
// 座標: 天の川の座標系（kpc、原点 = いて座A*、Y = 円盤の法線、X = 中心から太陽）。ワールドの向きからは _CosmicHierarchyMilkyWayX/Y/Z との内積で移します。
// CosmicNoise.hlsl（CosmicFbm、CosmicBlackbodyChroma）を先に読み込んでください。
#ifndef COSMIC_HIERARCHY_SKY_SHADING_INCLUDED
#define COSMIC_HIERARCHY_SKY_SHADING_INCLUDED

// 天の川の座標系の軸（ワールドの向き）。
float4 _CosmicHierarchyMilkyWayX;
float4 _CosmicHierarchyMilkyWayY;
float4 _CosmicHierarchyMilkyWayZ;
// xyz = カメラの位置（天の川の座標系、kpc）、w = 天の川の中にいる重み（外へ出ると 0、天の川の体積と二重に描かない）。
float4 _CosmicHierarchySkyOrigin;
// x = 淡い光の目の順応の倍率（カメラの位置の空の平均の明るさから。CosmicMilkyWayLight.AdaptationGain、Director が書く）、y = 1 で診断（いて座A* の空を星の地図だけにする）。
float4 _CosmicHierarchySkyLight;
// データの星の地図。_CosmicHierarchyStarMapInfo: x = 1 面のセルの数 N、y = 明るさの倍率、z = 地図がある（1）。
TEXTURE2D(_CosmicHierarchyStarMap);
float4 _CosmicHierarchyStarMapInfo;
// 淡い光の小さな地図（立方体の 6 面 × N² を横に並べた RenderTexture、Runtime/Hierarchy/CosmicHierarchySkyAtlas.cs が CosmicHierarchySkyBake.shader で描く。
// 中身は「明るさの面密度」の色で、画素の大きさと順応の倍率は引く側で掛ける）。_CosmicHierarchySkyAtlas = カメラの位置から、_CosmicHierarchySgrAAtlas = いて座A* から。
// _CosmicHierarchySkyAtlasInfo: x = カメラの位置の地図がある（1）、y = いて座A* の地図がある（1）、z = 1 面のセルの数 N。無いとき（Edit モードなど）は視線を直接積分する。
TEXTURE2D(_CosmicHierarchySkyAtlas); SAMPLER(sampler_CosmicHierarchySkyAtlas);
TEXTURE2D(_CosmicHierarchySgrAAtlas); SAMPLER(sampler_CosmicHierarchySgrAAtlas);
float4 _CosmicHierarchySkyAtlasInfo;

// 天の川の差動回転（CosmicHierarchyDirector.WriteGalaxyRotation、CosmicGalacticRotation.cs）。
// _CosmicGalaxyRotation: x = J2000 からの Myr、y = 腕の角度、z = 棒の角度（ラジアン、θ = atan2(z, x) が増える向きが正）、w = 回転の向きの符号。
// _CosmicGalaxySun: xyz = 太陽の位置（天の川の点群の物体空間）、w = 太陽のまわりで点を描かない半径。
// _CosmicGalaxyCurve: x = 回転曲線の平坦部 Vc（km/s）、y = 立ち上がりの半径（kpc）、z = km/s/kpc → rad/Myr、w = R0（kpc）。
#ifndef COSMIC_GALAXY_ROTATION_DECLARED
#define COSMIC_GALAXY_ROTATION_DECLARED
float4 _CosmicGalaxyRotation;
float4 _CosmicGalaxySun;
float4 _CosmicGalaxyCurve;
#endif

#define COSMIC_MW_R0 8.275
#define COSMIC_MW_SCALE_LENGTH 2.6
#define COSMIC_MW_SCALE_HEIGHT 0.3
#define COSMIC_MW_EDGE_START 15.0
#define COSMIC_MW_EDGE_END 20.0
#define COSMIC_MW_ARMS 4.0
#define COSMIC_MW_PITCH 0.20943951
#define COSMIC_MW_BAR 0.47123890
#define COSMIC_MW_BULGE_LONG 1.8
#define COSMIC_MW_BULGE_SHORT 0.4
#define COSMIC_MW_BULGE_HEIGHT 0.6
#define COSMIC_MW_SOLAR_DENSITY 0.05
#define COSMIC_MW_BULGE_LUMINOSITY 1e10
#define COSMIC_MW_SOLAR_EXTINCTION 1.0
#define COSMIC_MW_DUST_HEIGHT 0.1
#define COSMIC_MW_DUST_LENGTH 3.5
#define COSMIC_MW_PATH_START 0.001
#define COSMIC_MW_PATH_END 40.0
#define COSMIC_MW_PATH_STEPS 32
#define COSMIC_MW_ARM_CONTRAST 0.35
#define COSMIC_MW_ARM_FADE_START 1.5
#define COSMIC_MW_ARM_FADE_END 4.0
// 歩数の上限と、円盤の層の歩幅の規則（CosmicMilkyWayLight.NextEdge と同じ。薄い円盤を幾何級数の粗い歩幅で標本化すると同心円の縞になった）。
#define COSMIC_MW_MAX_STEPS 80
#define COSMIC_MW_LAYER_HALF 1.2
#define COSMIC_MW_DUST_HALF 0.35
#define COSMIC_MW_DUST_RISE 0.08
#define COSMIC_MW_DISK_RISE 0.25
#define COSMIC_MW_LAYER_MAX_STEP 1.0
#define COSMIC_MW_MIN_STEP 0.002
#define COSMIC_MW_EDGE_MIN_STEP 0.01
// 100 / L(M_V = 0)（L⊙、太陽の M_V = 4.83）× 10^(0.4 × 6.5)（露出）× 表示の倍率 3.5。CosmicMilkyWayLight.PixelIntensity と同じ。
#define COSMIC_MW_TO_PIXEL (100.0 / 85.5067 * 398.107 * 3.5)

float CosmicMwSmooth(float a, float b, float x){float t = saturate((x - a) / (b - a));return t * t * (3.0 - 2.0 * t);}

// 腕の濃淡（CosmicMilkyWayLight.Arm と同じ。点群の濃淡を ARM_CONTRAST の割合まで弱め、中心の近くでは消す）。
float CosmicMwArm(float radius, float theta)
{
    float phase = COSMIC_MW_ARMS * (theta - log(max(radius, 0.5) / COSMIC_MW_R0) / tan(COSMIC_MW_PITCH));
    float a = 0.5 + 0.5 * cos(phase);
    float shape = (0.35 + 0.65 * a * a * a) / 0.553125;
    return 1.0 + COSMIC_MW_ARM_CONTRAST * CosmicMwSmooth(COSMIC_MW_ARM_FADE_START, COSMIC_MW_ARM_FADE_END, radius) * (shape - 1.0);
}

// 光度の密度（L⊙/pc³）: x = 円盤、y = バルジ（色を分けるため別に返す）。
float2 CosmicMwLuminosity(float3 p)
{
    float radius = length(p.xz), theta = atan2(p.z, p.x);
    float edge = 1.0 - CosmicMwSmooth(COSMIC_MW_EDGE_START, COSMIC_MW_EDGE_END, radius);
    float disk = COSMIC_MW_SOLAR_DENSITY * exp(-(radius - COSMIC_MW_R0) / COSMIC_MW_SCALE_LENGTH) * exp(-abs(p.y) / COSMIC_MW_SCALE_HEIGHT) * edge
        * CosmicMwArm(radius, theta - _CosmicGalaxyRotation.y) / CosmicMwArm(COSMIC_MW_R0, 0.0);
    // 腕と棒は時刻の角度だけ回ります（CosmicMilkyWayLight.Luminosity と同じ。正規化の CosmicMwArm(R0, 0) は回さない）。
    float c = cos(COSMIC_MW_BAR + _CosmicGalaxyRotation.z), s = sin(COSMIC_MW_BAR + _CosmicGalaxyRotation.z);
    float u = p.x * c + p.z * s, v = -p.x * s + p.z * c;
    float peak = COSMIC_MW_BULGE_LUMINOSITY / (15.7496099 * COSMIC_MW_BULGE_LONG * COSMIC_MW_BULGE_SHORT * COSMIC_MW_BULGE_HEIGHT * 1e9);
    float bulge = peak * exp(-(u * u / (2.0 * COSMIC_MW_BULGE_LONG * COSMIC_MW_BULGE_LONG) + v * v / (2.0 * COSMIC_MW_BULGE_SHORT * COSMIC_MW_BULGE_SHORT)
        + p.y * p.y / (2.0 * COSMIC_MW_BULGE_HEIGHT * COSMIC_MW_BULGE_HEIGHT)));
    return float2(disk, bulge);
}

// 減光（V 等級/kpc）。塵の塊は天の川の座標の 3 次元ノイズ（約 150 pc の大きさ、平均は 1 前後、推定）。
float CosmicMwExtinction(float3 p, float footprint)
{
    float radius = length(p.xz);
    float smooth = COSMIC_MW_SOLAR_EXTINCTION * exp(-(radius - COSMIC_MW_R0) / COSMIC_MW_DUST_LENGTH) * exp(-abs(p.y) / COSMIC_MW_DUST_HEIGHT)
        * (1.0 - CosmicMwSmooth(COSMIC_MW_EDGE_START, COSMIC_MW_EDGE_END, radius));
    // 減光がほぼ無い高さ（|y| > 約 0.46 kpc）では塊のノイズを引かない（平均の値のまま。塵の層の外の歩が大半で、ノイズが最も重い）。
    if (smooth < 0.01) return smooth;
    float clump = 0.2 + 1.6 * saturate(0.5 + 0.9 * CosmicFbm(p * 6.0, 4, footprint * 6.0, 2.0, 0.55));
    return smooth * clump;
}

// 天の川の淡い光の面密度（色つき、画素の大きさ・順応を掛ける前）。origin は天の川の座標系（kpc）、directionWorld はワールドの単位ベクトル、
// footprintAngle は塵の塊のノイズを間引く角度（1 画素または地図の 1 セルの大きさ、ラジアン）。
float3 CosmicMilkyWaySurface(float3 origin, float3 directionWorld, float footprintAngle)
{
    float3 d = float3(dot(directionWorld, _CosmicHierarchyMilkyWayX.xyz), dot(directionWorld, _CosmicHierarchyMilkyWayY.xyz), dot(directionWorld, _CosmicHierarchyMilkyWayZ.xyz));
    float ratio = pow(COSMIC_MW_PATH_END / COSMIC_MW_PATH_START, 1.0 / COSMIC_MW_PATH_STEPS);
    float ady = max(abs(d.y), 1e-3);
    float previous = 0.0, optical = 0.0;
    float2 sum = 0.0;
    [loop] for (int i = 0; i < COSMIC_MW_MAX_STEPS; i++)
    {
        if (previous >= COSMIC_MW_PATH_END) break;
        // 歩幅: 幾何級数を基本に、円盤の層（|y| ≤ 1.2 kpc）では 1 歩で動く高さを制限し、層へ向かうときは層の縁にちょうど着く。
        float stepLength = previous > 0.0 ? max(previous * (ratio - 1.0), COSMIC_MW_PATH_START * ratio) : COSMIC_MW_PATH_START * ratio;
        float y = origin.y + d.y * previous, ay = abs(y);
        if (ay <= COSMIC_MW_LAYER_HALF)
        {
            float rise = ay <= COSMIC_MW_DUST_HALF ? COSMIC_MW_DUST_RISE : COSMIC_MW_DISK_RISE;
            stepLength = min(stepLength, min(max(rise / ady, COSMIC_MW_MIN_STEP), COSMIC_MW_LAYER_MAX_STEP));
        }
        else if (y * d.y < 0.0)
        {
            stepLength = min(stepLength, max((ay - COSMIC_MW_LAYER_HALF) / ady, COSMIC_MW_EDGE_MIN_STEP));
        }
        float next = previous + stepLength, mid = 0.5 * (previous + next), dl = next - previous;
        float3 p = origin + d * mid;
        // 塵の塊のノイズは、歩幅より細かい模様の段を footprint で消す（歩幅方向の標本化不足を避ける）。
        float extinction = CosmicMwExtinction(p, max(footprintAngle * mid, dl)) * dl;
        sum += CosmicMwLuminosity(p) * exp(-0.921 * (optical + 0.5 * extinction)) * dl * 1000.0;
        optical += extinction;
        previous = next;
    }
    // 円盤（若い星を含む、白っぽい）とバルジ（古い星、橙）の色。
    return sum.x * float3(1.0, 0.94, 0.86) + sum.y * float3(1.0, 0.80, 0.58);
}

// 天の川の淡い光の 1 画素の明るさと色（視線を直接積分する。地図が無いとき・地図を描くとき用）。
float3 CosmicMilkyWayLight(float3 origin, float3 directionWorld, float pixelAngle)
{
    return CosmicMilkyWaySurface(origin, directionWorld, pixelAngle) * (pixelAngle * pixelAngle) * COSMIC_MW_TO_PIXEL * _CosmicHierarchySkyLight.x;
}

// 向き → 立方体の面（0..5: +X −X +Y −Y +Z −Z）と面の座標 0..1（CosmicMilkyWayLight.CubeFace と同じ）。
int CosmicHierarchyCubeFace(float3 d, out float2 uv)
{
    float3 a = abs(d);
    int face; float sc, tc, ma;
    if (a.x >= a.y && a.x >= a.z) { face = d.x >= 0 ? 0 : 1; ma = a.x; sc = d.x >= 0 ? -d.z : d.z; tc = -d.y; }
    else if (a.y >= a.z) { face = d.y >= 0 ? 2 : 3; ma = a.y; sc = d.x; tc = d.y >= 0 ? d.z : -d.z; }
    else { face = d.z >= 0 ? 4 : 5; ma = a.z; sc = d.z >= 0 ? d.x : -d.x; tc = -d.y; }
    uv = 0.5 * (float2(sc, tc) / ma + 1.0);
    return face;
}

float3 CosmicHierarchyCubeDirection(int face, float2 uv)
{
    float sc = 2.0 * uv.x - 1.0, tc = 2.0 * uv.y - 1.0;
    float3 d = face == 0 ? float3(1, -tc, -sc) : face == 1 ? float3(-1, -tc, sc) : face == 2 ? float3(sc, 1, tc)
        : face == 3 ? float3(sc, -1, -tc) : face == 4 ? float3(sc, -tc, 1) : float3(-sc, -tc, -1);
    return normalize(d);
}

// 淡い光の地図を向きで引く（面の中だけで線形補間する。面の縁の補間は隣の面を見ないが、地図の 1 セルはなだらかな光の 0.5° ほどなので差は見えない）。
float3 CosmicHierarchyAtlasSurface(TEXTURE2D_PARAM(atlas, atlasSampler), float3 directionWorld)
{
    float cells = _CosmicHierarchySkyAtlasInfo.z;
    float2 uv;
    int face = CosmicHierarchyCubeFace(directionWorld, uv);
    float2 p = clamp(uv * cells, 0.5, cells - 0.5);
    return SAMPLE_TEXTURE2D_LOD(atlas, atlasSampler, float2((face * cells + p.x) / (6.0 * cells), p.y / cells), 0).rgb;
}

// カメラの位置から見た淡い光（スカイボックス）。地図があれば引き、無ければ視線を直接積分する。
float3 CosmicHierarchySkyLight(float3 originKpc, float3 directionWorld, float pixelAngle)
{
    if (_CosmicHierarchySkyAtlasInfo.x > 0.5)
    {
        return CosmicHierarchyAtlasSurface(TEXTURE2D_ARGS(_CosmicHierarchySkyAtlas, sampler_CosmicHierarchySkyAtlas), directionWorld)
            * (pixelAngle * pixelAngle) * COSMIC_MW_TO_PIXEL * _CosmicHierarchySkyLight.x;
    }
    return CosmicMilkyWayLight(originKpc, directionWorld, pixelAngle);
}

// データの星の地図（いて座A* から見た空）。セルとまわりの 8 セルを見て、画素の角度の大きさの光点で描きます（点群と同じ上限 6）。
float3 CosmicHierarchyStarMap(float3 directionWorld, float pixelAngle)
{
    if (_CosmicHierarchyStarMapInfo.z < 0.5) return 0;
    int cells = (int)_CosmicHierarchyStarMapInfo.x;
    float2 uv;
    int face = CosmicHierarchyCubeFace(directionWorld, uv);
    int2 cell = min((int2)floor(uv * cells), cells - 1);
    float size = max(pixelAngle * 1.1, 1e-6);
    float3 color = 0;
    [unroll] for (int j = -1; j <= 1; j++)
    {
        [unroll] for (int i = -1; i <= 1; i++)
        {
            int2 c = cell + int2(i, j);
            if (any(c < 0) || any(c >= cells)) continue;
            float4 star = LOAD_TEXTURE2D(_CosmicHierarchyStarMap, int2(face * cells + c.x, c.y));
            if (star.b <= 0) continue;
            float3 center = CosmicHierarchyCubeDirection(face, (float2(c) + 0.5 + star.rg) / cells);
            float distance = length(directionWorld - center);
            float core = exp(-distance * distance / (size * size));
            color += CosmicBlackbodyChroma(star.a * 10000.0) * min(star.b, 6.0) * core;
        }
    }
    return color * _CosmicHierarchyStarMapInfo.y;
}

// いて座A* の近くの空（レンズの画面外・突入の背景）: いて座A* の位置から見た淡い光と、データの星の地図。
float3 CosmicHierarchySgrASky(float3 directionWorld, float pixelAngle)
{
    // _CosmicHierarchySkyLight.y = 1: 診断（cosmic-sky=sgra-off）。淡い光を計算せず、星の地図だけにする。
    if (_CosmicHierarchySkyLight.y > 0.5) return CosmicHierarchyStarMap(directionWorld, pixelAngle);
    float3 light;
    if (_CosmicHierarchySkyAtlasInfo.y > 0.5)
    {
        light = CosmicHierarchyAtlasSurface(TEXTURE2D_ARGS(_CosmicHierarchySgrAAtlas, sampler_CosmicHierarchySgrAAtlas), directionWorld)
            * (pixelAngle * pixelAngle) * COSMIC_MW_TO_PIXEL * _CosmicHierarchySkyLight.x;
    }
    else
    {
        light = CosmicMilkyWayLight(float3(0, 0, 0), directionWorld, pixelAngle);
    }
    return light + CosmicHierarchyStarMap(directionWorld, pixelAngle);
}

#endif
