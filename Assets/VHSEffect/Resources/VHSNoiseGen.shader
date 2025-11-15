Shader "Hidden/VHSNoiseGen"
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
            Name "NoiseGen"

            HLSLPROGRAM
            
            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE2D_X(_HorizontalNoise);
            TEXTURE2D_X(_StripeNoise);
            float _HorizontalNoisePos;
            float _HorizontalNoisePower;
            float4 _StripeNoiseScaleOffset;
            float _Blend;

            float4 Frag (Varyings input) : SV_Target
            {
                float horizontalNoise = SAMPLE_TEXTURE2D(_HorizontalNoise, sampler_LinearRepeat, float2(_HorizontalNoisePos, input.texcoord.y)).r;
                float2 stripeNoise = SAMPLE_TEXTURE2D(_StripeNoise, sampler_LinearRepeat, (input.texcoord - _StripeNoiseScaleOffset.zw) * _StripeNoiseScaleOffset.xy).rg;
                return stripeNoise.r * (stripeNoise.g > pow((1-horizontalNoise)* (1 - horizontalNoise), _HorizontalNoisePower));
            }
            
            ENDHLSL
        }
    }
}