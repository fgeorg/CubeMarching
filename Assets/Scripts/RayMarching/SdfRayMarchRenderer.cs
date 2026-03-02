using System.Runtime.InteropServices;
using UnityEngine;

// Sits on the same GameObject as the Renderer (the "SDF window" quad).
// Subscribes to SdfScene.Rebuilt, builds its own GPU buffer from the node list,
// and pushes it into the material property block.
[ExecuteInEditMode]
public class SdfRayMarchRenderer : MonoBehaviour {
    [SerializeField] private SdfScene _sdfScene;
    private Renderer _renderer;
    private MaterialPropertyBlock _propertyBlock;
    private GraphicsBuffer _buffer;
    private GraphicsBuffer _primitivesBuffer;

    [Header("Voxel Acceleration")]
    [SerializeField] private ComputeShader _voxelBakeShader;
    [SerializeField] private bool _enableVoxelAccel = false;
    [SerializeField] private int _voxelResolution = 64;
    [SerializeField] private Vector3 _voxelCenter = Vector3.zero;
    [SerializeField] private float _voxelExtent = 10f;

    private RenderTexture _voxelTex;

    private void OnValidate() {
        OnRebuilt();
    }

    private void OnEnable() {
        _renderer = GetComponent<Renderer>();
        _propertyBlock = new MaterialPropertyBlock();
        if (_sdfScene != null) _sdfScene.Rebuilt += OnRebuilt;
        OnRebuilt(); // sync immediately in case SdfScene already has data
    }

    private void OnDisable() {
        if (_sdfScene != null) _sdfScene.Rebuilt -= OnRebuilt;
        _buffer?.Release();
        _buffer = null;
        _primitivesBuffer?.Release();
        _primitivesBuffer = null;
        if (_voxelTex != null)
        {
            _voxelTex.Release();
            _voxelTex = null;
        }
    }

    private void OnRebuilt() {
        if (_renderer == null || _sdfScene == null) return;

        var nodes = _sdfScene.Nodes;
        var primitives = _sdfScene.Primitives;
        int count = nodes.Count;
        int primCount = primitives.Count;

        // Metal requires the buffer to always be bound, even when count is 0.
        // Allocate a minimum 1-element buffer and use _SdfNodeCount to gate the loop.
        int bufferSize = Mathf.Max(count, 1);
        if (_buffer == null || _buffer.count != bufferSize) {
            _buffer?.Release();
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, Marshal.SizeOf<SdfScene.BakedSdfNode>());
        }

        int primBufferSize = Mathf.Max(primCount, 1);
        if (_primitivesBuffer == null || _primitivesBuffer.count != primBufferSize) {
            _primitivesBuffer?.Release();
            _primitivesBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, primBufferSize, Marshal.SizeOf<SdfScene.BakedSdfPrimitive>());
        }

        if (count > 0)
            _buffer.SetData(nodes);
        if (primCount > 0)
            _primitivesBuffer.SetData(primitives);

        _propertyBlock.SetBuffer("_SdfNodes", _buffer);
        _propertyBlock.SetBuffer("_SdfPrimitives", _primitivesBuffer);
        _propertyBlock.SetInteger("_SdfNodeCount", count);
        _renderer.SetPropertyBlock(_propertyBlock);

        if (_enableVoxelAccel && _voxelBakeShader != null)
            RebakeVoxels();
    }

    private void RebakeVoxels() {
        if (_voxelTex == null || _voxelTex.width != _voxelResolution)
        {
            if (_voxelTex != null) _voxelTex.Release();
            _voxelTex = new RenderTexture(_voxelResolution, _voxelResolution, 0, RenderTextureFormat.RHalf);
            _voxelTex.dimension         = UnityEngine.Rendering.TextureDimension.Tex3D;
            _voxelTex.volumeDepth       = _voxelResolution;
            _voxelTex.enableRandomWrite = true;
            _voxelTex.filterMode        = FilterMode.Bilinear;
            _voxelTex.wrapMode          = TextureWrapMode.Clamp;
            _voxelTex.Create();
        }

        int kernel = _voxelBakeShader.FindKernel("BakeVoxels");
        _voxelBakeShader.SetBuffer(kernel, "_SdfNodes",      _buffer);
        _voxelBakeShader.SetBuffer(kernel, "_SdfPrimitives", _primitivesBuffer);
        _voxelBakeShader.SetInt   ("_SdfNodeCount",    _sdfScene.NodeCount);
        _voxelBakeShader.SetTexture(kernel, "_VoxelOut", _voxelTex);
        float cellSize = _voxelExtent / _voxelResolution;
        Vector3 origin = _voxelCenter - Vector3.one * (_voxelExtent * 0.5f);
        _voxelBakeShader.SetVector("_VoxelOrigin",     origin);
        _voxelBakeShader.SetFloat ("_VoxelCellSize",   cellSize);
        _voxelBakeShader.SetInt   ("_VoxelResolution", _voxelResolution);
        int groups = Mathf.CeilToInt(_voxelResolution / 4f);
        _voxelBakeShader.Dispatch(kernel, groups, groups, groups);

        _propertyBlock.SetTexture("_VoxelTex",        _voxelTex);
        _propertyBlock.SetVector ("_VoxelOrigin",     origin);
        _propertyBlock.SetFloat  ("_VoxelCellSize",   cellSize);
        _propertyBlock.SetFloat  ("_VoxelResolution", _voxelResolution);
        _renderer.SetPropertyBlock(_propertyBlock);
    }
}
