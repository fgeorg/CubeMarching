# Plan: Dynamic SDF Scene Graph

## Editor Workflow

The hierarchy **is** the scene graph. You never think in RPN — the postfix conversion happens automatically.

**Setting up a scene:**
```
SdfScene          ← component on the raymarching quad (has the renderer ref)
  SmoothPair      ← SdfNodeComponent: type=SmoothUnion, k=0.5
    Sphere        ← SdfNodeComponent: type=Sphere, radius=0.6
    Torus         ← SdfNodeComponent: type=Torus, major=0.4, minor=0.15
  Box             ← SdfNodeComponent: type=Box, cornerRadius=0
```

Operators are configured by **parenting shapes under them** in the hierarchy. `SmoothPair` above smoothly blends whatever GameObjects are children of it. The fact that the GPU receives `[Sphere, Torus, SmoothUnion, Box, Union]` is invisible.

Nesting works arbitrarily deep:
```
SdfScene
  Intersection              ← SdfNodeComponent: type=Intersect
    SmoothBlob              ← SdfNodeComponent: type=SmoothUnion, k=0.3
      Sphere
      Sphere
    Box
```

**What you do in editor:**
1. Right-click hierarchy → Create Empty, name it, add `SdfNodeComponent`
2. Set type in the dropdown (Sphere / Box / Torus / Union / SmoothUnion / Intersect / Subtract)
3. If it's an operator, parent your shapes under it
4. Move/rotate/scale the GameObjects normally — transforms update live

Direct children of `SdfScene` that are not under any operator are implicitly unioned together.

> **Note on non-uniform scale:** Applying non-uniform scale to a primitive will distort the SDF (e.g. a scaled sphere becomes an ellipsoid in world space but the distance field still treats it as a sphere in local space). This can cause rendering artifacts. Acceptable for now; enforce uniform scale manually.

---

## Context

The current ray march scene is entirely hardcoded: `RayMarchScene.shader` has three fixed SDF primitives (sphere, torus, box) wired together with a single `SMinPoly` call, and `PassTransformsToShader.cs` passes three hardcoded transform matrices to match. There is no way to add/remove primitives or change how they combine without editing the shader.

The goal is to replace the hardcoded setup with a component-per-node system where:
- Unity GameObjects in the hierarchy provide the transforms for each primitive
- Inspector fields on each component describe the primitive type, its parameters, and (for op nodes) the combination operator
- The scene graph is serialized into a GPU buffer and evaluated in the shader via a postfix stack machine

Existing C# files (`CombinedDistanceField`, `PassTransformsToShader`, primitive SDF classes) are **left unchanged** for backward compatibility.

---

## Design Overview

### GPU buffer layout (80 bytes per node, no padding needed)
```
struct GpuSdfNode {
    float4 typeAndParams;  // x=type, y=param0, z=param1, w=param2
    float4x4 transform;    // worldToLocalMatrix for primitives; identity for ops
}
```

Primitive params:
- **Sphere**: `(Sphere=0, radius, 0, 0)` — box size via transform scale
- **Box**: `(Box=1, cornerRadius, 0, 0)` — unit 0.5 half-extents in local space, size from transform
- **Torus**: `(Torus=2, majorR, minorR, 0)`

Op params: `(opType, smoothK, 0, 0)` — transform = identity

### Node types (int values, stored as float in typeAndParams.x)
```
Sphere=0, Box=1, Torus=2
Union=10, SmoothUnion=11, Intersect=12, Subtract=13
```

### Postfix traversal
The `SdfScene` root treats all its direct `SdfNodeComponent` children as being implicitly Union-combined. For each op node, all active SdfNodeComponent grandchildren are combined by that op.

Example hierarchy → buffer:
```
SdfScene
  SmoothPair (SmoothUnion, k=0.5)
    Sphere (radius=0.6)
    Torus (major=0.4, minor=0.15)
  Box (cornerRadius=0)

→ [Sphere, Torus, SmoothUnion(k=0.5), Box, Union]

Stack trace:
  push Sphere → [s]
  push Torus  → [s, t]
  SmoothUnion → [smin(s,t)]
  push Box    → [smin(s,t), b]
  Union       → [min(smin(s,t), b)]  ✓
```

---

## Files to Create

### `Assets/Scripts/SDFs/SdfNodeComponent.cs`
- `[ExecuteInEditMode]`
- `public enum SdfNodeType` with values above
- Public fields: `nodeType`, `sphereRadius`, `boxCornerRadius`, `torusMajorRadius`, `torusMinorRadius`, `smoothK`
- Implements `IDistanceField.GetDistance(Vector3 p)` for CPU evaluation:
  - Primitives: `transform.InverseTransformPoint(p)` then SDF formula
  - Box: matches `CubeDistanceField` exactly — `half = 0.5*(1-2*cr)`, IQ rounded-box formula
  - Op nodes: iterate `foreach (Transform child in transform)`, collect children with `SdfNodeComponent`, fold-left with `CombineTwo(a, b)` helper
- `private float SMinCubic(a, b, k)` (copy from `CombinedDistanceField.cs`)

### `Assets/Scripts/SDFs/SdfScene.cs`
- `[ExecuteInEditMode]`, implements `IDistanceField`
- `[StructLayout(LayoutKind.Sequential)]` inner struct `GpuSdfNode { Vector4 typeAndParams; Matrix4x4 transform; }` — stride = 80
- `[SerializeField] MeshGenerator _generator` (optional, for `MarkDirty()` on transform changes)
- Uses `MaterialPropertyBlock` + `Renderer.SetPropertyBlock()` — **not** `sharedMaterial.SetBuffer` (avoids modifying asset)
- `GraphicsBuffer _buffer` — must call `_buffer.Release()` in `OnDisable()` to prevent GPU leak
- **Dirty detection:** `_hierarchyDirty = true` set in `OnEnable()` and `OnTransformChildrenChanged()`; transform changes detected via `TransformTracker[]` rebuilt in `RebuildBuffer()`
- `Update()`: check trackers → if dirty, call `RebuildBuffer()`
- `RebuildBuffer()`: traverse postfix → `_buffer.SetData()` → `_propertyBlock.SetBuffer("_SdfNodes", _buffer)` + `_propertyBlock.SetInteger("_SdfNodeCount", count)` → `_renderer.SetPropertyBlock(_propertyBlock)` → rebuild trackers → optional `_generator.MarkDirty()`
- `TraversePostOrder(SdfNodeComponent node, List<GpuSdfNode> list)`:
  - Primitive (`(int)nodeType < 10`): add primitive entry
  - Op: recurse into active children, then emit `(activeChildCount - 1)` op entries
- Root `SdfScene` implicitly unions all direct `SdfNodeComponent` children: after traversing all, emit `(directChildCount - 1)` Union ops
- `GetDistance(Vector3 p)` CPU evaluator: fixed `float[] stack = new float[16]`, `int sp = 0`, loop over `_nodeList`, mirror shader logic using `Matrix4x4.MultiplyPoint(p)` for transform, `SMinCubic` for smooth union

### `Assets/Scripts/Editor/SdfNodeComponentEditor.cs`
- `[CustomEditor(typeof(SdfNodeComponent))]`
- `serializedObject.Update()` / `ApplyModifiedProperties()`
- Always show `nodeType` (EnumPopup via `FindProperty("nodeType")`)
- **Important**: `enumValueIndex` ≠ enum int value for non-contiguous enums. Read current type as `(SdfNodeType)System.Enum.GetValues(typeof(SdfNodeType)).GetValue(typeProp.enumValueIndex)` to switch on
- Show only relevant fields per type:
  - Sphere → `sphereRadius`
  - Box → `boxCornerRadius`
  - Torus → `torusMajorRadius`, `torusMinorRadius`
  - SmoothUnion → `smoothK`
  - Union/Intersect/Subtract → HelpBox "No parameters"
  - Op nodes (any type ≥ 10) → additional HelpBox "Transform position/rotation/scale has no effect on operators"
- Use `serializedObject.FindProperty` + `EditorGUILayout.PropertyField` for automatic Undo/dirty marking

---

## Files to Leave Unchanged
- `CombinedDistanceField.cs`, `PassTransformsToShader.cs`
- `SphereDistanceField.cs`, `CubeDistanceField.cs`, `TorusDistanceField.cs`
- `MeshGenerator.cs`, `TransformTracker.cs`, `DistanceField.cs`

---

## Remaining Tasks

- [ ] Consolidate mesh gen: replace `CombinedDistanceField` reference in `MeshGenerator` with `SdfScene.GetDistance` so both the raymarcher and marching-cubes mesh share the same scene graph
- [ ] `RayMarchMaterialEditor.cs` — add `Draw("_Color")` line in the SDF section
