// CosmicScale の手続き生成の部品（材質の定数や global を持たない）: 整数ハッシュ（pcg3d）のノイズ、画素の大きさによるオクターブのフェード、
// 動く Worley、回転、2位相の周期リセット、黒体の色、光線と球の交点。天体のシェーダーは CosmicCommon.hlsl 経由で、スカイボックスは直接読みます。
// ハッシュは sin を使わない整数演算なので、D3D12 と WebGPU で同じ模様になります。
#ifndef COSMIC_NOISE_INCLUDED
#define COSMIC_NOISE_INCLUDED

#define COSMIC_PI 3.14159265358979

// ── ハッシュとノイズ ──
uint3 CosmicPcg3(uint3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return v;
}
// 整数格子点の [0,1) の乱数3つ。
float3 CosmicHash3(int3 p){return float3(CosmicPcg3(asuint(p))) * (1.0 / 4294967296.0);}
float CosmicHash1(int3 p){return CosmicHash3(p).x;}

float3 CosmicQuintic(float3 t){return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);}

// 3次元の勾配ノイズ（-1..1 程度）。
float CosmicNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = p - i;
    int3 c = (int3)i;
    float3 u = CosmicQuintic(f);
    float n000 = dot(CosmicHash3(c + int3(0,0,0)) * 2.0 - 1.0, f - float3(0,0,0));
    float n100 = dot(CosmicHash3(c + int3(1,0,0)) * 2.0 - 1.0, f - float3(1,0,0));
    float n010 = dot(CosmicHash3(c + int3(0,1,0)) * 2.0 - 1.0, f - float3(0,1,0));
    float n110 = dot(CosmicHash3(c + int3(1,1,0)) * 2.0 - 1.0, f - float3(1,1,0));
    float n001 = dot(CosmicHash3(c + int3(0,0,1)) * 2.0 - 1.0, f - float3(0,0,1));
    float n101 = dot(CosmicHash3(c + int3(1,0,1)) * 2.0 - 1.0, f - float3(1,0,1));
    float n011 = dot(CosmicHash3(c + int3(0,1,1)) * 2.0 - 1.0, f - float3(0,1,1));
    float n111 = dot(CosmicHash3(c + int3(1,1,1)) * 2.0 - 1.0, f - float3(1,1,1));
    float x00 = lerp(n000, n100, u.x), x10 = lerp(n010, n110, u.x);
    float x01 = lerp(n001, n101, u.x), x11 = lerp(n011, n111, u.x);
    return lerp(lerp(x00, x10, u.y), lerp(x01, x11, u.y), u.z) * 1.6;
}

// オクターブ k（周波数 frequency、単位は「座標1あたりの周期数」）を、画素の大きさ footprint（同じ座標の単位）で薄めます。
// 1画素に半周期以上入る細かさは平均（0）へ置き換え、遠くで模様がちらつかないようにします。
float CosmicOctaveWeight(float frequency, float footprint)
{
    return saturate(1.5 - 2.0 * frequency * footprint);
}

// 帯域制限した fBm（-1..1 程度）。octaves は最大の段数、footprint で細かい段から消えます。
float CosmicFbm(float3 p, int octaves, float footprint, float lacunarity, float gain)
{
    float sum = 0, amplitude = 0.5, frequency = 1.0, norm = 0;
    [loop] for (int k = 0; k < octaves; k++)
    {
        float w = CosmicOctaveWeight(frequency, footprint);
        if (w <= 0.0) break;
        sum += amplitude * w * CosmicNoise(p * frequency + float(k) * 17.13);
        norm += amplitude;
        amplitude *= gain; frequency *= lacunarity;
    }
    return norm > 0 ? sum / norm : 0;
}

// 動く Worley（細胞）。特徴点は格子ごとのハッシュの位置のまわりを時間でゆっくり回ります。x = 最近距離 F1、y = 2番目 F2。
float2 CosmicWorley(float3 p, float time, float wobble)
{
    float3 i = floor(p);
    float3 f = p - i;
    int3 c = (int3)i;
    float f1 = 8.0, f2 = 8.0;
    [unroll] for (int z = -1; z <= 1; z++)
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        int3 o = int3(x, y, z);
        float3 h = CosmicHash3(c + o);
        float3 jitter = 0.5 + (h - 0.5) * 0.8 + wobble * 0.18 * sin(time * (0.6 + h.yzx) + h.zxy * 6.2831);
        float3 d = float3(o) + jitter - f;
        float dd = dot(d, d);
        if (dd < f1) { f2 = f1; f1 = dd; } else if (dd < f2) { f2 = dd; }
    }
    return sqrt(float2(f1, f2));
}

// ── テクスチャのノイズ（体積とブラックホールの円盤用） ──
// CosmicNoiseVolume（C#）が起動時に作る周期的な3次元テクスチャ。1周期 = 座標の 8（1格子 = 座標の 1）。
// R/G = 勾配ノイズ（0..1）、B = Worley F1、A = F2 − F1。ミップマップの段は画素の大きさから選びます（帯域制限）。
TEXTURE3D(_CosmicNoise3D); SAMPLER(sampler_CosmicNoise3D);
#define COSMIC_NOISE_TEXEL 0.125

float4 CosmicNoiseTexel(float3 p, float footprint)
{
    float lod = clamp(log2(max(footprint / COSMIC_NOISE_TEXEL, 1e-6)), 0.0, 6.0);
    return SAMPLE_TEXTURE3D_LOD(_CosmicNoise3D, sampler_CosmicNoise3D, p * (1.0 / 8.0), lod);
}

// テクスチャのノイズ（−1..1 程度）。
float CosmicNoiseTex(float3 p, float footprint){return CosmicNoiseTexel(p, footprint).r * 2.0 - 1.0;}

// テクスチャの fBm。CosmicFbm と同じ引数（footprint は座標の単位の画素の大きさ）。
float CosmicFbmTex(float3 p, int octaves, float footprint, float lacunarity, float gain)
{
    float sum = 0, amplitude = 0.5, frequency = 1.0, norm = 0;
    [loop] for (int k = 0; k < octaves; k++)
    {
        float w = CosmicOctaveWeight(frequency, footprint);
        if (w <= 0.0) break;
        float4 n = CosmicNoiseTexel(p * frequency + float(k) * 3.17, footprint * frequency);
        sum += amplitude * w * ((k & 1) ? n.g : n.r) * 2.0 - amplitude * w;
        norm += amplitude;
        amplitude *= gain; frequency *= lacunarity;
    }
    return norm > 0 ? sum / norm : 0;
}

// Worley の近い順に4つの距離（F1 ≤ F2 ≤ F3 ≤ F4）。CosmicVolumeBake.compute の Worley4 と同じ点の置き方（同じハッシュ）です。
float4 CosmicWorley4(float3 p)
{
    int3 i = (int3)floor(p);
    float4 f = 64;
    [unroll] for (int z = -1; z <= 1; z++)
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        int3 c = i + int3(x, y, z);
        float3 gap = float3(c) + 0.1 + 0.8 * CosmicHash3(c + 7919) - p;
        // 比べるのは距離の2乗（平方根は最後の4つだけ）。
        float d = dot(gap, gap);
        if (d < f.x) { f = float4(d, f.xyz); }
        else if (d < f.y) { f = float4(f.x, d, f.yz); }
        else if (d < f.z) { f = float4(f.xy, d, f.z); }
        else if (d < f.w) { f.w = d; }
    }
    return sqrt(f);
}

// テクスチャの Worley（x = F1、y = F2）。
float2 CosmicWorleyTex(float3 p, float footprint)
{
    float4 n = CosmicNoiseTexel(p, footprint);
    return float2(n.b, n.b + n.a);
}

// ── 回転と流れ ──
float3 CosmicRotateY(float3 p, float angle)
{
    float s = sin(angle), c = cos(angle);
    return float3(c * p.x + s * p.z, p.y, -s * p.x + c * p.z);
}
// 軸 axis（単位）まわりに angle だけ回す（Rodrigues）。
float3 CosmicRotateAxis(float3 p, float3 axis, float angle)
{
    float s = sin(angle), c = cos(angle);
    return p * c + cross(axis, p) * s + axis * dot(axis, p) * (1.0 - c);
}

// 2位相の周期リセット（flow map と同じ考え方）。時刻 t、周期 period の位相 j（0 か 1）の進み τ（0..1）と重み。
// 移流量は τ × period に比例し、τ が 1→0 へ戻る瞬間はその位相の重みが 0 なので継ぎ目が見えません。
// 無限に溜まるせん断（帯が筋だけになる）を避けつつ、帯ごとに違う速さの流れを表します。
void CosmicFlowPhase(float t, float period, int j, out float tau, out float weight, out float epoch)
{
    float x = t / period + 0.5 * j;
    epoch = floor(x);
    tau = x - epoch;
    weight = 1.0 - abs(2.0 * tau - 1.0);
}

// ── 色 ──
// 黒体の色（線形 sRGB、輝度でおおよそ正規化）。1000〜40000 K の近似（Planck 分布を CIE 系でまとめた近似式）。
float3 CosmicBlackbody(float kelvin)
{
    float t = clamp(kelvin, 1000.0, 40000.0) / 100.0;
    float r = t <= 66.0 ? 1.0 : saturate(1.29293618 * pow(t - 60.0, -0.1332047592));
    float g = t <= 66.0 ? saturate(0.39008157 * log(t) - 0.63184144) : saturate(1.12989086 * pow(t - 60.0, -0.0755148492));
    float b = t >= 66.0 ? 1.0 : (t <= 19.0 ? 0.0 : saturate(0.54320679 * log(t - 10.0) - 1.19625409));
    float3 srgb = float3(r, g, b);
    float3 linearColor = srgb * srgb * (srgb * 0.3 + 0.7);
    float luminance = dot(linearColor, float3(0.2126, 0.7152, 0.0722));
    return linearColor / max(luminance, 1e-3);
}

// 黒体の色度（最大の成分で正規化、0..1）。明るさは別に掛けます（輝度で正規化すると低温の赤が 1 を大きく超え、ACES で白く褪せるため）。
float3 CosmicBlackbodyChroma(float kelvin)
{
    float3 c = CosmicBlackbody(kelvin);
    return c / max(max(c.r, c.g), max(c.b, 1e-4));
}

float CosmicLuminance(float3 c){return dot(c, float3(0.2126, 0.7152, 0.0722));}

// 単位直径の球（半径 0.5）と光線の交点（入る t、出る t）。交わらなければ x > y。
float2 CosmicRaySphere(float3 origin, float3 direction, float radius)
{
    float b = dot(origin, direction);
    float c = dot(origin, origin) - radius * radius;
    float h = b * b - c;
    if (h < 0.0) return float2(1.0, -1.0);
    h = sqrt(h);
    return float2(-b - h, -b + h);
}

#endif
