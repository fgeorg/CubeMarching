using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

[ExecuteInEditMode]
[RequireComponent(typeof(SdfNodeComponent))]
public class SdfScene : MonoBehaviour
{
    [StructLayout(LayoutKind.Sequential)]
    public struct BakedSdfNode
    {
        public Vector4 typeAndParams; // x=type, y=param0, z=param1, w=param2
        public Matrix4x4 transform; // worldToLocalMatrix for primitives; identity for ops
    }

    private SdfNodeComponent _rootNode;
    private readonly List<BakedSdfNode> _nodes = new List<BakedSdfNode>();
    private readonly List<Transform> _primitiveTransforms = new List<Transform>();
    private bool _hierarchyDirty;
    private TransformTracker[] _trackers = Array.Empty<TransformTracker>();
    // Fired after the node list is fully updated. Subscribers (e.g.
    // SdfRayMarchRenderer) build their own GPU buffers from Nodes.
    public event Action Rebuilt;
    public List<BakedSdfNode> Nodes => _nodes;

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
            // Union any children with this primitive: [prim, C1, Union, C2, Union, ...]
            foreach (Transform child in node.transform)
            {
                if (!child.gameObject.activeInHierarchy) continue;
                SdfNodeComponent childNode = child.GetComponent<SdfNodeComponent>();
                if (childNode == null) continue;
                AppendPostfix(childNode);
                _nodes.Add(MakeOp(SdfNodeComponent.SdfNodeType.Union, 0f));
            }
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
            // interleave: [C1, C2, op, C3, op, ...]
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

    private static BakedSdfNode MakePrimitive(SdfNodeComponent node)
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
        return new BakedSdfNode { typeAndParams = p, transform = node.transform.worldToLocalMatrix };
    }

    private static BakedSdfNode MakeUnary(SdfNodeComponent node)
    {
        float param = node.nodeType == SdfNodeComponent.SdfNodeType.Shell
            ? node.shellThickness
            : node.expandAmount;
        return new BakedSdfNode
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

    private static BakedSdfNode MakeOp(SdfNodeComponent.SdfNodeType type, float smoothK)
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
        return new BakedSdfNode
        {
            typeAndParams = new Vector4(gpuType, smoothK, 0, 0),
            transform = Matrix4x4.identity
        };
    }

    public float GetDistance(Vector3 p) => SdfSceneDistanceCpu.GetDistance(_nodes, p);
}
