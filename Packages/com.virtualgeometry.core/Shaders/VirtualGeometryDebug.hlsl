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
    if(_VGDebugMode==2){return frac(float3(0.17,0.41,0.73)*(data.y+1));}
    if(_VGDebugMode==3){float error=saturate(data.z/max(_VGDebugTargetError,0.0001)/2);return lerp(half3(0,0.5,1),half3(1,0,0),error);}
    if(_VGDebugMode==5){return _VGLoadingCount>0?half3(1,0.6,0):half3(0,0.8,0.2);}
    if(_VGDebugMode==6){return half3(0,0.8,0.2);}
    return VGHashColor((uint)(_VGDebugMode==4?data.w:data.x));
}
#endif