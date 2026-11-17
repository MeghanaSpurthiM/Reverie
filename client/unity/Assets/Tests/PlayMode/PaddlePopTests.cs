using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

/// Runs in the real game scene: a kick must pop a bubble that the resting paddles do not touch.
public class PaddlePopTests
{
    const string GameScene = "3DGS_Unity";
    const float RestWaitSeconds = 0.4f;  // longer than Bubble.spawnGracePeriod
    const float PeakKickTime = 0.5f;

    [UnitySetUp]
    public IEnumerator LoadGameScene()
    {
        yield return SceneManager.LoadSceneAsync(GameScene, LoadSceneMode.Single);
        yield return null;

        Object.FindAnyObjectByType<BubbleSpawnerFixed>().enabled = false;
        foreach (var bubble in Object.FindObjectsByType<Bubble>(FindObjectsSortMode.None))
            Object.Destroy(bubble.gameObject);
        yield return null;
    }

    [UnityTest]
    public IEnumerator Kick_PopsBubbleAtItsPeak([Values] PaddleKick kick)
    {
        var kicker = Object.FindAnyObjectByType<PaddleKicker>();
        var spawner = Object.FindAnyObjectByType<BubbleSpawnerFixed>();
        var foot = FindFoot(kicker);

        // Measure where this kick's foot peaks, then let the rig settle back to rest.
        kicker.Play(kick);
        kicker.Tick(PeakKickTime / kicker.animationSpeed);
        Vector3 peak = foot.position;
        kicker.Tick(1f);
        yield return null;

        var bubble = Object.Instantiate(spawner.bubblePrefab, peak, Quaternion.identity).GetComponent<Bubble>();
        bubble.speed = 0f;
        yield return new WaitForSeconds(RestWaitSeconds);
        Assert.That(bubble != null, "the bubble popped before the kick");

        int scoreBefore = ScoreManager.globalScore;
        kicker.Play(kick);
        yield return new WaitForSeconds(PaddleKickPose.Duration / kicker.animationSpeed);

        Assert.That(bubble == null, $"{kick} kick did not pop the bubble");
        Assert.That(ScoreManager.globalScore, Is.EqualTo(scoreBefore + 1));
    }

    static Transform FindFoot(PaddleKicker kicker)
    {
        foreach (var t in kicker.leftKnee.GetComponentsInChildren<Transform>())
            if (t.CompareTag("PlayerLeg")) return t;
        Assert.Fail("no PlayerLeg foot under the left knee");
        return null;
    }
}
