// SDF node type IDs.
// C# source of truth: SdfNodeComponent.SdfNodeType (enum int values must match).

// Primitives directly correspond to an SDF equation
#define SDF_PRIMITIVES_START  0

#define SDF_SPHERE  0
#define SDF_BOX     1
#define SDF_TORUS   2

#define SDF_PRIMITIVES_END   10


// Binary operators act on two values
#define SDF_BINARY_OPS_START 10

#define SDF_UNION            10
#define SDF_SMOOTH_UNION     11
#define SDF_INTERSECT        12
#define SDF_SUBTRACT         13
#define SDF_SMOOTH_INTERSECT 14
#define SDF_SMOOTH_SUBTRACT  15

#define SDF_BINARY_OPS_END   20

// Unary operators act on a single value
#define SDF_UNARY_OPS_START  20

#define SDF_SHELL            20
#define SDF_EXPAND           21

#define SDF_UNARY_OPS_END    30
