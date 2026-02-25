Shader "Custom/Flat Wireframe" {

    Properties {
        _Color              ("Tint Front",           Color)            = (1, 1, 1, 1)
        _ColorBack          ("Tint Back",            Color)            = (1, 1, 1, 1)
        _MainTex            ("Albedo",              2D)               = "white" {}
        _Cutoff             ("Alpha Cutoff",        Range(0, 1))      = 0.5
        _WireframeColor     ("Wireframe Color",     Color)            = (0, 0, 0, 1)
        _WireframeSmoothing ("Wireframe Smoothing", Range(0, 10))     = 1
        _WireframeThickness ("Wireframe Thickness", Range(0, 10))     = 1
        [Enum(UnityEngine.Rendering.CullMode)]        _Cull   ("Cull",   Float) = 2
        [Enum(Off,0,On,1)]                            _ZWrite ("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest  ("ZTest",  Float) = 4
    }

    SubShader {
        Tags {
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Geometry"
        }

        Pass {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite [_ZWrite]
            Cull   [_Cull]
            ZTest  [_ZTest]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
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

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
                float4 _ColorBack;
                float4 _WireframeColor;
                float  _WireframeSmoothing;
                float  _WireframeThickness;
                float  _Cutoff;
            CBUFFER_END

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            struct Attributes {
                float4 positionOS  : POSITION;
                float3 normalOS    : NORMAL;
                float2 uv          : TEXCOORD0;
                // Barycentric coords baked into UV1 by MeshGenerator.RegenerateWireframe().
                // Vertex i of a sequential index buffer is always corner (i%3) of its triangle,
                // so: i%3==0 → (1,0), i%3==1 → (0,1), i%3==2 → (0,0).
                float2 barycentric : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float2 uv          : TEXCOORD0;
                float2 barycentric : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs posInputs  = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs  normInputs = GetVertexNormalInputs(IN.normalOS);
                OUT.positionHCS = posInputs.positionCS;
                OUT.positionWS  = posInputs.positionWS;
                OUT.normalWS    = normInputs.normalWS;
                OUT.uv          = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.barycentric = IN.barycentric;
                return OUT;
            }

            float4 frag(Varyings IN, float vface : VFACE) : SV_Target {
                UNITY_SETUP_INSTANCE_ID(IN);

                float4 tint   = vface > 0 ? _Color : _ColorBack;
                float4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * tint;
                clip(albedo.a - _Cutoff);

                float3 normalWS = normalize(vface > 0 ? IN.normalWS : -IN.normalWS);

                InputData inputData = (InputData)0;
                inputData.positionWS          = IN.positionWS;
                inputData.normalWS            = normalWS;
                inputData.viewDirectionWS     = SafeNormalize(GetCameraPositionWS() - IN.positionWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionHCS);
                inputData.bakedGI             = SampleSH(normalWS);
                inputData.shadowMask          = unity_ProbesOcclusion;
                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #else
                    inputData.shadowCoord = float4(0, 0, 0, 0);
                #endif

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = albedo.rgb;
                surfaceData.alpha      = albedo.a;
                surfaceData.occlusion  = 1;
                surfaceData.smoothness = 0.5;
                surfaceData.normalTS   = half3(0, 0, 1);

                float3 litColor = UniversalFragmentPBR(inputData, surfaceData).rgb;

                // Wireframe overlay.
                // fwidth clamped at 0.05 — without this, small screen-space triangles flood
                // entirely with wireframe colour (threshold approaches min barycentric ~0.33).
                float3 barys     = float3(IN.barycentric, 1.0 - IN.barycentric.x - IN.barycentric.y);
                float3 deltas    = min(fwidth(barys), 0.05);
                float3 smoothing = deltas * _WireframeSmoothing;
                float3 thickness = deltas * _WireframeThickness;
                barys            = smoothstep(thickness, thickness + smoothing, barys);
                float  minBary   = min(barys.x, min(barys.y, barys.z));

                float3 finalColor = lerp(_WireframeColor.rgb, litColor, minBary);
                return float4(finalColor, albedo.a);
            }
            ENDHLSL
        }
    }
}
