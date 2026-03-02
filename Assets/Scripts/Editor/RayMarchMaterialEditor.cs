using UnityEditor;
using UnityEngine;

public class RayMarchMaterialEditor : ShaderGUI {
    public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties) {
        var backfaceMode = FindProperty("_BackfaceCullMode", properties);

        void Draw(string name) {
            var p = FindProperty(name, properties);
            editor.ShaderProperty(p, p.displayName);
        }

        void DrawLog(string name, float min, float max) {
            var p = FindProperty(name, properties);
            float clamped = Mathf.Clamp(p.floatValue, min, max);

            Rect rect = EditorGUILayout.GetControlRect();
            Rect right = EditorGUI.PrefixLabel(rect, new GUIContent(p.displayName));
            float fw = EditorGUIUtility.fieldWidth;
            Rect sliderRect = new Rect(right.x, right.y, right.width - fw - 4, right.height);
            Rect fieldRect = new Rect(right.xMax - fw, right.y, fw, right.height);

            EditorGUI.BeginChangeCheck();
            float newLog = GUI.HorizontalSlider(sliderRect, Mathf.Log10(clamped), Mathf.Log10(min), Mathf.Log10(max));
            float newVal = EditorGUI.FloatField(fieldRect, Mathf.Pow(10f, newLog));
            if (EditorGUI.EndChangeCheck())
                p.floatValue = Mathf.Clamp(newVal, min, max);
        }
        Draw("_Tint");
        EditorGUILayout.Space();

        // --- Ray March ---
        EditorGUILayout.LabelField("Ray March", EditorStyles.boldLabel);
        Draw("_MaxSteps");
        Draw("_MaxDist");
        DrawLog("_SurfDist", 1e-7f, 0.1f);
        DrawLog("_NormalDist", 1e-7f, 0.1f);
        Draw("_StepFactor");
        EditorGUILayout.Space();

        // --- Backface Culling ---
        EditorGUILayout.LabelField("Backface Culling", EditorStyles.boldLabel);
        editor.ShaderProperty(backfaceMode, backfaceMode.displayName);
        EditorGUILayout.Space();

        int modeIndex = (int)backfaceMode.floatValue;
        if (modeIndex == 1) // Alpha
        {
            Draw("_BackfaceCullMin");
            Draw("_BackfaceCullMax");
        } else if (modeIndex == 2) // Discard
          {
            Draw("_BackfaceCullThreshold");
        }

        // --- Voxel Acceleration ---
        EditorGUILayout.LabelField("Voxel Acceleration", EditorStyles.boldLabel);
        var voxelMode = FindProperty("_VoxelMode", properties);
        editor.ShaderProperty(voxelMode, voxelMode.displayName);
        if ((int)voxelMode.floatValue != 0) // not Off
        {
            Draw("_VoxelFilter");
        }
        if ((int)voxelMode.floatValue == 1) // Accel only
        {
            Draw("_MinSdfSteps");
        }
        EditorGUILayout.Space();

        // --- Temporal Warm-Start ---
        EditorGUILayout.LabelField("Temporal Warm-Start", EditorStyles.boldLabel);
        bool temporalOn = (editor.target as Material).IsKeywordEnabled("_TEMPORAL_WARMSTART_ON");
        EditorGUI.BeginChangeCheck();
        temporalOn = EditorGUILayout.Toggle("Enabled (requires TemporalWarmStartFeature)", temporalOn);
        if (EditorGUI.EndChangeCheck())
        {
            foreach (Object t in editor.targets)
            {
                Material m = t as Material;
                if (temporalOn)
                {
                    m.EnableKeyword("_TEMPORAL_WARMSTART_ON");
                }
                else
                {
                    m.DisableKeyword("_TEMPORAL_WARMSTART_ON");
                }
            }
        }
        EditorGUILayout.Space();
        editor.RenderQueueField();
    }
}
