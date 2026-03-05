using UnityEditor;
using UnityEngine;

public class RayMarchMaterialEditor : ShaderGUI {
    public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties) {
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
        EditorGUI.BeginChangeCheck();
        Draw("_Tint");
        if (EditorGUI.EndChangeCheck())
        {
            ProgressiveRefinementFeature.InvalidateColorBuffers();
        }
        EditorGUILayout.Space();

        // --- Ray March ---
        EditorGUILayout.LabelField("Ray March", EditorStyles.boldLabel);
        Draw("_MaxSteps");
        Draw("_MaxDist");
        DrawLog("_NormalDist", 1e-7f, 0.1f);
        Draw("_StepFactor");
        EditorGUILayout.Space();

        // --- Dist Fade ---
        EditorGUILayout.LabelField(new GUIContent("Dist Fade  ⓘ",
            "Fades out pixels that failed to converge within the max ray march steps. " +
            "The fade range is based on the final distance reached."),
            EditorStyles.boldLabel);
        bool distFadeOn = (editor.target as Material).IsKeywordEnabled("_DISTFADEMODE_ENABLED");
        EditorGUI.BeginChangeCheck();
        distFadeOn = EditorGUILayout.Toggle("Enabled", distFadeOn);
        if (distFadeOn)
        {
            Draw("_DistFadeMin");
            Draw("_DistFadeMax");
        }
        if (EditorGUI.EndChangeCheck())
        {
            foreach (Object t in editor.targets)
            {
                Material m = t as Material;
                if (distFadeOn)
                {
                    m.EnableKeyword("_DISTFADEMODE_ENABLED");
                }
                else
                {
                    m.DisableKeyword("_DISTFADEMODE_ENABLED");
                }
            }
        }
        EditorGUILayout.Space();

        // --- Progressive Refinement ---
        EditorGUILayout.LabelField("Progressive Refinement", EditorStyles.boldLabel);
        Material mat0 = editor.target as Material;
        bool temporalOn = mat0.IsKeywordEnabled("_PROGRESSIVE_REFINEMENT_ON") || mat0.IsKeywordEnabled("_PROGRESSIVE_COLOR_ON");
        bool colorOn = mat0.IsKeywordEnabled("_PROGRESSIVE_COLOR_ON");
        EditorGUI.BeginChangeCheck();
        temporalOn = EditorGUILayout.Toggle("Enabled", temporalOn);
        if (temporalOn)
        {
            colorOn = EditorGUILayout.Toggle("Use Previous Frame's Color", colorOn);
        }
        if (EditorGUI.EndChangeCheck())
        {
            foreach (Object t in editor.targets)
            {
                Material m = t as Material;
                m.DisableKeyword("_PROGRESSIVE_REFINEMENT_ON");
                m.DisableKeyword("_PROGRESSIVE_COLOR_ON");
                if (temporalOn)
                {
                    if (colorOn)
                    {
                        m.EnableKeyword("_PROGRESSIVE_COLOR_ON");
                    }
                    else
                    {
                        m.EnableKeyword("_PROGRESSIVE_REFINEMENT_ON");
                    }
                }
            }
        }
        EditorGUILayout.Space();

        editor.RenderQueueField();
    }
}
