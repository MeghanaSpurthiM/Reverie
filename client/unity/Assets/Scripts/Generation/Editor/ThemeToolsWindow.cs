using System.IO;
using UnityEditor;
using UnityEngine;

/// Window > Reverie > Theme Tools
/// Prompt expansion + Reverie server bake (Audio / Visual tab) and tool/target GLBs from
/// Sketchfab (Tool / Target tab) in one window. Same panel as the Theme Studio scene
/// (ThemeToolsPanel) — logic lives in ThemeToolsCore.
public class ThemeToolsWindow : EditorWindow
{
    [SerializeField] ThemeToolsCore _core = new();

    [MenuItem("Reverie/Theme Tools (Llama prompts + Sketchfab props)", priority = 0)]
    [MenuItem("Window/Reverie/Theme Tools")]
    static void Open()
    {
        var w = GetWindow<ThemeToolsWindow>("Theme Tools");
        w.minSize = new Vector2(440, 600);
    }

    void OnEnable()  => _core.FilesWritten += OnFilesWritten;
    void OnDisable() { _core.FilesWritten -= OnFilesWritten; _core.Cancel(); }

    void Update()
    {
        if (_core.Busy || _core.Monitoring) Repaint();
    }

    static void OnFilesWritten(string folder) => AssetDatabase.Refresh();

    void OnGUI() => _core.DrawGUI(DrawHostButtons);

    void DrawHostButtons()
    {
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Show in Explorer")) EditorUtility.RevealInFinder(_core.lastOutputFolder);
        if (GUILayout.Button("Refresh Assets"))   AssetDatabase.Refresh();

        EditorGUILayout.EndHorizontal();

        bool haveFolder = Directory.Exists(_core.lastOutputFolder);
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(!haveFolder))
        {
            // This machine only (PlayerPrefs) — what you'd use while trying themes out.
            if (GUILayout.Button(new GUIContent("Use in game", "Play this theme on this machine (ThemeSelection).")))
            {
                ThemeSelection.Folder = _core.lastOutputFolder;
                _core.Log($"Game will play {_core.lastOutputFolder} (this machine).");
            }
            // Project-wide default, committed with the project (the FolderConfig asset).
            var config = AssetDatabase.LoadAssetAtPath<FolderConfig>("Assets/FolderConfig.asset");
            using (new EditorGUI.DisabledScope(config == null))
            {
                if (GUILayout.Button(new GUIContent("Make project default", "Writes Assets/FolderConfig.asset.")))
                {
                    Undo.RecordObject(config, "Set Default Theme");
                    config.folderPath = ToProjectRelative(_core.lastOutputFolder);
                    EditorUtility.SetDirty(config);
                    AssetDatabase.SaveAssets();
                    _core.Log($"FolderConfig → {config.folderPath}");
                }
            }
        }
        using (new EditorGUI.DisabledScope(ThemeSelection.Folder == null))
        {
            if (GUILayout.Button("Use default"))
            {
                ThemeSelection.Clear();
                _core.Log("Game will play the FolderConfig default theme.");
            }
        }
        EditorGUILayout.EndHorizontal();
    }

    // Keeps FolderConfig portable (see FolderConfig.Portable) when the folder is inside the project.
    static string ToProjectRelative(string abs)
    {
        string root = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath)).Replace('\\', '/') + "/";
        string norm = Path.GetFullPath(abs).Replace('\\', '/');
        return norm.StartsWith(root, System.StringComparison.OrdinalIgnoreCase) ? norm.Substring(root.Length) : abs;
    }
}
