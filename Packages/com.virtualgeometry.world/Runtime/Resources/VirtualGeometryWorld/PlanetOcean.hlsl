#ifndef VIRTUAL_GEOMETRY_PLANET_OCEAN_INCLUDED
#define VIRTUAL_GEOMETRY_PLANET_OCEAN_INCLUDED

// 海の波(Gerstner)の共通の式。頂点の生成(PlanetOceanGenerate.compute)と、画素ごとの法線(RadialOcean.shader)が使う。
// C# の PlanetOceanSource.TryEvaluateVertexForDiagnostics と同じ式(片方を変えたらもう片方も変える)。
//
// 波 k の位相 θ = 2π·(Cell の原点での端数 + d·(Cell の原点からの相対位置 sphere)/λ) − ω·t。
// sphere は「波の位置を決める札」(変位する前の球面上の点)。Gerstner では、すべての波がこの札に付いて動く。
// 帯域制限: 波長が 2·quadLength 以上の波だけが頂点の変位に入る。それより短い波は、画素ごとに法線(波長が画素より十分長いとき)
// か粗さ(画素より短いとき)へ移す。

#define OCEAN_MAXIMUM_WAVES 32
#define OCEAN_TAU 6.28318530718

float _WaveCount;
// xyz: 波の進む方向(3 次元の単位ベクトル)、w: 波長の逆数
float4 _WaveDirection[OCEAN_MAXIMUM_WAVES];
// x: 振幅、y: Gerstner の尖り、z: 時刻の位相 ω·t(2π で巻いたもの)、w: 波長
float4 _WaveShape[OCEAN_MAXIMUM_WAVES];
// x: Cell の原点での位相の端数(0〜1)
float4 _WavePhase[OCEAN_MAXIMUM_WAVES];

float OceanTheta(int k,float3 sphere)
{
    return OCEAN_TAU*(_WavePhase[k].x+dot(_WaveDirection[k].xyz,sphere)*_WaveDirection[k].w)-_WaveShape[k].z;
}

// 頂点の変位(帯域制限で残る波)。sphere は札の位置、direction はその点の外向きの単位方向。
float3 OceanOffset(float3 sphere,float3 direction,float quadLength)
{
    float3 offset=0;
    int count=(int)_WaveCount;
    for(int k=0;k<count;k++)
    {
        if(_WaveShape[k].w<2.0*quadLength) { continue; }
        float3 d=_WaveDirection[k].xyz;
        float theta=OceanTheta(k,sphere);
        float s;
        float c;
        sincos(theta,s,c);
        float3 along=d-direction*dot(d,direction);
        offset+=direction*(_WaveShape[k].x*s)+along*(_WaveShape[k].y*_WaveShape[k].x*c);
    }
    return offset;
}

// 変位した位置 position(Cell の原点からの相対位置)から、札の位置を求める(position = sphere + offset(sphere) の不動点反復)。
// originDirection は Cell の原点の外向きの単位方向、radius は惑星の半径。変位は波の振幅の和より小さい。縮小は理論上の保証ではなく、実測で反復ごとに約 1/4 に縮む(Requirements/PlanetWorld.md §15)。
float3 OceanLabel(float3 position,float3 originDirection,float radius,float quadLength,int iterations)
{
    float3 sphere=position;
    for(int i=0;i<iterations;i++)
    {
        float3 direction=normalize(originDirection*radius+sphere);
        sphere=position-OceanOffset(sphere,direction,quadLength);
    }
    return sphere;
}

// 帯域制限で変位から落とした波の、画素ごとの寄与。
// gradient: 高さの勾配(接平面の向き、無次元)。resolved(波長が画素の大きさ footprint の 2 倍以上で 1、1 倍以下で 0)の分だけ法線へ。
// variance: 画素より短い波の傾きの分散(1/2·(A·k)²·|d_t|²)。粗さへ。
void OceanShortWaves(float3 sphere,float3 direction,float quadLength,float footprint,out float3 gradient,out float variance)
{
    gradient=0;
    variance=0;
    int count=(int)_WaveCount;
    for(int k=0;k<count;k++)
    {
        float wavelength=_WaveShape[k].w;
        if(wavelength>=2.0*quadLength) { continue; }
        float3 d=_WaveDirection[k].xyz;
        float3 tangent=d-direction*dot(d,direction);
        float slope=_WaveShape[k].x*OCEAN_TAU/wavelength;
        float resolved=saturate(wavelength/max(footprint,1e-6)-1.0);
        gradient+=resolved*slope*cos(OceanTheta(k,sphere))*tangent;
        variance+=(1.0-resolved)*0.5*slope*slope*dot(tangent,tangent);
    }
}

#endif
