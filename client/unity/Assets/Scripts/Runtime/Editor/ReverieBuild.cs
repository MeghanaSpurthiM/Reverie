using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// Reverie > Build > ...
///   One Game per Theme:    a player-only game for each sample theme, containing only that theme.
///   Game (player only):    the game scene, compiled with REVERIE_PLAYER_ONLY, which removes the
///                          Reverie.Generation assembly — no LLM, server, Sketchfab or API-key code.
///   Theme Studio + Game:   Theme Studio (opens first) plus the game, for making themes in a build.
/// Output goes to Builds/<name>-<platform>/, e.g. Builds/Reverie-Forest-Windows/ (gitignored; see BuildsRoot). Uses the active build target.
/// Command line: scripts/build_windows.ps1, docs/BUILD.md.
/// Lives in the Runtime editor assembly so it exists even when generation is compiled out.
public static class ReverieBuild
{
    public const string PlayerOnlyDefine = "REVERIE_PLAYER_ONLY";
    const string StudioScene = "Assets/Scenes/ThemeStudio.unity";
    const string GameScene   = "Assets/Scenes/3DGS_Unity.unity";
    const string ThemesFolder = "Assets/StreamingAssets/SampleThemes";
    const string HoldFolder   = "SourceData/BuildHold";   // other themes wait here during a build
    const string ConfigAsset  = "Assets/FolderConfig.asset";
    internal static readonly string[] ShippedThemes = { "forest", "beach" };
    static readonly string[] PlayerDocs = { "../../docs/PLAYER_README.txt", "../../docs/THIRD_PARTY.md" };
    const string GltfMaterials = "Assets/Resources/GltfMaterials";

    [MenuItem("Reverie/Build/Game (player only)", priority = 100)]
    static void BuildGame() => Build("Reverie-Game", new[] { GameScene }, new[] { PlayerOnlyDefine });

    [MenuItem("Reverie/Build/One Game per Theme", priority = 99)]
    static void BuildThemesFromMenu() => BuildThemes();

    /// Batch mode: Unity -batchmode -buildTarget Win64 -executeMethod ReverieBuild.BuildWindowsThemes
    public static void BuildWindowsThemes() => EditorApplication.Exit(BuildThemes() ? 0 : 1);

    internal static bool BuildThemes()
    {
        bool ok = true;
        foreach (string theme in ShippedThemes) ok &= BuildTheme(theme);
        return ok;
    }

    /// StreamingAssets ships whole, so the other themes are moved out of it for the build. Each game
    /// gets its own product name, which keeps its saved settings separate from the other games'.
    internal static bool BuildTheme(string theme)
    {
        RestoreHeldThemes();
        string title = char.ToUpperInvariant(theme[0]) + theme.Substring(1);
        var config = AssetDatabase.LoadAssetAtPath<FolderConfig>(ConfigAsset);
        string folderPath = config.folderPath;
        string productName = PlayerSettings.productName;
        try
        {
            Directory.CreateDirectory(HoldFolder);
            foreach (string other in ShippedThemes.Where(t => t != theme))
            {
                Directory.Move($"{ThemesFolder}/{other}", $"{HoldFolder}/{other}");
                File.Move($"{ThemesFolder}/{other}.meta", $"{HoldFolder}/{other}.meta");
            }
            AssetDatabase.Refresh();

            config.folderPath = $"{ThemesFolder}/{theme}";
            EditorUtility.SetDirty(config);
            PlayerSettings.productName = $"Reverie {title}";
            AssetDatabase.SaveAssets();
            return Build($"Reverie-{title}", new[] { GameScene }, new[] { PlayerOnlyDefine });
        }
        finally
        {
            RestoreHeldThemes();
            AssetDatabase.Refresh();
            PlayerSettings.productName = productName;
            config = AssetDatabase.LoadAssetAtPath<FolderConfig>(ConfigAsset);   // the build reloads scripts
            config.folderPath = folderPath;
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
        }
    }

    internal static void RestoreHeldThemes()
    {
        if (!Directory.Exists(HoldFolder)) return;
        foreach (string held in Directory.GetDirectories(HoldFolder))
            Directory.Move(held, $"{ThemesFolder}/{Path.GetFileName(held)}");
        foreach (string meta in Directory.GetFiles(HoldFolder, "*.meta"))
            File.Move(meta, $"{ThemesFolder}/{Path.GetFileName(meta)}");
        Directory.Delete(HoldFolder);
    }

    /// Where the <name>-<platform>/ build folders go: REVERIE_BUILDS_DIR, else <project>/Builds.
    /// Every platform's build script packages from client/unity/Builds/ unless REVERIE_BUILDS_DIR is set.
    internal static string BuildsRoot()
    {
        string dir = System.Environment.GetEnvironmentVariable("REVERIE_BUILDS_DIR");
        return string.IsNullOrEmpty(dir) ? Path.Combine(Application.dataPath, "..", "Builds") : dir;
    }

    /// The repository's docs/ folder. REVERIE_DOCS_DIR overrides it for a project copied out of the
    /// repository (scripts/build_windows.ps1 builds from such a copy).
    internal static string DocsDir()
    {
        string dir = System.Environment.GetEnvironmentVariable("REVERIE_DOCS_DIR");
        return string.IsNullOrEmpty(dir) ? "../../docs" : dir;
    }

    /// Adds the player's documents and removes Unity's debug files, which aren't meant to ship.
    internal static void FinishPackage(string gamePath)
    {
        string folder = Path.GetDirectoryName(gamePath);
        foreach (string doc in PlayerDocs)
        {
            string source = Path.Combine(DocsDir(), Path.GetFileName(doc));
            if (File.Exists(source)) File.Copy(source, Path.Combine(folder, Path.GetFileName(doc)), true);
            else Debug.LogWarning($"[Reverie] Player doc not found, not packaged: {Path.GetFullPath(source)}");
        }
        foreach (string debug in Directory.GetDirectories(folder, "*_DoNotShip"))
            Directory.Delete(debug, true);
    }

    [MenuItem("Reverie/Build/Theme Studio + Game", priority = 101)]
    static void BuildStudio()
    {
        if (!File.Exists(StudioScene))
        {
            EditorUtility.DisplayDialog("Theme Studio scene missing",
                "Create it first with Reverie > Theme Studio > Create or Open Scene.", "OK");
            return;
        }
        Build("Reverie-ThemeStudio", new[] { StudioScene, GameScene }, new string[0]);
    }

    /// glTFast finds its shaders by name at runtime, so Unity strips them unless something uses them.
    /// Always-including them compiles thousands of variants, so the build uses temporary materials
    /// that cover the keywords glTFast can set on the shipped props.
    internal static void CreateGltfMaterials()
    {
        AssetDatabase.DeleteAsset(GltfMaterials);
        AssetDatabase.CreateFolder(Path.GetDirectoryName(GltfMaterials), Path.GetFileName(GltfMaterials));
        string[] maps = { "_NORMALMAP", "_OCCLUSION", "_EMISSIVE" };
        string[][] surfaces =
        {
            new string[0],
            new[] { "_ALPHATEST_ON" },
            new[] { "_SURFACE_TYPE_TRANSPARENT", "_DISABLE_SSR_TRANSPARENT", "_ENABLE_FOG_ON_TRANSPARENT" },
        };

        foreach (string name in new[] { "glTF-pbrMetallicRoughness", "glTF-pbrSpecularGlossiness", "glTF-unlit" })
        {
            var shader = Shader.Find("Shader Graphs/" + name)
                ?? throw new FileNotFoundException($"Shader not found: {name}");
            for (int surface = 0; surface < surfaces.Length; surface++)
            for (int mask = 0; mask < 1 << maps.Length; mask++)
            {
                var material = new Material(shader);
                foreach (string keyword in surfaces[surface]) material.EnableKeyword(keyword);
                for (int i = 0; i < maps.Length; i++)
                    if ((mask & 1 << i) != 0) material.EnableKeyword(maps[i]);
                AssetDatabase.CreateAsset(material, $"{GltfMaterials}/{name}-{surface}-{mask}.mat");
            }
        }
        AssetDatabase.SaveAssets();
    }

    /// "Windows", "macOS", "Linux", "Android", or the target's name: the suffix that keeps each
    /// platform's builds apart in Builds/ (Reverie-Forest-Windows, Reverie-Forest-macOS, ...).
    internal static string PlatformSuffix(BuildTarget target) => target switch
    {
        BuildTarget.StandaloneWindows or BuildTarget.StandaloneWindows64 => "Windows",
        BuildTarget.StandaloneOSX => "macOS",
        BuildTarget.StandaloneLinux64 => "Linux",
        BuildTarget.Android => "Android",
        _ => target.ToString()
    };

    /// Appends "-<platform>" to a build name unless it already ends with it.
    internal static string WithPlatform(string name, BuildTarget target)
    {
        string suffix = "-" + PlatformSuffix(target);
        return name.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase) ? name : name + suffix;
    }

    /// Builds into Builds/<name>-<platform>/ (see WithPlatform).
    internal static bool Build(string name, string[] scenes, string[] defines)
    {
        // Without TMP's settings and font, every text in the game fails to load.
        if (!TmpEssentialsImporter.EnsureImported())
        {
            Debug.LogError("[Reverie] TMP Essential Resources are missing; not building.");
            return false;
        }
        CreateGltfMaterials();
        var target = EditorUserBuildSettings.activeBuildTarget;
        string ext = target switch
        {
            BuildTarget.StandaloneWindows or BuildTarget.StandaloneWindows64 => ".exe",
            BuildTarget.Android => ".apk",
            BuildTarget.StandaloneOSX => ".app",
            BuildTarget.StandaloneLinux64 => ".x86_64",   // Linux players need the executable name
            _ => ""
        };
        name = WithPlatform(name, target);
        string path = Path.GetFullPath(Path.Combine(BuildsRoot(), name, "Reverie" + ext));

        BuildReport report;
        try
        {
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = path,
                target = target,
                options = BuildOptions.None,
                extraScriptingDefines = defines,
            });
        }
        finally { AssetDatabase.DeleteAsset(GltfMaterials); }

        var s = report.summary;
        string msg = s.result == BuildResult.Succeeded
            ? $"{name} built: {path} ({s.totalSize / (1024 * 1024)} MB)"
            : $"{name} build {s.result} with {s.totalErrors} error(s) — see the Console.";
        Debug.Log($"[Reverie] {msg}");
        bool ok = s.result == BuildResult.Succeeded;
        if (ok) FinishPackage(path);
        if (ok && !Application.isBatchMode) EditorUtility.RevealInFinder(path);
        return ok;
    }
}
