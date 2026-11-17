using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Installs TextMesh Pro's "TMP Essential Resources" (font, shaders, settings), which are not in git
/// (Unity Companion License). Unity's built-in uGUI package ships them with the same GUIDs the scenes use.
/// Runs on the first open, and before every build (ReverieBuild.Build), synchronously, so batch builds
/// get them too. By hand: Window > TextMeshPro > Import TMP Essential Resources.
/// Batch: Unity -batchmode -quit -projectPath client/unity -executeMethod TmpEssentialsImporter.ImportAndExit
[InitializeOnLoad]
public static class TmpEssentialsImporter
{
    const string SettingsAsset = "Assets/TextMesh Pro/Resources/TMP Settings.asset";
    const string Package = "Packages/com.unity.ugui/Package Resources/TMP Essential Resources.unitypackage";

    static TmpEssentialsImporter()
    {
        if (!IsImported()) EditorApplication.delayCall += () => EnsureImported();
    }

    public static bool IsImported() => File.Exists(SettingsAsset);

    /// Unpacks the package into the project if needed. Returns true when the resources are present.
    public static bool EnsureImported()
    {
        if (IsImported()) return true;
        string package = Path.GetFullPath(Package);
        if (!File.Exists(package)) { Debug.LogError($"[TmpEssentialsImporter] Not found: {package}"); return false; }

        int written = Unpack(package);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log($"[TmpEssentialsImporter] Imported TMP Essential Resources ({written} assets).");
        return IsImported();
    }

    public static void ImportAndExit() => EditorApplication.Exit(EnsureImported() ? 0 : 1);

    /// A .unitypackage is a gzipped tar of <guid>/{pathname, asset, asset.meta}. Writes each asset and its
    /// .meta (which carries the GUID) to its project path. Folders get their .meta too.
    static int Unpack(string package)
    {
        var entries = new Dictionary<string, Dictionary<string, byte[]>>();
        using (var gz = new GZipStream(File.OpenRead(package), CompressionMode.Decompress))
        {
            var header = new byte[512];
            while (ReadFully(gz, header) && header[0] != 0)
            {
                string name = Field(header, 0, 100);
                string prefix = Field(header, 345, 155);
                if (prefix.Length > 0) name = prefix + "/" + name;
                long size = System.Convert.ToInt64(Field(header, 124, 12).Trim().Length == 0 ? "0" : Field(header, 124, 12).Trim(), 8);
                char type = (char)header[156];
                var data = new byte[size];
                if (!ReadFully(gz, data)) break;
                Skip(gz, (512 - size % 512) % 512);
                if (type != '0' && type != '\0') continue;          // pax headers, directories
                string[] parts = name.TrimStart('.', '/').Split('/');
                if (parts.Length != 2) continue;
                if (!entries.TryGetValue(parts[0], out var files)) entries[parts[0]] = files = new Dictionary<string, byte[]>();
                files[parts[1]] = data;
            }
        }

        int written = 0;
        foreach (var files in entries.Values)
        {
            if (!files.TryGetValue("pathname", out var pathBytes)) continue;
            string path = Encoding.UTF8.GetString(pathBytes).Split('\n')[0].Trim();
            if (!path.StartsWith("Assets/")) continue;
            if (files.TryGetValue("asset", out var asset))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, asset);
                written++;
            }
            else Directory.CreateDirectory(path);
            if (files.TryGetValue("asset.meta", out var meta)) File.WriteAllBytes(path + ".meta", meta);
        }
        return written;
    }

    static string Field(byte[] b, int start, int length)
    {
        int end = start;
        while (end < start + length && b[end] != 0) end++;
        return Encoding.ASCII.GetString(b, start, end - start);
    }

    static bool ReadFully(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return read == buffer.Length;
            read += n;
        }
        return true;
    }

    static void Skip(Stream s, long count)
    {
        var buffer = new byte[512];
        while (count > 0)
        {
            int n = s.Read(buffer, 0, (int)System.Math.Min(count, buffer.Length));
            if (n <= 0) return;
            count -= n;
        }
    }
}
