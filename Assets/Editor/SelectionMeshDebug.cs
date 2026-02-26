using UnityEditor;
using UnityEngine;

/// <summary>
/// Tracks the Scene View selection and writes vertex/triangle counts
/// to DebugStore whenever the selected object changes.
/// </summary>
[InitializeOnLoad]
public static class SelectionMeshDebug
{
    const string SelectedKey = "Selected";

    static SelectionMeshDebug()
    {
        Selection.selectionChanged += OnSelectionChanged;
    }

    static void OnSelectionChanged()
    {
        Mesh mesh = GetSelectedMesh();
        if (mesh == null)
        {
            DebugStore.Remove(SelectedKey);
            return;
        }

        DebugStore.Set(SelectedKey, $"{mesh.vertexCount} v   {mesh.triangles.Length / 3} t");
    }

    static Mesh GetSelectedMesh()
    {
        GameObject go = Selection.activeGameObject;
        if (go == null) return null;

        MeshFilter mf = go.GetComponent<MeshFilter>();
        if (mf != null) return mf.sharedMesh;

        SkinnedMeshRenderer smr = go.GetComponent<SkinnedMeshRenderer>();
        if (smr != null) return smr.sharedMesh;

        return null;
    }
}
