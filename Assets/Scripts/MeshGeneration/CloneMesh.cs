using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class CloneMesh : MonoBehaviour
{
    [SerializeField] protected GameObject _otherMesh = null;
    protected MeshFilter _meshFilter;
    protected bool _shouldRegenerate = true;

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
        if (_otherMesh == null)
        {
            return;
        }
        var meshToFollow = _otherMesh.GetComponent<MeshFilter>();
        if (meshToFollow == null)
        {
            return;
        }

        _shouldRegenerate = false;

        if (_meshFilter == null && _otherMesh != null)
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
