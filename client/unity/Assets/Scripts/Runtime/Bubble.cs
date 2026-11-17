using System;
using UnityEngine;

public class Bubble : MonoBehaviour
{
    /// Raised once per bubble after it starts, so the themed target can be attached at once.
    public static event Action<Bubble> Spawned;

    public BubbleSpawnerFixed spawner;
    public float speed             = 2.0f;
    public float destroyHeight     = 10.0f;
    public float destroyDepth      = -6.0f;
    public bool  moveDown          = false;
    public float spawnGracePeriod  = 0.2f;

    private float _spawnTime;
    private bool  _isPopped = false;
    private float _radius = 0.1f;   // the ball's world radius, from its SphereCollider
    private bool  _hasHitBox;
    private Bounds _hitBox;         // the themed target's bounds around the bubble origin (layer 4)

    void Start()
    {
        _spawnTime = Time.time;
        if (TryGetComponent<SphereCollider>(out var sphere))
        {
            Vector3 s = transform.lossyScale;
            _radius = sphere.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        }
        ThemeHooks.ApplyTargetVisual(transform);
        Spawned?.Invoke(this);
    }

    void Update()
    {
        Vector3 direction = moveDown ? Vector3.down : Vector3.up;
        transform.Translate(direction * speed * Time.deltaTime, Space.World);

        if (!moveDown && transform.position.y > destroyHeight) { DestroyBubble(); return; }
        if (moveDown && transform.position.y < destroyDepth)   { DestroyBubble(); return; }

        // Only a kick pops a target: touching a paddle at rest does nothing. With a themed target
        // attached, hits use the target's own bounds rather than the ball.
        var kicker = PaddleKicker.Active;
        if (kicker == null || _isPopped || Time.time < _spawnTime + spawnGracePeriod || !kicker.IsKicking) return;
        bool hit = _hasHitBox
            ? kicker.HitsBox(new Bounds(transform.position + _hitBox.center, _hitBox.size))
            : kicker.HitsBall(transform.position, _radius);
        if (hit)
        {
            _isPopped = true;
            Pop();
        }
    }

    /// Hits use these bounds (around the bubble origin, world axes) instead of the ball.
    public void SetHitBox(Bounds box) { _hitBox = box; _hasHitBox = true; }
    public void ClearHitBox() => _hasHitBox = false;

    // Scenes without a PaddleKicker fall back to the physics trigger.
    private void OnTriggerEnter(Collider other)
    {
        if (PaddleKicker.Active != null) return;
        if (_isPopped || Time.time < _spawnTime + spawnGracePeriod) return;
        if (IsPlayerLegOrAttachment(other.transform))
        {
            _isPopped = true;
            Pop();
        }
    }

    static bool IsPlayerLegOrAttachment(Transform t)
    {
        while (t != null)
        {
            if (t.CompareTag("PlayerLeg")) return true;
            t = t.parent;
        }
        return false;
    }

    public void Pop()
    {
        ScoreManager sm = ScoreManager.Instance ?? FindAnyObjectByType<ScoreManager>();
        if (sm != null) sm.AddScore(1);

        DestroyBubble();
    }

    private void DestroyBubble()
    {
        if (spawner != null) spawner.BubbleCleared(this);
        Destroy(gameObject);
    }
}
