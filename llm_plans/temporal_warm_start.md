# Temporal Warm-Start for SDF Ray Marching

## Context
Each frame, `RayMarch` starts at `dO = 0` (camera near plane) and sphere-traces to the SDF surface. Most steps cross empty space. Since the scene is mostly static and camera movement is continuous, the previous frame's hit distance is an excellent predictor for the current frame. By starting the march near the previous hit we skip empty-space traversal, saving most step budget.

The SDF itself is the validity oracle: evaluate `GetDistanceToScene` at the hinted world position — if the result is a small positive value the hint is good; if negative (inside geometry) or very large (camera moved too far) we fall back to a full march from zero.

## What We Store
After transparent rendering, copy the hardware depth buffer (which includes our SDF `SV_Depth` output) to a persistent `RFloat` RTHandle. Ping-pong between two such handles each frame. Pass the previous frame's inverse VP matrix and camera position as global uniforms so the main shader can decode the stored NDC depth back to a world position and project it onto the current ray.

## New Files
- `Assets/Shaders/CopySdfDepth.shader` — URP Blitter-based shader, reads `_BlitTexture` (depth), writes `float4(depth, 0, 0, 0)` to RFloat color target
- `Assets/Scripts/RayMarching/TemporalWarmStartFeature.cs` — `ScriptableRenderFeature` with two passes

## Modified Files
- `Assets/Shaders/RayMarchScene.shader`

## TemporalWarmStartFeature.cs

### Fields
```
RTHandle m_PrevHandle, m_CurrHandle   // ping-pong RFloat screen-sized textures
Matrix4x4 m_PrevInvVP                 // previous frame's (GPU proj * worldToCamera).inverse
Vector3   m_PrevCameraPos             // previous frame's camera position
bool      m_Initialized               // false on first frame — skip swap
Material  m_CopyDepthMat              // instantiated from CopySdfDepth shader
```

### AddRenderPasses (called once per frame, C# side)
1. `ReAllocateHandleIfNeeded` both handles (RFloat, screen-sized, no depth bits)
2. If `m_Initialized`: swap `m_PrevHandle ↔ m_CurrHandle`; set `m_Initialized = true`
3. Pass `m_PrevHandle`, `m_PrevInvVP`, `m_PrevCameraPos` to `BindPass`
4. Pass `m_CurrHandle` to `CapturePass`
5. Update `m_PrevInvVP` and `m_PrevCameraPos` from the current camera for next frame
6. Enqueue both passes

### BindTemporalDataPass (RenderPassEvent.BeforeRenderingTransparents)
`RecordRenderGraph`: `AddRasterRenderPass`, `AllowGlobalStateModification(true)`, in SetRenderFunc:
- `cmd.SetGlobalTexture("_PrevSdfDepthTex", prevDepthHandle)`
- `cmd.SetGlobalMatrix("_PrevInvVP", prevInvVP)`
- `cmd.SetGlobalVector("_PrevCameraPos", prevCameraPos)`

### CaptureDepthPass (RenderPassEvent.AfterRenderingTransparents)
`RecordRenderGraph`: `AddBlitPass` (or `AddRasterRenderPass`) from `resourceData.cameraDepth` → imported `m_CurrHandle` using `m_CopyDepthMat`.
Uses `RenderGraphUtils.BlitMaterialParameters`.

## RayMarchScene.shader Changes

### v2f struct — add:
```hlsl
float2 screenUV : TEXCOORD2;
```

### vert — add:
```hlsl
float4 screenPos = ComputeScreenPos(o.vertex);
o.screenUV = screenPos.xy / screenPos.w;
```

### Declarations (outside CBUFFER, globals):
```hlsl
#pragma shader_feature_local _TEMPORAL_WARMSTART_ON
TEXTURE2D(_PrevSdfDepthTex);
float4x4 _PrevInvVP;    // declared globally (set by render feature)
float4   _PrevCameraPos;
```

### RayMarch function — at the top, before the loop:
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

Note: `RayMarch` needs `screenUV` passed in as a parameter when `_TEMPORAL_WARMSTART_ON`.

## Manual Setup Step
After adding the C# file, the user must add `TemporalWarmStartFeature` to `Assets/Settings/ForwardRenderer.asset` via the Inspector, and assign the `CopySdfDepth` shader to the feature's slot. Then enable the `_TEMPORAL_WARMSTART_ON` keyword on the SDF material.

## Verification
- With feature active and keyword enabled: step count should drop significantly for static camera
- SceneViewPerformanceOverlay (existing) shows GPU timing improvement
- Moving the camera: first frame with large movement may revert to full march for some pixels; subsequent frames regain the warmstart
- SDF scene change (move a node): validation step catches stale hints; no visible artifacts, just a brief step-count spike
