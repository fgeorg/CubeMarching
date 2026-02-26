using UnityEngine;

public struct TransformTracker {
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
