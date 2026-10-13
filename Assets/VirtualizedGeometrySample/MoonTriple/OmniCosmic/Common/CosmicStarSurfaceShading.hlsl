// 恒星の表面（CosmicStarSurface の ForwardLit、StarSurface）。画像を使わず、温度・セルの大きさ・活動度・シードから表面を作ります。
// ・粒状斑: 動く Worley の細胞（明るい中心と暗い間隙）。2つの世代を時間でクロスフェードし、生まれては消える対流を表します。
// ・超粒状斑: 大きな細胞の明るい網目（彩層のネットワーク）。
// ・差動回転: 赤道が速く極が遅い回転を、2位相の周期リセットで描きます。
// ・黒点と白斑: 活動帯（±35°）にゆっくり形を変える暗い斑点と、そのまわりの明るい白斑（周縁で強い）。
// ・フレア: シードで決まる時刻・位置に、磁力線のループの束が伸びながら明るくなり、ゆっくり消えます。
// ・周縁減光: 温度が低いほど強く、周縁で赤みを帯びます。
// 構造の寿命は細胞の大きさに比例させます（大きな恒星ほど重々しくゆっくり変化し、大きさの体感を強める）。
// 画素の大きさ（footprint）より細かい階層は平均へ溶かすので、遠くから見ても、ちらつかずに表面がゆっくり活動して見えます。
// _CosmicP0: rgb = 光球の色（C# が温度から黒体の色を求めた線形 RGB、輝度 1）、w = 明るさ。
// _CosmicP1: x = 粒状斑の数（赤道1周あたり）、y = 明暗のコントラスト、z = 周縁減光の係数、w = 活動度（0..1）。
// _CosmicP2: x = 超粒状斑の数（赤道1周あたり）、y = 時間の倍率（大きい恒星ほど小さい）、z = 差動回転の強さ、w = 自転の表示周期（秒）。
// _CosmicP3: x = 色の幅と円板の活動領域の強さ（0 = 黒体の単色、太陽は 1）、y = 作風（下の「作風」）、z = 縁の輝き、w = 模様の動きの速さ（0 は 1 倍）。
#ifndef COSMIC_STAR_SURFACE_SHADING_INCLUDED
#define COSMIC_STAR_SURFACE_SHADING_INCLUDED

// 対流セルの明るさ（0..1）。cells は赤道1周あたりの数、time は細胞の時間。
float CosmicConvection(float3 q, float cells, float time, float footprint, float seed)
{
    float frequency = cells / 6.2831853;
    float weight = CosmicOctaveWeight(frequency, footprint * 0.7);
    if (weight <= 0.0) return 0.5;
    // 2つの世代を時間でクロスフェード（細胞の誕生と消滅）。
    float generationTime = time / 6.0;
    float g = floor(generationTime);
    float a = generationTime - g;
    float3 offsetA = (CosmicHash3(int3((int)g, 5, (int)seed)) - 0.5) * 7.0;
    float3 offsetB = (CosmicHash3(int3((int)g + 1, 5, (int)seed)) - 0.5) * 7.0;
    float2 wa = CosmicWorley(q * frequency + offsetA, time, 1.0);
    float2 wb = CosmicWorley(q * frequency + offsetB, time, 1.0);
    // 細胞の中心が明るく盛り上がり、境界（F2−F1 が小さい）がなだらかに暗い（ひび割れのような細い線にしない）。
    float cellA = smoothstep(0.0, 0.7, wa.y - wa.x) * (1.0 - 0.45 * wa.x * wa.x);
    float cellB = smoothstep(0.0, 0.7, wb.y - wb.x) * (1.0 - 0.45 * wb.x * wb.x);
    float value = lerp(cellA, cellB, smoothstep(0.0, 1.0, a));
    return lerp(0.5, value, weight);
}

// ── 作風（_CosmicP3.y、2026-10-06 のユーザーの判断。星ごとに 1 つ） ──
// 0: 従来の光球（粒状斑・網目・黒点・フレア、周縁減光）。CosmicHierarchy の通過する恒星はこのまま。
// 1: 縁が強く光る球（太陽・アルデバラン・デネブ）。従来の光球に、細かく沸き立つ粒の稜線と、縁の輝きを足します。
// 2: 金属箔のようなしわ・炎の表面（ベテルギウス・Stephenson 2-18）。何重にもゆがめたノイズの稜線（時間で流れる）をしわにし、
//    画面に固定した向きの明暗（光源の反射ではない）で浮き彫りにして、巨大な対流セルの明暗と炎の色（深紅〜橙〜黄白）を重ねます。
// 3: 極端紫外線で見た姿のイメージ（ポルックス。演出）。暗い銅色の地に、星と一緒に回る活動領域の扇形の筋とループ、暗いフィラメント、
//    ときどき光る青紫〜白のフレア。縁は明るくなります（光学的に薄いコロナの縁の増光）。
// 縁の輝き（_CosmicP3.z）: 円板の縁の近くを (1 − μ) の高い冪で明るくします。模様の動きの速さ（_CosmicP3.w、0 は 1 倍）は時間の倍率に掛けます
// （大きな恒星の時間の倍率は小さく、段の 6 秒の間に模様がほとんど動かなかったため）。重さを抑えるため、作風 1〜3 の細かいノイズは
// 3 次元テクスチャ（CosmicFbmTex）で読み、俯瞰（観察者の目）では段数を減らします。

// 時間（秒 × 時間の倍率 × 模様の動きの速さ）。
float CosmicStarTime()
{
    float speed = _CosmicP3.w > 0.0 ? _CosmicP3.w : 1.0;
    return CosmicSeconds() * _CosmicP2.y * speed;
}

// 縁の輝き（0..）: 縁へ向かう広い光と、縁のすぐ内側の細い光。
float CosmicStarRimGlow(float mu)
{
    float edge = 1.0 - mu;
    return _CosmicP3.z * (0.55 * pow(edge, 3.0) + 0.9 * pow(edge, 10.0));
}

// 作風 1 の細かく沸き立つ粒（平均 0 の明暗、−1..1 程度）。粒状斑の約 2.5 倍の細かさの稜線を、時間でゆっくり流します。
// 稜線の明るさは平均が 0.5 を大きく超えるので（足すと円板全体が明るくなり、黄白へ寄って色が褪せた。2026-10-06 の撮影）、
// ずらした 2 か所の稜線の差にして平均を 0 にします（明るい筋と暗い筋が混じる）。
float CosmicStarRidge(float3 p, float footprint)
{
    float n = clamp(CosmicFbmTex(p, CosmicIsObserverPass() ? 2 : 3, footprint, 2.1, 0.5) * 2.5, -1.0, 1.0);
    float ridge = 1.0 - abs(n);
    return ridge * ridge;
}

float CosmicStarBoil(float3 q, float cells, float t, float footprint, float seed)
{
    float frequency = cells * 2.5 / 6.2831853;
    float3 p = q * frequency + float3(seed * 0.37, t * 0.06, -t * 0.04);
    return CosmicStarRidge(p, footprint * frequency) - CosmicStarRidge(p * 1.07 + float3(31.7, -12.3, 7.9), footprint * frequency * 1.07);
}

// 作風 2: 金属箔のようなしわと炎の表面。
float3 CosmicCrumpledSurface(CosmicSurface s)
{
    float t = CosmicStarTime();
    float seed = _CosmicBody.z;
    float3 q0 = normalize(s.unitPosition);
    float rotationPeriod = max(_CosmicP2.w, 1.0);
    float3 q = CosmicRotateY(q0, -t / rotationPeriod * 6.2831853);
    float footprint = s.footprint;
    int octaves = CosmicIsObserverPass() ? 3 : 5;
    // しわの大きさ: 巨大な対流セル（_CosmicP1.x 個）の約 1/3。
    float scale = max(_CosmicP1.x, 1.0) * 0.35;
    float3 p = q * scale + seed * 0.13;
    float fp = footprint * scale;
    // 2 段のドメインワープ（時間で流れる）。
    float3 w1 = float3(CosmicFbmTex(p + float3(0.0, t * 0.05, 0.0), 3, fp, 2.0, 0.5),
                       CosmicFbmTex(p + float3(5.2, 1.3, -t * 0.05), 3, fp, 2.0, 0.5),
                       CosmicFbmTex(p + float3(-2.7, t * 0.04, 8.1), 3, fp, 2.0, 0.5));
    float3 p2 = p * 1.7 + 1.6 * w1;
    float n = CosmicFbmTex(p2 * 1.3 + float3(t * 0.03, 0.0, 0.0), octaves, fp * 2.2, 2.05, 0.55);
    float crumple = pow(saturate(1.0 - abs(n)), 3.0);
    // 画面に固定した向き（左上）の明暗: しわの高さの画面の傾き。1 画素あたりの変化を模様の単位へ直します。
    float slope = (ddx(crumple) - ddy(crumple)) / max(fp * 2.2 * 1.3 * 1.7 * 6.0, 1e-6);
    float emboss = clamp(slope * 0.25, -1.0, 1.0);
    // 巨大な対流セル（明るい中心・暗い間隙）。
    float cells = CosmicConvection(q, _CosmicP1.x, t, footprint, seed);
    float intensity = (0.3 + 0.95 * cells) * (0.45 + 0.85 * crumple) + 0.4 * emboss * (0.3 + crumple);
    intensity = lerp(1.0, intensity, _CosmicP1.y);
    float mu = saturate(dot(s.normalWS, s.viewWS));
    float limb = saturate(1.0 - 0.5 * _CosmicP1.z * (1.0 - mu));
    // 炎の色: 深紅 → 橙 → 黄白。表示の色（_CosmicP0.rgb）を少し混ぜます。
    float heat = saturate(intensity * 0.75);
    float3 deep = float3(0.5, 0.07, 0.02), mid = float3(1.0, 0.36, 0.07), hot = float3(1.0, 0.72, 0.32), white = float3(1.0, 0.93, 0.78);
    float3 ramp = heat < 0.45 ? lerp(deep, mid, heat / 0.45) : heat < 0.8 ? lerp(mid, hot, (heat - 0.45) / 0.35) : lerp(hot, white, (heat - 0.8) / 0.2);
    float3 color = lerp(ramp, ramp * _CosmicP0.rgb * 1.3, 0.3) * (0.35 + 0.9 * intensity) * limb;
    color += hot * CosmicStarRimGlow(mu) * (0.6 + 0.8 * crumple);
    return color * _CosmicP0.w * CosmicEmission();
}

// 作風 3: 極端紫外線で見た姿のイメージ。
float3 CosmicEuvSurface(CosmicSurface s)
{
    float t = CosmicStarTime();
    float seed = _CosmicBody.z;
    float3 q0 = normalize(s.unitPosition);
    float rotationPeriod = max(_CosmicP2.w, 1.0);
    float3 q = CosmicRotateY(q0, -t / rotationPeriod * 6.2831853);
    float footprint = s.footprint;
    bool observer = CosmicIsObserverPass();
    // 地: 網目の明暗と、大きな暗い領域（コロナホール）。
    float mottle = CosmicFbmTex(q * 6.0 + float3(seed, t * 0.02, 0.0), observer ? 2 : 4, footprint * 6.0, 2.0, 0.55);
    float holes = smoothstep(0.1, 0.45, CosmicFbmTex(q * 1.3 + seed + 7.0, 2, footprint * 1.3, 2.0, 0.5));
    float level = (0.14 + 0.12 * mottle) * (1.0 - 0.7 * holes);
    // 暗いフィラメント: 細く曲がりくねった暗い筋。時間で変わるゆがみで、筋がうねりながら流れます
    // （ゆっくり流すだけでは止まって見えた。2026-10-06 のユーザーの指摘「表面の傷のようなものもアニメーション」）。
    float3 writhe = float3(CosmicFbmTex(q * 1.6 + float3(0.0, t * 0.06, seed), 2, footprint * 1.6, 2.0, 0.5),
                           CosmicFbmTex(q * 1.6 + float3(7.3, -t * 0.05, seed + 3.0), 2, footprint * 1.6, 2.0, 0.5),
                           0.0);
    float threads = abs(CosmicFbmTex(q * 3.5 + writhe * 0.9 + float3(seed + 21.0, 0.0, t * 0.05), 3, footprint * 3.5, 2.0, 0.5));
    float filament = (1.0 - smoothstep(0.012, 0.045, threads)) * smoothstep(0.05, 0.35, CosmicFbmTex(q * 1.1 + seed + 3.0, 2, footprint, 2.0, 0.5) + 0.2);
    float active = 0, fans = 0, loops = 0, flare = 0;
    [loop] for (int k = 0; k < 5; k++)
    {
        float3 h = CosmicHash3(int3(k, 61, (int)seed));
        float lat = (h.x - 0.5) * 1.1;
        float lon = h.y * 6.2831853;
        float3 c = float3(cos(lat) * cos(lon), sin(lat), cos(lat) * sin(lon));
        if (dot(q, c) < 0.75) continue;
        float3 e1 = normalize(cross(c, abs(c.y) < 0.95 ? float3(0, 1, 0) : float3(1, 0, 0)));
        float3 e2 = cross(c, e1);
        float2 d = float2(dot(q - c, e1), dot(q - c, e2));
        float r = length(d);
        float size = 0.12 + 0.08 * h.z;
        active += exp(-r * r / (size * size * 0.12));
        // 扇形の筋: 中心から放射状の細い筋（ノイズで揺らし、ちらつく）。
        float angle = atan2(d.y, d.x);
        // 筋はゆっくり回り、ゆらぎも時間で変わります（ちらつく扇）。
        float wobble = CosmicNoiseTex(float3(d * 18.0, k + seed + t * 0.35), footprint * 18.0);
        float streak = pow(0.5 + 0.5 * cos(angle * 14.0 + wobble * 4.0 + t * 0.15 * (h.z - 0.5)), 5.0);
        fans += streak * exp(-r / (size * 0.9)) * smoothstep(size * 0.06, size * 0.3, r);
        // ループ: 2 つの足元を結ぶ入れ子の楕円の弧。
        float spin = h.x * 6.2831853;
        float2 dr = float2(cos(spin) * d.x - sin(spin) * d.y, sin(spin) * d.x + cos(spin) * d.y) / size;
        [unroll] for (int i = 0; i < 3; i++)
        {
            // ループは脈打つように大きさが変わります。
            float a = (0.45 + 0.25 * i) * (1.0 + 0.18 * sin(t * 0.8 + k * 1.9 + i * 1.3));
            float2 e = dr / float2(a, a * 0.55);
            float ring = abs(length(e) - 1.0) * a;
            loops += exp(-pow(ring / 0.05, 2.0)) * (dr.y >= 0.0 ? 1.0 : 0.3);
        }
        // フレア: 周期ごとにどこかの活動領域で、急に光ってゆっくり消えます。
        float period = 16.0 + 14.0 * h.z;
        float x = t / period + h.x;
        float age = (x - floor(x)) * period;
        float envelope = smoothstep(0.0, 0.3, age) * exp(-age / 2.5);
        flare += envelope * exp(-r * r / (size * size * 0.04));
    }
    float intensity = level + 1.1 * active + 0.8 * fans + 0.9 * loops;
    intensity *= 1.0 - 0.75 * filament;
    // 色: 銅 → 橙 → 金 → 白（極端紫外線の合成画像の配色に寄せる）。
    float x = saturate(intensity / 1.8);
    float3 copper = float3(0.4, 0.17, 0.06), orange = float3(1.0, 0.48, 0.13), gold = float3(1.0, 0.8, 0.42), white = float3(1.0, 0.97, 0.9);
    float3 color = x < 0.35 ? lerp(copper, orange, x / 0.35) : x < 0.75 ? lerp(orange, gold, (x - 0.35) / 0.4) : lerp(gold, white, (x - 0.75) / 0.25);
    color *= 0.4 + 0.9 * intensity;
    // 青紫〜白のフレア。
    color += lerp(float3(0.62, 0.32, 1.0), float3(1.0, 0.95, 1.0), saturate(flare)) * flare * 2.5;
    // 縁の増光（縁ほど光学的に薄いコロナを長く通る）。
    float mu = saturate(dot(s.normalWS, s.viewWS));
    color += gold * CosmicStarRimGlow(mu);
    return color * _CosmicP0.w * CosmicEmission();
}

// 作風 0（従来）と 1（縁が強く光る球）の光球。
float3 CosmicPhotosphere(CosmicSurface s, int style)
{
    float t = CosmicStarTime();
    float seed = _CosmicBody.z;
    float3 baseColor = _CosmicP0.rgb;
    float brightness = _CosmicP0.w * CosmicEmission();
    float3 q0 = normalize(s.unitPosition);
    float footprint = s.footprint;
    float pattern = 0;
    float spots = 0, faculae = 0;
    float rotationPeriod = max(_CosmicP2.w, 1.0);
    [unroll] for (int j = 0; j < 2; j++)
    {
        float tau, weight, epoch;
        CosmicFlowPhase(t, 40.0, j, tau, weight, epoch);
        // 自転（剛体）＋差動回転（赤道が速い）。
        float lat2 = q0.y * q0.y;
        float spin = t / rotationPeriod * 6.2831853;
        float differential = -_CosmicP2.z * lat2 * tau * 40.0 / rotationPeriod * 6.2831853;
        float3 q = CosmicRotateY(q0, -(spin + differential));
        float3 domain = q + (CosmicHash3(int3((int)epoch, j, (int)seed)) - 0.5) * 0.02;
        float granules = CosmicConvection(domain, _CosmicP1.x, t, footprint, seed);
        float network = CosmicConvection(domain * 1.0 + 3.1, _CosmicP2.x, t * 0.25, footprint, seed + 1);
        float mottle = CosmicFbm(domain * 3.0 + seed, 5, footprint * 3.0, 2.0, 0.55);
        // 白色光では超粒状斑の網目は弱く見えるので重みを小さくします（彩層の網目として薄く重ねる）。
        float value = 0.62 * granules + 0.1 * network + 0.28 * (0.5 + 0.5 * mottle);
        // 平均の明るさを変えずに細かい明暗だけを足します（混ぜると円板全体が明るくなり、トーンマップで色が褪せた）。
        if (style == 1) value += 0.3 * CosmicStarBoil(domain, _CosmicP1.x, t, footprint, seed);
        // 黒点と白斑: 活動帯に、ゆっくり形を変える低周波ノイズのしきい値で出します。
        float activeBand = exp(-pow((abs(q.y) - 0.42) / 0.16, 2.0));
        float activity = _CosmicP1.w;
        float spotNoise = CosmicNoise(domain * 9.0 + float3(0, t * 0.004, seed)) * 0.75 + CosmicNoise(domain * 23.0 + seed) * 0.25;
        float s1 = smoothstep(0.55 - 0.25 * activity, 0.75 - 0.25 * activity, spotNoise) * activeBand * activity;
        float f1 = smoothstep(0.25, 0.55, spotNoise) * activeBand * activity * (1.0 - s1);
        pattern += value * weight;
        spots += s1 * weight;
        faculae += f1 * weight;
    }
    // フレア: シードで決まる 6 個の事象。丸い光点ではなく、磁力線のループの束（入れ子の楕円の弧）として描き、
    // 弧が伸びながら明るくなり、ゆっくり消えます（丸い白い点は照らされた球の鏡面反射に見えたため）。
    float flareLoops = 0;
    [loop] for (int k = 0; k < 6; k++)
    {
        float period = 18.0 + 14.0 * CosmicHash1(int3(k, 21, (int)seed));
        float x = t / period + CosmicHash1(int3(k, 22, (int)seed));
        float generation = floor(x);
        float age = (x - generation) * period;
        float3 h = CosmicHash3(int3(k, (int)generation, (int)seed + 31));
        float lat = (h.x > 0.5 ? 1 : -1) * (0.2 + 0.35 * h.y);
        float lon = h.z * 6.2831853;
        float3 c = float3(cos(lat) * cos(lon), sin(lat), cos(lat) * sin(lon));
        if (dot(q0, c) < 0.9) continue;
        float3 e1 = normalize(cross(c, abs(c.y) < 0.95 ? float3(0, 1, 0) : float3(1, 0, 0)));
        float3 e2 = cross(c, e1);
        float size = 0.09 + 0.07 * h.y;
        float2 d2 = float2(dot(q0 - c, e1), dot(q0 - c, e2)) / size;
        float angle = h.x * 6.2831853;
        d2 = float2(cos(angle) * d2.x - sin(angle) * d2.y, sin(angle) * d2.x + cos(angle) * d2.y);
        float growth = 0.35 + 0.65 * smoothstep(0.0, 2.5, age);
        float envelope = smoothstep(0.0, 0.6, age) * exp(-age / 5.0);
        float loops = 0;
        [unroll] for (int i = 0; i < 4; i++)
        {
            float a = (0.35 + 0.22 * i) * growth;
            float2 e = d2 / float2(a, a * 0.5);
            float ring = abs(length(e) - 1.0) * a;
            loops += exp(-pow(ring / 0.035, 2.0)) * (d2.y >= 0 ? 1.0 : 0.25) * (0.7 + 0.3 * sin(i * 2.3 + h.z * 9.0));
        }
        float kernel = exp(-dot(d2, d2) / 0.03);
        flareLoops += (loops * 0.6 + kernel * 1.5) * envelope * _CosmicP1.w;
    }
    float contrast = _CosmicP1.y;
    float mu = saturate(dot(s.normalWS, s.viewWS));
    float limbCoefficient = _CosmicP1.z;
    // 二次の周縁減光 I(μ) = 1 − a(1−μ) − b(1−μ)²。
    float limb = saturate(1.0 - limbCoefficient * (1.0 - mu) - 0.25 * limbCoefficient * (1.0 - mu) * (1.0 - mu));
    float intensity = lerp(1.0, 0.45 + 1.1 * pattern, contrast);
    intensity *= 1.0 - 0.75 * spots;
    // 白斑・プラージュ: 周縁で強く、_CosmicP3.x > 0 の恒星（太陽）は円板の全体でも明るい活動領域として見せます。
    intensity += faculae * (0.35 * pow(1.0 - mu, 1.5) + 0.55 * _CosmicP3.x);
    // 色の幅（_CosmicP3.x、0 = 単色）: 暗い所ほど赤橙に、明るい所ほど黄白色に寄せます（温度の揺らぎ）。
    float heat = saturate((intensity - 0.55) / 0.9);
    float3 cool = baseColor * float3(1.0, 0.55, 0.3);
    float3 hot = lerp(baseColor, float3(1.0, 0.86, 0.45), 0.7);
    float3 tint = lerp(baseColor, lerp(cool, hot, heat), _CosmicP3.x);
    float3 color = tint * intensity * limb;
    // 周縁で赤みを帯びる（彩層の縁）。
    color *= lerp(float3(1, 1, 1), float3(1.15, 0.72, 0.5), pow(1.0 - mu, 3.0) * 0.6);
    // フレアのループは黄白色（表面の色に寄せた白）。
    color += lerp(baseColor, float3(1.0, 0.95, 0.82), 0.7) * flareLoops * limb;
    // 作風 1: 縁の輝き（星自身の色、模様の明るい所ほど強い）。白に寄せると Bloom で円板全体が白く褪せた。
    if (style == 1) color += tint * 1.25 * CosmicStarRimGlow(mu) * (0.7 + 0.6 * pattern);
    return color * brightness;
}

float3 CosmicSurfaceColor(CosmicSurface s)
{
    int style = (int)(_CosmicP3.y + 0.5);
    if (style == 2) return CosmicCrumpledSurface(s);
    if (style == 3) return CosmicEuvSurface(s);
    return CosmicPhotosphere(s, style);
}

#endif
