#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// Editor-only in-memory KV store for debug values displayed in the Scene View overlay.
/// Cleared automatically on scene change or domain reload (recompile / play mode enter).
/// Never touches disk. Usage from any script:
///   DebugStore.Set("verts", someCount);
///   DebugStore.Remove("verts");
/// </summary>
[InitializeOnLoad]
public static class DebugStore
{
    static readonly Dictionary<string, string> _entries = new Dictionary<string, string>();

    static DebugStore()
    {
        EditorSceneManager.activeSceneChangedInEditMode += (_, __) => Clear();
    }

    public static void Set(string key, string value)   => _entries[key] = value;
    public static void Set(string key, object value)   => _entries[key] = value != null ? value.ToString() : "null";
    public static void Remove(string key)              => _entries.Remove(key);
    public static void Clear()                         => _entries.Clear();
    public static int Count                            => _entries.Count;

    // Returns the live enumerator — only read on the main thread (editor GUI callbacks).
    public static Dictionary<string, string>.Enumerator GetEnumerator() => _entries.GetEnumerator();
}

#else

// No-op stubs for non-editor builds — calls compile away to nothing.
public static class DebugStore
{
    public static void Set(string key, string value) { }
    public static void Set(string key, object value) { }
    public static void Remove(string key)            { }
    public static void Clear()                       { }
}

#endif
