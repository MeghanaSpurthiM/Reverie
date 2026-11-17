using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

/// Runs in the real game scene: the solved lanes (KickLanes, as in the browser preview) mirror each
/// other, stay clear of the resting paddles, and are reached by their own kick.
public class KickLaneTests
{
    const string GameScene = "3DGS_Unity";

    PaddleKicker _kicker;
    BubbleSpawnerFixed _spawner;

    [UnitySetUp]
    public IEnumerator LoadGameScene()
    {
        yield return SceneManager.LoadSceneAsync(GameScene, LoadSceneMode.Single);
        yield return null;
        _kicker = Object.FindAnyObjectByType<PaddleKicker>();
        _spawner = Object.FindAnyObjectByType<BubbleSpawnerFixed>();
        _spawner.enabled = false;
        foreach (var bubble in Object.FindObjectsByType<Bubble>(FindObjectsSortMode.None))
            Object.Destroy(bubble.gameObject);
        yield return null;
    }

    Vector3[] SolveBallLanes(out float radius)
    {
        radius = 0.1f;
        var homes = new List<Vector3>();
        foreach (var p in _spawner.spawnPoints) homes.Add(p.position);
        return KickLanes.Compute(_kicker, new Bounds(Vector3.zero, Vector3.one * (2f * radius)), homes,
                                 _spawner.pairKicks, 1f, includeTools: false);
    }

    [Test]
    public void Paddles_RestSymmetricallyAboutTheirFeet()
    {
        Transform rig = _kicker.leftHip.parent;
        float leftOut  = rig.InverseTransformPoint(_kicker.leftFoot.position).x - rig.InverseTransformPoint(_kicker.leftPaddle.position).x;
        float rightOut = rig.InverseTransformPoint(_kicker.rightPaddle.position).x - rig.InverseTransformPoint(_kicker.rightFoot.position).x;
        Assert.That(leftOut, Is.EqualTo(rightOut).Within(1e-3f));
    }

    [Test]
    public void BallLanes_MirrorAndClearTheRestingPaddles()
    {
        var lanes = SolveBallLanes(out float r);
        Assert.That(lanes, Is.Not.Null, "no lanes fit the ball");

        float xm = (_kicker.leftPaddle.position.x + _kicker.rightPaddle.position.x) * 0.5f;
        for (int i = 0; i < lanes.Length; i += 2)
        {
            Assert.That(lanes[i].z, Is.EqualTo(lanes[i + 1].z).Within(1e-4f), $"pair {i / 2} z");
            Assert.That(lanes[i].x + lanes[i + 1].x, Is.EqualTo(2f * xm).Within(1e-3f), $"pair {i / 2} mirrors");
        }
        foreach (var lane in lanes)
            for (float y = 1f; y >= -4f; y -= 0.02f)
                Assert.That(_kicker.HitsBall(new Vector3(lane.x, y, lane.z), r), Is.False,
                            $"a resting paddle or foot touches the lane at {lane} (y {y:F2})");
    }

    // window.reverieGame.ballLanes in the browser preview, in Unity coordinates (x mirrored back).
    static readonly Vector3[] PreviewBallLanes =
    {
        new Vector3(-0.83f, 2.41f, 2.553f), new Vector3(0.82f, 2.41f, 2.553f),   // wide
        new Vector3(-0.75f, 2.41f, 1.893f), new Vector3(0.74f, 2.41f, 1.893f),   // side
        new Vector3(-0.61f, 2.41f, 1.673f), new Vector3(0.60f, 2.41f, 1.673f),   // front
    };

    // Within one cell of the solver's 1 cm grid on each axis. Some lanes sit on exact grid ties
    // (a 4 cm spread gap, a box edge on a cell edge) that the preview itself settles by the last
    // bits of its double arithmetic; Unity's float transforms can settle them one cell over.
    const float OneCell = 0.0105f;

    [Test]
    public void BallLanes_MatchTheBrowserPreview()
    {
        var lanes = SolveBallLanes(out _);
        Assert.That(lanes, Is.Not.Null);
        for (int i = 0; i < lanes.Length; i++)
        {
            Vector3 d = lanes[i] - PreviewBallLanes[i];
            Assert.That(Mathf.Abs(d.x) <= OneCell && Mathf.Abs(d.z) <= OneCell,
                        $"lane {i}: {lanes[i]:F3}, preview {PreviewBallLanes[i]:F3}");
        }
    }

    [Test]
    public void BallLanes_AreReachedByTheirOwnKick()
    {
        var lanes = SolveBallLanes(out float r);
        Assert.That(lanes, Is.Not.Null);
        for (int pair = 0; pair < lanes.Length / 2; pair++)
        {
            PaddleKick kick = _spawner.pairKicks[pair];
            foreach (var lane in new[] { lanes[pair * 2], lanes[pair * 2 + 1] })
            {
                bool reached = false;
                for (float t = 0f; t < PaddleKickPose.Duration && !reached; t += 0.01f)
                {
                    _kicker.Play(kick);
                    _kicker.Tick(t / _kicker.animationSpeed);
                    for (float y = 1f; y >= -4f && !reached; y -= 0.02f)
                        reached = _kicker.HitsBall(new Vector3(lane.x, y, lane.z), r);
                }
                _kicker.Tick(10f);
                Assert.That(reached, $"{kick} kick never reaches its lane at {lane}");
            }
        }
    }
}
