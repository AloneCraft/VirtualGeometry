// 月の表面の shader(Omni の Radial 経路)。RadialOcean と同じ頂点経路で、fragment が灰色の地面に、基準点を原点とする格子(1m、10cm、1cm)を描く。
// 格子の座標は「Cell の原点 − 基準点」を CPU が Logical で引いた値(_PlanetCellRef)から作るので、惑星の大きさによらず mm の精度で描ける。
// .vgshader が VG 用 pass へ複製する。
Shader "Omni/RadialMoonGrid"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (0.42, 0.41, 0.4, 1)
        _GridColor("Grid Color", Color) = (0.92, 0.9, 0.82, 1)
        // 基準点の接平面の 2 つの軸(惑星のローカル座標の単位ベクトル)。MoonBallDemo が設定する。
        _GridAxisU("Grid Axis U", Vector) = (1, 0, 0, 0)
        _GridAxisV("Grid Axis V", Vector) = (0, 0, 1, 0)
        _Smoothness("Smoothness", Range(0, 1)) = 0.05
        // 1 なら、照明も格子もなしで _FlatTint だけを出す(検証用)。
        _FlatColor("Flat Color (debug)", Float) = 0
        _FlatTint("Flat Tint (debug)", Color) = (0.5, 0.5, 0.5, 1)
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

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _GridColor;
                float4 _GridAxisU;
                float4 _GridAxisV;
                float  _Smoothness;
                float  _FlatColor;
                float4 _FlatTint;
            CBUFFER_END

            // Cell ごとの値(MaterialPropertyBlock、PlanetLayerSource.WriteCellProperties)。
            // _PlanetCellUp: xyz=Cell の原点の外向きの単位方向(object 空間)、w=惑星の半径。
            // _PlanetCellRef: xyz=Cell の原点 − 基準点(Logical で引いたもの)、w=基準点があれば 1。
            float4 _PlanetCellUp;
            float4 _PlanetCellRef;

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

            // 格子の線の濃さ。x を period ごとの線からの距離で見て、線の太さ width(画素より細くならない)で塗る。
            // 画素の大きさ footprint が period の 1/12 を超えたら消し始め、1/6 で消す(細かすぎる格子の折り返しを防ぐ)。
            float GridLine(float x, float period, float width, float footprint)
            {
                float distanceToLine = abs(frac(x / period + 0.5f) - 0.5f) * period;
                float halfWidth = max(0.5f * width, 0.5f * footprint);
                float coverage = saturate((halfWidth - distanceToLine) / max(footprint, 1.0e-9f) + 0.5f);
                float fade = saturate((period / 6.0f - footprint) / (period / 12.0f));
                return coverage * fade;
            }

            void LitPassFragment(
                Varyings input,
                out half4 outputColor : SV_Target,
                out float outputDepth : SV_Depth)
            {
                UNITY_SETUP_INSTANCE_ID(input);

                if (_FlatColor > 0.5f)
                {
                    outputColor = half4(_FlatTint.rgb, 1);
                    outputDepth = OmniRadialResolveFragmentDepth(input.positionOS, input.positionCS.z);
                    return;
                }

                float3 position = input.positionOS.xyz / max(input.positionOS.w, 1.0e-30f);
                half3 albedo = (half3)_BaseColor.rgb;
                // 基準点からの差(Cell の原点 − 基準点は CPU が Logical で引いた値)を、基準点の接平面の 2 つの軸で測る。
                if (_PlanetCellRef.w > 0.5f)
                {
                    float3 offset = _PlanetCellRef.xyz + position;
                    float u = dot(offset, _GridAxisU.xyz);
                    float v = dot(offset, _GridAxisV.xyz);
                    float footprint = max(length(ddx(position)), length(ddy(position)));
                    float line1m = max(GridLine(u, 1.0f, 0.006f, footprint), GridLine(v, 1.0f, 0.006f, footprint));
                    float line10cm = max(GridLine(u, 0.1f, 0.003f, footprint), GridLine(v, 0.1f, 0.003f, footprint));
                    float line1cm = max(GridLine(u, 0.01f, 0.0008f, footprint), GridLine(v, 0.01f, 0.0008f, footprint));
                    float grid = max(line1m, max(0.7f * line10cm, 0.4f * line1cm));
                    albedo = lerp(albedo, (half3)_GridColor.rgb, (half)grid);
                }

                half3 normalWS = normalize(input.normalWS);

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
                surfaceData.smoothness = _Smoothness;
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
