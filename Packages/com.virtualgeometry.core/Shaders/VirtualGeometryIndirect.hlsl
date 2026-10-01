#ifndef VIRTUAL_GEOMETRY_INDIRECT_INCLUDED
#define VIRTUAL_GEOMETRY_INDIRECT_INCLUDED
#include "UnityIndirect.cginc"
ByteAddressBuffer _VGDrawArguments;
uint _VGDrawCommand;
uint VGDrawVertexId(uint vertexId)
{
    // Unity DX12 indirect draws expose a command-local SV_VertexID. The installed
    // UnityIndirect helper also handles backends that already include startVertex.
    IndirectDrawArgs arguments;
    GetIndirectDrawArgs(arguments, _VGDrawArguments, _VGDrawCommand);
    return GetIndirectVertexID_Base(arguments, vertexId);
}
#endif