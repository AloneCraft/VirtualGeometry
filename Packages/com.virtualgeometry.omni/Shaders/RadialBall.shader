// サッカーボールの shader(Omni の Radial 経路)。RadialOcean と同じ頂点経路で、fragment が切頂二十面体の模様(黒い五角形と白い六角形、縫い目)を描く。
// 模様はボールの中心からの方向で決めるので、ボールの回転に付いて回り、Cell の境で途切れない。.vgshader が VG 用 pass へ複製する。
Shader "Omni/RadialBall"
{
    Properties
    {
        _WhiteColor("White (hexagon)", Color) = (0.95, 0.95, 0.93, 1)
        _BlackColor("Black (pentagon)", Color) = (0.03, 0.03, 0.03, 1)
        _SeamColor("Seam", Color) = (0.15, 0.15, 0.15, 1)
        // 縫い目の太さ(面の値の差。0.01 でおよそ ボールの半径の 1%)。
        _SeamWidth("Seam Width", Float) = 0.008
        _Smoothness("Smoothness", Range(0, 1)) = 0.55
        // 1 なら、照明も模様もなしで _FlatTint だけを出す(検証用)。
        _FlatColor("Flat Color (debug)", Float) = 0
        _FlatTint("Flat Tint (debug)", Color) = (1, 0.1, 0.1, 1)
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
                float4 _WhiteColor;
                float4 _BlackColor;
                float4 _SeamColor;
                float  _SeamWidth;
                float  _Smoothness;
                float  _FlatColor;
                float4 _FlatTint;
            CBUFFER_END

            // Cell ごとの値(MaterialPropertyBlock、PlanetLayerSource.WriteCellProperties)。
            // _PlanetCellUp: xyz=Cell の原点の外向きの単位方向(object 空間)、w=Cell の原点の惑星の中心からの距離(半径 + 原点を持ち上げた距離)。
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

            // 正二十面体の頂点の方向(切頂二十面体の五角形の面の法線)と、正十二面体の頂点の方向(六角形の面の法線)。
            static const float3 kPentagon[12] =
            {
                float3( 0.0000,  0.5257,  0.8507), float3( 0.0000,  0.5257, -0.8507), float3( 0.0000, -0.5257,  0.8507), float3( 0.0000, -0.5257, -0.8507),
                float3( 0.5257,  0.8507,  0.0000), float3( 0.5257, -0.8507,  0.0000), float3(-0.5257,  0.8507,  0.0000), float3(-0.5257, -0.8507,  0.0000),
                float3( 0.8507,  0.0000,  0.5257), float3( 0.8507,  0.0000, -0.5257), float3(-0.8507,  0.0000,  0.5257), float3(-0.8507,  0.0000, -0.5257)
            };
            static const float3 kHexagon[20] =
            {
                float3( 0.5774,  0.5774,  0.5774), float3( 0.5774,  0.5774, -0.5774), float3( 0.5774, -0.5774,  0.5774), float3( 0.5774, -0.5774, -0.5774),
                float3(-0.5774,  0.5774,  0.5774), float3(-0.5774,  0.5774, -0.5774), float3(-0.5774, -0.5774,  0.5774), float3(-0.5774, -0.5774, -0.5774),
                float3( 0.0000,  0.3568,  0.9342), float3( 0.0000,  0.3568, -0.9342), float3( 0.0000, -0.3568,  0.9342), float3( 0.0000, -0.3568, -0.9342),
                float3( 0.3568,  0.9342,  0.0000), float3( 0.3568, -0.9342,  0.0000), float3(-0.3568,  0.9342,  0.0000), float3(-0.3568, -0.9342,  0.0000),
                float3( 0.9342,  0.0000,  0.3568), float3( 0.9342,  0.0000, -0.3568), float3(-0.9342,  0.0000,  0.3568), float3(-0.9342,  0.0000, -0.3568)
            };

            // 切頂二十面体を球へ放射状に投影した模様。方向 dir から出た光線が最初に出る面 = dot(dir, 面の法線) / 面までの距離 が最大の面。
            // 辺の長さを 1 としたときの、中心から五角形の面までの距離は 2.3274、六角形の面までは 2.2674。
            // pentagon: 五角形の面なら 1。seam: 1 番目と 2 番目の面の値の差(辺の近くで 0)。
            void SoccerPattern(float3 dir, out float pentagon, out float seam)
            {
                float best = -1.0e9f;
                float second = -1.0e9f;
                float bestIsPentagon = 0.0f;
                for (int i = 0; i < 12; i++)
                {
                    float score = dot(dir, kPentagon[i]) / 2.3274f;
                    if (score > best) { second = best; best = score; bestIsPentagon = 1.0f; }
                    else if (score > second) { second = score; }
                }
                for (int j = 0; j < 20; j++)
                {
                    float score = dot(dir, kHexagon[j]) / 2.2674f;
                    if (score > best) { second = best; best = score; bestIsPentagon = 0.0f; }
                    else if (score > second) { second = score; }
                }
                pentagon = bestIsPentagon;
                seam = best - second;
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

                // ボールの中心からの方向(ボールの object 空間 = ボールと一緒に回る)。Cell の原点の方向 × Cell の原点の中心からの距離(半径 + 持ち上げた距離)+ Cell の原点からの相対位置。
                float3 position = input.positionOS.xyz / max(input.positionOS.w, 1.0e-30f);
                float3 dir = normalize(_PlanetCellUp.xyz * _PlanetCellUp.w + position);
                float pentagon;
                float seam;
                SoccerPattern(dir, pentagon, seam);
                float seamWidth = max(_SeamWidth, 1.5f * fwidth(seam));
                float seamMask = 1.0f - smoothstep(0.0f, seamWidth, seam);
                half3 albedo = (half3)lerp(_WhiteColor.rgb, _BlackColor.rgb, pentagon);
                albedo = lerp(albedo, (half3)_SeamColor.rgb, (half)seamMask);

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
