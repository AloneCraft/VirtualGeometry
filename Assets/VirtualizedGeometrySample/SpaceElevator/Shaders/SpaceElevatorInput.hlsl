// SpaceElevator/Procedural の Material property。SRP Batcher のため全 pass で同じ CBUFFER を使う。
// 色はすべて [HDR](Unity が gamma→linear 変換をしない)なので、線形値をそのまま入れる(SpaceElevatorMaterialBuilder)。
// _BaseMap / _BaseMap_ST / _Cutoff は URP の ShadowCaster / DepthOnly / DepthNormals pass が参照する名前に合わせたもの(この shader では alpha test を使わない)。
#ifndef SPACE_ELEVATOR_INPUT_INCLUDED
#define SPACE_ELEVATOR_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half4 _AccentColor;
    half4 _LightColor;
    half4 _GrimeColor;
    half4 _SlabColorA;
    half4 _SlabColorB;
    half4 _StainColor;
    half4 _PlainColorA;
    half4 _PlainColorB;
    half4 _MossColor;
    half4 _RockColorA;
    half4 _RockColorB;
    half4 _SnowColor;
    half4 _HazeColor;
    float4 _PanelSize;
    float _PatternScale;
    half _Cutoff;
    half _Variation;
    half _Metallic;
    half _Roughness;
    half _SpecularLevel;
    half _LineWidth;
    half _LineDark;
    half _Grime;
    half _Bump;
    half _AccentFraction;
    half _StreakStrength;
    half _StreakScale;
    half _RibPeriod;
    half _RibStrength;
    half _GrainStrength;
    half _TintStrength;
    half _LightFraction;
    half _LightStrength;
    half _Band;
    half _WetThreshold;
    half _WetSoftness;
    half _DryRoughness;
    half _WetRoughness;
    half _SnowLine;
    half _HazeDistance;
    half _HazeMax;
    half _PlanarStrength;
    half _PlanarDistortion;
CBUFFER_END

// 平面反射(SpaceElevatorPlanarReflection が Main Camera の描画中だけ設定する global)。Valid が 0 の camera では空の反射に戻す。
TEXTURE2D(_SpaceElevatorPlanarReflection);
SAMPLER(sampler_SpaceElevatorPlanarReflection);
float _SpaceElevatorPlanarReflectionValid;
float _SpaceElevatorPlanarReflectionMips;

#endif
