// ガス惑星の大気（帯・流れ・渦・小渦）の色を求める関数（CosmicGasGiantSurface.hlsl と Graphs/CosmicGraphNodes.hlsl が共有します）。
// 材質の定数は読まず（画像は CosmicGasGiantMap.hlsl の global。無ければ手続きだけ）、自己完結の部品です（_CosmicP0 は p0 引数で渡します: x = 自転の周期、y = 流れの強さ、z = リセットの周期、w = 巨大渦の周期）。
#ifndef COSMIC_GAS_GIANT_ATMOSPHERE_INCLUDED
#define COSMIC_GAS_GIANT_ATMOSPHERE_INCLUDED

#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicNoise.hlsl"
#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicGasGiantMap.hlsl"

// 緯度（sinφ = b）ごとの相対風速（自転に対する、右回りを正）。木星は赤道の強い順行ジェットと交互のジェット、海王星は赤道が逆行。
float CosmicZonalWind(float b, uint style)
{
    float phi = asin(clamp(b, -1, 1));
    if (style == 1)
    {
        // 海王星: 赤道で逆行（-）、中緯度で順行（+）の滑らかな分布。
        return -0.9 * cos(phi * 2.2) + 0.25;
    }
    if (style == 2)
    {
        // 土星: 赤道の幅広く強い順行ジェットと、弱い交互の帯。
        return 0.8 * exp(-phi * phi * 6.0) + 0.25 * sin(phi * 14.0);
    }
    // 木星: 交互の帯のジェット（緯度方向に約 7 往復）を、極へ向かって弱めます。
    float jets = sin(phi * 21.0 + 0.4) * 0.5 + sin(phi * 9.5) * 0.3;
    return jets * (1.0 - 0.6 * abs(b)) + 0.35 * exp(-phi * phi * 40.0);
}

// 帯の色（緯度の帯テーブル）。b に加えた揺らぎで境界が波打ちます。
float3 CosmicBandColor(float b, float detail, uint style)
{
    float phi = asin(clamp(b, -1, 1));
    if (style == 1)
    {
        float3 deep = float3(0.06, 0.15, 0.50), light = float3(0.20, 0.38, 0.78);
        float band = 0.5 + 0.5 * sin(phi * 9.0 + detail * 1.5);
        float3 c = lerp(deep, light, band * 0.45 + 0.25 * detail);
        return lerp(c, float3(0.55, 0.68, 0.92), smoothstep(0.75, 0.95, abs(b)));
    }
    if (style == 2)
    {
        // 土星: 淡い黄土色の帯（コントラストは木星よりずっと弱い）、極は青みがかった灰色。
        float s2 = sin(phi * 13.0 + detail * 1.2) + 0.4 * sin(phi * 37.0 + 0.7 + detail * 2.0);
        float3 pale = float3(0.88, 0.80, 0.62), ochre = float3(0.74, 0.62, 0.42);
        float3 c2 = lerp(ochre, pale, smoothstep(-0.8, 0.9, s2)) * (0.9 + 0.2 * (0.5 + 0.5 * detail));
        c2 = lerp(c2, c2 * float3(1.04, 1.0, 0.9), exp(-phi * phi * 20.0));
        return lerp(c2, float3(0.55, 0.60, 0.66), smoothstep(0.8, 0.97, abs(b)));
    }
    // 木星: 明るい「帯（zone）」と赤茶色の「縞（belt）」。
    // 主な帯（約 7 本）と細い縞（約 3 倍の細かさ）を重ねます。
    float s = sin(phi * 10.5 + 0.4 + detail * 1.8) + 0.5 * sin(phi * 29.0 + 1.3 + detail * 2.6);
    float zone = smoothstep(-0.7, 0.8, s);
    float3 belt = float3(0.50, 0.32, 0.20), beltDark = float3(0.30, 0.18, 0.11);
    float3 zoneColor = float3(0.86, 0.80, 0.68), zoneCream = float3(0.74, 0.66, 0.55);
    float3 c = lerp(lerp(beltDark, belt, 0.5 + 0.5 * detail), lerp(zoneCream, zoneColor, 0.5 + 0.5 * detail), zone);
    // 雲の濃淡（帯の中の明暗のむら）。
    c *= 0.82 + 0.36 * (0.5 + 0.5 * detail);
    // 赤道帯はやや黄色く、極域は灰青に。
    c = lerp(c, c * float3(1.05, 0.98, 0.85), exp(-phi * phi * 30.0));
    c = lerp(c, float3(0.55, 0.55, 0.58), smoothstep(0.72, 0.95, abs(b)));
    return c;
}

// 渦: 中心 center（単位球上）、角半径 radius、扁平 flatten（経度方向に伸ばす）、内部の回転角 angle。
// 中心ほど回転が大きいので、時間で角度を進めると渦巻きます（2位相リセットで巻きすぎない）。
float3 CosmicApplyVortex(float3 q, float3 center, float radius, float flatten, float angle, out float mask)
{
    float3 east = normalize(cross(float3(0, 1, 0), center) + 1e-5);
    float3 north = cross(center, east);
    float3 d = q - center * dot(q, center);
    float2 local = float2(dot(d, east) / flatten, dot(d, north));
    float r = length(local) / radius;
    mask = exp(-r * r * 1.4);
    float swirl = angle * exp(-r * r * 0.9);
    return normalize(CosmicRotateAxis(q, center, swirl));
}

float3 CosmicGasGiantAtmosphere(float3 q, float t, float footprint, uint style, float seed, float4 p0, out float vortexTint)
{
    float flowStrength = p0.y;
    float period = max(p0.z, 1.0);
    float vortexPeriod = max(p0.w, 1.0);
    float3 color = 0;
    vortexTint = 0;
    // NASA の画像を土台にする量（Windows。0 なら従来どおり手続きだけ）。画像には本物の大赤斑があるので、手続きの大赤斑の渦と色は消し、
    // 帯の流れは弱めて（画像が引き伸ばされて崩れないよう）、動きと細かい雲・小渦・海王星の暗斑は手続きで重ねる。
    float mapAmount = CosmicGasMapAmount(style);
    [unroll] for (int j = 0; j < 2; j++)
    {
        float tau, weight, epoch;
        CosmicFlowPhase(t, period, j, tau, weight, epoch);
        float advance = tau * period;
        // 帯ごとの相対流（極軸まわり）。
        float wind = CosmicZonalWind(q.y, style);
        float3 p = CosmicRotateY(q, wind * flowStrength * advance * 0.02 * lerp(1.0, 0.3, mapAmount));
        // 巨大渦: 木星の大赤斑（南緯22°）、海王星の大暗斑（南緯20°）。経度方向にゆっくり漂流します。
        float drift = (style == 1 ? 0.010 : -0.004) * t;
        float lat = style == 1 ? -0.35 : -0.38;
        float3 center = normalize(float3(cos(lat) * cos(drift + seed), sin(lat), cos(lat) * sin(drift + seed)));
        float grsMask;
        p = CosmicApplyVortex(p, center, style == 1 ? 0.10 : 0.15, 1.6, advance * 6.2831 / vortexPeriod * (style == 0 ? 1.0 - mapAmount : 1.0), grsMask);
        // 小渦（白斑）: 寿命のある 8 個。
        float ovalMask = 0;
        [loop] for (int k = 0; k < 8; k++)
        {
            float life = 40.0 + 25.0 * CosmicHash1(int3(k, 3, (int)seed));
            float x = t / life + CosmicHash1(int3(k, 7, (int)seed));
            float generation = floor(x);
            float age = x - generation;
            float3 h = CosmicHash3(int3(k, (int)generation, (int)seed + 11));
            float olat = (h.x - 0.5) * 1.6;
            float olon = h.y * 6.2831 + t * 0.004 * (h.z - 0.5);
            float3 oc = float3(cos(olat) * cos(olon), sin(olat), cos(olat) * sin(olon));
            float m;
            p = CosmicApplyVortex(p, oc, 0.025 + 0.02 * h.z, 1.5, advance * 6.2831 / (vortexPeriod * 0.5) * (h.z > 0.5 ? 1 : -1), m);
            // 出現→成熟→消滅（寿命の最初と最後で薄く）。
            ovalMask = max(ovalMask, m * smoothstep(0.0, 0.2, age) * smoothstep(1.0, 0.75, age));
        }
        // 位相ごとに模様の領域をずらし、リセットのたびに模様が少しずつ変わるようにします（同じ形の繰り返しにしない）。
        float3 domain = p * 4.0 + (CosmicHash3(int3((int)epoch, j, (int)seed)) - 0.5) * 3.0;
        float shear = abs(CosmicZonalWind(q.y + 0.02, style) - CosmicZonalWind(q.y - 0.02, style)) * 25.0;
        float warp = CosmicFbm(domain * float3(1, 2.5, 1), 5, footprint * 4.0, 2.1, 0.55);
        float b = p.y + warp * (0.02 + 0.05 * saturate(shear));
        // 経度方向に伸びた細かい雲（緯度方向の周波数を高く）。
        float clouds = CosmicFbm(domain * float3(2.5, 9.0, 2.5) + warp * 2.0, 7, footprint * 9.0, 2.2, 0.55);
        float3 c = CosmicBandColor(b, clouds, style);
        if (mapAmount > 0.0)
        {
            // 画像の色の上に、画像より細かい雲の濃淡を重ねる（海王星は 1024×512 なので特に効く）。
            float3 mapColor = CosmicGasMapColor(p, style, footprint) * (0.84 + 0.32 * (0.5 + 0.5 * clouds));
            c = lerp(c, mapColor, mapAmount);
            if (style == 0u) { grsMask *= 1.0 - mapAmount; }
        }
        // 大赤斑の色（赤橙の芯）と、海王星の大暗斑（暗い青）。
        float3 spot = style == 1 ? float3(0.05, 0.10, 0.35) : float3(0.72, 0.30, 0.16);
        // 土星には大赤斑のような目立つ渦を描きません。
        if (style == 2) { grsMask = 0.0; ovalMask *= 0.3; }
        c = lerp(c, spot * (0.8 + 0.4 * clouds), smoothstep(0.25, 0.75, grsMask));
        // 大赤斑のまわりの明るい輪（渦の縁を流れる雲）。
        c = lerp(c, float3(0.9, 0.85, 0.75), smoothstep(0.05, 0.2, grsMask) * smoothstep(0.45, 0.2, grsMask) * 0.5 * (style == 1 ? 0.0 : 1.0));
        c = lerp(c, style == 1 ? float3(0.85, 0.90, 1.0) : float3(0.97, 0.95, 0.92), ovalMask * 0.85);
        if (style == 1)
        {
            // 海王星の明るい筋雲（大暗斑の近くに多い）。
            float streak = saturate(CosmicNoise(domain * float3(1.5, 14.0, 1.5)) * 2.0 - 0.6);
            c = lerp(c, float3(0.92, 0.95, 1.0), streak * (0.25 + 0.6 * smoothstep(0.1, 0.6, grsMask)));
        }
        color += c * weight;
        vortexTint += grsMask * weight;
    }
    return color;
}

#endif
