using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class SdfNodeMenus
{
    // ── Primitives ────────────────────────────────────────────────────────────
    [MenuItem("GameObject/SDFs/1 - Primitives/Sphere", false, 2)]
    static void CreateSphere(MenuCommand cmd) => Create("Sphere", SdfNodeComponent.SdfNodeType.Sphere, cmd);

    [MenuItem("GameObject/SDFs/1 - Primitives/Box", false, 2)]
    static void CreateBox(MenuCommand cmd) => Create("Box", SdfNodeComponent.SdfNodeType.Box, cmd);

    [MenuItem("GameObject/SDFs/1 - Primitives/Torus", false, 2)]
    static void CreateTorus(MenuCommand cmd) => Create("Torus", SdfNodeComponent.SdfNodeType.Torus, cmd);

    // ── Binary operators ──────────────────────────────────────────────────────
    [MenuItem("GameObject/SDFs/2 - Binary Operations/Union", false, 2)]
    static void CreateUnion(MenuCommand cmd) => CreateOp("Union", SdfNodeComponent.SdfNodeType.Union, cmd);

    [MenuItem("GameObject/SDFs/2 - Binary Operations/Intersect", false, 2)]
    static void CreateIntersect(MenuCommand cmd) => CreateOp("Intersect", SdfNodeComponent.SdfNodeType.Intersect, cmd);

    [MenuItem("GameObject/SDFs/2 - Binary Operations/Subtract", false, 2)]
    static void CreateSubtract(MenuCommand cmd) => CreateOp("Subtract", SdfNodeComponent.SdfNodeType.Subtract, cmd);

    // ── Unary operators ───────────────────────────────────────────────────────
    [MenuItem("GameObject/SDFs/3 - Unary Operations/Shell", false, 2)]
    static void CreateShell(MenuCommand cmd) => CreateOp("Shell", SdfNodeComponent.SdfNodeType.Shell, cmd);

    [MenuItem("GameObject/SDFs/3 - Unary Operations/Expand", false, 2)]
    static void CreateExpand(MenuCommand cmd) => CreateOp("Expand", SdfNodeComponent.SdfNodeType.Expand, cmd);

    // ── Primitive helper ──────────────────────────────────────────────────────
    static void Create(string name, SdfNodeComponent.SdfNodeType type, MenuCommand cmd)
    {
        var go = new GameObject(name);
        GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
        go.AddComponent<SdfNodeComponent>().nodeType = type;
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        Selection.activeObject = go;
    }

    // ── Operator helper — wraps selected SDF nodes, or creates empty ──────────
    static void CreateOp(string name, SdfNodeComponent.SdfNodeType type, MenuCommand cmd)
    {
        // Unity calls this once per selected object; skip all but the first call.
        if (cmd.context is GameObject ctx && Selection.gameObjects.Length > 1
            && ctx != Selection.activeGameObject)
            return;

        // Collect selected SdfNodeComponents in hierarchy order.
        var toWrap = new List<GameObject>();
        foreach (var go in Selection.gameObjects)
        {
            if (go.GetComponent<SdfNodeComponent>() != null)
                toWrap.Add(go);
        }

        if (toWrap.Count == 0)
        {
            Create(name, type, cmd);
            return;
        }

        // Insert the new op where the first selected node currently sits.
        Transform firstTransform = toWrap[0].transform;
        Transform parent = firstTransform.parent;
        int siblingIndex = firstTransform.GetSiblingIndex();

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Wrap in " + name);
        int group = Undo.GetCurrentGroup();

        var op = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(op, "Create " + name);
        op.AddComponent<SdfNodeComponent>().nodeType = type;
        op.transform.SetParent(parent, false);
        op.transform.SetSiblingIndex(siblingIndex);

        foreach (var go in toWrap)
            Undo.SetTransformParent(go.transform, op.transform, "Reparent");

        Undo.CollapseUndoOperations(group);
        Selection.activeObject = op;
    }
}
