using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GLTFast;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// Attached to CanvasOVR.
/// Tool toggle ON:
///   - Loads tool.glb or tool_*.glb, fits it to the assigned Quad (scale + stand pivot), hides quad renderer.
///   - Loads target.glb or target_*.glb and attaches a copy to every GameObject tagged "Target" (bubbles).
///   - Scans every 0.3s for newly spawned bubbles so new ones get the target GLB too.
public class FolderEnvironmentLoader : MonoBehaviour
{
    [Header("Shared Folder")]
    public FolderConfig folderConfig;

    [Header("Systems")]
    public AmbientAudioController audioController;
    public SkyboxLoader             skyboxLoader;
    public GaussianSpawner        gaussianSpawner;

    [Header("Tool")]
    [Tooltip("First transform the tool GLB stands on.")]
    public Transform toolQuad1;
    [Tooltip("Second transform the tool GLB stands on.")]
    public Transform toolQuad2;
    [Tooltip("World-space euler rotation applied to the tool before fitting. Try (90,0,0) if it appears flat.")]
    public Vector3 toolRotationOffset = Vector3.zero;
    [Tooltip("Which end of the mesh attaches to the foot. 0 = bottom (handle/base), 1 = top (blade/tip). Use 1 when the strike face is at the top of the model.")]
    [Range(0f, 1f)]
    public float toolPivotNormalized = 0f;

    [Header("Toggles")]
    public Toggle audioToggle;
    public Toggle skyboxToggle;
    public Toggle gaussianToggle;
    public Toggle toolToggle;
    public Toggle roomToggle;

    [Header("Status Labels")]
    public TMP_Text audioStatus;
    public TMP_Text skyboxStatus;
    public TMP_Text gaussianStatus;
    public TMP_Text toolStatus;
    public TMP_Text roomStatus;

    [Header("Room")]
    [Tooltip("GameObject to show/hide with the Room toggle.")]
    public GameObject roomObject;

    [Header("Rows (hide when base scene is active)")]
    [Tooltip("The entire skybox row — hidden when base scene (room) is on.")]
    public GameObject skyboxRow;
    [Tooltip("The entire Gaussian row — hidden when base scene (room) is on.")]
    public GameObject gaussianRow;
    [Tooltip("The entire audio row — hidden when base scene (room) is on.")]
    public GameObject audioRow;
    [Tooltip("The entire tool/target row — hidden when base scene (room) is on.")]
    public GameObject toolRow;

    [Header("Keyboard Hotkeys (KeyCode int)")]
    public int audioKey    = 49; // 1
    public int skyboxKey   = 50; // 2
    public int gaussianKey = 51; // 3
    public int toolKey     = 52; // 4
    public int roomKey     = 53; // 5

    // ── Internal ──────────────────────────────────────────────────────────────

    bool _skyboxLoaded   = false;
    bool _gaussianLoaded = false;
    bool _gaussianLoading = false;
    string _gaussianFolder;          // folder the current splat came from
    bool _toolLoaded     = false;
    bool _toolLoading    = false;

    GltfImport _toolGltf;
    GltfImport _targetGltf;
    GameObject _toolInstance1;
    GameObject _toolInstance2;
    GameObject _targetTemplate;  // inactive clone source for bubble targets
    Renderer[] _quad1Renderers;  // captured before tool placement
    Renderer[] _quad2Renderers;

    readonly Dictionary<int, Renderer[]> _hiddenRenderers = new(); // bubble id → its renderers

    bool _audioOn    = false;
    bool _skyboxOn   = false;
    bool _gaussianOn = false;
    bool _toolOn     = false;
    bool _roomOn     = true;   // room starts visible

    Coroutine                  _targetScan;
    readonly HashSet<int>      _taggedBubbles        = new();
    readonly List<GameObject>  _activeTargetInstances = new();
    Bounds                     _templateBounds;

    [Header("Theme switching")]
    public KeyCode nextThemeKey = KeyCode.T;
    const OVRInput.Button NextThemeButton = OVRInput.Button.PrimaryThumbstick;   // left stick click
    static bool _showThemeAfterReload;

    /// True while the tool and target models are loading.
    public bool IsToolLoading => _toolLoading;

    static readonly string[] AudioExtensions = { ".wav", ".mp3", ".ogg" };

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Start()
    {
        if (skyboxLoader != null) skyboxLoader.enabled = false;

        audioToggle?.onValueChanged.AddListener(v    => { _audioOn    = v; ApplyAudio(); });
        skyboxToggle?.onValueChanged.AddListener(v   => { _skyboxOn   = v; ApplySkybox();  if (v) { TurnOffGaussian(); TurnOffRoom(); } else if (!_gaussianOn) { TurnOffAudio(); TurnOffTool(); } });
        gaussianToggle?.onValueChanged.AddListener(v => { _gaussianOn = v; ApplyGaussian(); if (v) { TurnOffSkybox();  TurnOffRoom(); } else if (!_skyboxOn)   { TurnOffAudio(); TurnOffTool(); } });
        toolToggle?.onValueChanged.AddListener(v     => { _toolOn     = v; ApplyTool(); });
        roomToggle?.onValueChanged.AddListener(v     => { _roomOn = v; ApplyRoom(); if (v) { TurnOffSkybox(); TurnOffGaussian(); } else if (!_gaussianOn) TurnOnSkybox(); });

        audioToggle?.SetIsOnWithoutNotify(false);
        skyboxToggle?.SetIsOnWithoutNotify(false);
        gaussianToggle?.SetIsOnWithoutNotify(false);
        toolToggle?.SetIsOnWithoutNotify(false);
        if (roomToggle != null)
        {
            roomToggle.isOn = _roomOn;
            roomToggle.GetComponent<ToggleSliderAnim>()?.Snap(_roomOn);
        }
        ApplyRoom(); // enforce correct status label regardless of listener chain ordering

        Bubble.Spawned += OnBubbleSpawned;

        if (_showThemeAfterReload)
        {
            _showThemeAfterReload = false;
            TurnOnSkybox();
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(nextThemeKey) || OVRInput.GetDown(NextThemeButton, OVRInput.Controller.LTouch))
        {
            NextTheme();
            return;
        }
        // Audio and the tool and target belong to a theme world: their keys do nothing until the
        // skybox or 3DGS is on and the base room is off (their rows are hidden then too).
        if (Input.GetKeyDown((KeyCode)audioKey) && WorldShown)    ToggleAudio();
        if (Input.GetKeyDown((KeyCode)skyboxKey))   ToggleSkybox();
        if (Input.GetKeyDown((KeyCode)gaussianKey)) ToggleGaussian();
        if (Input.GetKeyDown((KeyCode)toolKey) && WorldShown)     ToggleTool();
        if (Input.GetKeyDown((KeyCode)roomKey))     ToggleRoom();
    }

    void ToggleAudio()    { _audioOn    = !_audioOn;    ApplyAudio();    audioToggle?.SetIsOnWithoutNotify(_audioOn); }
    void ToggleSkybox()   { if (skyboxToggle   != null) skyboxToggle.isOn   = !skyboxToggle.isOn; }
    void ToggleGaussian() { if (gaussianToggle != null) gaussianToggle.isOn = !gaussianToggle.isOn; }

    void TurnOffSkybox()
    {
        if (skyboxToggle != null && skyboxToggle.isOn)
            skyboxToggle.isOn = false;   // fires onValueChanged → animates bar + ApplySkybox(false)
    }

    void TurnOffGaussian()
    {
        if (gaussianToggle != null && gaussianToggle.isOn)
            gaussianToggle.isOn = false; // fires onValueChanged → animates bar + ApplyGaussian(false)
    }

    void TurnOffRoom()
    {
        if (roomToggle != null && roomToggle.isOn)
            roomToggle.isOn = false;     // fires onValueChanged → animates bar + ApplyRoom(false)
        else if (roomToggle == null && _roomOn) { _roomOn = false; ApplyRoom(); }
    }

    void TurnOnRoom()
    {
        if (roomToggle != null && !roomToggle.isOn)
            roomToggle.isOn = true;      // fires onValueChanged → animates bar + ApplyRoom(true)
        else if (roomToggle == null && !_roomOn) { _roomOn = true; ApplyRoom(); }
    }

    void TurnOnSkybox()
    {
        if (skyboxToggle != null && !skyboxToggle.isOn)
            skyboxToggle.isOn = true;
        else if (skyboxToggle == null && !_skyboxOn) { _skyboxOn = true; ApplySkybox(); }
    }

    void TurnOffAudio()
    {
        if (audioToggle != null && audioToggle.isOn)
            audioToggle.isOn = false;
        else if (!_audioOn) return;
        else { _audioOn = false; ApplyAudio(); audioToggle?.SetIsOnWithoutNotify(false); }
    }

    void TurnOffTool()
    {
        if (toolToggle != null && toolToggle.isOn)
            toolToggle.isOn = false;
        else if (!_toolOn) return;
        else { _toolOn = false; ApplyTool(); toolToggle?.SetIsOnWithoutNotify(false); }
    }

    void ToggleTool() { _toolOn = !_toolOn; ApplyTool(); toolToggle?.SetIsOnWithoutNotify(_toolOn); }
    // Goes through the toggle like ToggleSkybox, so its listener also switches the other layers off.
    void ToggleRoom()
    {
        if (roomToggle != null) roomToggle.isOn = !roomToggle.isOn;
        else { _roomOn = !_roomOn; ApplyRoom(); }
    }

    void ApplyRoom()
    {
        if (roomObject != null) roomObject.SetActive(_roomOn);
        SetStatus(roomStatus, _roomOn ? "on" : "off");
        if (skyboxRow   != null) skyboxRow.SetActive(!_roomOn);
        if (gaussianRow != null) gaussianRow.SetActive(!_roomOn);
        RefreshAudioToolRows();
    }

    /// A theme world is on screen: the skybox or 3DGS, with the base room off.
    bool WorldShown => !_roomOn && (_skyboxOn || _gaussianOn);

    void RefreshAudioToolRows()
    {
        bool show = WorldShown;
        if (audioRow    != null) audioRow.SetActive(show);
        if (toolRow     != null) toolRow.SetActive(show);
        if (audioRow == null && audioToggle != null) audioToggle.interactable = show;
        if (toolRow  == null && toolToggle  != null) toolToggle.interactable  = show;
    }

    void OnDestroy()
    {
        Bubble.Spawned -= OnBubbleSpawned;
        roomToggle?.onValueChanged.RemoveAllListeners();
        audioToggle?.onValueChanged.RemoveAllListeners();
        skyboxToggle?.onValueChanged.RemoveAllListeners();
        gaussianToggle?.onValueChanged.RemoveAllListeners();
        toolToggle?.onValueChanged.RemoveAllListeners();
        _toolGltf?.Dispose();
        _targetGltf?.Dispose();
        if (_targetTemplate != null) Destroy(_targetTemplate);
    }

    // ── Audio ─────────────────────────────────────────────────────────────────

    void ApplyAudio()
    {
        if (!_audioOn) { audioController?.FadeOut(); SetStatus(audioStatus, "off"); return; }
        if (audioController == null) { SetStatus(audioStatus, "no controller"); return; }
        string path = FindFile(AudioExtensions);
        if (path == null) { SetStatus(audioStatus, "no file"); return; }
        SetStatus(audioStatus, "on");
        LoadAudioAsync(path);
    }

    async void LoadAudioAsync(string path)
    {
        try { await audioController.LoadAndPlay(path); }
        catch (Exception e) { Debug.LogError($"[FolderEnv] Audio: {e.Message}"); SetStatus(audioStatus, "error"); }
    }

    // ── Skybox ────────────────────────────────────────────────────────────────

    void ApplySkybox()
    {
        if (skyboxLoader == null) { Debug.LogWarning("[FolderEnv] skyboxLoader not assigned."); return; }
        if (_skyboxOn)
        {
            if (!_skyboxLoaded)
            {
                string themeFolder = ThemeSelection.Folder ??
                    (folderConfig != null && !string.IsNullOrWhiteSpace(folderConfig.folderPath) ? folderConfig.ResolvedPath : null);
                if (themeFolder != null) skyboxLoader.folderPath = themeFolder;
                skyboxLoader.enabled = true;
                _skyboxLoaded = true;
            }
            else skyboxLoader.Show();
            // The loader builds the sphere synchronously, so a missing or unreadable image shows up here.
            SetStatus(skyboxStatus, skyboxLoader.HasSphere ? "on" : "no file");
        }
        else { skyboxLoader.Hide(); SetStatus(skyboxStatus, "off"); }
        RefreshAudioToolRows();
    }

    // ── Gaussian ──────────────────────────────────────────────────────────────

    void ApplyGaussian()
    {
        if (gaussianSpawner == null) { Debug.LogWarning("[FolderEnv] gaussianSpawner not assigned."); return; }
        if (_gaussianOn)
        {
            // Reload when the theme folder changed since the last load (e.g. a new theme from Theme Tools).
            if (_gaussianLoaded && _gaussianFolder == CurrentFolder())
            {
                gaussianSpawner.Show();
                SetStatus(gaussianStatus, gaussianSpawner.HasSplat ? "on" : "no file");
            }
            else if (!_gaussianLoading) LoadGaussianAsync();
        }
        else { gaussianSpawner.Hide(); SetStatus(gaussianStatus, "off"); }
        RefreshAudioToolRows();
    }

    // ── Tool + Target ─────────────────────────────────────────────────────────

    string _toolState = "on";   // "on", or what is missing or broken in the theme's tool/target files

    void ApplyTool()
    {
        if (_toolOn)
        {
            if (_toolLoaded)
            {
                SetToolVisible(true);
                _taggedBubbles.Clear();   // re-scan so existing bubbles get targets
                StartTargetScan();
                UpdateTargetLanes();
            }
            else if (!_toolLoading) LoadToolAndTargetAsync();
        }
        else
        {
            SetToolVisible(false);
            StopTargetScan();
            RemoveAllTargets();
            UpdateTargetLanes();
        }
        SetStatus(toolStatus, _toolOn ? (_toolLoaded ? _toolState : "on") : "off");
    }

    void RemoveAllTargets()
    {
        foreach (var t in _activeTargetInstances)
        {
            if (t == null) continue;
            if (t.transform.parent != null && t.transform.parent.TryGetComponent<Bubble>(out var bubble))
                bubble.ClearHitBox();   // back to the ball's size
            Destroy(t);
        }
        _activeTargetInstances.Clear();
        _taggedBubbles.Clear();

        // Restore hidden bubble renderers
        foreach (var kv in _hiddenRenderers)
            foreach (var r in kv.Value)
                if (r) r.enabled = true;
        _hiddenRenderers.Clear();
    }

    void SetToolVisible(bool show)
    {
        _toolInstance1?.SetActive(show);
        _toolInstance2?.SetActive(show);
        SetRenderers(_quad1Renderers, !show);
        SetRenderers(_quad2Renderers, !show);
    }

    static void SetRenderers(Renderer[] renderers, bool enabled)
    {
        if (renderers == null) return;
        foreach (var r in renderers) if (r) r.enabled = enabled;
    }

    async void LoadToolAndTargetAsync()
    {
        try { await DoLoadToolAndTarget(); }
        catch (Exception e) { Debug.LogError($"[FolderEnv] LoadToolAndTarget threw: {e}"); _toolLoading = false; SetStatus(toolStatus, "error"); }
    }

    async System.Threading.Tasks.Task DoLoadToolAndTarget()
    {
        _toolLoading = true;
        string folder = CurrentFolder();

        if (!Directory.Exists(folder))
        {
            Debug.LogError($"[FolderEnv] Folder does not exist: {folder}");
            SetStatus(toolStatus, "folder missing");
            _toolLoading = false;
            return;
        }

        string toolFile   = ThemeFolder.FindTool(folder);
        string targetFile = ThemeFolder.FindTarget(folder);

        bool toolOk = false, targetOk = false;
        if (toolFile != null) toolOk = await PlaceToolFromPath(toolFile);
        else Debug.LogWarning($"[FolderEnv] No tool.glb or tool_*.glb in {folder}");

        if (targetFile != null) targetOk = await PrepareTargetFromPath(targetFile);
        else Debug.LogWarning($"[FolderEnv] No target.glb or target_*.glb in {folder}");

        // The game still plays with plain bubbles and paddles; the status says what is missing.
        _toolState = (toolOk, targetOk) switch
        {
            (true, true)   => "on",
            (false, false) => toolFile == null && targetFile == null ? "no file" : "error",
            (false, true)  => toolFile == null ? "no tool file" : "tool error",
            (true, false)  => targetFile == null ? "no target file" : "target error",
        };
        _toolLoaded  = true;
        _toolLoading = false;
        SetStatus(toolStatus, _toolOn ? _toolState : "off");
        if (_toolOn) StartTargetScan();
        UpdateTargetLanes();
    }

    /// Tells the spawner the target's size while layer 4 is on, so new pairs fall in lanes that fit it.
    void UpdateTargetLanes()
    {
        var spawner = FindAnyObjectByType<BubbleSpawnerFixed>();
        if (spawner == null) return;
        spawner.SetTargetBounds(_toolOn && _targetTemplate != null ? _templateBounds : (Bounds?)null);
    }

    /// Gives a newly spawned bubble its target straight away, rather than at the next scan.
    void OnBubbleSpawned(Bubble bubble)
    {
        if (_targetScan == null || bubble == null || !bubble.CompareTag("Target")) return;
        int id = bubble.gameObject.GetInstanceID();
        if (!_taggedBubbles.Add(id)) return;
        AttachTargetToBubble(bubble.gameObject);
    }

    // ── External GLBs (e.g. a generation tool that just fetched props) ─────────

    /// Places <paramref name="glbPath"/> as the tool, if the tool layer is on.
    public async void LoadToolFromGlb(string glbPath)
    {
        if (!_toolOn || string.IsNullOrEmpty(glbPath)) return;
        try
        {
            await PlaceToolFromPath(glbPath);
            _toolLoaded  = true;
            _toolLoading = false;
            SetStatus(toolStatus, "on");
            UpdateTargetLanes();   // the tool's sweep shapes the lanes
        }
        catch (Exception e) { Debug.LogError($"[FolderEnv] Theme tool: {e}"); _toolLoading = false; }
    }

    /// Uses <paramref name="glbPath"/> as the target visual, if the tool layer is on.
    public async void LoadTargetFromGlb(string glbPath)
    {
        if (!_toolOn || string.IsNullOrEmpty(glbPath)) return;
        try
        {
            await PrepareTargetFromPath(glbPath);
            _taggedBubbles.Clear();
            StartTargetScan();
            UpdateTargetLanes();
        }
        catch (Exception e) { Debug.LogError($"[FolderEnv] Theme target: {e}"); }
    }

    // ── GLTFast placement helpers (used by both folder and Sketchfab paths) ───

    async System.Threading.Tasks.Task<bool> PlaceToolFromPath(string glbPath)
    {
        if (string.IsNullOrEmpty(glbPath) || (toolQuad1 == null && toolQuad2 == null)) return false;

        if (_toolInstance1 != null) { Destroy(_toolInstance1); _toolInstance1 = null; }
        if (_toolInstance2 != null) { Destroy(_toolInstance2); _toolInstance2 = null; }
        _toolGltf?.Dispose();

        _toolGltf = new GltfImport();
        bool ok = await _toolGltf.Load(new Uri(glbPath).AbsoluteUri);
        if (!ok) { Debug.LogError($"[FolderEnv] Tool load failed: {glbPath}"); return false; }

        if (toolQuad1 != null)
        {
            _quad1Renderers = toolQuad1.GetComponentsInChildren<Renderer>(true);
            var c1 = new GameObject("_Tool1");
            c1.transform.SetParent(toolQuad1, worldPositionStays: false);
            c1.transform.localPosition = Vector3.zero;
            c1.transform.localRotation = Quaternion.identity;
            c1.transform.localScale    = Vector3.one;
            await _toolGltf.InstantiateMainSceneAsync(new GameObjectInstantiator(_toolGltf, c1.transform));
            FitAndStandOnQuad(c1, toolQuad1, Quaternion.Euler(toolRotationOffset), toolPivotNormalized);
            c1.AddComponent<KickTool>().Measure();   // the tool hits by its own bounds
            _toolInstance1 = c1;
            _toolInstance1.SetActive(_toolOn);
            SetRenderers(_quad1Renderers, !_toolOn);
        }

        if (toolQuad2 != null)
        {
            _quad2Renderers = toolQuad2.GetComponentsInChildren<Renderer>(true);
            var c2 = new GameObject("_Tool2");
            c2.transform.SetParent(toolQuad2, worldPositionStays: false);
            c2.transform.localPosition = Vector3.zero;
            c2.transform.localRotation = Quaternion.identity;
            c2.transform.localScale    = Vector3.one;
            await _toolGltf.InstantiateMainSceneAsync(new GameObjectInstantiator(_toolGltf, c2.transform));
            FitAndStandOnQuad(c2, toolQuad2, Quaternion.Euler(toolRotationOffset), toolPivotNormalized);
            c2.AddComponent<KickTool>().Measure();
            _toolInstance2 = c2;
            _toolInstance2.SetActive(_toolOn);
            SetRenderers(_quad2Renderers, !_toolOn);
        }

        Debug.Log($"[FolderEnv] Tool placed: {Path.GetFileName(glbPath)}");
        return true;
    }

    async System.Threading.Tasks.Task<bool> PrepareTargetFromPath(string glbPath)
    {
        if (string.IsNullOrEmpty(glbPath)) return false;

        if (_targetTemplate != null) { Destroy(_targetTemplate); _targetTemplate = null; }
        _targetGltf?.Dispose();

        _targetGltf = new GltfImport();
        bool ok = await _targetGltf.Load(new Uri(glbPath).AbsoluteUri);
        if (!ok) { Debug.LogError($"[FolderEnv] Target load failed: {glbPath}"); return false; }

        _targetTemplate = new GameObject("_TargetTemplate");
        _targetTemplate.SetActive(false);
        await _targetGltf.InstantiateMainSceneAsync(
            new GameObjectInstantiator(_targetGltf, _targetTemplate.transform));
        _targetTemplate.SetActive(true);
        _templateBounds = GetWorldBounds(_targetTemplate);
        _targetTemplate.SetActive(false);
        Debug.Log($"[FolderEnv] Target template ready: {Path.GetFileName(glbPath)}  bounds={_templateBounds.size}");
        return true;
    }

    // ── Quad fitting ──────────────────────────────────────────────────────────

    static void FitAndStandOnQuad(GameObject instance, Transform quad, Quaternion rotOffset, float pivotNormalized = 0f)
    {
        // Detach to world root so we can measure at a clean origin
        var parent = instance.transform.parent;
        instance.transform.SetParent(null, worldPositionStays: false);
        instance.transform.position   = Vector3.zero;
        instance.transform.rotation   = rotOffset; // applied before bounds so scale is correct
        instance.transform.localScale = Vector3.one;

        Bounds b = GetWorldBounds(instance);
        Debug.Log($"[FitAndStandOnQuad] {instance.name} raw bounds={b.size}  quad={quad.name} lossyScale={quad.lossyScale}");
        if (b.size.sqrMagnitude < 0.0001f)
        {
            Debug.LogWarning($"[FitAndStandOnQuad] {instance.name} has zero bounds — skipping scale");
            instance.transform.SetParent(parent, worldPositionStays: false);
            return;
        }

        // Scale to fill quad footprint uniformly (use x and y extents for vertical quads)
        float fitX = quad.lossyScale.x > 0.001f ? quad.lossyScale.x / b.size.x : float.MaxValue;
        float fitY = quad.lossyScale.y > 0.001f ? quad.lossyScale.y / b.size.y : float.MaxValue;
        float fitZ = quad.lossyScale.z > 0.001f ? quad.lossyScale.z / b.size.z : float.MaxValue;
        // Pick the two tightest axes (ignore the very-thin axis of a flat quad)
        float[] fits = { fitX, fitY, fitZ };
        System.Array.Sort(fits);
        float s = fits[0]; // smallest = tightest fit
        Debug.Log($"[FitAndStandOnQuad] {instance.name} fitX={fitX:F3} fitY={fitY:F3} fitZ={fitZ:F3}  chosen s={s:F3}");
        instance.transform.localScale = Vector3.one * s;

        // Re-measure after scale
        b = GetWorldBounds(instance);

        // Place: XZ centered on quad, pivot point on quad surface
        // pivotNormalized 0 = bottom of mesh at quad, 1 = top of mesh at quad
        float attachY = Mathf.Lerp(b.min.y, b.max.y, pivotNormalized);
        instance.transform.position = new Vector3(
            quad.position.x - b.center.x,
            quad.position.y - attachY,
            quad.position.z - b.center.z
        );

        // Re-attach keeping world position/scale/rotation (tool stays upright)
        instance.transform.SetParent(parent, worldPositionStays: true);
    }

    static Bounds GetWorldBounds(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(root.transform.position, Vector3.zero);
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    // ── Target bubble scanning ────────────────────────────────────────────────

    void StartTargetScan()
    {
        if (_targetTemplate == null || _targetScan != null) return;
        _targetScan = StartCoroutine(TargetScanCoroutine());
    }

    void StopTargetScan()
    {
        if (_targetScan == null) return;
        StopCoroutine(_targetScan);
        _targetScan = null;
    }

    IEnumerator TargetScanCoroutine()
    {
        while (true)
        {
            var found = GameObject.FindGameObjectsWithTag("Target");
            if (found.Length > 0) Debug.Log($"[FolderEnv] Target scan: {found.Length} tagged objects");
            foreach (var bubble in found)
            {
                if (bubble == null) continue;
                int id = bubble.GetInstanceID();
                if (_taggedBubbles.Contains(id)) continue;
                _taggedBubbles.Add(id);
                AttachTargetToBubble(bubble);
            }
            yield return new WaitForSeconds(0.3f);
        }
    }

    void AttachTargetToBubble(GameObject bubble)
    {
        // Hide the bubble's own renderers before adding child (get them first)
        var ownRenderers = bubble.GetComponents<Renderer>();
        if (ownRenderers.Length == 0)
            ownRenderers = bubble.GetComponentsInChildren<Renderer>();
        foreach (var r in ownRenderers) r.enabled = false;
        _hiddenRenderers[bubble.GetInstanceID()] = ownRenderers;

        var instance = Instantiate(_targetTemplate, bubble.transform);
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;
        instance.transform.localScale    = Vector3.one;

        // Scale to match bubble world size, accounting for bubble's lossyScale
        float bubbleLossy = bubble.transform.lossyScale.x;
        if (_templateBounds.size.x > 0f && bubbleLossy > 0f)
        {
            float bubbleSize = ownRenderers.Length > 0 ? ownRenderers[0].bounds.size.x
                                                       : bubbleLossy;
            float s = bubbleSize / (_templateBounds.size.x * bubbleLossy);
            instance.transform.localScale = Vector3.one * s;
        }

        instance.SetActive(true);
        _activeTargetInstances.Add(instance);

        // Hits use the target's own bounds (around the bubble origin), not the ball's sphere.
        if (bubble.TryGetComponent<Bubble>(out var b))
        {
            Bounds box = GetWorldBounds(instance);
            if (box.size.sqrMagnitude > 0f)
                b.SetHitBox(new Bounds(box.center - bubble.transform.position, box.size));
        }
    }

    /// Plays the next theme by reloading the scene, so every layer starts clean, then shows its skybox.
    void NextTheme()
    {
        string root = Application.streamingAssetsPath;
        var themes = ThemeFolder.ListThemes(Path.Combine(root, "SampleThemes"), Path.Combine(root, "Themes"));
        string next = ThemeFolder.Next(themes, CurrentFolder());
        if (next == null) return;
        ThemeSelection.Folder = next;
        _showThemeAfterReload = true;
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    // ── Gaussian loading ──────────────────────────────────────────────────────

    /// The theme folder to play: Theme Studio's selection if set, else the FolderConfig asset.
    string CurrentFolder() => ThemeSelection.Resolve(folderConfig);

    /// Order: Editor-imported .asset under Assets/ (Editor only) → prebaked .asset + .bytes files
    /// (e.g. StreamingAssets sample themes; Editor and builds) → .ply converted at runtime (Editor
    /// and builds) → the splat already assigned in the scene.
    async void LoadGaussianAsync()
    {
        string folder = CurrentFolder();
        if (!Directory.Exists(folder))
        {
            Debug.LogWarning($"[FolderEnv] Folder not found: {folder}");
            SetStatus(gaussianStatus, "no file");
            return;
        }
        _gaussianLoading = true;
        try
        {
#if UNITY_EDITOR
            string[] assetFiles = Directory.GetFiles(folder, "*.asset", SearchOption.TopDirectoryOnly);
            if (assetFiles.Length > 0)
            {
                string dbPath = ToAssetDatabasePath(assetFiles[0]);
                var asset = dbPath == null ? null : UnityEditor.AssetDatabase
                    .LoadAssetAtPath<GaussianSplatting.Runtime.GaussianSplatAsset>(dbPath);
                if (asset != null)
                {
                    gaussianSpawner.SpawnFromAsset(asset);
                    FinishGaussianLoad(folder);
                    return;
                }
            }
#endif
            // Progress callbacks arrive late, so ignore any that land after the load has finished.
            var progress = new Progress<float>(p => { if (_gaussianLoading) SetStatus(gaussianStatus, $"loading {p:P0}"); });
            string prebaked = RuntimeSplatLoader.FindPrebaked(folder);
            if (prebaked != null)
            {
                SetStatus(gaussianStatus, "loading…");
                if (await gaussianSpawner.SpawnFromPrebaked(prebaked, progress)) { FinishGaussianLoad(folder); return; }
                SetStatus(gaussianStatus, "error");
                return;
            }

            string ply = FindPly(folder);
            if (ply != null)
            {
                SetStatus(gaussianStatus, "loading…");
                if (await gaussianSpawner.SpawnFromPly(ply, progress)) { FinishGaussianLoad(folder); return; }
                SetStatus(gaussianStatus, "error");
                return;
            }

            gaussianSpawner.ActivatePrewarmed();
            FinishGaussianLoad(folder);
            if (!gaussianSpawner.HasSplat)
            {
                Debug.LogWarning($"[FolderEnv] No scene.asset, prebaked splat or .ply in {folder}");
                if (_gaussianOn) SetStatus(gaussianStatus, "no file");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[FolderEnv] 3DGS load failed: {e.Message}");
            SetStatus(gaussianStatus, "error");
        }
        finally { _gaussianLoading = false; }
    }

    void FinishGaussianLoad(string folder)
    {
        _gaussianLoaded = true;
        _gaussianFolder = folder;
        if (!_gaussianOn) { gaussianSpawner.Hide(); SetStatus(gaussianStatus, "off"); }
        else              SetStatus(gaussianStatus, "on");
    }

    /// Prefers the final DreamScene360 iteration, then any .ply (top level first).
    static string FindPly(string folder)
    {
        string exact = Path.Combine(folder, "point_cloud", "iteration_30000", "point_cloud.ply");
        if (File.Exists(exact)) return exact;
        string[] top = Directory.GetFiles(folder, "*.ply", SearchOption.TopDirectoryOnly);
        if (top.Length > 0) { Array.Sort(top, StringComparer.OrdinalIgnoreCase); return top[0]; }
        string[] any = Directory.GetFiles(folder, "*.ply", SearchOption.AllDirectories);
        if (any.Length > 0) { Array.Sort(any, StringComparer.OrdinalIgnoreCase); return any[0]; }
        return null;
    }

#if UNITY_EDITOR
    static string ToAssetDatabasePath(string absolutePath)
    {
        string n = absolutePath.Replace('\\', '/');
        int i = n.IndexOf("/Assets/");
        return i >= 0 ? n.Substring(i + 1) : null;
    }
#endif

    string FindFile(string[] extensions)
    {
        string folder = CurrentFolder();
        if (!Directory.Exists(folder)) return null;
        foreach (string ext in extensions)
        {
            string[] files = Directory.GetFiles(folder, "*" + ext, SearchOption.TopDirectoryOnly);
            if (files.Length > 0) return files[0];
        }
        return null;
    }

    static void SetStatus(TMP_Text label, string text) { if (label) label.text = text; }
}
