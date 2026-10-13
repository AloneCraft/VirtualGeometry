// 土星の環（CosmicSaturnRing の ForwardLit）。殻（単位直径の球、半径 0.5 = 土星の半径 × 見た目の広がり）の中で、環の面（物体の y = 0）と
// 氷の粒を光線で描きます。画像は使いません（環の動径の濃さは観測の値に基づく関数、粒は手続き）。
// ・遠景: 光線と環の面の交点の動径（土星の半径の単位）から光学的厚さ τ を求め、薄い層の一回散乱で反射（光と同じ側）と透過（逆光、前方散乱）を
//   求めます。不透明度は 1 − exp(−τ/μ)。土星の本影は交点から光源への線分と土星の球で判定します。
// ・近景: カメラが環の面のすぐ上（_CosmicRingEye.w > 0.5）のとき、面の厚さ ±H の層を一辺 c の立方体の格子に区切り、格子ごとの乱数で氷の粒
//   （半径 0.12〜0.9 m の球）を置き、光線を格子でたどって最初の粒を描きます。座標はカメラの近くの基準点からのメートル
//   （_CosmicRingEye.xyz、格子の通し番号は _CosmicRingAnchor）。物体空間の座標（単位 2.8e8 m）では粒の大きさを表せないためです。
//   氷: 拡散、なめらかな鏡面、粒ごとの小さな面（ファセット）のきらめき（時間で少し瞬く）、逆光の透け。画面で小さすぎる粒は遠景に任せます。
// _CosmicP0: x = 見た目の広がり（殻の半径 ÷ 土星の半径）、y = 環の内縁、z = A 環の外縁（土星の半径の倍数）、w = 明るさ。
// _CosmicP1: xyz = 光源の向き（物体空間）。_CosmicP2: x = 物体空間の単位の長さ（m、殻の直径）。
#ifndef COSMIC_SATURN_RING_SHADING_INCLUDED
#define COSMIC_SATURN_RING_SHADING_INCLUDED

// x = カメラの基準点からの位置（環の面の座標、m）、y = 環の面からの高さ（m）、z = 位置、w = 1 なら近景を描く。
float4 _CosmicRingEye;
// x, z = 基準点の格子の通し番号（環の面の座標）。
float4 _CosmicRingAnchor;

#define COSMIC_RING_SLAB 4.0
#define COSMIC_RING_CELL 2.0
#define COSMIC_RING_STEPS 96
// 近景で深度に使う距離の下限（物体空間の単位、約 28 万 km）。カメラの数十 m 先の点は、物体空間の float（単位 2.8 億 m）ではカメラの位置と区別できず、
// 深度がカメラの位置になって深度テストで消えていました。土星の面（約 0.25 単位先）よりずっと手前なので、環は土星の手前に描かれます。
#define COSMIC_RING_MIN_DEPTH 0.001

float CosmicRingBand(float x, float a, float b, float edge)
{
    return smoothstep(a - edge, a + edge, x) * (1.0 - smoothstep(b - edge, b + edge, x));
}

// 海王星の環（_CosmicP3.x = 1。ボイジャー 2 号の値、動径は体積平均半径 24,622 km の倍数）: ガレ 1.66〜1.74、ルヴェリエ 2.16、ラッセル 2.16〜2.32、
// アラゴ 2.32、アダムス 2.556。アダムスの弧（リベルテ・エガリテ・フラテルニテ・クラージュ）は方位角の狭い範囲だけ濃く、環と一緒にゆっくり回る
// （弧の位置と幅は概略の演出）。実際の光学的厚さ（約 1e-4〜0.1）は目に見えないので、_NEPTUNE_RING_BOOST 倍して描く（HUD に注記）。
#define COSMIC_NEPTUNE_RING_BOOST 40.0
float CosmicNeptuneRingTau(float x, float edge, float azimuth, out float3 tint)
{
    float galle = 3.0e-4 * CosmicRingBand(x, 1.661, 1.742, edge * 2.0);
    float leVerrier = 2.0e-3 * CosmicRingBand(x, 2.1604, 2.1643, max(edge, 0.0008));
    float lassell = 3.0e-4 * CosmicRingBand(x, 2.1643, 2.3232, edge);
    float arago = 1.5e-4 * CosmicRingBand(x, 2.3200, 2.3260, max(edge, 0.0008));
    // アダムス: 薄い全周の環に、4 つの弧（フラテルニテ・エガリテ・リベルテ・クラージュ）。弧は合わせて約 40° の範囲に固まり、濃い部分は全周の約 1 割
    // （ボイジャー 2 号。位置と幅は概略。方位は回転の 1 周 = 1。幅 σ は 0.012（約 4°）と 0.006（約 2°）。2026-10-08 のユーザーの指摘「リングの右側だけ
    // 半径が大きく見える」は、広すぎた弧（全周の約 4 割）が片側だけ外縁を濃く見せたため）。
    float phase = azimuth - CosmicSeconds() * 0.015;
    float arcs = 0.0;
    const float centers[4] = { 0.0, 0.04, 0.07, 0.10 };
    const float widths[4] = { 0.012, 0.006, 0.006, 0.006 };
    [unroll] for (int k = 0; k < 4; k++)
    {
        float d = frac(phase - centers[k] + 0.5) - 0.5;
        arcs = max(arcs, exp(-d * d / (2.0 * widths[k] * widths[k])));
    }
    float adams = (0.0025 + 0.06 * arcs) * CosmicRingBand(x, 2.5530, 2.5600, max(edge, 0.0006));
    float tau = (galle + leVerrier + lassell + arago + adams) * COSMIC_NEPTUNE_RING_BOOST;
    // 塵の色: 灰色がかった赤褐色（アダムスの弧はやや青白い）。
    tint = lerp(float3(0.58, 0.52, 0.46), float3(0.66, 0.70, 0.78), saturate(adams * 20.0));
    return tau;
}

// 動径 x（土星の半径の単位）の光学的厚さと、色合い。edge は画素の動径方向の大きさ（境界のちらつきを抑える）。
float CosmicRingTau(float x, float edge, float azimuth, out float3 tint)
{
    if (_CosmicP3.x > 0.5) { return CosmicNeptuneRingTau(x, edge, azimuth, tint); }
    // 細い環の重なり（リングレット）: 動径の 1 次元のノイズ。画素より細かい成分は弱めます。
    float fine = CosmicNoise(float3(x * 420.0, 1.7, 0.3));
    float fineWeight = saturate(1.0 - edge * 900.0);
    float ringlets = 1.0 + 0.45 * fine * fineWeight;
    float mid = CosmicNoise(float3(x * 60.0, 5.1, 2.9));
    float d = 0.012 * CosmicRingBand(x, 1.149, 1.279, edge);
    float c = 0.09 * (1.0 + 0.5 * mid) * CosmicRingBand(x, 1.282, 1.580, edge) * ringlets;
    float bInner = lerp(1.1, 2.6, smoothstep(1.58, 1.76, x));
    float b = bInner * (1.0 + 0.15 * mid) * CosmicRingBand(x, 1.580, 2.019, edge) * ringlets;
    float cassini = 0.10 * CosmicRingBand(x, 2.019, 2.098, edge);
    float encke = CosmicRingBand(x, 2.2912, 2.2967, max(edge, 0.0004));
    float keeler = CosmicRingBand(x, 2.3438, 2.3448, max(edge, 0.0003));
    float a = lerp(0.65, 0.45, smoothstep(2.10, 2.34, x)) * CosmicRingBand(x, 2.098, 2.3488, edge) * (1.0 - 0.95 * encke) * (1.0 - 0.8 * keeler) * ringlets;
    float f = 0.35 * CosmicRingBand(x, 2.4060, 2.4086, max(edge, 0.0006));
    float tau = d + c + b + cassini + a + f;
    float3 colorC = float3(0.50, 0.46, 0.42), colorB = float3(0.86, 0.77, 0.62), colorA = float3(0.78, 0.74, 0.68);
    tint = (c * colorC + b * colorB + (a + cassini + d + f) * colorA) / max(tau, 1e-4);
    return tau;
}

// 点 p（物体空間）が土星の本影の中か（1 = 日なた、0 = 影）。
float CosmicRingSunlit(float3 p, float3 lightDir)
{
    float planet = 0.5 / max(_CosmicP0.x, 1.0);
    float along = dot(p, lightDir);
    if (along >= 0.0) return 1.0;
    float miss = length(p - lightDir * along);
    return smoothstep(planet * 0.97, planet * 1.03, miss);
}

// 遠景の環（面の交点）。premultiplied の色と、当たった距離（物体空間、当たらなければ −1）。
float4 CosmicRingFar(float3 origin, float3 direction, float pixelAngle, out float hitDistance)
{
    hitDistance = -1.0;
    if (abs(direction.y) < 1e-6) return 0;
    float t = -origin.y / direction.y;
    if (t <= 0.0) return 0;
    float3 p = origin + direction * t;
    float toRadii = 2.0 * _CosmicP0.x;
    float x = length(p.xz) * toRadii;
    // 土星は F 環（2.41）まで、海王星はメッシュの外縁（アダムス環）まで。
    if (x < _CosmicP0.y || x > (_CosmicP3.x > 0.5 ? _CosmicP0.z + 0.01 : 2.42)) return 0;
    float mu = abs(direction.y);
    float edge = max(pixelAngle * t * toRadii / max(mu, 0.02), 1e-5);
    float3 tint;
    float tau = CosmicRingTau(x, edge, atan2(p.z, p.x) * 0.15915494 + 0.5, tint);
    if (tau <= 1e-5) return 0;
    float3 lightDir = normalize(_CosmicP1.xyz);
    float mu0 = max(abs(lightDir.y), 0.02);
    bool lit = sign(lightDir.y) == sign(origin.y);
    float intensity;
    if (lit)
    {
        // 光と同じ側から見る（反射）。後方散乱でやや明るい（塵の環は後方へほとんど散らさない）。
        intensity = mu0 / (mu0 + mu) * (1.0 - exp(-tau * (1.0 / mu + 1.0 / mu0))) * (_CosmicP3.x > 0.5 ? 0.5 : 1.6);
    }
    else
    {
        // 逆光（透過）。薄い所ほど光が抜け、前方散乱で光源の近くが明るい。
        float transmitted = abs(exp(-tau / mu0) - exp(-tau / mu)) * mu0 / max(abs(mu0 - mu), 1e-3);
        // 塵の環（海王星）は前方散乱が強く、逆光の光源の近くが特に明るい。
        float forward = _CosmicP3.x > 0.5 ? 6.0 * pow(saturate(dot(direction, lightDir)), 6.0) : 2.0 * pow(saturate(dot(direction, lightDir)), 4.0);
        intensity = transmitted * (0.6 + forward);
    }
    float sunlit = CosmicRingSunlit(p, lightDir);
    float alpha = 1.0 - exp(-tau / mu);
    float3 color = tint * intensity * sunlit * _CosmicKeyLight.w * _CosmicP0.w;
    hitDistance = t;
    return float4(color, alpha);
}

// 近景の氷の粒。当たれば true（color は不透明の色、distanceMeters はカメラからの距離 m）。marchEnd はたどり終えた距離。
bool CosmicRingNear(float3 eye, float3 direction, float pixelAngle, out float3 color, out float distanceMeters, out float marchEnd)
{
    color = 0;
    distanceMeters = 0;
    marchEnd = 0;
    if (direction.y >= -1e-4 || eye.y <= COSMIC_RING_SLAB) return false;
    float tEnter = (eye.y - COSMIC_RING_SLAB) / -direction.y;
    float tExit = (eye.y + COSMIC_RING_SLAB) / -direction.y;
    float3 p = eye + direction * (tEnter + 1e-3);
    float c = COSMIC_RING_CELL;
    int3 cell = int3(floor(p / c));
    int3 stepDir = int3(direction.x > 0 ? 1 : -1, direction.y > 0 ? 1 : -1, direction.z > 0 ? 1 : -1);
    float3 inv = 1.0 / max(abs(direction), 1e-6);
    float3 next = (float3(cell) + max(float3(stepDir), 0)) * c;
    float3 tMax = tEnter + abs(next - p) * inv;
    float3 tDelta = c * inv;
    float t = tEnter;
    int2 anchor = int2(_CosmicRingAnchor.x, _CosmicRingAnchor.z);
    float3 lightDir = normalize(_CosmicP1.xyz);
    [loop] for (int i = 0; i < COSMIC_RING_STEPS; i++)
    {
        if (t > tExit) break;
        int3 global = int3(cell.x + anchor.x, cell.y, cell.z + anchor.y);
        float3 h = CosmicHash3(global);
        if (h.x < 0.16)
        {
            float3 h2 = CosmicHash3(global + int3(7, 131, 17));
            float radius = 0.10 + 0.50 * h.y * h.y * h.y;
            float room = max(c * 0.5 - radius, 0.0);
            float3 center = (float3(cell) + 0.5) * c + (h2 - 0.5) * 2.0 * room;
            float2 hit = CosmicRaySphere(eye - center, direction, radius);
            // 画面で小さすぎる粒（1 画素未満）は描かず、遠景の帯に任せます。
            // CosmicRaySphere は外れると (1, −1)（x > y）を返すので、x ≤ y を確かめる（以前は外れた格子の全部が「当たり」になり、四角い塊に見えた）。
            if (hit.x <= hit.y && hit.x > 0.0 && radius / max(hit.x * pixelAngle, 1e-6) > 0.6)
            {
                float3 position = eye + direction * hit.x;
                float3 normal = normalize(position - center);
                // 氷の塊の凸凹（3 次元ノイズ）と、小さな平らな面（ファセット）。
                float3 local = (position - center) / radius;
                normal = normalize(normal + 0.22 * float3(CosmicNoise(local * 1.7 + h * 17.0), CosmicNoise(local * 1.7 + h * 29.0 + 3.1), CosmicNoise(local * 1.7 + h * 41.0 + 7.3)));
                // 小さな平らな面（ファセット）: 粒の表面の向きを 3 次元のノイズの勾配で細かく傾けます（格子で区切ると四角い模様になった）。
                float3 fq = local * 9.0 + h * 13.0;
                float3 facet = normalize(normal + 0.6 * float3(CosmicNoise(fq) - CosmicNoise(fq + float3(0.7, 0, 0)), CosmicNoise(fq) - CosmicNoise(fq + float3(0, 0.7, 0)), CosmicNoise(fq) - CosmicNoise(fq + float3(0, 0, 0.7))) * 4.0);
                float3 ice = lerp(float3(0.94, 0.93, 0.91), float3(0.86, 0.80, 0.70), h.z * 0.6);
                float nl = dot(normal, lightDir);
                float diffuse = saturate(nl) * 0.85 + saturate(-nl) * 0.06;
                float3 view = -direction;
                float smoothSpecular = pow(saturate(dot(reflect(-lightDir, normal), view)), 40.0) * 0.5;
                float twinkle = 0.55 + 0.45 * sin(CosmicSeconds() * (2.0 + 3.0 * h2.x) + h2.y * 40.0);
                float glint = pow(saturate(dot(reflect(-lightDir, facet), view)), 90.0) * 5.0 * twinkle;
                // 衝の効果（光源と同じ側から見ると、氷の粒の影が隠れて明るく輝く）。
                float opposition = pow(saturate(dot(view, lightDir)), 10.0) * 0.5;
                float backlit = pow(saturate(dot(direction, lightDir)), 6.0) * 0.35;
                // 層の下の方の粒ほど、上の粒の影で暗い（光が上から来るとき）。
                float depthShade = lerp(0.55, 1.0, saturate((position.y + COSMIC_RING_SLAB) / (2.0 * COSMIC_RING_SLAB)));
                float sunlit = CosmicRingSunlit(float3(0, 0, 0), lightDir);
                float3 saturnshine = float3(0.06, 0.05, 0.035) * saturate(0.5 + 0.5 * normal.y);
                color = (ice * (diffuse * depthShade + backlit + opposition) + (smoothSpecular + glint) * float3(1.0, 0.98, 0.94)) * sunlit * _CosmicKeyLight.w * _CosmicP0.w
                    + ice * saturnshine;
                distanceMeters = hit.x;
                return true;
            }
        }
        // 次の格子へ。
        if (tMax.x < tMax.y && tMax.x < tMax.z) { t = tMax.x; tMax.x += tDelta.x; cell.x += stepDir.x; }
        else if (tMax.y < tMax.z) { t = tMax.y; tMax.y += tDelta.y; cell.y += stepDir.y; }
        else { t = tMax.z; tMax.z += tDelta.z; cell.z += stepDir.z; }
    }
    marchEnd = t;
    return false;
}

#if defined(COSMIC_RING_FAR_MESH)
// 遠景の輪（平らな円環のメッシュ、CosmicSaturnRingFar.shader）。主画面でも俯瞰でも同じ式で描きます（観察者の目かどうかで分けない）。
// 殻（光線を球の中で追う方式）は、俯瞰では視線を復元できず輪が出なかった（2026-10-07 のユーザーの報告）ので、輪の帯はメッシュで描きます。
// 主カメラが環の面のすぐ上のとき（_CosmicRingEye.w = 1）は、氷の粒を殻（CosmicSaturnRing.shader）が描くので、帯は粒の隙間の層として
// 距離とともに濃くなり、まだらに透けます（一様な白い床にしない。奥の土星や星が隙間から見える）。
float4 CosmicShellShade(CosmicRay ray)
{
    float hit;
    float scale = max(_CosmicP2.x, 1.0);
    bool near = _CosmicRingEye.w > 0.5;
    float3 origin = ray.origin;
    // 近景の間は、環の面からの高さを正確な値（m）に置き換えます（物体空間の位置の精度は数十 m しかない）。
    if (near) origin.y = _CosmicRingEye.y / scale;
    float4 color = CosmicRingFar(origin, ray.direction, ray.pixelAngle, hit);
    if (hit < 0.0) return 0;
    g_CosmicShellHitDistance = max(hit, COSMIC_RING_MIN_DEPTH);
    if (near)
    {
        float3 farPoint = _CosmicRingEye.xyz + ray.direction * (hit * scale);
        float patch = saturate(0.5 + 0.8 * CosmicNoise(farPoint * float3(0.05, 0.4, 0.05) + 3.0) + 0.5 * CosmicNoise(farPoint * 0.31));
        color *= smoothstep(8.0, 400.0, hit * scale) * lerp(0.35, 1.0, patch);
    }
    return color;
}
#else
// 近景の氷の粒の殻（CosmicSaturnRing.shader）。主カメラが環の面のすぐ上（_CosmicRingEye.w = 1）のときだけ粒を描きます。
// 遠景の帯は円環のメッシュ（上の COSMIC_RING_FAR_MESH）が描くので、粒に当たらない光線は何も描きません。
// 主カメラを包む殻は、俯瞰では描きません（コロナと同じ共通の規則、CosmicShellPass.hlsl の COSMIC_SHELL_HIDE_ENCLOSING_IN_OBSERVER）。
float4 CosmicShellShade(CosmicRay ray)
{
    if (_CosmicRingEye.w <= 0.5) return 0;
    float scale = max(_CosmicP2.x, 1.0);
    float3 color;
    float meters, marchEnd;
    if (!CosmicRingNear(_CosmicRingEye.xyz, ray.direction, ray.pixelAngle, color, meters, marchEnd)) return 0;
    g_CosmicShellHitDistance = max(meters / scale, COSMIC_RING_MIN_DEPTH);
    return float4(color, 1.0);
}
#endif

#endif
