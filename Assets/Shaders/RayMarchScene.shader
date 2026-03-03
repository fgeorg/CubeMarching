Shader "RayMarchScene" {
    Properties {
        _MainTex ("Texture", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
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
        [KeywordEnum(Off, Accel, Debug)] _VoxelMode      ("Voxel Mode",      Float) = 0
        [KeywordEnum(Point, Trilinear, Snap, Minecraft)] _VoxelFilter ("Voxel Filter", Float) = 0
        [IntRange]                       _MinSdfSteps    ("Min Full Eval Steps", Range(1, 128)) = 32
        [HideInInspector] _VoxelTex       ("Voxel Tex",       3D)    = "" {}
        [HideInInspector] _VoxelOrigin    ("Voxel Origin",    Vector) = (0,0,0,0)
        [HideInInspector] _VoxelCellSize  ("Voxel Cell Size", Float)  = 0.1
        [HideInInspector] _VoxelResolution("Voxel Resolution",Float)  = 64
        // Off   = normal rendering
        // Ndc   = show previous frame's depth texture as grayscale
        // WriteTime = write _Time.y to RT1 each frame; show it in color.
        //             If the color slowly changes over time, RT1 write + ping-pong both work.
        //             If it's frozen, the pipeline is broken.
        [KeywordEnum(Off, Ndc, WriteTime)] _TemporalDebug ("Temporal Debug", Float) = 0
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
            #pragma shader_feature_local _BACKFACECULLMODE_DISABLED _BACKFACECULLMODE_ALPHA _BACKFACECULLMODE_DISCARD
            #pragma shader_feature_local _VOXELMODE_OFF _VOXELMODE_ACCEL _VOXELMODE_DEBUG
            #pragma shader_feature_local _VOXELFILTER_POINT _VOXELFILTER_TRILINEAR _VOXELFILTER_SNAP _VOXELFILTER_MINECRAFT
            #pragma shader_feature_local _TEMPORAL_WARMSTART_ON
            #pragma shader_feature_local _TEMPORALDEBUG_OFF _TEMPORALDEBUG_NDC _TEMPORALDEBUG_WRITETIME

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "SdfLighting.hlsl"

            struct appdata {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f {
                float4 vertex : SV_POSITION;
                float3 ro : TEXCOORD0;
                float3 hitPos : TEXCOORD1;
                float2 screenUV : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE3D(_VoxelTex);
            SAMPLER(sampler_point_clamp);
            SAMPLER(sampler_linear_clamp);

            // Temporal warm-start globals (set by TemporalWarmStartFeature each frame).
            TEXTURE2D(_PrevSdfDepthTex);
            float4x4 _PrevInvVP;
            float4   _PrevCameraPos;
            // UAV for depth capture — written via SetRandomWriteTarget(1, currHandle).
            // Avoids MRT slot routing issues on Metal by bypassing framebuffer attachment.
            RWTexture2D<float> _CurrSdfDepthTex : register(u1);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Tint;
                float _MaxDist;
                float _SurfDist;
                float _NormalDist;
                float _StepFactor;
                float _BackfaceCullMin;
                float _BackfaceCullMax;
                float _BackfaceCullThreshold;
                int _MaxSteps;
                int _MinSdfSteps;
                int _SdfNodeCount;
                float3 _VoxelOrigin;
                float  _VoxelCellSize;
                float  _VoxelResolution;
            CBUFFER_END

            #include "SdfSceneDistanceGpu.hlsl"

            v2f vert(appdata v) {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.ro = _WorldSpaceCameraPos;
                o.hitPos = TransformObjectToWorld(v.vertex.xyz);
                float4 screenPos = ComputeScreenPos(o.vertex);
                o.screenUV = screenPos.xy / screenPos.w;
                return o;
            }

            // Returns world-space distance from the voxel grid at p, or -1 if outside grid.
            // RHalf texture stores raw world-space distance — no decoding needed.
            float SampleVoxelDist(float3 p)
            {
                float3 uvw = (p - _VoxelOrigin) / (_VoxelCellSize * _VoxelResolution);
                if (any(uvw < 0) || any(uvw > 1)) return -1.0;
#if defined(_VOXELFILTER_TRILINEAR)
                return SAMPLE_TEXTURE3D_LOD(_VoxelTex, sampler_linear_clamp, uvw, 0).r;
#elif defined(_VOXELFILTER_SNAP)
                // Trilinear sample then floor to the nearest cell-size multiple.
                // Steps can only be whole-cell multiples, so the ray stops at the
                // outer face of the nearest voxel cell — blocky/Minecraft-style.
                float raw = SAMPLE_TEXTURE3D_LOD(_VoxelTex, sampler_linear_clamp, uvw, 0).r;
                return floor(raw / _VoxelCellSize) * _VoxelCellSize;
#else // _VOXELFILTER_POINT and _VOXELFILTER_MINECRAFT — nearest cell centre value
                return SAMPLE_TEXTURE3D_LOD(_VoxelTex, sampler_point_clamp, uvw, 0).r;
#endif
            }

            // Half-diagonal of a unit cube: sqrt(3)/2 ≈ 0.866.
            // The SDF was sampled at the voxel center; the ray can be up to this far from
            // center, so we subtract this many cell-lengths before using the value as a skip.
            #define VOXEL_HALF_DIAGONAL 0.866

            float3 GetNormal(float3 p) {
                float2 e = float2(_NormalDist, 0);
                float3 n = float3(
                    GetDistanceToScene(p + e.xyy),
                    GetDistanceToScene(p + e.yxy),
                    GetDistanceToScene(p + e.yyx)
                ) - GetDistanceToScene(p);
                return normalize(n);
            }

#if defined(_VOXELMODE_DEBUG) && defined(_VOXELFILTER_MINECRAFT)
            // Classic DDA grid traversal.  Steps one voxel cell at a time along the
            // ray, stopping as soon as it enters a solid cell (stored dist == 0).
            // Face normal is determined by which axis boundary was just crossed —
            // unambiguous because it is recorded at step time, not inferred post-hoc.
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
                    if (dist < _SurfDist)
                    {
                        // Entered a solid cell.  The face we crossed is the entry face;
                        // its outward normal opposes the ray direction on the winning axis.
                        faceNormal = -sign(rd) * mask;
                        return t;
                    }

                    // Sphere-tracing skip: if this cell is far from the surface,
                    // jump ahead along the full ray direction and rebuild all three
                    // DDA axes from the new position.  Advancing along only one axis
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
#endif

            float RayMarch(float3 ro, float3 rd, float2 screenUV) {
                float dO = 0;

#if defined(_TEMPORAL_WARMSTART_ON)
                // Sample the previous frame's NDC depth at this screen position.
                // Reconstruct the world hit position, project it onto the current ray,
                // then validate with the SDF. If valid, skip the empty-space portion.
                float prevNdcZ = SAMPLE_TEXTURE2D(_PrevSdfDepthTex, sampler_point_clamp, screenUV).r;
                if (prevNdcZ > 0.0 && prevNdcZ < 1.0)
                {
                    float2 ndcXY = screenUV * 2.0 - 1.0;
                    float4 worldPos4 = mul(_PrevInvVP, float4(ndcXY, prevNdcZ, 1.0));
                    float3 worldPos = worldPos4.xyz / worldPos4.w;
                    float t_hint = dot(worldPos - ro, rd);
                    if (t_hint > 0.0)
                    {
                        float sdfVal = GetDistanceToScene(ro + t_hint * rd);
                        if (sdfVal >= 0.0 && sdfVal < _MaxDist)
                        {
                            dO = max(0.0, t_hint - sdfVal);
                        }
                    }
                }
#endif

                [loop]
                for (int i = 0; i < _MaxSteps; i++) {
                    float3 p = ro + dO * rd;

#if defined(_VOXELMODE_ACCEL) || defined(_VOXELMODE_DEBUG)
                    float voxDist = SampleVoxelDist(p);
                    if (voxDist >= 0) {
#if defined(_VOXELMODE_DEBUG)
                        // Sphere-trace using the interpolated voxel value directly.
                        if (voxDist < _SurfDist) break;
                        dO += voxDist * _StepFactor;
                        if (dO > _MaxDist) break;
                        continue;
#else // _VOXELMODE_ACCEL
                        // Reserve _MinSdfSteps iterations exclusively for full SDF eval
                        // so grazing rays don't exhaust the budget on voxel skips.
                        if (i < _MaxSteps - _MinSdfSteps) {
                            float safeSkip = max(voxDist - VOXEL_HALF_DIAGONAL * _VoxelCellSize,
                                                 0.1 * _VoxelCellSize);
                            if (voxDist > _VoxelCellSize) {
                                dO += safeSkip;
                                if (dO > _MaxDist) break;
                                continue;
                            }
                        }
                        // within 1 cell of surface, OR reserved steps reached — fall through
#endif
                    }
#endif

                    float dS = GetDistanceToScene(p);
                    if (dS < _SurfDist || dO > _MaxDist) break;
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

                float3 ro = i.ro;
                float3 rd = normalize(i.hitPos - ro);

#if defined(_VOXELMODE_DEBUG) && defined(_VOXELFILTER_MINECRAFT)
                float3 faceNormal;
                float d = RayMarchDDA(ro, rd, faceNormal);
#else
                float d = RayMarch(ro, rd, i.screenUV);
#endif
                float4 col;

                if (d > _MaxDist) {
                    col.a = 0;
                    discard;
                }

                float3 p = ro + d * rd;
                float4 clipSpacePos = TransformWorldToHClip(p);

#if defined(_TEMPORALDEBUG_NDC)
                float prevNdcZ_dbg = SAMPLE_TEXTURE2D(_PrevSdfDepthTex, sampler_point_clamp, i.screenUV).r;
                color = float4(prevNdcZ_dbg, prevNdcZ_dbg, prevNdcZ_dbg, 1.0);
                depth = clipSpacePos.z / clipSpacePos.w;
                _CurrSdfDepthTex[uint2(i.vertex.xy)] = depth;
                return;
#endif

#if defined(_TEMPORALDEBUG_WRITETIME)
                // red = what _PrevSdfDepthTex stored last frame, green = writing this frame.
                // Both should advance together if RT1 write + ping-pong are working.
                float timeVal     = frac(_Time.y * 0.5);
                float prevTimeVal = SAMPLE_TEXTURE2D(_PrevSdfDepthTex, sampler_point_clamp, i.screenUV).r;
                color = float4(prevTimeVal, timeVal, 0.0, 1.0);
                depth = clipSpacePos.z / clipSpacePos.w;
                _CurrSdfDepthTex[uint2(i.vertex.xy)] = timeVal;
                return;
#endif

#if defined(_VOXELMODE_DEBUG) && defined(_VOXELFILTER_MINECRAFT)
                half3 normalWS = half3(faceNormal);
#else
                half3 normalWS = normalize(half3(GetNormal(p)));
#endif

                SdfMaterial mat = GetMaterialAtScene(p);
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = half3(_Tint.rgb * mat.color.rgb);
                surfaceData.metallic   = (half)mat.metallic;
                surfaceData.smoothness = (half)mat.smoothness;
                surfaceData.occlusion  = 1.0h;
                surfaceData.alpha      = 1.0h;
                surfaceData.normalTS   = half3(0, 0, 1);

                col.rgb = SdfLighting(p, normalWS, clipSpacePos, surfaceData);
                col.a = _Tint.a * mat.color.a;

                float ndotv = dot(normalWS, rd);
#if defined(_BACKFACECULLMODE_DISCARD)
                if (ndotv > _BackfaceCullThreshold) discard;
#elif defined(_BACKFACECULLMODE_ALPHA)
                col.a *= 1 - smoothstep(_BackfaceCullMin, _BackfaceCullMax, ndotv);
#endif

                color = saturate(col);
                depth = clipSpacePos.z / clipSpacePos.w;
                _CurrSdfDepthTex[uint2(i.vertex.xy)] = depth;
            }
            ENDHLSL
        }
    }
    CustomEditor "RayMarchMaterialEditor"
}
