using UnityEngine;

public class CubeDistanceField : MonoBehaviour, IDistanceField {
    [SerializeField] [Range(0, 0.5f)] float _cornerRadius = 0;
    public float GetDistance(Vector3 p) {
        p = transform.InverseTransformPoint(p);
        Vector3 b = (Vector3.one * 0.5f) * (1 - 2 * _cornerRadius);
        var q = p.Abs() - b;
        var box = (Vector3.Max(q, Vector3.zero)).magnitude + Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0.0f);
        return box - _cornerRadius;
    }
}