using UnityEngine;
using System.IO;
using System.Collections;

/// <summary>
/// Loads an equirectangular panorama and renders it as a sky sphere.
/// Works in Scene view, Game view, and Meta XR Simulator.
/// Requires SkySphere.shader in Assets/Shaders/.
///
/// Two ways to supply the panorama (first non-null wins):
///   1. Drag a Texture2D into the 'panorama' field — no disk I/O needed.
///   2. Set 'folderPath' to an absolute folder path — loads the first image found.
/// </summary>
[ExecuteAlways]
public class SkyboxLoader : MonoBehaviour
{
    [Header("Panorama Source (first non-null wins)")]
    [Tooltip("Drag a Texture2D directly. Takes priority over folderPath.")]
    public Texture2D panorama;

    [Tooltip("Absolute path to a folder containing a panorama image.")]
    public string folderPath = "";

    [Header("Sphere Settings")]
    public float sphereRadius = 500f;
    [Range(0f, 4f)]   public float exposure = 1f;
    [Range(0f, 360f)] public float rotation  = 0f;

    private static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".exr" };

    private GameObject skySphere;
    private Material   sphereMaterial;
    private Texture2D  _ownedTex; // non-null only when loaded from disk (not a project asset)

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void OnEnable()
    {
        Cleanup();
        if (Application.isPlaying)
            StartCoroutine(LoadSkybox());
        else
            LoadSkyboxImmediate();
    }

    void OnDisable() => Cleanup();
    void OnDestroy() => Cleanup();

    // ── Edit mode (synchronous) ───────────────────────────────────────────────

    void LoadSkyboxImmediate()
    {
        if (panorama != null)
        {
            BuildSphere(panorama);
            return; // project asset — don't own it
        }

        string file = ResolveFile();
        if (file == null) return;

        Debug.Log($"[SkyboxLoader] Edit mode loading: {file}");
        Texture2D tex = LoadTextureFromDisk(file);
        BuildSphere(tex);
        _ownedTex = tex; // assign AFTER build so Cleanup() inside BuildSphere destroys the OLD tex
    }

    // ── Play mode (coroutine) ─────────────────────────────────────────────────

    IEnumerator LoadSkybox()
    {
        if (panorama != null)
        {
            BuildSphere(panorama);
            yield break;
        }

        string file = ResolveFile();
        if (file == null) yield break;

        Debug.Log($"[SkyboxLoader] Play mode loading: {file}");
        Texture2D tex = LoadTextureFromDisk(file);
        BuildSphere(tex);
        _ownedTex = tex; // assign AFTER build so Cleanup() inside BuildSphere destroys the OLD tex
        yield return null;
    }

    // ── Shared build logic ────────────────────────────────────────────────────

    string ResolveFile()
    {
        // No source configured — silently skip (normal before FolderEnvironmentLoader sets folderPath)
        bool hasFolder = !string.IsNullOrWhiteSpace(folderPath);
        if (!hasFolder && panorama == null) return null;

        string folder = hasFolder ? folderPath : Application.streamingAssetsPath;

        if (!Directory.Exists(folder))
        {
            Debug.LogWarning($"[SkyboxLoader] Folder not found: '{folder}'.");
            return null;
        }

        string file = FindFileByExtension(folder, SupportedExtensions);
        if (file == null)
            Debug.LogWarning($"[SkyboxLoader] No image (.jpg/.png/.exr) found in '{folder}'.");

        return file;
    }

    Texture2D LoadTextureFromDisk(string filePath)
{
    byte[]    bytes = File.ReadAllBytes(filePath);
    string    ext   = Path.GetExtension(filePath).ToLower();
    Texture2D tex   = ext == ".exr"
        ? new Texture2D(2, 2, TextureFormat.RGBAFloat, mipChain: true, linear: true)
        : new Texture2D(2, 2, TextureFormat.RGBA32,    mipChain: true, linear: false);

    if (!tex.LoadImage(bytes))
    {
        Debug.LogError($"[SkyboxLoader] Failed to decode: {filePath}");
        return null;
    }

    tex.wrapModeU = TextureWrapMode.Repeat;
    tex.wrapModeV = TextureWrapMode.Clamp;   // clamp vertical to kill top/bottom seams
    tex.filterMode = FilterMode.Trilinear;   // kills grain — bilinear causes mip shimmer
    tex.Apply(updateMipmaps: true, makeNoLongerReadable: false);
    return tex;
}

    void BuildSphere(Texture2D tex)
    {
        if (tex == null) return;

        Cleanup();

        Shader shader = Shader.Find("Custom/SkySphere");
        if (shader == null)
        {
            Debug.LogError("[SkyboxLoader] Shader 'Custom/SkySphere' not found. " +
                           "Place SkySphere.shader in Assets/Shaders/ and reimport.");
            return;
        }

        sphereMaterial = new Material(shader);
        sphereMaterial.SetTexture("_MainTex", tex);
        sphereMaterial.SetFloat("_Exposure", exposure);
        tex.anisoLevel = 9; 

        skySphere      = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        skySphere.name = "SkySphere (Runtime)";

        Collider col = skySphere.GetComponent<Collider>();
        if (col != null) DestroyObj(col);

        skySphere.transform.localScale    = Vector3.one * sphereRadius;
        skySphere.transform.localRotation = Quaternion.Euler(0f, rotation, 0f);
        skySphere.transform.position      = Vector3.zero;
        skySphere.transform.SetParent(transform, worldPositionStays: false);

        MeshRenderer mr = skySphere.GetComponent<MeshRenderer>();
        mr.material          = sphereMaterial;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;

        if (Application.isPlaying)
        {
            Transform xrParent = FindXRParent();
            if (xrParent != null)
            {
                skySphere.transform.SetParent(xrParent, worldPositionStays: false);
                Debug.Log($"[SkyboxLoader] Parented to XR rig: {xrParent.name}");
            }
        }

        Debug.Log($"[SkyboxLoader] Sphere built — " +
                  $"Tex={tex.width}x{tex.height} Radius={sphereRadius}");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Show the sky sphere without reloading. Triggers a load if not yet built.</summary>
    public void Show()
    {
        if (skySphere != null) skySphere.SetActive(true);
        else                   enabled = true; // not yet loaded — trigger OnEnable
    }

    /// <summary>True once a panorama has been loaded and the sky sphere built.</summary>
    public bool HasSphere => skySphere != null;

    /// <summary>Hide the sky sphere without destroying it or unloading the texture.</summary>
    public void Hide()
    {
        if (skySphere != null) skySphere.SetActive(false);
    }

    /// <summary>Apply a texture directly without reloading from disk.</summary>
    public void ApplyTexture(Texture2D tex)
    {
        if (tex == null) return;
        if (sphereMaterial == null) { BuildSphere(tex); return; }
        sphereMaterial.SetTexture("_MainTex", tex);
    }

    /// <summary>Point at a new folder and reload.</summary>
    public void RefreshFromFolder(string path)
    {
        folderPath = path;
        OnDisable();
        OnEnable();
    }

    // ── Edit mode: follow Scene view camera ───────────────────────────────────

#if UNITY_EDITOR
    void Update()
    {
        if (Application.isPlaying || skySphere == null) return;
        var sv = UnityEditor.SceneView.lastActiveSceneView;
        if (sv != null)
            skySphere.transform.position = sv.camera.transform.position;
    }
#endif

    void OnValidate()
    {
        if (sphereMaterial != null)
            sphereMaterial.SetFloat("_Exposure", exposure);
        if (skySphere != null)
        {
            skySphere.transform.localRotation = Quaternion.Euler(0f, rotation, 0f);
            skySphere.transform.localScale    = Vector3.one * sphereRadius;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void Cleanup()
    {
        DestroyObj(skySphere);
        DestroyObj(sphereMaterial);
        DestroyObj(_ownedTex);   // only destroys textures we created from disk
        skySphere      = null;
        sphereMaterial = null;
        _ownedTex      = null;

        foreach (var go in GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            if (go != null && go.name == "SkySphere (Runtime)") DestroyObj(go);
    }

    void DestroyObj(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Destroy(obj);
        else                       DestroyImmediate(obj);
    }

    static Transform FindXRParent()
    {
        GameObject ovrRig = GameObject.Find("OVRCameraRig");
        if (ovrRig != null) return ovrRig.transform;

        foreach (string n in new[] {
            "[BuildingBlock] Camera Rig", "Camera Rig", "CameraRig",
            "XR Origin", "XRRig", "XR Rig" })
        {
            var go = GameObject.Find(n);
            if (go != null) return go.transform;
        }

        return Camera.main != null ? Camera.main.transform.root : null;
    }

    static string FindFileByExtension(string folder, string[] extensions)
    {
        foreach (string ext in extensions)
        {
            string[] matches = Directory.GetFiles(folder, "*" + ext, SearchOption.TopDirectoryOnly);
            if (matches.Length > 0) return matches[0];
        }
        return null;
    }
}
