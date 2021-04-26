using UnityEngine;

public class CubeDistanceField : MonoBehaviour, IDistanceField {
    public float GetDistance(Vector3 p) {
        p = transform.InverseTransformPoint(p);
        //return p.magnitude - 0.5f;
        Vector3 b = Vector3.one * 0.5f;
        var q = p.Abs() - b;
        return (Vector3.Max(q, Vector3.zero)).magnitude + Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0.0f);
    }
}