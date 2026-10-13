// ガス惑星の大気（CosmicGasGiant の ForwardLit、PlanetAtmosphere）。画像を使わず、球面上の3次元ノイズで帯と雲を作ります。
// ・惑星本体の自転: 標本化点を極軸まわりに一定の速さで回します（_CosmicP0.x = 表示上の自転周期）。
// ・帯ごとに違う速さの流れ（BandFlow）: 緯度ごとの風速 w(φ) で、さらに極軸まわりに回します。本体の自転とは別の相対的な流れです。
//   無限にせん断が溜まらないよう、2位相の周期リセット（CosmicFlowPhase）でクロスフェードします。
// ・帯の境界の乱流: ドメインワープの強さを風速の緯度微分（せん断）に比例させ、境界でだけ渦を巻かせます。
// ・巨大渦（GiantVortex）: 局所の回転軸まわりに、中心ほど速い角速度で標本化点を回す楕円の渦。帯とは別の速さで経度方向に漂流します。
// ・小渦: シードで決まる白い楕円の渦。寿命（出現→成熟→消滅）を持ち、消えるたびに別の場所へ現れます。
// 実際の木星では帯の相対的な流れは自転の約1%で目に見えないため、演出として 5〜10% に強めています（設計書「時間スケール」）。
// _CosmicBody.y: 0 = 木星、1 = 海王星、2 = 土星（淡い黄土色の帯、目立つ渦は無し）。_CosmicP0: x = 自転の表示周期（秒）、y = 流れの強さ、z = リセットの周期（秒）、w = 巨大渦の内部回転の周期（秒）。
#ifndef COSMIC_GAS_GIANT_SURFACE_INCLUDED
#define COSMIC_GAS_GIANT_SURFACE_INCLUDED

#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicGasGiantAtmosphere.hlsl"

float3 CosmicSurfaceColor(CosmicSurface s)
{
    uint style = (uint)(_CosmicBody.y + 0.5);
    float seed = _CosmicBody.z;
    float t = CosmicSeconds();
    // 本体の自転（表示上の周期）。
    float spin = _CosmicP0.x > 0 ? t / _CosmicP0.x * 6.2831853 : 0;
    float3 q = CosmicRotateY(normalize(s.unitPosition), -spin);
    float tint;
    float3 albedo = CosmicGasGiantAtmosphere(q, t, s.footprint, style, seed, _CosmicP0, tint);
    float3 lightDir = normalize(_CosmicKeyLight.xyz);
    float mu0 = dot(s.normalWS, lightDir);
    float mu = saturate(dot(s.normalWS, s.viewWS));
    // 大気の周縁減光と、明暗境界のなだらかさ（厚い大気）。
    float limb = 0.55 + 0.45 * pow(mu, 0.35);
    float diffuse = smoothstep(-0.08, 0.25, mu0) * saturate(mu0 * 0.85 + 0.15);
    float3 haze = (style == 1 ? float3(0.10, 0.20, 0.45) : style == 2 ? float3(0.28, 0.24, 0.16) : float3(0.25, 0.22, 0.18)) * pow(1.0 - mu, 3.0) * saturate(mu0 + 0.2);
    return albedo * (diffuse * limb * _CosmicKeyLight.w + 0.012) + haze * _CosmicKeyLight.w * 0.4;
}

#endif
