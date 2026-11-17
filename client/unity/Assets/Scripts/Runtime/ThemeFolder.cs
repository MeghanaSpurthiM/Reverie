using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// Finds drop-in files by name, so a theme or room can be made by copying files into a folder.
public static class ThemeFolder
{
    static readonly string[] ModelExtensions = { ".glb", ".gltf" };
    static readonly string[] Ignored = { "README.md", ".gitignore" };

    /// The held tool: tool.glb or tool.gltf, else the first tool_*.glb alphabetically.
    public static string FindTool(string folder) => FindProp(folder, "tool");

    /// The target: target.glb or target.gltf, else the first target_*.glb alphabetically.
    public static string FindTarget(string folder) => FindProp(folder, "target");

    /// The base room: room.glb or room.gltf.
    public static string FindRoom(string folder) => FindExact(folder, "room");

    /// Theme folders directly under the given roots, in alphabetical order. Empty templates don't count.
    public static List<string> ListThemes(params string[] roots) =>
        roots.Where(Directory.Exists)
             .SelectMany(Directory.GetDirectories)
             .Where(HasContent)
             .OrderBy(folder => Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// The theme after <paramref name="current"/>, wrapping around. Null if there's nothing to switch to.
    public static string Next(IList<string> themes, string current)
    {
        if (themes.Count == 0) return null;
        string full = Path.GetFullPath(current).TrimEnd('/', '\\');
        int index = themes.ToList().FindIndex(t =>
            string.Equals(Path.GetFullPath(t).TrimEnd('/', '\\'), full, StringComparison.OrdinalIgnoreCase));
        string next = themes[(index + 1) % themes.Count];
        return index >= 0 && themes.Count == 1 ? null : next;
    }

    static bool HasContent(string folder) =>
        Directory.GetFiles(folder).Any(f => !Ignored.Contains(Path.GetFileName(f)) && !f.EndsWith(".meta"));

    static string FindProp(string folder, string role)
    {
        string exact = FindExact(folder, role);
        if (exact != null || !Directory.Exists(folder)) return exact;

        string[] named = Directory.GetFiles(folder, role + "_*.glb", SearchOption.TopDirectoryOnly);
        if (named.Length == 0) return null;
        Array.Sort(named, StringComparer.OrdinalIgnoreCase);
        return named[0];
    }

    static string FindExact(string folder, string name)
    {
        foreach (string extension in ModelExtensions)
        {
            string path = Path.Combine(folder, name + extension);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
