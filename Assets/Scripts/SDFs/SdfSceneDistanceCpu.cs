using System.Collections.Generic;
using UnityEngine;

// CPU stack evaluator — mirrors EvalScene() in SdfSceneDistanceGpu.hlsl.
public static class SdfSceneDistanceCpu {
    public static float GetDistance(List<SdfScene.BakedSdfNode> nodes, Vector3 p) {
        float[] stack = new float[16];
        int sp = 0;

        for (int i = 0; i < nodes.Count; i++) {
            SdfScene.BakedSdfNode node = nodes[i];
            SdfNodeType t = (SdfNodeType)node.typeAndParams.x;
            float k = node.typeAndParams.y;

            float d = 1e10f;

            if ((int)t < SdfNodeTypeRanges.PrimitivesEnd) {
                // primitive → push

                Vector3 lp = node.transform.MultiplyPoint3x4(p);
                switch (t) {
                    case SdfNodeType.Sphere:
                        d = lp.magnitude - k;
                        break;
                    case SdfNodeType.Box:
                        Vector3 bh = new Vector3(node.typeAndParams.y, node.typeAndParams.z, node.typeAndParams.w);
                        Vector3 q = new Vector3(Mathf.Abs(lp.x) - bh.x, Mathf.Abs(lp.y) - bh.y, Mathf.Abs(lp.z) - bh.z);
                        d = Vector3.Max(q, Vector3.zero).magnitude
                            + Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f);
                        break;
                    case SdfNodeType.Torus:
                        float ring = new Vector2(lp.x, lp.y).magnitude - node.typeAndParams.y;
                        d = new Vector2(ring, lp.z).magnitude - node.typeAndParams.z;
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
                        d = -SmoothUnion(-a, -b, k);
                        break;
                    case SdfNodeType.SmoothSubtract:
                        d = -SmoothUnion(-a, b, k);
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

    // Quadratic smooth-min — matches SmoothUnion() in SdfSceneDistanceGpu.hlsl.
    private static float SmoothUnion(float a, float b, float k) {
        float h = Mathf.Max(k - Mathf.Abs(a - b), 0f);
        return Mathf.Min(a, b) - h * h * 0.25f / k;
    }
}
