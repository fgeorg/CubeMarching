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
        _Metallic ("Metallic", Range(0, 1)) = 0.0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
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
            #pragma shader_feature _MARCHMODE_SIMPLE _MARCHMODE_ENHANCED _MARCHMODE_SECANT _MARCHMODE_BINARY
            #pragma shader_feature _BACKFACECULLMODE_DISABLED _BACKFACECULLMODE_ALPHA _BACKFACECULLMODE_DISCARD
            #pragma multi_compile_fog

            // URP lighting keywords
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _MIXED_LIGHTING_SUBTRACTIVE
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "SDFLighting.hlsl"
            #include "SdfNodeTypes.hlsl"

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
                float _Metallic;
                float _Smoothness;
                int _MaxSteps;
                int _SdfNodeCount;
            CBUFFER_END

            struct SdfNode
            {
                float4 typeAndParams; // x=type, y=param0, z=param1, w=param2
                float4x4 transform;
            };
            StructuredBuffer<SdfNode> _SdfNodes;

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

            // Smooth boolean ops — https://iquilezles.org/articles/smin/
            float SmoothUnion(float a, float b, float k)
            {
                float h = max(k - abs(a - b), 0.0);
                return min(a, b) - h * h * 0.25 / k;
            }
            float SmoothSubtract(float a, float b, float k)  { return -SmoothUnion(-a, b, k); }
            float SmoothIntersect(float a, float b, float k)  { return -SmoothUnion(-a, -b, k); }

            #define STACK_SIZE 16
            struct SdfStack
            {
                float s0, s1, s2, s3, s4, s5, s6, s7;
                float s8, s9, s10, s11, s12, s13, s14, s15;
            };

            void SetStackValue(inout SdfStack stack, int index, float val)
            {
                switch(index)
                {
                    case 0: stack.s0 = val; break;
                    case 1: stack.s1 = val; break;
                    case 2: stack.s2 = val; break;
                    case 3: stack.s3 = val; break;
                    case 4: stack.s4 = val; break;
                    case 5: stack.s5 = val; break;
                    case 6: stack.s6 = val; break;
                    case 7: stack.s7 = val; break;
                    case 8: stack.s8 = val; break;
                    case 9: stack.s9 = val; break;
                    case 10: stack.s10 = val; break;
                    case 11: stack.s11 = val; break;
                    case 12: stack.s12 = val; break;
                    case 13: stack.s13 = val; break;
                    case 14: stack.s14 = val; break;
                    case 15: stack.s15 = val; break;
                }
            }

            float GetStackValue(SdfStack stack, int index)
            {
                switch(index)
                {
                    case 0: return stack.s0;
                    case 1: return stack.s1;
                    case 2: return stack.s2;
                    case 3: return stack.s3;
                    case 4: return stack.s4;
                    case 5: return stack.s5;
                    case 6: return stack.s6;
                    case 7: return stack.s7;
                    case 8: return stack.s8;
                    case 9: return stack.s9;
                    case 10: return stack.s10;
                    case 11: return stack.s11;
                    case 12: return stack.s12;
                    case 13: return stack.s13;
                    case 14: return stack.s14;
                    case 15: return stack.s15;
                }
                return 1e10; // Should not be reached
            }

            // Postfix stack evaluator. Primitives push; binary ops pop two and push result;
            // unary ops modify top in place. Stack depth 16 handles any realistic scene tree.
            float EvalScene(float3 p)
            {
                SdfStack stack = (SdfStack)0;
                int sp = 0;
                [loop]
                for (int i = 0; i < min(_SdfNodeCount, 64); i++)
                {
                    SdfNode node = _SdfNodes[i];
                    int t = (int)node.typeAndParams.x;
                    float k = node.typeAndParams.y;
                    if (t < SDF_UNION) // primitive — push
                    {
                        float3 lp = mul(node.transform, float4(p, 1.0)).xyz;
                        float d;
                        if (t == SDF_SPHERE)
                            d = length(lp) - node.typeAndParams.y;
                        else if (t == SDF_BOX)
                        {
                            float3 bh = node.typeAndParams.yzw;
                            float3 q = abs(lp) - bh;
                            d = length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
                        }
                        else // SDF_TORUS
                        {
                            float2 q2 = float2(length(lp.xy) - node.typeAndParams.y, lp.z);
                            d = length(q2) - node.typeAndParams.z;
                        }
                        if (sp < 16)
                        {
                            SetStackValue(stack, sp, d);
                            sp++;
                        }
                    }
                    else if (t >= SDF_SHELL) // unary modifier — modify top in place
                    {
                        if (sp >= 1)
                        {
                            float top = GetStackValue(stack, sp - 1);
                            if (t == SDF_SHELL) SetStackValue(stack, sp - 1, abs(top) - k);
                            else                SetStackValue(stack, sp - 1, top + k); // SDF_EXPAND
                        }
                    }
                    else if (sp >= 2) // binary operator — pop two, push result
                    {
                        sp--;
                        float b = GetStackValue(stack, sp);
                        sp--;
                        float a = GetStackValue(stack, sp);
                        float r;
                        if      (t == SDF_UNION)            r = min(a, b);
                        else if (t == SDF_SMOOTH_UNION)     r = SmoothUnion(a, b, k);
                        else if (t == SDF_INTERSECT)        r = max(a, b);
                        else if (t == SDF_SUBTRACT)         r = max(a, -b);
                        else if (t == SDF_SMOOTH_INTERSECT) r = SmoothIntersect(a, b, k);
                        else                                r = SmoothSubtract(a, b, k); // SDF_SMOOTH_SUBTRACT
                        SetStackValue(stack, sp, r);
                        sp++;
                    }
                }
                return sp > 0 ? GetStackValue(stack, 0) : 1e10;
            }

            float3 GetNormal(float3 p)
            {
                float2 e = float2(_NormalDist, 0);
                float3 n = float3(
                    EvalScene(p + e.xyy),
                    EvalScene(p + e.yxy),
                    EvalScene(p + e.yyx)
                ) - EvalScene(p);
                return normalize(n);
            }

            float2 RayMarchSimple(float3 ro, float3 rd)
            {
                float dO = 0;
                int i = 0;
                for (; i < _MaxSteps; i++)
                {
                    float3 p = ro + dO * rd;
                    float dS = EvalScene(p);
                    if (dS < _SurfDist || dO > _MaxDist) break;
                    dO += dS * _StepFactor;
                }
                return float2(dO, float(i) / float(_MaxSteps));
            }

            // Enhanced Sphere Tracing - Keinert et al. 2014
            float2 RayMarch(float3 ro, float3 rd)
            {
                float omega = _Omega;
                float dO = 0;
                float prevRadius = 0;
                float stepLength = 0;
                int i = 0;

                for (; i < _MaxSteps && dO < _MaxDist; i++)
                {
                    float radius = EvalScene(ro + rd * dO);
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

            // Secant refinement
            float2 RayMarchSecant(float3 ro, float3 rd)
            {
                float t0 = 0, f0 = _CoarseThresh * 2.0;
                float t1 = 0, f1 = 0;
                int i = 0;

                for (; i < _MaxSteps && t1 < _MaxDist; i++)
                {
                    f1 = EvalScene(ro + rd * t1);
                    if (f1 < _SurfDist) return float2(t1, float(i) / float(_MaxSteps));
                    if (f1 < _CoarseThresh) break;
                    t0 = t1; f0 = f1;
                    t1 += f1 * _StepFactor;
                }

                if (f1 >= _CoarseThresh) return float2(t1, float(i) / float(_MaxSteps));

                for (; i < _MaxSteps; i++)
                {
                    float denom = f1 - f0;
                    float t2 = (abs(denom) > 1e-7) ? t1 - f1 * (t1 - t0) / denom : t1 + f1;
                    t2 = clamp(t2, t1, t1 + f1);
                    float f2 = EvalScene(ro + rd * t2);

                    if (abs(f2) < _SurfDist) return float2(t2, float(i) / float(_MaxSteps));

                    if (f2 < 0.0)
                    {
                        float lo = t1, hi = t2;
                        for (int b = 0; b < 8; b++)
                        {
                            float mid = (lo + hi) * 0.5;
                            float fMid = EvalScene(ro + rd * mid);
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

            // Pure binary search
            float2 RayMarchBinary(float3 ro, float3 rd)
            {
                float lo = 0;
                float dO = 0;
                int i = 0;
                bool bracketed = false;

                for (; i < _MaxSteps && dO < _MaxDist; i++)
                {
                    float dS = EvalScene(ro + rd * dO);
                    if (dS < _SurfDist) return float2(dO, float(i) / float(_MaxSteps));
                    if (dS < 0.0) { bracketed = true; break; }
                    lo = dO;
                    dO += dS * (1 + _OvershootEps);
                }

                if (!bracketed) return float2(dO, float(i) / float(_MaxSteps));

                float hi = dO;
                for (; i < _MaxSteps; i++)
                {
                    float mid = (lo + hi) * 0.5;
                    float fMid = EvalScene(ro + rd * mid);
                    if (abs(fMid) < _SurfDist) return float2(mid, float(i) / float(_MaxSteps));
                    if (fMid < 0.0) hi = mid; else lo = mid;
                }

                return float2(lo, float(i) / float(_MaxSteps));
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
                float4 clipSpacePos = TransformWorldToHClip(p);
                half3 normalWS = normalize(half3(GetNormal(p)));

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = half3(1.0, 1.0, 1.0);
                surfaceData.metallic   = (half)_Metallic;
                surfaceData.smoothness = (half)_Smoothness;
                surfaceData.occlusion  = 1.0h;
                surfaceData.alpha      = 1.0h;
                surfaceData.normalTS   = half3(0, 0, 1);

                col.rgb = SDFLighting(p, normalWS, clipSpacePos, surfaceData);

                float ndotv = dot(normalWS, rd);
                #if defined(_BACKFACECULLMODE_DISCARD)
                    if (ndotv > _BackfaceCullThreshold) discard;
                #elif defined(_BACKFACECULLMODE_ALPHA)
                    col.a = 1 - smoothstep(_BackfaceCullMin, _BackfaceCullMax, ndotv);
                #endif

                color = saturate(col);
                depth = clipSpacePos.z / clipSpacePos.w;
            }
            ENDHLSL
        }
    }
    CustomEditor "RayMarchMaterialEditor"
}
