using UnityEditor;
using UnityEngine;

public class RayMarchMaterialEditor : ShaderGUI {
    public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties) {
        var marchMode = FindProperty("_MarchMode", properties);
        var backfaceMode = FindProperty("_BackfaceCullMode", properties);

        void Draw(string name) {
            var p = FindProperty(name, properties);
            editor.ShaderProperty(p, p.displayName);
        }

        void DrawLog(string name, float min, float max) {
            var p = FindProperty(name, properties);
            float clamped = Mathf.Clamp(p.floatValue, min, max);

            Rect rect  = EditorGUILayout.GetControlRect();
            Rect right = EditorGUI.PrefixLabel(rect, new GUIContent(p.displayName));
            float fw         = EditorGUIUtility.fieldWidth;
            Rect  sliderRect = new Rect(right.x,        right.y, right.width - fw - 4, right.height);
            Rect  fieldRect  = new Rect(right.xMax - fw, right.y, fw,                  right.height);

            EditorGUI.BeginChangeCheck();
            float newLog = GUI.HorizontalSlider(sliderRect, Mathf.Log10(clamped), Mathf.Log10(min), Mathf.Log10(max));
            float newVal = EditorGUI.FloatField(fieldRect, Mathf.Pow(10f, newLog));
            if (EditorGUI.EndChangeCheck())
                p.floatValue = Mathf.Clamp(newVal, min, max);
        }

        // --- Ray March ---
        EditorGUILayout.LabelField("Ray March", EditorStyles.boldLabel);
        editor.ShaderProperty(marchMode, marchMode.displayName);
        Draw("_MaxSteps");
        Draw("_MaxDist");
        DrawLog("_SurfDist",   1e-7f, 0.1f);
        DrawLog("_NormalDist", 1e-7f, 0.1f);
        Draw("_StepFactor");

        int marchModeIndex = (int)marchMode.floatValue;
        if (marchModeIndex == 1) Draw("_Omega");        // Enhanced
        else if (marchModeIndex == 2) Draw("_CoarseThresh"); // Secant
        else if (marchModeIndex == 3) Draw("_OvershootEps"); // Binary

        EditorGUILayout.Space();

        // --- SDF ---
        EditorGUILayout.LabelField("SDF", EditorStyles.boldLabel);
        Draw("_SMinKValue");

        EditorGUILayout.Space();

        // --- Backface Culling ---
        EditorGUILayout.LabelField("Backface Culling", EditorStyles.boldLabel);
        editor.ShaderProperty(backfaceMode, backfaceMode.displayName);

        int modeIndex = (int)backfaceMode.floatValue;
        if (modeIndex == 1) // Alpha
        {
            Draw("_BackfaceCullMin");
            Draw("_BackfaceCullMax");
        } else if (modeIndex == 2) // Discard
          {
            Draw("_BackfaceCullThreshold");
        }

        EditorGUILayout.Space();
        editor.RenderQueueField();
    }
}
