Shader "Hidden/VHSSmearURP"
{   
    SubShader
    {
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        ENDHLSL

        Tags { "RenderType"="Opaque" }
        LOD 100
        ZWrite Off Cull Off
        Pass
        {
            Name "Smear"

            HLSLPROGRAM
            
            #pragma vertex Vert
            #pragma fragment Frag

            float4 _TexelSize;
            float2 _SmearOffsetAttenuation;
            #define SMEAR_LENGTH 4

            float4 Frag (Varyings input) : SV_Target
            {
                float4 color = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord);
                float energy = 1;
                [unroll]
                for (uint o = 1; o <= SMEAR_LENGTH; o++)
                {
                    float falloff = exp(-_SmearOffsetAttenuation.y * o);
                    energy += falloff;
                    float uvx = input.texcoord.x - _TexelSize.x * _SmearOffsetAttenuation.x * o;
                    color += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, float2(uvx, input.texcoord.y)) * falloff * (uvx > 0);
                }
                return color / energy;
            }
            
            ENDHLSL
        }
    }
}
