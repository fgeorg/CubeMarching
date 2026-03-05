Shader "RayMarchScene" {
    Properties {
        _MainTex ("Texture", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        [IntRange] _MaxSteps ("Max Steps", Range(1, 200)) = 50
        _MaxDist ("Max Dist", Range(1, 1000)) = 100
        _NormalDist ("Normal Dist", Range(0.00001, 0.1)) = 0.01
        _StepFactor ("Step Factor", Range(0.5, 1.0)) = 1.0
        [Toggle(_MINDISTFADEMODE_ENABLED)] _MinDistFadeMode ("Min Dist Fade Mode", Float) = 1
        _DistFadeMin ("Dist Fade Min", Range(0, 0.2)) = 0.001
        _DistFadeMax ("Dist Fade Max", Range(0, 0.2)) = 0.02
        [HideInInspector] _SdfNodeCount ("SdfNodeCount", Int) = 0
    }
    SubShader {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalRenderPipeline" "Queue"="Transparent" }
        LOD 100
        Blend SrcAlpha OneMinusSrcAlpha

        Pass {
            Name "UniversalForward"
            Tags { "LightMode"="SdfProgressive" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            // URP lighting keywords
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma shader_feature_local _ _MIXED_LIGHTING_SUBTRACTIVE
            #pragma shader_feature _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
            #pragma multi_compile_instancing
            #pragma shader_feature_local _MINDISTFADEMODE_ENABLED
            #pragma shader_feature_local _ _PROGRESSIVE_REFINEMENT_ON _PROGRESSIVE_COLOR_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct appdata {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f {
                float4 vertex : SV_POSITION;
                float3 ro : TEXCOORD0;
                float3 hitPos : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            SAMPLER(sampler_point_clamp);

            // Progressive refinement globals (set by ProgressiveRefinementFeature each frame).
            float4x4 _SdfPrevViewProjMatrix;
            TEXTURE2D(_PrevSdfDistTex);
            // UAV for ray march distance capture — written via SetRandomWriteTarget(1, currHandle).
            // Avoids MRT slot routing issues on Metal by bypassing framebuffer attachment.
            RWTexture2D<float> _CurrSdfDistTex : register(u1);
            // Progressive color accumulation globals.
            TEXTURE2D(_PrevSdfColorTex);
            RWTexture2D<float4> _CurrSdfColorTex : register(u2);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Tint;
                float _MaxDist;
                float _NormalDist;
                float _StepFactor;
                float _DistFadeMin;
                float _DistFadeMax;
                int _MaxSteps;
                int _SdfNodeCount;
            CBUFFER_END

            #include "SdfSceneDistanceGpu.hlsl"
            #include "SdfLighting.hlsl"

            v2f vert(appdata v) {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.ro = _WorldSpaceCameraPos;
                o.hitPos = TransformObjectToWorld(v.vertex.xyz);
                o.screenPos = ComputeScreenPos(o.vertex);
                return o;
            }

            float3 GetNormal(float3 p) {
                float2 e = float2(_NormalDist, 0);
                float3 n = float3(
                    GetDistanceToScene(p + e.xyy),
                    GetDistanceToScene(p + e.yxy),
                    GetDistanceToScene(p + e.yyx)
                ) - GetDistanceToScene(p);
                return normalize(n);
            }

            float RayMarch(float3 ro, float3 rd, float dO, out float minDist) {
                minDist = _MaxDist;
                [loop]
                for (int i = 0; i < _MaxSteps; i++) {
                    float3 p = ro + dO * rd;
                    float dS = GetDistanceToScene(p);
                    minDist = min(minDist, dS);
                    // Break on max dist or precision lock (dS too small relative to dO to make progress).
                    // 1e-6 is a safety margin above float epsilon (~1.19e-7).
                    if (dO > _MaxDist || dS < dO * 1e-6) break;
                    dO += dS * _StepFactor;
                }
                return dO;
            }

            void frag(v2f i,
            out float4 color : SV_Target0,
            out float  depth : SV_Depth)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // Initialize outputs so discard paths satisfy the compiler.
                color = 0;
                depth = 0;

                if (_SdfNodeCount <= 0) {
                    discard;
                }

                float2 screenUV = i.screenPos.xy / i.screenPos.w;

                float3 ro = i.ro;
                float3 rd = normalize(i.hitPos - ro);
                float minDist = _MaxDist;

#if defined(_PROGRESSIVE_REFINEMENT_ON)
                float dO = SAMPLE_TEXTURE2D(_PrevSdfDistTex, sampler_point_clamp, screenUV).r;
#elif defined(_PROGRESSIVE_COLOR_ON)
                float dO = SAMPLE_TEXTURE2D(_PrevSdfDistTex, sampler_point_clamp, screenUV).r;
                float3 oldP = ro + dO * rd;

                if (abs(GetDistanceToScene(oldP) < dO * 1e-6)) {
                    // "cache hit"
                    float4 oldClipSpacePos = TransformWorldToHClip(oldP);
                    _CurrSdfDistTex[uint2(i.vertex.xy)] = dO;
                    color = _CurrSdfColorTex[uint2(i.vertex.xy)] = _PrevSdfColorTex[uint2(i.vertex.xy)];
                    depth = oldClipSpacePos.z / oldClipSpacePos.w;
                    return;
                }
                _CurrSdfColorTex[uint2(i.vertex.xy)] = float4(0, 0, 0, 0);
#else
                float dO = 0;
#endif

                float d = RayMarch(ro, rd, dO, minDist);

                if (d > _MaxDist) {
                    color.a = 0;
                    _CurrSdfDistTex[uint2(i.vertex.xy)] = d;
                    _CurrSdfColorTex[uint2(i.vertex.xy)] = float4(0, 0, 0, 0);
                    return;
                }

                float3 p = ro + d * rd;
                float4 clipSpacePos = TransformWorldToHClip(p);
                half3 normalWS = normalize(half3(GetNormal(p)));

                SdfMaterial mat = GetMaterialAtScene(p);
                color = SdfLighting(p, normalWS, clipSpacePos, mat, half4(_Tint));

#if defined(_MINDISTFADEMODE_ENABLED)
                color.a *= smoothstep(_DistFadeMax, _DistFadeMin, minDist);
#endif
                color = saturate(color);
#if defined(_PROGRESSIVE_COLOR_ON)
                float4 prevClip = mul(_SdfPrevViewProjMatrix, float4(p, 1.0));
                float2 prevUV = prevClip.w > 0.0
                    ? float2(prevClip.x, prevClip.y * _ProjectionParams.x) / prevClip.w * 0.5 + 0.5
                    : screenUV;
                float4 prevColor = SAMPLE_TEXTURE2D(_PrevSdfColorTex, sampler_point_clamp, prevUV);
                // alpha blend with the previous frame, but normalize to the material's transparency
                float t = color.a / _Tint.a;
                color.rgb = t * color.rgb + prevColor.rgb * (1.0 - t);
                color.a = max(color.a, prevColor.a);
#endif
                depth = clipSpacePos.z / clipSpacePos.w;
                _CurrSdfDistTex[uint2(i.vertex.xy)] = d;
                _CurrSdfColorTex[uint2(i.vertex.xy)] = color;
            }
            ENDHLSL
        }
    }
    CustomEditor "RayMarchMaterialEditor"
}
