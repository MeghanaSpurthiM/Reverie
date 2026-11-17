using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

/// Switches base rooms quickly, also while room files are loading, and checks that exactly one room
/// shows, nothing throws, and every loaded mesh and material is freed.
public class BaseRoomSwitchingTests
{
    const string GameScene = "3DGS_Unity";
    const string TestPrefix = "ReverieTestRoom";
    const float TimeoutSeconds = 60f;

    string _folder;
    GameObject _root;
    BaseRoomLoader _loader;
    GameObject _placeholder;
    readonly List<GameObject> _variants = new();

    [SetUp]
    public void MakeRoomFiles()
    {
        _folder = Path.Combine(Path.GetTempPath(), "ReverieBaseRoomTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "room.glb"), MakeFloorGlb("small", 4));
        File.WriteAllBytes(Path.Combine(_folder, "room_big.glb"), MakeFloorGlb("big", 300));   // takes a few frames
        File.WriteAllBytes(Path.Combine(_folder, "room_broken.glb"), Encoding.ASCII.GetBytes("glTF this is not a model"));
    }

    [TearDown]
    public void Cleanup()
    {
        if (_root != null) Object.Destroy(_root);
        _variants.Clear();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    /// Room
    /// ├── PlaceholderRoom
    /// ├── RoomVariant_A
    /// └── RoomVariant_B
    /// plus room.glb, room_big.glb and room_broken.glb in the room folder.
    void BuildRoom(bool withFiles = true)
    {
        _root = new GameObject("Room");
        _root.SetActive(false);   // so the fields are set before Start runs
        _placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _placeholder.name = "PlaceholderRoom";
        _placeholder.transform.SetParent(_root.transform, false);
        foreach (string name in new[] { "RoomVariant_A", "RoomVariant_B" })
        {
            var variant = GameObject.CreatePrimitive(PrimitiveType.Plane);
            variant.name = name;
            variant.transform.SetParent(_root.transform, false);
            _variants.Add(variant);
        }
        _loader = _root.AddComponent<BaseRoomLoader>();
        _loader.placeholder = _placeholder;
        _loader.logRoomChecks = false;
        _loader.RoomFolderPath = withFiles ? _folder : Path.Combine(_folder, "missing");
        _root.SetActive(true);
    }

    IEnumerator WaitUntilIdle(BaseRoomLoader loader)
    {
        float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        while (loader != null && loader.IsLoading && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.That(loader == null || !loader.IsLoading, "a room load did not finish");
        yield return null;   // Destroy runs at the end of the frame
    }

    static int Count<T>() where T : Object =>
        Resources.FindObjectsOfTypeAll<T>().Count(o => o.name.StartsWith(TestPrefix));

    static int LoadedRoomObjects() =>
        Resources.FindObjectsOfTypeAll<GameObject>().Count(go => go.name.StartsWith("_LoadedRoom") && go.scene.IsValid());

    int VisibleRooms()
    {
        int visible = _variants.Count(v => v.activeSelf) + (_placeholder.activeSelf ? 1 : 0);
        if (_loader.LoadedRoom != null && _loader.LoadedRoom.activeSelf) visible++;
        return visible;
    }

    void AssertNoLeaks()
    {
        Assert.That(LoadedRoomObjects(), Is.LessThanOrEqualTo(_loader != null && _loader.LoadedRoom != null ? 1 : 0), "loaded room objects");
        bool fileShown = _loader != null && _loader.LoadedRoom != null;
        Assert.That(Count<Mesh>(), Is.EqualTo(fileShown ? 1 : 0), "meshes from room files");
        Assert.That(Count<Material>(), Is.EqualTo(fileShown ? 1 : 0), "materials from room files");
    }

    [UnityTest]
    public IEnumerator ListsVariantsThenFiles()
    {
        BuildRoom();
        yield return null;
        var names = _loader.Rooms.Select(r => r.Name).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "RoomVariant_A", "RoomVariant_B", "room", "big", "broken" }));
        Assert.That(_loader.CurrentIndex, Is.EqualTo(0));
        Assert.That(_variants[0].activeSelf, Is.True);
        Assert.That(_placeholder.activeSelf, Is.False);
        Assert.That(VisibleRooms(), Is.EqualTo(1));
    }

    [UnityTest]
    public IEnumerator WithoutRooms_ShowsThePlaceholder()
    {
        BuildRoom(withFiles: false);
        foreach (var v in _variants) Object.Destroy(v);
        _variants.Clear();
        yield return null;
        _loader.Refresh();
        _loader.Select(0);
        Assert.That(_loader.Rooms.Single().IsPlaceholder, Is.True);
        Assert.That(_placeholder.activeSelf, Is.True);
        _loader.Next();
        Assert.That(_placeholder.activeSelf, Is.True);
    }

    [UnityTest]
    public IEnumerator CyclesThroughEveryRoom_OneAtATime()
    {
        LogAssert.ignoreFailingMessages = true;   // glTFast logs an error for room_broken.glb
        BuildRoom();
        yield return null;

        for (int i = 0; i < _loader.Rooms.Count * 2; i++)
        {
            _loader.Next();
            yield return WaitUntilIdle(_loader);
            Assert.That(VisibleRooms(), Is.EqualTo(1), $"after switching to {_loader.Current.Name}");
            AssertNoLeaks();

            var room = _loader.Current;
            if (room.File != null && room.Name != "broken")
            {
                Assert.That(_loader.LoadedRoom, Is.Not.Null, room.Name);
                Assert.That(_placeholder.activeSelf, Is.False);
            }
        }
    }

    [UnityTest]
    public IEnumerator BrokenFile_FallsBackToThePlaceholder()
    {
        LogAssert.ignoreFailingMessages = true;
        BuildRoom();
        yield return null;

        _loader.Select(_loader.Rooms.ToList().FindIndex(r => r.Name == "broken"));
        yield return WaitUntilIdle(_loader);

        Assert.That(_placeholder.activeSelf, Is.True);
        Assert.That(_loader.LastError, Does.Contain("broken"));
        Assert.That(VisibleRooms(), Is.EqualTo(1));
        AssertNoLeaks();

        _loader.Select(0);   // and it recovers
        Assert.That(_variants[0].activeSelf, Is.True);
        Assert.That(_placeholder.activeSelf, Is.False);
    }

    [UnityTest]
    public IEnumerator RapidCycling_WhileLoading_DoesNotLeak()
    {
        BuildRoom();
        yield return null;
        int big = _loader.Rooms.ToList().FindIndex(r => r.Name == "big");
        int small = _loader.Rooms.ToList().FindIndex(r => r.Name == "room");

        for (int frame = 0; frame < 90; frame++)
        {
            // Mix single steps, several switches in one frame, and restarts of the same big load.
            switch (frame % 4)
            {
                case 0: _loader.Select(big); break;
                case 1: _loader.Next(); _loader.Previous(); _loader.Select(small); break;
                case 2: _loader.Select(big); _loader.Select(big); break;
                default: _loader.Next(); break;
            }
            Assert.That(VisibleRooms(), Is.LessThanOrEqualTo(1));
            Assert.That(LoadedRoomObjects(), Is.LessThanOrEqualTo(2));   // the shown one and one being destroyed
            yield return null;
        }

        _loader.Select(big);
        yield return WaitUntilIdle(_loader);
        Assert.That(_loader.LoadedRoom, Is.Not.Null, "the last request wins");
        Assert.That(VisibleRooms(), Is.EqualTo(1));
        AssertNoLeaks();

        _loader.Select(0);
        yield return WaitUntilIdle(_loader);
        AssertNoLeaks();
        Assert.That(Count<Mesh>(), Is.Zero);
    }

    [UnityTest]
    public IEnumerator HidingTheRoomLayer_WhileLoading_IsSafe()
    {
        BuildRoom();
        yield return null;
        int big = _loader.Rooms.ToList().FindIndex(r => r.Name == "big");
        _loader.Select(big);
        for (int frame = 0; frame < 20; frame++)
        {
            _root.SetActive(frame % 2 == 1);   // what key 5 does
            yield return null;
        }
        _root.SetActive(true);
        yield return WaitUntilIdle(_loader);
        Assert.That(_loader.LoadedRoom, Is.Not.Null);
        Assert.That(VisibleRooms(), Is.EqualTo(1));
        AssertNoLeaks();
    }

    [UnityTest]
    public IEnumerator DestroyingTheRoom_WhileLoading_FreesEverything()
    {
        BuildRoom();
        yield return null;
        _loader.Select(_loader.Rooms.ToList().FindIndex(r => r.Name == "big"));
        var load = _loader.CurrentLoad;
        yield return null;
        Object.Destroy(_root);
        _root = null;

        float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        while (!load.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
        yield return null;
        Assert.That(load.IsCompleted, Is.True);
        Assert.That(LoadedRoomObjects(), Is.Zero);
        Assert.That(Count<Mesh>(), Is.Zero);
        Assert.That(Count<Material>(), Is.Zero);
    }

    [UnityTest]
    public IEnumerator ReloadingTheGameScene_WhileLoading_FreesEverything()
    {
        yield return SceneManager.LoadSceneAsync(GameScene, LoadSceneMode.Single);
        yield return null;
        var loader = Object.FindAnyObjectByType<BaseRoomLoader>(FindObjectsInactive.Include);
        Assert.That(loader, Is.Not.Null);
        Assert.That(loader.placeholder.activeInHierarchy, Is.True, "the shipped scene shows the placeholder");

        loader.RoomFolderPath = _folder;
        loader.Refresh();
        loader.Select(loader.Rooms.ToList().FindIndex(r => r.Name == "big"));
        var load = loader.CurrentLoad;

        yield return SceneManager.LoadSceneAsync(GameScene, LoadSceneMode.Single);   // what T (next theme) does
        float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        while (!load.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
        yield return null;

        Assert.That(load.IsCompleted, Is.True);
        Assert.That(LoadedRoomObjects(), Is.Zero);
        Assert.That(Count<Mesh>(), Is.Zero);
        var fresh = Object.FindAnyObjectByType<BaseRoomLoader>(FindObjectsInactive.Include);
        Assert.That(fresh.placeholder.activeInHierarchy, Is.True);
    }

    [UnityTest]
    public IEnumerator RoomLayerToggle_InGameScene_WhileCycling()
    {
        yield return SceneManager.LoadSceneAsync(GameScene, LoadSceneMode.Single);
        yield return null;
        var env = Object.FindAnyObjectByType<FolderEnvironmentLoader>();
        var loader = env.roomObject.GetComponent<BaseRoomLoader>();
        loader.RoomFolderPath = _folder;
        loader.Refresh();
        loader.logRoomChecks = false;

        for (int frame = 0; frame < 40; frame++)
        {
            if (frame % 3 == 0) loader.Next();
            if (frame % 5 == 0) env.roomToggle.isOn = !env.roomToggle.isOn;   // the panel switch for key 5
            yield return null;
        }
        env.roomToggle.isOn = true;
        loader.Select(loader.Rooms.ToList().FindIndex(r => r.Name == "room"));
        yield return WaitUntilIdle(loader);

        Assert.That(env.roomObject.activeSelf, Is.True);
        Assert.That(loader.LoadedRoom, Is.Not.Null);
        Assert.That(loader.placeholder.activeSelf, Is.False);
        Assert.That(LoadedRoomObjects(), Is.EqualTo(1));
        Assert.That(Count<Mesh>(), Is.EqualTo(1));
    }

    /// A glTF-Binary floor of <paramref name="cells"/>² quads with one mesh and one material.
    static byte[] MakeFloorGlb(string label, int cells)
    {
        int side = cells + 1, vertexCount = side * side, indexCount = cells * cells * 6;
        var bin = new MemoryStream();
        var writer = new BinaryWriter(bin);
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            writer.Write(x * 6f / cells - 3f); writer.Write(0f); writer.Write(z * 6f / cells - 3f);
        }
        int positionBytes = (int)bin.Length;
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            uint a = (uint)(z * side + x), b = a + 1, c = a + (uint)side, d = c + 1;
            writer.Write(a); writer.Write(c); writer.Write(b);
            writer.Write(b); writer.Write(c); writer.Write(d);
        }
        writer.Flush();
        int indexBytes = (int)bin.Length - positionBytes;

        string json =
            "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
            $"\"nodes\":[{{\"mesh\":0,\"name\":\"{TestPrefix}_{label}\"}}]," +
            $"\"meshes\":[{{\"name\":\"{TestPrefix}Mesh_{label}\",\"primitives\":[{{\"attributes\":{{\"POSITION\":0}},\"indices\":1,\"material\":0}}]}}]," +
            $"\"materials\":[{{\"name\":\"{TestPrefix}Mat_{label}\",\"pbrMetallicRoughness\":{{\"baseColorFactor\":[0.6,0.6,0.6,1]}}}}]," +
            $"\"buffers\":[{{\"byteLength\":{bin.Length}}}]," +
            $"\"bufferViews\":[{{\"buffer\":0,\"byteOffset\":0,\"byteLength\":{positionBytes},\"target\":34962}}," +
            $"{{\"buffer\":0,\"byteOffset\":{positionBytes},\"byteLength\":{indexBytes},\"target\":34963}}]," +
            $"\"accessors\":[{{\"bufferView\":0,\"componentType\":5126,\"count\":{vertexCount},\"type\":\"VEC3\",\"min\":[-3,0,-3],\"max\":[3,0,3]}}," +
            $"{{\"bufferView\":1,\"componentType\":5125,\"count\":{indexCount},\"type\":\"SCALAR\"}}]}}";

        byte[] jsonBytes = Pad(Encoding.UTF8.GetBytes(json), (byte)' ');
        byte[] binBytes = Pad(bin.ToArray(), 0);
        var glb = new MemoryStream();
        var w = new BinaryWriter(glb);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonBytes.Length + 8 + binBytes.Length));
        w.Write((uint)jsonBytes.Length); w.Write(0x4E4F534Au); w.Write(jsonBytes);
        w.Write((uint)binBytes.Length); w.Write(0x004E4942u); w.Write(binBytes);
        w.Flush();
        return glb.ToArray();
    }

    static byte[] Pad(byte[] data, byte fill)
    {
        int padded = (data.Length + 3) & ~3;
        var result = Enumerable.Repeat(fill, padded).ToArray();
        Array.Copy(data, result, data.Length);
        return result;
    }
}
