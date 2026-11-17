using UnityEngine;

/// Plays paddle kicks on a two-joint leg rig. Both paddles kick together, mirrored.
/// A target pops only during a kick, when it touches a foot, a paddle or the themed tool riding a
/// paddle (Bubble asks HitsBall / HitsBox each frame), as in the browser preview.
[DefaultExecutionOrder(-10)]   // poses the legs before targets test against them
public class PaddleKicker : MonoBehaviour
{
    public const int LegCount = 2;

    /// The kicker in the scene (null when there is none).
    public static PaddleKicker Active { get; private set; }

    [Header("Leg joints")]
    public Transform leftHip;
    public Transform leftKnee;
    public Transform rightHip;
    public Transform rightKnee;

    [Header("Feet and paddles (found under the knees when empty)")]
    public Transform leftFoot;
    public Transform rightFoot;
    public Transform leftPaddle;
    public Transform rightPaddle;
    [Tooltip("Radius of the foot's hit sphere. Taken from the foot's CapsuleCollider when it has one.")]
    public float footRadius = 0.05f;

    [Tooltip("Place each paddle the same distance out from its foot, keeping (within 0.3 mm) where both paddles rest, " +
             "so mirrored kicks sweep mirrored paths. The scene's right paddle sits 0.306 m beside its " +
             "foot and the left one under its foot, so without this a side or wide kick swings the " +
             "right paddle on a wider arc.")]
    public bool symmetricPaddles = true;

    [Header("Controller Buttons (Meta Quest)")]
    [Range(0.1f, 0.9f)]
    public float kickTriggerThreshold = 0.5f;
    public OVRInput.Button kickWideButton = OVRInput.Button.One;   // A
    public OVRInput.Button kickSideButton = OVRInput.Button.Two;   // B

    [Header("Animation Speed")]
    [Range(0.5f, 3f)]
    public float animationSpeed = 1.5f;

    PaddleKick _kick;
    float _time;
    bool _kicking;
    bool _prevTrigger, _prevSide, _prevWide;

    /// True while a kick plays. Targets only pop then.
    public bool IsKicking => _kicking;

    public bool HasLegs => leftHip && leftKnee && rightHip && rightKnee && leftFoot && rightFoot && leftPaddle && rightPaddle;

    void Awake()
    {
        FindLegParts();
        if (symmetricPaddles) MakePaddlesSymmetric();
    }

    void OnEnable() => Active = this;
    void OnDisable() { if (Active == this) Active = null; }

    void Start() => ApplyRest();

    void Update()
    {
        HandleInput();
        Tick(Time.deltaTime);
    }

    /// Starts a kick from its beginning, cutting off any kick in progress.
    public void Play(PaddleKick kick)
    {
        _kick = kick;
        _time = 0f;
        _kicking = true;
        Apply();
    }

    /// Advances the current kick by dt seconds.
    public void Tick(float dt)
    {
        if (!_kicking) return;
        _time += dt * animationSpeed;
        if (_time < PaddleKickPose.Duration) { Apply(); return; }
        _kicking = false;
        ApplyRest();
    }

    void HandleInput()
    {
        bool trigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch) >= kickTriggerThreshold;
        bool side    = OVRInput.Get(kickSideButton, OVRInput.Controller.All);
        bool wide    = OVRInput.Get(kickWideButton, OVRInput.Controller.All);

        bool frontDown = Input.GetMouseButtonDown(0) || (trigger && !_prevTrigger);
        bool sideDown  = Input.GetKeyDown(KeyCode.A) || (side && !_prevSide);
        bool wideDown  = Input.GetKeyDown(KeyCode.D) || (wide && !_prevWide);
        _prevTrigger = trigger;
        _prevSide    = side;
        _prevWide    = wide;

        if      (frontDown) Play(PaddleKick.Front);
        else if (sideDown)  Play(PaddleKick.Side);
        else if (wideDown)  Play(PaddleKick.Wide);
    }

    void Apply() => Pose(_kick, _time);

    void Pose(PaddleKick kick, float t)
    {
        PaddleKickPose.Evaluate(kick, t, -1f, out Quaternion hip, out Quaternion knee);
        leftHip.localRotation  = hip;
        leftKnee.localRotation = knee;

        PaddleKickPose.Evaluate(kick, t, 1f, out hip, out knee);
        rightHip.localRotation  = hip;
        rightKnee.localRotation = knee;
    }

    void ApplyRest()
    {
        leftHip.localRotation  = rightHip.localRotation  = PaddleKickPose.HipRest;
        leftKnee.localRotation = rightKnee.localRotation = PaddleKickPose.KneeRest;
    }

    // ── Legs ──────────────────────────────────────────────────────────────────

    void FindLegParts()
    {
        if (!leftFoot)    leftFoot    = FindFoot(leftKnee);
        if (!rightFoot)   rightFoot   = FindFoot(rightKnee);
        if (!leftPaddle)  leftPaddle  = FindPaddle(leftFoot);
        if (!rightPaddle) rightPaddle = FindPaddle(rightFoot);
        if (leftFoot && leftFoot.TryGetComponent<CapsuleCollider>(out var capsule))
        {
            Vector3 s = leftFoot.lossyScale;
            footRadius = capsule.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
        }
    }

    static Transform FindFoot(Transform knee)
    {
        if (!knee) return null;
        foreach (var t in knee.GetComponentsInChildren<Transform>(true))
            if (t != knee && t.CompareTag("PlayerLeg")) return t;
        return null;
    }

    static Transform FindPaddle(Transform foot)
    {
        if (!foot) return null;
        foreach (var mf in foot.GetComponentsInChildren<MeshFilter>(true))
            if (mf.transform != foot) return mf.transform;
        return null;
    }

    /// Hips, knees and feet mirror each other about the midline between the resting paddles, and
    /// each paddle sits the same distance out from its foot. Both paddles rest where they did.
    /// The browser preview's numbers (PADDLE_OUT and the hip x): the
    /// hips sit +/-0.0702 m about a centre moved 0.153 m along the rig's x, and each paddle 0.153 m
    /// out from its foot. The paddles then rest within 0.3 mm of the scene's spots. The exact
    /// numbers matter: the lane solver's 1 cm grid would round other numbers into other lanes.
    public const float PaddleOut = 0.153f;
    public const float HipHalfSpacing = 0.0702f;

    void MakePaddlesSymmetric()
    {
        if (!HasLegs || leftHip.parent != rightHip.parent || leftHip.parent == null) return;
        SetLocalX(leftHip,  PaddleOut - HipHalfSpacing);
        SetLocalX(rightHip, PaddleOut + HipHalfSpacing);
        Symmetrize(leftKnee, rightKnee, 0f);
        Symmetrize(leftFoot, rightFoot, 0f);
        // The joints only pitch at rest, so the feet's x axes line up with the rig's.
        SetLocalX(leftPaddle,  -PaddleOut);
        SetLocalX(rightPaddle,  PaddleOut);
    }

    /// Places a left/right joint pair at centre -/+ their mean half-spacing; returns that half-spacing.
    static float Symmetrize(Transform left, Transform right, float centre)
    {
        float half = (Mathf.Abs(left.localPosition.x) + Mathf.Abs(right.localPosition.x)) * 0.5f;
        SetLocalX(left,  centre - half);
        SetLocalX(right, centre + half);
        return half;
    }

    static void SetLocalX(Transform t, float x)
    {
        Vector3 p = t.localPosition;
        p.x = x;
        t.localPosition = p;
    }

    public Transform Foot(int leg)   => leg == 0 ? leftFoot : rightFoot;
    public Transform Paddle(int leg) => leg == 0 ? leftPaddle : rightPaddle;

    /// The active themed tool riding a paddle (layer 4), with its bounds in its own local space.
    public bool TryGetTool(int leg, out Matrix4x4 localToWorld, out Bounds localBounds)
    {
        var paddle = Paddle(leg);
        var tool = paddle ? paddle.GetComponentInChildren<KickTool>(false) : null;
        if (tool == null) { localToWorld = default; localBounds = default; return false; }
        localToWorld = tool.transform.localToWorldMatrix;
        localBounds = tool.localBounds;
        return true;
    }

    // ── Hit tests ─────────────────────────────────────────────────────────────

    /// A ball (the plain target) touches a foot or a paddle.
    public bool HitsBall(Vector3 centre, float radius)
    {
        if (!HasLegs) return false;
        for (int l = 0; l < LegCount; l++)
        {
            if ((Foot(l).position - centre).sqrMagnitude <= (radius + footRadius) * (radius + footRadius)) return true;
            Transform paddle = Paddle(l);
            Vector3 local = paddle.InverseTransformPoint(centre);
            Vector3 closest = paddle.TransformPoint(Mathf.Clamp(local.x, -0.5f, 0.5f), Mathf.Clamp(local.y, -0.5f, 0.5f), 0f);
            if ((closest - centre).sqrMagnitude <= radius * radius) return true;
        }
        return false;
    }

    /// A themed target, by its own world bounds, touches a foot, a paddle or a tool.
    public bool HitsBox(Bounds box)
    {
        if (!HasLegs) return false;
        for (int l = 0; l < LegCount; l++)
        {
            if (box.SqrDistance(Foot(l).position) <= footRadius * footRadius) return true;
            if (KickGeometry.ObbOverlapsBox(Paddle(l).localToWorldMatrix, KickGeometry.PaddleBox, box)) return true;
            // The tool hits by its own bounds: what you swing counts.
            if (TryGetTool(l, out Matrix4x4 tm, out Bounds tb) && KickGeometry.ObbOverlapsBox(tm, tb, box)) return true;
        }
        return false;
    }

    // ── Posing for the lane solver ────────────────────────────────────────────

    /// Poses the legs without changing the kick in progress. Call RestorePose afterwards.
    internal void SetPose(bool kicking, PaddleKick kick, float t)
    {
        if (kicking) Pose(kick, t);
        else ApplyRest();
    }

    /// Puts the legs back to the current kick (or rest).
    internal void RestorePose()
    {
        if (_kicking) Apply();
        else ApplyRest();
    }
}
