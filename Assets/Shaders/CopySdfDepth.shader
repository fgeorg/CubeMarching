// Blitter-compatible shader that copies the hardware depth value (from _BlitTexture,
// which Blitter.BlitTexture binds automatically) into a single-channel RFloat color target.
// Used by TemporalWarmStartFeature to capture per-frame SDF march depths.
Shader "Hidden/CopySdfDepth"
{
    SubShader
    {
        Pass
        {
            Name "CopySdfDepth"
            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            SAMPLER(sampler_point_clamp);

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float depth = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_point_clamp, input.texcoord).r;
                return float4(depth, 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }
    }
}
