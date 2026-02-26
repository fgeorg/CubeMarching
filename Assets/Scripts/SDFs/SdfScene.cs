using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

[ExecuteInEditMode]
public class SdfScene : MonoBehaviour
{
    // 80 bytes: Vector4 (16) + Matrix4x4 (64)
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuSdfNode
    {
        public Vector4   typeAndParams; // x=type, y=param0, z=param1, w=param2
        public Matrix4x4 transform;     // worldToLocalMatrix for primitives; identity for ops
    }

    [Tooltip("the static \"window\" into our sdf with the RayMarchScene material on it")]
    [SerializeField] private GameObject _SdfWindow;
    private Renderer             _renderer;
    private MaterialPropertyBlock _propertyBlock;
    private GraphicsBuffer        _buffer;
    private readonly List<GpuSdfNode> _nodes = new List<GpuSdfNode>();

    private void OnEnable()
    {
        _renderer      = _SdfWindow.GetComponent<Renderer>();
        _propertyBlock = new MaterialPropertyBlock();
        RebuildBuffer();
    }

    private void OnDisable()
    {
        _buffer?.Release();
        _buffer = null;
    }

    private void OnTransformChildrenChanged()
    {
        RebuildBuffer();
    }

    private void Update()
    {
        // TODO: replace with TransformTracker dirty detection — for now rebuild every frame
        RebuildBuffer();
    }

    private void RebuildBuffer()
    {
        if (_renderer == null) return;

        _nodes.Clear();
        bool first = true;

        foreach (Transform child in transform)
        {
            if (!child.gameObject.activeInHierarchy) continue;
            SdfNodeComponent node = child.GetComponent<SdfNodeComponent>();
            if (node == null) continue;
            TraversePostOrder(node, _nodes);
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
    }

    private void TraversePostOrder(SdfNodeComponent node, List<GpuSdfNode> list)
    {
        if ((int)node.nodeType < 10)
        {
            list.Add(MakePrimitive(node));
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
                TraversePostOrder(childNode, list);
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

    private static GpuSdfNode MakeOp(SdfNodeComponent.SdfNodeType type, float smoothK)
    {
        return new GpuSdfNode
        {
            typeAndParams = new Vector4((int)type, smoothK, 0, 0),
            transform     = Matrix4x4.identity
        };
    }
}
