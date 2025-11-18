Shader "Hidden/VHSCompositeURP"
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

            TEXTURE2D_X(_BlurredTex);
            TEXTURE2D_X(_SmearedTex);
            TEXTURE2D_X(_Grain);
            TEXTURE2D_X(_CRT);
            float4 _Grain_TexelSize;
            float _ColorBleedIntensity;
            float _SmearIntensity;
            float _EdgeIntensity;
            float _EdgeDistance;
            float _GrainIntensity;
            float4 _GrainScaleOffset;
            float4 _CRTScaleIntensity;
            
            float3 RGBToYCbCr(float3 rgb) {
                return float3(0.0625 + 0.257 * rgb.r + 0.50412 * rgb.g + 0.0979 * rgb.b,
                    0.5 - 0.14822 * rgb.r - 0.290 * rgb.g + 0.43921 * rgb.b,
                    0.5 + 0.43921 * rgb.r - 0.3678 * rgb.g - 0.07142 * rgb.b);
            }
            float3 YCbCrToRGB(float3 ycbcr) {
                
                ycbcr -= float3(0.0625, 0.5, 0.5);
                return float3(1.164 * ycbcr.x + 1.596 * ycbcr.z,
                    1.164 * ycbcr.x - 0.392 * ycbcr.y - 0.813 * ycbcr.z,
                    1.164 * ycbcr.x + 2.017 * ycbcr.y);
            }

            float4 Frag (Varyings input) : SV_Target
            {
                //float2 quarterpixel = _BlitTexture_TexelSize.xy * 0.25;
                
                float4 sharpColor = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord);

                float3 edges = sharpColor.rgb + 0.5 - (SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord - float2(_EdgeDistance, 0)).rgb);
                sharpColor.rgb += (edges - 0.5) * _EdgeIntensity;
                
                float3 smearedColor = SAMPLE_TEXTURE2D(_SmearedTex, sampler_LinearClamp, input.texcoord).rgb;
                sharpColor.rgb = lerp(sharpColor.rgb, smearedColor.rgb, _SmearIntensity);
                
                sharpColor.xyz = RGBToYCbCr(sharpColor.rgb);
                
                float3 blurredColor = RGBToYCbCr(SAMPLE_TEXTURE2D(_BlurredTex, sampler_LinearClamp, input.texcoord).rgb).xyz;
                float2 colorGrain = RGBToYCbCr(SAMPLE_TEXTURE2D(_Grain, sampler_LinearRepeat, (input.texcoord - _GrainScaleOffset.zw) * _GrainScaleOffset.xy).rgb).yz;
                float lumGrain = SAMPLE_TEXTURE2D(_Grain, sampler_LinearRepeat, (input.texcoord - _GrainScaleOffset.zw) * _GrainScaleOffset.xy * 4 - 0.5).g;
                sharpColor.yz = lerp(sharpColor.yz, blurredColor.yz, _ColorBleedIntensity);
                sharpColor.yz += (colorGrain.xy - 0.5) * _GrainIntensity * sharpColor.x;
                sharpColor.x *= 1 + (lumGrain - 0.5) * _GrainIntensity * 0.5;

                float4 color = float4(YCbCrToRGB(sharpColor.rgb), sharpColor.a);

                float4 crt = SAMPLE_TEXTURE2D(_CRT, sampler_LinearRepeat, input.texcoord * _CRTScaleIntensity.xy);
                float scanlines = lerp(1, crt.a * 1.5, _CRTScaleIntensity.w);
                crt.rgb = lerp(1, crt * 6 * scanlines, _CRTScaleIntensity.z);
                crt.rgb *= scanlines;
                color.rgb *= crt.rgb;
                
                return color;
            }
            
            ENDHLSL
        }
    }
}
