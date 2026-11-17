using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// Theme Tools — one panel, two tabs (theme word, output folder, settings and log are shared):
///   • Audio / Visual — an LLM expands the theme word into visual/audio prompts; the Reverie
///     server (server/reverie_server.py) bakes panorama + 3DGS + audio; results are downloaded.
///   • Tool / Target  — an LLM picks a tool/target prop pair; Sketchfab supplies the GLBs.
///
/// The server can be on this machine (http://localhost:8000, e.g. WSL2), another machine
/// (URL + token) or behind an SSH tunnel; set server.url / server.token in api_config.json.
/// Without a server, the Tool / Target tab and the bundled themes still work.
///
/// Draws with plain GUILayout so the same code runs inside an EditorWindow
/// (Window > Reverie > Theme Tools) and in a player build (ThemeToolsPanel, F1).
/// Everything is written to one theme folder in the layout FolderEnvironmentLoader reads:
///   panorama.jpg, audio.wav, scene.ply, tool_*.glb, target_*.glb
/// scene.ply is converted to splats at runtime (RuntimeSplatLoader), in the Editor and in builds.
[Serializable]
public class ThemeToolsCore
{
    // What the server bakes. DreamScene360 makes the panorama as part of 3DGS training, so there is
    // no panorama-only mode; "from pano" trains 3DGS from an existing/uploaded panorama instead.
    public enum BakeMode { Full, SceneOnly, AudioOnly, SceneFromPanorama, SceneFromPanoramaAudio }
    public const string DefaultServerUrl = "http://localhost:8000";

    // ── Settings ──────────────────────────────────────────────────────────────
    public string   theme      = "";
    public string   serverUrl  = DefaultServerUrl;
    [NonSerialized] public string serverToken;    // REVERIE_TOKEN of the server; from api_config.json
    [NonSerialized] public string sketchfabToken; // from api_config.json or SKETCHFAB_TOKEN
    [NonSerialized] public string llmApiKey;      // placeholder for a hosted LLM; not sent (the study's Llama is local)
    public bool     overwrite;                   // rebake stages that already exist on the server
    // Local Llama (as in the study). Ollama serves an OpenAI-compatible API on port 11434.
    public string   llmUrl     = "http://localhost:11434/v1/chat/completions";
    public string   llmModel   = "llama3.1:latest";
    public float    temperature = 0.4f;            // prompt expansion, as in the study
    public int      seed        = -1;              // -1 = no fixed seed (as in the study)
    public BakeMode bakeMode   = BakeMode.Full;
    [Tooltip("Theme folders are created here. Relative paths resolve against the project root.")]
    public string   outputRoot = "Assets/StreamingAssets/Themes";

    // 0 = Audio / Visual (prompt expansion + bake), 1 = Tool / Target (Sketchfab GLBs)
    public int tab;
    static readonly string[] TabNames = { "Audio / Visual", "Tool / Target" };

    // ── Results (editable in the panel) ───────────────────────────────────────
    public string setting = "", visualPrompt = "", audioPrompt = "";
    public string tool = "", target = "", reasoning = "";
    public string lastOutputFolder = "";

    public bool Busy       { get; private set; } // LLM / Sketchfab / bake submit in flight
    public bool Monitoring { get; private set; } // bake job being polled + downloaded

    /// Raised with the theme folder whenever files land on disk (editor host refreshes AssetDatabase).
    public event Action<string> FilesWritten;

    readonly List<string>         _log    = new();
    readonly List<UnityWebRequest> _active = new();
    UnityWebRequest _progressReq;
    int     _gen;          // bumped by Cancel(); ops started under an older value stop
    bool    _configLoaded;
    bool    _showSettings;
    bool    _showKeys;
    Vector2 _scroll, _logScroll;
    GUIStyle _wrapArea, _wrapLabel, _bold, _hint;

    static readonly string[] BakeModeNames = { "Full", "Pano+3DGS", "Audio", "3DGS from pano", "3DGS from pano+Audio" };

    bool NeedVisual       => bakeMode is BakeMode.Full or BakeMode.SceneOnly;
    bool NeedAudio        => bakeMode is BakeMode.Full or BakeMode.AudioOnly or BakeMode.SceneFromPanoramaAudio;
    bool BakeScene        => bakeMode != BakeMode.AudioOnly;
    bool BakeFromPanorama => bakeMode is BakeMode.SceneFromPanorama or BakeMode.SceneFromPanoramaAudio;

    // ─────────────────────────────────────────────────────────────────────────
    //  GUI
    // ─────────────────────────────────────────────────────────────────────────

    /// <param name="hostButtons">Extra host-specific buttons drawn under the output folder.</param>
    public void DrawGUI(Action hostButtons = null)
    {
        _wrapArea  ??= new GUIStyle(GUI.skin.textArea) { wordWrap = true };
        _wrapLabel ??= new GUIStyle(GUI.skin.label)    { wordWrap = true };
        _bold      ??= new GUIStyle(GUI.skin.label)    { fontStyle = FontStyle.Bold };

        _scroll = GUILayout.BeginScrollView(_scroll);
        bool idle = !Busy;

        GUILayout.Label("Theme word", _bold);
        GUI.enabled = idle;
        theme = GUILayout.TextField(theme);

        GUI.enabled = true;
        GUILayout.Space(6);
        tab = GUILayout.Toolbar(tab, TabNames, GUILayout.Height(24));
        GUILayout.Space(6);

        GUI.enabled = idle;
        if (tab == 0) DrawAudioVisualTab(idle);
        else          DrawToolTargetTab(idle);
        GUI.enabled = true;

        if (_progressReq != null)
            GUILayout.Label($"Downloading… {_progressReq.downloadProgress:P0}");
        if (Busy || Monitoring)
            if (GUILayout.Button("Cancel")) Cancel();

        if (!string.IsNullOrEmpty(lastOutputFolder))
        {
            GUILayout.Space(4);
            GUILayout.Label($"Output: {lastOutputFolder}", _wrapLabel);
            hostButtons?.Invoke();
        }

        GUILayout.Space(6);
        _showKeys = GUILayout.Toggle(_showKeys, KeysTitle());
        if (_showKeys) DrawApiKeys();
        _showSettings = GUILayout.Toggle(_showSettings, " Settings");
        if (_showSettings) DrawSettings();

        GUILayout.Space(6);
        GUILayout.Label("Log", _bold);
        _logScroll = GUILayout.BeginScrollView(_logScroll, GUI.skin.box, GUILayout.Height(150));
        GUILayout.Label(string.Join("\n", _log), _wrapLabel);
        GUILayout.EndScrollView();

        GUILayout.EndScrollView();
    }

    // ── Tab 0: prompt expansion for audio/visual generation + bake ────────────
    void DrawAudioVisualTab(bool idle)
    {
        GUI.enabled = idle && !Monitoring; // download step reads bakeMode while polling
        GUILayout.Label("Bake mode", _bold);
        bakeMode = (BakeMode)GUILayout.SelectionGrid((int)bakeMode, BakeModeNames, 3);
        GUILayout.Label(BakeModeHint(bakeMode), _wrapLabel);

        GUI.enabled = idle && !string.IsNullOrWhiteSpace(theme) && (NeedVisual || NeedAudio);
        if (GUILayout.Button("1. Expand Prompts with Llama", GUILayout.Height(30)))
            Run(ExpandPrompts);
        GUI.enabled = idle;

        if (!string.IsNullOrEmpty(setting)) GUILayout.Label($"Setting: {setting}", _wrapLabel);
        if (NeedVisual)
        {
            GUILayout.Label("Visual prompt (panorama / 3DGS)", _bold);
            visualPrompt = GUILayout.TextArea(visualPrompt, _wrapArea, GUILayout.Height(64));
        }
        if (NeedAudio)
        {
            GUILayout.Label("Audio prompt", _bold);
            audioPrompt = GUILayout.TextArea(audioPrompt, _wrapArea, GUILayout.Height(48));
        }

        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        GUI.enabled = idle && !Monitoring;
        if (GUILayout.Button("Check server", GUILayout.Width(110))) Run(CheckServer);
        overwrite = GUILayout.Toggle(overwrite, " Rebake existing");
        GUILayout.EndHorizontal();
        GUI.enabled = idle && CanBake();
        if (GUILayout.Button("2. Bake on Server + Download", GUILayout.Height(28))) Run(BakeAndDownload);
        GUI.enabled = true;
    }

    // ── Tab 1: tool/target suggestion + Sketchfab GLB download ────────────────
    void DrawToolTargetTab(bool idle)
    {
        GUI.enabled = idle && !string.IsNullOrWhiteSpace(theme);
        if (GUILayout.Button("1. Suggest Tool / Target with Llama", GUILayout.Height(30)))
            Run(SuggestToolTarget);
        GUI.enabled = idle;

        GUILayout.BeginHorizontal();
        GUILayout.Label("Tool", GUILayout.Width(44));   tool   = GUILayout.TextField(tool);
        GUILayout.Label("Target", GUILayout.Width(48)); target = GUILayout.TextField(target);
        GUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(reasoning)) GUILayout.Label(reasoning, _wrapLabel);

        GUILayout.Space(6);
        bool haveProps = !string.IsNullOrWhiteSpace(tool) && !string.IsNullOrWhiteSpace(target);
        GUI.enabled = idle && haveProps && !string.IsNullOrWhiteSpace(theme);
        if (GUILayout.Button("2. Fetch Tool/Target GLBs from Sketchfab", GUILayout.Height(28))) Run(FetchToolTarget);
        GUI.enabled = true;
    }

    void DrawSettings()
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUI.enabled = !Busy && !Monitoring;
        GUILayout.Label("Reverie server URL (this machine, a LAN host, or an SSH-tunnelled port)");
        serverUrl = GUILayout.TextField(serverUrl);
        GUILayout.Label("LLM endpoint (OpenAI-compatible chat/completions)");
        llmUrl = GUILayout.TextField(llmUrl);
        if (!IsLocal(llmUrl))
            GUILayout.Label("⚠ Reverie uses a local Llama: enter an address on this machine.", _wrapLabel);
        GUILayout.Label("LLM model");
        llmModel = GUILayout.TextField(llmModel);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Temperature", GUILayout.Width(80));
        if (float.TryParse(GUILayout.TextField(temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), GUILayout.Width(50)),
                           System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float tv))
            temperature = Mathf.Clamp(tv, 0f, 2f);
        GUILayout.Label("Seed (-1 = none)", GUILayout.Width(100));
        if (int.TryParse(GUILayout.TextField(seed.ToString(), GUILayout.Width(70)), out int sv)) seed = sv;
        GUILayout.EndHorizontal();
        GUILayout.Label("Output root");
        outputRoot = GUILayout.TextField(outputRoot);
        GUI.enabled = true;
        GUILayout.EndVertical();
    }

    // ── API keys: placeholders, where to get each key, and a per-machine save ──
    string KeysTitle() => " API keys" + (string.IsNullOrEmpty(sketchfabToken) ? "  (Sketchfab token missing)" : "");

    void DrawApiKeys()
    {
        if (!_configLoaded && !ApiConfig.StreamingNeedsWebRequest) _ = Config(_gen); // fill the fields once
        GUILayout.BeginVertical(GUI.skin.box);
        GUI.enabled = !Busy && !Monitoring;
        GUILayout.Label("Keys stay on this machine: they are saved outside the project and never committed or built into a player.", _wrapLabel);

        sketchfabToken = SecretField("Sketchfab API token", "paste your Sketchfab token here",
            "Needed for 2. Fetch Tool/Target GLBs. Free account: sketchfab.com → Settings → Password & API → API token. " +
            "Or set the SKETCHFAB_TOKEN environment variable.", sketchfabToken);
        serverToken = SecretField("Reverie server token", "blank if the server has no token",
            "The REVERIE_TOKEN the generation server was started with (docs/SERVER.md). Needed for 2. Bake on Server.", serverToken);
        llmApiKey = SecretField("LLM API key (not used yet)", "not needed for local Ollama",
            "Placeholder for a hosted LLM. Reverie runs Llama 3.1 locally through Ollama, which needs no key: " +
            "install Ollama, then run `ollama pull llama3.1`.", llmApiKey);

        if (GUILayout.Button("Save keys on this machine"))
        {
            ApiConfig.SaveOverride(new JObject
            {
                ["server"]    = new JObject { ["url"] = serverUrl, ["token"] = serverToken ?? "" },
                ["sketchfab"] = new JObject { ["api_token"] = sketchfabToken ?? "" },
                ["ollama"]    = new JObject { ["api_key"] = llmApiKey ?? "" },
            });
            _configLoaded = false;
            Log($"Saved to {ApiConfig.OverridePath}");
        }
        GUILayout.Label("Or copy StreamingAssets/api_config.example.json to api_config.json (gitignored) and fill it in.", _wrapLabel);
        GUI.enabled = true;
        GUILayout.EndVertical();
    }

    string SecretField(string label, string placeholder, string help, string value)
    {
        GUILayout.Label(label, _bold);
        value = GUILayout.PasswordField(value ?? "", '*');
        if (string.IsNullOrEmpty(value))
        {
            var hint = GUILayoutUtility.GetLastRect();
            GUI.Label(new Rect(hint.x + 4, hint.y, hint.width - 4, hint.height), placeholder,
                      _hint ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic, normal = { textColor = Color.gray } });
        }
        GUILayout.Label(help, _wrapLabel);
        GUILayout.Space(4);
        return value;
    }

    static string BakeModeHint(BakeMode m) => m switch {
        BakeMode.Full                   => "Panorama + 3DGS + audio (~2.5 h on an A100)",
        BakeMode.SceneOnly              => "Panorama + 3DGS, no audio",
        BakeMode.AudioOnly              => "Audio only (~3 min)",
        BakeMode.SceneFromPanorama      => "3DGS from this theme's panorama.jpg (uploaded if you have it locally)",
        BakeMode.SceneFromPanoramaAudio => "3DGS from this theme's panorama.jpg, plus audio",
        _                               => ""
    };

    bool CanBake() => !Monitoring && !string.IsNullOrWhiteSpace(theme)
                      && (!NeedVisual || !string.IsNullOrWhiteSpace(visualPrompt))
                      && (!NeedAudio  || !string.IsNullOrWhiteSpace(audioPrompt));

    // ─────────────────────────────────────────────────────────────────────────
    //  Operations
    // ─────────────────────────────────────────────────────────────────────────

    async void Run(Func<int, Task> op)
    {
        try { await op(_gen); }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception e) { Log($"ERROR: {e.Message}"); Debug.LogException(e); }
    }

    public void Cancel()
    {
        _gen++;
        foreach (var r in _active.ToArray()) r.Abort();
        Busy = Monitoring = false;
    }

    public string ThemeFolder()
    {
        string root = string.IsNullOrWhiteSpace(outputRoot) ? "Assets/StreamingAssets/Themes" : outputRoot;
        if (!Path.IsPathRooted(root))
            root = Path.Combine(Path.GetDirectoryName(Application.dataPath), root);
        return Path.GetFullPath(Path.Combine(root, ToSlug(theme.Trim())));
    }

    // ── Audio / Visual tab: expand theme word into visual/audio prompts ──────
    async Task ExpandPrompts(int gen)
    {
        Busy = true;
        try
        {
            string t = theme.Trim();
            Log($"Expanding '{t}' via local Llama ({llmModel})…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var p = JObject.Parse(await Chat(PromptSystem(NeedVisual, NeedAudio), t, true, temperature, gen));
            sw.Stop();
            setting = p["setting"]?.ToString() ?? t;
            if (NeedVisual) visualPrompt = p["visualDescription"]?.ToString() ?? $"Photorealistic environment: {setting}.";
            if (NeedAudio)  audioPrompt  = p["audioDescription"]?.ToString()  ?? $"Ambient sounds of {setting}.";
            Log($"Setting: {setting}");

            // Provenance: what produced these prompts.
            string outDir = ThemeFolder();
            Directory.CreateDirectory(outDir);
            lastOutputFolder = outDir;
            var record = new JObject
            {
                ["theme"] = t, ["created_at"] = DateTime.Now.ToString("s"),
                ["llm_endpoint"] = llmUrl, ["model"] = llmModel,
                ["temperature"] = temperature, ["seed"] = seed >= 0 ? (JToken)seed : JValue.CreateNull(),
                ["setting"] = setting, ["visual_prompt"] = visualPrompt, ["audio_prompt"] = audioPrompt,
                ["wall_clock_s"] = Math.Round(sw.Elapsed.TotalSeconds, 2),
            };
            File.WriteAllText(Path.Combine(outDir, "llm_expansion.json"), record.ToString(Newtonsoft.Json.Formatting.Indented));
            FilesWritten?.Invoke(outDir);
            Log("Done. Edit the prompts if needed, then Bake.");
        }
        finally { Busy = false; }
    }

    // ── Tool / Target tab: Llama picks a tool/target prop pair ────────────────
    async Task SuggestToolTarget(int gen)
    {
        Busy = true;
        try
        {
            string t = theme.Trim();
            Log("Asking Llama for a tool/target pair…");
            string raw = await Chat(ToolTargetSystem, ToolTargetUser(t), false, null, gen);
            // Chain-of-thought: the model reasons before the JSON — take the last {...} found.
            raw = Regex.Replace(raw, @"```json|```", "").Trim();
            var matches = Regex.Matches(raw, @"\{[^{}]*\}");
            if (matches.Count == 0) throw new Exception($"Llama returned non-JSON output:\n{raw}");
            var tt = JObject.Parse(matches[matches.Count - 1].Value);
            string tl = tt["tool"]?.ToString().Trim(), tg = tt["target"]?.ToString().Trim();
            if (string.IsNullOrWhiteSpace(tl) || string.IsNullOrWhiteSpace(tg))
                throw new Exception($"Llama response missing tool/target fields:\n{raw}");
            tool = tl; target = tg;
            reasoning = tt["reasoning"]?.ToString().Trim() ?? "";
            Log($"tool=\"{tool}\"  target=\"{target}\"");
            Log("Done. Edit tool/target if needed, then Fetch.");
        }
        finally { Busy = false; }
    }

    // ── 2. Sketchfab tool/target GLBs ─────────────────────────────────────────
    async Task FetchToolTarget(int gen)
    {
        Busy = true;
        try
        {
            await Config(gen);
            string token = sketchfabToken;
            if (string.IsNullOrEmpty(token))
                throw new Exception("No Sketchfab token. Open \"API keys\" below, paste it, and click Save keys on this machine.");

            string outDir = ThemeFolder();
            Directory.CreateDirectory(outDir);
            lastOutputFolder = outDir;

            foreach (var (role, query) in new[] { ("tool", tool.Trim()), ("target", target.Trim()) })
            {
                JObject best;
                if (Regex.IsMatch(query, "^[0-9a-f]{32}$"))
                {
                    // Exact Sketchfab model UID (e.g. to fetch the models used in the study).
                    Log($"Sketchfab: model {query} ({role})…");
                    best = await GetJson($"https://api.sketchfab.com/v3/models/{query}", "Token " + token, gen);
                    if (best == null) { Log($"  model {query} not found — skipped."); continue; }
                }
                else
                {
                    Log($"Sketchfab: searching \"{query}\" ({role})…");
                    // /v3/search?type=models does real keyword search; restricted=false keeps it SFW.
                    string searchUrl = "https://api.sketchfab.com/v3/search?type=models" +
                                       $"&q={Uri.EscapeDataString(query)}&downloadable=true&restricted=false&count=24";
                    var search = await GetJson(searchUrl, "Token " + token, gen) ?? throw new Exception($"Sketchfab search failed for \"{query}\".");
                    var models = (search["results"] as JArray)?.OfType<JObject>().ToList();
                    if (models == null || models.Count == 0) { Log($"  no downloadable models for \"{query}\" — skipped."); continue; }
                    best = PickBestMatch(models, query);
                    // Search results omit the slug, so their viewerUrl reads .../none-<uid>; the
                    // model endpoint has the canonical url, display name and license slug.
                    best = await GetJson($"https://api.sketchfab.com/v3/models/{best["uid"]}", "Token " + token, gen) ?? best;
                }
                string name = best["name"]?.ToString() ?? best["uid"]?.ToString();
                var dl = await GetJson($"https://api.sketchfab.com/v3/models/{best["uid"]}/download", "Token " + token, gen);
                string glbUrl = dl?["glb"]?["url"]?.ToString();
                if (string.IsNullOrEmpty(glbUrl)) { Log($"  \"{name}\" has no GLB download (may need Pro) — skipped."); continue; }

                // Download first, then replace any previous model for this role (the loader takes
                // the first {role}_*.glb, so a leftover would shadow the new one).
                string path = Path.Combine(outDir, $"{role}_{SanitizeFilename(name)}.glb");
                if (!await DownloadFile(glbUrl, path + ".part", null, 300, gen)) continue;
                foreach (var old in Directory.GetFiles(outDir, $"{role}_*.glb")) File.Delete(old);
                File.Move(path + ".part", path);
                Log($"  saved {Path.GetFileName(path)} ({FormatBytes(new FileInfo(path).Length)})");
                RecordSketchfab(outDir, role, query, best, Path.GetFileName(path));
                FilesWritten?.Invoke(outDir);
            }
        }
        finally { Busy = false; _progressReq = null; }
    }

    /// Attribution record (Sketchfab models are mostly CC licences that require credit).
    static void RecordSketchfab(string outDir, string role, string query, JObject model, string file)
    {
        string recordPath = Path.Combine(outDir, "sketchfab.json");
        JObject record;
        try { record = File.Exists(recordPath) ? JObject.Parse(File.ReadAllText(recordPath)) : new JObject(); }
        catch { record = new JObject(); }
        record[role] = new JObject
        {
            ["query"] = query, ["file"] = file, ["uid"] = model["uid"], ["name"] = model["name"],
            ["author"] = model["user"]?["displayName"] ?? model["user"]?["username"],
            ["url"] = model["viewerUrl"],
            ["license"] = LicenseCode(model["license"]),
            ["downloaded_at"] = DateTime.Now.ToString("s"),
        };
        File.WriteAllText(recordPath, record.ToString(Newtonsoft.Json.Formatting.Indented));
    }

    /// Sketchfab labels a licence "CC Attribution"; the credit files use the code form.
    static readonly Dictionary<string, string> LicenseCodes = new Dictionary<string, string>
    {
        ["by"] = "CC-BY-4.0", ["by-sa"] = "CC-BY-SA-4.0", ["by-nd"] = "CC-BY-ND-4.0",
        ["by-nc"] = "CC-BY-NC-4.0", ["by-nc-sa"] = "CC-BY-NC-SA-4.0",
        ["by-nc-nd"] = "CC-BY-NC-ND-4.0", ["cc0"] = "CC0-1.0",
    };

    static JToken LicenseCode(JToken license)
    {
        string slug = license?["slug"]?.ToString();
        if (slug != null && LicenseCodes.TryGetValue(slug, out string code)) return code;
        return license?["label"] ?? license;
    }

    // ── 3. Reverie server (server/reverie_server.py): submit, poll, download ──
    string ServerBase => (serverUrl ?? DefaultServerUrl).Trim().TrimEnd('/');
    string ServerAuth => string.IsNullOrEmpty(serverToken) ? null : "Bearer " + serverToken;

    async Task CheckServer(int gen)
    {
        Busy = true;
        try
        {
            await Config(gen);
            var h = await GetJson($"{ServerBase}/health", null, gen)
                ?? throw new Exception($"No Reverie server at {ServerBase}. Start it with `python reverie_server.py` " +
                                       "on the GPU machine, or check the URL / SSH tunnel (docs/REPLICATION_GUIDE.md).");
            Log($"Server OK — GPU: {h["gpu"]}; running: {Names(h["running"])}; queued: {Names(h["queued"])}");
            if (h["token_required"]?.Value<bool>() == true)
                Log(await GetJson($"{ServerBase}/themes", ServerAuth, gen) != null
                    ? "Token accepted."
                    : "Server requires a token: set server.token in api_config.json (or Settings) to its REVERIE_TOKEN.");
        }
        finally { Busy = false; }
    }

    static string Names(JToken arr) => arr is JArray a && a.Count > 0 ? string.Join(", ", a) : "none";

    async Task BakeAndDownload(int gen)
    {
        Busy = true;
        try
        {
            await Config(gen);
            string slug   = ToSlug(theme.Trim());
            string outDir = ThemeFolder();
            Directory.CreateDirectory(outDir);
            lastOutputFolder = outDir;
            string localPano = Path.Combine(outDir, "panorama.jpg");

            if (BakeFromPanorama && File.Exists(localPano))
            {
                Log("Uploading this theme's panorama.jpg to the server…");
                using var up = new UnityWebRequest($"{ServerBase}/themes/{slug}/panorama", "PUT")
                {
                    uploadHandler   = new UploadHandlerRaw(File.ReadAllBytes(localPano)),
                    downloadHandler = new DownloadHandlerBuffer()
                };
                up.SetRequestHeader("Content-Type", "application/octet-stream");
                if (ServerAuth != null) up.SetRequestHeader("Authorization", ServerAuth);
                await Send(up, 300, gen);
                if (up.result != UnityWebRequest.Result.Success) throw new Exception(ServerError(up, "Panorama upload"));
            }

            var body = new JObject
            {
                ["prompt"]                = NeedVisual ? visualPrompt.Trim() : "",
                ["audio_prompt"]          = NeedAudio  ? audioPrompt.Trim()  : "",
                ["scene"]                 = BakeScene,
                ["audio"]                 = NeedAudio,
                ["use_existing_panorama"] = BakeFromPanorama,
                ["overwrite"]             = overwrite,
            };
            Log($"Submitting '{BakeModeNames[(int)bakeMode]}' bake for '{slug}' to {ServerBase}…");
            using (var req = PostJson($"{ServerBase}/themes/{slug}/bake", body.ToString(), serverToken))
            {
                await Send(req, 30, gen);
                if (req.result != UnityWebRequest.Result.Success) throw new Exception(ServerError(req, "Bake request"));
                var note = JObject.Parse(req.downloadHandler.text)["note"];
                if (note != null) Log(note.ToString());
            }

            Busy = false;
            Monitoring = true;
            Log("Monitoring the bake (you can fetch props meanwhile)…");
            string statusUrl = $"{ServerBase}/themes/{slug}/status";
            DateTime deadline = DateTime.UtcNow.AddHours(8);
            int misses = 0;
            while (DateTime.UtcNow < deadline)
            {
                var st = await GetJson(statusUrl, ServerAuth, gen);
                if (st == null)
                {
                    if (++misses > 60) throw new Exception("No answer from the server for 5 minutes.");
                    SetLastLog("Waiting for server…");
                }
                else
                {
                    misses = 0;
                    string status = st["status"]?.ToString() ?? "";
                    float  prog   = st["progress"]?.Value<float>() ?? 0f;
                    SetLastLog(status == "queued" ? $"Queued on server (position {st["queue_position"]})…"
                                                  : $"{st["stage"]}  {prog:P0}");
                    if (status == "failed") throw new Exception($"Bake failed on the server:\n{st["error"]}");
                    if (status == "done") { await DownloadResults(slug, outDir, gen); return; }
                }
                await Task.Delay(5000);
                if (gen != _gen) throw new OperationCanceledException();
            }
            throw new Exception("Bake did not finish within 8 hours.");
        }
        finally { Busy = false; Monitoring = false; _progressReq = null; }
    }

    async Task DownloadResults(string slug, string outDir, int gen)
    {
        if (BakeScene)
        {
            Log("Downloading panorama…");
            await ServerDownload(slug, "panorama", Path.Combine(outDir, "panorama.jpg"), 300, gen);
            Log("Downloading 3DGS scene.ply (large)…");
            await ServerDownload(slug, "ply", Path.Combine(outDir, "scene.ply"), 0, gen);
        }
        if (NeedAudio)
        {
            Log("Downloading audio…");
            await ServerDownload(slug, "audio", Path.Combine(outDir, "audio.wav"), 300, gen);
        }
        await ServerDownload(slug, "meta", Path.Combine(outDir, "bake_meta.json"), 60, gen);
        FilesWritten?.Invoke(outDir);
        Log($"[{slug}] done → {outDir}");
    }

    /// Downloads to a .part file first so a failed download never replaces a good file.
    async Task ServerDownload(string slug, string asset, string path, int timeoutS, int gen)
    {
        string part = path + ".part";
        if (!await DownloadFile($"{ServerBase}/themes/{slug}/{asset}", part, ServerAuth, timeoutS, gen))
            throw new Exception($"Downloading {asset} from the server failed.");
        if (File.Exists(path)) File.Delete(path);
        File.Move(part, path);
    }

    static string ServerError(UnityWebRequest req, string what)
    {
        string detail = req.downloadHandler?.text;
        try { detail = JObject.Parse(detail)["detail"]?.ToString() ?? detail; } catch { }
        return req.responseCode == 401
            ? $"{what}: the server needs a token — set server.token in api_config.json (or Settings) to its REVERIE_TOKEN."
            : $"{what} failed ({req.responseCode} {req.error}): {detail}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  HTTP (UnityWebRequest so it works in editor and on device alike)
    // ─────────────────────────────────────────────────────────────────────────

    async Task Send(UnityWebRequest req, int timeoutS, int gen)
    {
        if (gen != _gen) throw new OperationCanceledException();
        req.timeout = timeoutS;
        var tcs = new TaskCompletionSource<bool>();
        _active.Add(req);
        req.SendWebRequest().completed += _ => tcs.TrySetResult(true);
        try { await tcs.Task; }
        finally { _active.Remove(req); }
        if (gen != _gen) throw new OperationCanceledException();
    }

    static UnityWebRequest PostJson(string url, string json, string bearer)
    {
        var req = new UnityWebRequest(url, "POST")
        {
            uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
            downloadHandler = new DownloadHandlerBuffer()
        };
        req.SetRequestHeader("Content-Type", "application/json");
        if (!string.IsNullOrEmpty(bearer)) req.SetRequestHeader("Authorization", "Bearer " + bearer);
        return req;
    }

    /// <param name="authorization">Full Authorization header value ("Token …" / "Bearer …"), or null.</param>
    async Task<JObject> GetJson(string url, string authorization, int gen)
    {
        using var req = UnityWebRequest.Get(url);
        if (authorization != null) req.SetRequestHeader("Authorization", authorization);
        await Send(req, 30, gen);
        if (req.result != UnityWebRequest.Result.Success) return null;
        try { return JObject.Parse(req.downloadHandler.text); } catch { return null; }
    }

    async Task<bool> DownloadFile(string url, string path, string authorization, int timeoutS, int gen)
    {
        using var req = UnityWebRequest.Get(url);
        req.downloadHandler = new DownloadHandlerFile(path) { removeFileOnAbort = true };
        if (authorization != null) req.SetRequestHeader("Authorization", authorization);
        _progressReq = req;
        try { await Send(req, timeoutS, gen); }
        finally { _progressReq = null; }
        if (req.result == UnityWebRequest.Result.Success) return true;
        Log($"Download failed ({Path.GetFileName(path)}): {req.error}");
        if (File.Exists(path)) File.Delete(path);
        return false;
    }

    async Task<string> Chat(string system, string user, bool jsonMode, float? temperature, int gen)
    {
        if (!IsLocal(llmUrl))
            throw new Exception("Reverie uses a local Llama. Set the LLM endpoint to an address on this machine.");
        var body = new JObject
        {
            ["model"]    = llmModel,
            ["messages"] = new JArray(
                new JObject { ["role"] = "system", ["content"] = system },
                new JObject { ["role"] = "user",   ["content"] = user })
        };
        if (temperature.HasValue) body["temperature"] = temperature.Value;
        if (seed >= 0) body["seed"] = seed;
        if (jsonMode) body["response_format"] = new JObject { ["type"] = "json_object" };

        using var req = PostJson(llmUrl, body.ToString(), null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Send(req, 600, gen); // a local model may need minutes to load on first request
        if (req.result != UnityWebRequest.Result.Success)
            throw new Exception($"LLM error {req.responseCode}: {req.error} {req.downloadHandler.text}\n(Is Ollama running? Try: ollama serve)");
        Log($"Llama ({llmModel}) responded in {sw.Elapsed.TotalSeconds:F1} s");
        return JObject.Parse(req.downloadHandler.text)["choices"][0]["message"]["content"].ToString();
    }

    // ── Config: ApiConfig (persistentDataPath override first, then StreamingAssets) ──
    async Task<ApiSettings> Config(int gen)
    {
        if (_configLoaded) return ApiConfig.Settings;
        _configLoaded = true;
        if (ApiConfig.StreamingNeedsWebRequest) // Android: StreamingAssets lives inside the APK
        {
            using var req = UnityWebRequest.Get(ApiConfig.StreamingPath);
            await Send(req, 10, gen);
            ApiConfig.LoadFromText(req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : null,
                                   ApiConfig.StreamingPath);
        }
        else ApiConfig.Load();
        var cfg = ApiConfig.Settings;
        if (cfg == null) Log(ApiConfig.LastError);
        // Server from api_config.json unless the URL was changed in Settings. ":8100" was the old default.
        bool urlUntouched = string.IsNullOrWhiteSpace(serverUrl) || serverUrl == DefaultServerUrl
                            || serverUrl.TrimEnd('/').EndsWith(":8100");
        if (urlUntouched) serverUrl = string.IsNullOrWhiteSpace(cfg?.server?.url) ? DefaultServerUrl : cfg.server.url;
        if (string.IsNullOrEmpty(serverToken))
            serverToken = !string.IsNullOrEmpty(cfg?.server?.token) ? cfg.server.token
                        : Environment.GetEnvironmentVariable("REVERIE_TOKEN");
        if (string.IsNullOrEmpty(llmApiKey)) llmApiKey = cfg?.ollama?.api_key;
        if (string.IsNullOrEmpty(sketchfabToken))
            sketchfabToken = !string.IsNullOrEmpty(cfg?.sketchfab?.api_token) ? cfg.sketchfab.api_token
                           : Environment.GetEnvironmentVariable("SKETCHFAB_TOKEN");
        return cfg;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    public void Log(string msg)
    {
        _log.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (_log.Count > 300) _log.RemoveAt(0);
        _logScroll = new Vector2(0, float.MaxValue);
    }

    void SetLastLog(string msg)
    {
        string stamped = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        if (_log.Count > 0) _log[^1] = stamped; else _log.Add(stamped);
    }

    static bool IsLocal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "0.0.0.0");

    static string ToSlug(string s) => Regex.Replace(s.ToLower(), @"[^a-z0-9]+", "-").Trim('-');

    static string SanitizeFilename(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = Regex.Replace(name, @"\s+", "_");
        return name.Length > 64 ? name[..64] : name;
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }

    // Words that strongly indicate a character/creature model — deprioritised so props win.
    static readonly string[] CharacterRedFlags = {
        "girl", "boy", "man", "woman", "female", "male", "character", "human",
        "person", "avatar", "soldier", "warrior", "knight", "ninja", "robot",
        "alien", "zombie", "monster", "creature", "beast", "demon", "angel",
        "anime", "waifu", "figure", "bust", "portrait", "face", "head",
        "sci-fi girl", "scifi girl", "cyberpunk girl", "fantasy girl"
    };

    /// Drop character models, then rank by keyword overlap.
    static JObject PickBestMatch(List<JObject> models, string query)
    {
        var props = models.FindAll(m =>
        {
            string nl = (m["name"]?.ToString() ?? "").ToLowerInvariant();
            return !CharacterRedFlags.Any(flag => nl.Contains(flag));
        });
        var candidates = props.Count > 0 ? props : models;

        string   queryLower = query.ToLowerInvariant();
        string[] keywords   = queryLower.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        JObject  best       = candidates[0];
        int      bestScore  = int.MinValue;

        foreach (var m in candidates)
        {
            string   nameLower = (m["name"]?.ToString() ?? "").ToLowerInvariant();
            string[] nameWords = nameLower.Split(new[] { ' ', '-', '_', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
            int score = 0;
            foreach (string kw in keywords)
                score += Array.Exists(nameWords, w => w == kw) ? 10 : nameLower.Contains(kw) ? 2 : 0;
            if (nameLower == queryLower) score += 20;
            else if (nameLower.StartsWith(queryLower)) score += 8;
            int extraWords = nameWords.Length - keywords.Length;
            if (extraWords > 2) score -= extraWords * 2;
            if (score > bestScore) { bestScore = score; best = m; }
        }
        Debug.Log($"[ThemeTools] Query='{query}' picked '{best["name"]}' score={bestScore} ({props.Count}/{models.Count} props after char-filter)");
        return best;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Prompts (system prompt identical to server/llm_system_prompt.txt, used by log_times.py)
    // ─────────────────────────────────────────────────────────────────────────

    static string PromptSystem(bool needVisual, bool needAudio)
    {
        // "setting" is always first — forces the model to reason about the universe/context
        // before writing the descriptions. Field order matters: LLMs attend to prior tokens.
        var jsonFields = new StringBuilder(@"  ""setting"": ""one specific evocative location for this theme""");
        var rules      = new StringBuilder(@"SETTING rules — classify the theme first, then apply the matching rule:

TYPE A — FRANCHISE (movie / game / TV show / book universe):
  Pick ONE iconic named location from that work.
  star wars       → 'Tatooine desert planet with twin suns'
  minecraft       → 'Overworld grassy plains biome at sunset'
  harry potter    → 'Hogwarts Great Hall interior'

TYPE B — GENRE or ERA (fantasy / sci-fi / prehistoric / horror / western…):
  Pick the single most archetypal, visually rich scene for that genre.
  fantasy         → 'ancient dragon-scorched castle courtyard at dusk'
  prehistoric     → 'Cretaceous jungle swamp with erupting volcano on horizon'
  futuristic sci-fi → 'neon-lit cyberpunk megacity street at night'
  space galaxy    → 'colorful deep-space nebula with swirling gas clouds and distant star clusters'

TYPE C — REAL PLACE (country / city / landmark / biome):
  Name the most visually distinctive version of that place.
  scotland highlands → 'Scottish Highlands moorland at golden hour with misty glens and heather'
  amazon            → 'Amazon rainforest canopy at dawn with morning mist'
  tokyo             → 'Tokyo Shibuya crossing at night with neon reflections'

TYPE D — ABSTRACT CONCEPT (seasons, emotions, elements…):
  Translate into the most evocative physical scene.
  winter          → 'frozen Nordic tundra under northern lights'
  fire            → 'volcanic lava field at night with glowing fissures'

One short phrase only — anchors all descriptions below.

");
        if (needVisual)
        {
            jsonFields.Append(",\n  \"visualDescription\": \"...\"");
            rules.AppendLine(@"VISUAL DESCRIPTION rules:
- FIRST phrase must restate the setting name from above
- Describe the full 360-degree environment — every direction, not just forward
- Style: comma-separated descriptive phrases, NOT full sentences
- Include: lighting quality, sky, ground surface, mid-distance, far background, atmosphere
- End with: no people, photorealistic
- TYPE-SPECIFIC requirements:
    FRANCHISE → at least 2 named in-universe props/vehicles/structures (Jawa sandcrawler, X-wing, Mos Eisley domes)
    GENRE/ERA  → archetypal genre props that establish the period instantly (stone battlements, dragon silhouette; neon holograms, flying cars; giant ferns, dinosaur bones)
    REAL PLACE → actual named landmarks, flora, geology. If the place is known for a castle or fortress, it MUST appear (Edinburgh Castle on volcanic rock, heather moorland, Highland mist)
    ABSTRACT   → dominant elemental or sensory details (magma glow, ash clouds, cracked black rock)
- Examples:
    fantasy    → 'ancient dragon-scorched castle courtyard at dusk, crumbling stone battlements, dragon silhouette on the horizon, glowing spell runes on cobblestones, misty valley below, moonlit purple sky, no people, photorealistic'
    prehistoric → 'Cretaceous jungle swamp, towering tree ferns, erupting volcano on the horizon, bioluminescent mushrooms, amber haze, dense tropical canopy overhead, no people, photorealistic'
    space galaxy → 'deep-space colorful nebula, swirling violet and cyan gas clouds, dense star field, distant galaxy spiral, glowing asteroid field, no light source horizon, infinite black void, no people, photorealistic'
    scotland    → 'Scottish Highlands moorland at golden hour, Edinburgh Castle silhouette on volcanic rock, ancient stone castle battlements in foreground, rolling heather hills, misty glens, dramatic cumulus clouds, no people, photorealistic'");
        }
        if (needVisual && needAudio) jsonFields.Append(",\n");
        if (needAudio)
        {
            jsonFields.Append("  \"audioDescription\": \"...\"");
            rules.AppendLine(@"AUDIO DESCRIPTION rules:
- Describe sounds you would hear in the SETTING named above
- Style: comma-separated sound phrases, NOT full sentences
- Exclude: music, voices, speech, narration
- TYPE-SPECIFIC requirements:
    FRANCHISE → include at least 2 named in-universe sounds (TIE fighter distant hum, lightsaber idle hum, R2-D2 chirps, Minecraft block break, creeper hiss)
    GENRE/ERA  → ambient sounds that define the era (pterodactyl cry, volcano rumble, lava bubble; magic spell crackle, torch flicker; servo motors, hover-car whoosh, hologram static)
    REAL PLACE → specific natural/urban sounds of that location (Highland wind on moorland, grouse calls, babbling burn; Tokyo city hum, neon buzz, distant train)
    ABSTRACT   → elemental sounds matching the concept (magma deep rumble, lava crackling, ash wind hiss, rock splitting)
- Examples:
    star wars   → 'howling Tatooine desert wind, distant R2 unit chirps, faint TIE fighter hum, sand dune shifting, speeder engine idle, no music, no voices'
    prehistoric → 'jungle canopy wind, distant dinosaur calls, erupting volcano low rumble, dense insect chorus, lava hiss, no music, no voices'
    space galaxy → 'deep cosmic resonance drone, solar wind hiss, distant asteroid impact rumble, radio telescope static, no music, no voices'
    scotland    → 'Highland wind across heather moorland, grouse and curlew calls, babbling burn, distant Edinburgh city hum, no music, no voices'");
        }

        return $@"You are a prompt engineer for a 3D scene generation pipeline. Given a theme word, output EXACTLY this JSON and nothing else:
{{
{jsonFields}
}}

{rules}";
    }

    const string ToolTargetSystem =
        "You are an experienced casual arcade game designer. " +
        "THE MECHANIC: The player swings, taps, or moves the TOOL so it makes DIRECT PHYSICAL CONTACT " +
        "with the TARGET to pop, break, collect, or clear it. " +
        "This is NOT a shooting or aiming game. There is no projectile flying across a gap. " +
        "Think: Fruit Ninja (blade touches fruit), Bubble Shooter (bubble touches bubble), " +
        "Candy Crush (candy touches candy), Cut the Rope (blade touches rope). " +
        "The tool and target must be able to physically touch in the same space. " +
        "STEP 0 — CLASSIFY THE THEME: " +
        "  A) FRANCHISE — theme names a specific movie, TV show, video game, or fictional universe " +
        "     (e.g. 'star wars', 'minecraft', 'harry potter'). " +
        "  B) SETTING — theme describes a place, environment, mood, or activity with no specific " +
        "     franchise (e.g. 'forest', 'beach', 'medieval dungeon', 'candy land'). " +
        "IF FRANCHISE: pick objects iconic to that universe and include the franchise name in both " +
        "  tool and target strings. Examples: " +
        "    star wars  → tool: lightsaber,         target: thermal detonator " +
        "    minecraft  → tool: diamond pickaxe,    target: TNT block " +
        "    mario      → tool: Mario hammer,       target: Koopa shell " +
        "    harry potter → tool: magic wand,       target: golden snitch " +
        "IF SETTING: pick the two most ordinary, iconic objects a person would find in that setting — " +
        "  favor plain everyday items over the franchise-style examples above. Examples: " +
        "    forest       → tool: axe,          target: log " +
        "    beach        → tool: surfboard,    target: sandcastle " +
        "    kitchen      → tool: rolling pin,  target: cookie dough " +
        "    construction → tool: hammer,       target: nail " +
        "In both cases the objects must be widely available as 3D models on Sketchfab " +
        "(search by the exact words you output). " +
        "SIMPLE TEST: Could a 10-year-old name both objects instantly and picture the tool acting " +
        "  on the target? If not, pick something simpler and more obvious. " +
        "CRITICAL DEFINITIONS: " +
        "  TOOL   = object the player drags, swings, or moves. It travels to meet the target. " +
        "  TARGET = stationary object that gets popped, cleared, or collected on contact. " +
        "GOOD examples (direct contact, recognisable objects): " +
        "  Fruit Ninja    → tool: blade,       target: watermelon " +
        "  Bubble Shooter → tool: bubble,      target: bubble " +
        "  Forest         → tool: axe,         target: log " +
        "  Star Wars      → tool: lightsaber,  target: thermal detonator " +
        "BAD — aim-and-shoot (tool never physically touches target): " +
        "  bow → tree trunk, dart → dartboard, cannon → asteroid " +
        "RULES: " +
        "  1. INANIMATE OBJECTS ONLY — no living things. " +
        "  2. KIND interactions — popping, slicing, collecting. Never harm characters. " +
        "  3. DIRECT CONTACT mechanic only — no projectiles. " +
        "  4. Use the most searchable 1-3 word Sketchfab query for each object. " +
        "  5. For FRANCHISE themes, include the franchise name in both tool and target strings. " +
        "     For SETTING themes, keep names simple and generic — no invented franchise names. " +
        "Respond with ONLY valid JSON, no markdown: " +
        "{\"setting\": \"...\", \"tool\": \"...\", \"target\": \"...\", \"reasoning\": \"one sentence\"}.";

    static string ToolTargetUser(string t) =>
        $"Theme: {t}\n\n" +
        "Step 1 — Classify: is this a FRANCHISE (specific movie/show/game/book) or a SETTING " +
        "(place/mood/activity with no specific franchise)?\n" +
        "Step 2 — If FRANCHISE: name it and list 3 iconic hand-held objects from it. " +
        "If SETTING: list 3 ordinary objects naturally found there.\n" +
        "Step 3 — Which object fits the TOOL role (player swings/moves it)?\n" +
        "Step 4 — Which fits the TARGET role (stationary, gets hit/collected)?\n" +
        "Step 5 — Output ONLY this JSON (no other text after it):\n" +
        "{\"setting\": \"a short description of the place or franchise location\", \"tool\": \"...\", \"target\": \"...\", \"reasoning\": \"one sentence\"}";
}
