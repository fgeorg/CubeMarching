using UnityEngine;

[ExecuteInEditMode]
public class CombinedDistanceField : MonoBehaviour, IDistanceField {
    [SerializeField] [Range(0, 5)] protected float _smoothMinFactor = 1;
    [SerializeField] protected MeshGenerator _generator;
    [SerializeField] protected CubeDistanceField _cube;
    [SerializeField] protected SphereDistanceField _sphere;
    [SerializeField] protected TorusDistanceField _torus;
    private Vector3 _oldPos;
    private Quaternion _oldRot;

    public float GetDistance(Vector3 p) {
        return SMinCubic(SMinCubic(_cube.GetDistance(p), _sphere.GetDistance(p), _smoothMinFactor), _torus.GetDistance(p), _smoothMinFactor);
    }

    private void Update() {
        if (_cube.transform.hasChanged) {
            _generator.MarkDirty();
        }
    }

    protected float SMinCubic(float a, float b, float k) {
        if (k <= 0) {
            return Mathf.Min(a, b);
        }
        float h = Mathf.Max(k - Mathf.Abs(a - b), 0.0f) / k;
        return Mathf.Min(a, b) - h * h * h * k * (1.0f / 6.0f);
    }
}