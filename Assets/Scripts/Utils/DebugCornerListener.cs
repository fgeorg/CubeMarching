using System;
using UnityEngine;

[ExecuteInEditMode]
public class DebugCornerListener : MonoBehaviour {
    public Action<bool> onActiveChanged;

    private void OnEnable() {
        onActiveChanged?.Invoke(true);
    }
    private void OnDisable() {
        onActiveChanged?.Invoke(false);
    }
}
