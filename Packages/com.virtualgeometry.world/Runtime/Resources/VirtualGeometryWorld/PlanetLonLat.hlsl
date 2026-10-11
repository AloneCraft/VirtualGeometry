#ifndef VIRTUAL_GEOMETRY_PLANET_LONLAT_INCLUDED
#define VIRTUAL_GEOMETRY_PLANET_LONLAT_INCLUDED

// 緯度経度の差(原点の単位方向 o と、頂点の単位方向との差 eps = n − o から)。実データの格子(PlanetRaster.hlsl)と、色の格子(RadialMoonGrid.shader の fragment)が使う。
// C# の PlanetRasterLayer.Delta / PolarDelta と同じ式(片方を変えたらもう片方も変える)。座標の規約: 経度 = atan2(x, y)、緯度 = atan2(z, √(x²+y²))。

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

#endif
