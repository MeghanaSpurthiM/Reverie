using UnityEngine;

/// Marks a themed tool that rides a paddle (placed by FolderEnvironmentLoader). While active, the
/// tool hits targets by its own bounds, like the paddle it replaces.
public class KickTool : MonoBehaviour
{
    [Tooltip("The tool's bounds in this transform's local space.")]
    public Bounds localBounds;

    /// Measures the bounds of every renderer under this transform, in its local space.
    public void Measure()
    {
        bool any = false;
        Bounds b = default;
        Matrix4x4 toLocal = transform.worldToLocalMatrix;
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            Bounds mesh;
            if (r is SkinnedMeshRenderer skinned) mesh = skinned.localBounds;
            else if (r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null) mesh = mf.sharedMesh.bounds;
            else continue;

            Matrix4x4 m = toLocal * r.transform.localToWorldMatrix;
            Vector3 c = mesh.center, e = mesh.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 p = m.MultiplyPoint3x4(corner);
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
        }
        localBounds = b;
    }
}
