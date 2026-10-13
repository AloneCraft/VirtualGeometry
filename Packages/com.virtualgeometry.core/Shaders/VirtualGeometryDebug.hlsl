#ifndef VIRTUAL_GEOMETRY_DEBUG_INCLUDED
#define VIRTUAL_GEOMETRY_DEBUG_INCLUDED
StructuredBuffer<float4> _VGVisibleDebug;
int _VGDebugMode;
float _VGDebugTargetError;
float _VGLoadingCount;
half3 VGHashColor(uint id)
{
    id=(id^61)^(id>>16);id*=9;id^=id>>4;id*=0x27d4eb2d;id^=id>>15;
    return half3((id&255)/255.0,((id>>8)&255)/255.0,((id>>16)&255)/255.0)*0.8+0.2;
}
// Triangles(7)はdebug bufferを使わず、描画中の三角形番号から色を作る。
half3 VGTriangleColor(uint primitiveId)
{
    return VGHashColor(primitiveId);
}
half3 VGDebugColor(float4 data)
{
    // LodLevel: Levelが上がる(粗くなる)ほど、青(最詳細)→緑→黄→赤へ連続して変える。12段以上は赤。
    if(_VGDebugMode==2)
    {
        float t=saturate(data.y/12.0)*3.0;
        float3 blue=float3(0,0.4,1);float3 green=float3(0,0.85,0.25);float3 yellow=float3(1,0.9,0);float3 red=float3(1,0.1,0);
        if(t<1.0){return lerp(blue,green,t);}
        if(t<2.0){return lerp(green,yellow,t-1.0);}
        return lerp(yellow,red,t-2.0);
    }
    if(_VGDebugMode==3){float error=saturate(data.z/max(_VGDebugTargetError,0.0001)/2);return lerp(half3(0,0.5,1),half3(1,0,0),error);}
    if(_VGDebugMode==5){return _VGLoadingCount>0?half3(1,0.6,0):half3(0,0.8,0.2);}
    if(_VGDebugMode==6){return half3(0,0.8,0.2);}
    return VGHashColor((uint)(_VGDebugMode==4?data.w:data.x));
}
#endif