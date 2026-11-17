using NUnit.Framework;
using UnityEngine;

/// Golden foot positions were sampled from the Animator rig used in the study, in rig-local
/// coordinates, so a change to the kick motion fails here.
public class PaddleKickerTests
{
    static readonly Vector3 LeftHipPosition  = new Vector3(-0.0704852343f, 0.819730759f, 0.0197751522f);
    static readonly Vector3 RightHipPosition = new Vector3(0.06988832f, 0.819730759f, 0.0197751522f);
    static readonly Vector3 LeftKneeOffset   = new Vector3(-0.03066892f, -0.364209384f, -0.00668891426f);
    static readonly Vector3 RightKneeOffset  = new Vector3(0.03066892f, -0.3642094f, -0.006688914f);
    static readonly Vector3 LeftFootOffset   = new Vector3(-0.011783774f, -0.437993238f, 0.0708685107f);
    static readonly Vector3 RightFootOffset  = new Vector3(0.01178378f, -0.43799324f, 0.0708685f);

    GameObject _root;
    PaddleKicker _kicker;
    Transform _leftFoot, _rightFoot;

    static Transform Joint(string name, Transform parent, Vector3 position)
    {
        var joint = new GameObject(name).transform;
        joint.SetParent(parent, false);
        joint.localPosition = position;
        return joint;
    }

    [SetUp]
    public void SetUp()
    {
        _root = new GameObject("PaddleRig");
        _kicker = _root.AddComponent<PaddleKicker>();
        _kicker.leftHip   = Joint("LeftHip", _root.transform, LeftHipPosition);
        _kicker.leftKnee  = Joint("LeftKnee", _kicker.leftHip, LeftKneeOffset);
        _leftFoot         = Joint("LeftFoot", _kicker.leftKnee, LeftFootOffset);
        _kicker.rightHip  = Joint("RightHip", _root.transform, RightHipPosition);
        _kicker.rightKnee = Joint("RightKnee", _kicker.rightHip, RightKneeOffset);
        _rightFoot        = Joint("RightFoot", _kicker.rightKnee, RightFootOffset);
    }

    [TearDown]
    public void TearDown() => Object.DestroyImmediate(_root);

    /// Plays a kick up to kick time t.
    void PlayTo(PaddleKick kick, float t)
    {
        _kicker.Play(kick);
        _kicker.Tick(t / _kicker.animationSpeed);
    }

    static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.That((expected - actual).magnitude, Is.LessThan(0.002f), $"expected {expected:F4}, got {actual:F4}");
    }

    [Test]
    public void FootPosition_MatchesStudyRig_AtPeak()
    {
        PlayTo(PaddleKick.Front, 0.45f);
        AssertNear(new Vector3(-0.1129f, 0.8646f, 0.8247f), _leftFoot.position);

        PlayTo(PaddleKick.Side, 0.45f);
        AssertNear(new Vector3(-0.3857f, 0.8646f, 0.7616f), _leftFoot.position);

        PlayTo(PaddleKick.Wide, 0.45f);
        AssertNear(new Vector3(-0.8414f, 0.8646f, 0.2552f), _leftFoot.position);
    }

    [Test]
    public void FootPosition_MatchesStudyRig_OnTheWayUp()
    {
        PlayTo(PaddleKick.Front, 0.3f);
        AssertNear(new Vector3(-0.1129f, 0.6433f, 0.7940f), _leftFoot.position);
    }

    [TestCase(PaddleKick.Side)]
    [TestCase(PaddleKick.Wide)]
    public void Legs_TurnOutwardInOppositeDirections(PaddleKick kick)
    {
        PlayTo(kick, 0.5f);
        Assert.That(_leftFoot.position.x, Is.LessThan(LeftHipPosition.x));
        Assert.That(_rightFoot.position.x, Is.GreaterThan(RightHipPosition.x));
    }

    [Test]
    public void Kick_EndsAtRestPose()
    {
        PlayTo(PaddleKick.Wide, PaddleKickPose.Duration);
        Assert.That(Quaternion.Angle(PaddleKickPose.HipRest, _kicker.leftHip.localRotation), Is.LessThan(0.01f));
        Assert.That(Quaternion.Angle(PaddleKickPose.KneeRest, _kicker.rightKnee.localRotation), Is.LessThan(0.01f));
    }

    [Test]
    public void Tick_WithoutKick_LeavesJointsAlone()
    {
        _kicker.leftKnee.localRotation = Quaternion.Euler(10f, 20f, 30f);
        Quaternion before = _kicker.leftKnee.localRotation;
        _kicker.Tick(0.1f);
        Assert.That(Quaternion.Angle(before, _kicker.leftKnee.localRotation), Is.LessThan(0.001f));
    }

    [Test]
    public void Play_DuringKick_RestartsFromTheBeginning()
    {
        PlayTo(PaddleKick.Front, 0.5f);
        _kicker.Play(PaddleKick.Side);
        Assert.That(Quaternion.Angle(PaddleKickPose.KneeRest, _kicker.leftKnee.localRotation), Is.LessThan(0.01f));
    }

    [TestCase(0f, 90f)]
    [TestCase(0.25f, 45f)]
    [TestCase(0.5f, 0f)]
    [TestCase(0.75f, 45f)]
    [TestCase(1f, 90f)]
    public void KneePitch_FollowsTheKickCurve(float t, float degrees)
    {
        Assert.That(PaddleKickPose.KneePitch(t), Is.EqualTo(degrees).Within(0.001f));
    }
}
