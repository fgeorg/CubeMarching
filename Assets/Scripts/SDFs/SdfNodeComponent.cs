using UnityEngine;

[ExecuteInEditMode]
public class SdfNodeComponent : MonoBehaviour
{
    public SdfNodeType nodeType = SdfNodeType.Union;
    public Color color = Color.white;
    [Range(0f, 1f)]
    public float metallic = 0f;
    [Range(0f, 1f)]
    public float smoothness = 0.5f;

    private void OnEnable()  => GetComponentInParent<SdfScene>()?.MarkDirty();
    private void OnDisable() => GetComponentInParent<SdfScene>()?.MarkDirty();
    private void OnTransformChildrenChanged() => GetComponentInParent<SdfScene>()?.MarkDirty();
    private void OnValidate() => GetComponentInParent<SdfScene>()?.MarkDirty();
    public float sphereRadius = 0.5f;
    public Vector3 boxHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);
    public float torusMajorRadius = 0.4f;
    public float torusMinorRadius = 0.15f;
    [Range(0f, 1f)]
    public float smoothK = 0.0f;
    [Range(1e-5f, 0.2f)]
    public float shellThickness = 0.05f;
    [Range(-0.1f, 0.1f)]
    public float expandAmount = 0.01f;
}
