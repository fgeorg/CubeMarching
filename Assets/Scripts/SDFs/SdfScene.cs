using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

[ExecuteInEditMode]
[RequireComponent(typeof(SdfNodeComponent))]
public class SdfScene : MonoBehaviour
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuSdfNode
    {
        public Vector4 typeAndParams; // x=type, y=param0, z=param1, w=param2
        public Matrix4x4 transform; // worldToLocalMatrix for primitives; identity for ops
    }

    private SdfNodeComponent _rootNode;
    private readonly List<GpuSdfNode> _nodes = new List<GpuSdfNode>();
    private readonly List<Transform> _primitiveTransforms = new List<Transform>();
    private bool _hierarchyDirty;
    private TransformTracker[] _trackers = Array.Empty<TransformTracker>();
    // Fired after the node list is fully updated. Subscribers (e.g.
    // SdfRayMarchRenderer) build their own GPU buffers from Nodes.
    public event Action Rebuilt;
    public List<GpuSdfNode> Nodes => _nodes;

    private void OnEnable()
    {
        _rootNode = GetComponent<SdfNodeComponent>();
        _hierarchyDirty = true;
        RebuildPostfix();
    }

    private void OnDisable()
    {
        _trackers = Array.Empty<TransformTracker>();
    }

    private void OnTransformChildrenChanged()
    {
        _hierarchyDirty = true;
    }

    public void MarkDirty()
    {
        _hierarchyDirty = true;
    }

    private void Update()
    {
        bool dirty = _hierarchyDirty;
        if (!dirty)
        {
            for (int i = 0; i < _trackers.Length; i++)
                if (_trackers[i].HasChanged()) { dirty = true; break; }
        }
        if (dirty) {
            RebuildPostfix();
        }
    }

    private void RebuildPostfix()
    {
        _hierarchyDirty = false;
        _nodes.Clear();
        _primitiveTransforms.Clear();

        AppendPostfix(_rootNode);

        // Rebuild transform trackers from all primitive nodes found during traversal
        _trackers = new TransformTracker[_primitiveTransforms.Count];
        for (int i = 0; i < _primitiveTransforms.Count; i++)
            _trackers[i] = new TransformTracker(_primitiveTransforms[i]);

        Rebuilt?.Invoke();
    }

    private void AppendPostfix(SdfNodeComponent node)
    {
        if ((int)node.nodeType < 10)
        {
            _nodes.Add(MakePrimitive(node));
            _primitiveTransforms.Add(node.transform);
        }
        else if ((int)node.nodeType >= 20) // unary modifier
        {
            // Union all children together first, then apply the modifier.
            bool first = true;
            foreach (Transform child in node.transform)
            {
                if (!child.gameObject.activeInHierarchy) continue;
                SdfNodeComponent childNode = child.GetComponent<SdfNodeComponent>();
                if (childNode == null) continue;
                AppendPostfix(childNode);
                if (!first) _nodes.Add(MakeOp(SdfNodeComponent.SdfNodeType.Union, 0f));
                first = false;
            }
            if (!first) // had at least one child
                _nodes.Add(MakeUnary(node));
        }
        else
        {
            // interleave: [C1, C2, op, C3, op, ...] keeps stack depth at 2
            bool first = true;
            foreach (Transform child in node.transform)
            {
                if (!child.gameObject.activeInHierarchy) continue;
                SdfNodeComponent childNode = child.GetComponent<SdfNodeComponent>();
                if (childNode == null) continue;
                AppendPostfix(childNode);
                if (!first) _nodes.Add(MakeOp(node.nodeType, node.smoothK));
                first = false;
            }
        }
    }

    private static GpuSdfNode MakePrimitive(SdfNodeComponent node)
    {
        Vector4 p;
        switch (node.nodeType)
        {
            case SdfNodeComponent.SdfNodeType.Sphere:
                p = new Vector4(0, node.sphereRadius, 0, 0);
                break;
            case SdfNodeComponent.SdfNodeType.Box:
                p = new Vector4(1, node.boxHalfExtents.x, node.boxHalfExtents.y, node.boxHalfExtents.z);
                break;
            default: // Torus
                p = new Vector4(2, node.torusMajorRadius, node.torusMinorRadius, 0);
                break;
        }
        return new GpuSdfNode { typeAndParams = p, transform = node.transform.worldToLocalMatrix };
    }

    private static GpuSdfNode MakeUnary(SdfNodeComponent node)
    {
        float param = node.nodeType == SdfNodeComponent.SdfNodeType.Shell
            ? node.shellThickness
            : node.expandAmount;
        return new GpuSdfNode
        {
            typeAndParams = new Vector4((int)node.nodeType, param, 0, 0),
            transform = Matrix4x4.identity
        };
    }

    // GPU-only smooth-variant type IDs (not in the C# enum).
    // Must match #define SDF_SMOOTH_* in Assets/Shaders/SdfNodeTypes.hlsl.
    private const int GpuSmoothUnion = 11;
    private const int GpuSmoothIntersect = 14;
    private const int GpuSmoothSubtract = 15;

    private static GpuSdfNode MakeOp(SdfNodeComponent.SdfNodeType type, float smoothK)
    {
        // Automatically pick smooth vs sharp variant based on k.
        bool smooth = smoothK > 0f;
        int gpuType;
        switch (type)
        {
            case SdfNodeComponent.SdfNodeType.Union: gpuType = smooth ? GpuSmoothUnion : (int)type; break;
            case SdfNodeComponent.SdfNodeType.Intersect: gpuType = smooth ? GpuSmoothIntersect : (int)type; break;
            case SdfNodeComponent.SdfNodeType.Subtract: gpuType = smooth ? GpuSmoothSubtract : (int)type; break;
            default: gpuType = (int)type; break;
        }
        return new GpuSdfNode
        {
            typeAndParams = new Vector4(gpuType, smoothK, 0, 0),
            transform = Matrix4x4.identity
        };
    }

    // CPU stack evaluator — mirrors EvalScene() in RayMarchScene.shader exactly.
    public float GetDistance(Vector3 p)
    {
        float[] stack = new float[16];
        int sp = 0;

        for (int i = 0; i < _nodes.Count; i++)
        {
            GpuSdfNode node = _nodes[i];
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
                    case 11: r = CpuSmoothUnion(a, b, k); break;               // SmoothUnion
                    case 12: r = Mathf.Max(a, b); break;                        // Intersect
                    case 13: r = Mathf.Max(a, -b); break;                       // Subtract
                    case 14: r = -CpuSmoothUnion(-a, -b, k); break;            // SmoothIntersect
                    default: r = -CpuSmoothUnion(-a, b, k); break;             // SmoothSubtract (15)
                }
                stack[sp++] = r;
            }
        }

        return sp > 0 ? stack[0] : 1e10f;
    }

    // Quadratic smooth-min — matches SmoothUnion() in RayMarchScene.shader.
    private static float CpuSmoothUnion(float a, float b, float k)
    {
        float h = Mathf.Max(k - Mathf.Abs(a - b), 0f);
        return Mathf.Min(a, b) - h * h * 0.25f / k;
    }
}
