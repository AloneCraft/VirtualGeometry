Shader "VirtualizedGeometry/Diagnostics/M0Indirect"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            Name "M0Indirect"
            ZWrite On
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            StructuredBuffer<float4> _VGVertices;
            struct Varyings { float4 positionCS:SV_POSITION; };
            Varyings Vert(uint vertexId:SV_VertexID)
            {
                Varyings output;
                output.positionCS=TransformWorldToHClip(_VGVertices[vertexId].xyz);
                return output;
            }
            half4 Frag(Varyings input):SV_Target
            {
                return half4(0.05,0.65,0.95,1.0);
            }
            ENDHLSL
        }
    }
}
