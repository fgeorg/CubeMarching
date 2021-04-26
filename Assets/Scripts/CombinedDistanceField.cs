using UnityEngine;

[ExecuteInEditMode]
public class CombinedDistanceField : MonoBehaviour, IDistanceField {
    [SerializeField] protected MeshGenerator _generator;
    [SerializeField] protected MonoBehaviour _cube;
    private Vector3 _oldPos;
    private Quaternion _oldRot;

    public float GetDistance(Vector3 p) {
        var cube = _cube as IDistanceField;
        return cube.GetDistance(p);
    }

    private void Update() {
        if (_cube.transform.hasChanged) {
            _generator.MarkDirty();
        }
    }
}