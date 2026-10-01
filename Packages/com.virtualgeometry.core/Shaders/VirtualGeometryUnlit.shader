Shader "VirtualizedGeometry/Unlit"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Name "VirtualGeometryForward"
            ZWrite On
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "VirtualGeometryDebug.hlsl"
            #include "VirtualGeometryIndirect.hlsl"
            struct VGVertex
            {
                float3 position;
                float3 normal;
                float2 uv;
            };
            StructuredBuffer<VGVertex> _VGVertices;
            StructuredBuffer<uint> _VGIndices;
            float4x4 _VGObjectToWorld;
            half4 _VGBaseColor;
            struct Varyings
            {
                float4 positionCS:SV_POSITION;
                float2 uv:TEXCOORD0;nointerpolation float4 debug:TEXCOORD1;
            };
            Varyings Vert(uint vertexId:SV_VertexID)
            {
                vertexId=VGDrawVertexId(vertexId);
                VGVertex vertex=_VGVertices[_VGIndices[vertexId]];
                Varyings output;
                float3 worldPosition=mul(_VGObjectToWorld,float4(vertex.position,1.0)).xyz;
                output.positionCS=TransformWorldToHClip(worldPosition);
                output.uv=vertex.uv;output.debug=_VGDebugMode>0&&_VGDebugMode<=4?_VGVisibleDebug[vertexId]:0;
                return output;
            }
            half4 Frag(Varyings input,uint primitiveId:SV_PrimitiveID):SV_Target
            {
                if(_VGDebugMode==7){return half4(VGTriangleColor(primitiveId),1);}
                return _VGDebugMode!=0?half4(VGDebugColor(input.debug),1):_VGBaseColor;
            }
            ENDHLSL
        }
    }
}
