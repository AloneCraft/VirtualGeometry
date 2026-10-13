// 月と地球の表面（CosmicPlanetLit の ForwardLit）。画像は既存の共有資産（EarthToMoon の地球、PlanetSurface の LROC 月）を参照します。
// 照明は演出用の主光源（_CosmicKeyLight、URP のライトは使いません）。物理的な太陽の位置による照明ではありません（比較の並びのための演出）。
// _CosmicBody.y: 0 = 月（月面の拡散: Lommel-Seeliger と Lambert の混合）、1 = 地球（昼の画像、雲、街明かり、海の照り返し、大気の縁）、
// 2 = 冒頭のサッカーボール（CosmicSoccerBallSurface.hlsl、画像を使わない計算の模様）。
// _CosmicP0: x = 自転の表示周期（秒、0 で静止）、y = 雲の追加の流れ（周/分）、z = 雲の不透明度、w = 街明かりの強さ。
#ifndef COSMIC_PLANET_SURFACE_INCLUDED
#define COSMIC_PLANET_SURFACE_INCLUDED

TEXTURE2D(_BaseMap);  SAMPLER(sampler_BaseMap);
TEXTURE2D(_NightMap); SAMPLER(sampler_NightMap);
TEXTURE2D(_CloudMap); SAMPLER(sampler_CloudMap);
TEXTURE2D(_LandMask); SAMPLER(sampler_LandMask);

#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicSoccerBallSurface.hlsl"

float3 CosmicSurfaceColor(CosmicSurface s)
{
    uint kind = (uint)(_CosmicBody.y + 0.5);
    if (kind == 2) { return CosmicSoccerBallColor(s); }
    float t = CosmicSeconds();
    float spin = _CosmicP0.x > 0 ? frac(t / _CosmicP0.x) : 0;
    float2 uv = float2(s.uv.x - spin, s.uv.y);
    float3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb * _BaseColor.rgb;
    float3 lightDir = normalize(_CosmicKeyLight.xyz);
    float intensity = _CosmicKeyLight.w;
    float mu0 = dot(s.normalWS, lightDir);
    float mu = saturate(dot(s.normalWS, s.viewWS));
    float3 ambient = float3(0.012, 0.013, 0.016);
    if (kind == 0)
    {
        // 月面: 満月で縁が暗くならない拡散（Lommel-Seeliger）を Lambert と混ぜます。
        float ls = mu0 > 0 ? 2.0 * mu0 / (mu0 + mu + 1e-4) : 0;
        float diffuse = lerp(saturate(mu0), ls * 0.5, 0.6);
        return albedo * (intensity * diffuse + ambient) * 1.35;
    }
    float land = SAMPLE_TEXTURE2D(_LandMask, sampler_LandMask, uv).r;
    float2 cloudUv = float2(uv.x - frac(t * _CosmicP0.y / 60.0), uv.y);
    float cloud = saturate(SAMPLE_TEXTURE2D(_CloudMap, sampler_CloudMap, cloudUv).r * _CosmicP0.z);
    // 霞（大気のレイリー散乱の光路輝度）を screen 合成で足します。
    float3 haze = float3(0.02, 0.045, 0.12);
    float3 surface = 1.0 - (1.0 - albedo) * (1.0 - haze);
    surface = lerp(surface, float3(0.92, 0.93, 0.95), cloud);
    float water = saturate(1.0 - land) * (1.0 - cloud);
    float3 halfVector = normalize(lightDir + s.viewWS);
    float glint = min(water * 0.02 * 202.0 / (2.0 * COSMIC_PI) * pow(saturate(dot(s.normalWS, halfVector)), 200.0) * saturate(mu0), 0.6);
    float night = smoothstep(0.08, -0.12, mu0) * (1.0 - cloud * 0.8);
    float3 lights = SAMPLE_TEXTURE2D(_NightMap, sampler_NightMap, uv).rgb * _CosmicP0.w * night;
    float rim = pow(1.0 - mu, 4.0) * saturate(mu0 + 0.15);
    return surface * (intensity * saturate(mu0) + ambient) + intensity * glint + lights + float3(0.10, 0.24, 0.60) * (intensity * rim * 0.35);
}

#endif
