using System;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class MeshGenerator : MonoBehaviour {
    public enum EAlgorithm {
        CubeMarch,
        Voxels
    }

    [SerializeField] protected Bounds _bounds = new Bounds(Vector3.zero, Vector3.one);
    [SerializeField] protected EAlgorithm _algorithm = EAlgorithm.CubeMarch;
    [Range(1, 50)] [SerializeField] protected int _resolution = 1;
    [Range(0, 10)] [SerializeField] protected int _projectionSteps = 1;
    [Range(-3, 3)] [SerializeField] protected float _projectionAmount = 0;
    [SerializeField] protected bool _dedupe = false;

    [SerializeField] protected bool _getNormalsFromSDF = false;

    [SerializeField] [Range(0, 1)] protected float _cubeMarchStepsToShow = 1;

    [SerializeField] protected CombinedDistanceField _distanceField = null;

    protected bool _shouldRegenerate = true;

    protected List<Vector3> _vertices = new List<Vector3>();
    protected List<Vector3> _normals = new List<Vector3>();
    protected List<int> _triangles = new List<int>();

    protected Mesh _mesh;

    public void MarkDirty() {
        _shouldRegenerate = true;
    }

    protected void OnValidate() {
        _shouldRegenerate = true;
    }

    // Update is called once per frame
    protected void Update() {
        if (_shouldRegenerate) {
            Regenerate();
        }
    }

    protected void Regenerate() {
        _shouldRegenerate = false;
        if (_mesh == null) {
            var existingMesh = GetComponent<MeshFilter>();
            DestroyImmediate(existingMesh);
            var mf = gameObject.AddComponent<MeshFilter>();
            mf.sharedMesh = new Mesh();
            _mesh = mf.sharedMesh;
        }
        _mesh.Clear();
        _vertices.Clear();
        _triangles.Clear();

        float cubeSize = 1.0f / _resolution;

        for (int x = 0; x < _resolution; x++) {
            for (int y = 0; y < _resolution; y++) {
                for (int z = 0; z < _resolution; z++) {
                    switch (_algorithm) {
                        case EAlgorithm.CubeMarch:
                            AddCube(_vertices, _triangles, x, y, z, cubeSize);
                            break;
                        case EAlgorithm.Voxels:
                            AddVoxel(_vertices, _triangles, x, y, z, cubeSize);
                            break;
                        default:
                            break;
                    }
                    float percentDone = (float)(x * _resolution * _resolution + y * _resolution + z) / (_resolution * _resolution * _resolution);

                    if (percentDone > _cubeMarchStepsToShow) {
                        break;
                    }
                }
            }
        }
        if (_dedupe) {
            DedupeVerts();
        }
        ProjectVerticesToSurface();

        _mesh.SetVertices(_vertices);
        _mesh.SetTriangles(_triangles, 0);
        if (_getNormalsFromSDF) {
            _normals.Clear();
            for (int i = 0; i < _vertices.Count; i++) {
                _normals.Add(GetNormal(_vertices[i]));
            }
            _mesh.SetNormals(_normals);
        } else {
            _mesh.RecalculateNormals();
        }
    }

    protected Vector3 CenterPointAtIndices(int x, int y, int z, float cubeSize) {
        return new Vector3(
            (x + 0.5f) * cubeSize * (_bounds.max.x - _bounds.min.x) + _bounds.min.x,
            (y + 0.5f) * cubeSize * (_bounds.max.y - _bounds.min.y) + _bounds.min.y,
            (z + 0.5f) * cubeSize * (_bounds.max.z - _bounds.min.z) + _bounds.min.z
        );
    }

    protected Vector3 PointAtIndices(int x, int y, int z, float cubeSize) {
        return new Vector3(
            x * cubeSize * (_bounds.max.x - _bounds.min.x) + _bounds.min.x,
            y * cubeSize * (_bounds.max.y - _bounds.min.y) + _bounds.min.y,
            z * cubeSize * (_bounds.max.z - _bounds.min.z) + _bounds.min.z
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

    private void DedupeVerts() {
        var map = new Dictionary<Vector3, int>();
        var indexMap = new int[_vertices.Count];
        int index = 0;
        var newVerts = new List<Vector3>();
        for (int i = 0; i < _vertices.Count; i++) {
            if (map.ContainsKey(_vertices[i])) {
                indexMap[i] = map[_vertices[i]];
            } else {
                indexMap[i] = index;
                map[_vertices[i]] = index;
                newVerts.Add(_vertices[i]);
                index++;
            }
        }
        _vertices = newVerts;
        for (int i = 0; i < _triangles.Count; i++) {
            _triangles[i] = indexMap[_triangles[i]];
        }

    }

    protected float GetDistance(Vector3 p) {
        return _distanceField.GetDistance(transform.TransformPoint(p));
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
    protected void AddCube(List<Vector3> vertices, List<int> triangles, int xi, int yi, int zi, float cubeSize) {

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

        var tris = MarchTables.triangulation[~bits & 255]; // flip inside and outside because the table uses the opposite winding order from Unity
        foreach (var tri in tris) {
            _triangles.Add(_vertices.Count);
            var edgePoint = MarchTables.edgePoints[tri];
            _vertices.Add(new Vector3(origin.x + edgePoint.x * cubeDim.x,
                                      origin.y + edgePoint.y * cubeDim.y,
                                      origin.z + edgePoint.z * cubeDim.z));
        }
    }

    protected void AddVoxel(List<Vector3> vertices, List<int> triangles, int xi, int yi, int zi, float cubeSize) {

        Vector3 c = CenterPointAtIndices(xi, yi, zi, cubeSize);
        if (GetDistance(c) > 0) {
            return;
        }

        int offset = vertices.Count;
        var hs = (_bounds.max - _bounds.min) * cubeSize / 2.0f;
        //below
        if (GetDistance(CenterPointAtIndices(xi, yi - 1, zi, cubeSize)) > 0) {
            vertices.Add(new Vector3(c.x - hs.x, c.y - hs.y, c.z - hs.z));
            vertices.Add(new Vector3(c.x + hs.x, c.y - hs.y, c.z - hs.z));
            vertices.Add(new Vector3(c.x + hs.x, c.y - hs.y, c.z + hs.z));
            vertices.Add(new Vector3(c.x - hs.x, c.y - hs.y, c.z + hs.z));
            AddQuadIndices(triangles, vertices.Count);
        }
        //above
        if (GetDistance(CenterPointAtIndices(xi, yi + 1, zi, cubeSize)) > 0) {
            vertices.Add(new Vector3(c.x - hs.x, c.y + hs.y, c.z - hs.z));
            vertices.Add(new Vector3(c.x - hs.x, c.y + hs.y, c.z + hs.z));
            vertices.Add(new Vector3(c.x + hs.x, c.y + hs.y, c.z + hs.z));
            vertices.Add(new Vector3(c.x + hs.x, c.y + hs.y, c.z - hs.z));
            AddQuadIndices(triangles, vertices.Count);
        }
        //left
        if (GetDistance(CenterPointAtIndices(xi - 1, yi, zi, cubeSize)) > 0) {
            vertices.Add(new Vector3(c.x - hs.x, c.y - hs.y, c.z - hs.z));
            vertices.Add(new Vector3(c.x - hs.x, c.y - hs.y, c.z + hs.z));
            vertices.Add(new Vector3(c.x - hs.x, c.y + hs.y, c.z + hs.z));
            vertices.Add(new Vector3(c.x - hs.x, c.y + hs.y, c.z - hs.z));
            AddQuadIndices(triangles, vertices.Count);
        }
        //right
        if (GetDistance(CenterPointAtIndices(xi + 1, yi, zi, cubeSize)) > 0) {
            vertices.Add(new Vector3(c.x + hs.x, c.y - hs.y, c.z - hs.z));
            vertices.Add(new Vector3(c.x + hs.x, c.y + hs.y, c.z - hs.z));
            vertices.Add(new Vector3(c.x + hs.x, c.y + hs.y, c.z + hs.z));
            vertices.Add(new Vector3(c.x + hs.x, c.y - hs.y, c.z + hs.z));
            AddQuadIndices(triangles, vertices.Count);
        }
        //front
        if (GetDistance(CenterPointAtIndices(xi, yi, zi - 1, cubeSize)) > 0) {
            vertices.Add(new Vector3(c.x - hs.x, c.y - hs.y, c.z - hs.z));
            vertices.Add(new Vector3(c.x - hs.x, c.y + hs.y, c.z - hs.z));
            vertices.Add(new Vector3(c.x + hs.x, c.y + hs.y, c.z - hs.z));
            vertices.Add(new Vector3(c.x + hs.x, c.y - hs.y, c.z - hs.z));
            AddQuadIndices(triangles, vertices.Count);
        }
        //back
        if (GetDistance(CenterPointAtIndices(xi, yi, zi + 1, cubeSize)) > 0) {
            vertices.Add(new Vector3(c.x - hs.x, c.y - hs.y, c.z + hs.z));
            vertices.Add(new Vector3(c.x + hs.x, c.y - hs.y, c.z + hs.z));
            vertices.Add(new Vector3(c.x + hs.x, c.y + hs.y, c.z + hs.z));
            vertices.Add(new Vector3(c.x - hs.x, c.y + hs.y, c.z + hs.z));
            AddQuadIndices(triangles, vertices.Count);
        }

    }

    protected void AddQuadIndices(List<int> triangles, int endIndex) {
        triangles.Add(endIndex - 4);
        triangles.Add(endIndex - 3);
        triangles.Add(endIndex - 2);
        triangles.Add(endIndex - 4);
        triangles.Add(endIndex - 2);
        triangles.Add(endIndex - 1);
    }
}