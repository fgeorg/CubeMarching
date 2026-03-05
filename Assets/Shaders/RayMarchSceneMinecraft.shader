Shader "RayMarchSceneMinecraft" {
    Properties {
        _MainTex ("Texture", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        [IntRange] _MaxSteps ("Max Steps", Range(1, 200)) = 50
        _MaxDist ("Max Dist", Range(1, 1000)) = 100
        [HideInInspector] _SdfNodeCount ("SdfNodeCount", Int) = 0
        [HideInInspector] _VoxelTex       ("Voxel Tex",       3D)    = "" {}
        [HideInInspector] _VoxelOrigin    ("Voxel Origin",    Vector) = (0,0,0,0)
        [HideInInspector] _VoxelCellSize  ("Voxel Cell Size", Float)  = 0.1
        [HideInInspector] _VoxelResolution("Voxel Resolution",Float)  = 64
    }
    SubShader {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalRenderPipeline" "Queue"="Transparent" }
        LOD 100
        Blend SrcAlpha OneMinusSrcAlpha

        Pass {
            Name "UniversalForward"
            Tags { "LightMode"="SdfMrt" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            // URP lighting keywords
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma shader_feature_local _ _MIXED_LIGHTING_SUBTRACTIVE
            #pragma shader_feature _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
            #pragma multi_compile_instancing

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
            TEXTURE3D(_VoxelTex);
            SAMPLER(sampler_point_clamp);

            // UAV outputs — written by ProgressiveRefinementFeature infrastructure.
            RWTexture2D<float> _CurrSdfDistTex : register(u1);
            RWTexture2D<float4> _CurrSdfColorTex : register(u2);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Tint;
                float _MaxDist;
                int _MaxSteps;
                int _SdfNodeCount;
                float3 _VoxelOrigin;
                float  _VoxelCellSize;
                float  _VoxelResolution;
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

            // Classic DDA grid traversal. Steps one voxel cell at a time along the
            // ray, stopping as soon as it enters a solid cell (stored dist <= 0).
            // Face normal is determined by which axis boundary was just crossed —
            // unambiguous because it is recorded at step time, not inferred post-hoc.
            // Sphere-tracing skip: cells far from the surface jump ahead multiple cells.
            float RayMarchDDA(float3 ro, float3 rd, out float3 faceNormal)
            {
                faceNormal = float3(0, 1, 0);

                // Safe reciprocal: clamp near-zero components to a tiny signed value
                // so the slab test and tDelta stay well-defined.
                float3 rdSafe;
                rdSafe.x = abs(rd.x) > 1e-6 ? rd.x : (rd.x >= 0.0 ? 1e-6 : -1e-6);
                rdSafe.y = abs(rd.y) > 1e-6 ? rd.y : (rd.y >= 0.0 ? 1e-6 : -1e-6);
                rdSafe.z = abs(rd.z) > 1e-6 ? rd.z : (rd.z >= 0.0 ? 1e-6 : -1e-6);
                float3 invRd = 1.0 / rdSafe;

                // Slab test: entry/exit t for the voxel AABB.
                float3 gridMin = _VoxelOrigin;
                float3 gridMax = gridMin + _VoxelResolution * _VoxelCellSize;
                float3 t0 = (gridMin - ro) * invRd;
                float3 t1 = (gridMax - ro) * invRd;
                float tEnter = max(max(min(t0.x, t1.x), min(t0.y, t1.y)), min(t0.z, t1.z));
                float tExit  = min(min(max(t0.x, t1.x), max(t0.y, t1.y)), max(t0.z, t1.z));
                if (tEnter > tExit || tExit < 0.0) return _MaxDist + 1.0;
                tEnter = max(tEnter, 0.0);

                // Starting cell — nudge inside the grid to avoid landing exactly on a face.
                float3 pEnter = ro + (tEnter + 1e-4 * _VoxelCellSize) * rd;
                int3 cell = clamp(int3(floor((pEnter - gridMin) / _VoxelCellSize)),
                    0, int(_VoxelResolution) - 1);

                // Per-axis step direction and t increment.
                int3   stepDir = int3(sign(rd));
                float3 tDelta  = abs(_VoxelCellSize * invRd);

                // t at which the ray first crosses the far face of the starting cell
                // on each axis (measured from ro, same origin as tEnter/tExit).
                float3 nextBnd;
                nextBnd.x = gridMin.x + (rd.x >= 0.0 ? float(cell.x + 1) : float(cell.x)) * _VoxelCellSize;
                nextBnd.y = gridMin.y + (rd.y >= 0.0 ? float(cell.y + 1) : float(cell.y)) * _VoxelCellSize;
                nextBnd.z = gridMin.z + (rd.z >= 0.0 ? float(cell.z + 1) : float(cell.z)) * _VoxelCellSize;
                float3 tMax = (nextBnd - ro) * invRd;

                float invN = 1.0 / _VoxelResolution;

                [loop]
                for (int i = 0; i < _MaxSteps; i++)
                {
                    // The axis with the smallest tMax is the next face to cross.
                    float3 mask;
                    mask.x = step(tMax.x, tMax.y) * step(tMax.x, tMax.z);
                    mask.y = (1.0 - mask.x) * step(tMax.y, tMax.z);
                    mask.z = (1.0 - mask.x) * (1.0 - mask.y);

                    float t = dot(tMax, mask);
                    if (t > tExit || t > _MaxDist) break;

                    // Cross into the next cell.
                    cell += stepDir * int3(int(mask.x), int(mask.y), int(mask.z));
                    tMax += mask * tDelta;

                    if (any(cell < 0) || any(cell >= int(_VoxelResolution))) break;

                    float3 uvw  = (float3(cell.x, cell.y, cell.z) + 0.5) * invN;
                    float  dist = SAMPLE_TEXTURE3D_LOD(_VoxelTex, sampler_point_clamp, uvw, 0).r;
                    if (dist <= 0)
                    {
                        // Entered a solid cell. The face we crossed is the entry face;
                        // its outward normal opposes the ray direction on the winning axis.
                        faceNormal = -sign(rd) * mask;
                        return t;
                    }

                    // Sphere-tracing skip: if this cell is far from the surface,
                    // jump ahead along the full ray direction and rebuild all three
                    // DDA axes from the new position. Advancing along only one axis
                    // desyncs the other two on diagonal rays, causing wrong hits.
                    if (dist > _VoxelCellSize)
                    {
                        // Jump from the current face by (dist - cellSize).
                        // The -cellSize margin accounts for the face-to-center gap
                        // so we stay conservatively in empty space.
                        float3 pJump = ro + (t + dist - _VoxelCellSize) * rd;
                        cell = clamp(int3(floor((pJump - gridMin) / _VoxelCellSize)),
                            0, int(_VoxelResolution) - 1);
                        float3 nb;
                        nb.x = gridMin.x + (rd.x >= 0.0 ? float(cell.x + 1) : float(cell.x)) * _VoxelCellSize;
                        nb.y = gridMin.y + (rd.y >= 0.0 ? float(cell.y + 1) : float(cell.y)) * _VoxelCellSize;
                        nb.z = gridMin.z + (rd.z >= 0.0 ? float(cell.z + 1) : float(cell.z)) * _VoxelCellSize;
                        tMax = (nb - ro) * invRd;
                    }
                }
                return _MaxDist + 1.0;
            }

            void frag(v2f i,
            out float4 color : SV_Target0,
            out float  depth : SV_Depth)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                color = 0;
                depth = 0;

                if (_SdfNodeCount <= 0) {
                    discard;
                }

                float3 ro = i.ro;
                float3 rd = normalize(i.hitPos - ro);

                float3 faceNormal;
                float d = RayMarchDDA(ro, rd, faceNormal);

                if (d > _MaxDist) {
                    color.a = 0;
                    _CurrSdfDistTex[uint2(i.vertex.xy)] = d;
                    _CurrSdfColorTex[uint2(i.vertex.xy)] = float4(0, 0, 0, 0);
                    return;
                }

                float3 p = ro + d * rd;
                float4 clipSpacePos = TransformWorldToHClip(p);
                half3 normalWS = half3(faceNormal);

                SdfMaterial mat = GetMaterialAtScene(p);
                color = SdfLighting(p, normalWS, clipSpacePos, mat, half4(_Tint));
                color = saturate(color);

                depth = clipSpacePos.z / clipSpacePos.w;
                _CurrSdfDistTex[uint2(i.vertex.xy)] = d;
                _CurrSdfColorTex[uint2(i.vertex.xy)] = color;
            }
            ENDHLSL
        }
    }
    CustomEditor "RayMarchMinecraftMaterialEditor"
}
