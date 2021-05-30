using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class CloneMesh : MonoBehaviour
{
    [SerializeField] protected MeshGenerator _meshGenerator = null;
    protected MeshFilter _meshFilter;
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
    }

    protected void Regenerate()
    {
        if (_meshGenerator == null)
        {
            return;
        }
        var meshToFollow = _meshGenerator.GetComponent<MeshFilter>();
        if (meshToFollow == null)
        {
            return;
        }

        _shouldRegenerate = false;

        if (_meshFilter == null && _meshGenerator != null)
        {
            var existingMesh = GetComponent<MeshFilter>();
            DestroyImmediate(existingMesh);
            _meshFilter = gameObject.AddComponent<MeshFilter>();
        }

        if (_meshFilter.sharedMesh != meshToFollow.sharedMesh)
        {
            _meshFilter.sharedMesh = meshToFollow.sharedMesh;
        }
    }
}
