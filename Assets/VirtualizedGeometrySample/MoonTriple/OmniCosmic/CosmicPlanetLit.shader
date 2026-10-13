// CosmicScale の月と地球。Radial(対数圧縮)に対応した不透明シェーダーで、keyword は使いません。
// 表面は Common/CosmicPlanetSurface.hlsl、頂点と深度は Common/CosmicOpaquePass.hlsl（全パスで UnityPerMaterial を同一にし、頂点関数は1引数）。
Shader "Omni/CosmicScale/CosmicPlanetLit"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)
        // x = 梯子の段の番号（global 配列の添字）、y = 種類・形、z = シード。
        _CosmicBody("Body (index, kind, seed, -)", Vector) = (0, 0, 0, 0)
        _CosmicP0("Parameters 0", Vector) = (0, 0, 0, 0)
        _CosmicP1("Parameters 1", Vector) = (0, 0, 0, 0)
        _CosmicP2("Parameters 2", Vector) = (0, 0, 0, 0)
        _CosmicP3("Parameters 3", Vector) = (0, 0, 0, 0)
        _NightMap("Night Lights (Earth)", 2D) = "black" {}
        _CloudMap("Clouds (Earth)", 2D) = "black" {}
        _LandMask("Land Mask (Earth)", 2D) = "black" {}
        // Radial ワープ用プロパティ(MaterialPropertyBlock で上書きされる)。宣言が無いと上書きが静かに無視されます。
        [HideInInspector] _RadialGlobal_RadialScale("_RadialGlobal_RadialScale", Float) = 1
        [HideInInspector] _RadialGlobal_Mode("_RadialGlobal_Mode", Float) = 2
        [HideInInspector] _RadialGlobal_LinearRadius("_RadialGlobal_LinearRadius", Float) = 10
        [HideInInspector] _RadialGlobal_Log2ViewScale("_RadialGlobal_Log2ViewScale", Float) = 0
        [HideInInspector] _RadialGlobal_EyeRelative("_RadialGlobal_EyeRelative", Vector) = (0, 0, 0, 0)
        [HideInInspector] _RadialGlobal_EyeRelativeExponent("_RadialGlobal_EyeRelativeExponent", Float) = 0
        [HideInInspector] _RadialGlobal_ViewRow0("_RadialGlobal_ViewRow0", Vector) = (1, 0, 0, 0)
        [HideInInspector] _RadialGlobal_ViewRow1("_RadialGlobal_ViewRow1", Vector) = (0, 1, 0, 0)
        [HideInInspector] _RadialGlobal_ViewRow2("_RadialGlobal_ViewRow2", Vector) = (0, 0, -1, 0)
        [HideInInspector] _RadialGlobal_UseRadialTransform("_RadialGlobal_UseRadialTransform", Float) = 0
        [HideInInspector] _RadialGlobal_RadialTransformRelative("_RadialGlobal_RadialTransformRelative", Vector) = (0, 0, 0, 0)
        [HideInInspector] _RadialGlobal_RadialTransformExponents("_RadialGlobal_RadialTransformExponents", Vector) = (0, 0, 0, 0)
        [HideInInspector] _RadialGlobal_RadialTransformBasisRow0("_RadialGlobal_RadialTransformBasisRow0", Vector) = (1, 0, 0, 0)
        [HideInInspector] _RadialGlobal_RadialTransformBasisRow1("_RadialGlobal_RadialTransformBasisRow1", Vector) = (0, 1, 0, 0)
        [HideInInspector] _RadialGlobal_RadialTransformBasisRow2("_RadialGlobal_RadialTransformBasisRow2", Vector) = (0, 0, 1, 0)
        [HideInInspector] _RadialGlobal_RadialTransformNormalRow0("_RadialGlobal_RadialTransformNormalRow0", Vector) = (1, 0, 0, 0)
        [HideInInspector] _RadialGlobal_RadialTransformNormalRow1("_RadialGlobal_RadialTransformNormalRow1", Vector) = (0, 1, 0, 0)
        [HideInInspector] _RadialGlobal_RadialTransformNormalRow2("_RadialGlobal_RadialTransformNormalRow2", Vector) = (0, 0, 1, 0)
        [HideInInspector] _RadialGlobal_UseRigidAnchor("_RadialGlobal_UseRigidAnchor", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "UniversalMaterialType" = "Lit"
            "OmniRadial" = "True"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Cull Back
            ZTest LEqual
            ZWrite On

            HLSLPROGRAM
            #pragma vertex CosmicOpaqueVertex
            #pragma fragment CosmicOpaqueFragment
            #pragma target 4.5
            #define COSMIC_PASS_FORWARD

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.omni.rendering/Runtime/Sharders/RadialInject.hlsl"
            #include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicDepthInvariance.hlsl"
            #include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicCommon.hlsl"
            #include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicPlanetSurface.hlsl"
            #include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicOpaquePass.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex CosmicOpaqueVertex
            #pragma fragment CosmicOpaqueFragment
            #pragma target 4.5
            #define COSMIC_PASS_DEPTH_ONLY

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.omni.rendering/Runtime/Sharders/RadialInject.hlsl"
            #include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicDepthInvariance.hlsl"
            #include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicCommon.hlsl"
            #include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicOpaquePass.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormalsOnly" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex CosmicOpaqueVertex
            #pragma fragment CosmicOpaqueFragment
            #pragma target 4.5
            #define COSMIC_PASS_DEPTH_NORMALS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.omni.rendering/Runtime/Sharders/RadialInject.hlsl"
            #include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicDepthInvariance.hlsl"
            #include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicCommon.hlsl"
            #include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicOpaquePass.hlsl"
            ENDHLSL
        }

        // ── Scene View 選択(Picking / SceneSelectionPass) ── 製品の RadialPickingPass.hlsl(可視描画と同じ camera-relative Radial 投影)。
        Pass
        {
            Name "ScenePickingPass"
            Tags { "LightMode" = "Picking" }
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex OmniPickingVert
            #pragma fragment OmniPickingPickFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.omni.rendering/Runtime/Sharders/RadialInject.hlsl"
            #include "Packages/com.omni.rendering/Runtime/Sharders/RadialPickingPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "SceneSelectionPass"
            Tags { "LightMode" = "SceneSelectionPass" }
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex OmniPickingVert
            #pragma fragment OmniPickingSelectionFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.omni.rendering/Runtime/Sharders/RadialInject.hlsl"
            #include "Packages/com.omni.rendering/Runtime/Sharders/RadialPickingPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
