// CosmicHierarchy の軌道線（CosmicOrbitLine）と光点（CosmicPointGlow）。どちらも加算合成で、深度は書かず SV_Depth で不透明の対数深度と ZTest を合わせます。
// 頂点関数は1引数、構造体は最初から UNITY_VERTEX_INPUT_INSTANCE_ID を持ちます（BRG の sidecar 生成の条件）。
#ifndef COSMIC_HIERARCHY_SHADING_INCLUDED
#define COSMIC_HIERARCHY_SHADING_INCLUDED

struct CosmicMeshAttributes
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionOS : POSITION;
    float2 uv         : TEXCOORD0;
};

// ── 軌道線 ──

struct CosmicOrbitVaryings
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionCS : SV_POSITION;
    float4 positionOS : TEXCOORD0;
};

CosmicOrbitVaryings CosmicOrbitVertex(CosmicMeshAttributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    CosmicOrbitVaryings output = (CosmicOrbitVaryings)0;
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    float3 positionOS = input.positionOS.xyz;
    output.positionCS = OmniRadialUsesLogicalTransform() ? CosmicInvariantLogicalClip(positionOS) : CosmicInvariantObjectClip(positionOS);
    output.positionOS = CosmicInvariantDepthPosition(positionOS);
    return output;
}

void CosmicOrbitFragment(CosmicOrbitVaryings input, out half4 outputColor : SV_Target, out float outputDepth : SV_Depth)
{
    UNITY_SETUP_INSTANCE_ID(input);
    float fade = CosmicFade();
    if (fade <= 0.001) discard;
    // 物理的な面の上の点（三角形の透視補間を Radial の写像の前へ戻した値）。長半径 = 1。
    float3 p = input.positionOS.xyz / input.positionOS.w;
    float ratio = max(_CosmicP1.x, 1e-3);
    float2 q = float2(p.x, p.z / ratio);
    float f = length(q) - 1.0;
    float fw = max(fwidth(f), 1e-7);
    float stroke = 1.0 - smoothstep(0.6, 1.6, abs(f) / fw);
    // 軌道が画面で小さく（半径 30 画素未満）なったら消します（遠くで1点に重なって明るい塊にならないため）。
    float pixelsPerUnit = 1.0 / max(fwidth(length(p.xz)), 1e-9);
    float size = smoothstep(8.0, 30.0, pixelsPerUnit);
    // 天体の今の位置（離心近点角）の後ろほど明るく。
    float body = _CosmicState[CosmicBodyIndex()].z;
    float angle = atan2(q.y, q.x);
    float behind = frac((body - angle) / (2.0 * PI));
    float trail = 0.3 + 0.7 * exp(-behind * 5.0);
    if (stroke * size <= 0.001) discard;
    outputColor = half4(_CosmicP0.rgb * (_CosmicP0.w * stroke * size * trail * fade), 0);
    outputDepth = CosmicInvariantFragmentDepth(input.positionOS, input.positionCS.z);
}

// ── 光点 ──
// 銀河団の光（_CosmicP2.x = 1）: 光点と同じ四角形を、銀河団の見かけの半径まで広げ、団内の光と高温ガスを表す淡い光を
// β モデルの面輝度 (1 + (r / rc)²)^−1.5（β = 2/3、rc = 半径の _CosmicP2.y 倍）で描きます（表現は推定。実際の団内光はとても淡い）。
// 見かけの半径の下限は _CosmicP2.z 画素（強調した表現）。カメラが団の中にいるときは消します。

struct CosmicGlowVaryings
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionCS : SV_POSITION;
    float2 corner : TEXCOORD0;
    nointerpolation float4 depthOS : TEXCOORD1;
    nointerpolation float visibility : TEXCOORD2;
};

// 物体空間のカメラ位置（CosmicShellPass.hlsl の CosmicEyeObjectSpace と同じ。そのファイルは殻の描画関数を要求するので読みません）。
float3 CosmicGlowEyeObjectSpace()
{
    if (OmniRadialUsesLogicalTransform() && !OmniRadialUsesSharedUnityTransform())
    {
        float3 mantissa;
        float exponent;
        OmniRadialLogicalEyePosition(float3(0, 0, 0), mantissa, exponent);
        float4 exponents = OmniRadialTransformExponentsValue();
        return OmniRadialInverseDirection(OmniRadialBasis(), -mantissa) * exp2(clamp(exponent - exponents.y, -126.0, 126.0));
    }
    return TransformWorldToObject(GetCameraPositionWS());
}

CosmicGlowVaryings CosmicGlowVertex(CosmicMeshAttributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    CosmicGlowVaryings output = (CosmicGlowVaryings)0;
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    float3 center = float3(0, 0, 0);
    float4 clip = OmniRadialUsesLogicalTransform() ? CosmicInvariantLogicalClip(center) : CosmicInvariantObjectClip(center);
    // 天体の円板の見かけの半径（画素）。物体空間は単位直径の球なので、半径 0.5 と視点までの距離から求めます。
    float distanceOS = length(CosmicGlowEyeObjectSpace());
    float angularRadius = asin(saturate(0.5 / max(distanceOS, 0.5)));
    float pixelAngle = 2.0 / (abs(UNITY_MATRIX_P[1][1]) * _ScaledScreenParams.y);
    float radiusPixels = angularRadius / max(pixelAngle, 1e-12);
    output.visibility = 1.0 - smoothstep(_CosmicP1.y, max(_CosmicP1.z, _CosmicP1.y + 1e-3), radiusPixels);
    float quadPixels = _CosmicP1.x;
    if (_CosmicP2.x > 0.5)
    {
        // 見かけの半径が _CosmicP2.z 画素より小さいときは、その大きさで描きます（銀河団の段の場面では多くの団が数画素で、見えなかった）。
        quadPixels = clamp(radiusPixels, _CosmicP2.z, 4096.0);
        output.visibility = smoothstep(0.55, 0.8, distanceOS);
    }
    // 頂点を中心から画素単位でずらします（uv は -1..1 の角）。
    float2 corner = input.uv;
    clip.xy += corner * (2.0 * quadPixels) / _ScaledScreenParams.xy * clip.w;
    output.positionCS = clip;
    output.corner = corner;
    output.depthOS = CosmicInvariantDepthPosition(center);
    return output;
}

void CosmicGlowFragment(CosmicGlowVaryings input, out half4 outputColor : SV_Target, out float outputDepth : SV_Depth)
{
    UNITY_SETUP_INSTANCE_ID(input);
    float fade = _CosmicState[CosmicBodyIndex()].w * input.visibility;
    float r2 = dot(input.corner, input.corner);
    if (fade <= 0.001 || r2 >= 1.0) discard;
    float core = exp(-r2 * 22.0);
    float halo = exp(-r2 * 5.0) * 0.18 * (1.0 - r2);
    float light = core + halo;
    if (_CosmicP2.x > 0.5)
    {
        float rc2 = max(_CosmicP2.y * _CosmicP2.y, 1e-4);
        float edge = pow(1.0 + 1.0 / rc2, -1.5);
        light = max(pow(1.0 + r2 / rc2, -1.5) - edge, 0.0) / (1.0 - edge) * (1.0 - r2);
    }
    outputColor = half4(_CosmicP0.rgb * (_CosmicP0.w * light * fade), 0);
    outputDepth = CosmicInvariantFragmentDepth(input.depthOS, input.positionCS.z);
}

#endif
