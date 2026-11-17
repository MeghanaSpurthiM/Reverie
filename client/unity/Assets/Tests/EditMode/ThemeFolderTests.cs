using System.IO;
using NUnit.Framework;

public class ThemeFolderTests
{
    string _folder;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_folder, true);

    string Touch(string name)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Test]
    public void FindTool_PrefersThePlainName()
    {
        Touch("tool_axe.glb");
        string plain = Touch("tool.glb");
        Assert.That(ThemeFolder.FindTool(_folder), Is.EqualTo(plain));
    }

    [Test]
    public void FindTool_FallsBackToTheFirstNamedFile()
    {
        Touch("tool_zebra.glb");
        string first = Touch("tool_axe.glb");
        Assert.That(ThemeFolder.FindTool(_folder), Is.EqualTo(first));
    }

    [Test]
    public void FindTarget_AcceptsGltf()
    {
        string target = Touch("target.gltf");
        Assert.That(ThemeFolder.FindTarget(_folder), Is.EqualTo(target));
    }

    [Test]
    public void FindTool_IgnoresTheTarget()
    {
        Touch("target.glb");
        Assert.That(ThemeFolder.FindTool(_folder), Is.Null);
    }

    [Test]
    public void FindRoom_RequiresThePlainName()
    {
        Touch("room_old.glb");
        Assert.That(ThemeFolder.FindRoom(_folder), Is.Null);
        string room = Touch("room.glb");
        Assert.That(ThemeFolder.FindRoom(_folder), Is.EqualTo(room));
    }

    string Theme(string name, string file)
    {
        string folder = Path.Combine(_folder, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, file), "");
        return folder;
    }

    [Test]
    public void ListThemes_IsAlphabeticalAndSkipsEmptyTemplates()
    {
        string forest = Theme("forest", "audio.wav");
        string beach = Theme("beach", "panorama.jpg");
        Theme("my-theme", "README.md");
        Assert.That(ThemeFolder.ListThemes(_folder), Is.EqualTo(new[] { beach, forest }));
    }

    [Test]
    public void Next_WrapsAround()
    {
        var themes = new[] { Theme("beach", "a.wav"), Theme("forest", "a.wav") };
        Assert.That(ThemeFolder.Next(themes, themes[0]), Is.EqualTo(themes[1]));
        Assert.That(ThemeFolder.Next(themes, themes[1]), Is.EqualTo(themes[0]));
    }

    [Test]
    public void Next_StartsAtTheFirstThemeFromAnUnknownFolder()
    {
        var themes = new[] { Theme("beach", "a.wav"), Theme("forest", "a.wav") };
        Assert.That(ThemeFolder.Next(themes, _folder), Is.EqualTo(themes[0]));
    }

    [Test]
    public void Next_HasNothingToSwitchToWithOneTheme()
    {
        var themes = new[] { Theme("beach", "a.wav") };
        Assert.That(ThemeFolder.Next(themes, themes[0]), Is.Null);
    }

    [Test]
    public void MissingFolder_FindsNothing()
    {
        string missing = Path.Combine(_folder, "missing");
        Assert.That(ThemeFolder.FindTool(missing), Is.Null);
        Assert.That(ThemeFolder.FindRoom(missing), Is.Null);
    }
}
