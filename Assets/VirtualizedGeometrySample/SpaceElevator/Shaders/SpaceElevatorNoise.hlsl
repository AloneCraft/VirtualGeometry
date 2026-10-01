// SpaceElevator/Procedural と SpaceElevator/Clouds が使う、オブジェクト空間の手続きノイズ。
// 改良 Perlin ノイズ(Ken Perlin 2002 の公開アルゴリズム: 5 次の補間と 12 方向の勾配)と、整数ハッシュ(PCG3D: Jarzynski & Olano 2020)の自前実装。
// 遠景のちらつきを抑えるため、fbm は画素の大きさ(footprint、模様座標の単位/画素)より細かいオクターブを弱める。
// VG の Virtual Geometry Shader 変換と衝突しないよう、名前は SE で始める(VG / _VG で始まる名前は使わない)。
#ifndef SPACE_ELEVATOR_NOISE_INCLUDED
#define SPACE_ELEVATOR_NOISE_INCLUDED

uint3 SEHashPcg3(uint3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    return v;
}

// 整数格子点ごとの一様乱数 [0,1)。パネルごとの色ムラ・点灯の選択に使う。
float SEHashCell(float3 cell)
{
    uint3 h = SEHashPcg3(asuint(int3(floor(cell))));
    return (h.x >> 8) * (1.0 / 16777216.0);
}

float3 SEHashCell3(float3 cell)
{
    uint3 h = SEHashPcg3(asuint(int3(floor(cell))));
    return float3(h >> 8) * (1.0 / 16777216.0);
}

float SEFade(float t)
{
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}

float SEGrad(uint h, float3 p)
{
    h &= 15u;
    float u = h < 8u ? p.x : p.y;
    float v = h < 4u ? p.y : ((h == 12u || h == 14u) ? p.x : p.z);
    return ((h & 1u) != 0u ? -u : u) + ((h & 2u) != 0u ? -v : v);
}

// 改良 Perlin ノイズ。値域はおよそ [-1,1]。
float SEPerlin(float3 p)
{
    float3 i = floor(p);
    float3 f = p - i;
    int3 c = int3(i);
    float3 u = float3(SEFade(f.x), SEFade(f.y), SEFade(f.z));
    float n000 = SEGrad(SEHashPcg3(asuint(c + int3(0, 0, 0))).x, f - float3(0, 0, 0));
    float n100 = SEGrad(SEHashPcg3(asuint(c + int3(1, 0, 0))).x, f - float3(1, 0, 0));
    float n010 = SEGrad(SEHashPcg3(asuint(c + int3(0, 1, 0))).x, f - float3(0, 1, 0));
    float n110 = SEGrad(SEHashPcg3(asuint(c + int3(1, 1, 0))).x, f - float3(1, 1, 0));
    float n001 = SEGrad(SEHashPcg3(asuint(c + int3(0, 0, 1))).x, f - float3(0, 0, 1));
    float n101 = SEGrad(SEHashPcg3(asuint(c + int3(1, 0, 1))).x, f - float3(1, 0, 1));
    float n011 = SEGrad(SEHashPcg3(asuint(c + int3(0, 1, 1))).x, f - float3(0, 1, 1));
    float n111 = SEGrad(SEHashPcg3(asuint(c + int3(1, 1, 1))).x, f - float3(1, 1, 1));
    float nx00 = lerp(n000, n100, u.x);
    float nx10 = lerp(n010, n110, u.x);
    float nx01 = lerp(n001, n101, u.x);
    float nx11 = lerp(n011, n111, u.x);
    return lerp(lerp(nx00, nx10, u.y), lerp(nx01, nx11, u.y), u.z) * 0.98;
}

// 画素の大きさに対するオクターブの重み。1 画素に半周期以上入る細かさは消す。
float SEOctaveWeight(float frequency, float footprint)
{
    return saturate(2.0 - 4.0 * frequency * footprint);
}

// 正規化した fBm。値域はおよそ [0,1]、平均 0.5。detail は Blender の Detail と同じく「追加するオクターブ数」(端数は最後のオクターブを補間)。
// footprint: 入力座標 p の単位で、1 画素がどれだけの長さに当たるか(fwidth で求める)。0 なら全オクターブ。
float SEFbm(float3 p, float detail, float roughness, float footprint)
{
    float sum = 0.0;
    float amplitude = 1.0;
    float total = 0.0;
    float frequency = 1.0;
    int count = (int)detail;
    [loop] for (int i = 0; i <= count; i++)
    {
        float w = SEOctaveWeight(frequency, footprint);
        sum += SEPerlin(p * frequency) * amplitude * w;
        total += amplitude;
        amplitude *= roughness;
        frequency *= 2.0;
    }
    float remainder = detail - count;
    if (remainder > 0.0)
    {
        float w = SEOctaveWeight(frequency, footprint) * remainder;
        sum += SEPerlin(p * frequency) * amplitude * w;
        total += amplitude * remainder;
    }
    return 0.5 + 0.5 * sum / max(total, 1e-5);
}

// 尾根状のノイズ(山の稜線・岩肌の凹凸用)。値域はおよそ [0,1]。
float SERidged(float3 p, float detail, float roughness, float footprint)
{
    float sum = 0.0;
    float amplitude = 1.0;
    float total = 0.0;
    float frequency = 1.0;
    float weight = 1.0;
    int count = (int)detail;
    [loop] for (int i = 0; i <= count; i++)
    {
        float w = SEOctaveWeight(frequency, footprint);
        float signal = 1.0 - abs(SEPerlin(p * frequency));
        signal *= signal * weight;
        weight = saturate(signal * 2.0);
        sum += signal * amplitude * w;
        total += amplitude;
        amplitude *= roughness;
        frequency *= 2.0;
    }
    return sum / max(total, 1e-5);
}

// Blender の Map Range(Linear・Clamp)。fromMin > fromMax の逆向き指定も同じ式で扱う。
float SEMapRange(float value, float fromMin, float fromMax, float toMin, float toMax)
{
    float t = (value - fromMin) / (fromMax - fromMin);
    return clamp(toMin + t * (toMax - toMin), min(toMin, toMax), max(toMin, toMax));
}

// Blender の Math Ping-Pong(scale 0.5 で、最も近い整数までの距離)。
float SEPingPong(float value, float scale)
{
    return abs(frac((value - scale) / (scale * 2.0)) * scale * 2.0 - scale);
}

#endif
