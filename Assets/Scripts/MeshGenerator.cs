using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class MeshGenerator : MonoBehaviour
{
    [Range(1, 50)] [SerializeField] protected int _resolution = 1;
    protected bool _shouldRegenerate = true;

    protected List<Vector3> _vertices = new List<Vector3>();
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

        for (int x = 0; x < _resolution; x++)
        {
            for (int y = 0; y < _resolution; y++)
            {
                for (int z = 0; z < _resolution; z++)
                {
                    var center = new Vector3((x + 0.5f) * cubeSize, (y + 0.5f) * cubeSize, (z + 0.5f) * cubeSize);
                    AddVoxel(_vertices, _triangles, center, cubeSize);
                }
            }
        }


        _mesh.SetVertices(_vertices);
        ProjectVerticesToSurface();
        _mesh.SetTriangles(_triangles, 0);
        _mesh.RecalculateNormals();
    }

    private void ProjectVerticesToSurface()
    {
        for (int i = 0; i < _vertices.Count; i++) {

        }
    }

    // signed distance to the mesh
    protected float GetDistance(Vector3 p)
    {
        // sphere
        return (p - new Vector3(0.5f, 0.5f, 0.5f)).magnitude - 0.5f;
    }


    protected void AddVoxel(List<Vector3> vertices, List<int> triangles, Vector3 c, float cubeSize)
    {
        if (GetDistance(c) > 0)
        {
            return;
        }

        int offset = vertices.Count;
        float hs = cubeSize / 2.0f;
        //below
        if (GetDistance(c + new Vector3(0, -cubeSize, 0)) > 0)
        {
            vertices.Add(new Vector3(c.x - hs, c.y - hs, c.z - hs));
            vertices.Add(new Vector3(c.x + hs, c.y - hs, c.z - hs));
            vertices.Add(new Vector3(c.x + hs, c.y - hs, c.z + hs));
            vertices.Add(new Vector3(c.x - hs, c.y - hs, c.z + hs));
            AddQuadIndices(triangles, vertices.Count);
        }
        //above
        if (GetDistance(c + new Vector3(0, cubeSize, 0)) > 0)
        {
            vertices.Add(new Vector3(c.x - hs, c.y + hs, c.z - hs));
            vertices.Add(new Vector3(c.x - hs, c.y + hs, c.z + hs));
            vertices.Add(new Vector3(c.x + hs, c.y + hs, c.z + hs));
            vertices.Add(new Vector3(c.x + hs, c.y + hs, c.z - hs));
            AddQuadIndices(triangles, vertices.Count);
        }
        //left
        if (GetDistance(c + new Vector3(-cubeSize, 0, 0)) > 0)
        {
            vertices.Add(new Vector3(c.x - hs, c.y - hs, c.z - hs));
            vertices.Add(new Vector3(c.x - hs, c.y - hs, c.z + hs));
            vertices.Add(new Vector3(c.x - hs, c.y + hs, c.z + hs));
            vertices.Add(new Vector3(c.x - hs, c.y + hs, c.z - hs));
            AddQuadIndices(triangles, vertices.Count);
        }
        //right
        if (GetDistance(c + new Vector3(cubeSize, 0, 0)) > 0)
        {
            vertices.Add(new Vector3(c.x + hs, c.y - hs, c.z - hs));
            vertices.Add(new Vector3(c.x + hs, c.y + hs, c.z - hs));
            vertices.Add(new Vector3(c.x + hs, c.y + hs, c.z + hs));
            vertices.Add(new Vector3(c.x + hs, c.y - hs, c.z + hs));
            AddQuadIndices(triangles, vertices.Count);
        }
        //front
        if (GetDistance(c + new Vector3(0, 0, -cubeSize)) > 0)
        {
            vertices.Add(new Vector3(c.x - hs, c.y - hs, c.z - hs));
            vertices.Add(new Vector3(c.x - hs, c.y + hs, c.z - hs));
            vertices.Add(new Vector3(c.x + hs, c.y + hs, c.z - hs));
            vertices.Add(new Vector3(c.x + hs, c.y - hs, c.z - hs));
            AddQuadIndices(triangles, vertices.Count);
        }
        //back
        if (GetDistance(c + new Vector3(0, 0, cubeSize)) > 0)
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

    // smooth cube, sides share vertices and normals so it's shaded like a sphere, which looks weird 
    protected void AddCube(List<Vector3> vertices, List<int> triangles, Vector3 c1, Vector3 c2)
    {
        int offset = vertices.Count;

        vertices.Add(new Vector3(c1.x, c1.y, c1.z));
        vertices.Add(new Vector3(c2.x, c1.y, c1.z));
        vertices.Add(new Vector3(c2.x, c1.y, c2.z));
        vertices.Add(new Vector3(c1.x, c1.y, c2.z));
        vertices.Add(new Vector3(c1.x, c2.y, c1.z));
        vertices.Add(new Vector3(c2.x, c2.y, c1.z));
        vertices.Add(new Vector3(c2.x, c2.y, c2.z));
        vertices.Add(new Vector3(c1.x, c2.y, c2.z));

        triangles.Add(offset);
        triangles.Add(offset + 1);
        triangles.Add(offset + 2);
        triangles.Add(offset);
        triangles.Add(offset + 2);
        triangles.Add(offset + 3);

        triangles.Add(offset);
        triangles.Add(offset + 5);
        triangles.Add(offset + 1);
        triangles.Add(offset);
        triangles.Add(offset + 4);
        triangles.Add(offset + 5);

        triangles.Add(offset + 1);
        triangles.Add(offset + 6);
        triangles.Add(offset + 2);
        triangles.Add(offset + 1);
        triangles.Add(offset + 5);
        triangles.Add(offset + 6);

        triangles.Add(offset + 2);
        triangles.Add(offset + 7);
        triangles.Add(offset + 3);
        triangles.Add(offset + 2);
        triangles.Add(offset + 6);
        triangles.Add(offset + 7);

        triangles.Add(offset + 3);
        triangles.Add(offset + 4);
        triangles.Add(offset + 0);
        triangles.Add(offset + 3);
        triangles.Add(offset + 7);
        triangles.Add(offset + 4);

        triangles.Add(offset + 4);
        triangles.Add(offset + 6);
        triangles.Add(offset + 5);
        triangles.Add(offset + 4);
        triangles.Add(offset + 7);
        triangles.Add(offset + 6);
    }
}
