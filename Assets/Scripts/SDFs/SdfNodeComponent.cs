using UnityEngine;

[ExecuteInEditMode]
public class SdfNodeComponent : MonoBehaviour
{
    // Integer values must match #define SDF_* in Assets/Shaders/SdfNodeTypes.hlsl.
    // GPU-only smooth variants (11, 14, 15) are NOT in this enum; they are emitted
    // by SdfScene.MakeOp when smoothK > 0 and declared as const int there.
    public enum SdfNodeType
    {
        // ── Primitives (0–9) ─────────────────────────────────────────────────
        Sphere = 0,
        Box = 1,
        Torus = 2,
        // ── Binary operators (10–19) ─────────────────────────────────────────
        Union = 10,
        Intersect = 12,
        Subtract = 13,
        // ── Unary modifiers (20+) ─────────────────────────────────────────────
        Shell = 20,  // abs(d) - thickness
        Expand = 21, // d + amount  (negative = contract)
    }

    public SdfNodeType nodeType = SdfNodeType.Sphere;
    public float sphereRadius = 0.5f;
    public Vector3 boxHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);
    public float torusMajorRadius = 0.4f;
    public float torusMinorRadius = 0.15f;
    [Range(0f, 1f)]
    public float smoothK = 0.3f;
    [Range(0f, 0.2f)]
    public float shellThickness = 0.05f;
    public float expandAmount = 0.1f;
}
