using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(SdfNodeComponent))]
public class SdfNodeComponentEditor : Editor {
    static readonly string[] s_TypeNames;
    static readonly int[] s_TypeValues;

    static SdfNodeComponentEditor() {
        var allTypes = (SdfNodeType[])System.Enum.GetValues(typeof(SdfNodeType));
        var editableTypes = System.Array.FindAll(allTypes, t => !t.ToString().StartsWith("Smooth"));
        s_TypeNames  = System.Array.ConvertAll(editableTypes, t => t.ToString());
        s_TypeValues = System.Array.ConvertAll(editableTypes, t => (int)t);
    }

    public override void OnInspectorGUI() {
        serializedObject.Update();

        var typeProp = serializedObject.FindProperty("nodeType");
        EditorGUI.BeginChangeCheck();
        typeProp.intValue = EditorGUILayout.IntPopup("Type", typeProp.intValue, s_TypeNames, s_TypeValues);
        bool typeChanged = EditorGUI.EndChangeCheck();

        if (typeChanged) {
            serializedObject.ApplyModifiedProperties();
            var node = (SdfNodeComponent)target;
            if (node.GetComponent<SdfScene>() == null) {
                Undo.RecordObject(node.gameObject, "Rename SDF Node");
                node.gameObject.name = NodeTypeName(node.nodeType);
            }
        }

        var current = (SdfNodeComponent)target;
        switch (current.nodeType) {
            case SdfNodeType.Sphere:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("sphereRadius"), new GUIContent("Radius"));
                break;
            case SdfNodeType.Box:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("boxHalfExtents"), new GUIContent("Half Extents"));
                break;
            case SdfNodeType.Torus:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("torusMajorRadius"), new GUIContent("Major Radius"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("torusMinorRadius"), new GUIContent("Minor Radius"));
                break;
            case SdfNodeType.Union:
            case SdfNodeType.Intersect:
            case SdfNodeType.Subtract:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("smoothK"), new GUIContent("Smooth K"));
                break;
            case SdfNodeType.Shell:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("shellThickness"), new GUIContent("Thickness"));
                break;
            case SdfNodeType.Expand:
                EditorGUILayout.PropertyField(serializedObject.FindProperty("expandAmount"), new GUIContent("Amount"));
                break;
        }

        if ((int)current.nodeType < SdfNodeTypeRanges.PrimitivesEnd) {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("color"), new GUIContent("Color"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("metallic"), new GUIContent("Metallic"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("smoothness"), new GUIContent("Smoothness"));
        }

        serializedObject.ApplyModifiedProperties();
    }

    static string NodeTypeName(SdfNodeType type) {
        return type.ToString();
    }
}
