using System;
using System.IO;
using System.Threading.Tasks;
using GaussianSplatting.Runtime;
using UnityEngine;

/// <summary>
/// Shows a Gaussian splat environment on one renderer. Three ways to supply it:
///   - SpawnFromAsset(asset): a GaussianSplatAsset imported in the Editor (smallest, fastest).
///   - SpawnFromKey(key):     one of the prebakedAssets below.
///   - SpawnFromPly(path):    a 3DGS .ply converted at runtime by RuntimeSplatLoader. Works in
///                            the Editor and in compiled players, so themes downloaded from the
///                            Reverie server render without an Editor import step.
/// </summary>
public class GaussianSpawner : MonoBehaviour
{
    [Header("Rig scene reference")]
    [Tooltip("Drag the PaddleRig root GameObject here. " +
             "The Gaussian renderer will be a child of this, making it " +
             "move with the rig and creating an immersive wrap.")]
    public Transform smplRoot;

    [Header("Renderer settings")]
    [Tooltip("Scale applied to the splat transform. " +
             "A 40m scale fills the playspace when standing inside the splat.")]
    public GaussianSplatRenderer prewarmedRenderer;
    public float splatScale = 40f;

    [Tooltip("Rotation offset to align the captured scene with Unity's Y-up world. " +
             "aras-p captures in a rotated coordinate frame — this corrects it.")]
    public Vector3 splatRotationOffset = new Vector3(-90f, 0f, 0f);

    [Header("Pre-baked asset library")]
    [Tooltip("Drag GaussianSplatAsset objects here from Assets/SplatAssets/. " +
             "Key = theme name (lowercase, hyphens). " +
             "Value = the GaussianSplatAsset ScriptableObject.")]
    public ThemeAssetEntry[] prebakedAssets;

    [System.Serializable]
    public struct ThemeAssetEntry
    {
        public string           themeKey;
        public GaussianSplatAsset asset;
    }

    // Internal state
    GameObject          _splatGo;
    GaussianSplatRenderer _renderer;

    // ── Public: load a GaussianSplatAsset directly ───────────────────────────

    /// <summary>Assign any GaussianSplatAsset directly — no key lookup needed.</summary>
    public void SpawnFromAsset(GaussianSplatAsset asset)
    {
        if (asset == null) return;
        EnsureRenderer();
        _renderer.m_Asset = asset;
        _splatGo.SetActive(true);
        Debug.Log($"<color=green>[Gaussian]</color> Loaded asset: {asset.name}");
    }

    // ── Public: swap to a pre-baked theme (instant, works in builds) ──────────

    /// <summary>Returns true if a matching asset was found and the renderer was activated.</summary>
    public bool SpawnFromKey(string themeKey)
    {
        string key = themeKey.ToLower().Replace(" ", "-");

        foreach (var entry in prebakedAssets)
        {
            if (entry.themeKey == key)
            {
                EnsureRenderer();
                _renderer.m_Asset = entry.asset;
                _splatGo.SetActive(true);
                Debug.Log($"<color=green>[Gaussian]</color> Swapped to pre-baked theme: {key}" +
                          $" | renderReady={_renderer.HasValidRenderSetup}");
                return true;
            }
        }

        Debug.LogWarning($"[Gaussian] No pre-baked asset found for key '{key}'. " +
                         $"Available keys: [{string.Join(", ", System.Array.ConvertAll(prebakedAssets, e => e.themeKey))}]");
        return false;
    }

    // ── Public: load a .ply at runtime (Editor and builds) ────────────────────

    GaussianSplatAsset _runtimeAsset;   // created from a .ply; freed when replaced

    /// <summary>
    /// Converts a 3DGS .ply into a GaussianSplatAsset in memory (RuntimeSplatLoader) and shows it.
    /// Works in compiled players. Returns false (and logs why) if the file can't be loaded.
    /// </summary>
    public Task<bool> SpawnFromPly(string plyPath, IProgress<float> progress = null) =>
        SpawnRuntime(plyPath, () => RuntimeSplatLoader.LoadAsync(plyPath, progress));

    /// <summary>
    /// Loads an Editor-imported splat asset that was copied as plain files (.asset + .bytes),
    /// e.g. a sample theme in StreamingAssets. Works in compiled players.
    /// </summary>
    public Task<bool> SpawnFromPrebaked(string assetPath, IProgress<float> progress = null) =>
        SpawnRuntime(assetPath, () => RuntimeSplatLoader.LoadPrebakedAsync(assetPath, progress));

    async Task<bool> SpawnRuntime(string path, Func<Task<GaussianSplatAsset>> load)
    {
        GaussianSplatAsset asset;
        try
        {
            Debug.Log($"[Gaussian] Loading {path} ...");
            asset = await load();
        }
        catch (Exception e)
        {
            Debug.LogError($"[Gaussian] Could not load {path}: {e.Message}");
            return false;
        }
        if (this == null) { RuntimeSplatLoader.Destroy(asset); return false; } // destroyed while loading

        SpawnFromAsset(asset);
        RuntimeSplatLoader.Destroy(_runtimeAsset);
        _runtimeAsset = asset;
        Debug.Log($"<color=green>[Gaussian]</color> {asset.splatCount:N0} splats loaded from {Path.GetFileName(path)}");
        return true;
    }

    // ── Renderer lifecycle ────────────────────────────────────────────────────

    void EnsureRenderer()
    {
        if (_splatGo != null) return;

        if (prewarmedRenderer != null)
        {
            // Reuse the pre-placed renderer — shaders already initialised by Editor
            _splatGo  = prewarmedRenderer.gameObject;
            _renderer = prewarmedRenderer;

            Transform parent = smplRoot != null ? smplRoot : transform;
            _splatGo.transform.SetParent(parent, worldPositionStays: false);
            _splatGo.transform.localPosition = Vector3.zero;
            _splatGo.transform.localRotation = Quaternion.Euler(splatRotationOffset);
            _splatGo.transform.localScale    = Vector3.one * splatScale;
            _splatGo.SetActive(false);

            Debug.Log($"[Gaussian] Reusing pre-warmed renderer under '{parent.name}'.");
            return;
        }

        // Fallback: dynamic creation (may cause shader resource error on some platforms)
        Transform fallbackParent = smplRoot != null ? smplRoot : transform;
        _splatGo = new GameObject("_GaussianEnvironment");
        _splatGo.transform.SetParent(fallbackParent, worldPositionStays: false);
        _splatGo.transform.localPosition = Vector3.zero;
        _splatGo.transform.localRotation = Quaternion.Euler(splatRotationOffset);
        _splatGo.transform.localScale    = Vector3.one * splatScale;
        _renderer = _splatGo.AddComponent<GaussianSplatRenderer>();
        _splatGo.SetActive(false);

        Debug.LogWarning("[Gaussian] No prewarmedRenderer assigned — created dynamically. " +
                        "Assign a scene-placed GaussianSplatRenderer to avoid shader errors.");
    }

    // ── Scene management ──────────────────────────────────────────────────────

    public void Hide() { if (_splatGo != null) _splatGo.SetActive(false); }
    public void Show() { if (_splatGo != null) _splatGo.SetActive(true); }

    /// <summary>
    /// Activate the prewarmed renderer directly — no key or .ply needed.
    /// Use when the GaussianSplatRenderer already has an asset assigned in the Inspector.
    /// </summary>
    public void ActivatePrewarmed()
    {
        EnsureRenderer();
        Show();
        Debug.Log("[Gaussian] Prewarmed renderer activated.");
    }

    /// <summary>True when a splat asset is assigned, so showing the renderer draws something.</summary>
    public bool HasSplat => _renderer != null && _renderer.m_Asset != null;

    /// <summary>Transform of the active splat GameObject (null until first SpawnFromKey/SpawnFromPly).</summary>
    public Transform splatTransform => _splatGo != null ? _splatGo.transform : null;

    void OnDestroy()
    {
        if (_splatGo != null) Destroy(_splatGo);
        RuntimeSplatLoader.Destroy(_runtimeAsset);
        _runtimeAsset = null;
    }
}