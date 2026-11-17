using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

public class BaseRoomFilesTests
{
    string _folder;

    [SetUp]
    public void MakeFolder()
    {
        _folder = Path.Combine(Path.GetTempPath(), "ReverieBaseRoomFiles_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void DeleteFolder() => Directory.Delete(_folder, true);

    void Touch(params string[] names)
    {
        foreach (string name in names) File.WriteAllText(Path.Combine(_folder, name), "");
    }

    [Test]
    public void List_PutsRoomFirstThenNamedRoomsAlphabetically()
    {
        Touch("room_office.gltf", "room.glb", "room_Kitchen.glb", "README.md", "room_attic.fbx", "other.glb", "room.glb.meta");
        var names = BaseRoomFiles.List(_folder).Select(Path.GetFileName).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "room.glb", "room_Kitchen.glb", "room_office.gltf" }));
    }

    [Test]
    public void List_WorksWithoutTheMainRoom()
    {
        Touch("room_b.glb", "room_a.glb");
        Assert.That(BaseRoomFiles.List(_folder).Select(Path.GetFileName), Is.EqualTo(new[] { "room_a.glb", "room_b.glb" }));
    }

    [Test]
    public void List_IsEmptyForAMissingFolder()
    {
        Assert.That(BaseRoomFiles.List(Path.Combine(_folder, "missing")), Is.Empty);
        Assert.That(BaseRoomFiles.List(null), Is.Empty);
    }

    [Test]
    public void DisplayName_DropsThePrefix()
    {
        Assert.That(BaseRoomFiles.DisplayName("/x/room_kitchen.glb"), Is.EqualTo("kitchen"));
        Assert.That(BaseRoomFiles.DisplayName("/x/room.glb"), Is.EqualTo("room"));
    }

    [Test]
    public void Checks_FindPinkMaterialsAndStrayCameras()
    {
        var room = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            var standard = Shader.Find("Standard");
            if (standard != null) room.GetComponent<Renderer>().sharedMaterial = new Material(standard) { name = "Old" };
            new GameObject("DemoCamera", typeof(Camera), typeof(AudioListener)).transform.SetParent(room.transform);

            var issues = BaseRoomChecks.Inspect(room);
            if (standard != null) Assert.That(issues.Any(i => i.Contains("render pink")), Is.True);
            Assert.That(issues.Any(i => i.Contains("Camera 'DemoCamera'")), Is.True);
            Assert.That(BaseRoomChecks.DisableStrayPlayerParts(room), Is.EqualTo(2));
            Assert.That(BaseRoomChecks.DisableStrayPlayerParts(room), Is.Zero);
        }
        finally { UnityEngine.Object.DestroyImmediate(room); }
    }
}
