#ifndef VIRTUAL_GEOMETRY_VERTEX_INPUT_INCLUDED
#define VIRTUAL_GEOMETRY_VERTEX_INPUT_INCLUDED
// Virtual Geometry Shader(.vgshader)が生成するVG passの頂点入力。
// 元shaderのvertex関数へ渡すAttributesを、Mesh頂点bufferの代わりにVGの可視index/頂点bufferから組み立てる。
#include "Packages/com.virtualgeometry.core/Shaders/VirtualGeometryIndirect.hlsl"

struct VGPackedVertex
{
    float3 position;
    float3 normal;
    float2 uv;
};
StructuredBuffer<VGPackedVertex> _VGVertices;
StructuredBuffer<uint> _VGIndices;

struct VGVertexData
{
    float3 positionOS;
    float3 normalOS;
    float4 tangentOS;
    float2 uv0;
    uint vertexId;
};

VGPackedVertex VGLoadPackedVertex(uint drawIndex)
{
    return _VGVertices[_VGIndices[drawIndex]];
}

// 頂点形式はtangentを持たない。同じ三角形の3頂点の位置とUVから面tangentを作り、頂点法線へ直交化する。
// UVが縮退した三角形では法線に直交する任意の向きを使う。
float4 VGFaceTangent(VGPackedVertex a,VGPackedVertex b,VGPackedVertex c,float3 normal)
{
    float3 edge1=b.position-a.position;
    float3 edge2=c.position-a.position;
    float2 uv1=b.uv-a.uv;
    float2 uv2=c.uv-a.uv;
    float determinant=uv1.x*uv2.y-uv1.y*uv2.x;
    float3 tangent;
    float3 bitangent;
    if(abs(determinant)>1e-12)
    {
        tangent=(edge1*uv2.y-edge2*uv1.y)/determinant;
        bitangent=(edge2*uv1.x-edge1*uv2.x)/determinant;
    }
    else
    {
        tangent=abs(normal.y)<0.99?cross(float3(0,1,0),normal):cross(float3(1,0,0),normal);
        bitangent=cross(normal,tangent);
    }
    tangent=tangent-normal*dot(normal,tangent);
    if(dot(tangent,tangent)<1e-20)
    {
        tangent=abs(normal.y)<0.99?cross(float3(0,1,0),normal):cross(float3(1,0,0),normal);
    }
    tangent=normalize(tangent);
    float handedness=dot(cross(normal,tangent),bitangent)<0?-1.0:1.0;
    return float4(tangent,handedness);
}

VGVertexData VGLoadVertexData(uint vertexId)
{
    uint drawIndex=VGDrawVertexId(vertexId);
    // material範囲の開始位置と各clusterのindex数は3の倍数なので、三角形の先頭は3の倍数に揃う。
    uint first=drawIndex-drawIndex%3;
    VGPackedVertex a=VGLoadPackedVertex(first);
    VGPackedVertex b=VGLoadPackedVertex(first+1);
    VGPackedVertex c=VGLoadPackedVertex(first+2);
    VGPackedVertex self=VGLoadPackedVertex(drawIndex);
    VGVertexData result;
    result.positionOS=self.position;
    result.normalOS=normalize(self.normal);
    result.tangentOS=VGFaceTangent(a,b,c,result.normalOS);
    result.uv0=self.uv;
    result.vertexId=drawIndex;
    return result;
}
#endif
