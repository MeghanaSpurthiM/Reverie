using UnityEngine;

/// <summary>
/// Shared config asset. Create one via:
///   Assets > Create > Scene Loader > Folder Config
/// Then assign it to both SkyboxLoader and AudioLoader.
/// </summary>
[CreateAssetMenu(menuName = "Scene Loader/Folder Config", fileName = "FolderConfig")]
public class FolderConfig : ScriptableObject
{
    [Tooltip("Absolute path to the folder containing your media files.\n" +
             "Examples:\n" +
             "  Windows : C:/MyMedia\n" +
             "  macOS   : /Users/you/MyMedia\n" +
             "  Leave empty to use StreamingAssets.")]
    public string folderPath = "";

    /// <summary>Returns the resolved folder path (falls back to StreamingAssets).</summary>
    public string ResolvedPath =>
        string.IsNullOrWhiteSpace(folderPath)
            ? Application.streamingAssetsPath
            : Portable(folderPath);

    /// Lets the project move between machines: relative paths ("Assets/Themes/forest") resolve
    /// against the project root, and a stale absolute path from another checkout
    /// (D:\Old\Project\Assets\Themes\forest) is remapped onto this project's Assets folder.
    /// "Assets/StreamingAssets/..." resolves to Application.streamingAssetsPath, so the same
    /// setting works in the Editor and in a compiled build (where StreamingAssets is copied).
    public static string Portable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        const string kStreaming = "Assets/StreamingAssets";
        string rel = path.Replace('\\', '/');
        if (!System.IO.Path.IsPathRooted(path) &&
            rel.StartsWith(kStreaming, System.StringComparison.OrdinalIgnoreCase) &&
            (rel.Length == kStreaming.Length || rel[kStreaming.Length] == '/'))
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(
                Application.streamingAssetsPath, rel.Substring(kStreaming.Length).TrimStart('/')));
        string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
        if (!System.IO.Path.IsPathRooted(path))
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, path));
        if (System.IO.Directory.Exists(path)) return path;

        string norm = path.Replace('\\', '/');
        int i = norm.LastIndexOf("/Assets/", System.StringComparison.OrdinalIgnoreCase);
        if (i < 0) return path;
        string remapped = System.IO.Path.Combine(projectRoot, norm.Substring(i + 1));
        return System.IO.Directory.Exists(remapped) ? remapped : path;
    }
}