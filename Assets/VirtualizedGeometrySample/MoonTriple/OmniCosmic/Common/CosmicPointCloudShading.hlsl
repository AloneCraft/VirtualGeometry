// CosmicHierarchy の点群（CosmicPointCloud、恒星・銀河のカタログと推定の点）。1点ごとの四角形（頂点の位置 = 点の中心、uv = 角 -1..1）を
// 画素単位で広げ、見かけの等級から明るさと大きさを決めます。加算合成、SV_Depth は点の中心の深度（手前の不透明な天体に隠れる）。
// 見かけの等級 m = 絶対等級 + 5 log10(距離 pc) − 5。光束は基準の等級との差の 10^(−0.4 (m − 基準))。
// 基準の等級（露出）はカメラの側の性質で、カメラと注視点の距離に合わせて上げます（_CosmicP0.y + 5 log10(距離 pc) − 5 + _CosmicP1.x）。
// ただし下限 _CosmicP1.y（恒星なら肉眼の 6.5 等）と上限 _CosmicP1.z で止めます。上限が無いと、構造の外へ出るほど一点の明るさが保たれたまま
// 画面上の点の密度だけが増え、面輝度が距離の 2 乗で増えて白く飛びます（2026-10-03 の撮影で確認）。上限で止まった後は、外から見た構造の
// 面輝度は物理と同じく距離によらず一定です。上限を固定の値だけにすると、月や地球の場面でも遠い銀河が全部見えてしまいます（同日に確認）。
// 暗い点（光束 < 閾値 _CosmicP1.w）は消さずに、光束 ÷ 閾値 の確率で残して閾値の明るさで描きます（点ごとの乱数 TEXCOORD1.z）。
// 光の総量の期待値が保たれるので、遠ざかっても構造は暗くならず、描く点の数は減ります。
// 色と大きさ（2026-10-05）: 芯の明るさは COSMIC_CLOUD_PEAK で止めて星の色を保ち（以前は光束 6 まで伸ばして ACES で白く飛び、色が見えなかった）、
// 明るい星ほど点を大きくします（log10(1 + 光束)）。基準より _CosmicP2.y 等以上明るい点（画面の星の上位およそ 2%）は芯を _CosmicP2.z 倍にし、
// 広い光（グレア）を付けて Bloom を効かせます。
// 銀河の形（_CosmicP2.x = 1）: 2MRS の銀河は、形態と直径の推定（TEXCOORD1.w）から、見かけの大きさの _CosmicP2.w 倍（下限は点の 1.4 倍）の
// 小さな銀河（渦巻・楕円・不規則）として描きます。向きと傾きは点ごとの乱数。恒星と同じ丸い点では、銀河団の場面が星の群れに見えた。
// _CosmicP0: x = 物体空間の 1 が何 pc か、y = 基準の絶対等級、z = 点の半径（画素）、w = 明るさ。
// _CosmicP1: x = 基準のずらし（等級）、y = 基準の下限、z = 基準の上限、w = 間引きの閾値。
// _CosmicP2: x = 銀河の形を描く（1）、y = グレアを付ける明るさ（基準より何等明るいか）、z = グレアの点の芯の倍率（0 でグレアなし）、w = 銀河の形の拡大率。
// _CosmicP3.w > 0: 網目の淡い光として描く（銀河の点群の2枚目、CosmicHierarchySceneBuilder の GalaxyCloudGlow）。各点を半径 _CosmicP3.w pc の
//   ガウスの光にし（画面で 1.5〜COSMIC_CLOUD_GLOW_MAX_PIXELS 画素）、光の総量は点の COSMIC_CLOUD_GLOW_GAIN 倍。重なった所が光るので、淡い光は
//   実際の銀河の壁とフィラメントに沿います（手続き生成の網目の体積は銀河の分布と無関係で、点が浮いて見えた。2026-10-05 のユーザー指摘）。
// _CosmicP3: x = 遠くから見るときに大きさの拡大とグレアを止める（1）、y・z = 止め始めと止め終わりの、点群の中心からカメラまでの距離（pc）。
//   天の川を外から見る場面で、明るい点が大きな球に見えた（2026-10-05 のユーザー指摘「銀河系スケールでは目立った大きさがないほうがいい」）。
//   止めた後も芯の明るさ（上限 COSMIC_CLOUD_PEAK）はそのままなので、明るい点の光の総量は減ります。
// global の _CosmicPointExposure.x = log10(カメラと注視点の距離 pc)。
// 頂点の TEXCOORD1: x = 絶対等級、y = 大きさの倍率、z = 点ごとの一様乱数 [0, 1)、w = 銀河の形（0 = なし、1 + 形態 + 8 × 直径 kpc）、
// または負の −(種族 + 温度 / 10⁵)（CosmicHierarchyStarMap.Encode。温度はいて座A* の星の地図だけが使う）。
// 天の川の差動回転（種族 1 = バルジ、2 = 円盤、3 = 若い星。2026-10-05 のユーザー判断）: 点の位置は J2000 の配置で、バルジは棒のパターン速度で剛体回転、
// 円盤は回転曲線 Ω(R) で回します。腕は回る密度波なので位置には埋め込まず、今の腕の位相の倍率 w（CosmicGalacticRotation.Weight と同じ式、平均 1）に比例する
// 確率 w / w_max で点を残し、残した点は w_max 倍明るくします（光の総量の期待値は w と同じ）。明るさだけで表すと、明るい点は芯の上限で止まり、
// 遠くからは大きさの拡大も止めるので、外から見た天の川の腕が見えなくなった（2026-10-05 の撮影）。点の密度で表すのは以前の棄却法と同じ見え方です。
// 太陽のまわり（_CosmicGalaxySun.w）は恒星のカタログが受け持つので描きません。
#ifndef COSMIC_POINT_CLOUD_SHADING_INCLUDED
#define COSMIC_POINT_CLOUD_SHADING_INCLUDED

float4 _CosmicPointExposure;

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

// 腕の本数とピッチ角（CosmicMilkyWayLight.ArmCount・PitchDegrees と同じ）と、種族ごとの明るさの倍率の平均（CosmicGalacticRotation.DiskWeightMean・YoungWeightMean）。
#define COSMIC_GALAXY_ARMS 4.0
#define COSMIC_GALAXY_PITCH 0.20943951
#define COSMIC_GALAXY_DISK_MEAN 0.3549536
#define COSMIC_GALAXY_YOUNG_MEAN 0.1981714
// 倍率の最大（CosmicGalacticRotation.MaxWeight）: 古い星は s = 0.675 で 1.2138、若い星は s = 1 で 3.0277。
#define COSMIC_GALAXY_DISK_MAX 1.213839
#define COSMIC_GALAXY_YOUNG_MAX 3.027683

// 天の川の点を時刻の位置へ回し、明るさの倍率（腕の位相、種族ごと）と、太陽のまわりで描かないか（visible）を返します。
float3 CosmicGalaxyRotate(float3 p, int population, out float weight, out float visible)
{
    float kpcPerUnit = _CosmicP0.x / 1000.0;
    float radius = length(p.xz) * kpcPerUnit;
    float angle;
    if (population == 1)
    {
        angle = _CosmicGalaxyRotation.z;
    }
    else
    {
        float r = max(radius, 0.05);
        float omega = _CosmicGalaxyCurve.x * tanh(r / _CosmicGalaxyCurve.y) / r * _CosmicGalaxyCurve.z;
        angle = _CosmicGalaxyRotation.w * omega * _CosmicGalaxyRotation.x;
    }
    float c = cos(angle), s = sin(angle);
    float3 q = float3(p.x * c - p.z * s, p.y, p.x * s + p.z * c);
    weight = 1.0;
    if (population >= 2)
    {
        float phase = COSMIC_GALAXY_ARMS * (atan2(q.z, q.x) - _CosmicGalaxyRotation.y - log(max(radius, 0.5) / _CosmicGalaxyCurve.w) / tan(COSMIC_GALAXY_PITCH));
        float a = 0.5 + 0.5 * cos(phase);
        float strength = a * a * a;
        float placed = 0.35 + 0.65 * strength;
        weight = population == 2 ? placed * (0.85 - 0.45 * strength) / COSMIC_GALAXY_DISK_MEAN : placed * (0.15 + 0.45 * strength) / COSMIC_GALAXY_YOUNG_MEAN;
    }
    visible = length(q - _CosmicGalaxySun.xyz) < _CosmicGalaxySun.w ? 0.0 : 1.0;
    return q;
}

// 点の芯の明るさの上限（_CosmicP0.w を掛ける前）。これより明るい星は大きさで明るさを表します。
#define COSMIC_CLOUD_PEAK 1.0
// 点の大きさの倍率の上限（暗い点の何倍まで大きくするか）と、遠くから見るとき（_CosmicP3）の上限。
#define COSMIC_CLOUD_MAX_GROWTH 3.2
#define COSMIC_CLOUD_DISTANT_GROWTH 0.8
// 網目の淡い光の画面の半径の上限（画素）と、光の総量の倍率。上限の半分から上限までで淡い光を消し、上限を超える点は描きません
// （手前の銀河の淡い光は大きなぼけた玉に見え、大きな四角形の塗り重ねで GPU が 44 ms になった。手前の銀河は点そのものが見えている）。
#define COSMIC_CLOUD_GLOW_MAX_PIXELS 48.0
#define COSMIC_CLOUD_GLOW_GAIN 0.06

struct CosmicCloudAttributes
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionOS : POSITION;
    float2 uv         : TEXCOORD0;
    float4 data       : TEXCOORD1;
    float4 color      : COLOR;
};

struct CosmicCloudVaryings
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionCS : SV_POSITION;
    float2 corner : TEXCOORD0;
    nointerpolation float4 colorIntensity : TEXCOORD1;
    nointerpolation float4 depthOS : TEXCOORD2;
    // x = グレアの強さ 0..1、y = 銀河の形の強さ 0..1、z = 形態、w = 点の四角形に対する銀河の大きさ（0..1）。
    nointerpolation float4 shape : TEXCOORD3;
    // 銀河の向き（cos, sin）、短軸と長軸の比、腕の位相。
    nointerpolation float4 orientation : TEXCOORD4;
};

CosmicCloudVaryings CosmicCloudVertex(CosmicCloudAttributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    CosmicCloudVaryings output = (CosmicCloudVaryings)0;
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    float3 center = input.positionOS.xyz;
    float galaxyWeight = 1.0, galaxyVisible = 1.0;
    if (input.data.w <= -1.0)
    {
        int population = (int)floor(-input.data.w);
        float weight;
        center = CosmicGalaxyRotate(center, population, weight, galaxyVisible);
        if (population >= 2)
        {
            // 腕の位相の倍率に比例する確率で残します（間引きの乱数 data.z とは別の、点ごとの乱数）。
            float maximum = population == 2 ? COSMIC_GALAXY_DISK_MAX : COSMIC_GALAXY_YOUNG_MAX;
            float chance = CosmicHash3(int3((int)(input.data.z * 16777216.0), 59, 13)).x;
            galaxyVisible *= chance * maximum < weight ? 1.0 : 0.0;
            galaxyWeight = maximum;
        }
    }
    float4 clip = OmniRadialUsesLogicalTransform() ? CosmicInvariantLogicalClip(center) : CosmicInvariantObjectClip(center);
    float distancePc = max(length(CosmicGlowEyeObjectSpace() - center) * _CosmicP0.x, 1e-6);
    float apparent = input.data.x + 5.0 * log10(distancePc) - 5.0;
    float reference = clamp(_CosmicP0.y + 5.0 * _CosmicPointExposure.x - 5.0 + _CosmicP1.x, _CosmicP1.y, _CosmicP1.z);
    float flux = exp2(-0.4 * 3.3219281 * (apparent - reference));
    float intensity = flux * CosmicFade() * galaxyWeight;
    // 閾値より暗い点は確率 intensity / 閾値 で閾値の明るさにし、残りは四角形を潰します（画素を塗らない）。
    float threshold = _CosmicP1.w;
    float visible = 1.0;
    if (intensity < threshold)
    {
        visible = input.data.z * threshold < intensity ? 1.0 : 0.0;
        intensity = threshold;
    }
    // 明るさは芯の明るさ（上限まで）と大きさに分けます。大きさは暗い点の 0.55 倍から、明るい点ほど log で大きく。
    // 遠くから見るとき（_CosmicP3）は、大きさの拡大とグレアを止めます。
    float distant = _CosmicP3.x > 0.5 ? smoothstep(_CosmicP3.y, _CosmicP3.z, length(CosmicGlowEyeObjectSpace()) * _CosmicP0.x) : 0.0;
    float growth = min(0.55 + 0.45 * log10(1.0 + intensity), COSMIC_CLOUD_MAX_GROWTH);
    growth = lerp(growth, min(growth, COSMIC_CLOUD_DISTANT_GROWTH), distant);
    float radius = _CosmicP0.z * input.data.y * growth;
    float peak = min(intensity, COSMIC_CLOUD_PEAK);
    // グレア: 基準より _CosmicP2.y 等以上明るい点。
    float glare = 0.0;
    if (_CosmicP2.z > 0.0)
    {
        float glareFlux = exp2(0.4 * 3.3219281 * _CosmicP2.y);
        glare = smoothstep(glareFlux, glareFlux * 2.5, intensity) * (1.0 - distant);
        peak *= 1.0 + (_CosmicP2.z - 1.0) * glare;
        radius *= 1.0 + 1.2 * glare;
    }
    // 銀河の形: 見かけの直径（推定）× 拡大率が点より大きいとき。
    float galaxyCode = input.data.w;
    float shapeWeight = 0.0, shapeFraction = 1.0, morphology = 0.0;
    if (_CosmicP3.w > 0.0)
    {
        // 網目の淡い光: 半径 _CosmicP3.w pc のガウス（芯の上限は掛けず、光の総量を保って広げる）。
        float pixelAngle = 2.0 / (abs(UNITY_MATRIX_P[1][1]) * _ScaledScreenParams.y);
        float rawPixels = _CosmicP3.w / distancePc / max(pixelAngle, 1e-12);
        float near = 1.0 - smoothstep(0.5 * COSMIC_CLOUD_GLOW_MAX_PIXELS, COSMIC_CLOUD_GLOW_MAX_PIXELS, rawPixels);
        float glowPixels = clamp(rawPixels, 1.5, COSMIC_CLOUD_GLOW_MAX_PIXELS);
        float spread = _CosmicP0.z / glowPixels;
        peak = intensity * COSMIC_CLOUD_GLOW_GAIN * spread * spread * near;
        radius = near > 0.0 ? glowPixels : 0.0;
        glare = 0.0;
        shapeWeight = 1.0;morphology = 5.0;shapeFraction = 1.0;
        output.orientation = float4(1, 0, 1, 0);
    }
    else if (_CosmicP2.x > 0.5 && galaxyCode >= 1.0)
    {
        float code = galaxyCode - 1.0;
        morphology = fmod(code, 8.0);
        float diameterPc = floor(code / 8.0) * 1000.0;
        float pixelAngle = 2.0 / (abs(UNITY_MATRIX_P[1][1]) * _ScaledScreenParams.y);
        float galaxyPixels = 0.5 * diameterPc / distancePc / max(pixelAngle, 1e-12) * _CosmicP2.w;
        // 見かけの大きさが点より小さくても、点の 1.6 倍の小さな銀河の形で描きます（恒星と同じ丸い点では、銀河団の場面が星の群れに見えた）。
        // 点の大きさ自体が表示上の約束なので、形も同じ約束で描きます。
        shapeWeight = 1.0;
        galaxyPixels = max(galaxyPixels, radius * 1.4);
        float shapeRadius = max(radius, galaxyPixels * 1.15);
        // 光の総量はおおむね保ち、面輝度は芯の 0.35 倍を下限にします（見えなくならないように）。
        float spread = radius / shapeRadius;
        peak = lerp(peak, peak * max(spread * spread * 2.0, 0.35), shapeWeight);
        shapeFraction = lerp(1.0, galaxyPixels / shapeRadius, shapeWeight);
        radius = lerp(radius, shapeRadius, shapeWeight);
        float3 h = CosmicHash3(int3((int)(input.data.z * 16777216.0), 41, 7));
        float angle = h.x * 6.2831853;
        // 渦巻・不規則は傾きで細くなり、楕円は丸みが残ります。
        float axis = morphology < 0.5 ? 0.55 + 0.45 * h.y : 0.18 + 0.82 * h.y;
        output.orientation = float4(cos(angle), sin(angle), axis, h.z * 6.2831853);
    }
    radius *= visible * galaxyVisible;
    clip.xy += input.uv * (2.0 * radius) / _ScaledScreenParams.xy * clip.w;
    output.positionCS = clip;
    output.corner = input.uv;
    output.colorIntensity = float4(input.color.rgb, peak * _CosmicP0.w);
    output.depthOS = CosmicInvariantDepthPosition(center);
    output.shape = float4(glare, shapeWeight, morphology, shapeFraction);
    return output;
}

// 銀河の面輝度（四角形の座標、銀河の端 = 1）。形態: 0 早期型（楕円）、1 渦巻、2 不規則、3 不明（渦巻の腕なし）。
float CosmicGalaxyShape(float2 corner, float4 shape, float4 orientation)
{
    float2 p = corner / max(shape.w, 1e-3);
    p = float2(p.x * orientation.x + p.y * orientation.y, -p.x * orientation.y + p.y * orientation.x);
    p.y /= max(orientation.z, 0.05);
    float r = length(p);
    if (r >= 1.0) return 0.0;
    float edge = 1.0 - smoothstep(0.75, 1.0, r);
    float morphology = shape.z;
    // 網目の淡い光（_CosmicP3.w）: なめらかなガウス。
    if (morphology > 4.5) return exp(-r * r * 3.0) * (1.0 - r * r) * 1.9;
    if (morphology < 0.5)
    {
        // 楕円: なめらかに中心が明るい。
        return (exp(-r * 5.0) * 1.4 + 0.15 * (1.0 - r)) * edge;
    }
    float bulge = exp(-r * r * 60.0) * 1.2;
    float disk = exp(-r * 3.2);
    if (morphology < 1.5)
    {
        // 渦巻: 2 本の対数螺旋の腕（ピッチ約 15°）。
        float arms = 0.5 + 0.5 * cos(2.0 * (atan2(p.y, p.x) - log(max(r, 0.05)) * 3.7) + orientation.w);
        return (bulge + disk * (0.45 + 0.75 * arms * arms)) * edge;
    }
    if (morphology < 2.5)
    {
        // 不規則: 中心の無い斑。
        float lumps = 0.55 + 0.45 * sin(p.x * 9.0 + orientation.w) * sin(p.y * 7.0 + orientation.w * 1.7);
        return disk * (0.6 + 0.8 * lumps) * edge;
    }
    return (bulge + disk * 0.8) * edge;
}

void CosmicCloudFragment(CosmicCloudVaryings input, out half4 outputColor : SV_Target, out float outputDepth : SV_Depth)
{
    UNITY_SETUP_INSTANCE_ID(input);
    float r2 = dot(input.corner, input.corner);
    if (r2 >= 1.0 || input.colorIntensity.a <= 0.001) discard;
    float core = exp(-r2 * 9.0);
    float halo = exp(-r2 * 3.0) * 0.15 * (1.0 - r2);
    // グレア: 芯の外へ広がる光（Bloom の前の画素に入れて、明るい星だけが眩しく見えるように）。
    float glareLight = input.shape.x * exp(-r2 * 2.2) * 0.45 * (1.0 - r2);
    float point_ = core + halo + glareLight;
    float light = lerp(point_, CosmicGalaxyShape(input.corner, input.shape, input.orientation), input.shape.y);
    outputColor = half4(input.colorIntensity.rgb * (input.colorIntensity.a * light), 0);
    outputDepth = CosmicInvariantFragmentDepth(input.depthOS, input.positionCS.z);
}

#endif
