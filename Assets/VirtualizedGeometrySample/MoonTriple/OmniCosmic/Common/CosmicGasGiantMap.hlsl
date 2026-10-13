// ガス惑星（木星・海王星）の表面の画像（NASA の画像から作った正距円筒図法）。CosmicGasGiantAtmosphere.hlsl が、帯の流れで動かした方向でこの画像を引き、
// 手続きの色の代わりに土台にします（Windows。2026-10-08 のユーザーの指示）。画像が無い（WebGPU のビルド・Windows でも資産が無いとき）は
// _CosmicGasMapMix が 0 で、従来の手続きの描き方のままです。画像は CosmicGasGiantMaps.cs が global として渡します（材質ではなく global）。
// 木星は style 0、海王星は style 1。土星（style 2）は画像を持ちません。
#ifndef COSMIC_GAS_GIANT_MAP_INCLUDED
#define COSMIC_GAS_GIANT_MAP_INCLUDED

TEXTURE2D(_CosmicGasMapJupiter);
SAMPLER(sampler_CosmicGasMapJupiter);
TEXTURE2D(_CosmicGasMapNeptune);
SAMPLER(sampler_CosmicGasMapNeptune);
// x = 木星、y = 海王星の画像を土台にする量（1 = 画像、0 = 手続きだけ）。
float4 _CosmicGasMapMix;

float CosmicGasMapAmount(uint style)
{
    return style == 0u ? _CosmicGasMapMix.x : (style == 1u ? _CosmicGasMapMix.y : 0.0);
}

// 物体空間の方向 q の正距円筒の色（u = (atan2(z, x) + π) / 2π、v = asin(y) / π + 0.5。CosmicScale の球のメッシュと同じ）。
// 導関数を使わず（経度の継ぎ目で u が跳んでもミップが粗くならない）、画素の大きさ footprint（半径 = 1 の球の上の長さ）からミップを選ぶ。
float3 CosmicGasMapColor(float3 q, uint style, float footprint)
{
    float u = (atan2(q.z, q.x) + 3.14159265) / 6.28318531;
    float v = asin(clamp(q.y, -1.0, 1.0)) / 3.14159265 + 0.5;
    float2 uv = float2(u, v);
    if (style == 1u)
    {
        // 1024×512: 1 画素 ≒ π/512 rad。
        float lod = max(0.0, log2(max(footprint, 1e-7) * 512.0 / 3.14159265));
        return SAMPLE_TEXTURE2D_LOD(_CosmicGasMapNeptune, sampler_CosmicGasMapNeptune, uv, lod).rgb;
    }
    // 4096×2048: 1 画素 ≒ π/2048 rad。
    float lod = max(0.0, log2(max(footprint, 1e-7) * 2048.0 / 3.14159265));
    return SAMPLE_TEXTURE2D_LOD(_CosmicGasMapJupiter, sampler_CosmicGasMapJupiter, uv, lod).rgb;
}

#endif
