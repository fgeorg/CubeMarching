# Plan: Progressive Color Accumulation (RGB ring buffer)

## Context
The existing progressive refinement system reuses previous-frame ray march distances (`d`) to warm-start marching, improving convergence. Unconverged pixels are currently faded to transparent via dist fade (`col.a *= smoothstep(...)`). The goal is to instead fill those unconverged pixels with the previous frame's RGB — hiding undersampled regions with stable, coherent color from the prior frame rather than punching a transparent hole.

## New Keyword
`_PROGRESSIVE_COLOR_ON` — standalone `shader_feature`. Can coexist with `_PROGRESSIVE_REFINEMENT_ON` and `_MINDISTFADEMODE_ENABLED` independently.

Color buffers are **always allocated** by the feature (same ring-buffer machinery as dist). The keyword gates shader behaviour only — no C# branching needed.

---

## 1. ProgressiveRefinementFeature.cs

### CameraState — add a second handle array
```csharp
public RTHandle[] colorHandles = new RTHandle[BufferCount];
```
Shares `bufferIndex` with `handles` — they advance in lockstep.

### AddRenderPasses — realloc color handles
After the dist realloc loop, build a `colorDesc` from the same base descriptor:
```csharp
RenderTextureDescriptor colorDesc = desc; // desc already has msaa=1, depthBits=0
colorDesc.graphicsFormat    = GraphicsFormat.R16G16B16A16_SFloat;
colorDesc.enableRandomWrite = true;

for (int i = 0; i < BufferCount; i++)
{
    RTHandle h = state.colorHandles[i];
    RenderingUtils.ReAllocateHandleIfNeeded(ref h, colorDesc,
        FilterMode.Point, TextureWrapMode.Clamp, name: $"_color_buffer_{i}");
    state.colorHandles[i] = h;
}
```

### SdfMrtPass.Setup — pass color handles
```csharp
public void Setup(RTHandle currDist, RTHandle prevDist,
                  RTHandle currColor, RTHandle prevColor, bool invalidate)
```

### PassData — add color fields
```csharp
public RenderTexture  currColorRT;
public TextureHandle  prevColor;
```

### RecordRenderGraph — bind color handles
- `currColorHandle = renderGraph.ImportTexture(m_CurrColorHandle)`
- `prevColor` = `blackTexture` when invalidated, else `ImportTexture(m_PrevColorHandle)`
- `builder.UseTexture(currColorHandle, AccessFlags.Write)`
- `builder.UseTexture(prevColor, AccessFlags.Read)`

### Render func additions
```csharp
ctx.cmd.SetGlobalTexture(s_PrevSdfColorTexId, data.prevColor);
ctx.cmd.SetRandomWriteTarget(2, data.currColorRT);
// dist is slot 1, color is slot 2
```

### Dispose — release color handles
```csharp
foreach (RTHandle h in state.colorHandles) h?.Release();
```

---

## 2. RayMarchScene.shader

### Declarations (alongside existing dist declarations)
```hlsl
#pragma shader_feature _PROGRESSIVE_COLOR_ON

TEXTURE2D(_PrevSdfColorTex);
RWTexture2D<float4> _CurrSdfColorTex : register(u2);
```

### minDist tracking
`minDist` is currently only tracked under `_MINDISTFADEMODE_ENABLED`. The march loop must also track it when `_PROGRESSIVE_COLOR_ON` is active:
```hlsl
#if defined(_MINDISTFADEMODE_ENABLED) || defined(_PROGRESSIVE_COLOR_ON)
    minDist = min(minDist, dS);
#endif
```

### Fragment shader — after `col` is fully computed, before `color = saturate(col)`
```hlsl
#if defined(_PROGRESSIVE_COLOR_ON)
    float4 prevColor = SAMPLE_TEXTURE2D(_PrevSdfColorTex, sampler_point_clamp, screenUV);
    float blend = smoothstep(_DistFadeMax, _DistFadeMin, minDist); // 1=converged, 0=not
    _CurrSdfColorTex[uint2(i.vertex.xy)] = saturate(col);          // write pre-blend color
    col.rgb = lerp(prevColor.rgb, col.rgb, blend);                  // fill gaps with prev frame
#endif
```
`_DistFadeMin`/`_DistFadeMax` are always in the CBUFFER regardless of `_MINDISTFADEMODE_ENABLED`, so they can be reused freely. The `_MINDISTFADEMODE_ENABLED` alpha fade is unaffected — the two keywords touch different channels and coexist cleanly.

### Early-exit paths (miss, backface discard)
At every path that returns early (ray miss, backface discard), write `float4(0,0,0,0)` to the color UAV, mirroring how `d` is written at every dist exit path. This avoids stale color data accumulating in uncovered pixels.

---

## 3. RayMarchMaterialEditor.cs

Add a "Progressive Color" section after the existing Progressive Refinement section, following the same toggle pattern used there:
```csharp
GUILayout.Label("Progressive Color", EditorStyles.boldLabel);
bool colorOn = mat.IsKeywordEnabled("_PROGRESSIVE_COLOR_ON");
EditorGUI.BeginChangeCheck();
colorOn = EditorGUILayout.Toggle("Enabled", colorOn);
if (EditorGUI.EndChangeCheck())
{
    if (colorOn) mat.EnableKeyword("_PROGRESSIVE_COLOR_ON");
    else         mat.DisableKeyword("_PROGRESSIVE_COLOR_ON");
}
```

---

## Critical files
- `Assets/Scripts/RayMarching/ProgressiveRefinementFeature.cs`
- `Assets/Shaders/RayMarchScene.shader`
- `Assets/Scripts/Editor/RayMarchMaterialEditor.cs`

## Verification
1. Enable `_PROGRESSIVE_REFINEMENT_ON` + `_PROGRESSIVE_COLOR_ON` on the material
2. Hold camera still — blended areas should stabilize as convergence improves
3. Move camera — invalidation clears color buffer to black, accumulation restarts cleanly
4. Enable `_MINDISTFADEMODE_ENABLED` simultaneously — alpha fade and RGB blend coexist
5. Frame debugger: `_color_buffer_0/1` should be R16G16B16A16 and alternate each frame
6. Test `BufferCount = 3` — ring buffer should still produce correct curr/prev indices
