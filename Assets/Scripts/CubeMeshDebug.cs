using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class CubeMeshDebug : MonoBehaviour {

    [SerializeField] protected int _includedCornersBits = 0;
    [SerializeField] GameObject[] _cornerSpheres = null;

    protected List<Vector3> _vertices = new List<Vector3>();
    protected List<Vector3> _normals = new List<Vector3>();
    protected List<int> _triangles = new List<int>();

    protected Mesh _mesh;

    protected bool _shouldRegenerate = true;

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

        for (int i = 0; i < _cornerSpheres?.Length; i++) {
            Debug.Log($"1 << i ({i}) = {1 << i}");
            _cornerSpheres[i].SetActive((_includedCornersBits & (1 << i)) != 0);
        }

        var tris = MarchTables.triangulation[~_includedCornersBits & 255]; // flip inside and outside because the table uses the opposite winding order from Unity
        foreach (var tri in tris) {
            _triangles.Add(_vertices.Count);
            _vertices.Add(MarchTables.edgePoints[tri] - 0.5f * Vector3.one);
        }

        _mesh.SetVertices(_vertices);
        _mesh.SetTriangles(_triangles, 0);
        _mesh.RecalculateNormals();
    }
}
