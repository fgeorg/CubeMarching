using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

[ExecuteInEditMode]
public class SdfScene : MonoBehaviour
{
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuSdfNode
    {
        public Vector4 typeAndParams; // x=type, y=param0, z=param1, w=param2
        public Matrix4x4 transform; // worldToLocalMatrix for primitives; identity for ops
    }

    [Tooltip("the static \"window\" into our sdf with the RayMarchScene material on it")]
    [SerializeField] private GameObject _SdfWindow;
    private Renderer _renderer;
    private MaterialPropertyBlock _propertyBlock;
    private GraphicsBuffer _buffer;
    private readonly List<GpuSdfNode> _nodes = new List<GpuSdfNode>();
    private bool _hierarchyDirty;
    private TransformTracker[] _trackers = Array.Empty<TransformTracker>();
    // Fired after the GPU buffer and CPU node list are fully updated.
    public event Action Rebuilt;

    private void OnEnable()
    {
        _renderer = _SdfWindow.GetComponent<Renderer>();
        _propertyBlock = new MaterialPropertyBlock();
        _hierarchyDirty = true;
        RebuildBuffer();
    }

    private void OnDisable()
    {
        _buffer?.Release();
        _buffer = null;
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
            RebuildBuffer();
        }
    }

    private void RebuildBuffer()
    {
        if (_renderer == null) return;
     
        _hierarchyDirty = false;

        _nodes.Clear();
        var primitiveTransforms = new List<Transform>();
        bool first = true;

        foreach (Transform child in transform)
        {
            if (!child.gameObject.activeInHierarchy) continue;
            SdfNodeComponent node = child.GetComponent<SdfNodeComponent>();
            if (node == null) continue;
            TraversePostOrder(node, _nodes, primitiveTransforms);
            // interleave: emit a union after each child past the first → depth stays at 2
            if (!first) _nodes.Add(MakeOp(SdfNodeComponent.SdfNodeType.Union, 0f));
            first = false;
        }

        int count = _nodes.Count;

        // Metal requires the buffer to always be bound, even when count is 0.
        // Allocate a minimum 1-element buffer and use _SdfNodeCount to gate the loop.
        int bufferSize = Mathf.Max(count, 1);
        if (_buffer == null || _buffer.count != bufferSize)
        {
            _buffer?.Release();
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, 80);
        }

        if (count > 0)
            _buffer.SetData(_nodes);

        _propertyBlock.SetBuffer("_SdfNodes", _buffer);
        _propertyBlock.SetInteger("_SdfNodeCount", count);
        _renderer.SetPropertyBlock(_propertyBlock);

        // Rebuild transform trackers from all primitive nodes found during traversal
        _trackers = new TransformTracker[primitiveTransforms.Count];
        for (int i = 0; i < primitiveTransforms.Count; i++)
            _trackers[i] = new TransformTracker(primitiveTransforms[i]);

        Rebuilt?.Invoke();
    }

    private void TraversePostOrder(SdfNodeComponent node, List<GpuSdfNode> list, List<Transform> primitiveTransforms)
    {
        if ((int)node.nodeType < 10)
        {
            list.Add(MakePrimitive(node));
            primitiveTransforms.Add(node.transform);
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
                TraversePostOrder(childNode, list, primitiveTransforms);
                if (!first) list.Add(MakeOp(SdfNodeComponent.SdfNodeType.Union, 0f));
                first = false;
            }
            if (!first) // had at least one child
                list.Add(MakeUnary(node));
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
                TraversePostOrder(childNode, list, primitiveTransforms);
                if (!first) list.Add(MakeOp(node.nodeType, node.smoothK));
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
