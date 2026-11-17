using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Reverie > Theme Studio > Create or Open Scene
/// Builds Assets/Scenes/ThemeStudio.unity: a camera and the Theme Studio panel (ThemeToolsPanel),
/// nothing from the game. Also makes sure both scenes are in the build list, Studio first.
public static class ThemeStudioSetup
{
    public const string StudioScene = "Assets/Scenes/ThemeStudio.unity";
    public const string GameScene   = "Assets/Scenes/3DGS_Unity.unity";

    [MenuItem("Reverie/Theme Studio/Create or Open Scene", priority = 0)]
    public static void CreateOrOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(StudioScene) == null)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cam.tag = "MainCamera";
            var c = cam.GetComponent<Camera>();
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = new Color(0.12f, 0.12f, 0.14f);

            var studio = new GameObject("Theme Studio");
            studio.AddComponent<ThemeToolsPanel>().gameScene = System.IO.Path.GetFileNameWithoutExtension(GameScene);

            EditorSceneManager.SaveScene(scene, StudioScene);
            Debug.Log($"[Reverie] Created {StudioScene}.");
        }
        else EditorSceneManager.OpenScene(StudioScene);

        EnsureBuildScenes();
    }

    /// Theme Studio first (so a Studio build opens on it), then the game.
    static void EnsureBuildScenes()
    {
        var list = EditorBuildSettings.scenes.Where(s => s.path != StudioScene && s.path != GameScene).ToList();
        list.Insert(0, new EditorBuildSettingsScene(StudioScene, true));
        list.Insert(1, new EditorBuildSettingsScene(GameScene, true));
        EditorBuildSettings.scenes = list.ToArray();
    }
}
