#ifndef VIRTUAL_GEOMETRY_PLANET_HEIGHT_INCLUDED
#define VIRTUAL_GEOMETRY_PLANET_HEIGHT_INCLUDED

// 手続き生成の惑星の高さの Field。C# の PlanetHeightField.Evaluate と同じ式(片方を変えたらもう片方も変える)。
// 位置は Cell の原点からの相対位置 sphere(球面上の点)で受け取り、octave ごとの格子は
// 「原点の格子番号 _NoiseLattice + 端数 _NoiseFraction + sphere の格子」に分けて、大きな座標を float にしない。
// 高さ = Hmax·clamp(大陸 + 山 + 細部, −1, 1)。各項の振幅は Hmax に対する割合。
// 帯域制限: 波長が 2·quadLength より短い octave は足さない(頂点の間隔で表せない帯域。粗い Cell の折り返しを防ぐ)。
// 実データの格子(PlanetRaster.hlsl)の高さを、手続きの高さの後ろに足す(PlanetCell.hlsl の _Radius、_HeightShift が先に必要)。
// 実データの帯域より上の細部の増幅: 実データがあるとき、遮断波長(実データの 1/2 画素の波長)より短い波長の octave を足す。
// octave d の波長を λd、遮断波長を λc(局所の格子の重みで全球と局所の値を混ぜる)として x = log2(λc/λd)。x > 0 の octave を、重み saturate(x)(1 octave かけて入る)、
// 振幅 A·2^(−H·x)(Hurst 指数 H。遮断波長で A、波長が半分になるごとに 2^−H 倍)で足す。平均 0 の gradient noise なので、帯域制限で落としても低い周波数は変わらない。
// 戻り値は「高さ − _HeightShift」(Cell の原点を持ち上げた分。頂点の高さ方向の成分を小さく保つため、実データは texel の段階で引く)。
// _HeightShift が 0 でない Cell は、地形の生成だけ。海の水深(PlanetOceanGenerate.compute)は _HeightShift=0 の Cell で、絶対の高さを使う。

#include "PlanetRaster.hlsl"

#define PLANET_HEIGHT_MAXIMUM_OCTAVES 32
#define PLANET_HEIGHT_CONTINENT_OCTAVES 4
#define PLANET_HEIGHT_MOUNTAIN_OCTAVES 6

int4 _NoiseLattice[PLANET_HEIGHT_MAXIMUM_OCTAVES];
float4 _NoiseFraction[PLANET_HEIGHT_MAXIMUM_OCTAVES];
float _InverseWavelength;
int _Octaves;
float _HeightAmplitude;
// x: 海面のしきい値、y: 海岸の幅の半分、z: 海の深さ、w: 平地の高さ
float4 _HeightShape;
// x: 山の最初の octave、y: 山の振幅、z: 細部の最初の octave、w: 細部の振幅
float4 _HeightBands;
// x: 増幅の振幅 A(m。0 で無効)、y: Hurst 指数 H、z: 全球の格子の遮断波長の log2(m)、w: 局所の格子の遮断波長の log2(m)
float4 _HeightAmplify;

uint3 PlanetPcg3d(uint3 v)
{
    v=v*1664525u+1013904223u;
    v.x+=v.y*v.z;v.y+=v.z*v.x;v.z+=v.x*v.y;
    v^=v>>16u;
    v.x+=v.y*v.z;v.y+=v.z*v.x;v.z+=v.x*v.y;
    return v;
}

float3 PlanetGradient(int3 cell,uint channel)
{
    uint3 hash=PlanetPcg3d(asuint(cell)+uint3(channel*0x9e3779b9u,channel*0x85ebca6bu,channel*0xc2b2ae35u));
    return normalize(float3(hash&0xffffu)/32767.5-1.0+1e-6);
}

// 3D の gradient noise(値はおよそ −1〜1、平均 0)。格子番号 i と、その中の位置 f(0〜1)。
float PlanetGradientNoise(int3 i,float3 f,uint channel)
{
    float3 w=f*f*f*(f*(f*6.0-15.0)+10.0);
    float n000=dot(PlanetGradient(i+int3(0,0,0),channel),f-float3(0,0,0));
    float n100=dot(PlanetGradient(i+int3(1,0,0),channel),f-float3(1,0,0));
    float n010=dot(PlanetGradient(i+int3(0,1,0),channel),f-float3(0,1,0));
    float n110=dot(PlanetGradient(i+int3(1,1,0),channel),f-float3(1,1,0));
    float n001=dot(PlanetGradient(i+int3(0,0,1),channel),f-float3(0,0,1));
    float n101=dot(PlanetGradient(i+int3(1,0,1),channel),f-float3(1,0,1));
    float n011=dot(PlanetGradient(i+int3(0,1,1),channel),f-float3(0,1,1));
    float n111=dot(PlanetGradient(i+int3(1,1,1),channel),f-float3(1,1,1));
    float x00=lerp(n000,n100,w.x);float x10=lerp(n010,n110,w.x);
    float x01=lerp(n001,n101,w.x);float x11=lerp(n011,n111,w.x);
    return lerp(lerp(x00,x10,w.y),lerp(x01,x11,w.y),w.z)*1.5;
}

// octave の noise。channel は項ごとに別の値にする(同じ octave でも別の模様)。
float PlanetOctaveNoise(float3 sphere,int octave,uint channel)
{
    float3 q=_NoiseFraction[octave].xyz+sphere*(_InverseWavelength*exp2((float)octave));
    float3 whole=floor(q);
    return PlanetGradientNoise(_NoiseLattice[octave].xyz+(int3)whole,q-whole,channel*64u+(uint)octave);
}

// octave の波長が 2·quadLength 以上(頂点の間隔で表せる)か。
bool PlanetOctaveResolved(int octave,float quadLength)
{
    return exp2(-(float)octave)>=2.0*quadLength*_InverseWavelength;
}

// 実データの帯域より上の細部(増幅)。regionalWeight は局所の格子の重み。
float PlanetAmplifiedHeight(float3 sphere,float quadLength,float regionalWeight)
{
    if(_HeightAmplify.x<=0.0||_RasterLayerCount<=0)
    {
        return 0.0;
    }
    float cutoff=lerp(_HeightAmplify.z,_HeightAmplify.w,regionalWeight);
    float baseLog2=-log2(_InverseWavelength);
    float sum=0;
    for(int d=0;d<_Octaves;d++)
    {
        float x=cutoff-(baseLog2-(float)d);
        if(x<=0.0||!PlanetOctaveResolved(d,quadLength))
        {
            continue;
        }
        sum+=saturate(x)*_HeightAmplify.x*exp2(-_HeightAmplify.y*x)*PlanetOctaveNoise(sphere,d,3u);
    }
    return sum;
}

float PlanetHeight(float3 sphere,float quadLength)
{
    // 大陸: 低い周波数の fBm を海面のしきい値で陸と海に分ける。
    float continent=0;
    float amplitude=0.5;
    for(int k=0;k<PLANET_HEIGHT_CONTINENT_OCTAVES&&k<_Octaves;k++)
    {
        if(PlanetOctaveResolved(k,quadLength))
        {
            continent+=amplitude*PlanetOctaveNoise(sphere,k,0u);
        }
        amplitude*=0.5;
    }
    float land=smoothstep(_HeightShape.x-_HeightShape.y,_HeightShape.x+_HeightShape.y,continent);
    float total=lerp(-_HeightShape.z,_HeightShape.w,land);
    // 山: 陸の上だけに ridged noise((1−|n|)² から平均の目安 0.4 を引く)。
    int mountainStart=(int)_HeightBands.x;
    float mountains=0;
    amplitude=_HeightBands.y;
    for(int m=0;m<PLANET_HEIGHT_MOUNTAIN_OCTAVES;m++)
    {
        int octave=mountainStart+m;
        if(octave<_Octaves&&PlanetOctaveResolved(octave,quadLength))
        {
            float ridge=1.0-abs(PlanetOctaveNoise(sphere,octave,1u));
            mountains+=amplitude*(ridge*ridge-0.4);
        }
        amplitude*=0.5;
    }
    total+=mountains*land;
    // 細部: 残りの octave の fBm(平均 0 なので、帯域制限で落としても低い周波数は変わらない)。
    int detailStart=(int)_HeightBands.z;
    amplitude=_HeightBands.w;
    for(int d=detailStart;d<_Octaves;d++)
    {
        if(PlanetOctaveResolved(d,quadLength))
        {
            total+=amplitude*PlanetOctaveNoise(sphere,d,2u);
        }
        amplitude*=0.5;
    }
    // 実データの高さ(PlanetRaster.hlsl)を足す。実データがあるときは、手続きの項は実データの帯域より上の細部に使う(PlanetTerrainSource の設定)。
    float regionalWeight;
    float raster=PlanetRasterHeightWeighted(sphere,regionalWeight);
    return clamp(total,-1.0,1.0)*_HeightAmplitude+raster+PlanetAmplifiedHeight(sphere,quadLength,regionalWeight);
}

#endif
