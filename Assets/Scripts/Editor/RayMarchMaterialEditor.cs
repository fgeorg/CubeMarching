using UnityEditor;
using UnityEngine;

public class RayMarchMaterialEditor : ShaderGUI {
    enum BackfaceCullMode { Off = 0, Alpha = 1 }
    enum MinDistFadeMode  { Off = 0, Enabled = 1 }
    enum VoxelMode        { Off = 0, AccelOnly = 1, Full = 2 }

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
        DrawLog("_NormalDist", 1e-7f, 0.1f);
        Draw("_StepFactor");
        EditorGUILayout.Space();

        // --- Backface Culling ---
        EditorGUILayout.LabelField("Backface Culling", EditorStyles.boldLabel);
        editor.ShaderProperty(backfaceMode, backfaceMode.displayName);
        EditorGUILayout.Space();

        var backfaceCullMode = (BackfaceCullMode)(int)backfaceMode.floatValue;
        if (backfaceCullMode == BackfaceCullMode.Alpha)
        {
            Draw("_BackfaceCullMin");
            Draw("_BackfaceCullMax");
        }
EditorGUILayout.Space();

        // --- Dist Fade ---
        EditorGUILayout.LabelField(new GUIContent("Dist Fade  ⓘ",
            "Fades out pixels that failed to converge within the max ray march steps. " +
            "The fade range is based on the final distance reached."),
            EditorStyles.boldLabel);
        var distFadeProp = FindProperty("_MinDistFadeMode", properties);
        editor.ShaderProperty(distFadeProp, distFadeProp.displayName);
        if ((MinDistFadeMode)(int)distFadeProp.floatValue == MinDistFadeMode.Enabled)
        {
            Draw("_DistFadeMin");
            Draw("_DistFadeMax");
        }
        EditorGUILayout.Space();

        // --- Voxel Acceleration ---
        EditorGUILayout.LabelField("Voxel Acceleration", EditorStyles.boldLabel);
        var voxelModeProp = FindProperty("_VoxelMode", properties);
        editor.ShaderProperty(voxelModeProp, voxelModeProp.displayName);
        var voxelModeVal = (VoxelMode)(int)voxelModeProp.floatValue;
        if (voxelModeVal != VoxelMode.Off)
        {
            Draw("_VoxelFilter");
        }
        if (voxelModeVal == VoxelMode.AccelOnly)
        {
            Draw("_MinSdfSteps");
        }
        EditorGUILayout.Space();

        // --- Progressive Refinement ---
        EditorGUILayout.LabelField("Progressive Refinement", EditorStyles.boldLabel);
        bool temporalOn = (editor.target as Material).IsKeywordEnabled("_PROGRESSIVE_REFINEMENT_ON");
        EditorGUI.BeginChangeCheck();
        temporalOn = EditorGUILayout.Toggle("Enabled (requires ProgressiveRefinementFeature)", temporalOn);
        Draw("_TemporalDebug");
        if (EditorGUI.EndChangeCheck())
        {
            foreach (Object t in editor.targets)
            {
                Material m = t as Material;
                if (temporalOn)
                {
                    m.EnableKeyword("_PROGRESSIVE_REFINEMENT_ON");
                }
                else
                {
                    m.DisableKeyword("_PROGRESSIVE_REFINEMENT_ON");
                }
            }
        }
        EditorGUILayout.Space();

        // --- Progressive Color ---
        EditorGUILayout.LabelField("Progressive Color", EditorStyles.boldLabel);
        bool colorOn = (editor.target as Material).IsKeywordEnabled("_PROGRESSIVE_COLOR_ON");
        EditorGUI.BeginChangeCheck();
        colorOn = EditorGUILayout.Toggle("Enabled", colorOn);
        if (EditorGUI.EndChangeCheck())
        {
            foreach (Object t in editor.targets)
            {
                Material m = t as Material;
                if (colorOn)
                {
                    m.EnableKeyword("_PROGRESSIVE_COLOR_ON");
                }
                else
                {
                    m.DisableKeyword("_PROGRESSIVE_COLOR_ON");
                }
            }
        }
        EditorGUILayout.Space();

        editor.RenderQueueField();
    }
}
