// CosmicScale の不透明な天体（CosmicPlanetLit、CosmicGasGiant、CosmicStarSurface）と半透明の殻で、深度を書くパス(ForwardLit、DepthOnly、DepthNormals)のクリップ座標とSV_Depthを
// ビット単位で一致させます。RadialInject.hlslの後に読み込みます。
// SolarSystem/Shaders/Common/SolarDepthInvariance.hlsl（元は EarthToMoon の E2DepthInvariance.hlsl）の複製です(デモを消してもこのデモがコンパイルできるように、他のデモのコードは読みません)。
//
// 既定のレンダラー(PC_High_Renderer)はDepth Priming(Forced)のため、DepthOnly/DepthNormalsで深度を先に書き、
// ForwardLitはZTest Equalで描かれます。同じ式でもパスごとに別々にコンパイルされると、積和の融合や演算順の違いで
// 1ulpずれることがあり、ずれた画素はForwardLitが描かれず背景色が穴として残ります(EarthToMoonで観測)。
// preciseで、これらの値を作る演算の並べ替えと融合を禁じます。各パスは必ずこの関数を通して計算します。
#ifndef COSMIC_DEPTH_INVARIANCE_INCLUDED
#define COSMIC_DEPTH_INVARIANCE_INCLUDED

// Logical(Radial)経路のクリップ座標。
float4 CosmicInvariantLogicalClip(float3 positionOS)
{
    precise float4 positionCS = OmniRadialCameraRelativeToHClip(OmniRadialObjectToCameraRelative(positionOS));
    return positionCS;
}

// Radialを使わない経路のクリップ座標。
float4 CosmicInvariantObjectClip(float3 positionOS)
{
    precise float4 positionCS = TransformObjectToHClip(positionOS);
    return positionCS;
}

// SV_Depthを復元するための同次object座標(OmniRadialDepthPosition)。
float4 CosmicInvariantDepthPosition(float3 positionOS)
{
    precise float4 depthPosition = OmniRadialDepthPosition(positionOS);
    return depthPosition;
}

// SV_Depth(OmniRadialResolveFragmentDepth)。
float CosmicInvariantFragmentDepth(float4 depthPositionOS, float rasterDepth)
{
    precise float depth = OmniRadialResolveFragmentDepth(depthPositionOS, rasterDepth);
    return depth;
}

#endif
