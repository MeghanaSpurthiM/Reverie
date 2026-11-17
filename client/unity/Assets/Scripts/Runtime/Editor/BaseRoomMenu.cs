using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Reverie → Base Room menu: adds Asset Store prefabs or empty templates as room variants under the
/// Room object, checks them, and opens the folder for room files. See docs/BASE_ROOM.md.
public static class BaseRoomMenu
{
    const string Menu = "Reverie/Base Room/";
    const string VariantPrefix = "RoomVariant_";

    [MenuItem(Menu + "Add Room Variant...", false, 0)]
    static void AddVariantFromPrefab()
    {
        var loader = FindLoader();
        if (loader == null) return;

        var prefabs = Selection.GetFiltered<GameObject>(SelectionMode.Assets)
            .Where(go => PrefabUtility.IsPartOfPrefabAsset(go)).ToList();
        if (prefabs.Count == 0)
        {
            string path = EditorUtility.OpenFilePanel("Choose a room prefab", Application.dataPath, "prefab");
            if (string.IsNullOrEmpty(path)) return;
            string assetPath = ToAssetPath(path);
            var prefab = assetPath == null ? null : AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("Add Room Variant", "Choose a .prefab inside this project's Assets folder.", "OK");
                return;
            }
            prefabs.Add(prefab);
        }

        GameObject last = null;
        foreach (var prefab in prefabs) last = CreateVariant(loader, prefab.name, prefab);
        Finish(last);
    }

    [MenuItem(Menu + "Add Empty Room Variant", false, 1)]
    static void AddEmptyVariant()
    {
        var loader = FindLoader();
        if (loader != null) Finish(CreateVariant(loader, "New", null));
    }

    [MenuItem(Menu + "Show Only Selected Room", false, 20)]
    static void ShowOnlySelected()
    {
        var loader = FindLoader();
        var selected = Selection.activeGameObject;
        Transform room = selected == null ? null : RoomChildOf(loader, selected.transform);
        if (room == null)
        {
            EditorUtility.DisplayDialog("Show Only Selected Room", "Select a room variant (a child of Room) first.", "OK");
            return;
        }
        foreach (Transform child in loader.transform)
        {
            Undo.RecordObject(child.gameObject, "Show Only Selected Room");
            child.gameObject.SetActive(child == room);
        }
        EditorSceneManager.MarkSceneDirty(loader.gameObject.scene);
    }

    [MenuItem(Menu + "Validate Rooms", false, 21)]
    static void Validate()
    {
        var loader = FindLoader();
        if (loader == null) return;

        int problems = 0, rooms = 0;
        foreach (Transform child in loader.transform)
        {
            if (child.gameObject == loader.placeholder) continue;
            rooms++;
            List<string> issues = BaseRoomChecks.Inspect(child.gameObject);
            problems += issues.Count;
            foreach (string issue in issues) Debug.LogWarning($"[BaseRoom] {child.name}: {issue}", child);
            if (issues.Count == 0) Debug.Log($"[BaseRoom] {child.name}: no problems found.", child);
        }

        var files = BaseRoomFiles.List(Path.Combine(Application.streamingAssetsPath, BaseRoomLoader.RoomFolder));
        foreach (string file in files) Debug.Log($"[BaseRoom] Room file: {Path.GetFileName(file)}");

        EditorUtility.DisplayDialog("Validate Rooms",
            $"{rooms} room variant(s) and {files.Count} room file(s).\n" +
            (problems == 0 ? "No problems found." : $"{problems} problem(s); see the Console."), "OK");
    }

    [MenuItem(Menu + "Open Room Files Folder", false, 40)]
    static void OpenFolder()
    {
        string folder = Path.Combine(Application.streamingAssetsPath, BaseRoomLoader.RoomFolder);
        Directory.CreateDirectory(folder);
        EditorUtility.RevealInFinder(Path.Combine(folder, "README.md"));
    }

    static GameObject CreateVariant(BaseRoomLoader loader, string name, GameObject prefab)
    {
        string unique = GameObjectUtility.GetUniqueNameForSibling(loader.transform, VariantPrefix + Clean(name));
        var root = new GameObject(unique, typeof(BaseRoomVariant));
        Undo.RegisterCreatedObjectUndo(root, "Add Room Variant");
        root.transform.SetParent(loader.transform, false);
        root.transform.localPosition = loader.floorOrigin;   // centre of the play area, on the floor

        var geometry = Child(root, "Geometry");
        Child(root, "Colliders");
        Child(root, "Lights");

        if (prefab != null)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, loader.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(instance, "Add Room Variant");
            instance.transform.SetParent(geometry.transform, false);
            foreach (string issue in BaseRoomChecks.Inspect(root))
                Debug.LogWarning($"[BaseRoom] {unique}: {issue}", root);
        }
        return root;
    }

    static GameObject Child(GameObject parent, string name)
    {
        var child = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(child, "Add Room Variant");
        child.transform.SetParent(parent.transform, false);
        return child;
    }

    static void Finish(GameObject variant)
    {
        if (variant == null) return;
        Selection.activeGameObject = variant;
        EditorGUIUtility.PingObject(variant);
        EditorSceneManager.MarkSceneDirty(variant.scene);
        Debug.Log($"[BaseRoom] Added {variant.name}. Line up its floor with the placeholder's floor, then save the scene. " +
                  "Use Reverie → Base Room → Show Only Selected Room to hide the others while you work.", variant);
    }

    static BaseRoomLoader FindLoader()
    {
        var loader = Object.FindAnyObjectByType<BaseRoomLoader>(FindObjectsInactive.Include);
        if (loader == null)
            EditorUtility.DisplayDialog("Base Room",
                "Open the game scene (Assets/Scenes/3DGS_Unity.unity) first. It has the Room object with BaseRoomLoader.", "OK");
        return loader;
    }

    static Transform RoomChildOf(BaseRoomLoader loader, Transform t)
    {
        if (loader == null) return null;
        while (t != null && t.parent != loader.transform) t = t.parent;
        return t;
    }

    static string Clean(string name) =>
        new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');

    static string ToAssetPath(string fullPath)
    {
        string full = Path.GetFullPath(fullPath).Replace('\\', '/');
        string data = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
        return full.StartsWith(data, System.StringComparison.OrdinalIgnoreCase) ? "Assets" + full.Substring(data.Length) : null;
    }
}
