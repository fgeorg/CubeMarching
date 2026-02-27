using System.Collections.Generic;
using UnityEngine;

// CPU stack evaluator — mirrors EvalScene() in SdfSceneDistanceGpu.hlsl.
public static class SdfSceneDistanceCpu {
    public static float GetDistance(List<SdfScene.BakedSdfNode> nodes, List<SdfScene.BakedSdfPrimitive> primitives, Vector3 p) {
        float[] stack = new float[16];
        int sp = 0;

        for (int i = 0; i < nodes.Count; i++) {
            SdfScene.BakedSdfNode node = nodes[i];
            SdfNodeType t = (SdfNodeType)node.typeAndParams.x;
            float k = node.typeAndParams.y;

            float d = 1e10f;

            if ((int)t < SdfNodeTypeRanges.PrimitivesEnd) {
                // primitive → push
                Vector3 lp = primitives[node.primitiveIndex].transform.MultiplyPoint3x4(p);
                switch (t) {
                    case SdfNodeType.Sphere:
                        d = SdfSphere(lp, node.typeAndParams.y);
                        break;
                    case SdfNodeType.Box:
                        d = SdfBox(lp, new Vector3(node.typeAndParams.y, node.typeAndParams.z, node.typeAndParams.w));
                        break;
                    case SdfNodeType.Torus:
                        d = SdfTorus(lp, node.typeAndParams.y, node.typeAndParams.z);
                        break;
                }
                if (sp < stack.Length) {
                    stack[sp++] = d;
                }
            } else if ((int)t <= SdfNodeTypeRanges.UnaryOpsEnd && sp >= 2) {
                // binary op → pop two, push result
                float b = stack[--sp];
                float a = stack[--sp];
                switch (t) {
                    case SdfNodeType.Union:
                        d = Mathf.Min(a, b);
                        break;
                    case SdfNodeType.SmoothUnion:
                        d = SmoothUnion(a, b, k);
                        break;
                    case SdfNodeType.Intersect:
                        d = Mathf.Max(a, b);
                        break;
                    case SdfNodeType.Subtract:
                        d = Mathf.Max(a, -b);
                        break;
                    case SdfNodeType.SmoothIntersect:
                        d = SmoothIntersect(a, b, k);
                        break;
                    case SdfNodeType.SmoothSubtract:
                        d = SmoothSubtract(a, b, k);
                        break;
                }
                stack[sp++] = d;
            } else if ((int)t <= SdfNodeTypeRanges.UnaryOpsEnd && sp >= 1) {
                // unary modifier → modify top in place
                switch (t) {
                    case SdfNodeType.Shell:
                        stack[sp - 1] = Mathf.Abs(stack[sp - 1]) - k;
                        break;
                    case SdfNodeType.Expand:
                        stack[sp - 1] = stack[sp - 1] - k;
                        break;
                }
            }
        }

        return sp > 0 ? stack[0] : 1e10f;
    }

    // Primitive SDFs — mirrors SdfSceneDistanceGpu.hlsl.
    private static float SdfSphere(Vector3 p, float r) {
        return p.magnitude - r;
    }

    private static float SdfBox(Vector3 p, Vector3 halfExtents) {
        Vector3 q = new Vector3(Mathf.Abs(p.x) - halfExtents.x, Mathf.Abs(p.y) - halfExtents.y, Mathf.Abs(p.z) - halfExtents.z);
        return Vector3.Max(q, Vector3.zero).magnitude + Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f);
    }

    private static float SdfTorus(Vector3 p, float ringRadius, float tubeRadius) {
        float ring = new Vector2(p.x, p.y).magnitude - ringRadius;
        return new Vector2(ring, p.z).magnitude - tubeRadius;
    }

    // Smooth boolean ops — https://iquilezles.org/articles/smin/
    private static float SmoothUnion(float a, float b, float k) {
        float h = Mathf.Max(k - Mathf.Abs(a - b), 0f);
        return Mathf.Min(a, b) - h * h * 0.25f / k;
    }

    private static float SmoothSubtract(float a, float b, float k) {
        return -SmoothUnion(-a, b, k);
    }

    private static float SmoothIntersect(float a, float b, float k) {
        return -SmoothUnion(-a, -b, k);
    }
}
