// CosmicHierarchy の最後の場面: いて座A* へ自由落下するカメラから見た景色（NASA SVS 14576 の再現を物理で描く）。全画面の三角形で描きます。
// 単位はシュバルツシルト半径 Rs = 1、光速 c = 1。回転しないシュバルツシルト時空。
// 1. 画素の向き（カメラの系）を、カメラの速度 β（雨粒の系 = 無限遠から静止して真っすぐ落ちる観測者に対する速度）でローレンツ変換して、
//    雨粒の系での光の進む向き n を求めます（光行差）。
// 2. 雨粒の系の向き ψ（外向きの半径方向からの角）から、光子の衝突係数 b = r sinψ / (1 − v cosψ)（v = √(1/r)）と、
//    座標の半径方向の動きの符号 sign(cosψ − v) を求めます（Painlevé–Gullstrand 座標、地平面の内側でも有効）。1 − v cosψ ≤ 0 の光子は
//    E ≤ 0（地平面の内側だけで起こり、外の宇宙から来られない）なので黒。
// 3. 光子の軌道面の中で u = 1/r の式 u'' = −u + 1.5 u² を過去へ積分します。u → 0（無限遠）なら、その向きの空（いて座A* から見たデータの空:
//    天の川の淡い光と、データの星の地図。CosmicHierarchySkyShading.hlsl）、
//    u が大きくなりすぎたら（特異点・過去の地平面）黒。刻みは u の変化の割合が一定になるように変えます。
// 4. 降着円盤（赤道面、rin〜rout）と交わったら、そこで止めて放射を足します。振動数比 g = E_観測 / E_放射体（落下するカメラと、ケプラー回転の放射体）で、
//    色温度 ∝ g、強さ ∝ g⁴。模様はノイズを軌道の向きに長く引き伸ばした筋（トレイル状、2026-10-03 のユーザーの提案）を差動回転させます。
#ifndef COSMIC_PLUNGE_SHADING_INCLUDED
#define COSMIC_PLUNGE_SHADING_INCLUDED

#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicHierarchySkyShading.hlsl"

#define COSMIC_PLUNGE_STEPS 420

// xyz = カメラの位置（ブラックホールの中心から、ワールドの軸、Rs 単位）、w = 半径 r。
float4 _CosmicPlungePosition;
// xyz = 雨粒の系に対するカメラの速度 β（ワールドの軸、光速比）、w = 不透明度（突入の最初に通常の画面から切り替える）。
float4 _CosmicPlungeVelocity;
// カメラの右・上・前（ワールドの軸）。_CosmicPlungeForward.w = tan(縦の画角 / 2)。
// 縦横比は描画先の大きさから求めます（Game View と撮影先の縦横比が違ってもずれない）。
float4 _CosmicPlungeRight;
float4 _CosmicPlungeUp;
float4 _CosmicPlungeForward;
// 降着円盤: xyz = 法線（ワールド）、w = 内縁（Rs）。_CosmicPlungeDiskX: xyz = 円盤の面の基準の向き、w = 外縁（Rs）。
float4 _CosmicPlungeDisk;
float4 _CosmicPlungeDiskX;
// x = 円盤の最高温度（K）、y = 円盤の明るさ、z = 空の明るさ（負なら検算の表示: 無限遠へ出る光は白、捕まる光は黒、円盤なし）、w = 暗転（1 = 通常、0 = 黒）。
float4 _CosmicPlungeParams;

struct CosmicPlungeVaryings
{
    float4 positionCS : SV_POSITION;
};

CosmicPlungeVaryings CosmicPlungeVertex(uint vertexID : SV_VertexID)
{
    CosmicPlungeVaryings output;
    output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
    return output;
}

// 円盤の筋の明るさ（0.25〜1.6 程度）。角度 a（物質とともに回る座標）と半径 r の空間で、軌道の向きに長いノイズを、2位相の周期リセットで流します。
float CosmicPlungeTrail(float angle, float r, float t, float footprint)
{
    float value = 0;
    [unroll] for (int j = 0; j < 2; j++)
    {
        float tau, weight, epoch;
        CosmicFlowPhase(t, 40.0, j, tau, weight, epoch);
        // ケプラー回転（Ω ∝ r^−1.5）で角度をずらし、内側ほど速く巻きます（表示用の速さ）。
        float a = angle - 1.6 * pow(max(r / 3.0, 1.0), -1.5) * tau * 40.0;
        float3 p = float3(cos(a) * 2.2, sin(a) * 2.2, log(r) * 10.0) + (CosmicHash3(int3((int)epoch, j, 17)) - 0.5) * 6.0;
        float n = CosmicFbm(p, 4, footprint, 2.1, 0.55);
        value += (0.35 + 1.1 * saturate(0.5 + 0.8 * n)) * weight;
    }
    return value;
}

float3 CosmicPlungeShade(float3 viewDirection, float pixelAngle)
{
    float3 position = _CosmicPlungePosition.xyz;
    float r = max(_CosmicPlungePosition.w, 1e-4);
    float3 rhat = position / r;
    float v = sqrt(1.0 / r);
    // 1. 光行差: カメラの系の光の進む向き（見る向きの逆）を、雨粒の系へ。
    float3 beta = _CosmicPlungeVelocity.xyz;
    float b2 = dot(beta, beta);
    float3 nObserver = -viewDirection;
    float3 n = nObserver;
    float gamma = 1.0;
    if (b2 > 1e-10)
    {
        gamma = rsqrt(max(1.0 - b2, 1e-6));
        float3 bhat = beta * rsqrt(b2);
        n = (nObserver + gamma * beta + (gamma - 1.0) * dot(nObserver, bhat) * bhat) / (gamma * (1.0 + dot(beta, nObserver)));
        n = normalize(n);
    }
    // 2. 衝突係数と半径方向の符号。
    float cosPsi = dot(n, rhat);
    float3 tangentVector = n - cosPsi * rhat;
    float sinPsi = length(tangentVector);
    float3 that = sinPsi > 1e-5 ? tangentVector / sinPsi : normalize(cross(rhat, abs(rhat.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
    float denominator = 1.0 - v * cosPsi;
    if (denominator <= 1e-5) return 0;
    float impact = r * sinPsi / denominator;
    float radialSign = cosPsi - v >= 0.0 ? 1.0 : -1.0;
    // 観測される光子のエネルギー ÷ 保存量 E（無限遠から来た光の振動数比）。
    float gObserver = gamma * (1.0 - dot(beta, n)) / denominator;
    // 光子の角運動量の向き（前向きの運動の）。
    float3 hhat = sinPsi > 1e-5 ? normalize(cross(rhat, n)) : float3(0, 0, 0);
    // 3. 過去への積分（軌道面の基底: A = 半径、B = 過去へ進む角度の向き）。
    float3 axisA = rhat;
    float3 axisB = -that;
    float u = 1.0 / r;
    float inverseImpact2 = impact > 1e-6 ? 1.0 / (impact * impact) : 1e12;
    float w = radialSign * sqrt(max(inverseImpact2 - u * u * (1.0 - u), 0.0));
    float phi = 0;
    float3 diskNormal = _CosmicPlungeDisk.xyz;
    float rin = _CosmicPlungeDisk.w, rout = _CosmicPlungeDiskX.w;
    float previousSide = dot(axisA, diskNormal);
    float t = CosmicSeconds();
    [loop] for (int i = 0; i < COSMIC_PLUNGE_STEPS; i++)
    {
        if (u < 1e-3 && w < 0.0)
        {
            // 無限遠へ出る: 残りの角度は直線で外挿します。
            if (_CosmicPlungeParams.z < 0.0) return 1;
            float rest = -u / w;
            float3 direction = cos(phi + rest) * axisA + sin(phi + rest) * axisB;
            float3 sky = CosmicHierarchySgrASky(normalize(direction), pixelAngle);
            return sky * clamp(pow(gObserver, 3.0), 0.05, 12.0) * _CosmicPlungeParams.z;
        }
        if (u > 200.0) return 0;
        float h = clamp(0.04 * u / max(abs(w), 1e-4), 2e-5, 0.04);
        // RK4（u' = w、w' = −u + 1.5 u²）。
        float k1u = w, k1w = -u + 1.5 * u * u;
        float u2 = u + 0.5 * h * k1u, w2 = w + 0.5 * h * k1w;
        float k2u = w2, k2w = -u2 + 1.5 * u2 * u2;
        float u3 = u + 0.5 * h * k2u, w3 = w + 0.5 * h * k2w;
        float k3u = w3, k3w = -u3 + 1.5 * u3 * u3;
        float u4 = u + h * k3u, w4 = w + h * k3w;
        float k4u = w4, k4w = -u4 + 1.5 * u4 * u4;
        float uNext = u + h / 6.0 * (k1u + 2.0 * k2u + 2.0 * k3u + k4u);
        float wNext = w + h / 6.0 * (k1w + 2.0 * k2w + 2.0 * k3w + k4w);
        float phiNext = phi + h;
        // 4. 円盤（赤道面）との交差。
        float3 xNext = cos(phiNext) * axisA + sin(phiNext) * axisB;
        float side = dot(xNext, diskNormal);
        if (side * previousSide < 0.0)
        {
            float f = previousSide / (previousSide - side);
            float uc = lerp(u, uNext, f);
            float rc = 1.0 / max(uc, 1e-6);
            if (rc > rin && rc < rout)
            {
                float phic = lerp(phi, phiNext, f);
                float3 xc = normalize(cos(phic) * axisA + sin(phic) * axisB);
                float profile = pow(rc / rin, -0.75) * pow(saturate(1.0 - sqrt(rin / rc)), 0.25) / 0.488;
                float ut = rsqrt(max(1.0 - 1.5 / rc, 1e-3));
                float omega = sqrt(0.5 / (rc * rc * rc));
                float angularMomentum = impact * dot(hhat, diskNormal);
                float g = gObserver / max(ut * (1.0 - omega * angularMomentum), 1e-3);
                float3 e1 = _CosmicPlungeDiskX.xyz, e2 = cross(diskNormal, e1);
                float angle = atan2(dot(xc, e2), dot(xc, e1));
                float trail = CosmicPlungeTrail(angle, rc, t, pixelAngle * rc * 4.0);
                float edge = smoothstep(rout, rout * 0.85, rc) * smoothstep(rin, rin * 1.06, rc);
                float3 color = CosmicBlackbodyChroma(_CosmicPlungeParams.x * profile * g) * pow(saturate(profile), 3.0) * pow(clamp(g, 0.0, 6.0), 4.0) * trail * edge;
                return color * _CosmicPlungeParams.y;
            }
        }
        previousSide = side;
        u = uNext; w = wNext; phi = phiNext;
    }
    return 0;
}

half4 CosmicPlungeFragment(CosmicPlungeVaryings input) : SV_Target
{
    float2 pixel = input.positionCS.xy / _ScaledScreenParams.xy;
    // 画面の上下: 左上が原点の API（D3D など）で、投影が反転している描画先（中間のテクスチャ、_ProjectionParams.x = −1）では、
    // 画素の行 0 が景色の下になります。左下が原点の API（OpenGL）では投影は反転しません。固定の符号にすると、Game View と撮影先の
    // どちらかで上下が逆になります（2026-10-03、レンズの場面との重ね合わせで円盤が X 字に交差した）。
#if UNITY_UV_STARTS_AT_TOP
    float flip = _ProjectionParams.x;
#else
    float flip = -_ProjectionParams.x;
#endif
    float2 ndc = float2(pixel.x * 2.0 - 1.0, (1.0 - pixel.y * 2.0) * flip);
    float tanHalf = _CosmicPlungeForward.w;
    float aspect = _ScaledScreenParams.x / _ScaledScreenParams.y;
    float3 view = normalize(_CosmicPlungeForward.xyz + _CosmicPlungeRight.xyz * (ndc.x * tanHalf * aspect) + _CosmicPlungeUp.xyz * (ndc.y * tanHalf));
    float pixelAngle = 2.0 * tanHalf / _ScaledScreenParams.y;
    float3 color = CosmicPlungeShade(view, pixelAngle) * _CosmicPlungeParams.w;
    return half4(color, saturate(_CosmicPlungeVelocity.w));
}

#endif
