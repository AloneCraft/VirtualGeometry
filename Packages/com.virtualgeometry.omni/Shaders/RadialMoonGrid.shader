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
        // 色(アルベド)の格子(PlanetTerrainSource.ColorLayer)があるとき、_BaseColor の代わりに色の texture の色 × _ColorGain を地面の色にする。
        // _ColorOnly が 1 なら、その色(線形)を、照明も格子もなしで出す(検証用)。
        _ColorGain("Color Gain", Float) = 1
        _ColorOnly("Color Only (debug)", Float) = 0
        [NoScaleOffset] _PlanetColorTex("Planet Color Texture", 2D) = "white" {}
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
            #include "Packages/com.virtualgeometry.world/Runtime/Resources/VirtualGeometryWorld/PlanetLonLat.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _GridColor;
                float4 _GridAxisU;
                float4 _GridAxisV;
                float  _Smoothness;
                float  _FlatColor;
                float4 _FlatTint;
                float  _ColorGain;
                float  _ColorOnly;
            CBUFFER_END

            TEXTURE2D(_PlanetColorTex);
            SAMPLER(sampler_PlanetColorTex);

            // Cell ごとの値(MaterialPropertyBlock、PlanetLayerSource.WriteCellProperties)。
            // _PlanetCellUp: xyz=Cell の原点の外向きの単位方向(object 空間)、w=Cell の原点の惑星の中心からの距離(半径 + 原点を持ち上げた距離)。
            // _PlanetCellRef: xyz=Cell の原点 − 基準点(Logical で引いたもの)、w=基準点があれば 1。
            float4 _PlanetCellUp;
            float4 _PlanetCellRef;
            // ボールの点光源(MoonTripleDemo が Shader.SetGlobalVector で毎フレーム書く。書かなければ 0 で、何も足さない)。
            // xyz=ボールの中心 − 基準点(惑星のローカル座標、m)、w=強さ。基準点があるときだけ働く(_PlanetCellRef.w > 0.5)。
            // 距離 d の減衰は 1 / (1 + (d / 0.4 m)^2)(影の中の補助光)。Omni の面(CosmicPlanetSurface.hlsl)の _CosmicBallLight と同じ式。
            float4 _PlanetBallLight;
            // 主光源(MoonTripleDemo が書く。Omni の面の _CosmicKeyLight と同じ値): xyz = 光へ向かう向き、w = 強さ。ボールがあるときだけ使う。
            float4 _PlanetKeyLight;
            // 色の格子の Cell ごとの値(PlanetTerrainSource.WriteCellProperties)。
            // _PlanetColorDirection: xyz=Cell の原点の単位方向(惑星のローカル座標)、w=色の格子があれば 1。
            // _PlanetColorIndex: xy=原点の画素の列・行(北が行 0)の整数部、zw=texture の幅・高さ。
            // _PlanetColorFraction: xy=原点の画素の列・行の端数、z=1 ラジアンあたりの列数(経度)、w=同(緯度)。
            float4 _PlanetColorDirection;
            float4 _PlanetColorIndex;
            float4 _PlanetColorFraction;

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
                half3 ballLight = 0;
                // ボールの点光源が有効なとき: Sun と環境光は 0.1 倍に下げ(光が届かない所は暗い)、格子の線は光の計算の後で色を直接乗せる(エミッシブ)。
                bool ballLit = _PlanetBallLight.w > 0.0f && _PlanetCellRef.w > 0.5f;
                float gridMask = 0;
                // 色の格子: 画素の緯度経度の差(Cell の原点の方向 o からの差 eps = n − o)を、桁落ちしない式で求め、色の texture の位置にする。
                // n = (o·(R+H) + position) の方向。n − o = (p + o·(R+H − |w|))/|w|、R+H − |w| = −(2(R+H)(o·p) + |p|²)/(|w| + R+H)(w = o·(R+H) + p)。
                if (_PlanetColorDirection.w > 0.5f)
                {
                    float3 o = _PlanetColorDirection.xyz;
                    float radius = _PlanetCellUp.w;
                    float3 w = o * radius + position;
                    float wl = length(w);
                    float correction = -(2.0f * radius * dot(o, position) + dot(position, position)) / (wl + radius);
                    float3 eps = (position + o * correction) / wl;
                    float2 delta = PlanetLonLatDelta(o, eps);
                    float column = _PlanetColorFraction.x + delta.x * _PlanetColorFraction.z;
                    float row = _PlanetColorFraction.y - delta.y * _PlanetColorFraction.w;
                    float2 uv = float2((_PlanetColorIndex.x + column + 0.5f) / _PlanetColorIndex.z,
                        1.0f - (_PlanetColorIndex.y + row + 0.5f) / _PlanetColorIndex.w);
                    // mip: 画素の大きさ(m)と、texture の 1 画素の南北の大きさ(m)から。
                    float pixelMeters = max(length(ddx(position)), length(ddy(position)));
                    float texelMeters = radius / _PlanetColorFraction.w;
                    float lod = max(0.0f, log2(max(pixelMeters, 1.0e-9f) / texelMeters));
                    albedo = (half3)SAMPLE_TEXTURE2D_LOD(_PlanetColorTex, sampler_PlanetColorTex, uv, lod).rgb * (half)_ColorGain;
                    if (_ColorOnly > 0.5f)
                    {
                        outputColor = half4(albedo, 1);
                        outputDepth = OmniRadialResolveFragmentDepth(input.positionOS, input.positionCS.z);
                        return;
                    }
                }
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
                    if (ballLit) { gridMask = grid; } else { albedo = lerp(albedo, (half3)_GridColor.rgb, (half)grid); }
                }

                half3 normalWS = normalize(input.normalWS);

                // ボールがあるとき(_PlanetBallLight.w > 0 かつ基準点がある)は、URP の光を使わず、Omni の面(CosmicPlanetSurface.hlsl)と同じ式で照らす:
                // 主光源(_PlanetKeyLight)の拡散(Lommel-Seeliger と Lambert の混合 = 月面の粗い面の反射)、ボールの球の影(解析的、縁は画素幅で滑らかに)、
                // ボールの周りの環境遮蔽(球による解析式)、影の中が真っ黒にならない弱い補助光(ボールの点光源)。URP の影の地図は、この座標の精度では使えない。
                half3 ballShaded = 0;
                if (ballLit)
                {
                    float3 toBall = _PlanetBallLight.xyz - (_PlanetCellRef.xyz + position);
                    float ballDistance2 = max(dot(toBall, toBall), 1.0e-12f);
                    float3 ballDirection = toBall * rsqrt(ballDistance2);
                    float3 lightDir = normalize(_PlanetKeyLight.xyz);
                    float3 viewDir = normalize(input.logicalViewDirectionWS);
                    float mu0 = dot((float3)normalWS, lightDir);
                    float mu = saturate(dot((float3)normalWS, viewDir));
                    float ls = mu0 > 0.0f ? 2.0f * mu0 / (mu0 + mu + 1.0e-4f) : 0.0f;
                    float diffuse = lerp(saturate(mu0), ls * 0.5f, 0.6f);
                    float alongLight = dot(toBall, lightDir);
                    float closest = sqrt(max(ballDistance2 - alongLight * alongLight, 0.0f));
                    float edge = max(fwidth(closest), 1.0e-4f);
                    float shadow = alongLight > 0.0f ? smoothstep(0.11f - edge, 0.11f + edge, closest) : 1.0f;
                    float ao = 1.0f - saturate(dot((float3)normalWS, ballDirection)) * (0.11f * 0.11f) / ballDistance2;
                    float radiance = _PlanetBallLight.w / (1.0f + ballDistance2 / (0.4f * 0.4f))
                        * (0.35f + 0.65f * saturate(dot((float3)normalWS, ballDirection)));
                    float3 ambient = float3(0.012f, 0.013f, 0.016f);
                    float3 lit = (float3)albedo * (_PlanetKeyLight.w * diffuse * shadow + ambient * ao + radiance * ao) * 1.35f;
                    // 格子の線はエミッシブ(光の計算の後で色を直接乗せる)。
                    lit = lerp(lit, _GridColor.rgb, gridMask);
                    ballShaded = (half3)lit;
                }

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
                if (ballLit)
                {
                    finalColor.rgb = ballShaded;
                }
                finalColor.rgb = MixFog(finalColor.rgb, inputData.fogCoord);
                outputColor = finalColor;
                outputDepth = OmniRadialResolveFragmentDepth(input.positionOS, input.positionCS.z);
            }
            ENDHLSL
        }
    }
}
