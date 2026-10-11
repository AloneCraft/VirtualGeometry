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

// atan2(y, x)。差が小さい(x が正で |y| < 0.1·x)ときは、atan の級数で相対精度を保つ(GPU の atan2 は小さな値で相対誤差が 1e-6 ほど出うる。
// 1mm 以下を測る頂点の高さ(位置の誤差 1e-4 m が高さの傾きにかかる)に効く)。極(y=0、x=0)は 0。
float PlanetAtan2(float y,float x)
{
    if(x>0.0&&abs(y)<0.1*x)
    {
        float t=y/x;
        float t2=t*t;
        return t*(1.0-t2*(1.0/3.0-t2*(1.0/5.0-t2*(1.0/7.0))));
    }
    if(y==0.0&&x==0.0)
    {
        return 0.0;
    }
    return atan2(y,x);
}

// 原点の単位方向 o と、頂点の単位方向との差 eps(= n − o)から、緯度経度の差(ラジアン)。x=経度、y=緯度。
// 緯度の差 = asin(nz) − asin(oz) = atan2(a·cb − b·ca, ca·cb + a·b)(a=nz、b=oz)、a·cb − b·ca は eps.z·(cb + b(a+b)/(ca+cb)) に直す。
// 経度の差 = atan2(nx·oy − ny·ox, nx·ox + ny·oy)。どちらも大きな値どうしの引き算が無い。
float2 PlanetLonLatDelta(float3 o,float3 eps)
{
    float a=o.z+eps.z;
    float b=o.z;
    float ca=sqrt(max(1.0-a*a,0.0));
    float cb=sqrt(max(1.0-b*b,0.0));
    float numerator=eps.z*(cb+b*(a+b)/(ca+cb+1e-30));
    float denominator=ca*cb+a*b;
    float latitude=PlanetAtan2(numerator,denominator);
    float crossValue=eps.x*o.y-eps.y*o.x;
    float dotValue=o.x*o.x+o.y*o.y+eps.x*o.x+eps.y*o.y;
    // 原点が極(水平成分が 0)で、頂点も同じ向きのとき、atan2(0, 0) は未定義(NaN の高さが Cell の bounds に入って、選択が細分化しなくなる)なので PlanetAtan2 が 0 にする。
    float longitude=PlanetAtan2(crossValue,dotValue);
    return float2(longitude,latitude);
}

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
    return lerp(lerp(h00,h10,fx),lerp(h01,h11,fx),fy);
}

// 実データの高さ(半径 R からの高さ)から _HeightShift を引いた値。sphere は Cell の(球面上の)原点からの球面上の点の相対位置。
float PlanetRasterHeight(float3 sphere)
{
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
    }
    return height;
}

#endif
