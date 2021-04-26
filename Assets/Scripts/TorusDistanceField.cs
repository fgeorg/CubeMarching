using UnityEngine;

public class TorusDistanceField : MonoBehaviour, IDistanceField {
    public float GetDistance(Vector3 p) {
        p = transform.InverseTransformPoint(p);
        return new Vector2(new Vector2(p.x, p.y).magnitude - 0.6f, p.z).magnitude - .2f;
    }
}