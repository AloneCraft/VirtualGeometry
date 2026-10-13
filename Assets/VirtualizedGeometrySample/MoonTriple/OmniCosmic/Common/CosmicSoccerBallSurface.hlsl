// CosmicScale 冒頭のサッカーボール（CosmicPlanetLit の種類 2）。形・材質・凹凸・汚れは、画像を使わずすべて計算で作ります。
// 形: 切頂二十面体の手縫いの球（5角形 12 枚 = 正二十面体の頂点の向き、6角形 20 枚 = 正二十面体の面の中心の向き）。
//   単位球の点 q の面は、面の平面までの距離 d（辺の長さ 1 で 5角形 2.3274、6角形 2.2673）で割った dot(q, n) / d が最大の面です
//   （中心から放射状に投影した切頂二十面体の面）。縫い目は隣の面と値が等しくなる大円の弧で、そこまでの角度は値の差 ÷ 接線方向の勾配です。
// 回転: 形は球なので、模様の座標だけを時間で回します（Rz·Rx·Ry、比が無理数に近い3つの角速度）。Logical の姿勢は静止のままです。
// 凹凸: パネルの膨らみ、縫い目の溝（縫い糸の張りによる周期的なくぼみ）、皮のしぼ。画素の大きさより細かい凹凸は薄めます（ちらつき防止）。
// 汚れ: 縫い目の黒ずみ、芝と泥のしみ、黒いパネルのこすれ、埃。ボールの局所座標で作るので、ボールと一緒に回ります。
// 照明: 演出用の主光源（_CosmicKeyLight）の拡散と GGX の光沢、すぐ隣の月の面からの照り返し（拡散と光沢）、縫い目の溝の遮蔽。
// _CosmicP0: xyz = 回転の角速度（rad/s）、w = 球の半径（m。凹凸と汚れの大きさを実寸で決めます）。
// _CosmicP1: xyz = 月の向き（world）、w = 月の面の明るさ（主光源の強さに対する比。月面の表示上のアルベドと拡散の係数を含む）。
#ifndef COSMIC_SOCCER_BALL_SURFACE_INCLUDED
#define COSMIC_SOCCER_BALL_SURFACE_INCLUDED

#define COSMIC_BALL_PENTAGON_DISTANCE 2.3274384
#define COSMIC_BALL_HEXAGON_DISTANCE 2.2672839

// 32 面の向き（0〜11 = 5角形 = 正二十面体の頂点、12〜31 = 6角形 = 正二十面体の面の中心）。1つの配列にします
// （5角形と6角形を別の配列にして三項演算で選ぶと、展開したループで両方の添字が評価され、範囲外の添字のエラーになったため）。
static const float3 CosmicBallFaces[32] =
{
    float3(-0.8506508, 0.0000000, -0.5257311), float3(-0.8506508, 0.0000000, 0.5257311),
    float3(-0.5257311, -0.8506508, 0.0000000), float3(-0.5257311, 0.8506508, 0.0000000),
    float3(0.0000000, -0.5257311, -0.8506508), float3(0.0000000, -0.5257311, 0.8506508),
    float3(0.0000000, 0.5257311, -0.8506508), float3(0.0000000, 0.5257311, 0.8506508),
    float3(0.5257311, -0.8506508, 0.0000000), float3(0.5257311, 0.8506508, 0.0000000),
    float3(0.8506508, 0.0000000, -0.5257311), float3(0.8506508, 0.0000000, 0.5257311),
    float3(-0.9341724, -0.3568221, 0.0000000), float3(-0.9341724, 0.3568221, 0.0000000),
    float3(-0.5773503, -0.5773503, -0.5773503), float3(-0.5773503, -0.5773503, 0.5773503),
    float3(-0.5773503, 0.5773503, -0.5773503), float3(-0.5773503, 0.5773503, 0.5773503),
    float3(-0.3568221, 0.0000000, -0.9341724), float3(-0.3568221, 0.0000000, 0.9341724),
    float3(0.0000000, -0.9341724, -0.3568221), float3(0.0000000, -0.9341724, 0.3568221),
    float3(0.0000000, 0.9341724, -0.3568221), float3(0.0000000, 0.9341724, 0.3568221),
    float3(0.3568221, 0.0000000, -0.9341724), float3(0.3568221, 0.0000000, 0.9341724),
    float3(0.5773503, -0.5773503, -0.5773503), float3(0.5773503, -0.5773503, 0.5773503),
    float3(0.5773503, 0.5773503, -0.5773503), float3(0.5773503, 0.5773503, 0.5773503),
    float3(0.9341724, -0.3568221, 0.0000000), float3(0.9341724, 0.3568221, 0.0000000)
};

// 面 k の向き ÷ 平面までの距離。
float3 CosmicBallFace(int k)
{
    return CosmicBallFaces[k] / (k < 12 ? COSMIC_BALL_PENTAGON_DISTANCE : COSMIC_BALL_HEXAGON_DISTANCE);
}

struct CosmicBallPanel
{
    int face;
    // 最も近い2本の縫い目までの角度（rad）と、そこから面の内側へ向かう接線の向き。
    float seam1, seam2;
    float3 inward1, inward2;
    // 最も近い縫い目に沿った角度（rad、縫い目の中点が 0）。
    float along1;
};

CosmicBallPanel CosmicBallFindPanel(float3 q)
{
    CosmicBallPanel panel;
    float best = -1.0;
    panel.face = 0;
    [loop] for (int k = 0; k < 32; k++)
    {
        float f = dot(q, CosmicBallFace(k));
        if (f > best) { best = f; panel.face = k; }
    }
    float3 own = CosmicBallFace(panel.face);
    panel.seam1 = 1e4; panel.seam2 = 1e4;
    panel.inward1 = float3(0, 0, 0); panel.inward2 = float3(0, 0, 0);
    int other = panel.face;
    [loop] for (int j = 0; j < 32; j++)
    {
        if (j == panel.face) { continue; }
        float3 g = own - CosmicBallFace(j);
        float3 tangent = g - dot(g, q) * q;
        float slope = length(tangent);
        if (slope < 1e-6) { continue; }
        float angle = dot(q, g) / slope;
        if (angle < panel.seam1)
        {
            panel.seam2 = panel.seam1; panel.inward2 = panel.inward1;
            panel.seam1 = angle; panel.inward1 = tangent / slope; other = j;
        }
        else if (angle < panel.seam2)
        {
            panel.seam2 = angle; panel.inward2 = tangent / slope;
        }
    }
    // 縫い目の大円の面の法線 m と、縫い目の中点の向き（2つの面の中心の中間）を基準に、縫い目に沿った角度を求めます。
    float3 m = normalize(own - CosmicBallFace(other));
    float3 middle = normalize(own) + normalize(CosmicBallFace(other));
    float3 axis1 = normalize(middle - dot(middle, m) * m);
    float3 axis2 = cross(m, axis1);
    panel.along1 = atan2(dot(q, axis2), dot(q, axis1));
    return panel;
}

// 模様の座標の回転（物体 → 模様は転置）。
float3x3 CosmicBallRotation(float t)
{
    float3 w = _CosmicP0.xyz * t;
    float3 c = cos(w), sn = sin(w);
    float3x3 rx = float3x3(1, 0, 0, 0, c.x, -sn.x, 0, sn.x, c.x);
    float3x3 ry = float3x3(c.y, 0, sn.y, 0, 1, 0, -sn.y, 0, c.y);
    float3x3 rz = float3x3(c.z, -sn.z, 0, sn.z, c.z, 0, 0, 0, 1);
    return mul(rz, mul(rx, ry));
}

float3 CosmicBallObjectNormalToWorld(float3 normalOS)
{
    return normalize(OmniRadialUsesLogicalTransform() ? OmniRadialObjectToLogicalNormal(normalOS) : TransformObjectToWorldNormal(normalOS));
}

// 皮のしぼの高さ（m）。position は球の表面の点（m、模様の座標）。
float CosmicBallGrain(float3 position)
{
    float2 cell = CosmicWorley(position / 0.0011, 0.0, 0.0);
    return 0.00003 * smoothstep(0.0, 0.35, cell.y - cell.x);
}

float3 CosmicSoccerBallColor(CosmicSurface s)
{
    float radius = max(_CosmicP0.w, 1e-4);
    float3x3 rotation = CosmicBallRotation(CosmicSeconds());
    float3 q = normalize(mul(transpose(rotation), normalize(s.unitPosition)));
    float pixel = s.footprint * radius;
    CosmicBallPanel panel = CosmicBallFindPanel(q);
    bool pentagon = panel.face < 12;
    float d1 = panel.seam1 * radius, d2 = panel.seam2 * radius;

    // 凹凸: 溝（縫い糸の張りで 6.5 mm ごとに深さが変わる）とパネルの膨らみ。傾き dh/dd を縫い目ごとに足します。
    const float grooveWidth = 0.0011, puffHeight = 0.0007, puffLength = 0.006, stitchSpacing = 0.0065;
    float along = panel.along1 * radius;
    float pucker = 0.5 + 0.5 * cos(6.2831853 * along / stitchSpacing);
    float grooveDepth = 0.0008 * (0.8 + 0.4 * pucker);
    float grooveWeight = CosmicOctaveWeight(0.5 / grooveWidth, pixel);
    float puffWeight = CosmicOctaveWeight(0.5 / puffLength, pixel);
    float e1 = exp(-(d1 / grooveWidth) * (d1 / grooveWidth)), e2 = exp(-(d2 / grooveWidth) * (d2 / grooveWidth));
    float slope1 = grooveWeight * grooveDepth * 2.0 * d1 / (grooveWidth * grooveWidth) * e1 + puffWeight * puffHeight / puffLength * exp(-d1 / puffLength);
    float slope2 = grooveWeight * grooveDepth * 2.0 * d2 / (grooveWidth * grooveWidth) * e2 + puffWeight * puffHeight / puffLength * exp(-d2 / puffLength);
    float3 normalQ = q - slope1 * panel.inward1 - slope2 * panel.inward2;
    // 皮のしぼ（約 1 mm の粒）。高さの差分から接線方向の傾きを求めます。
    float grainWeight = CosmicOctaveWeight(1.0 / 0.0011, pixel);
    if (grainWeight > 0.0)
    {
        float3 position = q * radius;
        const float delta = 0.00012;
        float h0 = CosmicBallGrain(position);
        float3 gradient = float3(
            CosmicBallGrain(position + float3(delta, 0, 0)) - h0,
            CosmicBallGrain(position + float3(0, delta, 0)) - h0,
            CosmicBallGrain(position + float3(0, 0, delta)) - h0) / delta;
        gradient -= dot(gradient, q) * q;
        normalQ -= grainWeight * gradient;
    }
    float3 normalWS = CosmicBallObjectNormalToWorld(mul(rotation, normalize(normalQ)));

    // 汚れ（模様の座標で作るので、ボールと一緒に回ります）。
    float dust = CosmicFbm(q * 3.0 + 11.0, 4, s.footprint * 3.0, 2.0, 0.5);
    float grass = smoothstep(0.18, 0.55, CosmicFbm(q * 2.3 + 37.0, 5, s.footprint * 2.3, 2.1, 0.55))
        * smoothstep(-0.1, 0.45, CosmicFbm(q * 11.0 + 5.0, 4, s.footprint * 11.0, 2.0, 0.5));
    float mud = smoothstep(0.3, 0.7, CosmicFbm(q * float3(16.0, 3.5, 16.0) + 71.0, 4, s.footprint * 16.0, 2.0, 0.5))
        * smoothstep(0.0, 0.5, CosmicFbm(q * 1.7 + 23.0, 3, s.footprint * 1.7, 2.0, 0.5));
    float scuff = smoothstep(0.45, 0.8, CosmicFbm(q * 20.0 + 3.0, 4, s.footprint * 20.0, 2.0, 0.5));
    float grime = exp(-(d1 / 0.0017) * (d1 / 0.0017));

    float3 albedo = pentagon ? float3(0.035, 0.035, 0.04) : float3(0.84, 0.83, 0.79);
    albedo = lerp(albedo, float3(0.14, 0.14, 0.14), pentagon ? scuff * 0.45 : 0.0);
    albedo *= 1.0 - 0.07 * saturate(dust * 0.5 + 0.5);
    albedo = lerp(albedo, float3(0.33, 0.41, 0.16), pentagon ? 0.0 : grass * 0.42);
    albedo = lerp(albedo, float3(0.41, 0.32, 0.21), mud * (pentagon ? 0.2 : 0.38));
    albedo = lerp(albedo, float3(0.17, 0.15, 0.12), grime * 0.6);
    float roughness = (pentagon ? 0.3 : 0.36) + 0.3 * grime + 0.25 * mud + 0.2 * scuff * (pentagon ? 1.0 : 0.3) + 0.08 * saturate(dust * 0.5 + 0.5);
    roughness = saturate(roughness);
    float occlusion = 1.0 - 0.55 * grooveWeight * e1;

    // 照明: 主光源の拡散と GGX の光沢（主光源の強さ w は他の天体と同じく「アルベド × 強さ × cos」の規約）。
    float3 lightDir = normalize(_CosmicKeyLight.xyz);
    float intensity = _CosmicKeyLight.w;
    float3 viewDir = s.viewWS;
    float nl = saturate(dot(normalWS, lightDir));
    float nv = max(dot(normalWS, viewDir), 1e-4);
    float3 halfVector = normalize(lightDir + viewDir);
    float nh = saturate(dot(normalWS, halfVector));
    float alpha = max(roughness * roughness, 0.002);
    float a2 = alpha * alpha;
    float denominator = nh * nh * (a2 - 1.0) + 1.0;
    float distribution = a2 / (COSMIC_PI * denominator * denominator);
    float smith = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    float visibility = 1.0 / ((nl * (1.0 - smith) + smith) * (nv * (1.0 - smith) + smith) * 4.0);
    const float f0 = 0.045;
    float fresnel = f0 + (1.0 - f0) * pow(1.0 - saturate(dot(halfVector, viewDir)), 5.0);
    float specular = distribution * visibility * fresnel * COSMIC_PI * nl * intensity;
    float3 color = albedo * intensity * nl * (1.0 - fresnel) + specular;

    // 隣の月の面からの照り返し: 月は片側の空の半分を覆う明るい灰色の壁です（拡散は (1 + cos) / 2 の見込み、光沢は反射の向きで）。
    float3 moonDir = normalize(_CosmicP1.xyz);
    float moonRadiance = _CosmicP1.w * intensity * saturate(dot(-moonDir, lightDir));
    color += albedo * moonRadiance * 0.5 * (1.0 + dot(normalWS, moonDir)) * occlusion;
    float3 reflected = reflect(-viewDir, normalWS);
    float spread = 0.25 + 0.6 * roughness;
    float cover = smoothstep(-spread, spread, dot(reflected, moonDir));
    float fresnelView = f0 + (1.0 - f0) * pow(1.0 - nv, 5.0);
    color += moonRadiance * cover * fresnelView * (1.0 - 0.6 * roughness) * occlusion;
    // 星明かり程度の環境光。
    color += albedo * 0.006 * occlusion;
    color *= lerp(1.0, occlusion, 0.5);

    // 赤道の線(MoonTriple の検証用。Omni の元のシェーダーには無い追加): ボールの中心を通る水平な帯。
    // 物体の y 軸は world の上向き(ボールは回転しない)。ボールの半分を月の面に埋めたとき、この線と面の境界のずれが、面の高さの誤差になる。
    // _CosmicP2.x = 帯の半幅(m。0 なら描かない)、yzw = 色。模様の回転の影響を受けない(物体の座標 s.unitPosition を使う)。
    float lineHalf = _CosmicP2.x;
    if (lineHalf > 0.0)
    {
        float height = abs(s.unitPosition.y) * radius;
        float edge = max(pixel, 1e-5);
        float band = 1.0 - smoothstep(lineHalf - edge, lineHalf + edge, height);
        color = lerp(color, _CosmicP2.yzw * max(intensity, 1.0), band);
    }
    return color;
}

#endif
