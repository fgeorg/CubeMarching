// Displays an R32_SFloat render texture as a grayscale image.
// Assign to a quad material to visualise _PrevSdfDepthTex or any depth capture RT.
Shader "Debug/DepthTex"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
        // Range multiplier — useful to amplify very small or very large depth values.
        _Scale ("Display Scale", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalRenderPipeline" }
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_point_clamp);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float  _Scale;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float v = SAMPLE_TEXTURE2D(_MainTex, sampler_point_clamp, IN.uv).r * _Scale;
                return float4(v, v, v, 1.0);
            }
            ENDHLSL
        }
    }
}
