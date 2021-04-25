using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class MeshGenerator : MonoBehaviour
{
    [Range(1, 50)] [SerializeField] protected int _resolution = 1;
    [Range(0, 10)] [SerializeField] protected int _projectionSteps = 1;
    [Range(-3, 3)] [SerializeField] protected float _projectionAmount = 0;
    [SerializeField] protected bool _dedupe = false;

    [SerializeField] protected bool _getNormalsFromSDF = false;
    [SerializeField] [Range(0, 5)] protected float _smoothMinFactor = 1;

    protected bool _shouldRegenerate = true;

    protected List<Vector3> _vertices = new List<Vector3>();
    protected List<Vector3> _normals = new List<Vector3>();
    protected List<int> _triangles = new List<int>();

    protected Mesh _mesh;

    protected void OnValidate()
    {
        _shouldRegenerate = true;
    }

    // Update is called once per frame
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
        if (_mesh == null)
        {
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

        for (int x = 0; x < _resolution; x++)
        {
            for (int y = 0; y < _resolution; y++)
            {
                for (int z = 0; z < _resolution; z++)
                {
                    var center = CenterPointAtIndices(x, y, z, cubeSize);
                    AddVoxel(_vertices, _triangles, x, y, z, cubeSize);
                }
            }
        }

        ProjectVerticesToSurface();
        if (_dedupe)
        {
            DedupeVerts();
        }

        _mesh.SetVertices(_vertices);
        _mesh.SetTriangles(_triangles, 0);
        if (_getNormalsFromSDF)
        {
            _normals.Clear();
            for (int i = 0; i < _vertices.Count; i++)
            {
                _normals.Add(GetNormal(_vertices[i]));
            }
            _mesh.SetNormals(_normals);
        }
        else
        {
            _mesh.RecalculateNormals();
        }
    }

    protected Vector3 CenterPointAtIndices(int x, int y, int z, float cubeSize)
    {
        return new Vector3((x + 0.5f) * cubeSize, (y + 0.5f) * cubeSize, (z + 0.5f) * cubeSize);
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

    private void DedupeVerts()
    {
        var map = new Dictionary<Vector3, int>();
        var indexMap = new int[_vertices.Count];
        int index = 0;
        var newVerts = new List<Vector3>();
        for (int i = 0; i < _vertices.Count; i++)
        {
            if (map.ContainsKey(_vertices[i]))
            {
                indexMap[i] = map[_vertices[i]];
            }
            else
            {
                indexMap[i] = index;
                map[_vertices[i]] = index;
                newVerts.Add(_vertices[i]);
                index++;
            }
        }
        _vertices = newVerts;
        for (int i = 0; i < _triangles.Count; i++)
        {
            _triangles[i] = indexMap[_triangles[i]];
        }

    }


    // signed distance to the mesh
    protected float GetDistance(Vector3 p)
    {
        // sphere
        var sphere = (p - new Vector3(0.5f, 0.5f, 0.5f)).magnitude - 0.3f;
        // torus
        var torus = new Vector2(new Vector2(p.x - 0.5f, p.y - 0.8f).magnitude - .4f, p.z - 0.5f).magnitude - .1f;
        return SMinCubic(sphere, torus, _smoothMinFactor);
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

    protected void AddVoxel(List<Vector3> vertices, List<int> triangles, int xi, int yi, int zi, float cubeSize)
    {
        Vector3 c = CenterPointAtIndices(xi, yi, zi, cubeSize);
        if (GetDistance(c) > 0)
        {
            return;
        }

        int offset = vertices.Count;
        float hs = cubeSize / 2.0f;
        //below
        if (GetDistance(CenterPointAtIndices(xi, yi - 1, zi, cubeSize)) > 0)
        {
            vertices.Add(new Vector3(c.x - hs, c.y - hs, c.z - hs));
            vertices.Add(new Vector3(c.x + hs, c.y - hs, c.z - hs));
            vertices.Add(new Vector3(c.x + hs, c.y - hs, c.z + hs));
            vertices.Add(new Vector3(c.x - hs, c.y - hs, c.z + hs));
            AddQuadIndices(triangles, vertices.Count);
        }
        //above
        if (GetDistance(CenterPointAtIndices(xi, yi + 1, zi, cubeSize)) > 0)
        {
            vertices.Add(new Vector3(c.x - hs, c.y + hs, c.z - hs));
            vertices.Add(new Vector3(c.x - hs, c.y + hs, c.z + hs));
            vertices.Add(new Vector3(c.x + hs, c.y + hs, c.z + hs));
            vertices.Add(new Vector3(c.x + hs, c.y + hs, c.z - hs));
            AddQuadIndices(triangles, vertices.Count);
        }
        //left
        if (GetDistance(CenterPointAtIndices(xi - 1, yi, zi, cubeSize)) > 0)
        {
            vertices.Add(new Vector3(c.x - hs, c.y - hs, c.z - hs));
            vertices.Add(new Vector3(c.x - hs, c.y - hs, c.z + hs));
            vertices.Add(new Vector3(c.x - hs, c.y + hs, c.z + hs));
            vertices.Add(new Vector3(c.x - hs, c.y + hs, c.z - hs));
            AddQuadIndices(triangles, vertices.Count);
        }
        //right
        if (GetDistance(CenterPointAtIndices(xi + 1, yi, zi, cubeSize)) > 0)
        {
            vertices.Add(new Vector3(c.x + hs, c.y - hs, c.z - hs));
            vertices.Add(new Vector3(c.x + hs, c.y + hs, c.z - hs));
            vertices.Add(new Vector3(c.x + hs, c.y + hs, c.z + hs));
            vertices.Add(new Vector3(c.x + hs, c.y - hs, c.z + hs));
            AddQuadIndices(triangles, vertices.Count);
        }
        //front
        if (GetDistance(CenterPointAtIndices(xi, yi, zi - 1, cubeSize)) > 0)
        {
            vertices.Add(new Vector3(c.x - hs, c.y - hs, c.z - hs));
            vertices.Add(new Vector3(c.x - hs, c.y + hs, c.z - hs));
            vertices.Add(new Vector3(c.x + hs, c.y + hs, c.z - hs));
            vertices.Add(new Vector3(c.x + hs, c.y - hs, c.z - hs));
            AddQuadIndices(triangles, vertices.Count);
        }
        //back
        if (GetDistance(CenterPointAtIndices(xi, yi, zi + 1, cubeSize)) > 0)
        {
            vertices.Add(new Vector3(c.x - hs, c.y - hs, c.z + hs));
            vertices.Add(new Vector3(c.x + hs, c.y - hs, c.z + hs));
            vertices.Add(new Vector3(c.x + hs, c.y + hs, c.z + hs));
            vertices.Add(new Vector3(c.x - hs, c.y + hs, c.z + hs));
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
