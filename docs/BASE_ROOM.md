# Bring your own base room

The base room is one of Reverie's three mutually exclusive environment layers, next to the skybox and the 3DGS scene. In the study it was a furnished living room built from two Unity Asset Store packs ([Vintage Retro Room URP](https://assetstore.unity.com/packages/slug/288108) and [Interior House Assets | URP](https://assetstore.unity.com/packages/slug/257122)). Their license does not allow publishing the pack files, so this repository ships a **placeholder room** of plain Unity boxes, plus the code and Editor tools to build your own. That is why the Unity scene looks different from the paper. The [browser demo](https://huggingface.co/spaces/MeghanaSpurthiM/ReverieDemo) shows the study room as a 360° picture (press `5`).

This guide shows how to build a room from an Asset Store pack or a `.glb` model, register several rooms, and switch between them at runtime.

| | |
|---|---|
| **Scene** | `client/unity/Assets/Scenes/3DGS_Unity.unity` |
| **Object** | `[BuildingBlock] Camera Rig > Room` with `BaseRoomLoader` |
| **Room files** | `client/unity/Assets/StreamingAssets/BaseRoom/room.glb`, `room_*.glb` |
| **Editor menu** | **Reverie → Base Room** |
| **In-game keys** | `5` room layer on or off · `R` next room (left thumbstick right on Quest) |
| **Unity** | 6000.0.56f1, URP 17, glTFast 6.1.0 |

- [Quick start](#quick-start)
- [How the Room object works](#how-the-room-object-works)
- [Expected hierarchy](#expected-hierarchy)
- [Scale, pivot and placement](#scale-pivot-and-placement)
- [Build a room from an Asset Store pack](#build-a-room-from-an-asset-store-pack)
- [Use a .glb or .gltf room](#use-a-glb-or-gltf-room)
- [Register and cycle several rooms](#register-and-cycle-several-rooms)
- [Materials and lighting](#materials-and-lighting)
- [Licensing](#licensing)
- [Troubleshooting](#troubleshooting)
- [Reference: BaseRoomLoader settings](#reference-baseroomloader-settings)

## Quick start

**From a model file (no Asset Store needed):**

1. Rename your room model `room.glb` and copy it into `client/unity/Assets/StreamingAssets/BaseRoom/`.
2. Open `3DGS_Unity.unity` and press Play.

**From an Asset Store pack:**

1. Import the pack through **Window → Package Manager → My Assets**.
2. Open `3DGS_Unity.unity`, select the pack's room prefab in the Project window, and choose **Reverie → Base Room → Add Room Variant...**.
3. Line up the room's floor with the placeholder's floor (see [Scale, pivot and placement](#scale-pivot-and-placement)).
4. Choose **Reverie → Base Room → Validate Rooms** and fix what it reports.
5. Save the scene and press Play. Press `R` to cycle if you added more than one room.

## How the Room object works

`BaseRoomLoader` on the `Room` object builds a list of rooms when the game starts and shows exactly one of them:

1. **Room variants**: every direct child of `Room` except `PlaceholderRoom`, in hierarchy order. They are switched on and off with `SetActive`.
2. **Room files**: `room.glb` (or `room.gltf`), then `room_*.glb` and `room_*.gltf` in alphabetical order, from `StreamingAssets/BaseRoom/`. A file is loaded with glTFast when its turn comes, and its meshes, materials and textures are freed when you move to the next room.
3. **The placeholder**: shown when no other room exists, while a room file is loading, and whenever a room fails to load.

The in-game room toggle (key `5`) shows or hides the whole `Room` object, so it controls whichever room is current. `R` moves to the next room while the room layer is on.

Switching is safe at any moment, including while a room file is still loading: the load is cancelled, its half-built objects are destroyed, and the glTFast import is disposed only after it has stopped. Several switches in one frame collapse into the last one. If the `Room` object or the whole scene is unloaded mid-load (for example with `T`, next theme), the load cleans up after itself. The PlayMode tests in `Assets/Tests/PlayMode/BaseRoomSwitchingTests.cs` exercise all of these cases.

## Expected hierarchy

```
[BuildingBlock] Camera Rig
└── Room                                  BaseRoomLoader
    ├── PlaceholderRoom                   shipped; floor, ceiling and four walls
    ├── RoomVariant_LivingRoom            BaseRoomVariant (optional), pivot on the floor
    │   ├── Geometry                      the pack's prefab(s): floor, walls, ceiling, furniture
    │   ├── Colliders                     optional, simple box colliders only
    │   └── Lights                        realtime point or spot lights for this room
    ├── RoomVariant_Office
    │   └── ...
    └── _LoadedRoom <name>                created at runtime for room.glb / room_*.glb
```

- **One root per room.** Everything that belongs to a room, including its lights and reflection probes, lives under its `RoomVariant_*` root, so switching rooms switches all of it.
- **`Room` stays under the camera rig.** It is a child of `[BuildingBlock] Camera Rig`, as in the study, so the room keeps its place relative to the player. Do not move `Room` itself; move your variant.
- **No player parts.** Remove cameras, audio listeners, character controllers and "FPS" or demo player prefabs that some packs put in their sample rooms. The loader turns off any camera or audio listener it finds and logs a warning.
- **`BaseRoomVariant` is optional.** Add it to give a room a display name, or untick **Include In Cycle** to keep a room in the scene but skip it.

### The study room

The study used two Unity Asset Store packs: [Vintage Retro Room URP](https://assetstore.unity.com/packages/slug/288108) v1.0 (Asset Store product 288108: the `Wall`, `Floor`, `Sofa` and `Armchair` prefabs with their URP materials) and [Interior House Assets | URP](https://assetstore.unity.com/packages/slug/257122) v1.0 (product 257122: the `WoodPlanksBrown` floor material). Get them from the Asset Store under their own licenses to rebuild it. They were assembled by hand under `Room`, with no colliders and realtime lighting only:

```
Room
├── Floor            pack Floor prefab, scaled to about 6 m × 6 m, top surface at Room-local y ≈ 0.66
├── Ceiling          pack Floor prefab, at y ≈ 3.3
├── FrontWall, BackWall, SideWallLeft, SideWallLeft (1)    three pack Wall prefabs each
├── Sofa, Armchair   pack furniture, standing on the floor
└── Directional Light, Directional Light (1)               realtime, no shadows
```

The placeholder reproduces this footprint (about 6 m × 6 m × 2.8 m, floor top at Room-local y = 0.66, centred at x = −2.55, z = 0.18), so the player, the kick zone and the falling targets line up with it exactly as they did in the study. To rebuild a similar room, make a `RoomVariant_*` with the same footprint.

## Scale, pivot and placement

| Convention | Value |
|---|---|
| Units | 1 Unity unit = 1 metre. A standard door is about 2 m tall. |
| Up axis | +Y. glTF files are converted from glTF's +Y up automatically. |
| Variant pivot | On the floor, at the centre of the play area. The floor's top surface is at the variant's local y = 0. |
| Variant position | `Room`-local **(−2.55, 0.66, 0.18)**, the placeholder's floor centre. **Add Room Variant** puts new variants there. |
| Player and targets | The player, kick zone and target spawn points belong to the camera rig and the gameplay objects, not to the room, and were tuned to the placeholder's footprint. Keep at least the placeholder's floor area (about 6 m × 6 m) clear of furniture. The player faces +Z. |
| Ceiling | At least the placeholder's 2.7 m, so targets stay visible. |

To check placement, select your variant, choose **Reverie → Base Room → Show Only Selected Room**, then temporarily turn on `PlaceholderRoom` to compare floors. Turn it off again before saving. The loader decides what shows at runtime, so the active state you save in the scene does not matter for play, but saving variants inactive keeps the Scene view tidy and avoids work at scene load.

Room files are placed at the same floor origin: `BaseRoomLoader.floorOrigin` (default `(−2.55, 0.66, 0.18)`). Use **Room Position**, **Room Rotation** and **Room Scale** for files whose floor is not at y = 0 or that are not in metres (`0.01` for centimetres).

## Build a room from an Asset Store pack

1. **Import the pack.** In Unity, choose **Window → Package Manager → My Assets**, find the pack and click **Import**. Import only the folders you need; demo scenes and sample players are not required.
2. **Convert materials to URP if needed.** Packs labelled "Built-in" render pink. See [Materials and lighting](#materials-and-lighting).
3. **Open the game scene.** `client/unity/Assets/Scenes/3DGS_Unity.unity`.
4. **Add the room.**
   - If the pack has one prefab for a whole room, select it in the Project window and choose **Reverie → Base Room → Add Room Variant...**. You can select several prefabs to add one variant each.
   - If the pack is modular (floors, walls, furniture), choose **Reverie → Base Room → Add Empty Room Variant**, rename it (for example `RoomVariant_Office`), and build the room under its `Geometry` child.
5. **Place it.** Move or scale the prefab under `Geometry`, not the variant root, until the floor's top surface is at the variant's y = 0 and the play area is centred on the root. Rotate it so the open side of the room is in front of the player (+Z).
6. **Light it.** Put realtime lights under `Lights`. See [Lighting](#lighting).
7. **Validate.** Choose **Reverie → Base Room → Validate Rooms** and fix each warning in the Console.
8. **Save the scene** and press Play. Press `5` to show the room and `R` to cycle.

Optional: save the finished variant as a prefab (drag it into a folder such as `Assets/MyRooms/`) so you can reuse it in other scenes. Keep that folder out of git, because it references the pack's assets (see [Licensing](#licensing)).

## Use a .glb or .gltf room

1. Export or download a room as glTF 2.0 (`.glb` is easiest; a `.gltf` needs its `.bin` and textures beside it).
2. Make sure it is in metres with the floor at y = 0, or plan to adjust **Room Position** and **Room Scale**.
3. Copy it into `client/unity/Assets/StreamingAssets/BaseRoom/` as `room.glb`, or as `room_<name>.glb` to add several.
4. Press Play.

Room files also work in a built player: copy them into `<Build>_Data/StreamingAssets/BaseRoom/`.

glTFast uses its own URP shaders, so file rooms never render pink. They have no colliders, and their lighting comes from the scene's own lights.

## Register and cycle several rooms

Every room you add is registered automatically; there is no list to maintain.

| You add | It becomes |
|---|---|
| A child of `Room` | A room variant, in hierarchy order |
| `room.glb` / `room.gltf` | The first room file |
| `room_kitchen.glb`, `room_office.gltf` | Further room files, alphabetical (`kitchen`, `office`) |
| Nothing | The placeholder only |

At start the loader shows room number **Start Room** (default `0`, the first variant). The order is variants first, then files, then the placeholder if **Include Placeholder In Cycle** is on.

| Input | Action |
|---|---|
| `5` | Room layer on or off |
| `R` | Next room, while the room layer is on |
| Left thumbstick right (Quest) | Next room, while the room layer is on |

From your own scripts:

```csharp
var rooms = FindAnyObjectByType<BaseRoomLoader>(FindObjectsInactive.Include);
rooms.Next();                 // or Previous(), or Select(index)
rooms.RoomShown += room => Debug.Log($"Now showing {room.Name}");
// After adding or removing a room at runtime:
rooms.Refresh();
```

Only one room is ever loaded from file at a time. Room variants stay in memory, because they are part of the scene; if you register many large Asset Store rooms, consider keeping only the ones you need in the scene.

## Materials and lighting

### Materials

The project uses the Universal Render Pipeline. Materials made for the Built-in pipeline (shaders such as `Standard`, `Legacy Shaders/*` or `Mobile/*`) render bright pink.

1. Select the pack's material folder in the Project window.
2. Choose **Edit → Rendering → Materials → Convert Selected Built-in Materials to URP**, or use **Window → Rendering → Render Pipeline Converter** → **Built-in to URP** → **Material Upgrade** for the whole project.
3. Materials with custom shaders cannot be converted automatically. Assign **Universal Render Pipeline/Lit** by hand and set **Base Map**, **Normal Map** and **Smoothness** again.

Prefer packs that list URP support. **Validate Rooms** and the loader's start-up check both list every pink material.

### Lighting

The game scene has one realtime **Directional Light** and no baked lightmaps, and the study room used realtime lights only. Prefabs do not carry lightmaps from their demo scene, so:

| Choose | Avoid |
|---|---|
| Realtime point and spot lights under the variant's `Lights` child | Lights set to **Baked**; they give no light without a bake |
| A few lights, with shadows only where they matter (VR renders twice) | A second Directional Light, unless the room needs a different sun |
| Reflection probes set to **Baked** or **On Awake** | Realtime probes refreshing **Every Frame** |
| Emissive materials for lamps | Light Probe Groups and lightmaps from the pack's demo scene |

If you want baked lighting, bake `3DGS_Unity` with only one room variant active, and know that the bake also affects the other layers. Mixed lights and lightmaps across several rooms are not supported by the switcher.

### Colliders

Reverie's gameplay does not need room colliders: the targets move on rails and only collide with the player's legs. **Disable Room Colliders** (on by default) turns off every collider in a room variant, so large mesh colliders from a pack cannot stall the frame when the room appears. Turn it off only if your own scripts need the room to be solid, and then use box colliders under `Colliders`, not mesh colliders on detailed meshes.

## Licensing

Unity Asset Store content is licensed to you under the [Unity Asset Store EULA](https://unity.com/legal/as-terms). It can be used in projects you build and ship, but **not redistributed as source assets**. For this repository that means:

- **Never commit** an imported pack folder, its `.meta` files, or a scene or prefab that you have saved with pack content inside it.
- The repository ships only `PlaceholderRoom`, the `BaseRoomLoader` code, the **Reverie → Base Room** menu and this guide. Everyone who wants the study's look imports a pack themselves.
- If you modify `3DGS_Unity.unity` by adding a variant, do not commit that scene change. Keep your rooms in a separate scene copy or prefab outside version control instead.
- Models in `StreamingAssets/BaseRoom/` have their own licenses. Check them before sharing a build, and credit authors where the license asks.
- A build that includes an Asset Store room is fine to distribute as a compiled application under the EULA; the source assets are not.

Before committing, run `git status` and check that no pack folders or room files are listed.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Room is pink or magenta | Built-in pipeline materials | [Convert materials](#materials) |
| Room is black or very dark | Lights set to **Baked**, or the pack expects its demo scene's lightmaps | Set lights to **Realtime**; add a point light under `Lights` |
| Room is huge, tiny, or under the floor | Pack or file not in metres, or pivot not on the floor | Scale the prefab under `Geometry`; for files use **Room Scale** (`0.01` for cm) and **Room Position** |
| Placeholder shows instead of my room | No variants and no `room*.glb`, or the room failed to load | Check the Console for `[BaseRoom]` warnings; check the file name (`room.glb`, not `Room.GLB` on Linux) |
| `[BaseRoom] Could not show room` | Corrupt or unsupported glTF (for example Draco or KTX textures without the matching package) | Re-export as plain glTF 2.0, or install the glTFast extension package |
| The view jumps to another camera or audio doubles | The pack's demo player or camera came along | Remove it from the variant; the loader also turns cameras and listeners off |
| Frame hitch when the room appears | Large mesh colliders, realtime reflection probes, or very high polygon counts | Keep **Disable Room Colliders** on; bake probes; use LODs or a lighter pack |
| `R` does nothing | The room layer is off, or only one room exists | Press `5` first; add a second variant or room file |
| Two rooms visible in the Scene view | Several variants are active in the Editor | Fine at runtime; use **Show Only Selected Room** for editing |

## Reference: BaseRoomLoader settings

| Setting | Default | Meaning |
|---|---|---|
| Placeholder | `PlaceholderRoom` | Fallback room |
| Floor Origin | `(−2.55, 0.66, 0.18)` | Room-local point where a room file's origin goes |
| Room Position / Rotation / Scale | `0` / `0` / `1` | Extra placement for room files |
| Next Room Key | `R` | Key for the next room |
| Next Room On Left Stick | on | Also cycle with the left thumbstick pushed right |
| Start Room | `0` | Room shown first |
| Include Placeholder In Cycle | off | Keep the placeholder in the cycle when other rooms exist |
| Load Room Files | on | Read `StreamingAssets/BaseRoom/` |
| Disable Room Colliders | on | Turn off colliders in room variants |
| Disable Stray Cameras | on | Turn off cameras and audio listeners in room variants |
| Log Room Checks | on | Log problems found in each variant at start |
