using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

[ExecuteInEditMode]
[RequireComponent(typeof(SdfNodeComponent))]
public class SdfScene : MonoBehaviour {
    [StructLayout(LayoutKind.Sequential)]
    public struct BakedSdfNode {
        public Vector4 typeAndParams; // x=type, y=param0, z=param1, w=param2
        public int primitiveIndex;    // index into Primitives; -1 for ops
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BakedSdfPrimitive {
        // worldToLocal
        public Matrix4x4 transform;
        public Vector4 color;               // rgba
        public Vector4 material;            // x=metallic, y=smoothness
    }

    private SdfNodeComponent _rootNode;
    private readonly List<BakedSdfNode> _nodes = new List<BakedSdfNode>();
    private readonly List<BakedSdfPrimitive> _primitives = new List<BakedSdfPrimitive>();
    private readonly List<Transform> _primitiveTransforms = new List<Transform>();
    private bool _hierarchyDirty;
    private TransformTracker[] _trackers = Array.Empty<TransformTracker>();
    // Fired after the node list is fully updated. Subscribers (e.g.
    // SdfRayMarchRenderer) build their own GPU buffers from Nodes.
    public event Action Rebuilt;
    public List<BakedSdfNode> Nodes => _nodes;
    public List<BakedSdfPrimitive> Primitives => _primitives;

    private void OnEnable() {
        _rootNode = GetComponent<SdfNodeComponent>();
        _hierarchyDirty = true;
        RebuildPostfix();
    }

    private void OnDisable() {
        _trackers = Array.Empty<TransformTracker>();
    }

    private void OnTransformChildrenChanged() {
        _hierarchyDirty = true;
    }

    public void MarkDirty() {
        _hierarchyDirty = true;
    }

    private void Update() {
        bool dirty = _hierarchyDirty;
        if (!dirty) {
            for (int i = 0; i < _trackers.Length; i++)
                if (_trackers[i].HasChanged()) { dirty = true; break; }
        }
        if (dirty) {
            RebuildPostfix();
        }
    }

    private void RebuildPostfix() {
        _hierarchyDirty = false;
        _nodes.Clear();
        _primitives.Clear();
        _primitiveTransforms.Clear();

        AppendPostfix(_rootNode);

        // Rebuild transform trackers from all primitive nodes found during traversal
        _trackers = new TransformTracker[_primitiveTransforms.Count];
        for (int i = 0; i < _primitiveTransforms.Count; i++)
            _trackers[i] = new TransformTracker(_primitiveTransforms[i]);

        Rebuilt?.Invoke();
    }

    private void AppendPostfix(SdfNodeComponent node) {
        if ((int)node.nodeType < SdfNodeTypeRanges.PrimitivesEnd) {
            // primitive

            int primIdx = _primitives.Count;
            _primitives.Add(new BakedSdfPrimitive {
                transform = node.transform.worldToLocalMatrix,
                color    = (Vector4)node.color,
                material = new Vector4(node.metallic, node.smoothness, 0, 0),
            });
            _nodes.Add(MakePrimitive(node, primIdx));
            _primitiveTransforms.Add(node.transform);
            // Union any children with this primitive: [prim, C1, Union, C2, Union, ...]
            foreach (Transform child in node.transform) {
                if (!child.gameObject.activeInHierarchy) continue;
                SdfNodeComponent childNode = child.GetComponent<SdfNodeComponent>();
                if (childNode == null) continue;
                AppendPostfix(childNode);
                _nodes.Add(MakeOp(SdfNodeType.Union, 0f));
            }
        } else if ((int)node.nodeType < SdfNodeTypeRanges.BinaryOpsEnd) {
            // binary operator

            // interleave: [C1, C2, op, C3, op, ...]
            bool first = true;
            foreach (Transform child in node.transform) {
                if (!child.gameObject.activeInHierarchy) continue;
                SdfNodeComponent childNode = child.GetComponent<SdfNodeComponent>();
                if (childNode == null) continue;
                AppendPostfix(childNode);
                if (!first) _nodes.Add(MakeOp(node.nodeType, node.smoothK));
                first = false;
            }
        } else {
            // unary modifier

            // Union all children together first, then apply the modifier.
            bool first = true;
            foreach (Transform child in node.transform) {
                if (!child.gameObject.activeInHierarchy) continue;
                SdfNodeComponent childNode = child.GetComponent<SdfNodeComponent>();
                if (childNode == null) continue;
                AppendPostfix(childNode);
                if (!first) _nodes.Add(MakeOp(SdfNodeType.Union, 0f));
                first = false;
            }
            if (!first) { // had at least one child
                _nodes.Add(MakeUnary(node));
            }
        }
    }

    private static BakedSdfNode MakePrimitive(SdfNodeComponent node, int primitiveIndex) {
        return new BakedSdfNode { typeAndParams = PrimitiveTypeAndParams(node), primitiveIndex = primitiveIndex };
    }

    private static Vector4 PrimitiveTypeAndParams(SdfNodeComponent node) {
        switch (node.nodeType) {
            case SdfNodeType.Sphere:
                return new Vector4(0, node.sphereRadius, 0, 0);
            case SdfNodeType.Box:
                return new Vector4(1, node.boxHalfExtents.x, node.boxHalfExtents.y, node.boxHalfExtents.z);
            default:
                return new Vector4(2, node.torusMajorRadius, node.torusMinorRadius, 0);
        }
    }

    private static BakedSdfNode MakeUnary(SdfNodeComponent node) {
        float param;
        switch (node.nodeType) {
            case SdfNodeType.Shell:
                param = node.shellThickness;
                break;
            default:
                param = node.expandAmount;
                break;
        }
        return new BakedSdfNode {
            typeAndParams = new Vector4((int)node.nodeType, param, 0, 0),
            primitiveIndex = -1,
        };
    }

    private static BakedSdfNode MakeOp(SdfNodeType type, float smoothK) {
        int gpuType = (int)type;
        bool smooth = smoothK > 0f;
        if (smooth) {
            switch (type) {
                case SdfNodeType.Union:
                    gpuType = (int)SdfNodeType.SmoothUnion;
                    break;
                case SdfNodeType.Intersect:
                    gpuType = (int)SdfNodeType.SmoothIntersect;
                    break;
                case SdfNodeType.Subtract:
                    gpuType = (int)SdfNodeType.SmoothSubtract;
                    break;
            }
        }

        return new BakedSdfNode {
            typeAndParams = new Vector4(gpuType, smoothK, 0, 0),
            primitiveIndex = -1,
        };
    }

    public float GetDistance(Vector3 p) => SdfSceneDistanceCpu.GetDistance(_nodes, _primitives, p);
}
