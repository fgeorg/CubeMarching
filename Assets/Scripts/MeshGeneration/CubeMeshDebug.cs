using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class CubeMeshDebug : MonoBehaviour {

    [SerializeField, Range(0, 255)] protected byte _includedCornersBits = 0;
    [SerializeField] GameObject[] _cornerSpheres = null;

    protected List<Vector3> _vertices = new List<Vector3>();
    protected List<Vector3> _normals = new List<Vector3>();
    protected List<int> _triangles = new List<int>();
    protected List<Vector2> _barycentrics = new List<Vector2>();

    protected Mesh _mesh;

    protected bool _shouldRegenerate = true;

    public void OnEnable() {
        if (_cornerSpheres == null) return;
        for (int i = 0; i < _cornerSpheres.Length; i++) {
            if (_cornerSpheres[i] == null) continue;
            var listener = _cornerSpheres[i].GetComponent<DebugCornerListener>()
                        ?? _cornerSpheres[i].AddComponent<DebugCornerListener>();
            int index = i;
            listener.onActiveChanged = active => OnCornerActiveChanged(index, active);
        }
    }

    protected void OnDisable() {
        if (_cornerSpheres == null) return;
        foreach (var sphere in _cornerSpheres) {
            var listener = sphere != null ? sphere.GetComponent<DebugCornerListener>() : null;
            if (listener != null) listener.onActiveChanged = null;
        }
    }

    private void OnCornerActiveChanged(int index, bool active) {
        if (active) {
            _includedCornersBits |= (byte)(1 << index);
        } else {
            _includedCornersBits &= (byte)~(1 << index);
        }
        _shouldRegenerate = true;
    }

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
        // flip inside and outside because the table uses the opposite winding order from Unity
        var tris = MarchTables.triangulation[~_includedCornersBits & 255];
        foreach (var tri in tris) {
            _triangles.Add(_vertices.Count);
            _vertices.Add(MarchTables.edgePoints[tri] - 0.5f * Vector3.one);
        }

        _barycentrics.Clear();
        for (int i = 0; i < _vertices.Count; i++) {
            int pos = i % 3;
            _barycentrics.Add(pos == 0 ? new Vector2(1, 0) :
                              pos == 1 ? new Vector2(0, 1) : Vector2.zero);
        }

        _mesh.SetVertices(_vertices);
        _mesh.SetTriangles(_triangles, 0);
        _mesh.SetUVs(1, _barycentrics);
        _mesh.RecalculateNormals();

        _shouldRegenerate = false;
    }
}
