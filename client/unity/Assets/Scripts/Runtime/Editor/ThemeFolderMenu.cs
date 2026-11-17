using UnityEditor;
using UnityEngine;

/// Lets anyone point the game at a theme folder without editing FolderConfig.
public static class ThemeFolderMenu
{
    [MenuItem("Reverie/Theme Folder/Choose...")]
    static void Choose()
    {
        string start = ThemeSelection.Folder ?? Application.streamingAssetsPath;
        string folder = EditorUtility.OpenFolderPanel("Choose a theme folder", start, "");
        if (string.IsNullOrEmpty(folder)) return;
        ThemeSelection.Folder = folder;
        Debug.Log($"[Reverie] The game will play: {folder}");
    }

    [MenuItem("Reverie/Theme Folder/Use Project Default")]
    static void UseDefault()
    {
        ThemeSelection.Clear();
        Debug.Log("[Reverie] The game will play the theme set in FolderConfig.");
    }
}
