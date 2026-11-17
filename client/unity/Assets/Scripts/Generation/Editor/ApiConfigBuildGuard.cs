using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// Keeps API keys out of builds. Unity copies StreamingAssets into every player, so just before
/// a build api_config.json is replaced by a copy with all keys/tokens blanked (URLs, endpoints
/// and model names stay), and the original is put back right after the build — whether it
/// succeeded, failed or the Editor crashed (backup in Library/, restored on next load).
/// Players get their keys from persistentDataPath/api_config.json (see ApiConfig).
[InitializeOnLoad]
public class ApiConfigBuildGuard : IPreprocessBuildWithReport, IPostprocessBuildWithReport
{
    public int callbackOrder => 0;

    static string Original => Path.Combine(Application.streamingAssetsPath, ApiConfig.FileName);
    static string Backup   => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "ReverieBuild", ApiConfig.FileName));

    static ApiConfigBuildGuard() => EditorApplication.delayCall += Restore;   // after a crash mid-build

    public void OnPreprocessBuild(BuildReport report)
    {
        Restore();                                   // never stack two swaps
        if (!File.Exists(Original)) return;

        JObject stripped;
        try { stripped = ApiConfig.StripSecrets(JObject.Parse(File.ReadAllText(Original))); }
        catch (System.Exception e)
        {
            throw new BuildFailedException($"{ApiConfig.FileName} is not valid JSON ({e.Message}); " +
                                           "refusing to build so its keys can't be shipped by accident.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Backup));
        File.Copy(Original, Backup, overwrite: true);
        File.WriteAllText(Original, stripped.ToString(Newtonsoft.Json.Formatting.Indented));
        Debug.Log($"[Reverie] Build: shipping {ApiConfig.FileName} without keys/tokens (original backed up to {Backup}).");

        EditorApplication.update += RestoreWhenBuildEnds;   // covers failed/cancelled builds
    }

    public void OnPostprocessBuild(BuildReport report) => Restore();

    static void RestoreWhenBuildEnds()
    {
        if (BuildPipeline.isBuildingPlayer) return;
        EditorApplication.update -= RestoreWhenBuildEnds;
        Restore();
    }

    static void Restore()
    {
        if (!File.Exists(Backup)) return;
        File.Copy(Backup, Original, overwrite: true);
        File.Delete(Backup);
        Debug.Log($"[Reverie] Restored {ApiConfig.FileName} with your keys after the build.");
    }
}
