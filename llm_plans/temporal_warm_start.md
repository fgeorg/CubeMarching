# Temporal Warm-Start for SDF Ray Marching

## Context
Each frame, `RayMarch` starts at `dO = 0` (camera near plane) and sphere-traces to the SDF surface. Most steps cross empty space. Since the scene is mostly static and camera movement is continuous, the previous frame's hit distance is an excellent predictor for the current frame. By starting the march near the previous hit we skip empty-space traversal, saving most of the step budget.

The SDF itself is the validity oracle: evaluate `GetDistanceToScene` at the hinted world position — if the result is a small positive value the hint is good; if negative (inside geometry) or very large (camera moved too far) we fall back to a full march from zero.

## Approach: MRT (Multiple Render Targets)

The SDF fragment shader already computes the NDC Z of the hit point (it writes `SV_Depth`). We add a second render target output (`SV_Target1`) that writes this same NDC Z value to a persistent per-camera RTHandle. That RTHandle is the warm-start texture for the next frame — no blit, no hardware depth copy, no extra draw call, no extra shader.

```
Frame N:
  BindPass    → set _PrevSdfDepthTex = prevHandle, set _PrevInvVP, _PrevCameraPos
  SDF pass    → ray march + shade → SV_Target0 (color), SV_Target1 (NDC Z → currHandle), SV_Depth
  [swap currHandle ↔ prevHandle for frame N+1]

Frame N+1:
  BindPass    → set _PrevSdfDepthTex = prevHandle  (= frame N's currHandle)
  SDF pass    → warm-starts from _PrevSdfDepthTex, writes new currHandle
  ...
```

State is per-camera (keyed by `Camera.GetInstanceID()`), so game view and scene view maintain independent histories.

## Files Changed

### New
- `Assets/Scripts/RayMarching/TemporalWarmStartFeature.cs` — `ScriptableRendererFeature` with one bind pass; the SDF pass itself is the capture

### Modified
- `Assets/Shaders/RayMarchScene.shader` — add `SV_Target1` output + `_TEMPORAL_WARMSTART_ON` warm-start logic

### Deleted / No Longer Needed
- `Assets/Shaders/CopySdfDepth.shader` — was only needed for the now-abandoned hardware depth blit approach

---

## TemporalWarmStartFeature.cs

### Per-camera state (Dictionary<int, CameraState>)
```
RTHandle prevHandle    // previous frame's NDC Z capture — bound as _PrevSdfDepthTex
RTHandle currHandle    // this frame will write here via MRT SV_Target1
Matrix4x4 prevInvVP   // (GPU proj * worldToCamera).inverse from last frame
Vector3 prevCameraPos  // camera world position from last frame
bool initialized       // false on first frame — skip swap
```

### AddRenderPasses
1. Look up per-camera state (default to identity matrices + uninitialized on first seen camera)
2. `ReAllocateHandleIfNeeded` both handles (R32_SFloat, screen-sized, no depth bits)
3. If `initialized`: swap `prevHandle ↔ currHandle`; else set `initialized = true`
4. Pass `prevHandle`, `prevInvVP`, `prevCameraPos` to `BindPass.Setup`
5. Pass `currHandle` to the SDF pass setup (so the feature can attach it as SV_Target1)
6. Update `prevInvVP` and `prevCameraPos` from the current camera for next frame
7. Enqueue `BindPass`; the SDF MRT attachment is handled via `SetBeforeRendering` or a dedicated raster pass wrapping the SDF draw

### BindTemporalDataPass (RenderPassEvent.BeforeRenderingTransparents)
`RecordRenderGraph`: `AddRasterRenderPass`, `AllowGlobalStateModification(true)`, in `SetRenderFunc`:
- `cmd.SetGlobalTexture("_PrevSdfDepthTex", prevDepthHandle)`
- `cmd.SetGlobalMatrix("_PrevInvVP", prevInvVP)`
- `cmd.SetGlobalVector("_PrevCameraPos", prevCameraPos)`

Falls back to `renderGraph.defaultResources.blackTexture` on first frame (prevNdcZ == 0 → warm-start guard in shader skips the hint cleanly).

### MRT attachment for the SDF pass
The SDF material renders in URP's transparent forward pass. To attach `currHandle` as SV_Target1 we need a dedicated raster pass in the feature (RenderPassEvent.BeforeRenderingTransparents, after BindPass, or a custom event) that:
- `SetRenderAttachment(activeColorTexture, 0)` — camera color
- `SetRenderAttachment(currHandle, 1)` — depth capture
- `SetRenderAttachmentDepth(cameraDepthTexture)`
- Draws the SDF mesh with the SDF material

This replaces URP's automatic transparent pass handling of the SDF object; the GameObject's MeshRenderer should be disabled or the material excluded from the default pass.

---

## RayMarchScene.shader Changes

### Pragma / declarations — add:
```hlsl
#pragma shader_feature_local _TEMPORAL_WARMSTART_ON

TEXTURE2D(_PrevSdfDepthTex);   // globally set by TemporalWarmStartFeature
float4x4 _PrevInvVP;
float4   _PrevCameraPos;
```

### frag — add second output:
```hlsl
void frag(v2f i,
    out float4 color        : SV_Target0,
    out float  depthCapture : SV_Target1,
    out float  depth        : SV_Depth)
{
    // ... existing march + shade logic ...

    float ndcZ = clipSpacePos.z / clipSpacePos.w;
    depth        = ndcZ;
    depthCapture = ndcZ;   // captured for next frame's warm-start
}
```

On miss (discard path): write `depthCapture = 0` before discarding so the guard `prevNdcZ > 0` correctly skips empty pixels next frame.

### RayMarch — warm-start block (unchanged from current):
```hlsl
#if defined(_TEMPORAL_WARMSTART_ON)
    float prevNdcZ = SAMPLE_TEXTURE2D(_PrevSdfDepthTex, sampler_point_clamp, screenUV).r;
    if (prevNdcZ > 0.0 && prevNdcZ < 1.0) {
        float2 ndcXY = screenUV * 2.0 - 1.0;
        float4 worldPos4 = mul(_PrevInvVP, float4(ndcXY, prevNdcZ, 1.0));
        float3 worldPos = worldPos4.xyz / worldPos4.w;
        float t_hint = dot(worldPos - ro, rd);
        if (t_hint > 0.0) {
            float sdfVal = GetDistanceToScene(ro + t_hint * rd);
            if (sdfVal >= 0.0 && sdfVal < _MaxDist) {
                dO = max(0.0, t_hint - sdfVal);
            }
        }
    }
#endif
```

---

## Key Design Properties
- **No blit / no extra pass**: depth capture is free — one extra float written per fragment
- **Per-camera isolation**: game view and scene view each have their own RTHandle pair; no global bleed
- **First-frame safe**: black texture fallback → `prevNdcZ == 0` → warm-start skipped cleanly
- **Miss pixels safe**: explicit `depthCapture = 0` on discard → won't produce stale warm-start hints

## Manual Setup
- Add `TemporalWarmStartFeature` to the URP ForwardRenderer asset
- No shader field to assign (CopySdfDepth is gone)
- Enable `_TEMPORAL_WARMSTART_ON` on the SDF material
- Disable URP's automatic rendering of the SDF MeshRenderer (feature owns the draw call)

## Verification
- Static camera: step count drops significantly (warm-start skips empty space)
- Moving camera: first frame after large movement may revert to full march for some pixels; subsequent frames regain warm-start
- Scene view and game view: independent depth histories, no cross-contamination
- SDF scene change: SDF validity check catches stale hints; no visible artifacts, brief step-count spike only
