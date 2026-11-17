using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// Finds base room models in a folder: room.glb or room.gltf first, then room_*.glb and
/// room_*.gltf in alphabetical order. Each file becomes one room in the cycle.
public static class BaseRoomFiles
{
    static readonly string[] Extensions = { ".glb", ".gltf" };

    public static List<string> List(string folder)
    {
        var rooms = new List<string>();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return rooms;

        string main = ThemeFolder.FindRoom(folder);
        if (main != null) rooms.Add(main);

        rooms.AddRange(Directory.GetFiles(folder, "room_*", SearchOption.TopDirectoryOnly)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));
        return rooms;
    }

    /// "room_kitchen.glb" → "kitchen", "room.glb" → "room".
    public static string DisplayName(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        return stem.StartsWith("room_", StringComparison.OrdinalIgnoreCase) && stem.Length > 5 ? stem.Substring(5) : stem;
    }
}
