using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class PassTransformsToShader : MonoBehaviour
{
    [SerializeField] private Transform _torus = null;
    [SerializeField] private Transform _sphere = null;
    [SerializeField] private Transform _box = null;
    private Material _mat = null;

    void Start()
    {
        _mat = GetComponent<Renderer>().sharedMaterial;
    }

    // Update is called once per frame
    void Update()
    {
        if (_mat == null)
        {
            return;
        }
        if (_torus != null)
        {
            _mat.SetMatrix("_TorusTransform", _torus.worldToLocalMatrix);
        }
        if (_sphere != null)
        {
            _mat.SetMatrix("_SphereTransform", _sphere.worldToLocalMatrix);
        }
        if (_box != null)
        {
            _mat.SetMatrix("_BoxTransform", _box.worldToLocalMatrix);
        }
    }
}
