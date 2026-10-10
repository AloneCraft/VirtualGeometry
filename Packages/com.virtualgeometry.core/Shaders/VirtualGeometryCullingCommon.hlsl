#ifndef VIRTUAL_GEOMETRY_CULLING_COMMON_INCLUDED
#define VIRTUAL_GEOMETRY_CULLING_COMMON_INCLUDED
// clusterのFrustum・HZB判定。ビルド済みDAGの選択(VirtualGeometrySelect.compute)と
// 実行時生成clusterのcull(VirtualGeometryClusterCull.compute)が同じ規則を使う。
// 座標はworld空間(Frustum平面、カメラ位置、ViewProjectionと同じ空間)。

Texture2D<float> _VGHzb;
float4 _VGScreenSize;
float4 _VGHzbSize;
int _VGReversedZ;
float _VGDepthBias;
float _VGTextureYSign;
float4x4 _VGViewProjection;
float4 _VGFrustumPlanes[6];
float3 _VGCameraPosition;
float _VGNearPlane;

float Farthest(float a,float b)
{
    return _VGReversedZ != 0 ? min(a,b) : max(a,b);
}

bool OutsideFrustum(float3 center,float radius)
{
    for(uint plane=0;plane<6;plane++)
    {
        if(dot(_VGFrustumPlanes[plane].xyz,center)+_VGFrustumPlanes[plane].w < -radius) { return true; }
    }
    return false;
}

bool Occluded(float3 center,float radius)
{
    // カメラ近傍・near planeとの交差では保守的に描画する。
    if(length(center-_VGCameraPosition) <= radius+_VGNearPlane) { return false; }
    if(dot(_VGFrustumPlanes[4].xyz,center)+_VGFrustumPlanes[4].w <= radius) { return false; }
    float2 uvMin=float2(1,1);
    float2 uvMax=float2(0,0);
    float nearest=_VGReversedZ != 0 ? 0.0 : 1.0;
    for(uint corner=0;corner<8;corner++)
    {
        float3 sign=float3((corner&1) != 0 ? 1.0 : -1.0,(corner&2) != 0 ? 1.0 : -1.0,(corner&4) != 0 ? 1.0 : -1.0);
        float4 clip=mul(_VGViewProjection,float4(center+sign*radius,1));
        if(clip.w <= 0.00001) { return false; }
        float3 ndc=clip.xyz/clip.w;
        float2 uv=float2(ndc.x,ndc.y*_VGTextureYSign)*0.5+0.5;
        uvMin=min(uvMin,uv);uvMax=max(uvMax,uv);
        nearest=_VGReversedZ != 0 ? max(nearest,ndc.z) : min(nearest,ndc.z);
    }
    // 画素境界を1px拡張し、最大2x2 texelに収まるmipから全領域の最遠深度を取得する。
    float2 pixelMin=clamp(uvMin*_VGScreenSize.xy-1,0,_VGScreenSize.xy-1);
    float2 pixelMax=clamp(uvMax*_VGScreenSize.xy+1,0,_VGScreenSize.xy-1);
    float extent=max(pixelMax.x-pixelMin.x,pixelMax.y-pixelMin.y);
    uint mip=(uint)clamp(ceil(log2(max(1.0,extent))),0,_VGHzbSize.z-1);
    uint2 mipSize=max(uint2(1,1),(uint2)_VGHzbSize.xy >> mip);
    uint2 a=min((uint2)floor(pixelMin/exp2((float)mip)),mipSize-1);
    uint2 b=min((uint2)floor(pixelMax/exp2((float)mip)),mipSize-1);
    float depth=Farthest(Farthest(_VGHzb.Load(int3(a,mip)),_VGHzb.Load(int3(b.x,a.y,mip))),
        Farthest(_VGHzb.Load(int3(a.x,b.y,mip)),_VGHzb.Load(int3(b,mip))));
    return _VGReversedZ != 0 ? nearest < depth-_VGDepthBias : nearest > depth+_VGDepthBias;
}
#endif
