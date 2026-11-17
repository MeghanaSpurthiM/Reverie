using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/// Theme Studio: the in-player host for ThemeToolsCore (the Editor equivalent is
/// Window > Reverie > Theme Tools). Lives in the ThemeStudio scene only — the game scene
/// contains no generation code. Create the scene with Reverie > Theme Studio > Create Scene.
///
/// "Play this theme" stores the theme folder in ThemeSelection and loads the game scene, which
/// reads that selection. In a separate game build (same product name) the selection carries over.
public class ThemeToolsPanel : MonoBehaviour
{
    [Tooltip("Game scene to load for \"Play this theme\" (must be in the build's scene list).")]
    public string gameScene = "3DGS_Unity";

    [SerializeField] ThemeToolsCore core = new();

    void Awake()
    {
        // StreamingAssets/Assets are read-only in a build — write themes to persistent storage.
        if (!Application.isEditor)
            core.outputRoot = Path.Combine(Application.persistentDataPath, "Themes");
    }

    void OnDestroy() => core.Cancel();

    void OnGUI()
    {
        const float pad = 16f;
        var area = new Rect(pad, pad, Mathf.Min(640, Screen.width - 2 * pad), Screen.height - 2 * pad);
        GUILayout.BeginArea(area, "Reverie Theme Studio", GUI.skin.window);
        core.DrawGUI(DrawHostButtons);
        GUILayout.EndArea();
    }

    void DrawHostButtons()
    {
        bool haveFolder = !core.Busy && Directory.Exists(core.lastOutputFolder);
        GUILayout.BeginHorizontal();
        GUI.enabled = haveFolder;
        if (GUILayout.Button("Use this theme in the game"))
        {
            ThemeSelection.Folder = core.lastOutputFolder;
            core.Log($"Game will play {core.lastOutputFolder}");
        }
        bool canPlay = haveFolder && SceneUtility.GetBuildIndexByScenePath(gameScene) >= 0;
        GUI.enabled = canPlay;
        if (GUILayout.Button(canPlay || !haveFolder ? "Play this theme" : $"Play ({gameScene} not in build)"))
        {
            ThemeSelection.Folder = core.lastOutputFolder;
            SceneManager.LoadScene(gameScene);
        }
        GUI.enabled = ThemeSelection.Folder != null;
        if (GUILayout.Button("Use default theme"))
        {
            ThemeSelection.Clear();
            core.Log("Game will play its default theme (FolderConfig).");
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();
    }
}
