using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class MeshGenerator : MonoBehaviour
{
    [Range(1, 50)] [SerializeField] protected int _resolution = 1;
    protected bool _shouldRegenerate = true;

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
        //Debug.DrawLine(new Vector3(0,0,0), new Vector3(1,0,0), Color.blue);
    }

    protected void Regenerate()
    {
        _shouldRegenerate = false;
        var mesh = GetComponent<MeshFilter>().mesh;
        mesh.Clear();
        var vertices = new List<Vector3>();
        var triangles = new List<int>();

        float cubeSize = 1.0f / _resolution;

        for (int x = 0; x < _resolution; x++)
        {
            for (int y = 0; y < _resolution; y++)
            {
                for (int z = 0; z < _resolution; z++)
                {
                    var center = new Vector3((x + 0.5f) * cubeSize, (y + 0.5f) * cubeSize, (z + 0.5f) * cubeSize);
                    //AddVoxel(vertices, triangles, center, cubeSize);
                    if (GetDistance(center) < 0)
                    {
                        AddCube(vertices, triangles, new Vector3(x * cubeSize, y * cubeSize, z * cubeSize), new Vector3((x + 1) * cubeSize, (y + 1) * cubeSize, (z + 1) * cubeSize));
                    }
                }
            }
        }


        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
    }

    // signed distance to the mesh
    protected float GetDistance(Vector3 p)
    {
        // sphere
        return (p - new Vector3(0.5f, 0.5f, 0.5f)).magnitude - 0.5f;
    }


    protected void AddVoxel(List<Vector3> vertices, List<int> triangles, Vector3 c1, float cubeSize)
    {

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
