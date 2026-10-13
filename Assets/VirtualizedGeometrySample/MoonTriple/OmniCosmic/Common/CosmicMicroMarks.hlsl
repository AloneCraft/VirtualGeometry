// CosmicScale の原子と陽子の「印」（CosmicMicroMarks）: 大きさの測れない物（電子・クォーク）を画面上で一定の画素の大きさの光る点で、
// 殻とグルーオンを 1 画素ほどの細い線で描きます。中性子星の磁力線（見えない磁場の図示）も同じ線で描きます。乗算済みアルファの合成（暗い背景では加算と同じに光り、明るい背景（遠くの月の面）の上でも
// 色が残って見える）。深度は書かず SV_Depth で不透明の対数深度と ZTest を合わせます。
// 頂点は 4 個で 1 つの印（四角形）。種類（TEXCOORD0.z）:
//   0 固定の点: POSITION が物体空間の位置。
//   1 殻を回る電子: POSITION が回転の軸（単位）、TEXCOORD1 = (殻の半径, 角速度 rad/s, 位相, -)。電子ごとに軸が違い、殻の球面の上をいろいろな向きに回る。
//   2 殻の円の 1 区間: 中心を通りカメラに向いた平面の円（殻の球の輪郭）。TEXCOORD1 = (半径, 始めの角, 終わりの角, -)。
//   3 固定の線の 1 区間: POSITION と NORMAL が両端。
//   4 中性子星の磁力線の 1 区間: POSITION と NORMAL が両端（星の半径 = 1、磁軸 = +Y の座標）。磁軸の傾き _CosmicP0.z と表示上の周期 _CosmicP0.y で
//     回し（CosmicBlackHoleShading.hlsl の CosmicMagneticAxis と同じ向き）、_CosmicP0.x 倍して物体の座標にします。星の見かけの円板
//     （半径 _CosmicP0.w）の後ろと、降着円盤（赤道面の _CosmicP1.z〜_CosmicP1.w、星の半径の単位。0 で無し）の向こう側は隠します。
//     アルファは 0.3 倍にして加算に近づけます（明るい星の表面の上で、線が暗い傷のように見えたため）。
// TEXCOORD0 = (角または (端, 側), 種類, 画素の大きさ)、TEXCOORD2 = (球の半径, 画素 a, 画素 b, -): その半径の球（物体の中心）の見かけの半径が
// a から b へ動く範囲で表示を変えます（a < b は大きくなると現れ、a > b は大きくなると消える。半径 0 は条件なし）。
// 材質の _CosmicP1.xy も同じ規則で、物体そのもの（半径 0.5）の見かけの大きさによる表示（原子が画素より小さい段では印を出さない）。
// 見かけの大きさと殻の円の向きは、描画中のカメラではなく圧縮の中心（主カメラ）から決めます（CosmicApparentSize.hlsl）。俯瞰カメラは
// 圧縮した世界をそのまま映すので、主カメラと同じ印が同じ向きで見えます。
// 頂点関数は1引数、構造体は最初から UNITY_VERTEX_INPUT_INSTANCE_ID を持ちます（BRG の sidecar 生成の条件。他の天体と同じ Scene BRG で描きます）。
#ifndef COSMIC_MICRO_MARKS_INCLUDED
#define COSMIC_MICRO_MARKS_INCLUDED

#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicApparentSize.hlsl"

struct CosmicMarksAttributes
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 shape      : TEXCOORD0;
    float4 motion     : TEXCOORD1;
    float4 fadeRule   : TEXCOORD2;
    float4 color      : COLOR;
};

struct CosmicMarksVaryings
{
    UNITY_VERTEX_INPUT_INSTANCE_ID
    float4 positionCS : SV_POSITION;
    float4 depthOS    : TEXCOORD0;
    float3 corner     : TEXCOORD1;
    float4 color      : TEXCOORD2;
};

float4 CosmicMarksClip(float3 positionOS)
{
    return OmniRadialUsesLogicalTransform() ? CosmicInvariantLogicalClip(positionOS) : CosmicInvariantObjectClip(positionOS);
}

float CosmicMarksRule(float pixels, float a, float b)
{
    if (abs(a - b) < 1e-6) return 1.0;
    return a < b ? smoothstep(a, b, pixels) : 1.0 - smoothstep(b, a, pixels);
}

float3 CosmicMarksOrbit(float3 axis, float radius, float speed, float phase)
{
    float3 helper = abs(axis.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 s0 = normalize(cross(axis, helper));
    float3 s1 = cross(axis, s0);
    float angle = phase + speed * CosmicSeconds();
    return radius * (s0 * cos(angle) + s1 * sin(angle));
}

// 種類 4: 磁軸の座標（星の半径の単位、磁軸 = +Y）の点を物体の座標へ。z 軸まわりに −傾き、y 軸まわりに −自転の角で回すと、
// +Y が CosmicMagneticAxis = (sin α cos φ, cos α, sin α sin φ) へ移ります。
float3 CosmicMarksMagnetic(float3 p)
{
    // 磁力線のねじれ（マグネター）: 磁軸まわりに、星の中心から見た緯度の向き（p.y / |p|）に比例して回します（北と南の足元が逆向きにねじれる）。
    // 角は body の状態 z（既定 0 = ねじれなし）。
    float twist = _CosmicState[CosmicBodyIndex()].z * p.y / max(length(p), 1e-3);
    float ctw = cos(twist), stw = sin(twist);
    p = float3(p.x * ctw - p.z * stw, p.y, p.x * stw + p.z * ctw);
    float tilt = _CosmicP0.z;
    float spin = 6.2831853 * CosmicSeconds() / max(_CosmicP0.y, 0.1);
    float ct = cos(tilt), st = sin(tilt), cs = cos(spin), ss = sin(spin);
    float3 tilted = float3(p.x * ct + p.y * st, -p.x * st + p.y * ct, p.z);
    return float3(tilted.x * cs - tilted.z * ss, tilted.y, tilted.x * ss + tilted.z * cs) * _CosmicP0.x;
}

// 種類 4 の点 p（物体の座標）の見え方（0..1）: 主カメラから見て星の見かけの円板の後ろにある点と、降着円盤の向こう側の点を隠します（光の曲がりは無視）。
float CosmicMarksFieldLineVisibility(float3 p, float3 eyeOS)
{
    float unit = max(_CosmicP0.x, 1e-9);
    float3 target = p / unit, eye = eyeOS / unit;
    float3 toTarget = target - eye;
    float span = length(toTarget);
    float3 direction = toTarget / max(span, 1e-9);
    float closest = -dot(eye, direction);
    float impact = length(eye + direction * closest);
    float visible = closest < span ? smoothstep(_CosmicP0.w * 0.92, _CosmicP0.w * 1.04, impact) : 1.0;
    if (_CosmicP1.z > 0.0 && eye.y * target.y < 0.0)
    {
        float3 crossing = eye + toTarget * (eye.y / (eye.y - target.y));
        float rho = length(crossing.xz);
        visible *= 1.0 - 0.85 * smoothstep(_CosmicP1.z * 0.9, _CosmicP1.z * 1.1, rho) * (1.0 - smoothstep(_CosmicP1.w * 0.7, _CosmicP1.w, rho));
    }
    return visible;
}

float3 CosmicMarksShell(float3 eyeOS, float radius, float angle)
{
    float lengthSquared = dot(eyeOS, eyeOS);
    float3 w = lengthSquared > 1e-20 ? eyeOS * rsqrt(lengthSquared) : float3(0, 0, 1);
    float3 helper = abs(w.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 u = normalize(cross(w, helper));
    float3 v = cross(w, u);
    return radius * (u * cos(angle) + v * sin(angle));
}

CosmicMarksVaryings CosmicMarksVertex(CosmicMarksAttributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    CosmicMarksVaryings output = (CosmicMarksVaryings)0;
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    float3 eyeOS = CosmicCenterObjectSpace();
    float visibility = CosmicFade() * CosmicMarksRule(CosmicApparentRadiusPixels(eyeOS, 0.5), _CosmicP1.x, _CosmicP1.y);
    if (input.fadeRule.x > 0.0)
    {
        visibility *= CosmicMarksRule(CosmicApparentRadiusPixels(eyeOS, input.fadeRule.x), input.fadeRule.y, input.fadeRule.z);
    }
    int kind = (int)(input.shape.z + 0.5);
    float size = input.shape.w;
    float4 clip;
    float3 anchor;
    if (kind <= 1)
    {
        anchor = kind == 0 ? input.positionOS.xyz : CosmicMarksOrbit(normalize(input.positionOS.xyz), input.motion.x, input.motion.y, input.motion.z);
        clip = CosmicMarksClip(anchor);
        clip.xy += input.shape.xy * (2.0 * size) / _ScaledScreenParams.xy * clip.w;
        output.corner = float3(input.shape.xy, 0);
    }
    else
    {
        float3 p0 = kind == 2 ? CosmicMarksShell(eyeOS, input.motion.x, input.motion.y) : kind == 4 ? CosmicMarksMagnetic(input.positionOS.xyz) : input.positionOS.xyz;
        float3 p1 = kind == 2 ? CosmicMarksShell(eyeOS, input.motion.x, input.motion.z) : kind == 4 ? CosmicMarksMagnetic(input.normalOS) : input.normalOS;
        float4 c0 = CosmicMarksClip(p0);
        float4 c1 = CosmicMarksClip(p1);
        bool atEnd = input.shape.x > 0.5;
        anchor = atEnd ? p1 : p0;
        clip = atEnd ? c1 : c0;
        if (c0.w <= 1e-6 || c1.w <= 1e-6)
        {
            // カメラの後ろへ回った区間は描きません（四角形を画面の外へ）。
            clip = float4(4.0, 4.0, 0.5, 1.0);
        }
        else
        {
            float2 delta = (c1.xy / c1.w - c0.xy / c0.w) * _ScaledScreenParams.xy;
            float lengthSquared = dot(delta, delta);
            float2 direction = lengthSquared > 1e-12 ? delta * rsqrt(lengthSquared) : float2(1, 0);
            float2 side = float2(-direction.y, direction.x);
            // 区間の両端を線の幅の半分だけ伸ばして、つなぎ目の隙間を消します。
            float2 offset = side * input.shape.y * size + direction * (atEnd ? 0.5 : -0.5) * size;
            clip.xy += offset * 2.0 / _ScaledScreenParams.xy * clip.w;
        }
        output.corner = float3(0, input.shape.y, 1);
        if (kind == 4) visibility *= CosmicMarksFieldLineVisibility(anchor, eyeOS);
    }
    output.positionCS = clip;
    output.depthOS = CosmicInvariantDepthPosition(anchor);
    output.color = float4(input.color.rgb * visibility, visibility * (kind == 4 ? 0.3 : 1.0));
    return output;
}

void CosmicMarksFragment(CosmicMarksVaryings input, out half4 outputColor : SV_Target, out float outputDepth : SV_Depth)
{
    UNITY_SETUP_INSTANCE_ID(input);
    if (input.color.a <= 0.001) discard;
    float intensity;
    if (input.corner.z < 0.5)
    {
        float r2 = dot(input.corner.xy, input.corner.xy);
        if (r2 >= 1.0) discard;
        intensity = exp(-r2 * 7.0) + exp(-r2 * 2.5) * 0.25 * (1.0 - r2);
    }
    else
    {
        float s = abs(input.corner.y);
        intensity = saturate(1.0 - s * s);
    }
    outputColor = half4(input.color.rgb * intensity, saturate(input.color.a * intensity));
    outputDepth = CosmicInvariantFragmentDepth(input.depthOS, input.positionCS.z);
}

#endif
