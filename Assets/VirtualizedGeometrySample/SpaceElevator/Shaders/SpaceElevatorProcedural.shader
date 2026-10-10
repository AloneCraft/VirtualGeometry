// space_elevator.fbx 用の手続き質感(UV 不要、Blender のオブジェクト座標で計算)。参照画像「地上が、宇宙へのはじまり。」に寄せた
// パネル・鋼材・濡れた広場のレシピを _Recipe で切り替える。
// VG で描くため、同じフォルダの SpaceElevatorProcedural.vgshader が VirtualGeometry/SpaceElevator/Procedural を生成し、Material はそちらを使う
// (SpaceElevatorMaterialBuilder が SpaceElevator/Materials/SE_GA_*.mat を作る。使用 scene: SpaceElevator.unity、Import.unity)。
Shader "SpaceElevator/Procedural"
{
    Properties
    {
        [KeywordEnum(Panel, Steel, WetGround)] _Recipe("Recipe", Float) = 0
        _PatternScale("Pattern Scale (Unity mesh unit to m)", Float) = 100

        [Header(Surface)]
        [HDR] _BaseColor("Base Color (linear)", Color) = (0.6, 0.6, 0.585, 1)
        _Variation("Panel Color Variation", Range(0, 0.5)) = 0.07
        _Metallic("Metallic", Range(0, 1)) = 0.2
        _Roughness("Roughness", Range(0, 1)) = 0.45
        _SpecularLevel("Specular IOR Level", Range(0, 2)) = 0.5
        _Bump("Bump Strength", Range(0, 1)) = 0.25

        [Header(Panels)]
        _PanelSize("Panel Size (m)", Vector) = (4, 4, 3, 0)
        _LineWidth("Seam Width (m)", Range(0, 0.3)) = 0.06
        _LineDark("Seam Darkness", Range(0, 1)) = 0.5
        [HDR] _AccentColor("Accent Color (linear)", Color) = (0.47, 0.48, 0.49, 1)
        _AccentFraction("Accent Fraction", Range(0, 1)) = 0
        _TintStrength("Warm/Cool Tint", Range(0, 1)) = 0.15
        _GrainStrength("Grain", Range(0, 1)) = 0.15
        _RibPeriod("Rib Period (m)", Range(0.1, 5)) = 0.9
        _RibStrength("Rib Depth (m)", Range(0, 0.1)) = 0.02

        [Header(Weathering)]
        _Grime("Grime", Range(0, 1)) = 0.25
        [HDR] _GrimeColor("Grime Multiplier", Color) = (0.45, 0.45, 0.45, 1)
        _StreakStrength("Rain Streaks", Range(0, 1)) = 0.3
        _StreakScale("Streak Frequency (1/m)", Range(0.1, 10)) = 2.5

        [Header(Lit Windows)]
        [HDR] _LightColor("Window Light (linear)", Color) = (1, 0.6, 0.28, 1)
        _LightFraction("Lit Panel Fraction", Range(0, 0.3)) = 0
        _LightStrength("Window Light Strength", Float) = 10

        [Header(Steel)]
        _Band("Joint Band Interval (m)", Float) = 8

        [Header(Wet Ground)]
        [HDR] _SlabColorA("Slab Color A", Color) = (0.25, 0.25, 0.245, 1)
        [HDR] _SlabColorB("Slab Color B", Color) = (0.33, 0.33, 0.32, 1)
        [HDR] _StainColor("Stain Color", Color) = (0.14, 0.14, 0.135, 1)
        _WetThreshold("Puddle Threshold", Range(0, 1)) = 0.44
        _WetSoftness("Puddle Softness", Range(0.001, 0.5)) = 0.08
        _DryRoughness("Dry Roughness", Range(0, 1)) = 0.22
        _WetRoughness("Wet Roughness", Range(0, 1)) = 0.015
        _PlanarStrength("Planar Reflection", Range(0, 1)) = 1
        _PlanarDistortion("Reflection Distortion", Range(0, 0.5)) = 0.05

        [HideInInspector] _BaseMap("Base Map", 2D) = "white" {}
        [HideInInspector] _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "SpaceElevatorInput.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex SpaceElevatorVertex
            #pragma fragment SpaceElevatorFragment
            #pragma shader_feature_local_fragment _RECIPE_PANEL _RECIPE_STEEL _RECIPE_WETGROUND
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include "SpaceElevatorForwardPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull Back
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }
}
