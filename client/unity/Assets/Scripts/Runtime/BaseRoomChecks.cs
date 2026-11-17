using System.Collections.Generic;
using UnityEngine;

/// Finds the usual problems with a room taken from an asset pack. The Room loader runs it on each
/// room variant when the game starts, and Reverie → Base Room → Validate Rooms runs it in the Editor.
public static class BaseRoomChecks
{
    /// Non-convex mesh colliders above this many triangles stall the frame when the room is shown.
    public const int MaxMeshColliderTriangles = 50000;

    /// A room taller or wider than this is probably in centimetres, or is a whole level.
    public const float MaxRoomSize = 200f;
    public const float MinRoomSize = 1f;

    /// The room's floor should sit within this distance of its root's origin.
    public const float FloorTolerance = 0.25f;

    /// Shaders from the Built-in Render Pipeline that render pink under URP.
    static readonly string[] BuiltInShaderPrefixes =
    {
        "Standard", "Legacy Shaders/", "Mobile/", "Nature/", "Particles/Standard", "Autodesk Interactive"
    };

    public static bool IsPink(Material material)
    {
        if (material == null) return true;
        Shader shader = material.shader;
        if (shader == null || !shader.isSupported || shader.name == "Hidden/InternalErrorShader") return true;
        foreach (string prefix in BuiltInShaderPrefixes)
            if (shader.name.StartsWith(prefix)) return true;
        return false;
    }

    /// Plain-language problems with <paramref name="room"/>. Empty means the room is ready to use.
    public static List<string> Inspect(GameObject room)
    {
        var issues = new List<string>();
        if (room == null) { issues.Add("The room is missing."); return issues; }

        var pink = new HashSet<string>();
        int missing = 0;
        var renderers = room.GetComponentsInChildren<Renderer>(true);
        foreach (var renderer in renderers)
        {
            if (renderer is ParticleSystemRenderer) continue;
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null) missing++;
                else if (IsPink(material)) pink.Add($"{material.name} ({material.shader.name})");
            }
        }
        if (pink.Count > 0)
            issues.Add($"{pink.Count} material(s) use Built-in shaders and render pink in URP: {Join(pink)}. " +
                       "Convert them with Window → Rendering → Render Pipeline Converter.");
        if (missing > 0)
            issues.Add($"{missing} renderer slot(s) have no material and render pink.");

        foreach (var collider in room.GetComponentsInChildren<MeshCollider>(true))
        {
            int triangles = TriangleCount(collider.sharedMesh);
            if (!collider.convex && triangles > MaxMeshColliderTriangles)
                issues.Add($"Mesh collider on '{collider.name}' has {triangles:N0} triangles. Replace it with box colliders " +
                           "or remove it; Reverie does not need room colliders.");
        }

        foreach (var light in room.GetComponentsInChildren<Light>(true))
        {
#if UNITY_EDITOR
            if (light.lightmapBakeType == LightmapBakeType.Baked)
                issues.Add($"Light '{light.name}' is Baked, so it gives no light unless you bake this scene. Set its Mode to Realtime.");
#endif
            if (light.type == LightType.Directional)
                issues.Add($"Directional light '{light.name}' adds to the scene's own Directional Light. " +
                           "Keep it only if the room should be lit differently.");
        }

        foreach (var probe in room.GetComponentsInChildren<ReflectionProbe>(true))
        {
            if (probe.mode == UnityEngine.Rendering.ReflectionProbeMode.Realtime &&
                probe.refreshMode == UnityEngine.Rendering.ReflectionProbeRefreshMode.EveryFrame)
                issues.Add($"Reflection probe '{probe.name}' re-renders every frame, which halves the frame rate in VR. " +
                           "Set it to Baked or On Awake.");
        }

        foreach (var camera in room.GetComponentsInChildren<Camera>(true))
            issues.Add($"Camera '{camera.name}' is inside the room. Remove it (asset pack demo player?); the loader turns it off.");
        foreach (var listener in room.GetComponentsInChildren<AudioListener>(true))
            issues.Add($"Audio Listener '{listener.name}' is inside the room. Remove it; the loader turns it off.");

        int lightmapped = 0;
        foreach (var renderer in renderers)
            if (renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 0xFFFE && LightmapSettings.lightmaps.Length == 0) lightmapped++;
        if (lightmapped > 0)
            issues.Add($"{lightmapped} renderer(s) expect baked lightmaps that this scene does not have. Use realtime lighting.");

        if (TryGetBounds(room, out Bounds bounds))
        {
            float size = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            if (size > MaxRoomSize)
                issues.Add($"The room is {size:F0} m across. Check its scale: 1 unit should be 1 metre.");
            else if (size < MinRoomSize)
                issues.Add($"The room is only {size:F2} m across. Check its scale: 1 unit should be 1 metre.");

            float floor = bounds.min.y - room.transform.position.y;
            float scale = Mathf.Abs(room.transform.lossyScale.y);
            if (scale > 0f && Mathf.Abs(floor / scale) > FloorTolerance)
                issues.Add($"The lowest point of the room is {floor / scale:F2} m from its root. Move the geometry so the floor is at y = 0.");
        }
        else issues.Add("The room has no visible meshes.");

        return issues;
    }

    /// Turns off parts of an asset pack scene that would fight the game's own camera rig.
    /// Returns how many components it turned off.
    public static int DisableStrayPlayerParts(GameObject room)
    {
        int count = 0;
        foreach (var camera in room.GetComponentsInChildren<Camera>(true))
            if (camera.enabled) { camera.enabled = false; count++; }
        foreach (var listener in room.GetComponentsInChildren<AudioListener>(true))
            if (listener.enabled) { listener.enabled = false; count++; }
        return count;
    }

    public static bool TryGetBounds(GameObject root, out Bounds bounds)
    {
        bounds = default;
        bool found = false;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is ParticleSystemRenderer) continue;
            if (!found) { bounds = renderer.bounds; found = true; }
            else bounds.Encapsulate(renderer.bounds);
        }
        return found;
    }

    static int TriangleCount(Mesh mesh)
    {
        if (mesh == null) return 0;
        long indices = 0;
        for (int i = 0; i < mesh.subMeshCount; i++) indices += mesh.GetIndexCount(i);
        return (int)(indices / 3);
    }

    static string Join(IEnumerable<string> items)
    {
        var list = new List<string>(items);
        if (list.Count > 5) { int more = list.Count - 5; list = list.GetRange(0, 5); list.Add($"and {more} more"); }
        return string.Join(", ", list);
    }
}
