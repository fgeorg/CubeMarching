using UnityEditor;
using UnityEngine;

public static class SdfNodeMenus
{
    // ── Primitives ────────────────────────────────────────────────────────────
    [MenuItem("GameObject/SDFs/Sphere", false, 2)]
    static void CreateSphere(MenuCommand cmd) => Create("Sphere", SdfNodeComponent.SdfNodeType.Sphere, cmd);

    [MenuItem("GameObject/SDFs/Box", false, 2)]
    static void CreateBox(MenuCommand cmd) => Create("Box", SdfNodeComponent.SdfNodeType.Box, cmd);

    [MenuItem("GameObject/SDFs/Torus", false, 2)]
    static void CreateTorus(MenuCommand cmd) => Create("Torus", SdfNodeComponent.SdfNodeType.Torus, cmd);

    // ── Operators ─────────────────────────────────────────────────────────────
    [MenuItem("GameObject/SDFs/Operations/Union", false, 2)]
    static void CreateUnion(MenuCommand cmd) => Create("Union", SdfNodeComponent.SdfNodeType.Union, cmd);

    [MenuItem("GameObject/SDFs/Operations/SmoothUnion", false, 2)]
    static void CreateSmoothUnion(MenuCommand cmd) => Create("SmoothUnion", SdfNodeComponent.SdfNodeType.SmoothUnion, cmd);

    [MenuItem("GameObject/SDFs/Operations/Intersect", false, 2)]
    static void CreateIntersect(MenuCommand cmd) => Create("Intersect", SdfNodeComponent.SdfNodeType.Intersect, cmd);

    [MenuItem("GameObject/SDFs/Operations/Subtract", false, 2)]
    static void CreateSubtract(MenuCommand cmd) => Create("Subtract", SdfNodeComponent.SdfNodeType.Subtract, cmd);

    // ── Shared helper ─────────────────────────────────────────────────────────
    static void Create(string name, SdfNodeComponent.SdfNodeType type, MenuCommand cmd)
    {
        var go = new GameObject(name);
        GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
        go.AddComponent<SdfNodeComponent>().nodeType = type;
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        Selection.activeObject = go;
    }
}
