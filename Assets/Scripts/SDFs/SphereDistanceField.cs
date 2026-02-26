using UnityEngine;

public class SphereDistanceField : MonoBehaviour, IDistanceField {
    public float GetDistance(Vector3 p) {
        p = transform.InverseTransformPoint(p);
        return p.magnitude - 0.5f;
    }
}