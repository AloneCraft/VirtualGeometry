// SpaceElevator/Procedural の forward pass。頂点からオブジェクト座標と法線を渡し、fragment で Blender 座標(m、Z上)に戻して質感レシピを計算する。
// VG の Virtual Geometry Shader 変換はこの pass を複製し、vertex 関数の入力 struct を VG 頂点で埋める。そのため入力 struct は 1 行 1 フィールドで書き、
// vertex 関数は「戻り値 名前(struct 引数){」の形にする(VirtualGeometryShaderSource.cs の解析規則)。
#ifndef SPACE_ELEVATOR_FORWARD_PASS_INCLUDED
#define SPACE_ELEVATOR_FORWARD_PASS_INCLUDED

// F0 を Blender の Specular IOR Level から決めるため、URP の BRDF を specular workflow で初期化する(Lighting.hlsl より前に定義する)。
#define _SPECULAR_SETUP 1
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "SpaceElevatorRecipes.hlsl"

struct SpaceElevatorAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
};

struct SpaceElevatorVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS : TEXCOORD1;
    float3 positionOS : TEXCOORD2;
    float3 normalOS : TEXCOORD3;
    half fogFactor : TEXCOORD4;
};

SpaceElevatorVaryings SpaceElevatorVertex(SpaceElevatorAttributes input)
{
    SpaceElevatorVaryings output;
    VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
    output.positionCS = position.positionCS;
    output.positionWS = position.positionWS;
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    output.positionOS = input.positionOS.xyz;
    output.normalOS = input.normalOS;
    output.fogFactor = ComputeFogFactor(position.positionCS.z);
    return output;
}

// Cycles の Bump ノードと同じ式(svm_node_set_bump)。高さの画面微分から法線を傾ける。UV も tangent も使わない。
// bumpDistance は高さ1あたりの変位(m)。strength は元の法線との混合率。
float3 SEBumpNormal(float3 normalWS, float3 positionWS, float height, float bumpDistance, float strength)
{
    float3 dPdx = ddx(positionWS);
    float3 dPdy = ddy(positionWS);
    float dHdx = ddx(height);
    float dHdy = ddy(height);
    float3 rx = cross(dPdy, normalWS);
    float3 ry = cross(normalWS, dPdx);
    float det = dot(dPdx, rx);
    float3 surfaceGradient = dHdx * rx + dHdy * ry;
    float3 bumped = abs(det) * normalWS - bumpDistance * sign(det) * surfaceGradient;
    float lengthSquared = dot(bumped, bumped);
    if (lengthSquared < 1e-20)
    {
        return normalWS;
    }
    bumped *= rsqrt(lengthSquared);
    return normalize(lerp(normalWS, bumped, saturate(strength)));
}

SESurface SEEvaluateRecipe(float3 pB, float3 nB, float footprint)
{
#if defined(_RECIPE_STEEL)
    return SESteelRecipe(pB, nB, footprint);
#elif defined(_RECIPE_WETGROUND)
    return SEWetGroundRecipe(pB, nB, footprint);
#else
    return SEPanelRecipe(pB, nB, footprint);
#endif
}

// 平面反射(Main Camera の描画中だけ有効)。凹凸で映り込みを揺らし、粗さに応じて mip でぼかす。
half3 SEPlanarReflection(InputData inputData, float3 geometricNormalWS, half perceptualRoughness, half3 skyReflection)
{
    if (_SpaceElevatorPlanarReflectionValid < 0.5)
    {
        return skyReflection;
    }
    float2 uv = inputData.normalizedScreenSpaceUV;
    uv += (inputData.normalWS.xz - geometricNormalWS.xz) * _PlanarDistortion;
    float mip = perceptualRoughness * _SpaceElevatorPlanarReflectionMips;
    half3 planar = SAMPLE_TEXTURE2D_LOD(_SpaceElevatorPlanarReflection, sampler_SpaceElevatorPlanarReflection, uv, mip).rgb;
    return lerp(skyReflection, planar, _PlanarStrength);
}

// URP の UniversalFragmentPBR と同じ組み立てで、環境反射の部分だけ平面反射に差し替える。
half4 SpaceElevatorFragment(SpaceElevatorVaryings input) : SV_Target
{
    float3 positionOS = input.positionOS;
    float3 normalOS = normalize(input.normalOS);
    float3 pB = float3(-positionOS.x, positionOS.y, positionOS.z) * _PatternScale;
    float3 nB = float3(-normalOS.x, normalOS.y, normalOS.z);
    float footprint = length(fwidth(pB)) * 0.7;

    SESurface recipe = SEEvaluateRecipe(pB, nB, footprint);

    float3 geometricNormalWS = normalize(input.normalWS);
    float3 normalWS = SEBumpNormal(geometricNormalWS, input.positionWS, recipe.height, 1.0, recipe.bumpStrength);

    InputData inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    inputData.positionCS = input.positionCS;
    inputData.normalWS = normalWS;
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
    inputData.bakedGI = SampleSH(normalWS);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    inputData.shadowMask = half4(1, 1, 1, 1);

    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo = recipe.albedo;
    surfaceData.metallic = recipe.metallic;
    // Blender の Specular IOR Level: 0.5 で F0 = 0.04。金属は albedo を F0 に使う。
    half dielectricF0 = 0.08 * recipe.specularLevel;
    surfaceData.specular = lerp(half3(dielectricF0, dielectricF0, dielectricF0), recipe.albedo, recipe.metallic);
    surfaceData.albedo = recipe.albedo * (1.0 - recipe.metallic);
    surfaceData.smoothness = 1.0 - recipe.roughness;
    surfaceData.normalTS = half3(0, 0, 1);
    surfaceData.emission = recipe.emission;
    surfaceData.occlusion = recipe.occlusion;
    surfaceData.alpha = 1;

    BRDFData brdfData;
    InitializeBRDFData(surfaceData, brdfData);
    BRDFData brdfDataClearCoat = (BRDFData)0;
    half4 shadowMask = CalculateShadowMask(inputData);
    AmbientOcclusionFactor aoFactor = CreateAmbientOcclusionFactor(inputData, surfaceData);
    Light mainLight = GetMainLight(inputData, shadowMask, aoFactor);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI);
    LightingData lightingData = CreateLightingData(inputData, surfaceData);

    half3 reflectVector = reflect(-inputData.viewDirectionWS, inputData.normalWS);
    half NoV = saturate(dot(inputData.normalWS, inputData.viewDirectionWS));
    half fresnelTerm = Pow4(1.0 - NoV);
    half3 indirectSpecular = GlossyEnvironmentReflection(reflectVector, inputData.positionWS, brdfData.perceptualRoughness, 1.0h, inputData.normalizedScreenSpaceUV);
    if (recipe.wetness > 0.0)
    {
        half3 planar = SEPlanarReflection(inputData, geometricNormalWS, brdfData.perceptualRoughness, indirectSpecular);
        indirectSpecular = lerp(indirectSpecular, planar, recipe.wetness);
    }
    lightingData.giColor = EnvironmentBRDF(brdfData, inputData.bakedGI, indirectSpecular, fresnelTerm) * aoFactor.indirectAmbientOcclusion;
    lightingData.mainLightColor = LightingPhysicallyBased(brdfData, brdfDataClearCoat, mainLight, inputData.normalWS, inputData.viewDirectionWS, 0.0, false);

    #if defined(_ADDITIONAL_LIGHTS)
    uint pixelLightCount = GetAdditionalLightsCount();
    #if USE_CLUSTER_LIGHT_LOOP
    [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
    {
        CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);
        lightingData.additionalLightsColor += LightingPhysicallyBased(brdfData, brdfDataClearCoat, light, inputData.normalWS, inputData.viewDirectionWS, 0.0, false);
    }
    #endif
    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);
        lightingData.additionalLightsColor += LightingPhysicallyBased(brdfData, brdfDataClearCoat, light, inputData.normalWS, inputData.viewDirectionWS, 0.0, false);
    LIGHT_LOOP_END
    #endif

    half4 color = CalculateFinalColor(lightingData, 1.0);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    return color;
}

#endif
