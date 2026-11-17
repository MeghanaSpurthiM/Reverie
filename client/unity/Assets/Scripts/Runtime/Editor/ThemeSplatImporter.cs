using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// Turns a theme's scene.ply into a UnityGaussianSplatting (aras-p) asset inside the theme folder:
/// scene.asset plus scene_pos/oth/col/shs.bytes. Uses the plugin's own importer, at the same
/// full-precision settings as the study themes.
public static class ThemeSplatImporter
{
    const string CreatorTypeName = "GaussianSplatting.Editor.GaussianSplatAssetCreator";
    const string WorkFolder = "Assets/_SplatImport";
    const string SampleForest = "Assets/StreamingAssets/SampleThemes/forest";
    const string BaseName = "scene";
    const int CustomQuality = 5;   // DataQuality.Custom; the format fields stay at full precision

    [MenuItem("Reverie/Theme Folder/Import scene.ply as Splat...")]
    static void ImportFromMenu()
    {
        string folder = EditorUtility.OpenFolderPanel("Theme folder containing scene.ply", Application.dataPath, "");
        if (string.IsNullOrEmpty(folder)) return;
        Import(folder);
    }

    /// Batch mode: Unity -batchmode -executeMethod ThemeSplatImporter.ImportSampleForest
    public static void ImportSampleForest()
    {
        bool ok = false;
        try { Import(Path.GetFullPath(SampleForest)); ok = true; }
        catch (Exception e) { Debug.LogError(e); }
        EditorApplication.Exit(ok ? 0 : 1);
    }

    public static void Import(string themeFolder)
    {
        string ply = Path.Combine(themeFolder, BaseName + ".ply");
        if (!File.Exists(ply)) throw new FileNotFoundException($"No {BaseName}.ply in {themeFolder}");

        Directory.CreateDirectory(WorkFolder);
        AssetDatabase.Refresh();
        try
        {
            RunCreator(ply);
            foreach (string file in Directory.GetFiles(WorkFolder, BaseName + "*").Where(f => !f.EndsWith(".meta")))
                File.Copy(file, Path.Combine(themeFolder, Path.GetFileName(file)), true);
        }
        finally
        {
            AssetDatabase.DeleteAsset(WorkFolder);
            AssetDatabase.Refresh();
        }
        Debug.Log($"[Reverie] Imported splat into {themeFolder}");
    }

    /// The plugin only exposes its importer as an editor window, so the window's fields are set directly.
    static void RunCreator(string ply)
    {
        Type type = TypeCache.GetTypesDerivedFrom<EditorWindow>().FirstOrDefault(t => t.FullName == CreatorTypeName)
            ?? throw new InvalidOperationException("UnityGaussianSplatting is not installed (see Packages/manifest.json).");

        var creator = ScriptableObject.CreateInstance(type);
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo Field(string name) => type.GetField(name, flags)
                ?? throw new MissingFieldException(type.Name, name);

            Field("m_InputFile").SetValue(creator, ply);
            Field("m_OutputFolder").SetValue(creator, WorkFolder);
            Field("m_ImportCameras").SetValue(creator, false);
            FieldInfo quality = Field("m_Quality");
            quality.SetValue(creator, Enum.ToObject(quality.FieldType, CustomQuality));
            foreach (string format in new[] { "m_FormatPos", "m_FormatScale", "m_FormatColor", "m_FormatSH" })
            {
                FieldInfo field = Field(format);
                field.SetValue(creator, Enum.ToObject(field.FieldType, 0));
            }

            type.GetMethod("CreateAsset", flags).Invoke(creator, null);

            if (Field("m_ErrorMessage").GetValue(creator) is string error && error.Length > 0)
                throw new InvalidOperationException(error);
        }
        finally { UnityEngine.Object.DestroyImmediate(creator); }
    }
}
