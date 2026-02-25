using UnityEngine;

[ExecuteInEditMode]
public class CombinedDistanceField : MonoBehaviour, IDistanceField {
    private struct TransformTracker {
        private readonly Transform _transform;
        private Matrix4x4 _lastMatrix;

        public TransformTracker(Transform t) {
            _transform = t;
            _lastMatrix = t.localToWorldMatrix;
        }

        public bool HasChanged() {
            var m = _transform.localToWorldMatrix;
            if (m == _lastMatrix) return false;
            _lastMatrix = m;
            return true;
        }
    }

    [SerializeField] [Range(0, 5)] protected float _smoothMinFactor = 1;
    [SerializeField] protected MeshGenerator _generator;
    [SerializeField] protected CubeDistanceField _cube;
    [SerializeField] protected SphereDistanceField _sphere;
    [SerializeField] protected TorusDistanceField _torus;

    private TransformTracker[] _trackers;

    private void OnEnable() {
        _trackers = new TransformTracker[] {
            new(_cube.transform),
            new(_sphere.transform),
            new(_torus.transform),
        };
    }

    public float GetDistance(Vector3 p) {
        return SMinCubic(SMinCubic(_cube.GetDistance(p), _sphere.GetDistance(p), _smoothMinFactor), _torus.GetDistance(p), _smoothMinFactor);
    }

    private void Update() {
        bool dirty = false;
        for (int i = 0; i < _trackers.Length; i++)
            dirty |= _trackers[i].HasChanged();
        if (dirty) _generator.MarkDirty();
    }

    protected float SMinCubic(float a, float b, float k) {
        if (k <= 0) {
            return Mathf.Min(a, b);
        }
        float h = Mathf.Max(k - Mathf.Abs(a - b), 0.0f) / k;
        return Mathf.Min(a, b) - h * h * h * k * (1.0f / 6.0f);
    }
}