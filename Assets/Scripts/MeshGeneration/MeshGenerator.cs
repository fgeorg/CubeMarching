using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// NOTE: This class has grown large enough that splitting the cell-generator
// logic (AddCube/AddVoxel and their dedup counterparts) into separate helper
// classes may be worth considering.
[ExecuteInEditMode]
public class MeshGenerator : MonoBehaviour {
    public enum EAlgorithm {
        Minecraft,
        MarchingCubes,
    }

    [SerializeField] protected Bounds _bounds = new Bounds(Vector3.zero, Vector3.one);
    [SerializeField] protected EAlgorithm _algorithm = EAlgorithm.MarchingCubes;
    [Range(1, 50)][SerializeField] protected int _resolution = 1;
    [Range(0, 10)][SerializeField] protected int _projectionSteps = 1;
    [Range(0, 1)][SerializeField] protected float _projectionAmount = 0;

    [SerializeField] protected bool _getNormalsFromSDF = false;

    [SerializeField][Range(0, 1)] protected float _cubeMarchStepsToShow = 1;

    [SerializeField] protected SdfScene _sdfScene = null;

    // Assign child GameObjects' MeshFilters here. Either or both may be set.
    // WireframeMesh: sequential index buffer, for use with the wireframe shader.
    // SmoothMesh: shared-vertex (deduped) index buffer, for smooth-shaded materials.
    [SerializeField] protected MeshFilter _fullMeshFilter = null;
    [SerializeField] protected MeshFilter _dedupedMeshFilter = null;

    protected bool _shouldRegenerate = true;
    private TransformTracker _selfTracker;

    // Scratch buffers reused each regeneration
    private List<Vector3> _vertices = new List<Vector3>();
    private List<int> _triangles = new List<int>();
    private List<Vector3> _normals = new List<Vector3>();
    private readonly List<Vector2> _barycentrics = new List<Vector2>();

    // Edge cache for during-generation dedup (marching cubes smooth path)
    private Dictionary<long, int> _edgeVertexCache = new Dictionary<long, int>();

    // Corner cache for during-generation dedup (voxel smooth path)
    private Dictionary<long, int> _cornerVertexCache = new Dictionary<long, int>();

    private Mesh _fullMesh;
    private Mesh _dedupedMesh;

    // ─── Unity lifecycle ──────────────────────────────────────────────────────

    public void MarkDirty() {
        _shouldRegenerate = true;
    }

    protected void OnEnable() {
        if (_sdfScene != null) {
            _sdfScene.Rebuilt += MarkDirty;
        }
        _selfTracker = new TransformTracker(transform);
        MarkDirty();
    }

    protected void OnDisable() {
        if (_fullMesh != null) {
            _fullMesh.Clear();
        }
        if (_dedupedMesh != null) {
            _dedupedMesh.Clear();
        }
        if (_sdfScene != null) {
            _sdfScene.Rebuilt -= MarkDirty;
        }
    }

    protected void OnValidate() {
        _shouldRegenerate = true;
    }

    protected void Update() {
        if (_selfTracker.HasChanged()) {
            _shouldRegenerate = true;
        }
        if (_shouldRegenerate) {
            Regenerate();
        }
    }

    // ─── Top-level regeneration dispatch ─────────────────────────────────────

    protected void Regenerate() {
        _shouldRegenerate = false;
        float cubeSize = 1.0f / _resolution;
        if (_fullMeshFilter != null) {
            RegenerateWireframe(cubeSize);
        }
        if (_dedupedMeshFilter != null) {
            RegenerateDeduped(cubeSize);
        }
    }

    // No dedup — index buffer is always sequential (0, 1, 2, 3, 4, 5, …).
    // Barycentrics are baked into UV channel 1 (TEXCOORD1)
    private void RegenerateWireframe(float cubeSize) {
        if (_fullMesh == null) {
            _fullMesh = new Mesh();
            _fullMesh.indexFormat = IndexFormat.UInt32;
            _fullMeshFilter.sharedMesh = _fullMesh;
        }
        _fullMesh.Clear();
        _vertices.Clear();
        _triangles.Clear();

        bool earlyExit = false;
        for (int x = 0; x < _resolution && !earlyExit; x++) {
            for (int y = 0; y < _resolution && !earlyExit; y++) {
                for (int z = 0; z < _resolution; z++) {
                    switch (_algorithm) {
                        case EAlgorithm.MarchingCubes:
                            AddCube(_vertices, _triangles, x, y, z, cubeSize);
                            break;
                        case EAlgorithm.Minecraft:
                            AddVoxel(x, y, z, cubeSize);
                            break;
                    }
                    float percentDone = (float)(x * _resolution * _resolution + y * _resolution + z)
                                      / (_resolution * _resolution * _resolution);
                    if (percentDone > _cubeMarchStepsToShow) { earlyExit = true; break; }
                }
            }
        }

        ProjectVerticesToSurface();

        // Bake per-vertex barycentric coordinates into UV1.
        // Sequential index buffer guarantees vertex i is always position (i % 3) in its triangle.
        _barycentrics.Clear();
        bool isMC = _algorithm == EAlgorithm.MarchingCubes;
        for (int i = 0; i < _vertices.Count; i++) {
            int pos = i % (isMC ? 3 : 4);
            _barycentrics.Add(pos == 0 ? new Vector2(1, 0) :
                              (pos == 1 || (!isMC && pos == 3)) ? new Vector2(0, 1) :
                              Vector2.zero);
        }

        _fullMesh.SetVertices(_vertices);
        _fullMesh.SetTriangles(_triangles, 0);
        DebugStore.Set("Full", $"{_vertices.Count} v   {_triangles.Count / 3} t");
        _fullMesh.SetUVs(1, _barycentrics);
        ApplyNormals(_fullMesh);
    }

    private void RegenerateDeduped(float cubeSize) {
        if (_dedupedMesh == null) {
            _dedupedMesh = new Mesh();
            _dedupedMesh.indexFormat = IndexFormat.UInt32;
            _dedupedMeshFilter.sharedMesh = _dedupedMesh;
        }
        _dedupedMesh.Clear();
        _vertices.Clear();
        _triangles.Clear();

        if (_algorithm == EAlgorithm.MarchingCubes) {
            _edgeVertexCache.Clear();
        } else {
            _cornerVertexCache.Clear();
        }

        bool earlyExit = false;
        for (int x = 0; x < _resolution && !earlyExit; x++) {
            for (int y = 0; y < _resolution && !earlyExit; y++) {
                for (int z = 0; z < _resolution; z++) {
                    if (_algorithm == EAlgorithm.MarchingCubes) {
                        AddCubeWithEdgeDedup(x, y, z, cubeSize);
                    } else {
                        AddVoxelWithCornerDedup(x, y, z, cubeSize);
                    }
                    float percentDone = (float)(x * _resolution * _resolution + y * _resolution + z)
                                      / (_resolution * _resolution * _resolution);
                    if (percentDone > _cubeMarchStepsToShow) { earlyExit = true; break; }
                }
            }
        }

        ProjectVerticesToSurface();
        _dedupedMesh.SetVertices(_vertices);
        _dedupedMesh.SetTriangles(_triangles, 0);
        DebugStore.Set("Deduped", $"{_vertices.Count} v   {_triangles.Count / 3} t");
        ApplyNormals(_dedupedMesh);
    }

    // ─── Wireframe (non-dedup) cell generators ────────────────────────────────

    protected void AddCube(List<Vector3> vertices, List<int> triangles, int xi, int yi, int zi, float cubeSize) {
        int bits = GetCornerBits(xi, yi, zi, cubeSize);
        var origin = PointAtIndices(xi, yi, zi, cubeSize);
        var cubeDim = (_bounds.max - _bounds.min) * cubeSize;

        var tris = MarchTables.triangulation[~bits & 255];
        foreach (var tri in tris) {
            _triangles.Add(_vertices.Count);
            var edgePoint = MarchTables.edgePoints[tri];
            _vertices.Add(new Vector3(origin.x + edgePoint.x * cubeDim.x,
                                      origin.y + edgePoint.y * cubeDim.y,
                                      origin.z + edgePoint.z * cubeDim.z));
        }
    }

    protected void AddVoxel(int xi, int yi, int zi, float cubeSize) {
        if (GetDistance(CenterPointAtIndices(xi, yi, zi, cubeSize)) > 0) { return; }

        // Bottom face
        if (GetDistance(CenterPointAtIndices(xi, yi - 1, zi, cubeSize)) > 0) {
            AddQuad((xi, yi, zi),
                    (xi + 1, yi, zi),
                    (xi + 1, yi, zi + 1),
                    (xi, yi, zi + 1),
                    cubeSize);
        }
        // Top face
        if (GetDistance(CenterPointAtIndices(xi, yi + 1, zi, cubeSize)) > 0) {
            AddQuad((xi, yi + 1, zi),
                    (xi, yi + 1, zi + 1),
                    (xi + 1, yi + 1, zi + 1),
                    (xi + 1, yi + 1, zi),
                    cubeSize);
        }
        // Left face
        if (GetDistance(CenterPointAtIndices(xi - 1, yi, zi, cubeSize)) > 0) {
            AddQuad((xi, yi, zi),
                    (xi, yi, zi + 1),
                    (xi, yi + 1, zi + 1),
                    (xi, yi + 1, zi),
                    cubeSize);
        }
        // Right face
        if (GetDistance(CenterPointAtIndices(xi + 1, yi, zi, cubeSize)) > 0) {
            AddQuad((xi + 1, yi, zi),
                    (xi + 1, yi + 1, zi),
                    (xi + 1, yi + 1, zi + 1),
                    (xi + 1, yi, zi + 1),
                    cubeSize);
        }
        // Front face
        if (GetDistance(CenterPointAtIndices(xi, yi, zi - 1, cubeSize)) > 0) {
            AddQuad((xi, yi, zi),
                    (xi, yi + 1, zi),
                    (xi + 1, yi + 1, zi),
                    (xi + 1, yi, zi),
                    cubeSize);
        }
        // Back face
        if (GetDistance(CenterPointAtIndices(xi, yi, zi + 1, cubeSize)) > 0) {
            AddQuad((xi, yi, zi + 1),
                    (xi + 1, yi, zi + 1),
                    (xi + 1, yi + 1, zi + 1),
                    (xi, yi + 1, zi + 1),
                    cubeSize);
        }
    }

    protected void AddQuad(
            (int x, int y, int z) a, (int x, int y, int z) b,
            (int x, int y, int z) c, (int x, int y, int z) d,
            float cubeSize) {
        _vertices.Add(PointAtIndices(a.x, a.y, a.z, cubeSize));
        _vertices.Add(PointAtIndices(b.x, b.y, b.z, cubeSize));
        _vertices.Add(PointAtIndices(c.x, c.y, c.z, cubeSize));
        _vertices.Add(PointAtIndices(d.x, d.y, d.z, cubeSize));
        AddQuadIndices(_triangles, _vertices.Count);
    }

    protected void AddQuadIndices(List<int> triangles, int endIndex) {
        triangles.Add(endIndex - 4);
        triangles.Add(endIndex - 3);
        triangles.Add(endIndex - 2);
        triangles.Add(endIndex - 4);
        triangles.Add(endIndex - 2);
        triangles.Add(endIndex - 1);
    }

    // ─── Dedup cell generators ────────────────────────────────────────────────

    // Shared: compute the 8-corner sign bitmask for the cube at (xi, yi, zi).
    private int GetCornerBits(int xi, int yi, int zi, float cubeSize) {
        int bits =
                GetDistance(PointAtIndices(xi + 0, yi + 0, zi + 1, cubeSize)) < 0 ? (1 << 0) : 0;
        bits |= GetDistance(PointAtIndices(xi + 1, yi + 0, zi + 1, cubeSize)) < 0 ? (1 << 1) : 0;
        bits |= GetDistance(PointAtIndices(xi + 1, yi + 0, zi + 0, cubeSize)) < 0 ? (1 << 2) : 0;
        bits |= GetDistance(PointAtIndices(xi + 0, yi + 0, zi + 0, cubeSize)) < 0 ? (1 << 3) : 0;
        bits |= GetDistance(PointAtIndices(xi + 0, yi + 1, zi + 1, cubeSize)) < 0 ? (1 << 4) : 0;
        bits |= GetDistance(PointAtIndices(xi + 1, yi + 1, zi + 1, cubeSize)) < 0 ? (1 << 5) : 0;
        bits |= GetDistance(PointAtIndices(xi + 1, yi + 1, zi + 0, cubeSize)) < 0 ? (1 << 6) : 0;
        bits |= GetDistance(PointAtIndices(xi + 0, yi + 1, zi + 0, cubeSize)) < 0 ? (1 << 7) : 0;
        return bits;
    }

    // Each triangle vertex lies on a cube edge; the EdgeKey uniquely identifies
    // that edge regardless of which adjacent cube visits it first.
    private void AddCubeWithEdgeDedup(int xi, int yi, int zi, float cubeSize) {
        int bits = GetCornerBits(xi, yi, zi, cubeSize);
        var origin = PointAtIndices(xi, yi, zi, cubeSize);
        var cubeDim = (_bounds.max - _bounds.min) * cubeSize;
        int stride = _resolution + 1;
        int strideX = stride * stride;

        var tris = MarchTables.triangulation[~bits & 255];
        foreach (var edgeIndex in tris) {
            int dxA = MarchTables.edgeCorners[edgeIndex, 0];
            int dyA = MarchTables.edgeCorners[edgeIndex, 1];
            int dzA = MarchTables.edgeCorners[edgeIndex, 2];
            int dxB = MarchTables.edgeCorners[edgeIndex, 3];
            int dyB = MarchTables.edgeCorners[edgeIndex, 4];
            int dzB = MarchTables.edgeCorners[edgeIndex, 5];

            int cornerA = (xi + dxA) * strideX + (yi + dyA) * stride + (zi + dzA);
            int cornerB = (xi + dxB) * strideX + (yi + dyB) * stride + (zi + dzB);
            long key = EdgeKey(cornerA, cornerB);

            if (_edgeVertexCache.TryGetValue(key, out int idx)) {
                _triangles.Add(idx);
            } else {
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

    private static long EdgeKey(int a, int b) {
        int lo = a < b ? a : b;
        int hi = a < b ? b : a;
        return ((long)lo << 32) | (uint)hi;
    }

    // Keyed on integer grid coordinates (xi, yi, zi) — no float comparison, no ambiguity.
    private void AddVoxelWithCornerDedup(int xi, int yi, int zi, float cubeSize) {
        if (GetDistance(CenterPointAtIndices(xi, yi, zi, cubeSize)) > 0) { return; }

        // Bottom face
        if (GetDistance(CenterPointAtIndices(xi, yi - 1, zi, cubeSize)) > 0) {
            AddDedupedQuad((xi, yi, zi),
                           (xi + 1, yi, zi),
                           (xi + 1, yi, zi + 1),
                           (xi, yi, zi + 1),
                           cubeSize);
        }
        // Top face
        if (GetDistance(CenterPointAtIndices(xi, yi + 1, zi, cubeSize)) > 0) {
            AddDedupedQuad((xi, yi + 1, zi),
                           (xi, yi + 1, zi + 1),
                           (xi + 1, yi + 1, zi + 1),
                           (xi + 1, yi + 1, zi),
                           cubeSize);
        }
        // Left face
        if (GetDistance(CenterPointAtIndices(xi - 1, yi, zi, cubeSize)) > 0) {
            AddDedupedQuad((xi, yi, zi),
                           (xi, yi, zi + 1),
                           (xi, yi + 1, zi + 1),
                           (xi, yi + 1, zi),
                           cubeSize);
        }
        // Right face
        if (GetDistance(CenterPointAtIndices(xi + 1, yi, zi, cubeSize)) > 0) {
            AddDedupedQuad((xi + 1, yi, zi),
                           (xi + 1, yi + 1, zi),
                           (xi + 1, yi + 1, zi + 1),
                           (xi + 1, yi, zi + 1),
                           cubeSize);
        }
        // Front face
        if (GetDistance(CenterPointAtIndices(xi, yi, zi - 1, cubeSize)) > 0) {
            AddDedupedQuad((xi, yi, zi),
                           (xi, yi + 1, zi),
                           (xi + 1, yi + 1, zi),
                           (xi + 1, yi, zi),
                           cubeSize);
        }
        // Back face
        if (GetDistance(CenterPointAtIndices(xi, yi, zi + 1, cubeSize)) > 0) {
            AddDedupedQuad((xi, yi, zi + 1),
                           (xi + 1, yi, zi + 1),
                           (xi + 1, yi + 1, zi + 1),
                           (xi, yi + 1, zi + 1),
                           cubeSize);
        }
    }

    private void AddDedupedQuad(
            (int x, int y, int z) a, (int x, int y, int z) b,
            (int x, int y, int z) c, (int x, int y, int z) d,
            float cubeSize) {
        int ia = GetOrAddVoxelCorner(a.x, a.y, a.z, cubeSize);
        int ib = GetOrAddVoxelCorner(b.x, b.y, b.z, cubeSize);
        int ic = GetOrAddVoxelCorner(c.x, c.y, c.z, cubeSize);
        int id = GetOrAddVoxelCorner(d.x, d.y, d.z, cubeSize);
        _triangles.Add(ia); _triangles.Add(ib); _triangles.Add(ic);
        _triangles.Add(ia); _triangles.Add(ic); _triangles.Add(id);
    }

    private int GetOrAddVoxelCorner(int xi, int yi, int zi, float cubeSize) {
        long key = CornerKey(xi, yi, zi);
        if (_cornerVertexCache.TryGetValue(key, out int idx)) { return idx; }
        int newIdx = _vertices.Count;
        _cornerVertexCache[key] = newIdx;
        _vertices.Add(PointAtIndices(xi, yi, zi, cubeSize));
        return newIdx;
    }

    // Packs integer grid coords into a single long. Resolution ≤ 50 means
    // coords ≤ 51, which fits comfortably in 21 bits per axis.
    private static long CornerKey(int x, int y, int z) =>
        ((long)x << 42) | ((long)y << 21) | (long)z;

    // ─── Coordinate and mesh helpers ──────────────────────────────────────────

    protected Vector3 PointAtIndices(int x, int y, int z, float cubeSize) {
        return new Vector3(
            x * cubeSize * (_bounds.max.x - _bounds.min.x) + _bounds.min.x,
            y * cubeSize * (_bounds.max.y - _bounds.min.y) + _bounds.min.y,
            z * cubeSize * (_bounds.max.z - _bounds.min.z) + _bounds.min.z
        );
    }

    protected Vector3 CenterPointAtIndices(int x, int y, int z, float cubeSize) {
        return new Vector3(
            (x + 0.5f) * cubeSize * (_bounds.max.x - _bounds.min.x) + _bounds.min.x,
            (y + 0.5f) * cubeSize * (_bounds.max.y - _bounds.min.y) + _bounds.min.y,
            (z + 0.5f) * cubeSize * (_bounds.max.z - _bounds.min.z) + _bounds.min.z
        );
    }

    protected void ProjectVerticesToSurface() {
        for (int i = 0; i < _vertices.Count; i++) {
            for (int j = 0; j < _projectionSteps; j++) {
                var n = GetNormal(_vertices[i]);
                _vertices[i] -= n * GetDistance(_vertices[i]) * _projectionAmount;
            }
        }
    }

    private void ApplyNormals(Mesh mesh) {
        if (_getNormalsFromSDF) {
            _normals.Clear();
            for (int i = 0; i < _vertices.Count; i++) {
                _normals.Add(GetNormal(_vertices[i]));
            }
            mesh.SetNormals(_normals);
        } else {
            mesh.RecalculateNormals();
        }
    }

    // ─── SDF evaluation ───────────────────────────────────────────────────────

    protected float GetDistance(Vector3 p) {
        return _sdfScene.GetDistance(transform.TransformPoint(p));
    }

    protected Vector3 GetNormal(Vector3 p) {
        Vector3 n = new Vector3(
            GetDistance(p + new Vector3(1e-2f, 0, 0)),
            GetDistance(p + new Vector3(0, 1e-2f, 0)),
            GetDistance(p + new Vector3(0, 0, 1e-2f))
        ) - GetDistance(p) * Vector3.one;
        return n.normalized;
    }

    protected float SMinCubic(float a, float b, float k) {
        if (k <= 0) {
            return Math.Min(a, b);
        }
        float h = Math.Max(k - Math.Abs(a - b), 0.0f) / k;
        return Math.Min(a, b) - h * h * h * k * (1.0f / 6.0f);
    }
}
