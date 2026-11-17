using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

/// A theme folder with no panorama, splat, audio or models: every layer can still be switched on
/// without exceptions, and its status says the file is missing instead of "on".
public class MissingThemeFilesTests
{
    const string GameScene = "3DGS_Unity";
    const float TimeoutSeconds = 30f;

    FolderEnvironmentLoader _env;
    string _selectedFolder, _emptyTheme;

    [UnitySetUp]
    public IEnumerator LoadGameSceneWithAnEmptyTheme()
    {
        _selectedFolder = ThemeSelection.Folder;
        _emptyTheme = Path.Combine(Path.GetTempPath(), "reverie-empty-theme-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_emptyTheme);
        ThemeSelection.Folder = _emptyTheme;
        yield return SceneManager.LoadSceneAsync(GameScene, LoadSceneMode.Single);
        yield return null;   // lets the loader's Start run, which resets the toggles
        _env = Object.FindAnyObjectByType<FolderEnvironmentLoader>();
        LogAssert.ignoreFailingMessages = true;   // missing files are logged as warnings
    }

    [TearDown]
    public void Restore()
    {
        ThemeSelection.Folder = _selectedFolder;
        if (Directory.Exists(_emptyTheme)) Directory.Delete(_emptyTheme, true);
    }

    [UnityTest]
    public IEnumerator Skybox_WithoutAPanorama_SaysNoFile()
    {
        _env.skyboxToggle.isOn = true;
        yield return null;
        Assert.That(_env.skyboxToggle.isOn, Is.True, "the layer still switches, so the base room goes away as usual");
        Assert.That(_env.skyboxStatus.text, Is.EqualTo("no file"));
    }

    [UnityTest]
    public IEnumerator Splat_WithoutASceneFile_SaysNoFileUnlessTheSceneHasOne()
    {
        _env.gaussianToggle.isOn = true;
        float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        while (_env.gaussianStatus.text.StartsWith("loading") && Time.realtimeSinceStartup < deadline) yield return null;
        // With nothing in the theme, the loader falls back to the splat assigned in the scene, if any.
        bool sceneSplat = _env.gaussianSpawner.HasSplat;
        Assert.That(_env.gaussianStatus.text, Is.EqualTo(sceneSplat ? "on" : "no file"));
    }

    [UnityTest]
    public IEnumerator AudioAndTool_WithoutFiles_SayNoFile()
    {
        _env.skyboxToggle.isOn = true;   // audio and tool rows are used only while a world is shown
        yield return null;

        _env.audioToggle.isOn = true;
        yield return null;
        Assert.That(_env.audioStatus.text, Is.EqualTo("no file"));

        _env.toolToggle.isOn = true;
        float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        while (_env.IsToolLoading && Time.realtimeSinceStartup < deadline) yield return null;
        yield return null;
        Assert.That(_env.IsToolLoading, Is.False, "the tool load finished");
        Assert.That(_env.toolStatus.text, Is.EqualTo("no file"));

        // Off and on again still works and keeps reporting the missing files.
        _env.toolToggle.isOn = false;
        yield return null;
        Assert.That(_env.toolStatus.text, Is.EqualTo("off"));
        _env.toolToggle.isOn = true;
        yield return null;
        Assert.That(_env.toolStatus.text, Is.EqualTo("no file"));
    }
}
