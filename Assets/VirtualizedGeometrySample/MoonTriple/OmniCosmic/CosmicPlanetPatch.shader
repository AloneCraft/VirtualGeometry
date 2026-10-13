// CosmicScale の冒頭の月を、分割した地面（PlanetSurface の PlanetSurfaceLod のパッチ）で描くシェーダー（CosmicPlanetSurface）。
// 表面の見た目は CosmicPlanetLit（Common/CosmicPlanetSurface.hlsl）と同じです。違いは、物体空間がパッチの中心からの差（float の桁が
// 落ちない）で、天体の局所の方向を頂点の法線（標高 0 の球では方向そのもの）から、UV（CosmicScale の球と同じ規約）を画素ごとに
// 求めることです（Common/CosmicOpaquePass.hlsl の COSMIC_SURFACE_FROM_NORMAL）。keyword は使いません。
Shader "Omni/CosmicScale/CosmicPlanetPatch"
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
            #define COSMIC_SURFACE_FROM_NORMAL

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
