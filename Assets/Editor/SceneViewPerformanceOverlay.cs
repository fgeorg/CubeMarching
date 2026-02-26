using UnityEditor;
using UnityEngine;
using Unity.Profiling;

[InitializeOnLoad]
public static class SceneViewPerformanceOverlay
{
    const float UpdateInterval = 0.5f;

    static ProfilerRecorder gpuRecorder;
    static ProfilerRecorder cpuRecorder;

    static double lastUpdateTime;
    static float displayCpuMs;
    static float displayGpuMs;
    static float displayFps;

    static double sampleAccumCpu;
    static double sampleAccumGpu;
    static int sampleCount;

    static SceneViewPerformanceOverlay()
    {
        gpuRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time");
        cpuRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");

        SceneView.duringSceneGui += OnSceneGUI;
    }

    static void OnSceneGUI(SceneView sceneView)
    {
        // Accumulate samples every repaint
        sampleAccumCpu += cpuRecorder.LastValue;
        sampleAccumGpu += gpuRecorder.LastValue;
        sampleCount++;

        double now = EditorApplication.timeSinceStartup;
        if (now - lastUpdateTime >= UpdateInterval && sampleCount > 0)
        {
            displayCpuMs = (float)(sampleAccumCpu / sampleCount) / 1_000_000f;
            displayGpuMs = (float)(sampleAccumGpu / sampleCount) / 1_000_000f;
            float maxMs = Mathf.Max(displayCpuMs, displayGpuMs);
            displayFps = maxMs > 0 ? 1000f / maxMs : 0;

            sampleAccumCpu = 0;
            sampleAccumGpu = 0;
            sampleCount = 0;
            lastUpdateTime = now;
        }

        Handles.BeginGUI();

        int extraLines = DebugStore.Count;
        float boxHeight = 70f + (extraLines > 0 ? extraLines * 20f : 0f);
        EditorGUI.DrawRect(new Rect(50, 10, 165, boxHeight), new Color(0, 0, 0, 0.7f));

        GUIStyle style = new GUIStyle(EditorStyles.boldLabel);
        style.normal.textColor = Color.green;
        GUI.Label(new Rect(55, 15, 155, 20), $"FPS: {displayFps:F0}", style);

        style.normal.textColor = Color.white;
        GUI.Label(new Rect(55, 35, 155, 20), $"CPU: {displayCpuMs:F2} ms", style);
        GUI.Label(new Rect(55, 55, 155, 20), $"GPU: {displayGpuMs:F2} ms", style);

        if (extraLines > 0)
        {
            float y = 75f;
            style.normal.textColor = new Color(1f, 0.85f, 0.3f); // warm yellow
            var it = DebugStore.GetEnumerator();
            while (it.MoveNext())
            {
                GUI.Label(new Rect(55, y, 155, 20), $"{it.Current.Key}: {it.Current.Value}", style);
                y += 20f;
            }
            it.Dispose();
        }

        Handles.EndGUI();

        sceneView.Repaint();
    }
}
