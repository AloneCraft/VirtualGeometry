#ifndef VIRTUAL_GEOMETRY_PLANET_CELL_INCLUDED
#define VIRTUAL_GEOMETRY_PLANET_CELL_INCLUDED

// 惑星の Cell 1つ分の格子の、Cell の原点(PlanetCellFrame.Origin = 半径 R の球面から _HeightShift だけ持ち上げた点)からの相対位置を求める部品。C# の PlanetCellGeometry と同じ式。
// 面の表と等角写像は CubeSphere.cs と同じ。大きな値(Cell の原点、中心の正接)は CPU が Logical 精度で求めて渡し、
// ここでは Cell の中心からの相対量だけを float で計算する(誤差は惑星の大きさではなく Cell の大きさに比例する)。

struct VGPackedVertex
{
    float3 position;
    float3 normal;
    float2 uv;
};

RWStructuredBuffer<VGPackedVertex> _Vertices;
int _CellQuads;
int _SlotVertexCount;
int _GridVertexCount;
int _Slot;
int _Face;
float _Radius;
// Cell の原点を、半径 _Radius の球面から外向きに持ち上げた距離(PlanetCellFrame.HeightShift)。0 なら原点は球面上。
// 球面上の点 sphere(SpherePoint の出力)は常に「球面上の原点」からの相対位置で、頂点の位置は PlanetCellVertex で持ち上げた原点からの相対位置にする。
float _HeightShift;
float _SkirtDepth;
// 1 quad の、面の角度での大きさ (π/2)/(N·2^L)。
float _QuadAngle;
// 1 quad の弧長(帯域制限に使う)。
float _QuadLength;
// 中心の正接 (x0, y0)。
float2 _CenterTangent;
// Cell の面の座標の始点と大きさ(UV 用。深い階層では float の精度で潰れるが、形には使わない)。
float4 _FaceRange;

static const float3 FaceNormal[6]={float3(1,0,0),float3(-1,0,0),float3(0,1,0),float3(0,-1,0),float3(0,0,1),float3(0,0,-1)};
static const float3 FaceU[6]={float3(0,0,1),float3(0,0,-1),float3(1,0,0),float3(1,0,0),float3(-1,0,0),float3(1,0,0)};
static const float3 FaceV[6]={float3(0,1,0),float3(0,1,0),float3(0,0,1),float3(0,0,-1),float3(0,1,0),float3(0,1,0)};

// tan(a0+δ)−tan(a0) を、tan(a0)=x0 と δ から差だけで求める。小さな δ では tan を級数で求める(GPU の tan は小さな値の相対精度が低い)。
float TangentOffset(float x0,float delta)
{
    float t;
    if(abs(delta) < 0.2)
    {
        float d2=delta*delta;
        t=delta*(1.0+d2*(1.0/3.0+d2*(2.0/15.0+d2*(17.0/315.0+d2*(62.0/2835.0)))));
    }
    else
    {
        t=tan(delta);
    }
    return t*(1.0+x0*x0)/(1.0-x0*t);
}

// Cell の中心から格子の番号で (a, b) の位置の、半径 _Radius の球面上の点(球面上の原点 SphereOrigin = 持ち上げる前の原点からの相対位置)と単位方向。a、b は −1〜N+1。
// R·(p/|p| − p0/|p0|) = R·(d/|p| − p0·(2p0·d + d·d)/(|p|·|p0|·(|p0|+|p|)))、p = p0 + d。
void SpherePoint(float a,float b,out float3 sphere,out float3 direction)
{
    float half=(float)_CellQuads*0.5;
    float x0=_CenterTangent.x;
    float y0=_CenterTangent.y;
    float dx=TangentOffset(x0,(a-half)*_QuadAngle);
    float dy=TangentOffset(y0,(b-half)*_QuadAngle);
    float3 p0=FaceNormal[_Face]+x0*FaceU[_Face]+y0*FaceV[_Face];
    float3 d=dx*FaceU[_Face]+dy*FaceV[_Face];
    float3 p=p0+d;
    float l0=length(p0);
    float l=length(p);
    float3 relative=d/l-p0*((2.0*dot(p0,d)+dot(d,d))/(l*l0*(l0+l)));
    sphere=relative*_Radius;
    direction=p/l;
}

// 持ち上げた原点からの頂点の位置。height は PlanetHeight の戻り値(= 高さ − _HeightShift。丸めの前に引いてある)。
// (R+h)n − (R+H)u = (R+H)(n−u) + (h−H)n = sphere·(1+H/R) + (h−H)·n(sphere = R(n−u))。H=0 なら sphere + direction·height。
float3 PlanetCellVertex(float3 sphere,float3 direction,float height)
{
    return sphere+sphere*(_HeightShift/_Radius)+direction*height;
}

// slot の中の頂点の番号から、格子の (i, j) と skirt かどうか。
void GridIndex(int index,out int i,out int j,out bool skirt)
{
    int side=_CellQuads+1;
    skirt=false;
    if(index < _GridVertexCount)
    {
        i=index%side;
        j=index/side;
        return;
    }
    int local=index-_GridVertexCount;
    int edge=local/side;
    int t=local%side;
    i=t;j=t;
    if(edge == 0) { j=0; }
    if(edge == 1) { i=_CellQuads; }
    if(edge == 2) { j=_CellQuads; }
    if(edge == 3) { i=0; }
    skirt=true;
}

#endif
