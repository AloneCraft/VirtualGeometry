// SpaceElevator/Procedural の質感レシピ。Blender の生成スクリプト(se_common.py の panel_pattern / mat_panel / mat_steel、
// se_ground_anchor.py の mat_wet_ground / mat_terrain / mat_water)を骨格に、参照画像「地上が、宇宙へのはじまり。」へ寄せる要素
// (雨だれ汚れ・縦リブ・コンクリートの粒・暖色と寒色のムラ・濡れた路面)を足したもの。
// 座標はすべて Blender のオブジェクト座標(m、Z上)。footprint は 1 画素に当たる長さ(m)で、細かい模様を遠景で弱めるのに使う。
#ifndef SPACE_ELEVATOR_RECIPES_INCLUDED
#define SPACE_ELEVATOR_RECIPES_INCLUDED

#include "SpaceElevatorNoise.hlsl"

struct SESurface
{
    half3 albedo;
    half metallic;
    half roughness;
    half specularLevel;   // Blender の Specular IOR Level(0.5 で F0 = 0.04)
    half3 emission;
    float height;         // 凹凸の高さ(m)。法線は Cycles の Bump と同じく画面微分で求める
    half bumpStrength;    // Blender の Bump Strength(元の法線との混合率)
    half wetness;         // 平面反射の強さ(濡れた路面・水面だけ 0 以上)
    half occlusion;
};

SESurface SEDefaultSurface()
{
    SESurface s;
    s.albedo = half3(0.5, 0.5, 0.5);
    s.metallic = 0;
    s.roughness = 0.5;
    s.specularLevel = 0.5;
    s.emission = 0;
    s.height = 0;
    s.bumpStrength = 0;
    s.wetness = 0;
    s.occlusion = 1;
    return s;
}

// 目地の線(0..1)。seamDistance は最も近い目地までの距離(m)。画素より細い線は太らせて濃さを下げ、平均の暗さを保つ。
float SELine(float seamDistance, float width, float footprint)
{
    float inner = width * 0.35;
    float seam = SEMapRange(seamDistance, inner - footprint * 0.5, width + footprint * 0.5, 1.0, 0.0);
    return seam * saturate(width / max(width + footprint, 1e-5));
}

// Blender の panel_pattern。面の法線方向の軸には目地を引かない。cell は目地で区切られたパネルの番号。
float SEPanelLines(float3 p, float3 normal, float3 size, float width, float footprint, out float3 cell)
{
    float3 q = p / size;
    float3 axisWeight = float3(
        SEMapRange(abs(normal.x), 0.35, 0.65, 1.0, 0.0),
        SEMapRange(abs(normal.y), 0.35, 0.65, 1.0, 0.0),
        SEMapRange(abs(normal.z), 0.35, 0.65, 1.0, 0.0));
    float lx = SELine(SEPingPong(q.x, 0.5) * size.x, width, footprint) * axisWeight.x;
    float ly = SELine(SEPingPong(q.y, 0.5) * size.y, width, footprint) * axisWeight.y;
    float lz = SELine(SEPingPong(q.z, 0.5) * size.z, width, footprint) * axisWeight.z;
    cell = floor(q);
    return max(max(lx, ly), lz);
}

// 塗装パネル・コンクリートパネル(Panel, PanelAlt, PanelDark, Concrete, ConcreteDark, ContainerA/B)。
SESurface SEPanelRecipe(float3 pB, float3 nB, float footprint)
{
    SESurface s = SEDefaultSurface();
    float3 size = max(_PanelSize.xyz, 0.01);
    float3 p = pB + float3(0.37, 0.61, 0.13) * size;
    float3 an = abs(nB);
    float3 cell;
    float seam = SEPanelLines(p, nB, size, _LineWidth, footprint, cell);
    float r1 = SEHashCell(cell);
    float r2 = SEHashCell(cell + float3(17.0, 5.0, 9.0));
    float r3 = SEHashCell(cell + float3(3.0, 41.0, 23.0));

    // パネルごとの色ムラと、一部の差し色パネル
    half3 baseColor = _BaseColor.rgb;
    half3 color = lerp(baseColor * (1.0 - _Variation), min(baseColor * (1.0 + _Variation), 1.0), r1);
    if (r2 > 1.0 - _AccentFraction)
    {
        color = _AccentColor.rgb * (0.92 + 0.16 * r3);
    }
    // 広い範囲の暖色・寒色のムラ(参照画像の日焼け・汚れの色味)
    float tint = SEFbm(p * 0.015, 2.0, 0.5, footprint * 0.015) - 0.5;
    color *= 1.0 + half3(1.0, 0.25, -0.6) * tint * _TintStrength;

    // 汚れ: Blender の大きな斑(縦に伸ばした fbm)と、パネル上端から垂れる雨だれ
    float3 grimeCoord = p * float3(1.0, 1.0, 0.18);
    float g1 = SEFbm(grimeCoord * 0.09, 6.0, 0.6, footprint * 0.09);
    float g2 = SEFbm(p * 0.6, 3.0, 0.5, footprint * 0.6);
    float grime = SEMapRange(g1, 0.42, 0.78, 0.0, _Grime) * SEMapRange(g2, 0.3, 0.7, 0.6, 1.2);
    float vertical = 1.0 - an.z;
    float top = frac(p.z / size.z);
    float streakNoise = SEFbm(float3(p.x * _StreakScale, p.y * _StreakScale, p.z * 0.05), 3.0, 0.55, footprint * _StreakScale);
    float streak = saturate((streakNoise - 0.48) * 4.0) * pow(top, 1.6) * vertical;
    grime = saturate(grime + streak * _StreakStrength);
    color = lerp(color, color * _GrimeColor.rgb, grime);

    // コンクリートの粒・塗装の細かいムラ
    float grain = SEFbm(p * 4.0, 2.0, 0.5, footprint * 4.0) - 0.5;
    color *= 1.0 + grain * _GrainStrength;

    // 縦リブ: 鉛直な面に、水平方向の周期で浅い溝を刻む(参照画像の縦筋)
    float horizontal = an.x > an.y ? p.y : p.x;
    float ribPhase = SEPingPong(horizontal / max(_RibPeriod, 0.05), 0.5) * 2.0;
    float ribFade = saturate(1.0 - footprint / max(_RibPeriod * 0.5, 1e-4));
    float rib = smoothstep(0.55, 0.9, ribPhase) * vertical * ribFade;
    color *= 1.0 - rib * 0.25 * saturate(_RibStrength * 25.0);

    // 目地の暗さ
    color = lerp(color, half3(0.03, 0.03, 0.03), seam * _LineDark);
    s.albedo = saturate(color);
    s.metallic = _Metallic;
    s.roughness = saturate(_Roughness + (g2 - 0.5) * 0.25 + seam * 0.25 + grime * 0.15);
    s.specularLevel = _SpecularLevel;
    s.occlusion = 1.0 - seam * 0.5 - rib * 0.3;
    s.height = (1.0 - seam) * 0.03 - rib * _RibStrength + grain * 0.002 * _GrainStrength;
    s.bumpStrength = _Bump;

    // 点灯窓: 一部のパネルの中央に小さな光る窓(鉛直面だけ)。画素より小さくなったら面積の平均に置き換える。
    if (_LightFraction > 0.0)
    {
        float3 fr = frac(p / size);
        float inZ = step(0.42, fr.z) * step(fr.z, 0.58);
        float inX = step(0.3, fr.x) * step(fr.x, 0.7);
        float inY = step(0.3, fr.y) * step(fr.y, 0.7);
        float side = an.x > an.y ? inY : inX;
        float pick = step(1.0 - _LightFraction, r2);
        float lit = inZ * side * step(an.z, 0.5) * pick;
        float coverage = saturate(footprint / max(size.z * 0.16, 1e-4));
        lit = lerp(lit, 0.064 * pick * step(an.z, 0.5), coverage);
        s.emission = _LightColor.rgb * lit * _LightStrength;
    }
    return s;
}

// テザー・手すりの鋼材(Blender の mat_steel)。band ごとの継ぎ目と、縦に流れるヘアライン。
SESurface SESteelRecipe(float3 pB, float3 nB, float footprint)
{
    SESurface s = SEDefaultSurface();
    float band = max(_Band, 0.1);
    float d = SEPingPong(pB.z / band, 0.5) * band;
    float joint = SEMapRange(d, 0.05 - footprint * 0.5, 0.15 + footprint * 0.5, 1.0, 0.0);
    float streak = SEFbm(pB * float3(6.0, 6.0, 0.05), 8.0, 0.5, footprint * 6.0);
    half3 baseColor = _BaseColor.rgb;
    half3 color = lerp(baseColor * 0.85, baseColor, SEMapRange(streak, 0.3, 0.7, 0.0, 1.0));
    color = lerp(color, half3(0.05, 0.05, 0.05), joint * 0.7);
    s.albedo = color;
    s.metallic = 1.0;
    s.roughness = saturate(_Roughness + streak * 0.12);
    s.specularLevel = _SpecularLevel;
    s.height = (1.0 - joint) * 0.05;
    s.bumpStrength = _Bump;
    s.occlusion = 1.0 - joint * 0.4;
    return s;
}

// 濡れた広場(Blender の mat_wet_ground)。9 m 角の版、染み、タイヤ痕、水たまり。水たまりほど滑らかで暗く、平面反射が強い。
SESurface SEWetGroundRecipe(float3 pB, float3 nB, float footprint)
{
    SESurface s = SEDefaultSurface();
    float3 p = pB + float3(3.1, 4.7, 0.3);
    float3 cell;
    float seam = SEPanelLines(p, nB, float3(9.0, 9.0, 9.0), _LineWidth, footprint, cell);
    float r1 = SEHashCell(cell);
    half3 color = lerp(_SlabColorA.rgb, _SlabColorB.rgb, r1);
    float stain = SEFbm(p * 0.04, 6.0, 0.6, footprint * 0.04);
    color = lerp(color, _StainColor.rgb, SEMapRange(stain, 0.45, 0.8, 0.0, 0.35));
    float tyre = SEFbm(p * float3(1.0, 0.03, 1.0) * 0.9, 2.0, 0.5, footprint * 0.9);
    color = lerp(color, half3(0.08, 0.08, 0.08), SEMapRange(tyre, 0.55, 0.75, 0.0, 0.35));
    float puddle = SEFbm(p * 0.03, 5.0, 0.55, footprint * 0.03);
    float wet = SEMapRange(puddle, _WetThreshold, _WetThreshold + _WetSoftness, 0.0, 1.0);
    color = lerp(color, color * 0.45, wet * 0.6);
    color = lerp(color, half3(0.03, 0.03, 0.03), seam * _LineDark);
    float grain = SEFbm(p * 3.0, 2.0, 0.5, footprint * 3.0) - 0.5;
    color *= 1.0 + grain * _GrainStrength;
    s.albedo = color;
    s.metallic = 0;
    s.roughness = saturate(lerp(_DryRoughness, _WetRoughness, wet) + seam * 0.3);
    s.specularLevel = _SpecularLevel;
    s.height = (1.0 - seam) * 0.02 + grain * 0.004 * (1.0 - wet);
    s.bumpStrength = _Bump * (1.0 - wet * 0.8);
    s.wetness = lerp(0.35, 1.0, wet);
    s.occlusion = 1.0 - seam * 0.4;
    return s;
}

// 山岳地形(Blender の mat_terrain)。高さと斜面で草地・岩・雪を塗り分ける。nB は表裏をそろえた法線(下向きの面も上を向く)。
SESurface SETerrainRecipe(float3 pB, float3 nB, float footprint)
{
    SESurface s = SEDefaultSurface();
    float z = pB.z;
    float nz = nB.z;
    float n1 = SEFbm(pB * 0.0012, 8.0, 0.6, footprint * 0.0012);
    float n2 = SEFbm(pB * 0.02, 6.0, 0.7, footprint * 0.02);
    half3 plain = lerp(_PlainColorA.rgb, _PlainColorB.rgb, n1);
    plain = lerp(plain, _MossColor.rgb, SEMapRange(n2, 0.3, 0.7, 0.0, 0.5));
    half3 rock = lerp(_RockColorA.rgb, _RockColorB.rgb, n2);
    float rockiness = saturate(SEMapRange(nz, 0.96, 0.82, 0.0, 1.0) + SEMapRange(z, 40.0, 350.0, 0.0, 0.7));
    half3 color = lerp(plain, rock, rockiness);
    float snowline = z + (n1 - 0.5) * 900.0;
    float snow = SEMapRange(snowline, _SnowLine, _SnowLine + 350.0, 0.0, 1.0) * SEMapRange(nz, 0.55, 0.78, 0.0, 1.0);
    color = lerp(color, _SnowColor.rgb, snow);
    float detail = SERidged(pB * 0.03, 10.0, 0.65, footprint * 0.03);
    s.albedo = color;
    s.metallic = 0;
    s.roughness = lerp(0.9, 0.6, snow);
    s.specularLevel = _SpecularLevel;
    s.height = detail * 30.0;
    s.bumpStrength = _Bump;
    return s;
}

// 湖面(Blender の mat_water)。細かいさざ波の凹凸と強い平面反射。
SESurface SEWaterRecipe(float3 pB, float3 nB, float footprint)
{
    SESurface s = SEDefaultSurface();
    float ripple = SEFbm(pB * float3(1.0, 3.0, 1.0) * 0.05, 4.0, 0.5, footprint * 0.05);
    s.albedo = _BaseColor.rgb;
    s.metallic = 0;
    s.roughness = _Roughness;
    s.specularLevel = _SpecularLevel;
    s.height = ripple * 0.5;
    s.bumpStrength = _Bump;
    s.wetness = 1.0;
    return s;
}

#endif
