Shader "Hidden/VirtualizedGeometry/M5DepthConsumer"{
    SubShader{
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass{
            Blend SrcAlpha OneMinusSrcAlpha ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            struct Varyings{float4 positionCS:SV_POSITION;};
            Varyings Vert(float4 positionOS:POSITION){Varyings result;result.positionCS=TransformObjectToHClip(positionOS.xyz);return result;}
            half4 Frag(Varyings input):SV_Target{
                float depth=SampleSceneDepth(GetNormalizedScreenSpaceUV(input.positionCS));
                return half4(depth>0.00001?half3(0.1,0.8,0.3):half3(1,0,0),0.5);
            }
            ENDHLSL
        }
    }
}