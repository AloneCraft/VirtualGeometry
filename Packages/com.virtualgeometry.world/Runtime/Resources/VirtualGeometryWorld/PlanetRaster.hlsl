#ifndef VIRTUAL_GEOMETRY_PLANET_RASTER_INCLUDED
#define VIRTUAL_GEOMETRY_PLANET_RASTER_INCLUDED

// 緯度経度の格子(実データの高さ)の項。C# の PlanetRasterLayer.Sample と同じ式(片方を変えたらもう片方も変える)。
// 頂点の位置は「Cell の原点の緯度経度 + 原点からの差」で表す。原点の画素の位置は CPU が倍精度で整数部(_RasterIndex)と端数(_RasterFraction)に分けて渡し、
// 頂点ごとの緯度経度の差は、桁落ちしない式で求めた単位方向の差 eps = sphere/R から求める(緯度経度を float の方向から求めると、半径 10^6 m で約 0.1 m に量子化される)。
// 帯域制限: quad の長さが画素より大きい Cell は、CPU が選んだ mip(_RasterIndex.z)を使う。
// 高さは「高さ − _HeightShift」を返す。texel の値から先に _HeightShift を引いてから補間する(補間した後で引くと、補間結果の float の丸めが高さの桁で残る)。
// PlanetCell.hlsl の _Radius、_HeightShift を使う(PlanetCell.hlsl を先に include する)。

#define PLANET_RASTER_LAYERS 2

Texture2D<float> _PlanetRaster0;
Texture2D<float> _PlanetRaster1;
int _RasterLayerCount;
// Cell の原点の単位方向(CPU が Logical から求めた値)。
float3 _RasterDirection;
// 格子ごとの Cell の値(2 つ)。
// _RasterIndex: x=原点の画素の列(mip の画素の単位)、y=行、z=mip、w=経度が一周するか
int4 _RasterIndex[PLANET_RASTER_LAYERS];
// _RasterFraction: x=列の端数、y=行の端数、z=mip の画素の数/ラジアン(経度)、w=同(緯度)
float4 _RasterFraction[PLANET_RASTER_LAYERS];
// _RasterSize: x=mip の幅、y=mip の高さ、z=局所の格子か、w=mip 0 の幅
int4 _RasterSize[PLANET_RASTER_LAYERS];
// _RasterRegional: x=範囲の端から重み 0 の画素数(mip 0)、y=重みが 1 になるまでの画素数、z=mip の倍率(2^-mip)、w=mip 0 の高さ
float4 _RasterRegional[PLANET_RASTER_LAYERS];
// _RasterPole: x=mip の北極の値(先頭の行の平均)、y=南極の値(最後の行の平均)。経度が一周する格子(全球)だけが使う(極冠)。
float4 _RasterPole[PLANET_RASTER_LAYERS];

#include "PlanetLonLat.hlsl"

float PlanetRasterLoad(int layer,int x,int y,int mip)
{
    if(layer==0)
    {
        return _PlanetRaster0.Load(int3(x,y,mip));
    }
    return _PlanetRaster1.Load(int3(x,y,mip));
}

// layer の双線形補間。weight は局所の格子の重み(全球は 1)。重みが 0 なら高さは 0。
float PlanetRasterLayerSample(int layer,float2 delta,out float weight)
{
    int4 index=_RasterIndex[layer];
    float4 fraction=_RasterFraction[layer];
    int4 size=_RasterSize[layer];
    float4 regional=_RasterRegional[layer];
    float column=fraction.x+delta.x*fraction.z;
    float row=fraction.y-delta.y*fraction.w;
    weight=1.0;
    if(size.z!=0)
    {
        // mip 0 の画素の位置での、範囲の端までの距離(画素)。
        float column0=((float)index.x+column+0.5)/regional.z-0.5;
        float row0=((float)index.y+row+0.5)/regional.z-0.5;
        float edge=min(min(column0,(float)size.w-1.0-column0),min(row0,regional.w-1.0-row0));
        float t=saturate((edge-regional.x)/max(regional.y,1e-6));
        weight=t*t*(3.0-2.0*t);
        if(weight<=0.0)
        {
            return 0.0;
        }
    }
    float floorColumn=floor(column);
    float floorRow=floor(row);
    float fx=column-floorColumn;
    float fy=row-floorRow;
    int x0=index.x+(int)floorColumn;
    int y0=index.y+(int)floorRow;
    int rowUnclamped=y0;
    int x1=x0+1;
    int y1=y0+1;
    int w=size.x;
    int h=size.y;
    if(index.w!=0)
    {
        x0=((x0%w)+w)%w;
        x1=((x1%w)+w)%w;
    }
    else
    {
        x0=clamp(x0,0,w-1);
        x1=clamp(x1,0,w-1);
    }
    y0=clamp(y0,0,h-1);
    y1=clamp(y1,0,h-1);
    float h00=PlanetRasterLoad(layer,x0,y0,index.z)-_HeightShift;
    float h10=PlanetRasterLoad(layer,x1,y0,index.z)-_HeightShift;
    float h01=PlanetRasterLoad(layer,x0,y1,index.z)-_HeightShift;
    float h11=PlanetRasterLoad(layer,x1,y1,index.z)-_HeightShift;
    float result=lerp(lerp(h00,h10,fx),lerp(h01,h11,fx),fy);
    if(index.w!=0)
    {
        // 極冠。極(北は行の位置 -0.5、南は h-0.5)から半画素の中は、極の値 P から先頭(最後)の行の補間した値へ、極からの距離に比例して移る
        // (先頭の行より北を先頭の行へ clamp すると、極の近くの高さが経度で決まり、極で定まらない)。C# の PlanetRasterLayer.PoleCap と同じ式。
        float4 pole=_RasterPole[layer];
        if(rowUnclamped<0)
        {
            float t=saturate(2.0*((float)rowUnclamped+fy)+1.0);
            float poleHeight=pole.x-_HeightShift;
            result=poleHeight+(result-poleHeight)*t;
        }
        else if(rowUnclamped>=h-1)
        {
            float t=saturate(1.0+2.0*((float)(h-1-rowUnclamped)-fy));
            float poleHeight=pole.y-_HeightShift;
            result=poleHeight+(result-poleHeight)*t;
        }
    }
    return result;
}

// 実データの高さ(半径 R からの高さ)から _HeightShift を引いた値。sphere は Cell の(球面上の)原点からの球面上の点の相対位置。
// regionalWeight は局所の格子の重み(0〜1。局所の格子が無いときは 0)。実データの帯域より上の細部の増幅(PlanetHeight.hlsl)が、遮断波長を重みで混ぜるために使う。
float PlanetRasterHeightWeighted(float3 sphere,out float regionalWeight)
{
    regionalWeight=0.0;
    if(_RasterLayerCount<=0)
    {
        return -_HeightShift;
    }
    float3 eps=sphere/_Radius;
    float2 delta=PlanetLonLatDelta(_RasterDirection,eps);
    float weight0;
    float height=PlanetRasterLayerSample(0,delta,weight0);
    if(_RasterLayerCount>1)
    {
        float weight1;
        float regional=PlanetRasterLayerSample(1,delta,weight1);
        height=lerp(height,regional,weight1);
        regionalWeight=weight1;
    }
    return height;
}

float PlanetRasterHeight(float3 sphere)
{
    float regionalWeight;
    return PlanetRasterHeightWeighted(sphere,regionalWeight);
}

#endif
