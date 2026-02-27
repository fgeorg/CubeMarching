using UnityEngine;

// Sits on the same GameObject as the Renderer (the "SDF window" quad).
// Subscribes to SdfScene.Rebuilt, builds its own GPU buffer from the node list,
// and pushes it into the material property block.
[ExecuteInEditMode]
public class SdfRayMarchRenderer : MonoBehaviour
{
    [SerializeField] private SdfScene _sdfScene;
    private Renderer _renderer;
    private MaterialPropertyBlock _propertyBlock;
    private GraphicsBuffer _buffer;

    private void OnEnable()
    {
        _renderer = GetComponent<Renderer>();
        _propertyBlock = new MaterialPropertyBlock();
        if (_sdfScene != null) _sdfScene.Rebuilt += OnRebuilt;
        OnRebuilt(); // sync immediately in case SdfScene already has data
    }

    private void OnDisable()
    {
        if (_sdfScene != null) _sdfScene.Rebuilt -= OnRebuilt;
        _buffer?.Release();
        _buffer = null;
    }

    private void OnRebuilt()
    {
        if (_renderer == null || _sdfScene == null) return;

        var nodes = _sdfScene.Nodes;
        int count = nodes.Count;

        // Metal requires the buffer to always be bound, even when count is 0.
        // Allocate a minimum 1-element buffer and use _SdfNodeCount to gate the loop.
        int bufferSize = Mathf.Max(count, 1);
        if (_buffer == null || _buffer.count != bufferSize)
        {
            _buffer?.Release();
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, 80);
        }

        if (count > 0)
            _buffer.SetData(nodes);

        _propertyBlock.SetBuffer("_SdfNodes", _buffer);
        _propertyBlock.SetInteger("_SdfNodeCount", count);
        _renderer.SetPropertyBlock(_propertyBlock);
    }
}
