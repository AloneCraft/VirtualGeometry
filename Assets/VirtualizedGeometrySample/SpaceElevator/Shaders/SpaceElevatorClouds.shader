// space_elevator.fbx の雲の層(SE_GA_CloudLayer、高度 2.6 km の 120 km 四方の板)用の半透明シェーダー。
// Blender の mat_clouds(se_ground_anchor.py:153-169: 歪ませた fbm の密度で透明と発光を混ぜる)を骨格に、参照画像の積雲へ寄せて
// 太陽側を暖色に、底を青灰色にする。半透明は VG で描けないため、この雲の部品だけ仕様どおり通常 Proxy で描かれる。
// 使用 scene: SpaceElevator.unity、Import.unity(Material: SpaceElevator/Materials/SE_GA_Clouds.mat)。
Shader "SpaceElevator/Clouds"
{
    Properties
    {
        _PatternScale("Pattern Scale (Unity mesh unit to m)", Float) = 100
        [HDR] _LitColor("Lit Color (linear)", Color) = (1.0, 0.98, 0.95, 1)
        [HDR] _ShadeColor("Shade Color (linear)", Color) = (0.72, 0.76, 0.85, 1)
        _Brightness("Brightness", Float) = 1.4
        _SunTint("Sun Tint", Range(0, 1)) = 0.5
        _Coverage("Coverage", Range(-0.3, 0.3)) = 0
        _Softness("Edge Softness", Range(0.01, 0.5)) = 0.1
        _Opacity("Opacity", Range(0, 1)) = 1
        [HDR] _HazeColor("Haze Color", Color) = (0.1719, 0.2125, 0.2688, 1)
        _HazeDistance("Haze Distance (m)", Float) = 38000
        _HazeMax("Haze Max", Range(0, 1)) = 0.85
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Clouds"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex CloudVertex
            #pragma fragment CloudFragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "SpaceElevatorNoise.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _PatternScale;
                half4 _LitColor;
                half4 _ShadeColor;
                half _Brightness;
                half _SunTint;
                half _Coverage;
                half _Softness;
                half _Opacity;
                half4 _HazeColor;
                half _HazeDistance;
                half _HazeMax;
            CBUFFER_END

            struct CloudAttributes
            {
                float4 positionOS : POSITION;
            };

            struct CloudVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 positionOS : TEXCOORD1;
            };

            CloudVaryings CloudVertex(CloudAttributes input)
            {
                CloudVaryings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.positionOS = input.positionOS.xyz;
                return output;
            }

            half4 CloudFragment(CloudVaryings input) : SV_Target
            {
                float3 p = float3(-input.positionOS.x, input.positionOS.y, input.positionOS.z) * _PatternScale;
                float footprint = length(fwidth(p)) * 0.7;
                // Blender: warp = noise(p, 0.00012)、q = p + warp * (3000,3000,0)、n = noise(q, 0.00035, detail 10)、cover = noise(p, 0.00006)
                float warpX = SEFbm(p * 0.00012, 3.0, 0.5, footprint * 0.00012);
                float warpY = SEFbm(p * 0.00012 + float3(31.4, 7.7, 0.0), 3.0, 0.5, footprint * 0.00012);
                float3 q = p + float3(warpX - 0.5, warpY - 0.5, 0.0) * 6000.0;
                float density = SEFbm(q * 0.00035, 10.0, 0.62, footprint * 0.00035);
                float cover = SEFbm(p * 0.00006, 2.0, 0.5, footprint * 0.00006);
                float threshold = SEMapRange(cover, 0.35, 0.65, 0.62, 0.5) - _Coverage;
                float dens = density - threshold;
                half alpha = SEMapRange(dens, 0.0, _Softness, 0.0, 1.0) * _Opacity;
                half shade = SEMapRange(dens, 0.0, 0.2, 1.0, 0.55);
                half3 color = lerp(_ShadeColor.rgb, _LitColor.rgb, shade) * _Brightness;
                // 太陽の色で明るい側を染める(夕方の暖色)
                Light mainLight = GetMainLight();
                color *= lerp(half3(1, 1, 1), mainLight.color / max(max(mainLight.color.r, mainLight.color.g), max(mainLight.color.b, 1e-3)), _SunTint * shade);
                float viewDistance = distance(input.positionWS, GetCameraPositionWS());
                half haze = min(1.0 - exp(-viewDistance / max(_HazeDistance, 1.0)), _HazeMax);
                color = lerp(color, _HazeColor.rgb, haze);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
