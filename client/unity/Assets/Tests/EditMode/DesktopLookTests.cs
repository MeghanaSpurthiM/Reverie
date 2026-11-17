using NUnit.Framework;
using UnityEngine;

public class DesktopLookTests
{
    GameObject _space;
    DesktopLook _look;

    [SetUp]
    public void SetUp()
    {
        _space = new GameObject("TrackingSpace");
        _look = _space.AddComponent<DesktopLook>();
        _look.eye = new GameObject("Eye").transform;
        _look.eye.SetParent(_space.transform, false);
        _look.eye.localPosition = new Vector3(0.5f, 1.5f, -2f);
    }

    [TearDown]
    public void TearDown() => Object.DestroyImmediate(_space);

    [Test]
    public void Look_KeepsTheEyeInPlace()
    {
        Vector3 before = _look.eye.position;
        _look.Look(70f, 30f);
        Assert.That((_look.eye.position - before).magnitude, Is.LessThan(0.001f));
    }

    [Test]
    public void Look_TurnsRightAndDown()
    {
        _look.Look(90f, 0f);
        Assert.That(_look.eye.forward.x, Is.GreaterThan(0.99f));

        _look.Look(0f, 45f);
        Assert.That(_look.eye.forward.y, Is.LessThan(-0.5f));
    }

    [Test]
    public void Look_StopsAtMaxPitch()
    {
        _look.Look(0f, 500f);
        float angle = Vector3.Angle(Vector3.forward, _look.eye.forward);
        Assert.That(angle, Is.EqualTo(_look.maxPitch).Within(0.01f));
    }
}
