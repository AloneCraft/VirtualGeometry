// CosmicScale の画像のガイドの星雲の星（CosmicNebulaStars、Docs/Plans/CosmicNebula3D.md の段階 3）。公式画像からガイドを作るときに
// 除いた星（make_guides.py の星の一覧）を、1 つずつ画面上で一定の画素の大きさの光る点で描きます。
// 位置: 星のガイドの面の座標 p を通る主役のカメラの視線の上で、形の模型の下地の体積の密度（CosmicNebulaVolumeDensity）に比例した奥行きを、
//   星ごとの乱数で選びます（奥行きが決まっている星は、その奥行き z。密度の無い視線は、単位球の中で一様に）。主役のカメラから見ると、
//   画像の星の位置に重なります。
// 窓: 画像の四角の視野の隅まで星を置くと、横や上から見たとき星が四角い枠に並んで見えたので、ガス（CosmicGuidedSampleAt の window）と
//   同じく、画像の中心からの距離で縁へなだらかに薄れさせます（|p| が 0.7 から 1 で 0 へ。隅の星は描かない）。
// 小さく写る星雲: 描画中のカメラから見た星雲の球（半径 0.5）の見かけの半径が COSMIC_NEBULA_STAR_FADE_PIXELS より小さいときは星を描きません
//   （何千もの星が数画素に重なって、星雲より明るい 1 つの光の点になった。俯瞰のワイプでは、どの星雲の星もほぼ全部が主カメラの位置に
//   重なる描画があり（2026-10-05 に観測、原因の描画経路は未特定）、その点が大きな光になっていた。そこでは星雲の見かけの半径も 0 になる）。
// 明るさ: 光の総量 F（ガイドの RGB × 面の面積）を主役のカメラから見た光束とし、星から主カメラまでの距離の 2 乗で弱めます
//   （主カメラから見た光束 = F × J × (d_hero ÷ d)²。J = 主役のカメラから見た画像の面の単位面積の立体角）。画素の和がこの光束 ÷ 1 画素の
//   立体角になるよう、点の芯の明るさと大きさを決めます（明るい星ほど点を大きくし、芯は COSMIC_NEBULA_STAR_PEAK で止める）。
//   距離と画素の角度は、描画中のカメラではなく圧縮の中心（主カメラ）から測ります（CosmicApparentSize.hlsl。俯瞰カメラにも同じ点が映る）。
// 合成は乗算済みアルファ（アルファ 0 で加算）。深度は書かず SV_Depth で不透明の対数深度と ZTest を合わせます。星雲の体積（描画順 2990）の
// 後に描くので体積に吸収されません（画像の星の明るさは、星雲を通して見た明るさのため）。
// 頂点は 4 個で 1 つの星（四角形）。TEXCOORD0 = (角 x, 角 y, 星の番号, -)。POSITION は使いません（メッシュの境界は物体の球）。
// 材質: _CosmicBody.x = 梯子の段の番号（消え方）、_CosmicBody.w = スラブの番号（CosmicVolumeAtlas.Slab.Code）、
// _CosmicP0.w = 明るさ（星雲の体積の _CosmicP0.w × _CosmicP1.w と同じ）、_CosmicP2.xy = 点の半径の最小と最大（画素）。
// 星の一覧が無い（配布物）・ガイドの配列が無い・番号が星の数以上の四角形は、画面の外へ置いて描きません。
// 頂点関数は1引数、構造体は最初から UNITY_VERTEX_INPUT_INSTANCE_ID を持ちます（BRG の sidecar 生成の条件）。
#ifndef COSMIC_NEBULA_STARS_INCLUDED
#define COSMIC_NEBULA_STARS_INCLUDED

#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicApparentSize.hlsl"
#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicNebulaModels.hlsl"
#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicGuideGlobals.hlsl"

// 点の芯の明るさの上限（明るさを掛けた後）。これより明るい星は点を大きくします（_CosmicP2.y まで）。
#define COSMIC_NEBULA_STAR_PEAK 4.0
// 点の形 exp(-4.5 ρ²)（ρ = 中心からの距離 ÷ 半径、ρ ≤ 1）の面積分 ÷ 半径²（π (1 − e^-4.5) ÷ 4.5）。
#define COSMIC_NEBULA_STAR_KERNEL 0.6912
// 奥行きを選ぶ歩数。
#define COSMIC_NEBULA_STAR_STEPS 48
// 画素の和がこれより小さい星は描きません。
#define COSMIC_NEBULA_STAR_MIN 1e-4
// 星雲の見かけの半径（画素）がこの範囲で星を出します（x 未満は描かない、y 以上は全部）。
#define COSMIC_NEBULA_STAR_FADE_PIXELS float2(3.0, 12.0)

struct CosmicNebulaStarsAttributes
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionOS : POSITION;
    float4 shape      : TEXCOORD0;
};

struct CosmicNebulaStarsVaryings
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionCS : SV_POSITION;
    float4 depthOS    : TEXCOORD0;
    // 点の中心からの位置（点の半径 = 1）。
    float2 corner     : TEXCOORD1;
    // 点の芯の明るさ。
    nointerpolation float3 color : TEXCOORD2;
};

// 主役の視線（origin + t·direction、画像の面の座標）の上の星の位置。z が |z| ≤ 1.5 ならその奥行き、そうでなければ模型の下地の体積の
// 密度に比例した奥行きを一様乱数 u で選びます（密度が無い視線は単位球の中で一様に、単位球を通らない視線は画像の面 z = 0）。
float3 CosmicNebulaStarPosition(uint model, float3 origin, float3 direction, float u, float z)
{
    float tPlane = -origin.z / max(direction.z, 1e-4);
    float t = tPlane;
    float b = dot(origin, direction), c = dot(origin, origin) - 1.0, disc = b * b - c;
    if (abs(z) <= 1.5)
    {
        t = (z - origin.z) / max(direction.z, 1e-4);
    }
    else if (disc > 0.0)
    {
        float root = sqrt(disc);
        float t0 = max(-b - root, 0.0), t1 = -b + root;
        t = lerp(t0, t1, u);
        float dt = (t1 - t0) / COSMIC_NEBULA_STAR_STEPS;
        float density[COSMIC_NEBULA_STAR_STEPS];
        float total = 0.0;
        [unroll] for (int i = 0; i < COSMIC_NEBULA_STAR_STEPS; i++)
        {
            density[i] = CosmicNebulaVolumeDensity(model, origin + direction * (t0 + (i + 0.5) * dt));
            total += density[i];
        }
        if (total > 1e-6)
        {
            float target = u * total, sum = 0.0;
            bool found = false;
            [unroll] for (int k = 0; k < COSMIC_NEBULA_STAR_STEPS; k++)
            {
                if (!found && sum + density[k] >= target)
                {
                    t = t0 + (k + saturate((target - sum) / max(density[k], 1e-9))) * dt;
                    found = true;
                }
                sum += density[k];
            }
        }
    }
    return origin + direction * t;
}

float4 CosmicNebulaStarsClip(float3 positionOS)
{
    return OmniRadialUsesLogicalTransform() ? CosmicInvariantLogicalClip(positionOS) : CosmicInvariantObjectClip(positionOS);
}

// 描画中のカメラから見た、物体の球（半径 0.5）の見かけの半径（画素）。中心と 3 つの軸の端を写した距離の最大で近似します
// （カメラの後ろの点は使いません）。
float CosmicNebulaStarsAxisPixels(float4 center, float4 axis)
{
    return center.w > 1e-6 && axis.w > 1e-6 ? length((axis.xy / axis.w - center.xy / center.w) * (0.5 * _ScaledScreenParams.xy)) : 0.0;
}

float CosmicNebulaStarsSpreadPixels()
{
    float4 center = CosmicNebulaStarsClip(float3(0, 0, 0));
    float4 x = CosmicNebulaStarsClip(float3(0.5, 0, 0));
    float4 y = CosmicNebulaStarsClip(float3(0, 0.5, 0));
    float4 z = CosmicNebulaStarsClip(float3(0, 0, 0.5));
    return max(CosmicNebulaStarsAxisPixels(center, x), max(CosmicNebulaStarsAxisPixels(center, y), CosmicNebulaStarsAxisPixels(center, z)));
}

CosmicNebulaStarsVaryings CosmicNebulaStarsVertex(CosmicNebulaStarsAttributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    CosmicNebulaStarsVaryings output = (CosmicNebulaStarsVaryings)0;
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    // 描かない星の四角形は画面の外へ置きます。
    output.positionCS = float4(4.0, 4.0, 0.5, 1.0);
    int code = (int)(_CosmicBody.w + 0.5);
    uint slab = (uint)clamp(code - 1, 0, 15);
    uint index = (uint)(input.shape.z + 0.5);
    uint count = (uint)(_CosmicGuideModel[slab].z + 0.5);
    uint model = (uint)(_CosmicGuideModel[slab].x + 0.5);
    uint perRow = max((uint)(_CosmicNebulaStarsInfo.x + 0.5), 1u);
    float fade = CosmicFade();
    if (_CosmicGuideCards > 0.5 && code >= 1 && code <= 16 && index < count && model > 0 && fade > 0.0)
    {
        int2 texel = int2((index % perRow) * 2, index / perRow);
        float4 place = LOAD_TEXTURE2D_ARRAY_LOD(_CosmicNebulaStars, texel, slab, 0);
        float4 flux = LOAD_TEXTURE2D_ARRAY_LOD(_CosmicNebulaStars, texel + int2(1, 0), slab, 0);
        float4 r0 = _CosmicGuideBasis[slab * 3], r1 = _CosmicGuideBasis[slab * 3 + 1], r2 = _CosmicGuideBasis[slab * 3 + 2];
        float3 eye = float3(r0.w, r1.w, r2.w);
        float3 origin, direction;
        CosmicGuideRay(place.xy, eye, origin, direction);
        float3 star = CosmicNebulaStarPosition(model, origin, direction, place.w, place.z);
        // 主役のカメラから見た、画像の面の単位面積の立体角と、主役のカメラから星までの距離（主役のカメラが近く、平行な視線で読む
        // ときは、星から主役のカメラまでの距離を画像の面までの距離と同じとみなします）。
        float3 plane = float3(place.xy, 0.0);
        float planeDistance = eye.z < -1.2 ? length(plane - eye) : max(length(eye), 1.0);
        float heroDistance = eye.z < -1.2 ? length(star - eye) : planeDistance;
        float solid = direction.z / max(planeDistance * planeDistance, 1e-9);
        // 主カメラ（圧縮の中心）の位置を画像の面の座標へ（基底は正規直交、q の 2 倍が画像の面の座標）。
        float3 centerOS = CosmicCenterObjectSpace();
        float3 center = float3(dot(r0.xyz, centerOS), dot(r1.xyz, centerOS), dot(r2.xyz, centerOS)) * 2.0;
        float distance = max(length(star - center), 1e-6);
        float pixelAngle = _CosmicMainView.x > 0.0 ? _CosmicMainView.x : 2.0 / (abs(UNITY_MATRIX_P[1][1]) * _ScaledScreenParams.y);
        float ratio = heroDistance / distance;
        float3 energy = max(flux.rgb, 0.0) * (solid * ratio * ratio / max(pixelAngle * pixelAngle, 1e-30)) * (_CosmicP0.w * fade);
        energy *= smoothstep(1.0, 0.7, length(place.xy));
        float3 positionOS = (r0.xyz * star.x + r1.xyz * star.y + r2.xyz * star.z) * 0.5;
        float4 clip = CosmicNebulaStarsClip(positionOS);
        energy *= smoothstep(COSMIC_NEBULA_STAR_FADE_PIXELS.x, COSMIC_NEBULA_STAR_FADE_PIXELS.y, CosmicNebulaStarsSpreadPixels());
        float strongest = max(energy.r, max(energy.g, energy.b));
        if (strongest >= COSMIC_NEBULA_STAR_MIN && clip.w > 1e-6)
        {
            float radius = clamp(sqrt(strongest / (COSMIC_NEBULA_STAR_KERNEL * COSMIC_NEBULA_STAR_PEAK)), _CosmicP2.x, max(_CosmicP2.y, _CosmicP2.x));
            // 四角形は点の半径より半画素広げます（画素の中心が点の縁にかかる所まで描く）。
            float extent = radius + 0.5;
            clip.xy += input.shape.xy * (2.0 * extent) / _ScaledScreenParams.xy * clip.w;
            output.positionCS = clip;
            output.depthOS = CosmicInvariantDepthPosition(positionOS);
            output.corner = input.shape.xy * (extent / radius);
            output.color = energy / (COSMIC_NEBULA_STAR_KERNEL * radius * radius);
        }
    }
    return output;
}

void CosmicNebulaStarsFragment(CosmicNebulaStarsVaryings input, out half4 outputColor : SV_Target, out float outputDepth : SV_Depth)
{
    UNITY_SETUP_INSTANCE_ID(input);
    float r2 = dot(input.corner, input.corner);
    if (r2 >= 1.0) discard;
    outputColor = half4(input.color * exp(-4.5 * r2), 0.0);
    outputDepth = CosmicInvariantFragmentDepth(input.depthOS, input.positionCS.z);
}

#endif
