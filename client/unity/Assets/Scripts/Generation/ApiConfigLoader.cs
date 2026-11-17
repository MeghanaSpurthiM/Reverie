using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// ── Schema of api_config.json (field names match the JSON keys) ─────────────────────────
// Unknown keys are ignored and missing sections stay null, so every section is optional.
[Serializable] public class ApiSettings
{
    public ServerSettings     server;      // Reverie server (Theme Tools)
    public OllamaSettings     ollama;      // local LLM
    public SketchfabSettings  sketchfab;
}
[Serializable] public class ServerSettings     { public string url, token; }
[Serializable] public class OllamaSettings     { public string endpoint, model, api_key; }
[Serializable] public class SketchfabSettings  { public string api_token, search_endpoint, download_endpoint; }

/// <summary>
/// Single reader for api_config.json (Newtonsoft). Two layers, the second overriding the first:
///   1. StreamingAssets/api_config.json — the project's config. In the Editor it holds your keys
///      (gitignored); in builds it is shipped with every key and token blanked (ApiConfigBuildGuard),
///      so it only carries URLs, endpoints and model names.
///   2. persistentDataPath/api_config.json — per-machine settings: the keys a build needs. Written
///      by the Theme Tools panel ("Save on this device") or by hand.
/// Copy StreamingAssets/api_config.example.json to api_config.json and fill in your keys.
/// // and /* */ comments are allowed in the file.
/// </summary>
public static class ApiConfig
{
    public const string FileName        = "api_config.json";
    public const string ExampleFileName = "api_config.example.json";

    public static string OverridePath  => Path.Combine(Application.persistentDataPath, FileName);
    public static string StreamingPath => Path.Combine(Application.streamingAssetsPath, FileName);

    /// The file people edit: the per-machine override if present, otherwise StreamingAssets.
    public static string ResolvedPath => File.Exists(OverridePath) ? OverridePath : StreamingPath;

    /// True where StreamingAssets is inside an archive (Android APK) and must be read with
    /// UnityWebRequest; fetch <see cref="StreamingPath"/> and pass the text to <see cref="LoadFromText"/>.
    public static bool StreamingNeedsWebRequest => StreamingPath.Contains("://");

    static ApiSettings _settings;
    static bool _loaded;

    /// Null when no config could be read — see <see cref="LastError"/>.
    public static ApiSettings Settings
    {
        get { if (!_loaded) Load(); return _settings; }
    }

    public static string LastError { get; private set; }

    /// (Re)reads both layers from disk.
    public static void Load()
    {
        string baseText = !StreamingNeedsWebRequest && File.Exists(StreamingPath) ? File.ReadAllText(StreamingPath) : null;
        LoadFromText(baseText, StreamingPath);
    }

    /// Uses <paramref name="baseJson"/> (StreamingAssets text, or null) as the base layer and
    /// applies the per-machine override on top.
    public static bool LoadFromText(string baseJson, string source = FileName)
    {
        _loaded = true;
        try
        {
            JObject merged = string.IsNullOrWhiteSpace(baseJson) ? null : JObject.Parse(baseJson);
            if (File.Exists(OverridePath))
            {
                var over = JObject.Parse(File.ReadAllText(OverridePath));
                if (merged == null) merged = over;
                else merged.Merge(over, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            }
            if (merged == null)
            {
                Fail($"No {FileName}: copy {ExampleFileName} to {FileName} in StreamingAssets (Editor), " +
                     $"or save settings on this device ({OverridePath}).");
                return false;
            }
            _settings = merged.ToObject<ApiSettings>() ?? new ApiSettings();
            LastError = null;
            return true;
        }
        catch (Exception e)
        {
            Fail($"Failed to parse {source} / {OverridePath}: {e.Message}");
            return false;
        }
    }

    /// Writes values into the per-machine override (merged with what is already there), then
    /// reloads. Example: SaveOverride(new JObject { ["sketchfab"] = new JObject { ["api_token"] = t } }).
    public static void SaveOverride(JObject values)
    {
        JObject current = File.Exists(OverridePath) ? JObject.Parse(File.ReadAllText(OverridePath)) : new JObject();
        current.Merge(values, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
        Directory.CreateDirectory(Path.GetDirectoryName(OverridePath));
        File.WriteAllText(OverridePath, current.ToString(Formatting.Indented));
        Invalidate();
    }

    /// Blanks every secret (keys named like *key*, *token*, *secret*, *password*) in place.
    /// Used when shipping the config inside a build.
    public static JObject StripSecrets(JObject config)
    {
        foreach (var p in config.DescendantsAndSelf().OfType<JProperty>().ToList())
        {
            string n = p.Name.ToLowerInvariant();
            if (p.Value.Type == JTokenType.String &&
                (n.Contains("key") || n.Contains("token") || n.Contains("secret") || n.Contains("password")))
                p.Value = "";
        }
        return config;
    }

    /// Forces a re-read on next access (e.g. after editing the file in the Editor).
    public static void Invalidate() => _loaded = false;

    static void Fail(string error)
    {
        _settings = null;
        LastError = error;
        Debug.LogError($"[Config] {error}");
    }
}

/// Scene component kept for existing scenes: loads the config on Awake. Code can also
/// use <see cref="ApiConfig.Settings"/> directly — it loads on first access.
public class ApiConfigLoader : MonoBehaviour
{
    public static ApiSettings Settings => ApiConfig.Settings;

    void Awake()
    {
        ApiConfig.Invalidate();
        if (ApiConfig.Settings != null) Debug.Log("<color=green>[Config] Loaded.</color>");
    }
}
