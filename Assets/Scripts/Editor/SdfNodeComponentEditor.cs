using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(SdfNodeComponent))]
public class SdfNodeComponentEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var typeProp = serializedObject.FindProperty("nodeType");
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(typeProp, new GUIContent("Type"));
        bool typeChanged = EditorGUI.EndChangeCheck();

        if (typeChanged)
        {
            serializedObject.ApplyModifiedProperties();
            var node = (SdfNodeComponent)target;
            if (node.GetComponent<SdfScene>() == null)
            {
                Undo.RecordObject(node.gameObject, "Rename SDF Node");
                node.gameObject.name = NodeTypeName(node.nodeType);
            }
        }

        var current = (SdfNodeComponent)target;
        switch (current.nodeType)
        {
            case SdfNodeComponent.SdfNodeType.Sphere:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("sphereRadius"), new GUIContent("Radius"));
                break;
            case SdfNodeComponent.SdfNodeType.Box:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("boxHalfExtents"), new GUIContent("Half Extents"));
                break;
            case SdfNodeComponent.SdfNodeType.Torus:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("torusMajorRadius"), new GUIContent("Major Radius"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("torusMinorRadius"), new GUIContent("Minor Radius"));
                break;
            case SdfNodeComponent.SdfNodeType.Union:
            case SdfNodeComponent.SdfNodeType.Intersect:
            case SdfNodeComponent.SdfNodeType.Subtract:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("smoothK"), new GUIContent("Smooth K"));
                break;
            case SdfNodeComponent.SdfNodeType.Shell:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("shellThickness"), new GUIContent("Thickness"));
                break;
            case SdfNodeComponent.SdfNodeType.Expand:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("expandAmount"), new GUIContent("Amount"));
                break;
        }

        serializedObject.ApplyModifiedProperties();
    }

    static string NodeTypeName(SdfNodeComponent.SdfNodeType type) {
        return type.ToString();
    }
}
