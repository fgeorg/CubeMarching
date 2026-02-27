using System.Collections.Generic;
using UnityEngine;

// CPU stack evaluator — mirrors EvalScene() in SdfSceneDistanceGpu.hlsl.
public static class SdfSceneDistanceCpu
{
    public static float GetDistance(List<SdfScene.BakedSdfNode> nodes, Vector3 p)
    {
        float[] stack = new float[16];
        int sp = 0;

        for (int i = 0; i < nodes.Count; i++)
        {
            SdfScene.BakedSdfNode node = nodes[i];
            int t = (int)node.typeAndParams.x;
            float k = node.typeAndParams.y;

            if (t < 10) // primitive → push
            {
                Vector3 lp = node.transform.MultiplyPoint3x4(p);
                float d;
                if (t == 0) // Sphere
                {
                    d = lp.magnitude - k;
                }
                else if (t == 1) // Box
                {
                    Vector3 bh = new Vector3(node.typeAndParams.y, node.typeAndParams.z, node.typeAndParams.w);
                    Vector3 q = new Vector3(Mathf.Abs(lp.x) - bh.x, Mathf.Abs(lp.y) - bh.y, Mathf.Abs(lp.z) - bh.z);
                    d = Vector3.Max(q, Vector3.zero).magnitude
                        + Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f);
                }
                else // Torus (t == 2)
                {
                    float ring = new Vector2(lp.x, lp.y).magnitude - node.typeAndParams.y;
                    d = new Vector2(ring, lp.z).magnitude - node.typeAndParams.z;
                }
                if (sp < stack.Length) stack[sp++] = d;
            }
            else if (t >= 20) // unary modifier → modify top in place
            {
                if (sp >= 1)
                    stack[sp - 1] = t == (int)SdfNodeComponent.SdfNodeType.Shell
                        ? Mathf.Abs(stack[sp - 1]) - k   // Shell
                        : stack[sp - 1] - k;             // Expand
            }
            else if (sp >= 2) // binary op → pop two, push result
            {
                float b = stack[--sp];
                float a = stack[--sp];
                float r;
                switch (t)
                {
                    case 10: r = Mathf.Min(a, b); break;                        // Union
                    case 11: r = SmoothUnion(a, b, k); break;                   // SmoothUnion
                    case 12: r = Mathf.Max(a, b); break;                        // Intersect
                    case 13: r = Mathf.Max(a, -b); break;                       // Subtract
                    case 14: r = -SmoothUnion(-a, -b, k); break;                // SmoothIntersect
                    default: r = -SmoothUnion(-a, b, k); break;                 // SmoothSubtract (15)
                }
                stack[sp++] = r;
            }
        }

        return sp > 0 ? stack[0] : 1e10f;
    }

    // Quadratic smooth-min — matches SmoothUnion() in SdfSceneDistanceGpu.hlsl.
    private static float SmoothUnion(float a, float b, float k)
    {
        float h = Mathf.Max(k - Mathf.Abs(a - b), 0f);
        return Mathf.Min(a, b) - h * h * 0.25f / k;
    }
}
