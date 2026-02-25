Shader "RayMarch"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalRenderPipeline" "Queue"="Transparent" }
        LOD 100
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog

            // URP lighting and shadow keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _MIXED_LIGHTING_SUBTRACTIVE
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            #define MAX_STEPS 50
            #define MAX_DIST 100
            #define SURF_DIST 1e-3

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 ro : TEXCOORD0;
                float3 hitPos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
            CBUFFER_END

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                // ray origin and hit position in object space
                o.ro = TransformWorldToObject(_WorldSpaceCameraPos);
                o.hitPos = v.vertex.xyz;
                return o;
            }

            float SMinCubic(float a, float b, float k)
            {
                float h = max(k - abs(a - b), 0.0) / k;
                return min(a, b) - h * h * h * k * (1.0 / 6.0);
            }

            float GetDist(float3 p)
            {
                float4 s = float4(0.0, 0.0, 0.0, 0.2);
                float dSphere = length(p - s.xyz) - s.w;
                float dTorus = length(float2(length(p.xy) - .4, p.z)) - .1;
                return SMinCubic(dSphere, dTorus, .30 + sin(_Time.x * 50) * .14);
            }

            float3 GetNormal(float3 p)
            {
                float2 e = float2(1e-2, 0);
                float3 n = float3(
                    GetDist(p + e.xyy),
                    GetDist(p + e.yxy),
                    GetDist(p + e.yyx)
                ) - GetDist(p);
                return normalize(n);
            }

            float2 RayMarch(float3 ro, float3 rd)
            {
                float dO = 0;
                int i = 0;
                for (; i < MAX_STEPS; i++)
                {
                    float3 p = ro + dO * rd;
                    float dS = GetDist(p);
                    if (dS < SURF_DIST || dO > MAX_DIST) break;
                    dO += dS;
                }
                return float2(dO, float(i) / MAX_STEPS);
            }

            float3 GetLighting(float3 posOS)
            {
                float3 normalOS = GetNormal(posOS);
                float3 normalWS = TransformObjectToWorldNormal(normalOS);
                float3 posWS = TransformObjectToWorld(posOS);

                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    float4 shadowCoord = TransformWorldToShadowCoord(posWS);
                    Light mainLight = GetMainLight(shadowCoord);
                #else
                    Light mainLight = GetMainLight();
                #endif

                half nl = max(0, dot(normalWS, mainLight.direction));
                float3 col = nl * mainLight.color * mainLight.shadowAttenuation * mainLight.distanceAttenuation;

                // Ambient / spherical harmonics
                col += SampleSH(normalWS);

                #ifdef _ADDITIONAL_LIGHTS
                    int additionalLightsCount = GetAdditionalLightsCount();
                    for (int j = 0; j < additionalLightsCount; ++j)
                    {
                        Light light = GetAdditionalLight(j, posWS);
                        half addNL = max(0, dot(normalWS, light.direction));
                        col += addNL * light.color * light.shadowAttenuation * light.distanceAttenuation;
                    }
                #endif

                return col;
            }

            void frag(v2f i, out float4 color : SV_Target, out float depth : SV_Depth)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float3 ro = i.ro;
                float3 rd = normalize(i.hitPos - ro);

                float2 rm = RayMarch(ro, rd);
                float d = rm.x;
                float4 col = 0;
                col.a = 1;

                if (d > MAX_DIST)
                {
                    col.a = 0;
                    discard;
                }

                float3 p = ro + d * rd;
                col.rgb = GetLighting(p);
                col.b += rm.y;
                if (col.g < 0)
                {
                    col.g *= -.1;
                }

                color = saturate(col);

                float4 clipSpacePos = TransformObjectToHClip(p);
                depth = clipSpacePos.z / clipSpacePos.w;
            }
            ENDHLSL
        }
    }
}
