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

// atan2(y, x) を、float の丸めに近い精度(相対 3e-7 ほど)で求める。|a| <= tan(π/8) に折り返して級数にする。
// GPU の atan2 は誤差が大きい(1e-5 ほど。極冠の経度が列の位置に 1e-3 画素ずれ、極から 100 m の頂点で 1mm の高さの違いになった)ので、極の近くの緯度経度の差に使う。
float PlanetAtan2Accurate(float y,float x)
{
    float ax=abs(x);
    float ay=abs(y);
    float high=max(ax,ay);
    float low=min(ax,ay);
    if(high==0.0)
    {
        return 0.0;
    }
    float a=low/high;
    float t=a;
    bool folded=a>0.41421356;
    if(folded)
    {
        t=(a-1.0)/(a+1.0);
    }
    float t2=t*t;
    float result=t*(1.0+t2*(-1.0/3.0+t2*(1.0/5.0+t2*(-1.0/7.0+t2*(1.0/9.0+t2*(-1.0/11.0+t2*(1.0/13.0+t2*(-1.0/15.0))))))));
    if(folded)
    {
        result=0.78539816+result;
    }
    if(ay>ax)
    {
        result=1.57079633-result;
    }
    if(x<0.0)
    {
        result=3.14159265-result;
    }
    if(y<0.0)
    {
        result=-result;
    }
    return result;
}

// この |o.z| 以上(緯度 75° 以上)の原点は、PlanetPolarDelta で緯度経度の差を求める。C# の PlanetRasterLayer.PolarZ と同じ値。
#define PLANET_POLAR_Z 0.966

// 極の近く(|o.z| ≥ PLANET_POLAR_Z)の、緯度経度の差(ラジアン。x=経度、y=緯度)。C# の PlanetRasterLayer.PolarDelta と同じ式。
// 緯度を cos(緯度) = √(1−z²) から求める式(PlanetLonLatDelta)は、極で桁落ちする(float の o.z が 1.0 になる緯度 89.99° より先では緯度が −90° にずれる)ので、
// 水平成分の大きさ ho = |o_h|、hn = |n_h| から求める: 緯度の差 = atan2(eps.z·ho − (hn−ho)·oz, hn·ho + nz·oz)、経度の差 = atan2(eps.x·oy − eps.y·ox, ho² + o_h·eps_h)。
// hn − ho = (|n_h|² − ho²)/(hn + ho) は、大きな値どうしの引き算をしない形。hn は n_h = o_h + eps_h の長さを成分の和から直接求める
// (|n_h|² = ho² + d2 の平方根にすると、頂点が極に近いとき(n_h → 0)に相殺して、hn に ho の 3e-4 倍の誤差が出る)。
float2 PlanetPolarDelta(float3 o,float3 eps)
{
    float ho2=o.x*o.x+o.y*o.y;
    float ho=sqrt(ho2);
    float oe=o.x*eps.x+o.y*eps.y;
    float d2=2.0*oe+eps.x*eps.x+eps.y*eps.y;
    float nx=o.x+eps.x;
    float ny=o.y+eps.y;
    float hn=sqrt(nx*nx+ny*ny);
    float eh=d2/(hn+ho+1e-30);
    float nz=o.z+eps.z;
    float latitude=PlanetAtan2Accurate(eps.z*ho-eh*o.z,hn*ho+nz*o.z);
    float longitude;
    if(ho2<1e-30)
    {
        // 原点が極そのもの(階層 0 の根だけ)。原点の経度は 0 で、経度の差は頂点の経度。
        longitude=PlanetAtan2Accurate(eps.x,eps.y);
    }
    else
    {
        longitude=PlanetAtan2Accurate(eps.x*o.y-eps.y*o.x,ho2+oe);
    }
    return float2(longitude,latitude);
}

// 原点の単位方向 o と、頂点の単位方向との差 eps(= n − o)から、緯度経度の差(ラジアン)。x=経度、y=緯度。
// 緯度の差 = asin(nz) − asin(oz) = atan2(a·cb − b·ca, ca·cb + a·b)(a=nz、b=oz)、a·cb − b·ca は eps.z·(cb + b(a+b)/(ca+cb)) に直す。
// 経度の差 = atan2(nx·oy − ny·ox, nx·ox + ny·oy)。どちらも大きな値どうしの引き算が無い。
// |o.z| ≥ PLANET_POLAR_Z の原点は PlanetPolarDelta に任せる。
float2 PlanetLonLatDelta(float3 o,float3 eps)
{
    float2 result;
    if(abs(o.z)>=PLANET_POLAR_Z)
    {
        result=PlanetPolarDelta(o,eps);
    }
    else
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
        // 頂点が極そのもの(水平成分が 0)で、原点が極でないとき、cross と dot がともに 0 になり、atan2(0, 0) は未定義
        // (NaN の高さが Cell の bounds に入って、選択が細分化しなくなる)なので PlanetAtan2 が 0 にする。原点が極のときは PlanetPolarDelta が受け持つ。
        float longitude=PlanetAtan2(crossValue,dotValue);
        result=float2(longitude,latitude);
    }
    return result;
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
