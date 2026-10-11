// 惑星の海の shader(Omni の Radial 経路)。RadialGlobalLit の forward pass を元に、fragment だけを海にしたもの。
// .vgshader(RadialOcean.vgshader)が、この forward pass を VG 用 pass(頂点入力を VG の buffer にしたもの)へ複製する。
// 頂点は PlanetOceanGenerate.compute の出力: position は Cell の原点からの相対位置、uv.x は水深(m、地形の高さの符号を変えたもの)。
// fragment がすること:
//   1. 海岸線: 水深が 0 以下(陸)の画素は描かない。海岸線は地形の高さの Field の 0 の等高線(深度の交わりに頼らない)。
//   2. 法線: 帯域制限で頂点の変位から落とした波のうち、波長が画素の大きさの 2 倍以上のものを、画素ごとの傾きとして足す。
//   3. 粗さ: 画素より短い波は、傾きの分散を粗さに足す(波長が画素の 1〜2 倍の間は法線から粗さへなめらかに移す)。
//   4. 色と泡: 水深で浅瀬の色と深い所の色を混ぜ、水深が浅い所に波模様の泡を足す。
// 深度は Omni の対数描画の関数で書く。
Shader "Omni/RadialOcean"
{
    Properties
    {
        _DeepColor("Deep Color", Color) = (0.02, 0.12, 0.3, 1)
        _ShallowColor("Shallow Color", Color) = (0.1, 0.5, 0.55, 1)
        // 水深(m)に対する色の減衰。浅瀬の色の割合は exp(−水深·Absorption)。
        _Absorption("Absorption (1/m)", Float) = 0.15
        _Smoothness("Smoothness", Range(0, 1)) = 0.9
        _FoamColor("Foam Color", Color) = (1, 1, 1, 1)
        // 泡が出る水深(m)。0 なら泡なし。
        _FoamDepth("Foam Depth (m)", Float) = 3
        // 1 なら、照明も泡もなしで _DeepColor だけを出す(海岸線や LOD の検証用)。
        _FlatColor("Flat Color (debug)", Float) = 0
        // Radial ワープ用プロパティ(MaterialPropertyBlock で上書きされる。宣言が無いと上書きが黙って無視される)。
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
            #pragma vertex LitPassVertex
            #pragma fragment LitPassFragment
            #pragma target 4.5

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.omni.rendering/Runtime/Sharders/RadialInject.hlsl"
            #include "Packages/com.omni.rendering/Runtime/Sharders/RadialVertexHooks.hlsl"
            #include "Packages/com.virtualgeometry.world/Runtime/Resources/VirtualGeometryWorld/PlanetOcean.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _DeepColor;
                float4 _ShallowColor;
                float  _Absorption;
                float  _Smoothness;
                float4 _FoamColor;
                float  _FoamDepth;
                float  _FlatColor;
            CBUFFER_END

            // Cell ごとの値(MaterialPropertyBlock、PlanetLayerSource.WriteCellProperties)。
            // _PlanetCellUp: xyz=Cell の原点の外向きの単位方向(object 空間)、w=Cell の原点の惑星の中心からの距離(海の Cell は持ち上げないので半径)。_PlanetCellQuad: x=quad の弧長。
            float4 _PlanetCellUp;
            float4 _PlanetCellQuad;

            struct Attributes
            {
                UNITY_VERTEX_INPUT_INSTANCE_ID
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 lightmapUV : TEXCOORD1;
            };

            struct Varyings
            {
                UNITY_VERTEX_INPUT_INSTANCE_ID
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD2;
                float2 lightmapUV : TEXCOORD4;
                float3 trueWS     : TEXCOORD5;
                float3 logicalViewDirectionWS : TEXCOORD6;
                float4 positionOS : TEXCOORD7;
            };

            Varyings LitPassVertex(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output = (Varyings)0;
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionOS = input.positionOS.xyz;
                output.trueWS = TransformObjectToWorld(positionOS);

                if (OmniRadialUsesLogicalTransform())
                {
                    float3 mappedPosition = OmniRadialObjectToCameraRelative(positionOS);
                    output.positionCS = OmniRadialCameraRelativeToHClip(mappedPosition);
                    output.normalWS = OmniRadialObjectToLogicalNormal(input.normalOS);
                }
                else
                {
                    output.positionCS = TransformWorldToHClip(output.trueWS);
                    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                }

                output.logicalViewDirectionWS = OmniRadialUsesLogicalTransform()
                    ? OmniGetLogicalViewDirection(positionOS)
                    : GetWorldSpaceNormalizeViewDir(output.trueWS);
                output.positionOS = OmniRadialDepthPosition(positionOS);
                output.uv = input.uv;
                output.lightmapUV = input.lightmapUV;
                return output;
            }

            // 札(波の位置を決める点)を求める不動点反復の回数。反復ごとに誤差が kA(≪1)倍に縮む。
            #define OCEAN_LABEL_ITERATIONS 3

            void LitPassFragment(
                Varyings input,
                out half4 outputColor : SV_Target,
                out float outputDepth : SV_Depth)
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float depth = input.uv.x;
                // 海岸線: 陸(水深 0 以下)は描かない。
                clip(depth);

                float3 position = input.positionOS.xyz / max(input.positionOS.w, 1.0e-30f);
                float quadLength = _PlanetCellQuad.x;
                float3 label = OceanLabel(position, _PlanetCellUp.xyz, _PlanetCellUp.w, quadLength, OCEAN_LABEL_ITERATIONS);
                float3 up = normalize(_PlanetCellUp.xyz * _PlanetCellUp.w + label);

                if (_FlatColor > 0.5f)
                {
                    outputColor = half4(_DeepColor.rgb, 1);
                    outputDepth = OmniRadialResolveFragmentDepth(input.positionOS, input.positionCS.z);
                    return;
                }

                // 画素の大きさ(Cell の原点からの相対位置の変化量)。
                float footprint = max(length(ddx(position)), length(ddy(position)));
                float3 gradientOS;
                float variance;
                OceanShortWaves(label, up, quadLength, footprint, gradientOS, variance);

                half3 normalWS = normalize(input.normalWS);
                float gradientLength = length(gradientOS);
                if (gradientLength > 1.0e-9f)
                {
                    float3 gradientWS;
                    if (OmniRadialUsesLogicalTransform())
                    {
                        gradientWS = OmniRadialObjectToLogicalDirection(gradientOS) * gradientLength;
                    }
                    else
                    {
                        gradientWS = TransformObjectToWorldDir(gradientOS, false);
                    }
                    normalWS = normalize(normalWS - (half3)gradientWS);
                }

                // 色と泡
                float shallow = exp(-depth * _Absorption);
                half3 albedo = (half3)lerp(_DeepColor.rgb, _ShallowColor.rgb, shallow);
                float foam = 0;
                if (_FoamDepth > 0.0f)
                {
                    float shore = saturate(1.0f - depth / _FoamDepth);
                    // 泡の模様は 2 つの波の位相の積。位相は Cell の原点での端数を含むので、Cell の境で途切れない
                    // (Cell の原点からの相対位置だけで作ると、Cell ごとに模様が変わる)。波は成分の 1/2 と 2/3 の所(数 m の波長)。
                    int waveCount = (int)_WaveCount;
                    int first = min((int)(_WaveCount * 0.5f), waveCount - 1);
                    int second = min((int)(_WaveCount * 0.65f), waveCount - 1);
                    float pattern = (0.5f + 0.5f * sin(OceanTheta(first, label))) * (0.5f + 0.5f * sin(OceanTheta(second, label) + 1.3f));
                    foam = shore * smoothstep(0.1f, 0.5f, pattern);
                }
                albedo = lerp(albedo, (half3)_FoamColor.rgb, (half)foam);

                // 粗さ: 傾きの分散を足す。泡は粗い。
                float roughness = 1.0f - _Smoothness;
                half smoothness = (half)(1.0f - sqrt(roughness * roughness + variance));
                smoothness = lerp(smoothness, (half)0.1, (half)foam);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.trueWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = normalize(input.logicalViewDirectionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.trueWS);
                inputData.bakedGI = SAMPLE_GI(input.lightmapUV, half3(0, 0, 0), normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.metallic = 0;
                surfaceData.smoothness = saturate(smoothness);
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.occlusion = 1;
                surfaceData.alpha = 1;

                half4 finalColor = UniversalFragmentPBR(inputData, surfaceData);
                finalColor.rgb = MixFog(finalColor.rgb, inputData.fogCoord);
                outputColor = finalColor;
                outputDepth = OmniRadialResolveFragmentDepth(input.positionOS, input.positionCS.z);
            }
            ENDHLSL
        }
    }
}
