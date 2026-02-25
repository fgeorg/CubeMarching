Shader "RayMarchScene"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _SMinKValue ("SMinKValue", Range(0,8)) = 0.3
        [IntRange] _MaxSteps ("Max Steps", Range(1, 200)) = 50
        _MaxDist ("Max Dist", Range(1, 1000)) = 100
        _SurfDist ("Surf Dist", Range(0.00001, 0.1)) = 0.001
        _NormalDist ("Normal Dist", Range(0.00001, 0.1)) = 0.01
        [KeywordEnum(Simple, Enhanced, Secant, Binary)] _MarchMode ("March Mode", Float) = 0
        _Omega ("Relaxation Factor (Enhanced)", Range(1.0, 1.8)) = 1.3
        _StepFactor ("Step Factor", Range(0.5, 1.0)) = 1.0
        _CoarseThresh ("Coarse Threshold (Secant)", Range(0.001, 1.0)) = 0.1
        _OvershootEps ("Overshoot Epsilon (Binary)", Range(0.0, 1.0)) = 0.1
        [KeywordEnum(Disabled, Alpha, Discard)] _BackfaceCullMode ("Backface Cull Mode", Float) = 1
        _BackfaceCullMin ("Backface Cull Min", Range(0, 1.0)) = 0.1
        _BackfaceCullMax ("Backface Cull Max", Range(0, 1.0)) = 0.5
        _BackfaceCullThreshold ("Backface Cull Threshold", Range(0.0, 1.0)) = 0.0
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
            #pragma shader_feature _MARCHMODE_SIMPLE _MARCHMODE_ENHANCED _MARCHMODE_SECANT _MARCHMODE_BINARY
            #pragma shader_feature _BACKFACECULLMODE_DISABLED _BACKFACECULLMODE_ALPHA _BACKFACECULLMODE_DISCARD
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
                float4x4 _TorusTransform;
                float4x4 _SphereTransform;
                float4x4 _BoxTransform;
                float _SMinKValue;
                float _MaxDist;
                float _SurfDist;
                float _NormalDist;
                float _Omega;
                float _StepFactor;
                float _CoarseThresh;
                float _OvershootEps;
                float _BackfaceCullMin;
                float _BackfaceCullMax;
                float _BackfaceCullThreshold;
                int _MaxSteps;
            CBUFFER_END

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                // ray origin and hit position in world space
                o.ro = _WorldSpaceCameraPos;
                o.hitPos = TransformObjectToWorld(v.vertex.xyz);
                return o;
            }

            // https://www.iquilezles.org/www/articles/smin/smin.htm

            float SMinPoly(float a, float b, float k)
            {
                float h = max(k - abs(a - b), 0.0) / k;
                return min(a, b) - h * h * k * (1.0 / 4.0);
            }

            float SMinCubic(float a, float b, float k)
            {
                float h = max(k - abs(a - b), 0.0) / k;
                return min(a, b) - h * h * h * k * (1.0 / 6.0);
            }

            float SMinExp(float a, float b, float k)
            {
                float res = exp2(-k * a) + exp2(-k * b);
                return -log2(res) / k;
            }

            float SMinPow(float a, float b, float k)
            {
                a = pow(a, k); b = pow(b, k);
                return pow((a * b) / (a + b), 1.0 / k);
            }

            float GetDistToSphere(float3 p)
            {
                p = mul(_SphereTransform, float4(p, 1)).xyz;
                return length(p) - 0.6;
            }

            float GetDistToTorus(float3 p)
            {
                p = mul(_TorusTransform, float4(p, 1)).xyz;
                return length(float2(length(p.xy) - .4, p.z)) - .15;
            }

            float GetDistToBox(float3 p)
            {
                p = mul(_BoxTransform, float4(p, 1)).xyz;
                float3 q = abs(p) - 0.5;
                return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
            }

            float GetDist(float3 p)
            {
                float dSphere = GetDistToSphere(p);
                float dTorus = GetDistToTorus(p);
                float dBox = GetDistToBox(p);
                return SMinPoly(dBox, SMinPoly(dSphere, dTorus, _SMinKValue), _SMinKValue);
            }

            float3 GetNormal(float3 p)
            {
                float2 e = float2(_NormalDist, 0);
                float3 n = float3(
                    GetDist(p + e.xyy),
                    GetDist(p + e.yxy),
                    GetDist(p + e.yyx)
                ) - GetDist(p);
                return normalize(n);
            }

            float2 RayMarchSimple(float3 ro, float3 rd)
            {
                float dO = 0;
                int i = 0;
                for (; i < _MaxSteps; i++)
                {
                    float3 p = ro + dO * rd;
                    float dS = GetDist(p);
                    if (dS < _SurfDist || dO > _MaxDist) break;
                    dO += dS * _StepFactor;
                }
                return float2(dO, float(i) / float(_MaxSteps));
            }

            // Enhanced Sphere Tracing - Keinert et al. 2014
            // Uses relaxed steps (omega > 1) and partially rolls back when an overstep is detected.
            // prevRadius and convergence are only updated on non-failed steps to avoid using
            // values sampled at the (potentially invalid) overstepped position.
            float2 RayMarch(float3 ro, float3 rd)
            {
                float omega = _Omega;
                float dO = 0;
                float prevRadius = 0;
                float stepLength = 0;
                int i = 0;

                for (; i < _MaxSteps && dO < _MaxDist; i++)
                {
                    float radius = GetDist(ro + rd * dO);

                    // Overstep detection: the safe spheres at consecutive steps must overlap.
                    // If prevRadius + radius < stepLength they don't, so we overstepped.
                    bool sorFailed = omega > 1.0 && (radius + prevRadius) < stepLength;

                    if (sorFailed)
                    {
                        stepLength = prevRadius - stepLength;
                        omega = 1.0;
                    }
                    else
                    {
                        stepLength = radius * omega;
                        prevRadius = radius;
                        if (radius < _SurfDist) break;
                    }

                    dO += stepLength * _StepFactor;
                }
                return float2(dO, float(i) / float(_MaxSteps));
            }

            // Secant refinement.
            // Phase 1: standard sphere trace, stop when dS < _CoarseThresh.
            // Phase 2: secant method — linearly predicts where dS reaches 0 using the two
            //          most recent samples. One GetDist call per step, superlinear convergence.
            //          If the secant step lands inside the surface (dS < 0), falls back to
            //          bisection on the bracketed interval.
            float2 RayMarchSecant(float3 ro, float3 rd)
            {
                // t0/f0 = previous sample, t1/f1 = current sample
                float t0 = 0, f0 = _CoarseThresh * 2.0; // dummy: outside coarse zone
                float t1 = 0, f1 = 0;
                int i = 0;

                // Phase 1: sphere trace until within _CoarseThresh
                for (; i < _MaxSteps && t1 < _MaxDist; i++)
                {
                    f1 = GetDist(ro + rd * t1);
                    if (f1 < _SurfDist) return float2(t1, float(i) / float(_MaxSteps));
                    if (f1 < _CoarseThresh) break;
                    t0 = t1; f0 = f1;
                    t1 += f1 * _StepFactor;
                }

                // If we never entered the coarse zone, the ray missed
                if (f1 >= _CoarseThresh) return float2(t1, float(i) / float(_MaxSteps));

                // Phase 2: secant method
                for (; i < _MaxSteps; i++)
                {
                    float denom = f1 - f0;
                    // Secant step: extrapolate linearly to where dS = 0.
                    // Clamped to [t1, t1 + f1] so it can't diverge past one safe sphere step.
                    float t2 = (abs(denom) > 1e-7) ? t1 - f1 * (t1 - t0) / denom : t1 + f1;
                    t2 = clamp(t2, t1, t1 + f1);
                    float f2 = GetDist(ro + rd * t2);

                    if (abs(f2) < _SurfDist) return float2(t2, float(i) / float(_MaxSteps));

                    // Secant stepped inside the surface — now we have a proper bracket.
                    // Bisect between t1 (outside) and t2 (inside).
                    if (f2 < 0.0)
                    {
                        float lo = t1, hi = t2;
                        for (int b = 0; b < 8; b++)
                        {
                            float mid = (lo + hi) * 0.5;
                            float fMid = GetDist(ro + rd * mid);
                            if (abs(fMid) < _SurfDist) return float2(mid, 1.0);
                            if (fMid < 0.0) hi = mid; else lo = mid;
                        }
                        return float2(lo, 1.0);
                    }

                    t0 = t1; f0 = f1;
                    t1 = t2; f1 = f2;
                }

                return float2(t1, float(i) / float(_MaxSteps));
            }

            // Pure binary search.
            // Phase 1: standard sphere trace with step = dS * (1 + _OvershootEps).
            //          The epsilon overcomes smin's underestimation, eventually stepping
            //          inside the surface and getting a negative dS reading.
            // Phase 2: classic bisection on the bracketed interval [lo, hi] where
            //          dS(lo) > 0 and dS(hi) < 0. Halves the interval each step —
            //          no risk of divergence, guaranteed convergence.
            float2 RayMarchBinary(float3 ro, float3 rd)
            {
                float lo = 0;
                float dO = 0;
                int i = 0;
                bool bracketed = false;

                // Phase 1: march with overshoot until we step inside the surface
                for (; i < _MaxSteps && dO < _MaxDist; i++)
                {
                    float dS = GetDist(ro + rd * dO);
                    if (dS < _SurfDist) return float2(dO, float(i) / float(_MaxSteps));
                    if (dS < 0.0) { bracketed = true; break; }
                    lo = dO;
                    dO += dS * (1 + _OvershootEps);
                }

                if (!bracketed) return float2(dO, float(i) / float(_MaxSteps));

                // Phase 2: bisect [lo, dO] — lo is outside (dS > 0), dO is inside (dS < 0)
                float hi = dO;
                for (; i < _MaxSteps; i++)
                {
                    float mid = (lo + hi) * 0.5;
                    float fMid = GetDist(ro + rd * mid);
                    if (abs(fMid) < _SurfDist) return float2(mid, float(i) / float(_MaxSteps));
                    if (fMid < 0.0) hi = mid; else lo = mid;
                }

                return float2(lo, float(i) / float(_MaxSteps));
            }

            float3 GetLighting(float3 p, float3 worldNormal)
            {
                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    float4 shadowCoord = TransformWorldToShadowCoord(p);
                    Light mainLight = GetMainLight(shadowCoord);
                #else
                    Light mainLight = GetMainLight();
                #endif

                half nl = max(0, dot(worldNormal, mainLight.direction));
                float3 col = nl * mainLight.color * mainLight.shadowAttenuation * mainLight.distanceAttenuation;

                // Ambient / spherical harmonics
                col += SampleSH(worldNormal);

                #ifdef _ADDITIONAL_LIGHTS
                    int additionalLightsCount = GetAdditionalLightsCount();
                    for (int j = 0; j < additionalLightsCount; ++j)
                    {
                        Light light = GetAdditionalLight(j, p);
                        half addNL = max(0, dot(worldNormal, light.direction));
                        col += addNL * light.color * light.shadowAttenuation * light.distanceAttenuation;
                    }
                #endif

                // Per-primitive coloring: modulate RGB by distance to each SDF primitive
                col *= 1 - float3(GetDistToSphere(p), GetDistToTorus(p), GetDistToBox(p));
                return col;
            }

            void frag(v2f i, out float4 color : SV_Target, out float depth : SV_Depth)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float3 ro = i.ro;
                float3 rd = normalize(i.hitPos - ro);

                #if defined(_MARCHMODE_BINARY)
                    float2 rm = RayMarchBinary(ro, rd);
                #elif defined(_MARCHMODE_SECANT)
                    float2 rm = RayMarchSecant(ro, rd);
                #elif defined(_MARCHMODE_ENHANCED)
                    float2 rm = RayMarch(ro, rd);
                #else
                    float2 rm = RayMarchSimple(ro, rd);
                #endif

                float d = rm.x;
                float4 col = 0;
                col.a = 1;

                if (d > _MaxDist)
                {
                    col.a = 0;
                    discard;
                }

                float3 p = ro + d * rd;
                float3 worldNormal = GetNormal(p);
                col.rgb = GetLighting(p, worldNormal);

                float ndotv = dot(worldNormal, rd);
                #if defined(_BACKFACECULLMODE_DISCARD)
                    if (ndotv > _BackfaceCullThreshold) discard;
                #elif defined(_BACKFACECULLMODE_ALPHA)
                    col.a = 1 - smoothstep(_BackfaceCullMin, _BackfaceCullMax, ndotv);
                #endif

                color = saturate(col);

                float4 clipSpacePos = TransformWorldToHClip(p);
                depth = clipSpacePos.z / clipSpacePos.w;
            }
            ENDHLSL
        }
    }
    CustomEditor "RayMarchMaterialEditor"
}
