using System.Collections;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

/// Runs in the real game scene against the default theme and the placeholder room.
public class ThemeLayerTests
{
    const string GameScene = "3DGS_Unity";
    const float LoadTimeoutSeconds = 120f;

    FolderEnvironmentLoader _loader;
    string _selectedFolder;

    [UnitySetUp]
    public IEnumerator LoadGameScene()
    {
        _selectedFolder = ThemeSelection.Folder;
        ThemeSelection.Clear();
        yield return SceneManager.LoadSceneAsync(GameScene, LoadSceneMode.Single);
        yield return Resources.UnloadUnusedAssets();  // otherwise it can destroy textures mid-load
        yield return null;  // lets the loader's Start run, which resets the toggles
        _loader = Object.FindAnyObjectByType<FolderEnvironmentLoader>();
    }

    [TearDown]
    public void RestoreSelection() => ThemeSelection.Folder = _selectedFolder;

    static IEnumerator WaitForOn(Toggle toggle, TMP_Text status)
    {
        toggle.isOn = true;
        float deadline = Time.realtimeSinceStartup + LoadTimeoutSeconds;
        while (status.text != "on" && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.That(status.text, Is.EqualTo("on"));
    }

    [UnityTest]
    public IEnumerator DefaultTheme_LoadsTheSplat()
    {
        yield return WaitForOn(_loader.gaussianToggle, _loader.gaussianStatus);
    }

    [UnityTest]
    public IEnumerator DefaultTheme_LoadsTheToolAndTarget()
    {
        _loader.toolToggle.isOn = true;  // the status says "on" before the models have loaded
        float deadline = Time.realtimeSinceStartup + LoadTimeoutSeconds;
        while ((GameObject.Find("_Tool2") == null || _loader.IsToolLoading) && Time.realtimeSinceStartup < deadline)
            yield return null;

        Assert.That(GameObject.Find("_Tool1"), Is.Not.Null);
        Assert.That(GameObject.Find("_Tool2"), Is.Not.Null);
        yield return WaitForOn(_loader.toolToggle, _loader.toolStatus);
    }

    [UnityTest]
    public IEnumerator BaseRoom_ShowsThePlaceholder()
    {
        yield return null;
        var room = _loader.roomObject.GetComponent<BaseRoomLoader>();
        Assert.That(room, Is.Not.Null);
        Assert.That(room.placeholder.activeInHierarchy, Is.True);
    }
}
