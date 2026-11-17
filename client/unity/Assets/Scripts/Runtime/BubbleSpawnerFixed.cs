using System.Collections.Generic;
using UnityEngine;

/// Spawns bubbles in symmetric left/right pairs.
/// spawnPoints are paired by consecutive index: [0,1] = pair 0, [2,3] = pair 1, etc.
/// Arrange them symmetrically in the scene (0=left, 1=right, 2=left, 3=right, ...).
/// When both bubbles of a pair clear, the next pair spawns.
///
/// With solveLanes on, each pair falls in lanes moved as little as possible from its spawn points
/// so that only its own kick reaches it, the paddles at rest never touch it, and one well-timed
/// kick pops both (KickLanes). Themed targets (layer 4) get lanes for their own size.
public class BubbleSpawnerFixed : MonoBehaviour
{
    public GameObject  bubblePrefab;
    public Transform[] spawnPoints; // must be even; paired as (0,1), (2,3), (4,5)...
    [Tooltip("How many symmetric pairs fall simultaneously.")]
    public int activePairs = 1;
    [Tooltip("Bubbles fall downward from spawn points instead of rising.")]
    public bool fallDown = true;

    [Header("Lanes")]
    [Tooltip("Move each pair's lanes so that only its own kick reaches it (see KickLanes).")]
    public bool solveLanes = true;
    [Tooltip("The kick that should pop each pair, in spawn point pair order.")]
    public PaddleKick[] pairKicks = { PaddleKick.Wide, PaddleKick.Side, PaddleKick.Front };

    // bubble → pair index
    readonly Dictionary<Bubble, int> _pairOf    = new Dictionary<Bubble, int>();
    // pair index → live bubble count
    readonly Dictionary<int,    int> _pairCount = new Dictionary<int, int>();

    int PairCount => spawnPoints.Length / 2;
    int _lastSpawned = -1;

    Vector3[] _ballLanes;    bool _ballSolved;
    Vector3[] _targetLanes;  bool _targetSolved;
    bool   _hasTarget;
    Bounds _targetBox;       // the themed target around the bubble origin, at bubble size

    void Start() => SpawnNewSet();

    /// Called by FolderEnvironmentLoader: the themed target's bounds (its model at the origin, unscaled),
    /// while layer 4 is on; null when it is off. New pairs then fall in lanes fitted to the target.
    public void SetTargetBounds(Bounds? templateBounds)
    {
        _targetSolved = false;
        _targetLanes = null;
        _hasTarget = templateBounds.HasValue && templateBounds.Value.size.x > 0f;
        if (!_hasTarget) return;
        // The target is scaled to be as wide as the bubble.
        Bounds b = templateBounds.Value;
        float k = 2f * BallRadius() / b.size.x;
        _targetBox = new Bounds(b.center * k, b.size * k);
    }

    public void SpawnNewSet()
    {
        foreach (var b in new List<Bubble>(_pairOf.Keys))
            if (b != null) Destroy(b.gameObject);
        _pairOf.Clear();
        _pairCount.Clear();
        _lastSpawned = -1;

        if (PairCount == 0) { Debug.LogWarning("[Spawner] Need at least 2 spawn points (one pair)."); return; }

        int count = Mathf.Min(activePairs, PairCount);
        var used  = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int p = PickNext(used);
            used.Add(p);
            SpawnPair(p);
        }
    }

    public void BubbleCleared(Bubble b)
    {
        if (!_pairOf.TryGetValue(b, out int idx)) return;
        _pairOf.Remove(b);

        _pairCount[idx]--;
        if (_pairCount[idx] > 0) return; // partner still alive

        _pairCount.Remove(idx);
        _lastSpawned = idx;
        SpawnPair(PickNext(new HashSet<int>(_pairCount.Keys)));
    }

    void SpawnPair(int idx)
    {
        _pairCount[idx] = 0;
        Vector3[] lanes = Lanes();
        SpawnAt(idx * 2,     idx, lanes); // left
        SpawnAt(idx * 2 + 1, idx, lanes); // right
        _lastSpawned = idx;
    }

    void SpawnAt(int pointIndex, int idx, Vector3[] lanes)
    {
        Transform point = spawnPoints[pointIndex];
        if (point == null) return;
        Vector3 at = lanes != null ? lanes[pointIndex] : point.position;
        var obj = Instantiate(bubblePrefab, at, Quaternion.identity);
        ThemeHooks.Register(obj);
        var b = obj.GetComponent<Bubble>();
        if (b == null) return;
        b.spawner  = this;
        b.moveDown = fallDown;
        _pairOf[b]      = idx;
        _pairCount[idx]++;
    }

    int PickNext(HashSet<int> active)
    {
        var available = new List<int>();
        for (int i = 0; i < PairCount; i++)
            if (!active.Contains(i)) available.Add(i);

        if (available.Count == 0) return Random.Range(0, PairCount);

        var preferred = available.FindAll(i => i != _lastSpawned);
        var pool = preferred.Count > 0 ? preferred : available;
        return pool[Random.Range(0, pool.Count)];
    }

    // ── Lanes ─────────────────────────────────────────────────────────────────

    /// The lanes for the next pair: the target's if layer 4 is on, else the ball's; null = spawn points.
    Vector3[] Lanes()
    {
        if (!solveLanes || !fallDown) return null;
        if (_hasTarget)
        {
            if (!_targetSolved) { _targetLanes = Solve(_targetBox, includeTools: true, "target"); _targetSolved = true; }
            if (_targetLanes != null) return _targetLanes;
        }
        if (!_ballSolved)
        {
            float r = BallRadius();
            _ballLanes = Solve(new Bounds(Vector3.zero, Vector3.one * (2f * r)), includeTools: false, "ball");
            _ballSolved = true;
        }
        return _ballLanes;
    }

    Vector3[] Solve(Bounds box, bool includeTools, string what)
    {
        var kicker = PaddleKicker.Active;
        if (kicker == null) return null;
        var homes = new List<Vector3>(spawnPoints.Length);
        foreach (var p in spawnPoints)
        {
            if (p == null) return null;
            homes.Add(p.position);
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lanes = KickLanes.Compute(kicker, box, homes, pairKicks, FallSpeed(), includeTools);
        if (lanes == null) Debug.LogWarning($"[Spawner] No {what} lanes keep clear of the resting paddles; using the spawn points.");
        else Debug.Log($"[Spawner] {what} lanes solved in {watch.ElapsedMilliseconds} ms: {string.Join(", ", System.Array.ConvertAll(lanes, l => l.ToString("F3")))}");
        return lanes;
    }

    float BallRadius()
    {
        if (bubblePrefab != null && bubblePrefab.TryGetComponent<SphereCollider>(out var sphere))
        {
            Vector3 s = bubblePrefab.transform.localScale;
            return sphere.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        }
        return 0.1f;
    }

    float FallSpeed() =>
        bubblePrefab != null && bubblePrefab.TryGetComponent<Bubble>(out var b) ? b.speed : 1f;
}
