using UnityEditor;
using UnityEngine;

public class RayMarchMinecraftMaterialEditor : ShaderGUI {
    public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties) {
        void Draw(string name) {
            var p = FindProperty(name, properties);
            editor.ShaderProperty(p, p.displayName);
        }

        Draw("_Tint");
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Ray March", EditorStyles.boldLabel);
        Draw("_MaxSteps");
        Draw("_MaxDist");
        EditorGUILayout.Space();

        editor.RenderQueueField();
    }
}
