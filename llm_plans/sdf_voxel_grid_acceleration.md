# Plan: SDF Voxel Acceleration Grid

## Context
Grazing rays between objects are hitting the `_MaxSteps` cap because the SDF value in narrow corridors is genuinely small — sphere tracing takes many tiny steps there regardless of evaluation cost. The fix is a coarse 3D grid that caches quantized distances so the marching loop can skip ahead by multiple cell lengths per step, removing the stuck-in-corridor problem. Each cell stores a 1-byte lower bound: "how many cell lengths to the nearest surface" (0–255). When the decoded value drops to ≤1 cell length, the loop falls back to the real SDF evaluator for accurate surface placement. A debug keyword renders purely from the voxel structure (no SDF calls at all) to verify the grid is populated and traversed correctly.

---

## Files to Create / Modify

### New: `Assets/Shaders/VoxelBake.compute`
Compute shader that fills the 3D grid. One thread per voxel.

```hlsl
#pragma kernel BakeVoxels

// Must be declared before SdfSceneDistanceGpu.hlsl include
int _SdfNodeCount;
#include "SdfSceneDistanceGpu.hlsl"   // brings in _SdfNodes, _SdfPrimitives, GetDistanceToScene()
// NOTE: SdfSceneDistanceGpu.hlsl already declares:
//   StructuredBuffer<SdfNode> _SdfNodes;
//   StructuredBuffer<SdfPrimitive> _SdfPrimitives;

RWTexture3D<half> _VoxelOut;  // matches RHalf texture format explicitly
float3 _VoxelOrigin;
float  _VoxelCellSize;
int    _VoxelResolution;

[numthreads(4, 4, 4)]
void BakeVoxels(uint3 id : SV_DispatchThreadID)
{
    if (any(id >= (uint)_VoxelResolution)) return;
    float3 cellCenter = _VoxelOrigin + (id + 0.5) * _VoxelCellSize;
    float dist = GetDistanceToScene(cellCenter);
    // Store raw world-space distance. Clamp negatives to 0 (interior voxels).
    _VoxelOut[id] = (half)max(dist, 0.0);
}
```

Dispatch from C# using ceiling division so non-multiples of 4 are handled safely:
```csharp
int groups = Mathf.CeilToInt(_voxelResolution / 4f);
_voxelBakeShader.Dispatch(kernel, groups, groups, groups);
```
Resolution should still be a multiple of 4 as a convention, but the guard `any(id >= _VoxelResolution)` handles any remainder threads safely.

---

### Modified: `Assets/Scripts/RayMarching/SdfRayMarchRenderer.cs`

Add a voxel baking section. Keep all existing logic intact.

**New serialized fields:**
```csharp
[Header("Voxel Acceleration")]
[SerializeField] ComputeShader _voxelBakeShader;       // assign VoxelBake.compute in Inspector
[SerializeField] bool          _enableVoxelAccel = false;
[SerializeField] int           _voxelResolution   = 64;  // cells per axis; must be multiple of 4
[SerializeField] Vector3       _voxelOrigin       = new Vector3(-5f, -5f, -5f);
[SerializeField] float         _voxelCellSize     = 10f / 64f; // extent / resolution
```

**New private fields:**
```csharp
RenderTexture _voxelTex; // 3D, R8, enableRandomWrite = true
```

**New method `RebakeVoxels()`, called from `OnRebuilt()` when `_enableVoxelAccel`:**
```csharp
void RebakeVoxels()
{
    // (Re)create 3D render texture if resolution changed
    if (_voxelTex == null || _voxelTex.width != _voxelResolution)
    {
        if (_voxelTex != null) _voxelTex.Release();
        _voxelTex = new RenderTexture(_voxelResolution, _voxelResolution, 0, RenderTextureFormat.RHalf);
        _voxelTex.dimension        = UnityEngine.Rendering.TextureDimension.Tex3D;
        _voxelTex.volumeDepth      = _voxelResolution;
        _voxelTex.enableRandomWrite = true;
        _voxelTex.filterMode       = FilterMode.Bilinear;
        _voxelTex.wrapMode         = TextureWrapMode.Clamp;
        _voxelTex.Create();
    }

    int kernel = _voxelBakeShader.FindKernel("BakeVoxels");
    _voxelBakeShader.SetBuffer(kernel, "_SdfNodes",      _buffer);
    _voxelBakeShader.SetBuffer(kernel, "_SdfPrimitives", _primitivesBuffer);
    _voxelBakeShader.SetInt   ("_SdfNodeCount",  _scene.NodeCount);  // expose NodeCount from SdfScene
    _voxelBakeShader.SetTexture(kernel, "_VoxelOut", _voxelTex);
    _voxelBakeShader.SetVector("_VoxelOrigin",   _voxelOrigin);
    _voxelBakeShader.SetFloat ("_VoxelCellSize", _voxelCellSize);
    _voxelBakeShader.SetInt   ("_VoxelResolution", _voxelResolution);
    int groups = Mathf.CeilToInt(_voxelResolution / 4f);
    _voxelBakeShader.Dispatch(kernel, groups, groups, groups);

    _propertyBlock.SetTexture("_VoxelTex",        _voxelTex);
    _propertyBlock.SetVector ("_VoxelOrigin",     _voxelOrigin);
    _propertyBlock.SetFloat  ("_VoxelCellSize",   _voxelCellSize);
    _propertyBlock.SetFloat  ("_VoxelResolution", _voxelResolution);
    _renderer.SetPropertyBlock(_propertyBlock);
}
```

**`SdfScene` needs a `NodeCount` property exposed** (currently baked list is private — add `public int NodeCount => _nodes.Count;` to `SdfScene.cs`).

**Release in `OnDisable()`:**
```csharp
if (_voxelTex != null) { _voxelTex.Release(); _voxelTex = null; }
```

---

### Modified: `Assets/Shaders/RayMarchScene.shader`

**New properties:**
```hlsl
[Toggle(_VOXEL_ACCEL_ENABLED)] _VoxelAccelEnabled ("Voxel Accel", Float) = 0
[Toggle(_VOXEL_DEBUG)]         _VoxelDebug        ("Voxel Debug", Float) = 0
[HideInInspector] _VoxelTex       ("Voxel Tex",       3D)    = "" {}
[HideInInspector] _VoxelOrigin    ("Voxel Origin",    Vector) = (0,0,0,0)
[HideInInspector] _VoxelCellSize  ("Voxel Cell Size", Float)  = 0.1
[HideInInspector] _VoxelResolution("Voxel Resolution",Float)  = 64
```

**New pragmas:**
```hlsl
#pragma shader_feature_local _VOXEL_ACCEL_ENABLED
#pragma shader_feature_local _VOXEL_DEBUG
```

**New CBUFFER additions (inside existing `CBUFFER_START(UnityPerMaterial)`):**
```hlsl
float3 _VoxelOrigin;
float  _VoxelCellSize;
float  _VoxelResolution;
```

**New sampler/texture declarations (outside CBUFFER):**
```hlsl
TEXTURE3D(_VoxelTex);
// Uses Unity built-in explicit samplers — no SAMPLER(sampler_VoxelTex) needed.
// sampler_point_clamp and sampler_linear_clamp are always available in URP.
```

**New properties (filter toggle):**
```hlsl
[KeywordEnum(Point, Trilinear)] _VoxelFilter ("Voxel Filter", Float) = 0
```

**New pragma:**
```hlsl
#pragma shader_feature_local _VOXELFILTER_POINT _VOXELFILTER_TRILINEAR
```

**New helper (add before `RayMarch`):**
```hlsl
// Returns world-space distance to surface from voxel grid at p, or -1 if outside grid.
// RHalf texture stores raw world-space distance — no decoding needed.
float SampleVoxelDist(float3 p)
{
    float3 uvw = (p - _VoxelOrigin) / (_VoxelCellSize * _VoxelResolution);
    if (any(uvw < 0) || any(uvw > 1)) return -1.0;
#if defined(_VOXELFILTER_TRILINEAR)
    return SAMPLE_TEXTURE3D_LOD(_VoxelTex, sampler_linear_clamp, uvw, 0).r;
#else // _VOXELFILTER_POINT (default)
    return SAMPLE_TEXTURE3D_LOD(_VoxelTex, sampler_point_clamp, uvw, 0).r;
#endif
}

// Half-diagonal of a unit cube: sqrt(3)/2 ≈ 0.866.
// The SDF was sampled at the voxel center; the ray can be up to this far from center,
// so we must subtract this many cell-lengths before using the value as a skip distance.
#define VOXEL_HALF_DIAGONAL 0.866
```

**Modified `RayMarch()`:**
```hlsl
float RayMarch(float3 ro, float3 rd) {
    float dO = 0;
    [loop]
    for (int i = 0; i < _MaxSteps; i++) {
        float3 p = ro + dO * rd;

#if defined(_VOXEL_ACCEL_ENABLED) || defined(_VOXEL_DEBUG)
        float voxDist = SampleVoxelDist(p);
        if (voxDist >= 0) { // inside grid
            // Safe skip: subtract half-diagonal to account for ray being
            // up to sqrt(3)/2 cell lengths from the voxel center.
            // Minimum step of 0.1 cells prevents infinite loops.
            float safeSkip = max(voxDist - VOXEL_HALF_DIAGONAL * _VoxelCellSize,
                                 0.1 * _VoxelCellSize);
#if defined(_VOXEL_DEBUG)
            if (voxDist < _VoxelCellSize) break; // within 1 cell — call it a hit
            dO += safeSkip;
            if (dO > _MaxDist) break;
            continue;
#else // _VOXEL_ACCEL_ENABLED
            if (voxDist > _VoxelCellSize) {
                dO += safeSkip;
                if (dO > _MaxDist) break; // don't burn steps in empty grid space
                continue;
            }
            // within 1 cell of surface — fall through to full eval
#endif
        }
#endif

        float dS = GetDistanceToScene(p);
        if (dS < _SurfDist || dO > _MaxDist) break;
        dO += dS * _StepFactor;
    }
    return dO;
}
```

Conservative skip uses `sqrt(3)/2 ≈ 0.866` cell-lengths subtracted, not 0.5: the SDF is sampled at the voxel center but the ray position can be at the voxel corner, which is `sqrt(3)/2` cell-lengths away. Both voxel branches also check `dO > _MaxDist` before `continue` to prevent rays in empty grid space burning the full step budget.

**Modified `GetNormal()` — voxel gradient path for debug mode:**
```hlsl
float3 GetNormal(float3 p) {
#if defined(_VOXEL_DEBUG)
    float uvStep = 1.0 / _VoxelResolution;
    float3 uvw = (p - _VoxelOrigin) / (_VoxelCellSize * _VoxelResolution);
    float3 n = float3(
        SAMPLE_TEXTURE3D_LOD(_VoxelTex, sampler_VoxelTex, uvw + float3(uvStep, 0, 0), 0).r
      - SAMPLE_TEXTURE3D_LOD(_VoxelTex, sampler_VoxelTex, uvw - float3(uvStep, 0, 0), 0).r,
        SAMPLE_TEXTURE3D_LOD(_VoxelTex, sampler_VoxelTex, uvw + float3(0, uvStep, 0), 0).r
      - SAMPLE_TEXTURE3D_LOD(_VoxelTex, sampler_VoxelTex, uvw - float3(0, uvStep, 0), 0).r,
        SAMPLE_TEXTURE3D_LOD(_VoxelTex, sampler_VoxelTex, uvw + float3(0, 0, uvStep), 0).r
      - SAMPLE_TEXTURE3D_LOD(_VoxelTex, sampler_VoxelTex, uvw - float3(0, 0, uvStep), 0).r
    );
    return normalize(n);
#else
    float2 e = float2(_NormalDist, 0);
    float3 n = float3(
        GetDistanceToScene(p + e.xyy),
        GetDistanceToScene(p + e.yxy),
        GetDistanceToScene(p + e.yyx)
    ) - GetDistanceToScene(p);
    return normalize(n);
#endif
}
```

**In `frag()`, skip `GetMaterialAtScene` in debug mode:**
```hlsl
#if defined(_VOXEL_DEBUG)
    SdfMaterial mat = (SdfMaterial)0;
    mat.color = float4(0.7, 0.7, 0.7, 1.0); // flat grey
    mat.smoothness = 0.3;
#else
    SdfMaterial mat = GetMaterialAtScene(p);
#endif
```

---

## Key Constraints / Notes

- **`_SdfNodeCount` in compute shader**: `SdfSceneDistanceGpu.hlsl` requires it declared before the include. The compute shader declares `int _SdfNodeCount;` globally before `#include "SdfSceneDistanceGpu.hlsl"`. This works because HLSL resolves it by name regardless of cbuffer vs global scope.
- **`_VOXEL_DEBUG` overrides `_VOXEL_ACCEL_ENABLED`**: both can coexist as separate toggles; `_VOXEL_DEBUG` means "voxel only, no SDF eval".
- **Resolution must be multiple of 4** (thread group size). Default 64 → 512³ bytes = 262 KB. 128 → 2 MB.
- **Conservative skip**: uses `sqrt(3)/2 ≈ 0.866` cell-lengths subtracted (the half-diagonal of a unit cube), not 0.5. Subtracting only 0.5 would be unsafe — a ray at a voxel corner is 0.866 cell-lengths from center, so a step of `cellDist - 0.5` can overstep and clip through diagonal surfaces.
- **`RHalf` format**: stores raw world-space distance, no encode/decode. `RWTexture3D<half>` is the explicit correct declaration for an RHalf UAV. Avoids the UNorm UAV typing issues that `R8` would require (`RWTexture3D<unorm float>`).
- **Alternative worth trying — R8 + cell-length encoding**: since the grid is already quantized and sub-cell precision is meaningless, `R8` (256 levels, 1 byte/voxel) is arguably the right precision for this use case. The maximum storable distance of 255 cell-lengths comfortably covers any scene that fits inside the grid. Would require `RWTexture3D<unorm float>` in HLSL and `clamp(dist / _VoxelCellSize, 0, 255) / 255.0` encode / `encoded * 255.0 * _VoxelCellSize` decode. Halves memory footprint (256KB vs 512KB at 64³) and may improve cache performance. Try this if profiling shows texture bandwidth is a bottleneck.
- **`_MaxDist` inside voxel branch**: both `continue` paths check `dO > _MaxDist` to prevent rays pointing at empty grid space burning the full `_MaxSteps` budget.
- **Bounds are manual** for now: set `_voxelOrigin` and `_voxelCellSize` in Inspector to cover scene geometry. Future: auto-compute from primitive bounds.
- **Rebake is synchronous** (GPU compute dispatch). For scenes that change every frame this could be a bottleneck; acceptable for now since baking is triggered only by the `Rebuilt` event.

---

## Verification

1. Enable `_enableVoxelAccel` on `SdfRayMarchRenderer`, assign the compute shader, set bounds to cover the scene.
2. Enable `_VOXEL_DEBUG` keyword in the material: geometry should render blocky/voxelated (surface at ≤1 cell resolution). Flat grey shading confirms no SDF calls. Normals will show faceting at grid resolution.
3. Disable `_VOXEL_DEBUG`, enable `_VOXEL_ACCEL_ENABLED`: rendering should look identical to baseline (or very close). Profile with RenderDoc / Unity Profiler — GPU time on the ray march pass should drop significantly for scenes with multiple objects.
4. Verify no visual regression in narrow gaps between objects (the cases that previously hit `_MaxSteps`).
5. Move scene objects in editor → `SdfScene.Rebuilt` fires → `RebakeVoxels()` runs → voxel texture updates on next frame.
