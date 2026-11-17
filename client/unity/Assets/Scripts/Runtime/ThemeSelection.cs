using System.IO;
using UnityEngine;

/// <summary>
/// Which theme folder the game plays. Theme Studio (or the Theme Tools window) sets it; the game
/// (FolderEnvironmentLoader) reads it, falling back to its FolderConfig asset when nothing is
/// selected or the folder no longer exists.
///
/// Stored in PlayerPrefs, so it survives restarts and is shared between a Theme Studio build and
/// a game build of this project (same company/product name). The Editor keeps its own PlayerPrefs.
/// </summary>
public static class ThemeSelection
{
    const string Key = "Reverie.SelectedThemeFolder";

    /// Absolute path of the selected theme folder, or null when none is selected / it is gone.
    public static string Folder
    {
        get
        {
            string f = PlayerPrefs.GetString(Key, "");
            return !string.IsNullOrEmpty(f) && Directory.Exists(f) ? f : null;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) PlayerPrefs.DeleteKey(Key);
            else PlayerPrefs.SetString(Key, Path.GetFullPath(value));
            PlayerPrefs.Save();
        }
    }

    /// Use the scene's FolderConfig again.
    public static void Clear() => Folder = null;

    /// The folder to play: the selection if valid, else <paramref name="config"/>, else StreamingAssets.
    public static string Resolve(FolderConfig config) =>
        Folder ?? (config != null ? config.ResolvedPath : Application.streamingAssetsPath);
}
