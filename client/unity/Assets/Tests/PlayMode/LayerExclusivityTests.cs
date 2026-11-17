using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

/// The skybox, 3DGS scene and base room replace each other: after any key press (2, 3 or 5) at most
/// one of them is on, and the room object is shown exactly when the room layer is on.
public class LayerExclusivityTests
{
    const string GameScene = "3DGS_Unity";
    FolderEnvironmentLoader _env;
    string _selectedFolder;

    [UnitySetUp]
    public IEnumerator LoadGameScene()
    {
        _selectedFolder = ThemeSelection.Folder;
        ThemeSelection.Clear();
        yield return SceneManager.LoadSceneAsync(GameScene, LoadSceneMode.Single);
        yield return null;
        _env = Object.FindAnyObjectByType<FolderEnvironmentLoader>();
        // Layer content may be missing (sample themes are a separate download); this test is about which layer is on.
        LogAssert.ignoreFailingMessages = true;
    }

    [TearDown]
    public void RestoreSelection() => ThemeSelection.Folder = _selectedFolder;

    bool Flag(string field) =>
        (bool)typeof(FolderEnvironmentLoader).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_env);

    void Press(string toggleMethod) =>
        typeof(FolderEnvironmentLoader).GetMethod(toggleMethod, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_env, null);

    void AssertOneLayer(string after)
    {
        bool sky = Flag("_skyboxOn"), splat = Flag("_gaussianOn"), room = Flag("_roomOn");
        int on = (sky ? 1 : 0) + (splat ? 1 : 0) + (room ? 1 : 0);
        Assert.That(on, Is.LessThanOrEqualTo(1), $"after {after}: skybox={sky} 3dgs={splat} room={room}");
        Assert.That(_env.roomObject.activeSelf, Is.EqualTo(room), $"after {after}: room object");
        Assert.That(_env.roomToggle.isOn, Is.EqualTo(room), $"after {after}: room switch");
        Assert.That(_env.skyboxToggle.isOn, Is.EqualTo(sky), $"after {after}: skybox switch");
        Assert.That(_env.gaussianToggle.isOn, Is.EqualTo(splat), $"after {after}: 3DGS switch");
    }

    [UnityTest]
    public IEnumerator EachKey_LeavesOnlyOneEnvironmentLayer()
    {
        AssertOneLayer("start");
        Assert.That(Flag("_roomOn"), Is.True, "the room shows at start");

        var keys = new[]
        {
            ("2", "ToggleSkybox"), ("5", "ToggleRoom"), ("3", "ToggleGaussian"), ("5", "ToggleRoom"),
            ("5", "ToggleRoom"), ("2", "ToggleSkybox"), ("3", "ToggleGaussian"), ("3", "ToggleGaussian"),
            ("5", "ToggleRoom"), ("2", "ToggleSkybox"), ("2", "ToggleSkybox"), ("5", "ToggleRoom"),
        };
        foreach (var (key, method) in keys)
        {
            bool wasOn = method switch
            {
                "ToggleSkybox" => Flag("_skyboxOn"),
                "ToggleGaussian" => Flag("_gaussianOn"),
                _ => Flag("_roomOn"),
            };
            Press(method);
            yield return null;
            AssertOneLayer($"key {key}");
            if (!wasOn)
            {
                string field = method == "ToggleSkybox" ? "_skyboxOn" : method == "ToggleGaussian" ? "_gaussianOn" : "_roomOn";
                Assert.That(Flag(field), Is.True, $"key {key} turns its layer on");
            }
        }
    }

    [UnityTest]
    public IEnumerator RoomKey_FromThe3dgsScene_SwitchesTheSplatOff()
    {
        Press("ToggleGaussian");
        yield return null;
        Assert.That(Flag("_gaussianOn"), Is.True);
        Assert.That(Flag("_roomOn"), Is.False);

        Press("ToggleRoom");   // the reported bug: this used to leave the 3DGS scene on
        yield return null;
        Assert.That(Flag("_roomOn"), Is.True);
        Assert.That(Flag("_gaussianOn"), Is.False);
        AssertOneLayer("key 5");
    }
}
