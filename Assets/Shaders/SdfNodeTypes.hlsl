// SDF node type IDs.
// C# source of truth: SdfNodeComponent.SdfNodeType (enum int values must match).
// GPU-only smooth variants are not in the C# enum; they are emitted by SdfScene.MakeOp
// when smoothK > 0, and are also declared as private const int in SdfScene.cs.
// Keep all three files in sync when adding new types.

// ── Primitives (0–9) ─────────────────────────────────────────────────────────
#define SDF_SPHERE  0
#define SDF_BOX     1
#define SDF_TORUS   2

// ── Binary operators — sharp (10–19) ─────────────────────────────────────────
#define SDF_UNION     10
#define SDF_INTERSECT 12
#define SDF_SUBTRACT  13

// ── Binary operators — smooth, GPU-only ──────────────────────────────────────
#define SDF_SMOOTH_UNION     11
#define SDF_SMOOTH_INTERSECT 14
#define SDF_SMOOTH_SUBTRACT  15

// ── Unary modifiers (20+) ─────────────────────────────────────────────────────
#define SDF_SHELL  20
#define SDF_EXPAND 21
