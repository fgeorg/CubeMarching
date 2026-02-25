# CubeMarching Refactor Plan

## Context

The last git commit (`964df00`) is on Unity 2020.3.5f1. Since then, all changes
(Unity 6 upgrade + wireframe experiments) are uncommitted and messy. This plan
describes how to cleanly reset, re-upgrade, and rebuild everything properly.

---

## Phase 1 — Revert to Clean Baseline

```bash
git checkout -- .
```

This discards all uncommitted changes and takes the project back to Unity 2020.3.5f1.
It also reverts `MeshGenerator.cs` and the wireframe shader to their pre-experiment state.

---

## Phase 2 — Upgrade to Unity 6

1. Open the project folder in Unity Hub
2. Select Unity **6000.3.9f1** (or latest 6.x LTS)
3. Unity will prompt to upgrade — accept
4. Let Unity auto-upgrade packages (URP, etc.)
5. In Project Settings → Graphics, assign the URP asset if prompted
6. Do **not** commit yet — verify the scene loads without errors first

Key package versions to expect after upgrade:
- `com.unity.render-pipelines.universal` → 17.x
- Unity 6 drops OpenGL Core on macOS; **Metal is the only graphics API**

---

## Phase 3 — Remove Old Wireframe Shader & Add Clean One

Delete or clear `Assets/Shaders/Flat Wireframe Shader.shader` and rebuild from
scratch using everything learned in our previous session (see **Lessons Learned**
below).

---

## Phase 4 — Rethink Dedupe & Mesh Architecture

### The Problem With Post-Process Dedupe

Currently `DedupeVerts()` runs after all triangles are generated. It's a
position-based dictionary scan over all vertices — O(n). This is fine, but it
couples the mesh used for **smooth shading** with the mesh used for **wireframe
rendering**, because:

- Wireframe rendering via `SV_VertexID % 3` requires a **sequential index buffer**
  (i.e., one unique vertex per triangle corner, indices = 0,1,2,3,4,5,…)
- Deduped meshes have **shared vertices** with a non-sequential index buffer —
  `SV_VertexID % 3` gives wrong barycentric slots

Our previous fix was: dedupe → compute smooth normals on shared topology →
re-expand vertices back to sequential. This works but is wasteful (we dedupe
then immediately re-expand).

### Recommended Architecture: Two Mesh Generators

Split `MeshGenerator` into two subclasses:

```
MeshGenerator (base — abstract geometry generation, no dedup, sequential indices)
  ├── WireframeMeshGenerator   : plain sequential mesh, wireframe + flat/smooth shader
  └── SmoothMeshGenerator      : deduped during generation, smooth normals
```

`CloneMesh.cs` can be replaced by simply having two GameObjects each with their
own `MeshGenerator` subclass pointing at the same `CombinedDistanceField`.

### Better: Dedupe During Generation (for Marching Cubes)

Post-process deduplication is wasteful. The natural structure of marching cubes
means every triangle vertex lies on a **cube edge**, and each edge is shared by
at most 2 adjacent cubes. We can deduplicate at generation time using an edge
dictionary:

```
EdgeKey = (globalCornerIndexA, globalCornerIndexB)  // sorted, so A < B
globalCornerIndex = x*(res+1)*(res+1) + y*(res+1) + z
```

For each of the 12 edges of a marching cube, before adding the vertex look up
its `EdgeKey`. If it already exists, reuse the vertex index. This produces a
properly shared-vertex mesh in one pass with no re-scan.

For the **voxel** algorithm, face vertices are already shared at corners — a
similar dictionary keyed on position (or on the two neighboring voxel indices)
works the same way.

### Plan for `SmoothMeshGenerator`

1. Use edge-dictionary dedup **during** `AddCube` / `AddVoxel`
2. After generation, run `RecalculateNormals()` (or SDF-gradient normals) on
   the shared-vertex topology — correct smooth normals, no re-expansion needed
3. No `SV_VertexID % 3` — this mesh is for shading only, not wireframe

### Plan for `WireframeMeshGenerator`

1. No dedup at all — index buffer is always sequential
2. `SV_VertexID % 3` in the shader computes barycentrics with zero CPU work
3. Can use `RecalculateNormals()` for flat normals (one normal per triangle face,
   since no vertices are shared), or pass `_getNormalsFromSDF` for per-vertex SDF normals
4. Note: flat normals are actually desirable here — a wireframe mesh looks better
   with faceted shading

---

## Phase 5 — New Wireframe Shader

### What We Learned (Do Not Forget)

**Metal has no geometry shader stage.** Unity 6 dropped OpenGL Core on macOS.
Any shader with `#pragma geometry` is silently invisible on Metal — it won't
appear in the material dropdown. The fix is to compute barycentrics without a
geometry stage.

**`SV_VertexID % 3` is the cleanest approach.** On Metal, `SV_VertexID` gives
the vertex buffer index after index-buffer fetch. For a sequential index buffer
(indices = 0,1,2,3,4,5,…), `SV_VertexID % 3` correctly identifies each vertex's
position within its triangle (0→first, 1→second, 2→third). No UV channel needed.

**`fwidth` clamp must be ≤ 0.05.** The center of a triangle has a minimum
barycentric value of ~0.33. If `fwidth(barys)` is clamped at 0.25 (as is common
in online examples), the smoothstep threshold approaches 0.33, causing
small screen-space triangles to flood entirely with wireframe color. Clamping at
`0.05` keeps the threshold well below 0.33 at all screen sizes.

**Do not pass a zero shadow coord to `GetMainLight(shadowCoord)`.** Without
proper shadow keywords, the shadow coord is zero and `shadowAttenuation = 0`,
making all geometry black. Use the zero-argument overload `GetMainLight()`.

**Ambient floor prevents fully black backlit faces.** `SampleSH(normalWS)` can
return near-zero for certain normals in dark scenes. Add a floor:
```hlsl
float3 ambient = max(SampleSH(normalWS), float3(0.1, 0.1, 0.1));
```

### New Shader Template

```hlsl
Shader "Custom/Flat Wireframe" {

    Properties {
        _Color             ("Tint",               Color)          = (1, 1, 1, 1)
        _MainTex           ("Albedo",             2D)             = "white" {}
        _Cutoff            ("Alpha Cutoff",       Range(0, 1))    = 0.5
        _WireframeColor    ("Wireframe Color",    Color)          = (0, 0, 0, 1)
        _WireframeSmoothing("Wireframe Smoothing",Range(0, 10))   = 1
        _WireframeThickness("Wireframe Thickness",Range(0, 10))   = 1
        [Enum(UnityEngine.Rendering.CullMode)]      _Cull   ("Cull",   Float) = 2
        [Enum(Off,0,On,1)]                          _ZWrite ("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
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
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
                float4 _WireframeColor;
                float  _WireframeSmoothing;
                float  _WireframeThickness;
                float  _Cutoff;
            CBUFFER_END

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            struct Attributes {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                uint   vertexID   : SV_VertexID;   // position within triangle via % 3
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings {
                float4 positionHCS : SV_POSITION;
                float3 normalWS    : TEXCOORD2;
                float2 uv          : TEXCOORD0;
                float2 barycentric : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexNormalInputs normInputs = GetVertexNormalInputs(IN.normalOS);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS    = normInputs.normalWS;
                OUT.uv          = TRANSFORM_TEX(IN.uv, _MainTex);

                // Barycentrics derived from position within triangle.
                // Works because our index buffer is always sequential (one unique
                // vertex per corner), so SV_VertexID % 3 == corner index.
                uint pos = IN.vertexID % 3;
                OUT.barycentric = pos == 0 ? float2(1, 0) :
                                  pos == 1 ? float2(0, 1) : float2(0, 0);
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                UNITY_SETUP_INSTANCE_ID(IN);

                float4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * _Color;
                clip(albedo.a - _Cutoff);

                // Lambert diffuse + SH ambient
                float3 normalWS  = normalize(IN.normalWS);
                Light  mainLight = GetMainLight();           // no shadow coord — safe
                float  NdotL     = saturate(dot(normalWS, mainLight.direction));
                float3 diffuse   = mainLight.color * NdotL;
                float3 ambient   = max(SampleSH(normalWS), float3(0.1, 0.1, 0.1));
                float3 litColor  = (diffuse + ambient) * albedo.rgb;

                // Wireframe overlay — cap fwidth so no triangle floods its face
                // with wireframe colour regardless of screen size.
                float3 barys     = float3(IN.barycentric, 1.0 - IN.barycentric.x - IN.barycentric.y);
                float3 deltas    = min(fwidth(barys), 0.05);
                float3 smoothing = deltas * _WireframeSmoothing;
                float3 thickness = deltas * _WireframeThickness;
                barys    = smoothstep(thickness, thickness + smoothing, barys);
                float minBary = min(barys.x, min(barys.y, barys.z));

                float3 finalColor = lerp(_WireframeColor.rgb, litColor, minBary);
                return float4(finalColor, albedo.a);
            }
            ENDHLSL
        }
    }
}
```

**Requirement:** The mesh rendered with this shader **must** have a sequential
index buffer (indices = 0,1,2,3,4,5,…). `WireframeMeshGenerator` guarantees this
by never deduping. `SmoothMeshGenerator` should NOT use this shader.

---

## Summary Checklist

- [ ] `git checkout -- .` — revert all uncommitted changes
- [ ] Open in Unity 6000.3.9f1 — accept upgrade, let packages update
- [ ] Verify base scene loads and marching-cubes mesh renders
- [ ] Delete old wireframe shader (or clear and replace)
- [ ] Add new wireframe shader from Phase 5 template
- [ ] Refactor `MeshGenerator`:
  - [ ] Extract `WireframeMeshGenerator` (no dedupe, sequential indices)
  - [ ] Extract `SmoothMeshGenerator` (edge-dict dedupe during generation, smooth normals)
  - [ ] Remove `_dedupe` flag from base class (no longer needed)
  - [ ] Remove `DedupeVerts()`, `ComputeNormalsFromTopology()`, `ReexpandVerts()` from base class
- [ ] Update scene:
  - [ ] Assign `WireframeMeshGenerator` + wireframe material to primary object
  - [ ] Assign `SmoothMeshGenerator` + opaque material to secondary object (or same object)
  - [ ] Remove `CloneMesh.cs` usage if replaced by two generators
- [ ] Commit clean state
