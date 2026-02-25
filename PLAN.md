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


Plan: Dedupe Rethink for MeshGenerator
Context
The current MeshGenerator has a _dedupe bool that triggers a post-process
vertex merge pass. This was originally added to get smooth normals (shared-vertex
topology required for averaging adjacent face normals). However, the wireframe
shader needs a sequential index buffer (SV_VertexID % 3 to compute
barycentrics), which is incompatible with a deduped mesh. The previous fix was
a wasteful three-step round-trip: dedupe → compute smooth normals → re-expand
back to sequential. This plan eliminates that complexity with a cleaner two-path
architecture.

Fundamental insight: Wireframe and smooth-shaded meshes have mutually
incompatible requirements. The right solution is to produce them as separate
meshes rather than contorting one mesh to serve both.

Architecture: One Class, Two Mesh Paths
Keep the MeshGenerator class (avoids re-wiring CombinedDistanceField's
serialized _generator reference and MarkDirty() call). Replace _dedupe
with an _shadingMode enum and two serialized MeshFilter references.


_wireframeMeshFilter  →  sequential index buffer, flat/SDF normals
_smoothMeshFilter     →  shared-vertex index buffer, smooth normals
Both filters can be assigned simultaneously. When both are set, Regenerate()
populates both meshes in one call.

Files to Change
Assets/Scripts/MeshGenerator.cs — primary changes
Assets/Scripts/MarchTables.cs — add one new table
MarchTables.cs: New Table
Add a mapping from edge index → two corner offsets. This is the only data
needed for during-generation edge dedup.


// For each of the 12 edges: { cornerA_dx, cornerA_dy, cornerA_dz,
//                              cornerB_dx, cornerB_dy, cornerB_dz }
// Verified against edgePoints midpoints (each entry averages to edgePoints[i])
public static readonly int[,] edgeCornerOffsets = {
    { 0,0,1,  1,0,1 },  // edge 0
    { 1,0,1,  1,0,0 },  // edge 1
    { 1,0,0,  0,0,0 },  // edge 2
    { 0,0,0,  0,0,1 },  // edge 3
    { 0,1,1,  1,1,1 },  // edge 4
    { 1,1,1,  1,1,0 },  // edge 5
    { 1,1,0,  0,1,0 },  // edge 6
    { 0,1,0,  0,1,1 },  // edge 7
    { 0,0,1,  0,1,1 },  // edge 8
    { 1,0,1,  1,1,1 },  // edge 9
    { 1,0,0,  1,1,0 },  // edge 10
    { 0,0,0,  0,1,0 },  // edge 11
};
MeshGenerator.cs Changes
Fields: remove / replace / add
Remove	Replace with
bool _dedupe	EShadingMode _shadingMode (enum: Wireframe, Smooth)
List<Vector3> _normals	local variable where needed
—	MeshFilter _wireframeMeshFilter (serialized)
—	MeshFilter _smoothMeshFilter (serialized)
—	Dictionary<long, int> _edgeVertexCache (class-level, reused each regen)
Mesh _mesh	Mesh _wireframeMesh, Mesh _smoothMesh
Methods: remove
Method	Reason
DedupeVerts()	Replaced by during-generation edge cache (MC) and post-process position dict (voxels, renamed)
ReexpandVerts()	Only existed to restore sequential index buffer after dedup — no longer needed
ComputeNormalsFromTopology()	Unity's RecalculateNormals() on a shared-vertex mesh is equivalent and built-in
New Regenerate() flow

protected void Regenerate() {
    _shouldRegenerate = false;
    float cubeSize = 1.0f / _resolution;
    if (_wireframeMeshFilter != null) RegenerateWireframe(cubeSize);
    if (_smoothMeshFilter    != null) RegenerateSmooth(cubeSize);
}
RegenerateWireframe(float cubeSize)
Init _wireframeMesh on _wireframeMeshFilter if needed
Clear mesh, _vertices, _triangles
Run AddCube / AddVoxel loop (existing code, unchanged) → sequential index buffer
ProjectVerticesToSurface()
SetVertices, SetTriangles
RecalculateNormals() OR SDF normals if _getNormalsFromSDF
Add: _wireframeMesh.indexFormat = IndexFormat.UInt32 on init (latent bug fix)
RegenerateSmooth(float cubeSize)
CubeMarch path — dedupe during generation:

Init _smoothMesh on _smoothMeshFilter if needed
Clear mesh, _vertices, _triangles, _edgeVertexCache
Run loop calling AddCubeWithEdgeDedup() instead of AddCube() → shared-vertex index buffer
ProjectVerticesToSurface()
SetVertices, SetTriangles
RecalculateNormals() (Unity averages across shared vertices → smooth) OR SDF normals
Voxel path — post-process dedupe is fine (vertex positions are exact grid multiples, float equality is safe):

Same as wireframe generation
After generation, run renamed PostProcessDedupeVerts() (existing DedupeVerts() logic)
RecalculateNormals() or SDF normals
New AddCubeWithEdgeDedup() method
Same corner-sampling and triangulation logic as AddCube. Difference: instead
of appending a new vertex unconditionally, compute an edge key and look it up
first.


// Corner global index for a grid of (res+1)^3 corners
int stride  = _resolution + 1;
int strideX = stride * stride;

foreach (var edgeIndex in MarchTables.triangulation[~bits & 255]) {
    int dxA = MarchTables.edgeCornerOffsets[edgeIndex, 0], ...;  // 6 offsets
    int cornerA = (xi+dxA)*strideX + (yi+dyA)*stride + (zi+dzA);
    int cornerB = (xi+dxB)*strideX + (yi+dyB)*stride + (zi+dzB);
    long key = EdgeKey(cornerA, cornerB);

    if (_edgeVertexCache.TryGetValue(key, out int idx)) {
        _triangles.Add(idx);
    } else {
        _edgeVertexCache[key] = _vertices.Count;
        _triangles.Add(_vertices.Count);
        var ep = MarchTables.edgePoints[edgeIndex];
        _vertices.Add(new Vector3(origin.x + ep.x*cubeDim.x, ...));
    }
}

static long EdgeKey(int a, int b) {
    int lo = a < b ? a : b, hi = a < b ? b : a;
    return ((long)lo << 32) | (uint)hi;
}
Why this is correct: All vertices are at fixed edge midpoints (no
iso-surface interpolation). The midpoint of edge e for cube (xi,yi,zi) is
always the same world-space position regardless of which adjacent cube produces
it, so the first cube wins and adjacent cubes reuse its vertex index.

Note on _cubeMarchStepsToShow: Apply this debug-truncation only in
RegenerateWireframe, not in RegenerateSmooth (a partial smooth mesh with
orphaned cache entries is confusing).

Scene Setup
Remove CloneMesh.cs from the scene. Instead:

Create two child GameObjects on the generator: WireframeMesh and SmoothMesh
Each gets a MeshFilter + MeshRenderer with its own material
Serialize both MeshFilter refs into MeshGenerator._wireframeMeshFilter and _smoothMeshFilter
CloneMesh.cs file can remain — just unused in the scene.
CombinedDistanceField.cs needs no changes (MarkDirty() interface unchanged).

Known Risk: IndexFormat
At _resolution ≥ ~23, vertex count exceeds Unity's default 16-bit index limit
(65,535). Add after mesh creation:


_mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
This is a latent bug in the existing code independent of this refactor.

Verification
Assign _wireframeMeshFilter only → wireframe mesh appears, SV_VertexID % 3 barycentrics correct
Assign _smoothMeshFilter only → smooth shaded mesh appears, no seam artifacts at shared edges
Assign both → both meshes regenerate together, no shared-state corruption
Toggle _algorithm between CubeMarch and Voxels in both modes
Enable _getNormalsFromSDF in both modes
Set _resolution = 30 → no silent black mesh (IndexFormat.UInt32 fix working)
Confirm _edgeVertexCache vertex count < full sequential count (dedup is happening)