using UnityEditor;
using UnityEngine;
using Unity.Profiling;

[InitializeOnLoad]
public static class SceneViewPerformanceOverlay {
    const float UpdateInterval = 0.5f;
    const string PrefKey = "SceneViewPerfOverlay.Enabled";
    const string MenuPath = "Custom Tools/Performance Overlay";

    static bool _isEnabled = EditorPrefs.GetBool(PrefKey, true);
    public static bool IsEnabled {
        get => _isEnabled;
        set {
            _isEnabled = value;
            EditorPrefs.SetBool(PrefKey, value);
            SceneView.RepaintAll();
        }
    }

    static ProfilerRecorder gpuRecorder;
    static ProfilerRecorder cpuRecorder;

    static double lastUpdateTime;
    static double lastFrameTime;
    static float displayCpuMs;
    static float displayGpuMs;
    static float displayFps;

    static double sampleAccumCpu;
    static double sampleAccumGpu;
    static double sampleAccumFrameMs;
    static int sampleCount;

    static int marginX = 55;
    static int marginY = 10;

    static int padding = 5;
    static int lineHeight = 18;
    static int boxWidth = 170;


    static SceneViewPerformanceOverlay() {
        gpuRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time");
        cpuRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");

        SceneView.duringSceneGui += OnSceneGUI;
        EditorApplication.update += OnUpdate;
    }

    static void OnUpdate() {
        double now = EditorApplication.timeSinceStartup;
        if (lastFrameTime > 0)
            sampleAccumFrameMs += (now - lastFrameTime) * 1000.0;
        lastFrameTime = now;

        sampleAccumCpu += cpuRecorder.LastValue;
        sampleAccumGpu += gpuRecorder.LastValue;
        sampleCount++;

        if (now - lastUpdateTime >= UpdateInterval && sampleCount > 0) {
            displayCpuMs = (float)(sampleAccumCpu / sampleCount) / 1_000_000f;
            displayGpuMs = (float)(sampleAccumGpu / sampleCount) / 1_000_000f;
            float avgFrameMs = (float)(sampleAccumFrameMs / sampleCount);
            displayFps = avgFrameMs > 0 ? 1000f / avgFrameMs : 0;

            sampleAccumCpu = 0;
            sampleAccumGpu = 0;
            sampleAccumFrameMs = 0;
            sampleCount = 0;
            lastUpdateTime = now;
        }
    }

    static void OnSceneGUI(SceneView sceneView) {
        if (!IsEnabled) return;

        Handles.BeginGUI();

        int extraLines = DebugStore.Count;
        float boxHeight = 2 * padding + (3 + extraLines) * lineHeight;
        EditorGUI.DrawRect(new Rect(marginX, marginY, boxWidth, boxHeight), new Color(0, 0, 0, 0.7f));

        GUIStyle style = new GUIStyle(EditorStyles.boldLabel);
        style.normal.textColor = new Color(.6f, 1.0f, 0.6f);
        GUI.Label(new Rect(marginX + padding, marginY + padding, 155, 20), $"FPS: {displayFps:F0}", style);

        style.normal.textColor = Color.white;
        GUI.Label(new Rect(marginX + padding, marginY + padding + lineHeight, 155, 20), $"CPU: {displayCpuMs:F2} ms", style);
        GUI.Label(new Rect(marginX + padding, marginY + padding + 2 * lineHeight, 155, 20), $"GPU: {displayGpuMs:F2} ms", style);

        if (extraLines > 0) {
            float y = marginY + padding + 3 * lineHeight;
            style.normal.textColor = new Color(1f, 0.9f, 0.5f);
            var it = DebugStore.GetEnumerator();
            while (it.MoveNext()) {
                GUI.Label(new Rect(marginX + padding, y, boxWidth - 2 * padding, 20), $"{it.Current.Key}: {it.Current.Value}", style);
                y += lineHeight;
            }
            it.Dispose();
        }

        Handles.EndGUI();

        sceneView.Repaint();
    }

    [MenuItem(MenuPath)]
    static void Toggle() {
        SceneViewPerformanceOverlay.IsEnabled = !SceneViewPerformanceOverlay.IsEnabled;
    }

    [MenuItem(MenuPath, validate = true)]
    static bool ToggleValidate() {
        Menu.SetChecked(MenuPath, SceneViewPerformanceOverlay.IsEnabled);
        return true;
    }
}