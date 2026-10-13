// CosmicScale の半透明の殻（コロナ、ブラックホール、体積）の頂点・フラグメントの共通部。
// 殻は単位直径の球（物体空間の半径 0.5）。フラグメントで物理的な視線（物体空間の始点と向き）を復元し、
// float4 CosmicShellShade(CosmicRay ray) が乗算済みアルファの色を返します（このファイルの後に読むファイルで定義）。
// 物体空間のカメラ位置は、Logical の仮数・指数のまま求めた「中心 − 視点」を基底の逆で戻します（_WorldSpaceCameraPos は
// 1e18 m を超えると Unity Transform が仮置きになるため使いません）。頂点で1回求め、補間しない値として渡します。
// 深度は殻の面の深度を SV_Depth に書き、不透明の対数深度と ZTest を合わせます（深度バッファへは書きません）。
// 俯瞰のワイプなど観察者の目でも、殻は頂点ごとに写像します（2026-10-05 のユーザー判断: 主カメラの描画を優先し、俯瞰はデバッグ用。
// 殻を剛体として置く方式は取りやめました）。そのため観察者の目では、物体空間の視線を主カメラの目からしか復元できず、
// 俯瞰のブラックホールは主カメラの視線方向へ押しつぶされ、空の置き換え（重力レンズ）は行いません。
// COSMIC_SHELL_HIDE_ENCLOSING_IN_OBSERVER を定義した殻（コロナ）は、観察者の目では、主カメラを内側に含むとき描きません。
// 主カメラを包む殻は、圧縮（主カメラからの向きを保ち距離だけを縮める）の後も主カメラを囲む球のまま残り、そこへ主カメラの視線で
// 計算した光が貼られて、俯瞰の模型を中心にしたドームになっていました（太陽の段で主カメラが隣のポルックスのコロナの中にいる。2026-10-06）。
// 主画面では、殻の中の主カメラから殻全体が裏面になり、Cull Back で描かれません。
#ifndef COSMIC_SHELL_PASS_INCLUDED
#define COSMIC_SHELL_PASS_INCLUDED

struct CosmicShellAttributes
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
};

struct CosmicShellVaryings
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionCS : SV_POSITION;
    float4 positionOS : TEXCOORD0;
    nointerpolation float3 eyeOS : TEXCOORD1;
};

// 物体空間のカメラ位置（単位直径の球の座標）。
float3 CosmicEyeObjectSpace()
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

CosmicShellVaryings CosmicShellVertex(CosmicShellAttributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    CosmicShellVaryings output = (CosmicShellVaryings)0;
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    float3 positionOS = input.positionOS.xyz;
#if defined(COSMIC_SHELL_SHRINK_WIDE_LENS_IN_OBSERVER)
    // 広げたレンズ（ブラックホールの mode 0 で _CosmicP3.x = 1）は、観察者の目では殻を _CosmicP3.z 倍（従来の大きさ）に縮めます。
    // 主カメラ（中心から約 90 Rs）が 150 Rs の殻の中に入り、俯瞰で主カメラを包むドームに円盤が貼られるため。光線は単位直径の球で
    // 求めるので、積分の始点と範囲は変わりません。
    if (CosmicIsObserverPass() && _CosmicP3.x > 0.5 && _CosmicP0.w < 0.5) positionOS *= _CosmicP3.z;
#endif
    output.positionCS = OmniRadialUsesLogicalTransform() ? CosmicInvariantLogicalClip(positionOS) : CosmicInvariantObjectClip(positionOS);
    output.positionOS = CosmicInvariantDepthPosition(positionOS);
    output.eyeOS = CosmicEyeObjectSpace();
#if defined(COSMIC_SHELL_HIDE_ENCLOSING_IN_OBSERVER)
    // 観察者の目で、主カメラが殻（単位直径の球）の中にあるときは、頂点を 1 点に潰します（面積 0 の三角形は画素を描かない）。
    if (CosmicIsObserverPass() && dot(output.eyeOS, output.eyeOS) < 0.25) output.positionCS = float4(0, 0, 0, 1);
#endif
    return output;
}

// 物体空間の視線。origin はカメラ、direction は単位ベクトル、surface はこの画素の殻の面の点。
struct CosmicRay
{
    float3 origin;
    float3 direction;
    float3 surface;
    // 球の中に入る t と出る t（カメラが中にいれば enter = 0）。
    float enter;
    float exit;
    // 画素の角度の大きさ（ラジアン）。
    float pixelAngle;
    // 画面の UV（深度テクスチャの標本化用）。
    float2 screenUV;
};

float4 CosmicShellShade(CosmicRay ray);

#if defined(COSMIC_SHELL_DEPTH_FROM_HIT)
// 殻の中の物（土星の環の面と氷の粒）が当たった点の、視線の上の距離（物体空間の単位）。CosmicShellShade が書き、深度に使います。
// 当たらなかった画素は負のまま（殻の面の深度）。
static float g_CosmicShellHitDistance = -1.0;
#endif

void CosmicShellFragment(CosmicShellVaryings input, out half4 outputColor : SV_Target, out float outputDepth : SV_Depth)
{
    UNITY_SETUP_INSTANCE_ID(input);
    float fade = CosmicFade();
    if (fade <= 0.001) discard;
    CosmicRay ray;
    ray.surface = input.positionOS.xyz / input.positionOS.w;
    ray.origin = input.eyeOS;
    float3 toSurface = ray.surface - ray.origin;
    ray.direction = normalize(toSurface);
    float2 hit = CosmicRaySphere(ray.origin, ray.direction, 0.5);
    ray.enter = max(hit.x, 0.0);
    ray.exit = max(hit.y, ray.enter);
    float3 dx = ddx(ray.direction), dy = ddy(ray.direction);
    ray.pixelAngle = max(sqrt(max(dot(dx, dx), dot(dy, dy))), 1e-6);
    ray.screenUV = input.positionCS.xy / _ScaledScreenParams.xy;
#if defined(COSMIC_SHELL_SHADE_APPLIES_FADE)
    // 表示の重みはシェーダーの側で掛けます（ブラックホール: 広げたレンズは現れる途中に曲がりを重みに比例させ、星空を二重にしない）。
    float4 color = CosmicShellShade(ray);
#else
    float4 color = CosmicShellShade(ray) * fade;
#endif
    outputColor = half4(color);
#if defined(COSMIC_SHELL_DEPTH_FROM_HIT)
    // 当たった点の深度（手前の不透明の天体との前後を正しくする。土星の環が土星の手前と奥で隠れ方を変える）。
    if (g_CosmicShellHitDistance >= 0.0)
        outputDepth = CosmicInvariantFragmentDepth(float4(ray.origin + ray.direction * g_CosmicShellHitDistance, 1), input.positionCS.z);
    else
        outputDepth = CosmicInvariantFragmentDepth(input.positionOS, input.positionCS.z);
#elif defined(COSMIC_SHELL_CENTER_DEPTH)
    // 境界の球の中心と同じ距離にある、この画素の視線上の点の深度（ブラックホール。球の中にある隣の天体を上書きしないため）。
    // 中心そのものの深度（奥行き z）を全画素に使うと、画面の横にある大きな球（カメラからほぼ真横の中心）では z がほぼ 0 で
    // 深度が最も手前になり、ずっと手前の天体の上に円盤が描かれていました（太陽の段で、はくちょう座A がポルックスの上に出た）。
    float3 centerDistancePoint = ray.origin + ray.direction * length(ray.origin);
    outputDepth = CosmicInvariantFragmentDepth(float4(centerDistancePoint, 1), input.positionCS.z);
#else
    outputDepth = CosmicInvariantFragmentDepth(input.positionOS, input.positionCS.z);
#endif
}

#endif
