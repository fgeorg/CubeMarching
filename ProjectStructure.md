# Project Structure

The project is organized around a shared **SDF scene graph** that feeds two independent rendering pipelines: a real-time GPU ray marcher and a CPU mesh generator.

## Scenes (`Assets/Scenes/`)

- **`RayMarching.unity`** — Main showcase. Renders the SDF in real-time via a ray-march shader on proxy geometry.
- **`MeshGeneration.unity`** — Tessellates the same SDF into a regular Unity mesh using marching cubes or voxel algorithms.
- **`CubeMarchDebug.unity`** — Interactive single-cube debugger for visualizing individual marching-cubes triangulation cases.

## Scripts (`Assets/Scripts/`)

### `SDFs/` — The SDF scene graph

- `SdfNodeComponent.cs` — MonoBehaviour placed on every node; carries node type and parameters (primitives: Sphere, Box, Torus; binary ops: Union, Intersect, Subtract; unary modifiers: Shell, Expand)
- `SdfScene.cs` — Root of the hierarchy; bakes the tree into a flat postfix node list each frame and fires a `Rebuilt` event for subscribers
- `SdfSceneDistanceCpu.cs` — C# mirror of the GPU distance evaluator; used by mesh generation

### `RayMarching/`

- `SdfRayMarchRenderer.cs` — Uploads the baked node list to a `GraphicsBuffer` and pushes it to the ray-march material each frame

### `MeshGeneration/`

- `MeshGenerator.cs` — Tessellates the SDF into a Unity mesh; supports marching cubes and Minecraft-style voxel algorithms, with optional surface reprojection and wireframe/deduped mesh variants
- `MarchTables.cs` — Classic 256-case marching-cubes lookup tables (from Paul Bourke)

### `Utils/`

- `TransformTracker.cs` — Lightweight struct for detecting transform movement without Unity's expensive `hasChanged` flag
- `DebugStore.cs` — Editor-only key-value store for publishing values to the Scene View performance overlay
- `SimpleCameraController.cs` — Standard fly camera (WASD + right-mouse-drag)
- `VectorMathExtensions.cs` — `Vector3` extension methods (`Abs`, `Multiply`)

### `Editor/`

- `SdfNodeComponentEditor.cs` — Custom Inspector that shows only relevant parameters per node type
- `SdfNodeMenus.cs` — `GameObject > SDFs > ...` menu for creating primitives and operations
- `RayMarchMaterialEditor.cs` — Custom shader GUI with log-scale sliders for tiny epsilon values
- `SceneViewPerformanceOverlay.cs` — FPS/CPU/GPU HUD rendered in the Scene View

## Shaders (`Assets/Shaders/`)

- `SdfNodeTypes.hlsl` — Canonical type-ID `#define` table shared between C# and HLSL
- `SdfSceneDistanceGpu.hlsl` — GPU postfix stack evaluator; mirrors `SdfSceneDistanceCpu.cs` exactly
- `SdfStack.hlsl` — Metal-compatible 8-slot stack (named fields + switch dispatch; no dynamic indexing)
- `RayMarchScene.shader` — Ray-march shader: sphere traces per-fragment, writes correct hardware depth so the SDF surface occludes regular scene geometry
- `Flat Wireframe Shader.shader` — Wireframe overlay using barycentric coordinates baked into UV1 by `MeshGenerator`

## Data Flow

```
Unity Hierarchy (SdfNodeComponent tree)
              |
              v
          SdfScene  (postfix bake each frame)
              |
     ---------+----------------------------------
     |                                          |
     v                                          v
SdfRayMarchRenderer                       MeshGenerator
(GraphicsBuffer -> material)              (CPU marching cubes / voxel)
     |                                          |
     v                                          v
RayMarchScene.shader                  Mesh -> MeshFilter
(per-fragment ray march)              (wireframe + deduped paths)
```

Both consumers subscribe to `SdfScene.Rebuilt` and update reactively whenever the hierarchy changes or a primitive moves. The CPU evaluator (`SdfSceneDistanceCpu`) and GPU evaluator (`SdfSceneDistanceGpu.hlsl`) implement identical postfix stack machines so mesh generation and ray marching always render the same shape.
