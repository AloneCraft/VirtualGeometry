// CosmicScale の不透明な球（CosmicPlanetLit、CosmicGasGiant、CosmicStarSurface）の頂点・フラグメントの共通部。
// 使う側は Core.hlsl → RadialInject.hlsl → CosmicDepthInvariance.hlsl → CosmicCommon.hlsl → 自分の表面の関数 → このファイルの順に読み、
// 表面の色を返す float3 CosmicSurfaceColor(CosmicSurface s) を、このファイルより前に定義します（ForwardLit のときだけ使います）。
// パスは COSMIC_PASS_FORWARD / COSMIC_PASS_DEPTH_ONLY / COSMIC_PASS_DEPTH_NORMALS のどれかを define して読みます。
// 深度を書く3つのパスは CosmicDepthInvariance の precise 関数でクリップ座標と SV_Depth を一致させます（Depth Priming の穴を防ぐ）。
// 頂点関数は1引数、Attributes/Varyings は最初から UNITY_VERTEX_INPUT_INSTANCE_ID を持ちます（BRG の sidecar 生成の条件）。
// COSMIC_SURFACE_FROM_NORMAL（CosmicPlanetPatch）: 分割した地面（PlanetSurface のパッチ）用。物体空間はパッチの中心からの差なので、
// 天体の局所の方向は頂点の法線（標高 0 の球では方向そのもの）から求め、UV（CosmicScale の球と同じ規約）も画素ごとに求めます。
// COSMIC_SURFACE_MESH_NORMAL（CosmicMicroLit）: 球ではない形（凹んだゴルフボール、核子、原子核の模型、ヘモグロビン、赤血球、砂粒）用。表面の法線は頂点の法線の補間（物体空間）です。
#ifndef COSMIC_OPAQUE_PASS_INCLUDED
#define COSMIC_OPAQUE_PASS_INCLUDED

struct CosmicAttributes
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv         : TEXCOORD0;
};

struct CosmicVaryings
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionCS : SV_POSITION;
    float4 positionOS : TEXCOORD0;
    float2 uv         : TEXCOORD1;
    float3 normalWS   : TEXCOORD2;
#if defined(COSMIC_SURFACE_FROM_NORMAL) || defined(COSMIC_SURFACE_MESH_NORMAL)
    float3 normalOS   : TEXCOORD3;
#endif
    // MoonTriple が追加: 表面の点の、カメラからの相対位置(ボールの点光源の距離に使う)。
    float3 cameraRelative : TEXCOORD4;
};

#if defined(COSMIC_SURFACE_MESH_NORMAL) && !defined(COSMIC_SPIN_AXIS)
// 回転（_CosmicP3.x = 角速度 rad/s）の軸。Y を 15° 傾けた向き（CosmicMicroSurface.hlsl も同じ値を先に定義します）。
#define COSMIC_SPIN_AXIS float3(0.2588190, 0.9659258, 0.0)
#endif

CosmicVaryings CosmicOpaqueVertex(CosmicAttributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    CosmicVaryings output = (CosmicVaryings)0;
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    precise float3 positionOS = input.positionOS.xyz;
    float3 normalOS = input.normalOS;
#if defined(COSMIC_SURFACE_MESH_NORMAL)
    // 回転（_CosmicP3.x = 角速度 rad/s、0 は回さない）: ゴルフボール（ディンプルを凹ませた形そのものを回す。2026-10-06 のユーザーの指示）。
    // 回転軸は Y を 15° 傾けた向き。深度を書く 3 つのパスで同じ位置になるよう precise で求めます（パスごとのずれは Depth Priming の穴になる）。
    if (_CosmicP3.x != 0.0)
    {
        precise float angle = CosmicSeconds() * _CosmicP3.x;
        precise float3 rotated = CosmicRotateAxis(positionOS, COSMIC_SPIN_AXIS, angle);
        positionOS = rotated;
        normalOS = CosmicRotateAxis(normalOS, COSMIC_SPIN_AXIS, angle);
    }
#endif
    if (OmniRadialUsesLogicalTransform())
    {
        output.positionCS = CosmicInvariantLogicalClip(positionOS);
#if defined(COSMIC_PASS_DEPTH_NORMALS)
        RadialMapSample mapSample = OmniRadialObjectSample(positionOS);
        output.normalWS = OmniRadialObjectToMappedNormal(positionOS, normalOS, mapSample);
#else
        output.normalWS = OmniRadialObjectToLogicalNormal(normalOS);
#endif
    }
    else
    {
        output.positionCS = CosmicInvariantObjectClip(positionOS);
        output.normalWS = TransformObjectToWorldNormal(normalOS);
    }
    output.positionOS = CosmicInvariantDepthPosition(positionOS);
    output.cameraRelative = OmniRadialUsesLogicalTransform() ? OmniRadialObjectToLogicalCameraRelative(positionOS) : TransformObjectToWorld(positionOS) - _WorldSpaceCameraPos;
    output.uv = input.uv;
#if defined(COSMIC_SURFACE_FROM_NORMAL) || defined(COSMIC_SURFACE_MESH_NORMAL)
    output.normalOS = normalOS;
#endif
    return output;
}

#if defined(COSMIC_SURFACE_FROM_NORMAL)
// CosmicScale の球のメッシュと同じ UV（Y が北極、u = (atan2(z, x) + π) / 2π、v = asin(y) / π + 0.5）を方向から求めます。
// 経度の継ぎ目（u が 1 から 0 へ跳ぶ所）で隣の画素との差が大きくなり、ミップが最も粗くなって線が見えないよう、
// 継ぎ目を反対側へ移した u と比べて、画面上の変化が小さい方を使います（範囲外の u はテクスチャの繰り返しで同じ色）。
float2 CosmicSphereUvFromDirection(float3 direction)
{
    float u = (atan2(direction.z, direction.x) + PI) / (2.0 * PI);
    float v = asin(clamp(direction.y, -1.0, 1.0)) / PI + 0.5;
    float wrapped = frac(u + 0.5) - 0.5;
    return float2(fwidth(u) <= fwidth(wrapped) + 1e-6 ? u : wrapped, v);
}
#endif

#if defined(COSMIC_PASS_FORWARD)
float3 CosmicSurfaceColor(CosmicSurface s);
#endif

void CosmicOpaqueFragment(CosmicVaryings input, out half4 outputColor : SV_Target, out float outputDepth : SV_Depth)
{
    UNITY_SETUP_INSTANCE_ID(input);
#if defined(COSMIC_PASS_FORWARD)
    float3 objectPosition = input.positionOS.xyz / input.positionOS.w;
    CosmicSurface s;
#if defined(COSMIC_SURFACE_FROM_NORMAL)
    // パッチ: 天体の局所の方向は補間した頂点の法線から（物体空間の位置はパッチの中心からの差）。
    float3 direction = normalize(input.normalOS);
    s.unitPosition = direction;
    s.normalOS = direction;
    s.normalWS = OmniRadialUsesLogicalTransform() ? OmniRadialObjectToLogicalNormal(direction) : TransformObjectToWorldNormal(direction);
    s.uv = CosmicSphereUvFromDirection(direction);
#else
    s.unitPosition = objectPosition * 2.0;
#if defined(COSMIC_SURFACE_MESH_NORMAL)
    // 回す形（ゴルフボール）の模様（くぼみの陰り・汚れ）は、回す前の位置、つまり物体に貼り付いた座標で求めます
    // （回した後の位置で求めると、形は回っても模様がその場に残った。2026-10-06 のユーザーの指摘）。照明は回した後の法線のまま。
    if (_CosmicP3.x != 0.0) s.unitPosition = CosmicRotateAxis(objectPosition, COSMIC_SPIN_AXIS, -CosmicSeconds() * _CosmicP3.x) * 2.0;
    // 球ではない形: 頂点の法線の補間（面が平らな所は一定、角を丸めた所は滑らかにつながる）。
    s.normalOS = normalize(input.normalOS);
#else
    // 画素ごとに物理面の点から法線と視線を求め直します（巨大な球でも頂点補間の歪みを受けません）。
    s.normalOS = normalize(objectPosition);
#endif
    s.normalWS = OmniRadialUsesLogicalTransform() ? OmniRadialObjectToLogicalNormal(s.normalOS) : normalize(input.normalWS);
    s.uv = input.uv;
#endif
    s.cameraRelative = input.cameraRelative;
    s.viewWS = OmniRadialUsesLogicalTransform() ? OmniRadialReconstructLogicalViewDirection(input.positionOS) : GetWorldSpaceNormalizeViewDir(TransformObjectToWorld(objectPosition));
    float3 dx = ddx(s.unitPosition), dy = ddy(s.unitPosition);
    s.footprint = max(sqrt(max(dot(dx, dx), dot(dy, dy))), 1e-7);
    float3 color = CosmicSurfaceColor(s);
    if (_CosmicDebugView > 0.5)
    {
        int mode = (int)(_CosmicDebugView + 0.5);
        if (mode == 1) color = s.viewWS * 0.5 + 0.5;
        else if (mode == 2) color = s.normalWS * 0.5 + 0.5;
        else if (mode == 3) color = float3(frac(s.uv * 4096.0), 0);
        else if (mode == 4) color = saturate(dot(s.normalWS, s.viewWS)).xxx;
        else if (mode == 5) color = saturate(dot(s.normalWS, normalize(_CosmicKeyLight.xyz))).xxx;
    }
    outputColor = half4(color, 1);
#elif defined(COSMIC_PASS_DEPTH_NORMALS)
    outputColor = half4(normalize(input.normalWS) * 0.5 + 0.5, 0);
#else
    outputColor = input.positionCS.z;
#endif
    outputDepth = CosmicInvariantFragmentDepth(input.positionOS, input.positionCS.z);
}

#endif
