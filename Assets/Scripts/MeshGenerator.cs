using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteInEditMode]
public class MeshGenerator : MonoBehaviour
{
    public enum EAlgorithm
    {
        CubeMarch,
        Voxels
    }

    [SerializeField] protected Bounds _bounds = new Bounds(Vector3.zero, Vector3.one);
    [SerializeField] protected EAlgorithm _algorithm = EAlgorithm.CubeMarch;
    [Range(1, 50)][SerializeField] protected int _resolution = 1;
    [Range(0, 10)][SerializeField] protected int _projectionSteps = 1;
    [Range(-3, 3)][SerializeField] protected float _projectionAmount = 0;

    [SerializeField] protected bool _getNormalsFromSDF = false;

    [SerializeField][Range(0, 1)] protected float _cubeMarchStepsToShow = 1;

    [SerializeField] protected CombinedDistanceField _distanceField = null;

    // Assign child GameObjects' MeshFilters here. Either or both may be set.
    // WireframeMesh: sequential index buffer, for use with the wireframe shader.
    // SmoothMesh: shared-vertex (deduped) index buffer, for smooth-shaded materials.
    [SerializeField] protected MeshFilter _wireframeMeshFilter = null;
    [SerializeField] protected MeshFilter _smoothMeshFilter = null;

    protected bool _shouldRegenerate = true;

    // Scratch buffers reused each regeneration
    private List<Vector3> _vertices = new List<Vector3>();
    private List<int> _triangles = new List<int>();
    private List<Vector3> _normals = new List<Vector3>();

    // Edge cache for during-generation dedup (marching cubes smooth path)
    private Dictionary<long, int> _edgeVertexCache = new Dictionary<long, int>();

    // Corner cache for during-generation dedup (voxel smooth path)
    private Dictionary<long, int> _cornerVertexCache = new Dictionary<long, int>();

    private Mesh _wireframeMesh;
    private Mesh _smoothMesh;

    public void MarkDirty()
    {
        _shouldRegenerate = true;
    }

    protected void OnValidate()
    {
        _shouldRegenerate = true;
    }

    protected void Update()
    {
        if (_shouldRegenerate)
        {
            Regenerate();
        }
    }

    protected void Regenerate()
    {
        _shouldRegenerate = false;
        float cubeSize = 1.0f / _resolution;
        if (_wireframeMeshFilter != null) RegenerateWireframe(cubeSize);
        if (_smoothMeshFilter != null) RegenerateSmooth(cubeSize);
    }

    // ─── Wireframe path ──────────────────────────────────────────────────────
    // No dedup — index buffer is always sequential (0, 1, 2, 3, 4, 5, …).
    // Barycentrics are baked into UV channel 1 (TEXCOORD1) so the shader
    // doesn't depend on SV_VertexID, which is unreliable in Unity's URP pipeline.

    private readonly List<Vector2> _barycentrics = new List<Vector2>();

    private void RegenerateWireframe(float cubeSize)
    {
        if (_wireframeMesh == null)
        {
            _wireframeMesh = new Mesh();
            _wireframeMesh.indexFormat = IndexFormat.UInt32;
            _wireframeMeshFilter.sharedMesh = _wireframeMesh;
        }
        _wireframeMesh.Clear();
        _vertices.Clear();
        _triangles.Clear();

        bool earlyExit = false;
        for (int x = 0; x < _resolution && !earlyExit; x++)
        {
            for (int y = 0; y < _resolution && !earlyExit; y++)
            {
                for (int z = 0; z < _resolution; z++)
                {
                    switch (_algorithm)
                    {
                        case EAlgorithm.CubeMarch:
                            AddCube(_vertices, _triangles, x, y, z, cubeSize);
                            break;
                        case EAlgorithm.Voxels:
                            AddVoxel(_vertices, _triangles, x, y, z, cubeSize);
                            break;
                    }
                    float percentDone = (float)(x * _resolution * _resolution + y * _resolution + z)
                                      / (_resolution * _resolution * _resolution);
                    if (percentDone > _cubeMarchStepsToShow) { earlyExit = true; break; }
                }
            }
        }

        ProjectVerticesToSurface();

        // TODO: we can likely do this in the shader itself using vertex index 
        if (_algorithm == EAlgorithm.CubeMarch)
        {
            // Bake per-vertex barycentric coordinates into UV1.
            // Sequential index buffer guarantees vertex i is always position (i % 3) in its triangle.
            _barycentrics.Clear();
            for (int i = 0; i < _vertices.Count; i++)
            {
                int pos = i % 3;
                _barycentrics.Add(pos == 0 ? new Vector2(1, 0) :
                                  pos == 1 ? new Vector2(0, 1) : Vector2.zero);
            }
        }
        else
        {
            _barycentrics.Clear();
            for (int i = 0; i < _vertices.Count; i++)
            {
                int pos = i % 4;
                _barycentrics.Add(pos == 0 ? new Vector2(1, 0) :
                                  pos == 1 || pos == 3 ? new Vector2(0, 1) :
                                  Vector2.zero);
            }
        }

        _wireframeMesh.SetVertices(_vertices);
        _wireframeMesh.SetTriangles(_triangles, 0);
        _wireframeMesh.SetUVs(1, _barycentrics);
        ApplyNormals(_wireframeMesh);
    }

    // ─── Smooth path ─────────────────────────────────────────────────────────
    // CubeMarch: dedup during generation using an edge dictionary keyed on integer corner indices.
    // Voxels: dedup during generation using a corner dictionary keyed on integer grid coords.

    private void RegenerateSmooth(float cubeSize)
    {
        if (_smoothMesh == null)
        {
            _smoothMesh = new Mesh();
            _smoothMesh.indexFormat = IndexFormat.UInt32;
            _smoothMeshFilter.sharedMesh = _smoothMesh;
        }
        _smoothMesh.Clear();
        _vertices.Clear();
        _triangles.Clear();

        if (_algorithm == EAlgorithm.CubeMarch)
        {
            _edgeVertexCache.Clear();
            bool earlyExit = false;
            for (int x = 0; x < _resolution && !earlyExit; x++)
            {
                for (int y = 0; y < _resolution && !earlyExit; y++)
                {
                    for (int z = 0; z < _resolution; z++)
                    {
                        AddCubeWithEdgeDedup(x, y, z, cubeSize);
                        float percentDone = (float)(x * _resolution * _resolution + y * _resolution + z)
                                          / (_resolution * _resolution * _resolution);
                        if (percentDone > _cubeMarchStepsToShow) { earlyExit = true; break; }
                    }
                }
            }
        }
        else
        {
            _cornerVertexCache.Clear();
            bool earlyExit = false;
            for (int x = 0; x < _resolution && !earlyExit; x++)
            {
                for (int y = 0; y < _resolution && !earlyExit; y++)
                {
                    for (int z = 0; z < _resolution; z++)
                    {
                        AddVoxelWithCornerDedup(x, y, z, cubeSize);
                        float percentDone = (float)(x * _resolution * _resolution + y * _resolution + z)
                                          / (_resolution * _resolution * _resolution);
                        if (percentDone > _cubeMarchStepsToShow) { earlyExit = true; break; }
                    }
                }
            }
        }

        ProjectVerticesToSurface();
        _smoothMesh.SetVertices(_vertices);
        _smoothMesh.SetTriangles(_triangles, 0);
        ApplyNormals(_smoothMesh);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void ApplyNormals(Mesh mesh)
    {
        if (_getNormalsFromSDF)
        {
            _normals.Clear();
            for (int i = 0; i < _vertices.Count; i++)
            {
                _normals.Add(GetNormal(_vertices[i]));
            }
            mesh.SetNormals(_normals);
        }
        else
        {
            mesh.RecalculateNormals();
        }
    }

    protected Vector3 CenterPointAtIndices(int x, int y, int z, float cubeSize)
    {
        return new Vector3(
            (x + 0.5f) * cubeSize * (_bounds.max.x - _bounds.min.x) + _bounds.min.x,
            (y + 0.5f) * cubeSize * (_bounds.max.y - _bounds.min.y) + _bounds.min.y,
            (z + 0.5f) * cubeSize * (_bounds.max.z - _bounds.min.z) + _bounds.min.z
        );
    }

    protected Vector3 PointAtIndices(int x, int y, int z, float cubeSize)
    {
        return new Vector3(
            x * cubeSize * (_bounds.max.x - _bounds.min.x) + _bounds.min.x,
            y * cubeSize * (_bounds.max.y - _bounds.min.y) + _bounds.min.y,
            z * cubeSize * (_bounds.max.z - _bounds.min.z) + _bounds.min.z
        );
    }

    protected void ProjectVerticesToSurface()
    {
        for (int i = 0; i < _vertices.Count; i++)
        {
            for (int j = 0; j < _projectionSteps; j++)
            {
                var n = GetNormal(_vertices[i]);
                _vertices[i] -= n * GetDistance(_vertices[i]) * _projectionAmount;
            }
        }
    }

    // During-generation dedup for the marching cubes smooth path.
    // Each triangle vertex lies on a cube edge; the EdgeKey uniquely identifies
    // that edge regardless of which adjacent cube visits it first.
    private void AddCubeWithEdgeDedup(int xi, int yi, int zi, float cubeSize)
    {
        int bits =
                GetDistance(PointAtIndices(xi + 0, yi + 0, zi + 1, cubeSize)) < 0 ? (1 << 0) : 0;
        bits |= GetDistance(PointAtIndices(xi + 1, yi + 0, zi + 1, cubeSize)) < 0 ? (1 << 1) : 0;
        bits |= GetDistance(PointAtIndices(xi + 1, yi + 0, zi + 0, cubeSize)) < 0 ? (1 << 2) : 0;
        bits |= GetDistance(PointAtIndices(xi + 0, yi + 0, zi + 0, cubeSize)) < 0 ? (1 << 3) : 0;
        bits |= GetDistance(PointAtIndices(xi + 0, yi + 1, zi + 1, cubeSize)) < 0 ? (1 << 4) : 0;
        bits |= GetDistance(PointAtIndices(xi + 1, yi + 1, zi + 1, cubeSize)) < 0 ? (1 << 5) : 0;
        bits |= GetDistance(PointAtIndices(xi + 1, yi + 1, zi + 0, cubeSize)) < 0 ? (1 << 6) : 0;
        bits |= GetDistance(PointAtIndices(xi + 0, yi + 1, zi + 0, cubeSize)) < 0 ? (1 << 7) : 0;

        var origin = PointAtIndices(xi, yi, zi, cubeSize);
        var cubeDim = (_bounds.max - _bounds.min) * cubeSize;
        int stride = _resolution + 1;
        int strideX = stride * stride;

        var tris = MarchTables.triangulation[~bits & 255];
        foreach (var edgeIndex in tris)
        {
            int dxA = MarchTables.edgeCornerOffsets[edgeIndex, 0];
            int dyA = MarchTables.edgeCornerOffsets[edgeIndex, 1];
            int dzA = MarchTables.edgeCornerOffsets[edgeIndex, 2];
            int dxB = MarchTables.edgeCornerOffsets[edgeIndex, 3];
            int dyB = MarchTables.edgeCornerOffsets[edgeIndex, 4];
            int dzB = MarchTables.edgeCornerOffsets[edgeIndex, 5];

            int cornerA = (xi + dxA) * strideX + (yi + dyA) * stride + (zi + dzA);
            int cornerB = (xi + dxB) * strideX + (yi + dyB) * stride + (zi + dzB);
            long key = EdgeKey(cornerA, cornerB);

            if (_edgeVertexCache.TryGetValue(key, out int idx))
            {
                _triangles.Add(idx);
            }
            else
            {
                int newIdx = _vertices.Count;
                _edgeVertexCache[key] = newIdx;
                _triangles.Add(newIdx);
                var ep = MarchTables.edgePoints[edgeIndex];
                _vertices.Add(new Vector3(origin.x + ep.x * cubeDim.x,
                                          origin.y + ep.y * cubeDim.y,
                                          origin.z + ep.z * cubeDim.z));
            }
        }
    }

    private static long EdgeKey(int a, int b)
    {
        int lo = a < b ? a : b;
        int hi = a < b ? b : a;
        return ((long)lo << 32) | (uint)hi;
    }

    // During-generation dedup for the voxel smooth path.
    // Keyed on integer grid coordinates (xi, yi, zi) — no float comparison, no ambiguity.
    private void AddVoxelWithCornerDedup(int xi, int yi, int zi, float cubeSize)
    {
        if (GetDistance(CenterPointAtIndices(xi, yi, zi, cubeSize)) > 0) return;

        //below (-Y face)
        if (GetDistance(CenterPointAtIndices(xi, yi - 1, zi, cubeSize)) > 0)
        {
            int i000 = GetOrAddVoxelCorner(xi,     yi,     zi,     cubeSize);
            int i100 = GetOrAddVoxelCorner(xi + 1, yi,     zi,     cubeSize);
            int i101 = GetOrAddVoxelCorner(xi + 1, yi,     zi + 1, cubeSize);
            int i001 = GetOrAddVoxelCorner(xi,     yi,     zi + 1, cubeSize);
            _triangles.Add(i000); _triangles.Add(i100); _triangles.Add(i101);
            _triangles.Add(i000); _triangles.Add(i101); _triangles.Add(i001);
        }
        //above (+Y face)
        if (GetDistance(CenterPointAtIndices(xi, yi + 1, zi, cubeSize)) > 0)
        {
            int i010 = GetOrAddVoxelCorner(xi,     yi + 1, zi,     cubeSize);
            int i011 = GetOrAddVoxelCorner(xi,     yi + 1, zi + 1, cubeSize);
            int i111 = GetOrAddVoxelCorner(xi + 1, yi + 1, zi + 1, cubeSize);
            int i110 = GetOrAddVoxelCorner(xi + 1, yi + 1, zi,     cubeSize);
            _triangles.Add(i010); _triangles.Add(i011); _triangles.Add(i111);
            _triangles.Add(i010); _triangles.Add(i111); _triangles.Add(i110);
        }
        //left (-X face)
        if (GetDistance(CenterPointAtIndices(xi - 1, yi, zi, cubeSize)) > 0)
        {
            int i000 = GetOrAddVoxelCorner(xi, yi,     zi,     cubeSize);
            int i001 = GetOrAddVoxelCorner(xi, yi,     zi + 1, cubeSize);
            int i011 = GetOrAddVoxelCorner(xi, yi + 1, zi + 1, cubeSize);
            int i010 = GetOrAddVoxelCorner(xi, yi + 1, zi,     cubeSize);
            _triangles.Add(i000); _triangles.Add(i001); _triangles.Add(i011);
            _triangles.Add(i000); _triangles.Add(i011); _triangles.Add(i010);
        }
        //right (+X face)
        if (GetDistance(CenterPointAtIndices(xi + 1, yi, zi, cubeSize)) > 0)
        {
            int i100 = GetOrAddVoxelCorner(xi + 1, yi,     zi,     cubeSize);
            int i110 = GetOrAddVoxelCorner(xi + 1, yi + 1, zi,     cubeSize);
            int i111 = GetOrAddVoxelCorner(xi + 1, yi + 1, zi + 1, cubeSize);
            int i101 = GetOrAddVoxelCorner(xi + 1, yi,     zi + 1, cubeSize);
            _triangles.Add(i100); _triangles.Add(i110); _triangles.Add(i111);
            _triangles.Add(i100); _triangles.Add(i111); _triangles.Add(i101);
        }
        //front (-Z face)
        if (GetDistance(CenterPointAtIndices(xi, yi, zi - 1, cubeSize)) > 0)
        {
            int i000 = GetOrAddVoxelCorner(xi,     yi,     zi, cubeSize);
            int i010 = GetOrAddVoxelCorner(xi,     yi + 1, zi, cubeSize);
            int i110 = GetOrAddVoxelCorner(xi + 1, yi + 1, zi, cubeSize);
            int i100 = GetOrAddVoxelCorner(xi + 1, yi,     zi, cubeSize);
            _triangles.Add(i000); _triangles.Add(i010); _triangles.Add(i110);
            _triangles.Add(i000); _triangles.Add(i110); _triangles.Add(i100);
        }
        //back (+Z face)
        if (GetDistance(CenterPointAtIndices(xi, yi, zi + 1, cubeSize)) > 0)
        {
            int i001 = GetOrAddVoxelCorner(xi,     yi,     zi + 1, cubeSize);
            int i101 = GetOrAddVoxelCorner(xi + 1, yi,     zi + 1, cubeSize);
            int i111 = GetOrAddVoxelCorner(xi + 1, yi + 1, zi + 1, cubeSize);
            int i011 = GetOrAddVoxelCorner(xi,     yi + 1, zi + 1, cubeSize);
            _triangles.Add(i001); _triangles.Add(i101); _triangles.Add(i111);
            _triangles.Add(i001); _triangles.Add(i111); _triangles.Add(i011);
        }
    }

    private int GetOrAddVoxelCorner(int xi, int yi, int zi, float cubeSize)
    {
        long key = CornerKey(xi, yi, zi);
        if (_cornerVertexCache.TryGetValue(key, out int idx)) return idx;
        int newIdx = _vertices.Count;
        _cornerVertexCache[key] = newIdx;
        _vertices.Add(PointAtIndices(xi, yi, zi, cubeSize));
        return newIdx;
    }

    // Packs integer grid coords into a single long. Resolution ≤ 50 means
    // coords ≤ 51, which fits comfortably in 21 bits per axis.
    private static long CornerKey(int x, int y, int z) =>
        ((long)x << 42) | ((long)y << 21) | (long)z;

    protected float GetDistance(Vector3 p)
    {
        return _distanceField.GetDistance(transform.TransformPoint(p));
    }

    protected Vector3 GetNormal(Vector3 p)
    {
        Vector3 n = new Vector3(
            GetDistance(p + new Vector3(1e-2f, 0, 0)),
            GetDistance(p + new Vector3(0, 1e-2f, 0)),
            GetDistance(p + new Vector3(0, 0, 1e-2f))
        ) - GetDistance(p) * Vector3.one;
        return n.normalized;
    }

    protected float SMinCubic(float a, float b, float k)
    {
        if (k <= 0)
        {
            return Math.Min(a, b);
        }
        float h = Math.Max(k - Math.Abs(a - b), 0.0f) / k;
        return Math.Min(a, b) - h * h * h * k * (1.0f / 6.0f);
    }

    protected void AddCube(List<Vector3> vertices, List<int> triangles, int xi, int yi, int zi, float cubeSize)
    {
        int bits =
                GetDistance(PointAtIndices(xi + 0, yi + 0, zi + 1, cubeSize)) < 0 ? (1 << 0) : 0;
        bits |= GetDistance(PointAtIndices(xi + 1, yi + 0, zi + 1, cubeSize)) < 0 ? (1 << 1) : 0;
        bits |= GetDistance(PointAtIndices(xi + 1, yi + 0, zi + 0, cubeSize)) < 0 ? (1 << 2) : 0;
        bits |= GetDistance(PointAtIndices(xi + 0, yi + 0, zi + 0, cubeSize)) < 0 ? (1 << 3) : 0;
        bits |= GetDistance(PointAtIndices(xi + 0, yi + 1, zi + 1, cubeSize)) < 0 ? (1 << 4) : 0;
        bits |= GetDistance(PointAtIndices(xi + 1, yi + 1, zi + 1, cubeSize)) < 0 ? (1 << 5) : 0;
        bits |= GetDistance(PointAtIndices(xi + 1, yi + 1, zi + 0, cubeSize)) < 0 ? (1 << 6) : 0;
        bits |= GetDistance(PointAtIndices(xi + 0, yi + 1, zi + 0, cubeSize)) < 0 ? (1 << 7) : 0;
        var origin = PointAtIndices(xi, yi, zi, cubeSize);
        var cubeDim = (_bounds.max - _bounds.min) * cubeSize;

        var tris = MarchTables.triangulation[~bits & 255];
        foreach (var tri in tris)
        {
            _triangles.Add(_vertices.Count);
            var edgePoint = MarchTables.edgePoints[tri];
            _vertices.Add(new Vector3(origin.x + edgePoint.x * cubeDim.x,
                                      origin.y + edgePoint.y * cubeDim.y,
                                      origin.z + edgePoint.z * cubeDim.z));
        }
    }

    protected void AddVoxel(List<Vector3> vertices, List<int> triangles, int xi, int yi, int zi, float cubeSize)
    {
        if (GetDistance(CenterPointAtIndices(xi, yi, zi, cubeSize)) > 0) return;

        // Corners keyed by integer grid indices via PointAtIndices.
        // Each corner is computed as: n * cubeSize * (max - min) + min
        // Two adjacent voxels sharing a corner call PointAtIndices with the same integer n,
        // so they take an identical arithmetic path and get bit-identical floats.
        // This makes Dictionary<Vector3, int> dedup safe (no float-equality ambiguity).
        Vector3 p000 = PointAtIndices(xi,     yi,     zi,     cubeSize);
        Vector3 p100 = PointAtIndices(xi + 1, yi,     zi,     cubeSize);
        Vector3 p010 = PointAtIndices(xi,     yi + 1, zi,     cubeSize);
        Vector3 p110 = PointAtIndices(xi + 1, yi + 1, zi,     cubeSize);
        Vector3 p001 = PointAtIndices(xi,     yi,     zi + 1, cubeSize);
        Vector3 p101 = PointAtIndices(xi + 1, yi,     zi + 1, cubeSize);
        Vector3 p011 = PointAtIndices(xi,     yi + 1, zi + 1, cubeSize);
        Vector3 p111 = PointAtIndices(xi + 1, yi + 1, zi + 1, cubeSize);

        //below (-Y face)
        if (GetDistance(CenterPointAtIndices(xi, yi - 1, zi, cubeSize)) > 0)
        {
            vertices.Add(p000); vertices.Add(p100); vertices.Add(p101); vertices.Add(p001);
            AddQuadIndices(triangles, vertices.Count);
        }
        //above (+Y face)
        if (GetDistance(CenterPointAtIndices(xi, yi + 1, zi, cubeSize)) > 0)
        {
            vertices.Add(p010); vertices.Add(p011); vertices.Add(p111); vertices.Add(p110);
            AddQuadIndices(triangles, vertices.Count);
        }
        //left (-X face)
        if (GetDistance(CenterPointAtIndices(xi - 1, yi, zi, cubeSize)) > 0)
        {
            vertices.Add(p000); vertices.Add(p001); vertices.Add(p011); vertices.Add(p010);
            AddQuadIndices(triangles, vertices.Count);
        }
        //right (+X face)
        if (GetDistance(CenterPointAtIndices(xi + 1, yi, zi, cubeSize)) > 0)
        {
            vertices.Add(p100); vertices.Add(p110); vertices.Add(p111); vertices.Add(p101);
            AddQuadIndices(triangles, vertices.Count);
        }
        //front (-Z face)
        if (GetDistance(CenterPointAtIndices(xi, yi, zi - 1, cubeSize)) > 0)
        {
            vertices.Add(p000); vertices.Add(p010); vertices.Add(p110); vertices.Add(p100);
            AddQuadIndices(triangles, vertices.Count);
        }
        //back (+Z face)
        if (GetDistance(CenterPointAtIndices(xi, yi, zi + 1, cubeSize)) > 0)
        {
            vertices.Add(p001); vertices.Add(p101); vertices.Add(p111); vertices.Add(p011);
            AddQuadIndices(triangles, vertices.Count);
        }
    }

    protected void AddQuadIndices(List<int> triangles, int endIndex)
    {
        triangles.Add(endIndex - 4);
        triangles.Add(endIndex - 3);
        triangles.Add(endIndex - 2);
        triangles.Add(endIndex - 4);
        triangles.Add(endIndex - 2);
        triangles.Add(endIndex - 1);
    }
}
