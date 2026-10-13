// 星雲・星団・銀河・電波銀河・大規模構造・観測可能な宇宙（CosmicVolume）。境界の球（単位半径 q、|q| ≤ 1）の中を光線で積分し、
// 放射と吸収を前から後ろへ合成します（乗算済みアルファ）。形は _CosmicBody.y の番号（keyword ではなく uniform の分岐）。
// 形はどれも手続き生成の「それらしい概略」で、特定の天体の写真をなぞっていません（HUD と設計書で明示）。
// 時間変化はゆっくりにします（星雲の乱流のゆらぎ、銀河の差動回転と剛体回転する腕、宇宙の網のきらめき）。
// _CosmicBody.y（形）: 0 環状の惑星状星雲、1 双極の惑星状星雲、2 超新星残骸の繊維、3 HII 領域（散光星雲）、4 球状星団、
//   5 円盤銀河（渦巻・棒・不規則）、6 楕円銀河、7 電波銀河（ジェットとローブ、偽色）、8 宇宙の網（超銀河団・グレートウォール）、9 観測可能な宇宙。
// _CosmicP0: rgb = 主の色、w = 明るさ。_CosmicP1: rgb = 副の色、w = 密度。
// _CosmicP2: x,y,z = 形ごとの数値（下の各関数）、w = 時間の倍率。_CosmicP3: x,y,z = 形ごとの数値、w = 歩数の倍率。
// 銀河（5 円盤の焼いた形、6 楕円）は、星の光の大部分を点の星へ配り直します（CosmicGalaxyStar）。雲のような拡散光ではなく、
// 1 つ 1 つの星がくっきり見え、ぼかすと元の体積の光（写真のような姿）に戻ります。
#ifndef COSMIC_VOLUME_SHADING_INCLUDED
#define COSMIC_VOLUME_SHADING_INCLUDED

#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicNebulaModels.hlsl"
#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicGuideGlobals.hlsl"

#define COSMIC_VOLUME_STEPS 32
// 歩数の品質の倍率（CosmicRenderQuality。録画では 2、実時間は 1。未設定の 0 も 1）。
float _CosmicQualitySteps;
float CosmicQualitySteps() { return max(_CosmicQualitySteps, 1.0); }

// 起動時に焼いた3次元密度（CosmicVolumeAtlas）。_CosmicBody.w = スラブの番号（星雲 1.., 銀河 101.., 0 は焼いた形を使わない）。
TEXTURE3D(_CosmicNebulaAtlas); SAMPLER(sampler_CosmicNebulaAtlas);
TEXTURE3D(_CosmicGalaxyAtlas); SAMPLER(sampler_CosmicGalaxyAtlas);
float _CosmicVolumeBaked;
float _CosmicGuidedMask;
// x = 星雲のスラブ数、y = 銀河のスラブ数、z = 星雲の1スラブの格子数、w = 銀河の1スラブの奥行きの格子数。
float4 _CosmicAtlasInfo;
// x = 銀河の面の格子数。
float4 _CosmicAtlasPlane;

struct CosmicMedium
{
    float3 emission;
    float absorption;
};

CosmicMedium CosmicEmpty(){CosmicMedium m; m.emission = 0; m.absorption = 0; return m;}

// 0: 環状の惑星状星雲（環状星雲・らせん星雲）。環の軸は物体の Y。内側は青緑（[O III]）、外側は赤（Hα）。
CosmicMedium CosmicRingNebula(float3 q, float t, float fp)
{
    CosmicMedium m = CosmicEmpty();
    float rho = length(q.xz);
    float ring = exp(-pow((rho - _CosmicP2.x) / _CosmicP2.y, 2.0)) * exp(-pow(q.y / (_CosmicP2.z + 0.2 * rho), 2.0));
    float n = CosmicFbmTex(q * 5.0 + float3(0, t * 0.01, _CosmicBody.z), 5, fp * 5.0, 2.1, 0.55);
    // 彗星状の小塊（らせん星雲）: 半径方向に伸びた筋。
    float knots = pow(saturate(CosmicNoiseTex(float3(atan2(q.z, q.x) * 18.0, rho * 4.0, q.y * 3.0 + _CosmicBody.z), fp) * 0.5 + 0.5), 6.0) * _CosmicP3.x;
    float density = ring * (0.55 + 0.7 * (0.5 + 0.5 * n)) + knots * smoothstep(0.3, 0.9, rho) * smoothstep(1.0, 0.8, rho);
    float inner = exp(-pow(rho / max(_CosmicP2.x * 0.8, 0.05), 2.0)) * exp(-pow(q.y / 0.4, 2.0));
    float3 color = lerp(_CosmicP1.rgb, _CosmicP0.rgb, smoothstep(_CosmicP2.x - 0.15, _CosmicP2.x + 0.2, rho));
    m.emission = color * density + _CosmicP1.rgb * inner * 0.35;
    // 中心の白色矮星。
    m.emission += float3(0.8, 0.9, 1.0) * exp(-dot(q, q) / 0.0004) * 4.0;
    return m;
}

// 1: 双極の惑星状星雲（キャッツアイ星雲）。傾いた入れ子の楕円体の殻と、同心の淡い環。
CosmicMedium CosmicBipolarNebula(float3 q, float t, float fp)
{
    CosmicMedium m = CosmicEmpty();
    float n = CosmicFbmTex(q * 6.0 + _CosmicBody.z + t * 0.005, 4, fp * 6.0, 2.0, 0.5);
    float3 a = q / float3(0.35, 0.75, 0.35);
    float3 b = CosmicRotateAxis(q, normalize(float3(0.3, 0, 1)), 0.9) / float3(0.3, 0.6, 0.3);
    float shellA = exp(-pow((length(a) - 1.0) / 0.12, 2.0));
    float shellB = exp(-pow((length(b) - 1.0) / 0.10, 2.0));
    float halo = exp(-pow(frac(length(q) * 5.0) - 0.5, 2.0) / 0.01) * smoothstep(1.0, 0.5, length(q)) * 0.15;
    m.emission = _CosmicP0.rgb * (shellA * (0.7 + 0.5 * n)) + _CosmicP1.rgb * (shellB * (0.8 + 0.4 * n)) + _CosmicP0.rgb * halo;
    m.emission += float3(0.85, 0.95, 1.0) * exp(-dot(q, q) / 0.0003) * 4.0;
    return m;
}

// 2: 超新星残骸（かに星雲）。楕円体の中の橙赤の繊維の網と、青白いシンクロトロンの拡散光。
CosmicMedium CosmicSupernovaRemnant(float3 q, float t, float fp)
{
    CosmicMedium m = CosmicEmpty();
    float3 e = q / float3(1.0, 0.68, 0.75);
    float r = length(e);
    if (r > 1.0) return m;
    float n = CosmicFbmTex(e * 3.5 + _CosmicBody.z + float3(t * 0.004, 0, 0), 5, fp * 3.5, 2.1, 0.55);
    float filaments = pow(saturate(1.0 - abs(n) * 3.0), 5.0) * smoothstep(1.0, 0.55, r);
    float synchrotron = exp(-r * r * 2.5);
    m.emission = _CosmicP0.rgb * filaments * 1.6 + _CosmicP1.rgb * synchrotron * 0.45;
    // 中心のパルサー（点滅）。
    m.emission += float3(0.8, 0.9, 1.0) * exp(-dot(q, q) / 0.0002) * (3.0 + 2.0 * sin(t * 12.0));
    return m;
}

// 10: 年老いた超新星残骸（網状星雲・ベール星雲）。星の中心は空っぽで、半径 _CosmicP2.x の薄い球殻の上にだけ、細い繊維が走る。
// 見る向きで縁が明るく見える（殻を斜めに通る視線ほど長く積分する）。内側のガスは描かない（らせん星雲の環の中の光とは別）。
// _CosmicP0.rgb: 殻の外側の色、_CosmicP1.rgb: 内側の色、_CosmicP2.x: 殻の半径（球の半径 1 に対して）、_CosmicP2.y: 殻の厚み、_CosmicP2.z: 繊維の細さ（大きいほど細い）。
CosmicMedium CosmicThinShell(float3 q, float t, float fp)
{
    CosmicMedium m = CosmicEmpty();
    float r = length(q);
    float radius = _CosmicP2.x;
    float thickness = max(_CosmicP2.y, 0.02);
    float shell = exp(-pow((r - radius) / thickness, 2.0));
    if (shell < 0.01) return m;
    // 殻は一様ではなく、塊（大きな模様）と、その上の細い繊維（符号が変わる境目の筋）でできている。
    float clump = CosmicFbmTex(q * 2.2 + _CosmicBody.z, 3, fp * 2.2, 2.0, 0.5);
    float n = CosmicFbmTex(q * 7.0 + float3(0, t * 0.004, _CosmicBody.z * 1.7), 5, fp * 7.0, 2.1, 0.55);
    float filaments = pow(saturate(1.0 - abs(n) * max(_CosmicP2.z, 0.5) * 2.5), 4.0);
    float density = shell * filaments * smoothstep(-0.35, 0.35, clump);
    float3 color = lerp(_CosmicP1.rgb, _CosmicP0.rgb, smoothstep(radius - thickness, radius + thickness, r));
    m.emission = color * density;
    return m;
}

// 11: コロナ質量放出（CME）。コロナグラフ（LASCO など）で知られる三部構造: 明るい前面の弧、暗い空洞、中に残る明るい核（プロミネンス由来）。
// 物体の X が進む向き。物体は太陽から前面までの距離（front）に対し、中心を 0.6 front、直径を 0.9 front に置く前提で、座標を front の単位へ直す。
// 前面は球殻のうち進む向きの側ほど明るく、太陽へ向かって細くなる 2 枚の壁（脚）でつながる。_CosmicP0.rgb: 前面の色、_CosmicP1.rgb: 核の色。
CosmicMedium CosmicCmeCloud(float3 q, float t, float fp)
{
    CosmicMedium m = CosmicEmpty();
    float3 p = float3(0.6 + 0.45 * q.x, 0.45 * q.y, 0.45 * q.z);
    float3 center = float3(0.62, 0.0, 0.0);
    float radius = 0.34;
    float n = CosmicFbmTex(p * 9.0 + _CosmicBody.z, 4, fp * 9.0, 2.1, 0.55);
    float tex = 0.55 + 0.9 * (0.5 + 0.5 * n);
    float dBubble = length(p - center);
    float shell = exp(-pow((dBubble - radius) / 0.05, 2.0));
    float forward = 0.3 + 0.7 * smoothstep(-0.3, 0.9, (p.x - center.x) / radius);
    float rho = length(p.yz);
    float legs = exp(-pow((rho - 0.53 * p.x) / 0.035, 2.0)) * smoothstep(0.0, 0.06, p.x) * (1.0 - smoothstep(0.22, 0.34, p.x)) * 0.6;
    float3 c = (p - float3(0.46, 0.0, 0.0)) / float3(0.13, 0.07, 0.07);
    float core = exp(-dot(c, c));
    m.emission = _CosmicP0.rgb * (shell * forward + legs) * tex + _CosmicP1.rgb * core * (0.7 + 0.6 * (0.5 + 0.5 * n)) * 1.3;
    return m;
}

// 12: 太陽風。太陽（球の中心）から放射状に伸びるストリーマ（赤道に多い光の筋）と、赤道面の渦を巻く腕（パーカー螺旋）。
// _CosmicP2.x: 球の半径が太陽半径の何倍か、_CosmicP2.z: 螺旋の巻き（球の半径あたりの rad）。_CosmicP0.rgb: 光の色。
CosmicMedium CosmicSolarWind(float3 q, float t, float fp)
{
    CosmicMedium m = CosmicEmpty();
    float r01 = length(q);
    float suns = max(r01 * _CosmicP2.x, 1.0);
    if (suns < 1.03 || r01 > 1.0) return m;
    float3 dir = q / r01;
    float equatorial = 1.0 - abs(dir.y);
    float ns = CosmicNoiseTex(dir * 5.0 + float3(t * 0.002, 0.0, _CosmicBody.z), fp) * 0.5 + 0.5;
    float rays = pow(saturate(ns * 1.3 - 0.25 + 0.4 * equatorial), 4.0);
    float radial = smoothstep(1.0, 1.6, suns) * pow(suns, -1.8);
    float phi = atan2(q.z, q.x);
    float arms = pow(0.5 + 0.5 * cos(3.0 * (phi - _CosmicP2.z * length(q.xz) + t * 0.0004)), 4.0);
    float sheet = exp(-pow(q.y / 0.1, 2.0)) * smoothstep(0.05, 0.2, r01) * (1.0 - smoothstep(0.7, 1.0, r01));
    m.emission = _CosmicP0.rgb * (rays * radial * 0.25 + arms * sheet * 0.02);
    return m;
}

// 3: HII 領域（オリオン大星雲・わし星雲・タランチュラ星雲）。赤い電離ガスの雲、青緑の芯、暗い塵の柱と筋、埋もれた若い星。
CosmicMedium CosmicHiiRegion(float3 q, float t, float fp)
{
    CosmicMedium m = CosmicEmpty();
    float r = length(q);
    float3 drift = float3(t * 0.006, t * 0.003, 0);
    if (r > 1.0) return m;
    // 不規則な外形: 低周波のノイズで半径をゆがめ、雲の切れ目を作ります（球の塊に見せない）。
    // 外形を先に求め、外側では高い周波数の雲を引きません（1歩あたりのテクスチャ参照を減らす）。
    float shape = CosmicFbmTex(q * 1.3 + _CosmicBody.z * 3.1, 2, fp * 1.3, 2.0, 0.5);
    float envelope = smoothstep(1.0, 0.45, r * (1.0 - 0.45 * shape));
    if (envelope <= 0.0) return m;
    float cloud = CosmicFbmTex(q * 2.2 + _CosmicBody.z + drift, 4, fp * 2.2, 2.05, 0.55);
    float detail = CosmicFbmTex(q * 7.0 - drift, 2, fp * 7.0, 2.1, 0.5);
    float density = saturate(smoothstep(-0.15, 0.45, cloud + 0.25 * shape) * 1.2 + detail * 0.25) * envelope;
    float core = exp(-pow(r / 0.35, 2.0));
    // 塵の柱（わし星雲の「創造の柱」を連想させる、ノイズの筋）。
    float pillars = _CosmicP2.x > 0.05 ? smoothstep(0.15, 0.45, CosmicFbmTex(float3(q.x * 4.0, q.y * 1.2, q.z * 4.0) + _CosmicBody.z * 2.0, 2, fp * 4.0, 2.0, 0.5)) * _CosmicP2.x : 0.0;
    float dust = saturate(smoothstep(0.1, 0.5, detail) * 0.6 + pillars) * envelope;
    m.emission = (_CosmicP0.rgb * density + _CosmicP1.rgb * core * density * 1.5) * (1.0 - 0.5 * dust);
    m.absorption = dust * _CosmicP2.y;
    return m;
}

// 6: 楕円銀河（IC 1101）。Sérsic 型（n = 4）の滑らかな黄色い光と、まわりの球状星団の点。
CosmicMedium CosmicElliptical(float3 q, float t, float fp)
{
    CosmicMedium m = CosmicEmpty();
    float3 e = q / float3(1.0, 0.72, 0.85);
    float r = max(length(e), 1e-3);
    float profile = exp(-7.67 * (pow(r / 0.25, 0.25) - 1.0));
    m.emission = lerp(_CosmicP0.rgb, _CosmicP1.rgb, saturate(r * 1.4)) * min(profile, 40.0) * 0.06;
    return m;
}

// 5: 円盤銀河。腕は剛体回転する密度波（対数らせん）、星と塵は差動回転（平坦な回転曲線: Ω ∝ 1/r）を2位相リセットで回します。
// _CosmicP2: x = 腕の本数、y = 腕のピッチ角（ラジアン）、z = 棒の強さ。_CosmicP3: x = 不規則さ（0 渦巻 〜 1 不規則）、y = 円盤の厚さ、z = バルジの強さ。
CosmicMedium CosmicDiskGalaxy(float3 q, float t, float fp)
{
    CosmicMedium m = CosmicEmpty();
    float r = length(q.xz);
    float thickness = _CosmicP3.y;
    float vertical = exp(-abs(q.y) / thickness);
    float disk = exp(-r / 0.42) * vertical * smoothstep(1.0, 0.85, r);
    if (disk < 1e-4 && r > 0.2) return m;
    float pattern = t * 0.0025;
    float phi = atan2(q.z, q.x);
    float arms = _CosmicP2.x;
    float spiral = log(max(r, 0.02)) / tan(max(_CosmicP2.y, 0.05));
    float armWave = 0.5 + 0.5 * cos(arms * (phi - spiral - pattern));
    // 棒（中心の細長い明るい部分）。
    float2 barAxis = float2(cos(pattern * 1.3), sin(pattern * 1.3));
    float along = dot(q.xz, barAxis), across = dot(q.xz, float2(-barAxis.y, barAxis.x));
    float bar = exp(-pow(along / 0.32, 2.0) - pow(across / 0.07, 2.0)) * _CosmicP2.z;
    float stars = 0;
    [unroll] for (int j = 0; j < 2; j++)
    {
        float tau, weight, epoch;
        CosmicFlowPhase(t, 60.0, j, tau, weight, epoch);
        float omega = 0.05 / max(r, 0.05);
        float3 p = CosmicRotateY(q, -omega * tau * 60.0);
        float3 domain = p * float3(6.0, 12.0, 6.0) + (CosmicHash3(int3((int)epoch, j, (int)_CosmicBody.z)) - 0.5) * 4.0;
        stars += (0.5 + 0.5 * CosmicFbmTex(domain, 3, fp * 12.0, 2.1, 0.55)) * weight;
    }
    // 塵のむらは差動回転させず、剛体回転する腕と一緒に回します（2位相分のテクスチャ参照を省く）。
    float dustNoise = 0.5 + 0.5 * CosmicFbmTex(CosmicRotateY(q, -pattern) * float3(10.0, 20.0, 10.0) + 9.0 + _CosmicBody.z, 2, fp * 20.0, 2.0, 0.5);
    float irregular = _CosmicP3.x;
    float armMask = lerp(pow(armWave, 2.0) * (0.6 + 0.6 * stars), stars, irregular);
    float young = armMask * stars;
    float3 old = _CosmicP1.rgb * disk * (0.45 + 0.4 * stars);
    float3 blue = _CosmicP0.rgb * disk * young * 2.4;
    // 星形成領域（腕の上のピンクの点）。
    float knots = pow(saturate(stars * 1.4 - 0.55), 4.0) * armMask * disk * 6.0;
    float bulge = exp(-pow(length(q / float3(1.0, 0.7, 1.0)) / 0.08, 1.2)) * _CosmicP3.z;
    m.emission = old + blue + float3(1.0, 0.45, 0.6) * knots + _CosmicP1.rgb * (bulge * 3.0 + bar * disk * 2.0);
    // 塵の帯（腕の内側の縁）。
    float lane = pow(saturate(0.5 + 0.5 * cos(arms * (phi - spiral - pattern) + 1.2)), 6.0) * (1.0 - irregular * 0.6);
    m.absorption = disk * exp(-abs(q.y) / (thickness * 0.5)) * (lane * 0.8 + dustNoise * 0.4) * 9.0;
    return m;
}

// 7: 電波銀河（アルキオネウス）。中心の小さな母銀河、細いジェット、両端の大きなローブ。電波の強さを偽色で表します。
CosmicMedium CosmicRadioGalaxy(float3 q, float t, float fp)
{
    CosmicMedium m = CosmicEmpty();
    float host = exp(-dot(q, q) / 0.0006);
    float rho = length(q.xz);
    float jet = exp(-pow(rho / (0.008 + 0.03 * abs(q.y)), 2.0)) * smoothstep(0.85, 0.2, abs(q.y));
    float3 lobeCenter = float3(0, sign(q.y) * 0.72, 0);
    float3 l = (q - lobeCenter) / float3(0.26, 0.24, 0.26);
    float lobeNoise = CosmicFbmTex(q * 4.0 + _CosmicBody.z + float3(0, t * 0.002, 0), 5, fp * 4.0, 2.1, 0.55);
    float lobe = exp(-pow(length(l) * (1.0 + 0.35 * lobeNoise), 2.0) * 1.4);
    m.emission = float3(1.0, 0.92, 0.75) * host * 3.0 + _CosmicP1.rgb * jet * 0.8 + lerp(_CosmicP0.rgb, _CosmicP1.rgb, saturate(0.5 + lobeNoise)) * lobe * 0.35;
    return m;
}

// 8: 宇宙の網（超銀河団・グレートウォール）。Worley の稜線で銀河のフィラメントと結節点を作り、全体の形で包みます。
// _CosmicP2: x = 網の細かさ、y = 形（0 球、1 壁: 薄い板）、z = 中心へ集まる流れの強さ（ラニアケア）。
CosmicMedium CosmicCosmicWeb(float3 q, float t, float fp)
{
    CosmicMedium m = CosmicEmpty();
    float3 shapeQ = _CosmicP2.y > 0.5 ? q / float3(1.0, 0.22, 0.5) : q;
    // 外形は低周波のノイズでゆがめ、完全な球や板に見せません。
    float edge = CosmicFbmTex(q * 1.4 + _CosmicBody.z * 0.37, 3, fp * 1.4, 2.0, 0.5);
    float envelope = smoothstep(1.0, 0.6, length(shapeQ) * (1.0 - 0.35 * edge));
    if (envelope <= 0) return m;
    float frequency = _CosmicP2.x;
    // 中心へ集まる流れ: 中心に近いほど網を引き寄せて濃くします（銀河の流れの「盆地」）。
    float3 p = q * frequency * (1.0 + _CosmicP2.z * 0.35 * (1.0 - length(q)));
    // 大きな空洞の壁（銀河のフィラメント）と、その中の細かい網の2段。交わるところが銀河団（明るい結節点）になります。
    float2 w = CosmicWorleyTex(p + _CosmicBody.z, fp * frequency);
    float wall = exp(-pow((w.y - w.x) / 0.08, 2.0));
    float2 w2 = CosmicWorleyTex(p * 2.6 + 11.0 + _CosmicBody.z, fp * frequency * 2.6);
    float fine = exp(-pow((w2.y - w2.x) / 0.07, 2.0)) * CosmicOctaveWeight(frequency * 2.6, fp);
    float cluster = pow(wall * fine, 2.0);
    float sparkle = CosmicOctaveWeight(frequency * 8.0, fp) * pow(saturate(CosmicNoiseTex(q * frequency * 8.0 + _CosmicBody.z, fp) * 0.5 + 0.5), 6.0) * 2.0;
    float filament = wall * (0.35 + 0.65 * fine);
    m.emission = (_CosmicP0.rgb * filament * 0.45 + _CosmicP1.rgb * cluster * 2.5 + _CosmicP1.rgb * sparkle * filament) * envelope;
    return m;
}

// 9: 観測可能な宇宙。内側は宇宙の網、外縁は宇宙マイクロ波背景放射（CMB）を連想させる殻（手続き生成の揺らぎ、観測図ではありません）。
CosmicMedium CosmicUniverse(float3 q, float t, float fp)
{
    CosmicMedium m = CosmicCosmicWeb(q, t, fp);
    float r = length(q);
    float shell = exp(-pow((r - 0.97) / 0.025, 2.0));
    float fluctuation = shell > 0.01 ? CosmicFbmTex(q * 9.0 + 3.0, 5, fp * 9.0, 2.0, 0.6) : 0.0;
    float3 cmb = lerp(float3(0.15, 0.25, 0.9), float3(1.0, 0.55, 0.15), saturate(0.5 + fluctuation * 1.4));
    m.emission += cmb * shell * 0.9;
    return m;
}

int CosmicSlabCode() { return (int)(_CosmicBody.w + 0.5); }

// 歩みの始点のずらし（0..1）。光線の上の点 x（物体空間に固定した座標）を、その点での画素の大きさ footprint の升目に切り、升目のハッシュで決めます。
// 以前は画素座標のハッシュで、視点が動くと構造は動くのに粒の模様だけが画面に貼り付いて取り残された（2026-10-06 のユーザー指摘）。
// 連続な関数で物体空間のずらしを作ると隣の画素とほぼ同じ値になり、標本の殻と薄い壁の交わりが同心円の縞になった（以前の記録）。
// 升目は画素の大きさなので隣の画素は別の升目になり、縞になりません。升目の大きさは 2 の冪へ丸め、隣の 2 つの冪のどちらを使うかを
// 端数の確率で升目ごとに選びます（値を混ぜると一様でなくなるため。距離が変わっても粒が一斉に跳ばない）。
float CosmicAnchoredJitter(float3 x, float footprint)
{
    float level = log2(max(footprint, 1e-6));
    float lower = floor(level);
    float above = level - lower;
    int3 cellA = (int3)floor(x / exp2(lower));
    int3 cellB = (int3)floor(x / exp2(lower + 1.0));
    float3 a = CosmicHash3(cellA + int3(977, 31 * (int)lower, 0));
    float3 b = CosmicHash3(cellB + int3(977, 31 * (int)lower + 31, 0));
    return a.y < above ? b.x : a.x;
}

// 距離 d（細胞の単位）の細い稜線。幅は画素の大きさ（cells）より細くせず、太らせた分だけ暗くします（光の総量を保つ）。
float CosmicRidge(float d, float width, float cells)
{
    float effective = max(width, cells);
    return exp(-pow(d / effective, 2.0)) * (width / effective);
}

// 星雲の atlas（値は平方根で入っているので2乗して返す）。ミップは画素の大きさで選び、スラブの境界の半格子より内側だけを読みます。
float4 CosmicNebulaTexel(float3 q, float footprint)
{
    float texels = _CosmicAtlasInfo.z;
    float lod = clamp(log2(max(footprint * texels * 0.5, 1e-6)), 0.0, 5.0);
    float halfTexel = 0.5 * exp2(lod) / texels;
    float3 uvw = saturate(q * 0.5 + 0.5);
    uvw.z = ((float)(CosmicSlabCode() - 1) + clamp(uvw.z, halfTexel, 1.0 - halfTexel)) / _CosmicAtlasInfo.x;
    float4 c = SAMPLE_TEXTURE3D_LOD(_CosmicNebulaAtlas, sampler_CosmicNebulaAtlas, uvw, lod);
    return c * c;
}

// 銀河の atlas。テクスチャの x, y = 円盤の面（q.x, q.z）、z = 鉛直（±halfHeight を ±1 へ）。
float4 CosmicGalaxyTexel(float3 q, float halfHeight, float footprint)
{
    float lod = clamp(log2(max(footprint * _CosmicAtlasPlane.x * 0.5, 1e-6)), 0.0, 4.0);
    float halfTexel = 0.5 * exp2(lod) / _CosmicAtlasInfo.w;
    float v = clamp(q.y / halfHeight, -1.0, 1.0) * 0.5 + 0.5;
    float3 uvw = float3(saturate(q.x * 0.5 + 0.5), saturate(q.z * 0.5 + 0.5),
        ((float)(CosmicSlabCode() - 101) + clamp(v, halfTexel, 1.0 - halfTexel)) / _CosmicAtlasInfo.y);
    float4 c = SAMPLE_TEXTURE3D_LOD(_CosmicGalaxyAtlas, sampler_CosmicGalaxyAtlas, uvw, lod);
    return c * c;
}

// 焼いた星雲（と電波銀河のローブ）。テクスチャの解像度より細かい模様は、ゆっくり流れる3次元ノイズで足します。
CosmicMedium CosmicBakedNebula(float3 q, float t, float fp, uint shape)
{
    CosmicMedium m = CosmicEmpty();
    if (dot(q, q) > 1.0) return m;
    float4 c = CosmicNebulaTexel(q, fp);
    uint slab = (uint)(CosmicSlabCode() - 1);
    bool guided = ((((uint)(_CosmicGuidedMask + 0.5)) >> slab) & 1u) != 0u;
    float3 drift = float3(t * 0.004, t * 0.002, 0);
    float detail = 0.75 + 0.5 * saturate(CosmicNoiseTex(q * 9.0 + drift + _CosmicBody.z, fp * 9.0) * 0.5 + 0.5);
    if (guided)
    {
        // 画像のガイドから作ったスラブ: RGB = 放射（元の画像の色）、A = 塵の吸収（CosmicVolumeBake.compute の BakeGuided）。
        m.emission = c.rgb * (detail / 0.35);
        m.absorption = c.a * 20.0 / max(_CosmicP1.w, 1e-3);
        return m;
    }
    if (shape == 8 || shape == 9)
    {
        // 宇宙の網。空洞を Voronoi の細胞とみなし、2つの細胞の境（F2 − F1）を淡い壁、3つが接する辺（F3 − F1）をフィラメント、
        // 4つが接する点（F4 − F1）を結節点（銀河団）として、ここで正確に計算します（どこまで近づいても細い筋がくっきり）。
        // 焼いたのは網を曲げるずれ（RGB）と、粗いフィラメントまでの目安の距離と全体の形（A）だけです（CosmicWebDensity）。
        // ・細かい段（2.4 倍の細胞）のフィラメントを、粗いフィラメントの近くにだけ重ねます（ある程度フラクタルな入れ子）。
        // ・粗いフィラメントから遠い空洞の標本（A が大きい）は、網の計算を省きます（細かい段も粗い網の近くにしか出さないため）。
        //   宇宙全体が画面を覆う場面で GPU 40 ms かかったため、省く範囲・歩数・見える奥行きを絞っています。
        // ・稜線の幅は画素の大きさより細くせず、太らせた分だけ暗くして光の総量を保ちます（遠くでちらつかない）。
        // ・密度で色を変え（空洞の暗い色 → 主の色 → 副の色 → 白い結節点）、吸収も持たせて手前の網が奥の網を隠すようにします。
        // ・焼いた解像度より細かい階層は、網の上にだけ現れる小さな塊（銀河の集まり）として画素の大きさに応じて足します。
        float hint = c.a * 0.5;
        if (hint < 0.3)
        {
            float frequency = _CosmicP2.x;
            float cells = max(fp * frequency, 1e-5);
            float3 seedOffset = _CosmicBody.z * float3(0.137, 0.231, 0.419);
            float3 p = q * frequency * (1.0 + _CosmicP2.z * 0.35 * (1.0 - length(q))) + (c.rgb - 0.5) * 1.2;
            float4 coarse = CosmicWorley4(p + seedOffset);
            float spread = coarse.z - coarse.x;
            float wall = CosmicRidge(coarse.y - coarse.x, 0.04, cells);
            float filament = CosmicRidge(spread, 0.05, cells);
            float node = CosmicRidge(coarse.w - coarse.x, 0.1, cells);
            float support = pow(saturate(1.0 - spread / 0.25), 2.0);
            float fine = 0;
            if (support > 0.01)
            {
                float4 small = CosmicWorley4(p * 2.4 + seedOffset * 2.0);
                fine = CosmicRidge(small.z - small.x, 0.06, cells * 2.4) * support;
            }
            float blob = CosmicNoiseTex(q * frequency * 10.0 + _CosmicBody.z, fp * frequency * 10.0);
            float sparkle = CosmicOctaveWeight(frequency * 10.0, fp) * smoothstep(0.3, 0.75, blob);
            float web = (wall * 0.1 + filament * 0.8 + fine * 0.5) * (0.75 + 0.6 * sparkle);
            float3 color = lerp(_CosmicP0.rgb * 0.3, _CosmicP0.rgb, smoothstep(0.0, 0.25, web));
            color = lerp(color, _CosmicP1.rgb, smoothstep(0.35, 0.8, web));
            color = lerp(color, float3(1.0, 0.95, 0.82), saturate(node * filament * 2.0));
            // 細い網は体積の中のごく一部なので、横切る光の道のりが短い分だけ明るくします。
            m.emission = color * (web * 20.0 + node * filament * 40.0);
            m.absorption = web * 3.0;
        }
        if (shape == 9)
        {
            // 観測可能な宇宙: 一様な薄い減光で、手前の数層の網だけが見えるようにします（奥の網まで足すと、何十層もの網が重なって
            // 絡まった霧になったため）。空洞にはごく淡い主の色を置きます。
            float r = length(q);
            float inside = smoothstep(1.0, 0.94, r);
            m.absorption += 1.8 * inside;
            m.emission += _CosmicP0.rgb * 0.08 * inside;
            // 外縁の宇宙マイクロ波背景放射（CMB）を連想させる殻（手続き生成の揺らぎ、観測図ではありません）。正面では薄く、
            // 縁（殻を斜めに長く通る所）だけが輪として見える強さにします（正面の殻が中の網を覆っていたため）。
            float shell = exp(-pow((r - 0.97) / 0.025, 2.0));
            if (shell > 0.01)
            {
                float fluctuation = saturate(0.5 + CosmicFbmTex(q * 9.0 + 3.0, 5, fp * 9.0, 2.0, 0.6) * 1.4);
                m.emission += lerp(float3(0.15, 0.25, 0.9), float3(1.0, 0.55, 0.15), fluctuation) * shell * 0.25;
            }
        }
        return m;
    }
    if (shape == 7)
    {
        float host = exp(-dot(q, q) / 0.0006);
        m.emission = float3(1.0, 0.92, 0.75) * host * 3.0 + lerp(_CosmicP0.rgb, _CosmicP1.rgb, saturate(c.r * 1.4)) * c.r * 0.5 * detail + _CosmicP1.rgb * c.g * 1.2;
        return m;
    }
    m.emission = (_CosmicP0.rgb * c.r + _CosmicP1.rgb * c.g) * (1.4 * detail) + float3(0.85, 0.92, 1.0) * c.a * 2.0;
    m.absorption = c.b * 6.0 * detail;
    return m;
}

// 銀河の光を、星の光（古い星の黄色い円盤とバルジ、腕の若い青い星）とガスの光（星形成領域のピンク）、塵の吸収に分けて返します。
// 5: 焼いた円盤銀河（腕の模様＝密度波は剛体回転、バルジは解析的）。6: 楕円銀河（解析的、ガスと塵なし）。
void CosmicGalaxyLight(uint shape, float3 q, float t, float fp, out float3 stellar, out float3 gas, out float absorption)
{
    if (shape == 6)
    {
        stellar = CosmicElliptical(q, t, fp).emission; gas = 0; absorption = 0;
        return;
    }
    float halfHeight = max(_CosmicP3.y * 8.0, 0.14) * 0.5;
    float3 rq = CosmicRotateY(q, -t * 0.0025);
    // 焼いた板の外（星のセルは板の外へはみ出ることがある）は、端の格子の値を引き伸ばさずに 0 にします。
    float4 c = abs(q.y) < halfHeight ? CosmicGalaxyTexel(rq, halfHeight, fp) : 0;
    float bulge = exp(-pow(length(q / float3(1.0, 0.7, 1.0)) / 0.08, 1.2)) * _CosmicP3.z;
    stellar = _CosmicP1.rgb * (c.a * 1.2 + bulge * 3.0) + _CosmicP0.rgb * c.g * 2.2;
    gas = float3(1.0, 0.45, 0.6) * c.r * 4.0;
    absorption = c.b * 18.0;
}

// 銀河の体積の放射。星へ配った割合（starFraction）を除いた星の光に、解像しない星の集まりの細かいむらを付け、ガスの光を足します。
CosmicMedium CosmicGalaxyMedium(uint shape, float3 q, float t, float fp, float starFraction)
{
    float3 stellar, gas; float absorption;
    CosmicGalaxyLight(shape, q, t, fp, stellar, gas, absorption);
    float sparkle = shape == 5 ? 0.6 + 0.8 * saturate(CosmicNoiseTex(CosmicRotateY(q, -t * 0.0025) * float3(40.0, 60.0, 40.0) + _CosmicBody.z, fp * 40.0) * 0.5 + 0.5) : 1.0;
    CosmicMedium m;
    m.emission = stellar * (1.0 - starFraction) * sparkle + gas;
    m.absorption = absorption;
    return m;
}

// ── 銀河の星 ──
// 物体空間（q）の立方体の格子の各セルに 1 つずつ星の候補を置き（位置・明るさ・色はセルの番号のハッシュ）、その場所の星の光が強いほど
// 高い確率で星にします。星の光束 = 星の光 × セルの体積 ÷ 確率 × 明るさのばらつき（平均 1）なので、星の明るさの和の期待値は
// 体積の星の光の積分と同じです（ぼかすと元の姿）。星は画素の大きさのガウス分布（くっきりした点）で描きます。
// セルの大きさは銀河の中心までの距離の画素の COSMIC_STAR_CELL_PIXELS 倍（画面上の星の密度を一定に）。2 の累乗の段で区切り、
// 隣り合う 2 段を距離で重みを付けて重ね、段が変わるときに星が一斉に入れ替わって見えないようにします。
// 光線が通るセルを順にたどり（3 次元 DDA）、体積の積分の歩みと交互に進めて、手前の塵の吸収を星にも掛けます。
// 銀河が画面で小さい（直径 約 100 画素未満）ときは星へ配らず、写真のような滑らかな光だけにします（遠くの銀河の星は解像しない）。
#define COSMIC_STAR_CELL_PIXELS 6.0
#define COSMIC_STAR_FRACTION 0.8
#define COSMIC_STAR_CELLS 96

struct CosmicStarWalk
{
    int3 cell;
    int3 stepDir;
    float3 tMax;
    float3 tDelta;
    float size;
    int left;
};

// q = qo + qd·s の光線で、s の位置のセルから歩き始めます（s は殻の物体空間の距離、qd = 2·向き）。
CosmicStarWalk CosmicStarWalkBegin(float3 qo, float3 qd, float s, float size, int budget)
{
    CosmicStarWalk w;
    float3 q = qo + qd * s;
    w.size = size;
    w.cell = (int3)floor(q / size);
    w.stepDir = int3(qd.x >= 0 ? 1 : -1, qd.y >= 0 ? 1 : -1, qd.z >= 0 ? 1 : -1);
    float3 inverse = 1.0 / max(abs(qd), 1e-7);
    float3 boundary = (float3(w.cell) + float3(w.stepDir > 0)) * size;
    w.tMax = s + abs(boundary - q) * inverse;
    w.tDelta = size * inverse;
    w.left = budget;
    return w;
}

// 1 つのセルの星が、この画素の光線に足す明るさ（物体空間 q の単位、体積の積分と同じ単位）。axis = 光線の向き（単位ベクトル）。
float3 CosmicGalaxyStar(uint shape, int3 cell, float size, int level, float3 qo, float3 axis, float pixelAngle, float t)
{
    int3 key = int3(cell.x, cell.y, cell.z + (level + 64) * 4096 + (int)_CosmicBody.z * 1048576);
    float3 h = CosmicHash3(key);
    // 位置はセルのほぼ全体に散らします（中央寄りにすると、斜めから見たとき格子の列が並んで見えたため）。
    // セルの端の星は隣のセルを通る光線で少し欠けますが、星の大きさ（約 1 画素）より小さい差です。
    float3 star = (float3(cell) + 0.03 + 0.94 * h) * size;
    float along = dot(star - qo, axis);
    if (along <= 0.0 || dot(star, star) > 1.0) return 0;
    float3 offset = star - qo - axis * along;
    float sigma = max(0.6 * pixelAngle * along, 1e-7);
    float d2 = dot(offset, offset) / (sigma * sigma);
    // 光線から離れた星（ほとんどのセル）は、密度を読む前に打ち切ります。
    if (d2 > 9.0) return 0;
    float3 stellar, gas; float absorption;
    CosmicGalaxyLight(shape, star, t, size, stellar, gas, absorption);
    float luminance = CosmicLuminance(stellar);
    if (luminance <= 1e-5) return 0;
    float presence = saturate(sqrt(luminance) * 0.9);
    float3 h2 = CosmicHash3(key + int3(17, 59, 101));
    if (h2.x >= presence) return 0;
    // 明るさのばらつき（平均 1。ほとんどは暗く、まれに明るい星。(−ln u)^2.2 の平均は Γ(3.2) ≈ 2.42）と、色温度のばらつき（輝度は保つ）。
    float brightness = 0.1 + 0.9 * pow(-log(max(h2.y, 1e-4)), 2.2) / 2.42;
    float3 tint = lerp(float3(1.0, 0.78, 0.6), float3(0.72, 0.84, 1.0), h2.z);
    tint /= CosmicLuminance(tint);
    float kernel = exp(-d2) / (3.14159265 * sigma * sigma);
    return stellar * tint * (brightness * size * size * size / presence * kernel);
}

// 光線の s が sEnd に届くまでに入るセルを順に処理します（現在のセル → 次のセル …）。
void CosmicStarWalkTo(inout CosmicStarWalk w, float sEnd, int level, uint shape, float3 qo, float3 axis, float pixelAngle, float t, float weight, inout float3 color)
{
    [loop] while (w.left > 0)
    {
        color += CosmicGalaxyStar(shape, w.cell, w.size, level, qo, axis, pixelAngle, t) * weight;
        w.left--;
        float exitT = min(w.tMax.x, min(w.tMax.y, w.tMax.z));
        if (w.tMax.x <= w.tMax.y && w.tMax.x <= w.tMax.z) { w.cell.x += w.stepDir.x; w.tMax.x += w.tDelta.x; }
        else if (w.tMax.y <= w.tMax.z) { w.cell.y += w.stepDir.y; w.tMax.y += w.tDelta.y; }
        else { w.cell.z += w.stepDir.z; w.tMax.z += w.tDelta.z; }
        if (exitT >= sEnd) break;
    }
}

CosmicMedium CosmicMediumAt(uint shape, float3 q, float t, float fp)
{
    if (_CosmicVolumeBaked > 0.5)
    {
        int code = CosmicSlabCode();
        if (code >= 101 && shape == 5) return CosmicGalaxyMedium(5, q, t, fp, 0.0);
        if (code >= 1 && code < 101) return CosmicBakedNebula(q, t, fp, shape);
    }
    if (shape == 0) return CosmicRingNebula(q, t, fp);
    if (shape == 1) return CosmicBipolarNebula(q, t, fp);
    if (shape == 2) return CosmicSupernovaRemnant(q, t, fp);
    if (shape == 3) return CosmicHiiRegion(q, t, fp);
    if (shape == 5) return CosmicDiskGalaxy(q, t, fp);
    if (shape == 6) return CosmicElliptical(q, t, fp);
    if (shape == 7) return CosmicRadioGalaxy(q, t, fp);
    if (shape == 8) return CosmicCosmicWeb(q, t, fp);
    if (shape == 10) return CosmicThinShell(q, t, fp);
    if (shape == 11) return CosmicCmeCloud(q, t, fp);
    if (shape == 12) return CosmicSolarWind(q, t, fp);
    return CosmicUniverse(q, t, fp);
}

// 4: 球状星団（オメガ星団）。Plummer 分布の面密度で、視線に垂直な面の上に星を散らします（体積の積分は不要）。
float4 CosmicGlobularCluster(CosmicRay ray)
{
    float tc = -dot(ray.origin, ray.direction);
    float3 closest = ray.origin + ray.direction * tc;
    float b = length(closest) * 2.0;
    if (b > 1.0) return 0;
    float a = _CosmicP2.x;
    float surface = pow(1.0 + b * b / (a * a), -2.0);
    // 視線に垂直な面の2軸。
    float3 axisU = normalize(cross(ray.direction, abs(ray.direction.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
    float3 axisV = cross(ray.direction, axisU);
    float2 plane = float2(dot(closest, axisU), dot(closest, axisV)) * 2.0;
    float pixel = ray.pixelAngle * length(ray.origin) * 2.0;
    float3 stars = 0;
    [unroll] for (int layer = 0; layer < 3; layer++)
    {
        float density = 60.0 * (layer + 1) * (layer + 1);
        float2 cellPosition = plane * density;
        int2 cell = (int2)floor(cellPosition);
        float3 h = CosmicHash3(int3(cell, layer + (int)_CosmicBody.z * 7));
        if (h.x < surface * (layer == 0 ? 0.9 : 0.75))
        {
            float2 starPosition = (float2(cell) + 0.5 + (h.yz - 0.5) * 0.5) / density;
            float distance = length(plane - starPosition);
            float size = max(pixel * 0.8, 0.0006);
            float3 color = CosmicBlackbody(lerp(3800.0, 7500.0, CosmicHash1(int3(cell, 91))));
            stars += color * exp(-distance * distance / (size * size)) * (0.6 + 2.0 * pow(h.y, 4.0)) / (layer + 1);
        }
    }
    float3 glow = _CosmicP0.rgb * surface * 0.25;
    float3 color = (stars + glow) * _CosmicP0.w;
    return float4(color, 0);
}

// ── 画像のガイドの星雲を、カメラの方へ回した1枚の面で描く（創作、CosmicVolumeAtlas の guide 配列がある環境だけ） ──
// ガイドの画像を奥行きへ押し出して焼いた体積は、焼いた位置（主役のカメラ）から見れば画像どおりですが、近くの斜めの視線では
// 押し出しが流れた筋に見え、128³ の格子も粗く見えました（各段の登場の瞬間、画面の端に大きく写るとき）。そこで、焼いた体積の
// 代わりに、画像の正面をカメラの方へ回した面で、ガイドの 2D 画像を直接読みます。正面からは画像どおりで、角度が変わっても
// 筋にならず、近くでも guide の解像度（256²）まで細かく見えます。物理的な形ではなく見た目のための作りです。
// 奥行きの違う数枚の層に分けると、近いほど層ごとの拡大率が違い、同じ模様が中心から放射状にずれて重なって見えたため1枚にしました。
TEXTURE2D_ARRAY(_CosmicGuideArray); SAMPLER(sampler_CosmicGuideArray);
// _CosmicGuideBasis・_CosmicGuideModel・_CosmicGuideCards は CosmicGuideGlobals.hlsl（星雲の星と共有）。
// 形の模型の視線ごとの合計（CosmicVolumeBake.compute の BakeGuideColumn）。R = 曲面の「重み × 見る向きの倍率」の合計、G = 塵の層を載せる交点の番号、
// B = 下地の体積の密度の積分。
TEXTURE2D_ARRAY(_CosmicGuideColumn); SAMPLER(sampler_CosmicGuideColumn);
// x = guide の一辺の画素数、y = ミップの数。
float4 _CosmicGuideInfo;

// 画像の正面がカメラの方へ回る割合（1 = 常に正面を向ける）。少し残すと、回り込むときに向きの変化が感じられます。
#define COSMIC_GUIDE_FOLLOW 0.8

float4 CosmicGuideSample(uint slab, float2 p, float lod)
{
    float4 g = SAMPLE_TEXTURE2D_ARRAY_LOD(_CosmicGuideArray, sampler_CosmicGuideArray, p * 0.5 + 0.5, slab, lod);
    return g * g;
}

float4 CosmicGuidedCards(CosmicRay ray, uint slab)
{
    float3 ix = _CosmicGuideBasis[slab * 3].xyz;
    float3 iy = _CosmicGuideBasis[slab * 3 + 1].xyz;
    float3 front = -_CosmicGuideBasis[slab * 3 + 2].xyz;
    float3 eye = ray.origin * 2.0;
    float3 direction = ray.direction;
    float eyeDistance = length(eye);
    float3 toward = eyeDistance > 1e-4 ? eye / eyeDistance : front;
    // 面の向き: 画像の正面をカメラの方へ COSMIC_GUIDE_FOLLOW だけ回します。中心の近く（星雲の中）では画像の正面のまま
    // （向きが急に回らないように）。真後ろの近くでは常に正面を向けます。
    float c = dot(front, toward);
    float follow = c < -0.85 ? 1.0 : COSMIC_GUIDE_FOLLOW;
    follow *= smoothstep(0.35, 1.1, eyeDistance);
    float angle = acos(clamp(c, -1.0, 1.0)) * follow;
    float3 axis = cross(front, toward);
    float axisLength = length(axis);
    float3 n = front, rx = ix, ry = iy;
    if (axisLength > 1e-5 && angle > 1e-5)
    {
        axis /= axisLength;
        n = CosmicRotateAxis(front, axis, angle);
        rx = CosmicRotateAxis(ix, axis, angle);
        ry = CosmicRotateAxis(iy, axis, angle);
    }
    else if (c < 0.0 && follow > 0.5)
    {
        n = -front; rx = -ix; ry = iy;
    }
    float denominator = dot(direction, n);
    if (abs(denominator) < 1e-4) return 0;
    // 面（中心を通り n に垂直）との交点。カメラの後ろなら描きません。
    float t = -dot(eye, n) / denominator;
    if (t <= 0.0) return 0;
    float3 p3 = eye + direction * t;
    float r3 = dot(p3, p3);
    if (r3 >= 1.0) return 0;
    float2 p = float2(dot(p3, rx), dot(p3, ry));
    float seed = _CosmicBody.z;
    // 画素の大きさ（単位半径）から guide のミップを選びます。斜めに見るほど広がります。
    float footprint = ray.pixelAngle * t / max(abs(denominator), 0.2);
    float lod = clamp(log2(max(footprint * _CosmicGuideInfo.x * 0.5, 1e-6)), 0.0, _CosmicGuideInfo.y - 1.0);
    float4 g = CosmicGuideSample(slab, p, lod);
    // 外形: 焼いた形と同じく、ゆがめた窓で柔らかく消します（四角い画像の縁や丸い円盤に見えないように）。
    float2 bent = p * (1.0 + 0.4 * CosmicFbmTex(float3(p * 1.4, 3.7 + seed), 4, footprint * 1.4, 2.0, 0.55));
    float window = smoothstep(1.0, 0.4, length(bent)) * smoothstep(1.0, 0.85, sqrt(r3));
    // 拡大されて guide の画素が見えるほど近いときだけ、細かい模様を足します（平均は 1 のまま）。
    float magnified = saturate(-log2(max(footprint * _CosmicGuideInfo.x, 1e-6)) * 0.5);
    float detail = 1.0 + magnified * 0.45 * CosmicFbmTex(float3(p * 24.0, 5.3 + seed), 3, footprint * 24.0, 2.1, 0.55);
    // 塵: 後ろの景色を暗くし（焼いた形の透過率 1 − 0.92·塵 と同じ）、放射の半分（塵の層より奥の分）を弱めます。
    float dust = 0.92 * saturate(g.a) * window;
    float3 color = g.rgb * (window * max(detail, 0.0) * _CosmicP1.w * (1.0 - 0.5 * dust)) * _CosmicP0.w;
    return float4(color, dust);
}

// ── 画像のガイドの星雲を、形の模型の下地の体積と細部の曲面で描く（Docs/Plans/CosmicNebula3D.md） ──
// 画像の光を 2 つに分けます（2026-10-05 のユーザーの判断「粗い光は体積、細部だけ曲面」）。
// ・下地 = min(画像, COSMIC_NEBULA_BASE_FRACTION × ぼかした画像)。縁へなだらかに薄れる体積（CosmicNebulaVolumeDensity）に置き、
//   主役の視線ごとの密度の積分（_CosmicGuideColumn の B）で割ります。輪郭が出ず、光が滑らかなので斜めから見ても筋が目立ちません。
// ・細部 = 画像 − 下地（0 以上）。模型の厚さの無い曲面の交点に置き、主役の視線の上の「重み × 見る向きの倍率」の合計（R）で割ります
//   （見る向きの倍率 CosmicSurfaceSlant は cos² で、輪郭へなだらかに消える）。画像の細部は曲面に貼られたように縮んで見え、筋になりません。
// どちらも、主役のカメラの視線のどの点も画像の同じ画素（主役のカメラから画像の面への投影）の値を持つので、主役のカメラから見た
// 明るさの合計はちょうど画像です。体積の歩みと曲面の交点は、奥行きの順に重ねます（塵の層より奥の光だけが暗くなる）。
// 塵: 主役の視線の上で、曲面の重みの合計が模型ごとの割合（CosmicNebulaDustFraction）に達する交点（G）に、主役の視線の透過率が
// 1 − 0.92·塵 になる薄い吸収の層を載せます。
bool CosmicGuideModeled(uint slab) { return _CosmicGuideModel[slab].x > 0.5; }

// 主役のカメラ（eye）からの視線の上の点 x の、画像の色・下地・細部・視線ごとの合計。
struct CosmicGuidedSample
{
    float2 p;
    float4 g;
    float3 baseColor, detailColor;
    float4 column;
    float window;
};

CosmicGuidedSample CosmicGuidedSampleAt(uint slab, float3 x, float3 eye, float footprint)
{
    CosmicGuidedSample sample = (CosmicGuidedSample)0;
    sample.p = CosmicGuideProject(x, eye);
    float lod = clamp(log2(max(footprint * _CosmicGuideInfo.x * 0.5, 1e-6)), 0.0, _CosmicGuideInfo.y - 1.0);
    sample.g = CosmicGuideSample(slab, sample.p, lod);
    float3 low = CosmicGuideSample(slab, sample.p, max(COSMIC_NEBULA_BASE_LOD, lod)).rgb;
    sample.baseColor = min(sample.g.rgb, COSMIC_NEBULA_BASE_FRACTION * low);
    sample.detailColor = sample.g.rgb - sample.baseColor;
    sample.column = SAMPLE_TEXTURE2D_ARRAY_LOD(_CosmicGuideColumn, sampler_CosmicGuideColumn, sample.p * 0.5 + 0.5, _CosmicGuideModel[slab].y, 0);
    sample.window = smoothstep(1.0, 0.6, length(sample.p)) * smoothstep(1.0, 0.85, length(x));
    return sample;
}

float4 CosmicGuidedNebula(CosmicRay ray, uint slab)
{
    float4 r0 = _CosmicGuideBasis[slab * 3], r1 = _CosmicGuideBasis[slab * 3 + 1], r2 = _CosmicGuideBasis[slab * 3 + 2];
    float3 eye = float3(r0.w, r1.w, r2.w);
    // 視線を画像の面の座標へ（q の単位。基底は正規直交なので向きは単位のまま）。
    float3 oq = ray.origin * 2.0;
    float3 o = float3(dot(r0.xyz, oq), dot(r1.xyz, oq), dot(r2.xyz, oq));
    float3 d = float3(dot(r0.xyz, ray.direction), dot(r1.xyz, ray.direction), dot(r2.xyz, ray.direction));
    float uIn = ray.enter * 2.0, uOut = ray.exit * 2.0;
    uint model = (uint)(_CosmicGuideModel[slab].x + 0.5);
    // 曲面の交点を集めます。
    float hitU[COSMIC_SURFACE_HITS];
    uint hitSurface[COSMIC_SURFACE_HITS];
    uint count = 0;
    [loop] for (uint i = 0; i < COSMIC_SURFACE_MAX; i++)
    {
        CosmicNebulaSurface surface = CosmicNebulaSurfaceAt(model, i);
        if (surface.weight <= 0.0) continue;
        float t0, t1;
        uint roots = CosmicSurfaceIntersect(surface, o, d, t0, t1);
        [loop] for (uint k = 0; k < roots; k++)
        {
            float t = k == 0 ? t0 : t1;
            if (t >= uIn && t <= uOut && count < COSMIC_SURFACE_HITS) { hitU[count] = t; hitSurface[count] = i; count++; }
        }
    }
    float3 color = 0;
    float transmittance = 1.0;
    float previous = -1e30;
    uint done = 0;
    // 下地の体積の歩数（ワイプでは半分、CosmicShellShade と同じ）。
    int steps = max((int)(COSMIC_VOLUME_STEPS * max(_CosmicP3.w, 0.25) * CosmicQualitySteps() * (CosmicIsObserverPass() ? 0.5 : 1.0)), 8);
    float du = (uOut - uIn) / steps;
    // 歩みの始点のずらし（CosmicShellShade と同じ、物体空間に固定した升目のハッシュ。CosmicAnchoredJitter）。
    // 入る点（カメラが中なら 1 歩目の点）で決めます。u は q の単位なので、画素の大きさも q の単位です。
    float uAnchor = max(uIn, du);
    float jitter = CosmicAnchoredJitter(o + d * uAnchor, ray.pixelAngle * uAnchor);
    [loop] for (int step = 0; step <= steps; step++)
    {
        float uStep = step < steps ? uIn + (step + jitter) * du : 1e30;
        // この歩みより手前の曲面の交点（細部）を、手前から順に重ねます（交点は 20 個までなので選択で十分）。
        [loop] for (; done < count; done++)
        {
            uint best = 0; float bestU = 1e30;
            [loop] for (uint m = 0; m < count; m++)
            {
                if (hitU[m] > previous && hitU[m] < bestU) { bestU = hitU[m]; best = m; }
            }
            if (bestU > uStep) break;
            previous = bestU;
            uint index = hitSurface[best];
            CosmicNebulaSurface surface = CosmicNebulaSurfaceAt(model, index);
            float3 x = o + d * bestU;
            float3 n = CosmicSurfaceNormal(surface, x);
            float cosView = abs(dot(n, d));
            float footprint = ray.pixelAngle * bestU / max(cosView, 0.2);
            CosmicGuidedSample sample = CosmicGuidedSampleAt(slab, x, eye, footprint);
            if (abs(sample.p.x) >= 1.0 || abs(sample.p.y) >= 1.0) continue;
            float3 heroOrigin, heroDirection;
            CosmicGuideRay(sample.p, eye, heroOrigin, heroDirection);
            float cosHero = abs(dot(n, heroDirection));
            // 主役のカメラから見た明るさの COSMIC_SURFACE_MAX_GAIN 倍まで（主役のカメラから見ると両者は同じなので、正面の一致は保たれます）。
            float slant = min(CosmicSurfaceSlant(cosView), COSMIC_SURFACE_MAX_GAIN * CosmicSurfaceSlant(cosHero));
            // 拡大されて guide の画素が見えるほど近いときだけ、細かい模様を足します（平均は 1 のまま）。
            float magnified = saturate(-log2(max(footprint * _CosmicGuideInfo.x, 1e-6)) * 0.5);
            float detail = magnified > 0.0 ? max(1.0 + magnified * 0.45 * CosmicFbmTex(x * 24.0 + _CosmicBody.z, 3, footprint * 24.0, 2.1, 0.55), 0.0) : 1.0;
            color += transmittance * sample.detailColor * (CosmicSurfaceWeight(surface, x) * slant / max(sample.column.r, 1e-6) * sample.window * detail);
            if (abs(CosmicSurfaceHitCode(index, CosmicSurfaceHeroRoot(surface, x, eye)) - sample.column.g) < 0.25)
            {
                float tau = -log(max(1.0 - 0.92 * saturate(sample.g.a) * sample.window, 0.02)) * max(cosHero, COSMIC_SURFACE_MIN_COSINE);
                transmittance *= exp(-tau / max(cosView, COSMIC_SURFACE_MIN_COSINE));
            }
        }
        if (step >= steps) break;
        // 下地の体積の 1 歩。
        float3 x = o + d * uStep;
        float density = CosmicNebulaVolumeDensity(model, x);
        if (density > 1e-5)
        {
            CosmicGuidedSample sample = CosmicGuidedSampleAt(slab, x, eye, ray.pixelAngle * max(uStep, 1e-4));
            if (abs(sample.p.x) < 1.0 && abs(sample.p.y) < 1.0)
            {
                color += transmittance * sample.baseColor * (density / max(sample.column.b, 1e-6) * sample.window * du);
            }
        }
    }
    color *= _CosmicP1.w * _CosmicP0.w;
    return float4(color, 1.0 - transmittance);
}

float4 CosmicShellShade(CosmicRay ray)
{
    uint shape = (uint)(_CosmicBody.y + 0.5);
    if (shape == 4) return CosmicGlobularCluster(ray);
    float t = CosmicSeconds() * _CosmicP2.w;
    float enter = ray.enter, exit = ray.exit;
    if (shape == 5)
    {
        // 円盤銀河は薄い板の中だけを積分します（球全体を歩くと、ほとんどの歩数が空の空間に使われる）。
        float halfHeight = max(_CosmicP3.y * 8.0, 0.14) * 0.5;
        if (abs(ray.direction.y) > 1e-5)
        {
            float a = (-halfHeight - ray.origin.y) / ray.direction.y, b = (halfHeight - ray.origin.y) / ray.direction.y;
            enter = max(enter, min(a, b)); exit = min(exit, max(a, b));
        }
        else if (abs(ray.origin.y) > halfHeight) return 0;
    }
    float length01 = exit - enter;
    if (length01 <= 0) return 0;
    // 画像のガイドの星雲は、guide の配列があれば、形の模型（CosmicGuidedNebula）か、模型がまだ無ければ 1 枚の面（CosmicGuidedCards）で描きます。
    int guideCode = CosmicSlabCode();
    if (_CosmicGuideCards > 0.5 && guideCode >= 1 && guideCode < 101 &&
        (((((uint)(_CosmicGuidedMask + 0.5)) >> (uint)(guideCode - 1)) & 1u) != 0u))
    {
        uint guideSlab = (uint)(guideCode - 1);
        return CosmicGuideModeled(guideSlab) ? CosmicGuidedNebula(ray, guideSlab) : CosmicGuidedCards(ray, guideSlab);
    }
    int steps = (int)(COSMIC_VOLUME_STEPS * max(_CosmicP3.w, 0.25) * CosmicQualitySteps());
    // 画像のガイドから作った星雲は、奥行きの層が薄いので歩数を2倍にします（取りこぼすと中心から放射状の筋が出たため）。
    int code = CosmicSlabCode();
    if (_CosmicVolumeBaked > 0.5 && code >= 1 && code < 101 && (((((uint)(_CosmicGuidedMask + 0.5)) >> (uint)(code - 1)) & 1u) != 0u)) steps *= 2;
    // ワイプでは歩数を半分にします（画素が少なく、視線の復元も近似なので細部は見えない）。
    if (CosmicIsObserverPass()) steps = max(steps / 2, 8);
    float dt = length01 / steps;
    // 画素ごとに始点をずらして段差の縞を消します。ずらしは、光線が体積に入る点（カメラが中なら 1 歩目の点）の、物体空間に固定した
    // 升目のハッシュです（CosmicAnchoredJitter）。画素座標のハッシュでは粒の模様が画面に貼り付き、視点が動くと取り残された（2026-10-06）。
    // インターリーブド勾配ノイズは歩数の少ない体積で 2 画素周期の斜めの規則的な網点（スクリーントーン）になった（2026-10-05）。
    float sAnchor = max(enter, dt);
    float jitter = CosmicAnchoredJitter((ray.origin + ray.direction * sAnchor) * 2.0, ray.pixelAngle * sAnchor * 2.0);
    float3 color = 0;
    float transmittance = 1;
    float density = _CosmicP1.w;
    // 銀河の星（CosmicGalaxyStar）。セルの大きさは銀河の中心までの距離の画素から決め、隣り合う 2 段を重ねます。
    bool galaxy = shape == 6 || (shape == 5 && _CosmicVolumeBaked > 0.5 && code >= 101);
    float starFraction = 0, weightB = 0;
    int levelA = 0;
    float3 qo = ray.origin * 2.0;
    CosmicStarWalk walkA = (CosmicStarWalk)0, walkB = (CosmicStarWalk)0;
    // ワイプでは星へ配りません（拡散光だけ）。
    if (galaxy && !CosmicIsObserverPass())
    {
        float cell = max(ray.pixelAngle * length(ray.origin) * 2.0 * COSMIC_STAR_CELL_PIXELS, 1e-6);
        starFraction = COSMIC_STAR_FRACTION * smoothstep(12.0, 40.0, 2.0 / cell);
        if (starFraction > 0.0)
        {
            float level = log2(cell);
            float lower = floor(level);
            weightB = level - lower;
            levelA = (int)lower;
            walkA = CosmicStarWalkBegin(qo, ray.direction * 2.0, enter, exp2(lower), COSMIC_STAR_CELLS);
            walkB = CosmicStarWalkBegin(qo, ray.direction * 2.0, enter, exp2(lower + 1.0), COSMIC_STAR_CELLS);
        }
    }
    [loop] for (int i = 0; i < steps; i++)
    {
        float s = enter + (i + jitter) * dt;
        float3 q = (ray.origin + ray.direction * s) * 2.0;
        float footprint = ray.pixelAngle * max(s, 1e-4) * 2.0;
        CosmicMedium medium;
        if (galaxy) medium = CosmicGalaxyMedium(shape, q, t, footprint, starFraction);
        else medium = CosmicMediumAt(shape, q, t, footprint);
        float stepLength = dt * 2.0;
        color += transmittance * medium.emission * density * stepLength;
        if (starFraction > 0.0)
        {
            float sEnd = enter + (i + 1) * dt;
            float3 stars = 0;
            if (weightB < 0.99) CosmicStarWalkTo(walkA, sEnd, levelA, shape, qo, ray.direction, ray.pixelAngle, t, 1.0 - weightB, stars);
            if (weightB > 0.01) CosmicStarWalkTo(walkB, sEnd, levelA + 1, shape, qo, ray.direction, ray.pixelAngle, t, weightB, stars);
            color += transmittance * stars * (starFraction * density);
        }
        transmittance *= exp(-medium.absorption * density * stepLength);
        if (transmittance < 0.01) break;
    }
    color *= _CosmicP0.w;
    float alpha = 1.0 - transmittance;
    return float4(color, alpha);
}

#endif
