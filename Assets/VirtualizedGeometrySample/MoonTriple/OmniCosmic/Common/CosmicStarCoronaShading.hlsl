// 恒星のコロナ・外層大気（CosmicStarCorona、StarCorona）。恒星と同じ中心の大きな殻に描き、視線と中心の最接近距離 b から光を求めて加算します。
// 光は縁の外へ指数的に減り、方向ごとのノイズで筋（ストリーマー）を作り、ゆっくり動かします。恒星の円板の内側には足しません。
// プロミネンス（_CosmicP2.x > 0 の恒星、太陽）: 表面の2つの足元を結ぶアーチ状のループを、弧に沿った点と視線の距離から描きます。
// ループに沿って物質が流れ、ゆっくり成長し、寿命の終わりに噴出して広がりながら消え、別の場所に現れます。
// 恒星の円板に隠れる部分（円板の奥）は描きません。
// _CosmicP0: rgb = 色、w = 強さ。_CosmicP1: x = 恒星の半径 ÷ 殻の半径、y = 減衰の長さ（恒星の半径の倍数）、z = 筋の強さ、w = 時間の倍率。
// _CosmicP2: x = プロミネンスの数（0..6）、y = ループの半径（恒星の半径の倍数）、z = 明るさ、w = 寿命（秒）。
// _CosmicP3: x = 作風（表面の作風と同じ番号。0 は従来）、y = 縁のすぐ外の光の強さ、z = その減衰の長さ（恒星の半径の倍数）、w = 炎の強さ（作風 2）。
// 作風 1〜3 は縁のすぐ外を強く光らせます（輪郭が強く輝く）。作風 2 は縁から立ち上がり外へ流れる炎の舌、作風 3 はループを金色にします
// （極端紫外線で見たループのイメージ。2026-10-06 のユーザーの判断）。
#ifndef COSMIC_STAR_CORONA_SHADING_INCLUDED
#define COSMIC_STAR_CORONA_SHADING_INCLUDED

#define COSMIC_PROMINENCE_STEPS 40

// ループ k の形（世代ごとに場所が変わる）。base = 足元の中点（恒星の表面）、along = 足元を結ぶ向き、up = 外向き、radius = ループの半径、
// lift = 噴出の度合い（0..1）、fade = 明るさの包絡線、flow = 流れの位相。
void CosmicProminence(int k, float t, float starRadius, out float3 base, out float3 along, out float3 up, out float radius, out float lift, out float fade, out float flow)
{
    float life = max(_CosmicP2.w, 10.0) * (0.75 + 0.5 * CosmicHash1(int3(k, 41, (int)_CosmicBody.z)));
    float x = t / life + CosmicHash1(int3(k, 42, (int)_CosmicBody.z));
    float generation = floor(x);
    float age = x - generation;
    float3 h = CosmicHash3(int3(k, (int)generation, (int)_CosmicBody.z + 57));
    float lat = (h.x - 0.5) * 1.9;
    float lon = h.y * 6.2831853;
    up = float3(cos(lat) * cos(lon), sin(lat), cos(lat) * sin(lon));
    float3 e1 = normalize(cross(up, abs(up.y) < 0.95 ? float3(0, 1, 0) : float3(1, 0, 0)));
    float3 e2 = cross(up, e1);
    float angle = h.z * 6.2831853;
    along = e1 * cos(angle) + e2 * sin(angle);
    base = up * starRadius;
    // 成長（0..0.2）→ 安定 → 噴出（0.8..1、ループが外へ持ち上がり広がる）。3 本に 1 本は噴出せず静かに消えます。
    bool erupts = frac(h.z * 7.31) < 0.67;
    lift = erupts ? smoothstep(0.78, 1.0, age) : 0.0;
    radius = starRadius * _CosmicP2.y * (0.6 + 0.6 * frac(h.x * 13.7)) * (0.55 + 0.45 * smoothstep(0.0, 0.2, age)) * (1.0 + 1.6 * lift);
    fade = smoothstep(0.0, 0.12, age) * (1.0 - smoothstep(erupts ? 0.88 : 0.75, 1.0, age));
    flow = t * (0.15 + 0.1 * h.x);
    if (_CosmicP3.x > 2.5)
    {
        // 作風 3（極端紫外線のイメージ）: ループがゆっくり大きく伸び縮みし、足元の向きも揺れます
        // （寿命の中の成長と噴出だけでは、ほとんど動いていないように見えた。2026-10-06 のユーザーの指摘）。
        radius *= 1.0 + 0.45 * sin(t * 0.9 + k * 2.1);
        float sway = 0.5 * sin(t * 0.35 + k * 1.3);
        along = along * cos(sway) + cross(up, along) * sin(sway);
    }
}

float3 CosmicProminences(CosmicRay ray, float starRadius, float t)
{
    int count = (int)min(_CosmicP2.x + 0.5, 6.0);
    if (count <= 0) return 0;
    // ループは細い管なので、視線に沿って歩くと太さより歩幅が大きく取りこぼします。代わりにループの弧に沿って点を置き、
    // 各点と視線の距離から光を足します（細い管の線の放射の投影）。視線から遠いループは先に除外します。
    float2 hitStar = CosmicRaySphere(ray.origin, ray.direction, starRadius);
    float3 sum = 0;
    [loop] for (int k = 0; k < count; k++)
    {
        float3 base, along, up; float radius, lift, fade, flow;
        CosmicProminence(k, t, starRadius, base, along, up, radius, lift, fade, flow);
        if (fade <= 0.0) continue;
        float height = radius * (1.0 + 0.6 * lift);
        float3 middle = base + up * height * 0.5;
        float3 toMiddle = middle - ray.origin;
        float tMiddle = dot(toMiddle, ray.direction);
        if (length(toMiddle - ray.direction * tMiddle) > radius * 1.6 + height) continue;
        float3 side = cross(up, along);
        // 管の太さ（最低でも画素の 1.2 倍。遠くからでも線として見える）。
        float pixel = ray.pixelAngle * max(tMiddle, 1e-4);
        float thickness = max(radius * (0.1 + 0.08 * lift), pixel * 1.2);
        float glow = 0;
        [loop] for (int i = 0; i < COSMIC_PROMINENCE_STEPS; i++)
        {
            float theta = (i + 0.5) / COSMIC_PROMINENCE_STEPS * 3.14159265;
            // ループはわずかにねじれ、足元から頂点へ物質が流れます（流れる筋の明暗）。
            float twist = sin(theta * 3.0 + flow * 2.0 + k) * 0.08;
            float3 p = base + along * (cos(theta) * radius) + up * (sin(theta) * height) + side * (twist * radius);
            float3 rel = p - ray.origin;
            float tp = dot(rel, ray.direction);
            if (tp < 0.0) continue;
            // 恒星の円板の奥（視線が円板に当たる点より遠い）の部分は描きません。
            if (hitStar.y > 0.0 && tp > hitStar.x) continue;
            float d = length(rel - ray.direction * tp);
            float strand = 0.55 + 0.45 * sin(theta * 14.0 - flow * 6.0 + k * 1.7) * sin(theta * 5.3 + flow * 2.1 + k * 3.1);
            glow += exp(-pow(d / thickness, 2.0)) * strand;
        }
        // 弧の点の間隔と太さで正規化した、管に沿った明るさ。
        float spacing = 3.14159265 * radius / COSMIC_PROMINENCE_STEPS;
        sum += glow * (spacing / (thickness * 1.7725)) * fade * (1.0 - 0.5 * lift);
    }
    // 色は Hα の赤（作風 3 は極端紫外線のイメージの金色）。
    float3 loopColor = _CosmicP3.x > 2.5 ? float3(1.0, 0.72, 0.3) : float3(1.0, 0.3, 0.12);
    return loopColor * sum * _CosmicP2.z * 2.0;
}

float4 CosmicShellShade(CosmicRay ray)
{
    float starRadius = 0.5 * _CosmicP1.x;
    float t = CosmicSeconds() * _CosmicP1.w;
    float3 prominences = CosmicProminences(ray, starRadius, t);
    float tc = -dot(ray.origin, ray.direction);
    float3 closest = ray.origin + ray.direction * tc;
    float b = length(closest);
    float3 corona = 0;
    if (b >= starRadius * 0.995)
    {
        float height = (b - starRadius) / starRadius;
        float falloff = exp(-height / max(_CosmicP1.y, 1e-3));
        float edge = smoothstep(0.5, 0.42, b);
        float3 u = closest / max(b, 1e-6);
        // 方向ごとの筋（中心からの方向だけに依存するノイズを、時間でゆっくり回す）。
        float3 su = CosmicRotateY(u, t * 0.01);
        float streamer = CosmicFbm(su * 9.0 + _CosmicBody.z, 4, ray.pixelAngle * 30.0, 2.1, 0.5);
        float streak = saturate(0.75 + _CosmicP1.z * streamer * 0.8);
        corona = _CosmicP0.rgb * falloff * streak * edge * _CosmicP0.w;
        if (_CosmicP3.x > 0.5)
        {
            // 縁のすぐ外の強い光（短い減衰）。
            float rim = exp(-height / max(_CosmicP3.z, 1e-3));
            corona += _CosmicP0.rgb * rim * _CosmicP3.y * edge * (0.8 + 0.4 * streak);
        }
        if (abs(_CosmicP3.x - 2.0) < 0.5)
        {
            // 炎の舌: 中心からの向きのノイズを、高さと時間で外へずらし（縁から立ち上がって外へ流れる）、細い舌にします。
            float3 flow = su * (7.0 + height * 9.0 - t * 0.8);
            float tongue = CosmicFbm(flow + _CosmicBody.z, 4, ray.pixelAngle * 40.0, 2.1, 0.55);
            float flame = pow(saturate(tongue * 1.8 + 0.25), 2.0) * exp(-height / max(_CosmicP1.y * 0.7, 1e-3));
            float3 flameColor = lerp(float3(1.0, 0.3, 0.05), float3(1.0, 0.75, 0.35), saturate(1.0 - height * 3.0));
            corona += flameColor * flame * _CosmicP3.w * edge;
        }
    }
    float3 color = (corona + prominences) * CosmicEmission();
    return float4(color, 0);
}

#endif
