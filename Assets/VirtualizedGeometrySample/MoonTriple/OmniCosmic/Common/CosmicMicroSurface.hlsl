// CosmicScale の陽子から砂粒・ゴルフボールまでの表面（CosmicMicroLit の ForwardLit）。形はメッシュ（CosmicScaleMeshBuilder）、色と細かい模様は計算で作ります。
// _CosmicBody.y: 0 = ゴルフボール（ディンプルはメッシュを実際に凹ませた形。CosmicScaleMeshBuilder.CreateGolfBall）、1 = 砂粒（石英）、
//   3 = ヘモグロビン（UV の x: 0 α 鎖、1 β 鎖、2 ヘム、3 鉄）、4 = 赤血球、5 = 陽子の段の陽子、6 = 鉄 56 の原子核の模型（UV の x: 0 陽子、1 中性子）。
//   鉄原子（2）は面を持たない印（CosmicMicroMarks）で描きます。
// 照明は演出用の主光源（_CosmicKeyLight）の拡散と GGX の光沢、弱い環境の照り返し（URP のライトは使いません）。
// _CosmicP1: x = 環境の明るさ（拡散の下地、主光源の強さに対する比）。
// _CosmicP2: x = 図解のマットな陰影にする度合い（0..1）。1 では光学的な反射（鏡面・フレネル）を持たず、縁が明るい（原子と分子の図解）。
#ifndef COSMIC_MICRO_SURFACE_INCLUDED
#define COSMIC_MICRO_SURFACE_INCLUDED

#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicApparentSize.hlsl"

// ゴルフボールのカラーマップ（印刷と塗装のむら）とノーマルマップ（塗装のざらつき）。正距円筒（CosmicScaleTextureBuilder）。
TEXTURE2D(_BaseMap);  SAMPLER(sampler_BaseMap);
TEXTURE2D(_BumpMap);  SAMPLER(sampler_BumpMap);

#ifndef COSMIC_SPIN_AXIS
// 回転（_CosmicP3.x = 角速度 rad/s）の軸。CosmicOpaquePass.hlsl と同じ値（こちらが先に読まれるので、ここでも定義します）。
#define COSMIC_SPIN_AXIS float3(0.2588190, 0.9659258, 0.0)
#endif

// 照明。diffuse は拡散の色（金属は 0）、f0 は垂直入射の反射率（金属は色付き）。fill は主光源に依らない拡散の下地（環境）の強さ。
float3 CosmicMicroShade(float3 diffuse, float3 f0, float roughness, float3 normalWS, float3 viewWS, float occlusion, float fill)
{
    // 電子顕微鏡の像（_CosmicP2.x）: 反射を持たせず、金属も拡散の色にする。
    float matte = _CosmicP2.x;
    diffuse = diffuse + f0 * matte;
    f0 = f0 * (1.0 - matte);
    roughness = lerp(roughness, 1.0, matte);
    float3 lightDir = normalize(_CosmicKeyLight.xyz);
    float intensity = _CosmicKeyLight.w;
    float nl = saturate(dot(normalWS, lightDir));
    float nv = max(dot(normalWS, viewWS), 1e-4);
    float3 halfVector = normalize(lightDir + viewWS);
    float nh = saturate(dot(normalWS, halfVector));
    float vh = saturate(dot(halfVector, viewWS));
    float alpha = max(roughness * roughness, 0.002);
    float a2 = alpha * alpha;
    float denominator = nh * nh * (a2 - 1.0) + 1.0;
    float distribution = a2 / (COSMIC_PI * denominator * denominator);
    float smith = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    float visibility = 1.0 / ((nl * (1.0 - smith) + smith) * (nv * (1.0 - smith) + smith) * 4.0);
    float3 fresnel = f0 + (1.0 - f0) * pow(1.0 - vh, 5.0);
    float3 specular = distribution * visibility * fresnel * COSMIC_PI * nl * intensity;
    float3 color = diffuse * intensity * nl * (1.0 - fresnel) + specular;
    // 環境: 主光源の側が明るいなだらかな照り返し（拡散の下地と、粗さで広がる鏡面）。
    float3 reflected = reflect(-viewWS, normalWS);
    float glow = pow(saturate(dot(reflected, lightDir) * 0.5 + 0.5), 3.0);
    float3 fresnelView = f0 + (1.0 - f0) * pow(1.0 - nv, 5.0);
    color += fresnelView * glow * 0.22 * intensity * (1.0 - 0.7 * roughness);
    // fill: 主光源に依らない環境の明るさ。拡散の下地と、金属の一様な環境の映り込み（鏡面の反射率 × 環境）。
    color += diffuse * (0.006 + fill * intensity * (0.55 + 0.45 * dot(normalWS, lightDir)));
    color += fresnelView * fill * intensity * 0.8;
    // 電子顕微鏡の像の縁: 面が視線に斜めな所ほど二次電子が増えて明るい。
    color += diffuse * pow(1.0 - nv, 2.5) * 0.55 * matte * intensity;
    // 疑似カラーが ACES で白く褪せないよう、電子顕微鏡の像は少し暗くします。
    return color * lerp(1.0, occlusion, 0.7) * lerp(1.0, 0.68, matte);
}

// ── ゴルフボール ──
// メッシュ（CosmicScaleMeshBuilder.GolfDimples、GolfDimpleAngle）と同じ値。凹みの形と法線はメッシュが持ち、ここではくぼみの底の暗さに使います。
#define COSMIC_GOLF_DIMPLES 336.0
#define COSMIC_GOLF_ANGLE 0.092

float3 CosmicFibonacciPoint(float index, float count)
{
    float z = 1.0 - (2.0 * index + 1.0) / count;
    float r = sqrt(max(0.0, 1.0 - z * z));
    float phi = index * 2.3999632;
    return float3(r * cos(phi), z, r * sin(phi));
}

// 方向 q に最も近いディンプルの中心（球面フィボナッチ格子 336 点）と、その cos。
void CosmicGolfNearest(float3 q, out float3 center, out float cosine)
{
    float count = COSMIC_GOLF_DIMPLES;
    float first = floor((1.0 - q.y) * 0.5 * count);
    cosine = -2.0;
    center = float3(0.0, 1.0, 0.0);
    [loop] for (int k = -45; k <= 45; k++)
    {
        float index = first + k;
        if (index < 0.0 || index >= count) { continue; }
        float3 c = CosmicFibonacciPoint(index, count);
        float d = dot(q, c);
        if (d > cosine) { cosine = d; center = c; }
    }
}

// 物体空間の方向 q の正距円筒の UV（u = (atan2(z, x) + π) / 2π、v = asin(y) / π + 0.5。CosmicScaleTextureBuilder と同じ）と、
// 経度の継ぎ目でミップが粗くならないよう、継ぎ目を反対側へ移した u と比べて画面上の変化が小さい方を使います。
float2 CosmicGolfUv(float3 q)
{
    float u = (atan2(q.z, q.x) + PI) / (2.0 * PI);
    float v = asin(clamp(q.y, -1.0, 1.0)) / PI + 0.5;
    float wrapped = frac(u + 0.5) - 0.5;
    return float2(fwidth(u) <= fwidth(wrapped) + 1e-6 ? u : wrapped, v);
}

float3 CosmicGolfBallColor(CosmicSurface s)
{
    // くぼみの位置は物体空間の位置の方向から（法線はくぼみの斜面で傾くので使わない）。
    float3 q = normalize(s.unitPosition);
    float3 center;
    float cosine;
    CosmicGolfNearest(q, center, cosine);
    float angle = acos(clamp(cosine, -1.0, 1.0));
    float t = angle / COSMIC_GOLF_ANGLE;
    float inside = t < 1.0 ? 1.0 : 0.0;
    float profile = inside * (1.0 - t * t) * (1.0 - t * t);
    float weight = CosmicOctaveWeight(1.0 / (2.0 * COSMIC_GOLF_ANGLE), s.footprint);
    float3 normalWS = s.normalWS;

    // テクスチャ: 回す前の物体に貼り付いた方向 q で読みます（模様は形と一緒に回る）。
    float2 uv = CosmicGolfUv(q);
    float3 paint = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb;
    float3 bump = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv).rgb * 2.0 - 1.0;
    // 接線（東・北）を回す前の方向で作り、形と同じだけ回してから、頂点の法線を傾けます（照明は回した後の法線）。
    float3 east = normalize(float3(-q.z, 0.0, q.x) + float3(1e-6, 0.0, 0.0));
    float3 north = cross(q, east);
    float spinAngle = _CosmicP3.x != 0.0 ? CosmicSeconds() * _CosmicP3.x : 0.0;
    east = CosmicRotateAxis(east, COSMIC_SPIN_AXIS, spinAngle);
    north = CosmicRotateAxis(north, COSMIC_SPIN_AXIS, spinAngle);
    // 画面で細かすぎるときは、ざらつきを弱めます（ちらつかないように）。
    float bumpWeight = CosmicOctaveWeight(140.0, s.footprint);
    float3 normalOS = normalize(s.normalOS + (east * bump.x + north * bump.y) * bumpWeight);
    normalWS = OmniRadialUsesLogicalTransform() ? OmniRadialObjectToLogicalNormal(normalOS) : TransformObjectToWorldNormal(normalOS);

    // 汚れ（ごくわずかなくすみ）と、くぼみの底の暗さ。
    float dust = CosmicFbm(q * 4.0 + 13.0, 3, s.footprint * 4.0, 2.0, 0.5);
    float3 diffuse = float3(0.86, 0.85, 0.82) * (1.0 - 0.06 * saturate(dust * 0.5 + 0.5)) * paint;
    float occlusion = 1.0 - 0.5 * weight * profile;
    return CosmicMicroShade(diffuse, float3(0.05, 0.05, 0.05), 0.24 + 0.1 * weight * profile, normalWS, s.viewWS, occlusion, 0.0);
}

// ── 砂粒 ──
float3 CosmicSandGrainColor(CosmicSurface s)
{
    float3 p = s.unitPosition * 0.5;
    float pixel = s.footprint * 0.5;
    float seed = _CosmicBody.z;
    float mottle = CosmicFbm(p * 3.0 + seed, 4, pixel * 3.0, 2.0, 0.5);
    float fine = CosmicFbm(p * 70.0 + seed * 1.7, 3, pixel * 70.0, 2.0, 0.5);
    float stain = smoothstep(0.2, 0.6, CosmicFbm(p * 6.0 + seed * 3.1, 3, pixel * 6.0, 2.0, 0.5));
    // 石英の粒: 乳白色から淡い琥珀色。鉄分のしみは橙褐色。
    float3 diffuse = lerp(float3(0.66, 0.54, 0.38), float3(0.88, 0.78, 0.62), saturate(mottle * 0.5 + 0.5));
    diffuse = lerp(diffuse, float3(0.58, 0.34, 0.17), stain * 0.5);
    diffuse *= 1.0 - 0.16 * saturate(fine * 0.5 + 0.5);

    // 貝殻状の細かい凹凸: 3 次元の fBm の勾配（接線方向だけ）で法線を傾けます。
    float3 normalOS = normalize(s.normalOS);
    const float bumpFrequency = 14.0;
    float bumpWeight = CosmicOctaveWeight(bumpFrequency, pixel);
    if (bumpWeight > 0.0)
    {
        const float h = 0.004;
        float3 q = p * bumpFrequency;
        float f0 = CosmicFbm(q, 3, pixel * bumpFrequency, 2.0, 0.5);
        float3 gradient = float3(
            CosmicFbm(q + float3(h * bumpFrequency, 0, 0), 3, pixel * bumpFrequency, 2.0, 0.5) - f0,
            CosmicFbm(q + float3(0, h * bumpFrequency, 0), 3, pixel * bumpFrequency, 2.0, 0.5) - f0,
            CosmicFbm(q + float3(0, 0, h * bumpFrequency), 3, pixel * bumpFrequency, 2.0, 0.5) - f0) / h;
        gradient -= dot(gradient, normalOS) * normalOS;
        normalOS = normalize(normalOS - bumpWeight * 0.008 * gradient);
    }
    float3 normalWS = normalize(OmniRadialUsesLogicalTransform() ? OmniRadialObjectToLogicalNormal(normalOS) : TransformObjectToWorldNormal(normalOS));

    // 半透明に見せる補正: 裏から回り込む光（巻き付き）と、縁の暖色の透け。
    float3 lightDir = normalize(_CosmicKeyLight.xyz);
    float wrap = saturate((dot(normalWS, lightDir) + 0.45) / 1.45) - saturate(dot(normalWS, lightDir));
    float rim = pow(1.0 - saturate(dot(normalWS, s.viewWS)), 3.0);
    float3 color = CosmicMicroShade(diffuse, float3(0.045, 0.045, 0.045), 0.32, normalWS, s.viewWS, 1.0, 0.0);
    color += diffuse * _CosmicKeyLight.w * (wrap * 0.35 + rim * 0.1 * float3(1.0, 0.85, 0.6));
    return color;
}

// ── ヘモグロビン（4 本の鎖とヘム） ──
float3 CosmicHemoglobinColor(CosmicSurface s)
{
    float id = s.uv.x;
    float3 color = id < 0.5 ? float3(0.86, 0.42, 0.38) : id < 1.5 ? float3(0.42, 0.56, 0.88) : id < 2.5 ? float3(0.50, 0.07, 0.09) : float3(0.95, 0.55, 0.20);
    // たんぱく質の表面のゆるい濃淡。
    float3 p = s.unitPosition * 0.5;
    color *= 0.9 + 0.12 * CosmicFbm(p * 18.0 + id * 7.0, 3, s.footprint * 0.5 * 18.0, 2.0, 0.5);
    return CosmicMicroShade(color, float3(0.03, 0.03, 0.03), 0.6, s.normalWS, s.viewWS, 1.0, _CosmicP1.x);
}

// ── 赤血球 ──
float3 CosmicRedBloodCellColor(CosmicSurface s)
{
    float3 p = s.unitPosition * 0.5;
    float pixel = s.footprint * 0.5;
    float membrane = CosmicFbm(p * 40.0 + 3.0, 3, pixel * 40.0, 2.0, 0.5);
    float3 diffuse = float3(0.55, 0.06, 0.05) * (0.92 + 0.1 * membrane);
    float3 lightDir = normalize(_CosmicKeyLight.xyz);
    float nl = dot(s.normalWS, lightDir);
    // 半透明に見せる補正: 裏から回り込む光と、縁の透け。
    float wrap = saturate((nl + 0.5) / 1.5) - saturate(nl);
    float rim = pow(1.0 - saturate(dot(s.normalWS, s.viewWS)), 2.5);
    float3 color = CosmicMicroShade(diffuse, float3(0.04, 0.04, 0.04), 0.35, s.normalWS, s.viewWS, 1.0, _CosmicP1.x);
    color += float3(0.8, 0.12, 0.08) * _CosmicKeyLight.w * (wrap * 0.35 + rim * 0.25);
    return color;
}

// ── 核子（陽子と、原子核の模型の陽子・中性子） ── 図解の色: 陽子は赤、中性子は灰青。マットな陰影（原子と分子の図解と同じ）。
// 陽子の段の陽子（kind 5）は、主カメラから見た見かけの半径が 25〜60 画素より大きく写ると、縁が明るく中の暗いガラスのような陰影に移ります
// （中のクォークとグルーオンの印を玉の上に重ねて描くので、半透明の球の中が透けて見える。原子核の模型の中では他の陽子と同じ玉）。
float3 CosmicNucleonColor(CosmicSurface s, float id)
{
    float3 color = id < 0.5 ? float3(0.86, 0.30, 0.26) : float3(0.62, 0.68, 0.80);
    float3 lit = CosmicMicroShade(color, float3(0.03, 0.03, 0.03), 0.6, s.normalWS, s.viewWS, 1.0, _CosmicP1.x);
    if (_CosmicBody.y > 4.5 && _CosmicBody.y < 5.5)
    {
        float glass = smoothstep(25.0, 60.0, CosmicApparentRadiusPixels(CosmicCenterObjectSpace(), 0.5));
        float rim = pow(1.0 - saturate(dot(s.normalWS, s.viewWS)), 2.0);
        float3 glassColor = color * _CosmicKeyLight.w * (0.08 + 0.9 * rim);
        lit = lerp(lit, glassColor, glass);
    }
    return lit;
}

float3 CosmicSurfaceColor(CosmicSurface s)
{
    uint kind = (uint)(_CosmicBody.y + 0.5);
    if (kind == 0) { return CosmicGolfBallColor(s); }
    if (kind == 1) { return CosmicSandGrainColor(s); }
    if (kind == 3) { return CosmicHemoglobinColor(s); }
    if (kind == 4) { return CosmicRedBloodCellColor(s); }
    if (kind == 5 || kind == 6) { return CosmicNucleonColor(s, kind == 5 ? 0.0 : s.uv.x); }
    return float3(0.5, 0.5, 0.5);
}

#endif
