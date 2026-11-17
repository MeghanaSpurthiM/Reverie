# The Unity Game and Theme Tools

Everything about the Unity client in `client/unity/`: how it is organised, how to play, how to add your own themes and props, and the theme folder format. For building players, see [BUILD.md](BUILD.md); for the base room, see [BASE_ROOM.md](BASE_ROOM.md).

## Unity client

A VR bubble-pop game whose environment — skybox, 3D
Gaussian-Splat scene, ambient audio, and a "tool"/"target" prop pair (what you swing, what
pops) — is generated from a text prompt and switched at runtime via independent toggles.
Built as a research apparatus for studying prompt-driven VR environments.

### Generation and play are separate

| | **Theme Studio** (generate) | **Game** (play) |
|---|---|---|
| Scene | `Assets/Scenes/ThemeStudio.unity` | `Assets/Scenes/3DGS_Unity.unity` |
| Code | `Assets/Scripts/Generation/` → assembly `Reverie.Generation` | `Assets/Scripts/Runtime/` → assembly `Reverie.Runtime` |
| Does | LLM prompt expansion, server bake + download, Sketchfab props, API keys | Loads a theme folder: skybox, 3DGS, audio, tool/target; the bubble game |
| Network | local Ollama, Reverie server, Sketchfab | none |

- **One-way dependency.** Generation may use runtime code; runtime never references
  generation (enforced by the assemblies). The only seam is `ThemeHooks` (runtime objects
  announce themselves; generation tools subscribe when present).
- **Hand-off.** Theme Tools picks a theme folder (`ThemeSelection`, stored in PlayerPrefs); the
  game plays it, falling back to `Assets/FolderConfig.asset` (default: the forest sample).
- **Player-only builds.** The scripting define `REVERIE_PLAYER_ONLY` removes the
  `Reverie.Generation` assembly entirely.

### Setup

1. Open `client/unity/` with Unity 6000.0.56f1 from Unity Hub. The first open resolves
   `Packages/manifest.json`, including UnityGaussianSplatting from GitHub (needs network).
   If it fails with `fatal: not in a git directory`, see *Troubleshooting* in
   [docs/REPLICATION_GUIDE.md](REPLICATION_GUIDE.md).
2. The first open also imports TextMesh Pro's Essential Resources (`TmpEssentialsImporter`);
   they are git-ignored. In batch mode: `-executeMethod TmpEssentialsImporter.ImportAndExit`.
3. Enter keys in **Reverie → Theme Tools → API keys → Save keys on this machine** (stored in
   `Application.persistentDataPath`, never in the project). Or copy
   `Assets/StreamingAssets/api_config.example.json` to `api_config.json` (gitignored) and fill it in.
4. To use the sample themes, copy `Reverie_Data/StreamingAssets/SampleThemes/` from a Windows
   build into `Assets/StreamingAssets/`. They are too large for git.

### Making and playing a theme

- **Editor:** *Reverie → Theme Tools*, or press Play in the ThemeStudio scene.
  *Audio / Visual* tab: Expand Prompts → Check server → Bake on Server + Download.
  *Tool / Target* tab: Suggest → Fetch GLBs. Then **Use in game** (this machine) or
  **Make project default** (writes `FolderConfig.asset`), and Play `3DGS_Unity`.
- **Build:** run the *Theme Studio + Game* build, make a theme, click **Play this theme**.

| Bake mode | Server does | Typical time (A100) |
|---|---|---|
| Full | panorama + 3DGS + audio | ~165 min |
| Pano+3DGS | panorama + 3DGS | ~160 min |
| Audio | audio only | 0.4–2 min |
| 3DGS from pano | 3DGS from the theme's panorama (uploaded if local) | ~160 min |
| 3DGS from pano+Audio | as above, plus audio | ~162 min |

Themes are written to `Assets/StreamingAssets/Themes/<theme>/` (Editor) or
`Application.persistentDataPath/Themes/<theme>/` (builds). `RuntimeSplatLoader` loads both
`scene.ply` files and imported splats, in the Editor and in builds. To import a splat, choose
**Reverie → Theme Folder → Import scene.ply as Splat...**.

### Building

**Reverie → Build** (uses the active build target; output in `Builds/`, gitignored):

| Menu | Scenes | Contains generation code |
|---|---|---|
| *One Game per Theme* | game, one theme each | no (`REVERIE_PLAYER_ONLY`) |
| *Game (player only)* | game | no (`REVERIE_PLAYER_ONLY`) |
| *Theme Studio + Game* | Studio (opens first), game | yes |

- **Keys never ship.** `ApiConfigBuildGuard` blanks every key/token in `api_config.json` for the
  duration of a build. In a Studio build, enter the server and Sketchfab tokens in
  *API keys* → *Save keys on this machine*
  (`persistentDataPath/api_config.json`).
- **Sample themes.** Everything in `StreamingAssets` ships, and the themes live in `StreamingAssets/SampleThemes/` (not in git; see the top-level README). *One Game per Theme* moves the other themes aside for each build. To rebuild from the command line: `Unity -batchmode -buildTarget Win64 -executeMethod ReverieBuild.BuildWindowsThemes`.
- The Studio panel renders on the desktop window / PC-VR mirror, not inside a standalone
  Quest headset.

### Layout

```
Assets/
  Scenes/            3DGS_Unity (game), ThemeStudio (generation)
  Scripts/
    Runtime/         Reverie.Runtime: loaders, RuntimeSplatLoader, game, ThemeHooks, ThemeSelection
      Editor/        Reverie.Runtime.Editor: Reverie menus (Build, Theme Folder), UI scaffolding helpers
    Generation/      Reverie.Generation: Theme Tools panel, ApiConfig, LLM/Sketchfab clients
      Editor/        Reverie.Generation.Editor: Theme Tools window, Studio scene setup, build key guard
  StreamingAssets/
    SampleThemes/              sample themes, shipped in builds (not in git)
    Themes/                    generated and drop-in themes
    BaseRoom/                  drop-in room
    api_config.example.json    config template (copy to api_config.json)
  Tests/             EditMode and PlayMode tests
  FolderConfig.asset default theme folder for the game
```

### Provenance

Each theme folder records how it was made: `llm_expansion.json` (model, temperature, seed,
prompts, timing), `bake_meta.json` (server bake provenance) and `sketchfab.json` (model UID,
author, URL and license of each prop, for attribution).

## Customizing Reverie

You can try your own theme, props and room without a GPU server. Copy files into a folder, press Play, and any layer you skip simply stays off.

### Add your own tool and target

1. Download two models as `.glb` or `.gltf`, for example from [Sketchfab](https://sketchfab.com) or [Poly Haven](https://polyhaven.com/models).
2. Rename them `tool.glb` and `target.glb`.
3. Copy them into `client/unity/Assets/StreamingAssets/Themes/my-theme/`.
4. In Unity, choose **Reverie → Theme Folder → Choose...** and select `my-theme`.
5. Open `client/unity/Assets/Scenes/3DGS_Unity.unity`, press Play, and press `4` to show the tool and target.

Check each model's license, and credit its author if you share your theme.

### Build a full theme

A theme is one folder. Every file is optional.

```
my-theme/
├── panorama.jpg      # 360° image for the skybox layer
├── audio.wav         # ambient loop
├── tool.glb          # held tool
├── target.glb        # falling target
├── scene.ply         # 3DGS source, before import
├── scene.asset       # 3DGS splat, after import
├── scene_pos.bytes
├── scene_oth.bytes
├── scene_col.bytes
└── scene_shs.bytes
```

#### Add a 3DGS scene

Reverie renders splats with [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) by Aras Pranckevičius. Unity installs it automatically from `client/unity/Packages/manifest.json`.

1. Copy your point cloud into the theme folder and name it `scene.ply`.
2. Choose **Reverie → Theme Folder → Import scene.ply as Splat...** and select the theme folder.
3. Wait for the import to finish. It writes `scene.asset` and the `scene_*.bytes` files beside `scene.ply`.

`Assets/StreamingAssets/Themes/sample-splat/scene.ply` is a 50,000-splat preview of the forest theme, decimated from the 2,097,152 splats the paper used, so this step can be run straight from a clone. It has no panorama, audio or props: only the 3DGS layer shows anything.

The import runs the plugin's own importer at full precision, the setting used for the study themes. To use a smaller quality level, run **Tools → Gaussian Splats → Create GaussianSplatAsset** yourself, then copy the output into the theme folder.

### Replace the base room

The repository ships a plain placeholder room, because the room used in the study comes from a commercial asset pack that cannot be redistributed. You can add one or more rooms of your own and switch between them in the game. **[Bring your own base room](BASE_ROOM.md)** has the full guide: hierarchy, scale, URP materials, lighting, licensing and troubleshooting.

#### Option 1: drop in a model

1. Download a room as `.glb` or `.gltf`, in metres with the floor at y = 0.
2. Rename it `room.glb`. To add more rooms, name them `room_<name>.glb`.
3. Copy them into `client/unity/Assets/StreamingAssets/BaseRoom/`.
4. Press Play. The first room replaces the placeholder; press `R` for the next one.

If the room is the wrong size or position, select the `Room` object and adjust **Room Position**, **Room Rotation** and **Room Scale** on `BaseRoomLoader`.

#### Option 2: use a Unity Asset Store room

1. Import the pack through **Window → Package Manager → My Assets**. Convert its materials to URP if they render pink.
2. Open the `3DGS_Unity` scene, select the pack's room prefab in the Project window, and choose **Reverie → Base Room → Add Room Variant...**.
3. Line up its floor with the placeholder's floor, then choose **Reverie → Base Room → Validate Rooms**.

Never commit the pack's files, or a scene saved with them in it. See [Licensing](BASE_ROOM.md#licensing).

### Play the game

A headset is optional. The game runs on a monitor in the Unity Editor, with the same themes and scoring.

Open `client/unity/Assets/Scenes/3DGS_Unity.unity` and press Play. To return to the default theme afterwards, choose **Reverie → Theme Folder → Use Project Default**.

#### Keyboard and mouse

| Input | Action |
|---|---|
| Left click | Front kick |
| `A` | Side kick |
| `D` | Wide kick |
| Arrow keys | Look around |
| Hold right mouse button and move | Look around |
| `1` | Audio on or off |
| `2` | Skybox on or off |
| `3` | 3DGS scene on or off |
| `4` | Tool and target on or off |
| `5` | Base room on or off |
| `R` | Next base room, when more than one is available |
| `T` | Next theme, when more than one is available |

The skybox, 3DGS scene and base room replace each other, so only one shows at a time. If the theme has no file for a layer, the layer still switches, its status label in the settings panel reads `no file` (`error` if the file cannot be read), and the rest of the game keeps working: without `tool.glb` and `target.glb` you kick plain bubbles with the paddles. The look controls turn off automatically when a headset is active.

#### Meta Quest 3

The study used a Meta Quest 3 connected to the PC with Quest Link. The project is already configured for it, so you don't need to change any Unity settings.

1. Install the Meta Quest Link app on your Windows PC and sign in.
2. In the app's settings, allow apps from unknown sources and set Meta Quest Link as the active OpenXR runtime.
3. Connect the headset with a USB-C cable, or use Air Link.
4. In the headset, start Quest Link.
5. In Unity, open `client/unity/Assets/Scenes/3DGS_Unity.unity` and press Play.

| Controller input | Action |
|---|---|
| Right trigger | Front kick |
| `B` | Side kick |
| `A` | Wide kick |
| Left `Y` | Show or hide the layer panel |
| Left stick click | Next theme, when more than one is available |
| Left stick right | Next base room, when more than one is available |
| Left laser and left trigger | Use the layer panel |

Rendering 3DGS on a headset needs a capable GPU. The study used an NVIDIA RTX 5070 laptop.

## Theme format

This document describes the files in a Reverie theme. To add your own theme, see [Customizing Reverie](#customizing-reverie).

Every theme — generated or shipped — is one folder that the game loads as a unit:

```
<theme>/
├── panorama.jpg          # 360° panorama (skybox layer)
├── scene.asset           # 3DGS splat (3DGS layer), with scene_*.bytes; imported from scene.ply
├── audio.wav             # 10 s ambience, looped (audio layer)
├── tool.glb              # held tool (tool/target layer); or tool_<name>.glb
├── target.glb            # target the tool pops (tool/target layer); or target_<name>.glb
├── llm_expansion.json    # provenance: prompt expansion
├── bake_meta.json        # provenance: server bake
└── sketchfab.json        # provenance + attribution: props
```

Theme folders live in `client/unity/Assets/StreamingAssets/Themes/<theme>/`, or in `Application.persistentDataPath/Themes/<theme>/` when generated in a *Theme Studio + Game* build. The sample themes use `client/unity/Assets/StreamingAssets/SampleThemes/<theme>/`.

The 3DGS layer is a [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) asset in the theme folder (`scene.asset` + `scene_*.bytes`). A theme with only `scene.ply` also loads, converted at runtime. To create the asset, choose **Reverie → Theme Folder → Import scene.ply as Splat...**. To add your own theme, props or room, see [docs/GAME.md](#customizing-reverie).

#### Files

| File | Produced by | Loaded by |
|---|---|---|
| `panorama.jpg` | server: StitchDiffusion | skybox loader |
| `scene.ply` | server: DreamScene360's final `point_cloud.ply` | `RuntimeSplatLoader` → `GaussianSpawner` |
| `scene.asset`, `scene_*.bytes` | client: imported from `scene.ply` | `RuntimeSplatLoader` → `GaussianSpawner` |
| `audio.wav` | server: AudioLDM 2 | `AmbientAudioController` |
| `tool.glb`, `target.glb` | `fetch_props.py`, or copied in by hand | `FolderEnvironmentLoader` (glTFast) |
| `tool_*.glb`, `target_*.glb` | Theme Tools | `FolderEnvironmentLoader` (glTFast); used if the plain name is absent, first file alphabetically |

#### Provenance Records

`llm_expansion.json` — one object:

| Field | Type | Description |
|---|---|---|
| `theme` | string | theme word as entered |
| `created_at` | string | local timestamp (ISO 8601) |
| `llm_endpoint`, `model` | string | OpenAI-compatible endpoint and model (default `llama3.1:latest`) |
| `temperature`, `seed` | number, number \| null | sampling settings (study: 0.4, no seed) |
| `setting` | string | the location the LLM chose for the theme |
| `visual_prompt`, `audio_prompt` | string | prompts sent to the server (editable before baking) |
| `wall_clock_s` | number | expansion time in seconds |

`sketchfab.json` — keyed by role (`"tool"`, `"target"`):

| Field | Type | Description |
|---|---|---|
| `query` | string | search phrase or exact 32-character model UID |
| `file` | string | saved `.glb` file name |
| `uid`, `name`, `author`, `url` | string | Sketchfab model identity and page |
| `license` | string | model license (required for attribution) |
| `downloaded_at` | string | local timestamp |

`bake_meta.json` — written by the server (`outputs/<slug>/meta.json`):

| Field | Type | Description |
|---|---|---|
| `slug`, `prompt`, `audio_prompt` | string | theme folder name and prompts used |
| `baked_at` | string | timestamp |
| `pano_path`, `ply_path`, `wav_path` | string \| null | file name of each output present on the server |
| `step_seconds` | object | duration of each bake step |
| `elapsed_min` | number | total bake time |
