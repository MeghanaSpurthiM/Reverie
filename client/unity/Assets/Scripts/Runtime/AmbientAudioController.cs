using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// AmbientAudioController
/// ======================
/// Crossfades ambient audio. Supports two call sites:
///
///   EnvironmentManager (server download path):
///     Audio.PlayAmbient(audioClip);          // AudioClip already in memory
///
///   Theme loading (disk path):
///     await audioController.LoadAndPlay(wavPath);  // load from file path
///
/// Attach to any persistent GameObject (e.g. AudioSystem or EnvironmentSystem).
/// Requires one AudioSource on the same GameObject.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class AmbientAudioController : MonoBehaviour
{
    [Header("Crossfade")]
    [Tooltip("Seconds to crossfade between tracks.")]
    public float crossfadeDuration = 2.0f;

    [Tooltip("Target volume after fade-in.")]
    [Range(0f, 1f)]
    public float targetVolume = 0.6f;

    [Tooltip("Loop the ambient track.")]
    public bool loop = true;

    AudioSource          _sourceA;
    AudioSource          _sourceB;
    bool                 _aIsActive  = true;
    readonly HashSet<AudioClip> _ownedClips = new HashSet<AudioClip>(); // clips we loaded from disk

    AudioSource Active   => _aIsActive ? _sourceA : _sourceB;
    AudioSource Inactive => _aIsActive ? _sourceB : _sourceA;

    void Awake()
    {
        _sourceA = GetComponent<AudioSource>();
        _sourceA.loop        = loop;
        _sourceA.volume      = 0f;
        _sourceA.playOnAwake = false;

        var child = new GameObject("AudioSource_B");
        child.transform.SetParent(transform, false);
        _sourceB = child.AddComponent<AudioSource>();
        _sourceB.loop        = loop;
        _sourceB.volume      = 0f;
        _sourceB.playOnAwake = false;
    }

    // ── Call site 1: AudioClip already in memory (EnvironmentManager) ────────

    /// <summary>
    /// Play an AudioClip that has already been downloaded / loaded.
    /// Used by EnvironmentManager after downloading audio from the server.
    /// Crossfades from the currently playing track.
    /// </summary>
    public void PlayAmbient(AudioClip clip)
    {
        if (clip == null) { Debug.LogWarning("[Audio] PlayAmbient called with null clip."); return; }
        CrossfadeTo(clip);
    }

    // ── Call site 2: File path on disk (theme loading) ──────────────────────────

    /// <summary>
    /// Load a .wav / .mp3 / .ogg from disk then crossfade to it.
    /// Awaitable.
    /// </summary>
    public async Task LoadAndPlay(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"[Audio] File not found: {filePath}");

        AudioClip clip = await LoadClipAsync(filePath);
        if (clip == null)
            throw new System.Exception($"[Audio] Failed to load clip from: {filePath}");

        CrossfadeTo(clip);
    }

    // ── Stop / fade ───────────────────────────────────────────────────────────

    /// <summary>Stop both sources immediately and release loaded clips.</summary>
    public void Stop()
    {
        StopAllCoroutines();
        ReleaseClip(_sourceA.clip); _sourceA.Stop(); _sourceA.clip = null;
        ReleaseClip(_sourceB.clip); _sourceB.Stop(); _sourceB.clip = null;
    }

    /// <summary>Fade out the active source over crossfadeDuration.</summary>
    public void FadeOut() => StartCoroutine(FadeOutCoroutine());

    void OnDestroy()
    {
        foreach (var clip in _ownedClips)
            if (clip != null) Destroy(clip);
        _ownedClips.Clear();
    }

    void ReleaseClip(AudioClip clip)
    {
        if (clip != null && _ownedClips.Remove(clip))
            Destroy(clip);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    async Task<AudioClip> LoadClipAsync(string path)
    {
        string uri = "file://" + path.Replace("\\", "/");
        AudioType type = DetectAudioType(path);

        using var req = UnityWebRequestMultimedia.GetAudioClip(uri, type);
        var op = req.SendWebRequest();
        while (!op.isDone) await Task.Yield();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[Audio] Load error: {req.error} — {uri}");
            return null;
        }

        var clip = DownloadHandlerAudioClip.GetContent(req);
        clip.name = Path.GetFileNameWithoutExtension(path);
        _ownedClips.Add(clip); // track for cleanup
        return clip;
    }

    static AudioType DetectAudioType(string path)
    {
        return Path.GetExtension(path).ToLower() switch
        {
            ".mp3" => AudioType.MPEG,
            ".ogg" => AudioType.OGGVORBIS,
            _      => AudioType.WAV,
        };
    }

    void CrossfadeTo(AudioClip clip)
    {
        StopAllCoroutines();

        AudioSource next = Inactive;
        AudioSource prev = Active;

        // Release any clip left on the inactive source from an interrupted fade
        ReleaseClip(next.clip);
        next.clip = null;

        next.clip   = clip;
        next.loop   = loop;
        next.volume = 0f;
        next.Play();

        _aIsActive = !_aIsActive;
        StartCoroutine(CrossfadeCoroutine(prev, next));
    }

    IEnumerator CrossfadeCoroutine(AudioSource fadeOut, AudioSource fadeIn)
    {
        float elapsed  = 0f;
        float startVol = fadeOut.volume;

        while (elapsed < crossfadeDuration)
        {
            elapsed += Time.deltaTime;
            float t  = Mathf.Clamp01(elapsed / crossfadeDuration);
            fadeIn.volume  = Mathf.Lerp(0f,       targetVolume, t);
            fadeOut.volume = Mathf.Lerp(startVol, 0f,           t);
            yield return null;
        }

        fadeIn.volume  = targetVolume;
        fadeOut.volume = 0f;
        fadeOut.Stop();
        ReleaseClip(fadeOut.clip);
        fadeOut.clip = null;
    }

    IEnumerator FadeOutCoroutine()
    {
        float elapsed  = 0f;
        float startVol = Active.volume;

        while (elapsed < crossfadeDuration)
        {
            elapsed += Time.deltaTime;
            Active.volume = Mathf.Lerp(startVol, 0f, elapsed / crossfadeDuration);
            yield return null;
        }

        ReleaseClip(Active.clip);
        Active.Stop();
        Active.clip = null;
    }
}