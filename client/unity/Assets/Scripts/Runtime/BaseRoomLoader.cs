using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

/// Attached to the Room object. Shows one base room at a time and cycles through all of them:
///
///  1. Room variants: every direct child of Room except the placeholder, for example an Asset Store
///     prefab added with Reverie → Base Room → Add Room Variant. They are switched on and off.
///  2. Room files in StreamingAssets/BaseRoom: room.glb or room.gltf, then room_*.glb and room_*.gltf.
///     Each is loaded with glTFast when chosen and freed when the next room is chosen.
///
/// The placeholder shows when there is no other room, while a room file is loading, and when a
/// room fails to load. Press the next-room key (R by default) while the room layer is on to cycle.
/// See docs/BASE_ROOM.md.
public class BaseRoomLoader : MonoBehaviour
{
    public const string RoomFolder = "BaseRoom";
    const string LoadedRoomPrefix = "_LoadedRoom";

    [Tooltip("Shown when there is no other room, while a room file loads, and if a room fails to load.")]
    public GameObject placeholder;

    [Header("Placement of room files (.glb / .gltf)")]
    [Tooltip("Room-local point where a room file's origin goes: the centre of the play area, on the floor.")]
    public Vector3 floorOrigin = new(-2.55f, 0.66f, 0.18f);
    [Tooltip("Extra offset from the floor origin, for files whose floor is not at y = 0.")]
    public Vector3 roomPosition = Vector3.zero;
    public Vector3 roomRotation = Vector3.zero;
    [Tooltip("1 for files in metres, 0.01 for files in centimetres.")]
    public float roomScale = 1f;

    [Header("Switching")]
    [Tooltip("Key that shows the next room while the room layer is on.")]
    public KeyCode nextRoomKey = KeyCode.R;
    [Tooltip("Also cycle with a push right on the left thumbstick.")]
    public bool nextRoomOnLeftStick = true;
    [Tooltip("Index of the room shown at start, in the order listed by Rooms.")]
    public int startRoom = 0;
    [Tooltip("Keep the placeholder in the cycle even when other rooms exist.")]
    public bool includePlaceholderInCycle = false;
    [Tooltip("Read room files from StreamingAssets/BaseRoom.")]
    public bool loadRoomFiles = true;

    [Header("Safety")]
    [Tooltip("Turn off colliders in room variants. Gameplay does not use them, and large mesh colliders stall the frame when a room appears.")]
    public bool disableRoomColliders = true;
    [Tooltip("Turn off cameras and audio listeners that asset pack rooms sometimes include.")]
    public bool disableStrayCameras = true;
    [Tooltip("Log the problems BaseRoomChecks finds in each room variant.")]
    public bool logRoomChecks = true;

    /// A room in the cycle: a GameObject under Room, or a file to load.
    public sealed class Entry
    {
        public string Name;
        public GameObject Variant;
        public string File;
        public bool IsPlaceholder;
    }

    readonly List<Entry> _entries = new();
    int _current = -1;
    int _version;                       // bumped on every switch; a load that sees a newer value gives up
    bool _switching;
    int? _queued;                       // a switch requested while another was being applied
    bool _destroyed;
    bool _initialized;

    CancellationTokenSource _cts;
    Task _load = Task.CompletedTask;
    int _pendingLoads;                  // loads still running, including cancelled ones cleaning up
    GameObject _loadedRoom;             // the instance of the current room file, once loaded
    GltfImport _loadedImport;           // owns its meshes, materials and textures

    string _folderOverride;

    /// Rooms in cycle order.
    public IReadOnlyList<Entry> Rooms => _entries;
    public int CurrentIndex => _current;
    public Entry Current => _current >= 0 && _current < _entries.Count ? _entries[_current] : null;
    /// True while a room file is loading, including a cancelled load that is still cleaning up.
    public bool IsLoading => _pendingLoads > 0;
    /// The latest load, for tests and tools that need to wait for it. It never throws.
    public Task CurrentLoad => _load;
    /// The instance of the current room file, or null.
    public GameObject LoadedRoom => _loadedRoom;
    /// The last load error, or null.
    public string LastError { get; private set; }
    /// Raised after a room becomes visible (or the placeholder, after a failure).
    public event Action<Entry> RoomShown;

    /// Folder holding room files. Defaults to StreamingAssets/BaseRoom.
    public string RoomFolderPath
    {
        get => _folderOverride ?? Path.Combine(Application.streamingAssetsPath, RoomFolder);
        set => _folderOverride = value;
    }

    void Start()
    {
        if (!_initialized) Refresh();
        if (_version == 0) Select(startRoom);   // unless something already chose a room
    }

    void Update()
    {
        bool next = Input.GetKeyDown(nextRoomKey);
        if (!next && nextRoomOnLeftStick)
            next = OVRInput.GetDown(OVRInput.Button.PrimaryThumbstickRight, OVRInput.Controller.LTouch);
        if (next) Next();
    }

    /// Re-reads the room variants under Room and the room files, then keeps showing the same room if it
    /// still exists. Call it after adding or removing rooms at runtime.
    public void Refresh()
    {
        _initialized = true;
        Entry previous = Current;
        _entries.Clear();

        foreach (Transform child in transform)
        {
            GameObject go = child.gameObject;
            if (go == placeholder || go.name.StartsWith(LoadedRoomPrefix)) continue;
            var marker = go.GetComponent<BaseRoomVariant>();
            if (marker != null && !marker.includeInCycle) { go.SetActive(false); continue; }

            PrepareVariant(go);
            _entries.Add(new Entry { Name = marker != null ? marker.DisplayName : go.name, Variant = go });
        }

        if (loadRoomFiles)
        {
            try
            {
                foreach (string file in BaseRoomFiles.List(RoomFolderPath))
                    _entries.Add(new Entry { Name = BaseRoomFiles.DisplayName(file), File = file });
            }
            catch (Exception e) { Debug.LogWarning($"[BaseRoom] Could not read {RoomFolderPath}: {e.Message}"); }
        }

        if (placeholder != null && (_entries.Count == 0 || includePlaceholderInCycle))
            _entries.Add(new Entry { Name = "Placeholder", Variant = placeholder, IsPlaceholder = true });

        // Keep the same room selected if it is still there; the caller decides whether to switch.
        _current = previous == null ? -1 : _entries.FindIndex(e =>
            (e.Variant != null && e.Variant == previous.Variant) || (e.File != null && e.File == previous.File));
    }

    public void Next() => Step(+1);
    public void Previous() => Step(-1);

    void Step(int direction)
    {
        if (_entries.Count == 0) return;
        int from = _queued ?? _current;
        Select(from < 0 ? 0 : from + direction);
    }

    /// Shows room <paramref name="index"/> (wrapping around) and hides every other room. Safe to call at
    /// any time, also while a room file is still loading: that load is cancelled and freed.
    public void Select(int index)
    {
        if (_destroyed) return;
        if (_switching) { _queued = index; return; }   // e.g. a room's OnEnable asked to switch
        if (!_initialized) Refresh();

        _switching = true;
        try { Apply(index); }
        finally { _switching = false; }

        if (_queued is int queued) { _queued = null; Select(queued); }
    }

    void Apply(int index)
    {
        int version = ++_version;
        CancelLoad();
        UnloadRoomFile();

        if (_entries.Count == 0) { _current = -1; ShowPlaceholder(true); return; }

        _current = ((index % _entries.Count) + _entries.Count) % _entries.Count;
        Entry entry = _entries[_current];

        foreach (Entry other in _entries)
            if (other != entry && other.Variant != null && !other.IsPlaceholder) other.Variant.SetActive(false);

        if (entry.File != null)
        {
            ShowPlaceholder(true);   // until the file has loaded
            _cts = new CancellationTokenSource();
            _load = LoadFileAsync(entry, version, _cts);
            return;
        }

        if (entry.Variant == null)
        {
            Fail(entry, "its GameObject was destroyed");
            return;
        }

        ShowPlaceholder(entry.IsPlaceholder);
        entry.Variant.SetActive(true);
        LastError = null;
        Debug.Log($"[BaseRoom] Showing {entry.Name}");
        RoomShown?.Invoke(entry);
    }

    async Task LoadFileAsync(Entry entry, int version, CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        _pendingLoads++;
        var import = new GltfImport();
        GameObject container = null;
        bool kept = false;
        try
        {
            bool ok = await import.Load(new Uri(entry.File).AbsoluteUri, null, token);
            if (!IsCurrent(version, token)) return;
            if (!ok) { Fail(entry, "glTFast could not read it"); return; }

            // Build the room hidden, so a half-instantiated room never shows.
            container = new GameObject($"{LoadedRoomPrefix} {entry.Name}");
            container.SetActive(false);
            Transform room = container.transform;
            room.SetParent(transform, false);
            room.localPosition = floorOrigin + roomPosition;
            room.localRotation = Quaternion.Euler(roomRotation);
            room.localScale = Vector3.one * roomScale;

            // Instantiation ignores the token, so it always runs to the end before we look again.
            ok = await import.InstantiateMainSceneAsync(new GameObjectInstantiator(import, room), token);
            if (!IsCurrent(version, token) || container == null) return;
            if (!ok) { Fail(entry, "glTFast could not instantiate it"); return; }

            _loadedRoom = container;
            _loadedImport = import;
            kept = true;
            container.SetActive(true);
            ShowPlaceholder(false);
            LastError = null;
            Debug.Log($"[BaseRoom] Showing {entry.Name} ({Path.GetFileName(entry.File)})");
            RoomShown?.Invoke(entry);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (IsCurrent(version, token)) Fail(entry, e.Message);
            else Debug.Log($"[BaseRoom] A cancelled load of {entry.Name} ended with: {e.Message}");
        }
        finally
        {
            _pendingLoads--;
            if (!kept)
            {
                // Objects first, then the meshes, materials and textures they use.
                if (container != null) Destroy(container);
                import.Dispose();
            }
            if (_cts == cts) _cts = null;   // finished, so there is nothing left to cancel
            cts.Dispose();   // disposed here, not in CancelLoad, so glTFast never sees a disposed token
        }
    }

    bool IsCurrent(int version, CancellationToken token) =>
        !_destroyed && this != null && version == _version && !token.IsCancellationRequested;

    void Fail(Entry entry, string reason)
    {
        LastError = $"Could not show room '{entry.Name}': {reason}";
        Debug.LogWarning($"[BaseRoom] {LastError}. Showing the placeholder instead.");
        if (entry.Variant != null && !entry.IsPlaceholder) entry.Variant.SetActive(false);
        ShowPlaceholder(true);
        RoomShown?.Invoke(entry);
    }

    void ShowPlaceholder(bool show)
    {
        if (placeholder != null) placeholder.SetActive(show);
    }

    void PrepareVariant(GameObject variant)
    {
        if (disableRoomColliders)
            foreach (var collider in variant.GetComponentsInChildren<Collider>(true)) collider.enabled = false;

        if (disableStrayCameras && BaseRoomChecks.DisableStrayPlayerParts(variant) > 0)
            Debug.LogWarning($"[BaseRoom] Turned off the cameras and audio listeners inside '{variant.name}'.");

        if (logRoomChecks)
            foreach (string issue in BaseRoomChecks.Inspect(variant))
                Debug.LogWarning($"[BaseRoom] {variant.name}: {issue}");
    }

    void CancelLoad()
    {
        if (_cts == null) return;
        _cts.Cancel();    // the load disposes it when it ends
        _cts = null;
    }

    void UnloadRoomFile()
    {
        if (_loadedRoom != null) Destroy(_loadedRoom);
        _loadedRoom = null;
        _loadedImport?.Dispose();
        _loadedImport = null;
    }

    void OnDestroy()
    {
        _destroyed = true;
        _version++;
        CancelLoad();
        UnloadRoomFile();
        RoomShown = null;
    }
}
