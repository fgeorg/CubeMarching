using UnityEngine;

[ExecuteInEditMode]
public class SdfNodeComponent : MonoBehaviour
{
    public enum SdfNodeType
    {
        Sphere    = 0,
        Box       = 1,
        Torus     = 2,
        Union     = 10,
        SmoothUnion = 11,
        Intersect = 12,
        Subtract  = 13,
    }

    public SdfNodeType nodeType = SdfNodeType.Sphere;
    public float sphereRadius      = 0.5f;
    public float boxCornerRadius   = 0f;
    public float torusMajorRadius  = 0.4f;
    public float torusMinorRadius  = 0.15f;
    public float smoothK           = 0.3f;
}
