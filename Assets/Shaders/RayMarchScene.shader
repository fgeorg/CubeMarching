Shader "RayMarchScene"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Metallic ("Metallic", Range(0, 1)) = 0.0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5

        [IntRange] _MaxSteps ("Max Steps", Range(1, 200)) = 50
        _MaxDist ("Max Dist", Range(1, 1000)) = 100
        _SurfDist ("Surf Dist", Range(0.00001, 0.1)) = 0.001
        _NormalDist ("Normal Dist", Range(0.00001, 0.1)) = 0.01
        _StepFactor ("Step Factor", Range(0.5, 1.0)) = 1.0
        [KeywordEnum(Disabled, Alpha, Discard)] _BackfaceCullMode ("Backface Cull Mode", Float) = 1
        _BackfaceCullMin ("Backface Cull Min", Range(0, 1.0)) = 0.1
        _BackfaceCullMax ("Backface Cull Max", Range(0, 1.0)) = 0.5
        _BackfaceCullThreshold ("Backface Cull Threshold", Range(0.0, 1.0)) = 0.0
        [HideInInspector] _SdfNodeCount ("SdfNodeCount", Int) = 0
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
            #pragma target 4.5
            // URP lighting keywords
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma shader_feature_local _ _MIXED_LIGHTING_SUBTRACTIVE
            #pragma shader_feature _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
            #pragma multi_compile_instancing
            #pragma shader_feature_local _BACKFACECULLMODE_DISABLED _BACKFACECULLMODE_ALPHA _BACKFACECULLMODE_DISCARD

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "SdfLighting.hlsl"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

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
                float4 _Tint;
                float _MaxDist;
                float _SurfDist;
                float _NormalDist;
                float _StepFactor;
                float _Metallic;
                float _Smoothness;
                float _BackfaceCullMin;
                float _BackfaceCullMax;
                float _BackfaceCullThreshold;
                int _MaxSteps;
                int _SdfNodeCount;
            CBUFFER_END

            #include "SdfSceneDistanceGpu.hlsl"

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.ro = _WorldSpaceCameraPos;
                o.hitPos = TransformObjectToWorld(v.vertex.xyz);
                return o;
            }

            float3 GetNormal(float3 p)
            {
                float2 e = float2(_NormalDist, 0);
                float3 n = float3(
                GetDistanceToScene(p + e.xyy),
                GetDistanceToScene(p + e.yxy),
                GetDistanceToScene(p + e.yyx)
                ) - GetDistanceToScene(p);
                return normalize(n);
            }

            float RayMarch(float3 ro, float3 rd)
            {
                float dO = 0;
                [loop]
                for (int i = 0; i < _MaxSteps; i++)
                {
                    float3 p = ro + dO * rd;
                    float dS = GetDistanceToScene(p);
                    if (dS < _SurfDist || dO > _MaxDist) break;
                    dO += dS * _StepFactor;
                }
                return dO;
            }

            void frag(v2f i, out float4 color : SV_Target, out float depth : SV_Depth)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                if (_SdfNodeCount <= 0)
                {
                    color = 0;
                    depth = 0;
                    discard;
                }

                float3 ro = i.ro;
                float3 rd = normalize(i.hitPos - ro);

                float d = RayMarch(ro, rd);
                float4 col = 0;
                col.a = 1;

                if (d > _MaxDist)
                {
                    col.a = 0;
                    discard;
                }

                float3 p = ro + d * rd;
                float4 clipSpacePos = TransformWorldToHClip(p);
                half3 normalWS = normalize(half3(GetNormal(p)));

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = half3(_Tint.rgb);
                surfaceData.metallic   = (half)_Metallic;
                surfaceData.smoothness = (half)_Smoothness;
                surfaceData.occlusion  = 1.0h;
                surfaceData.alpha      = 1.0h;
                surfaceData.normalTS   = half3(0, 0, 1);

                col.rgb = SdfLighting(p, normalWS, clipSpacePos, surfaceData);
                col.a = _Tint.a;

                float ndotv = dot(normalWS, rd);
#if defined(_BACKFACECULLMODE_DISCARD)
                if (ndotv > _BackfaceCullThreshold) discard;
#elif defined(_BACKFACECULLMODE_ALPHA)
                col.a *= 1 - smoothstep(_BackfaceCullMin, _BackfaceCullMax, ndotv);
#endif

                color = saturate(col);
                depth = clipSpacePos.z / clipSpacePos.w;
            }
            ENDHLSL
        }
    }
    CustomEditor "RayMarchMaterialEditor"
}
