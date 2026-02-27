// SDF node type IDs.
// HLSL mirror: Assets/Shaders/SdfNodeTypes.hlsl (#define SDF_* values must match).
// Keep both files in sync when adding new types.

// Primitives directly correspond to an SDF equation
public enum SdfNodeType {
    Sphere = 0,
    Box = 1,
    Torus = 2,

    // Binary operators act on two values
    Union = 10,
    SmoothUnion = 11,
    Intersect = 12,
    Subtract = 13,
    SmoothIntersect = 14,
    SmoothSubtract = 15,

    // Unary operators act on a single value
    Shell = 20, // abs(d) - thickness
    Expand = 21, // d - amount (negative = contract)
}

public static class SdfNodeTypeRanges {
    public const int PrimitivesStart = 0;
    public const int PrimitivesEnd = 10;
    public const int BinaryOpsStart = 10;
    public const int BinaryOpsEnd = 20;
    public const int UnaryOpsStart = 20;
    public const int UnaryOpsEnd = 30;
}
