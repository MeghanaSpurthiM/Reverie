# Building the Game

Windows player builds: one per sample theme. They play on a monitor or in a Meta Quest 3 over Quest Link.

## Windows

Reverie's Windows release is two standalone games, one per sample theme
(`Reverie-Forest-Windows`, `Reverie-Beach-Windows`). Each plays on a monitor with keyboard and
mouse, or in a Meta Quest over Link (OpenXR). They contain the game only: no generation code, API
keys or network access (`REVERIE_PLAYER_ONLY`).

Build them with one command (below).

### Requirements

| | Version | Notes |
|---|---|---|
| Windows | 10 or 11, 64-bit | `tar.exe` and `robocopy` (built in) |
| Unity | **6000.0.56f1** | via Unity Hub; *Windows Build Support (Mono)* ships with the Windows editor |
| git | any recent | Unity's package manager clones UnityGaussianSplatting v1.1.2 from GitHub |
| PowerShell | 5.1 or 7 | |
| Disk | ~20 GB free | project copy + Library ≈ 12 GB, themes 0.9 GB, players 2 × 0.5 GB |
| Network | first build only | Unity packages (Meta XR Core SDK, glTFast from OpenUPM) |
| Sample themes | `Reverie-SampleThemes.zip` (843 MB) | not in git; see below |

GPU for *running* the players: DirectX 12. The 3DGS layer needs a recent dedicated GPU (the study
used an RTX 5070 laptop GPU).

#### Sample themes

The themes are Unity Gaussian-splat assets (2.1 M splats each, Very High quality, ~485 MB per
theme), too large for git. Get `Reverie-SampleThemes.zip` (it contains `SampleThemes/` and
`SampleThemes.meta`) from the project's GitHub release, or use the
`Reverie_Data/StreamingAssets/` folder of an existing Windows build.

### Build locally (one command)

```powershell
pwsh scripts/build_windows.ps1 -ThemesZip path\to\Reverie-SampleThemes.zip
```

(`powershell -ExecutionPolicy Bypass -File scripts\build_windows.ps1 ...` works the same on
Windows PowerShell 5.1.) Other inputs:

| Parameter | Default | Meaning |
|---|---|---|
| `-ThemesZip` | — | local path or https URL of `Reverie-SampleThemes.zip` |
| `-ThemesDir` | — | a folder that contains `SampleThemes/` (instead of the zip) |
| `-WorkDir` | `<repo>\..\_build\windows` | project copy, `build.log` and `dist\`; outside the repository |
| `-UnityPath` | `$env:UNITY_PATH`, then Unity Hub's install of the version in `ProjectVersion.txt` | `Unity.exe` |
| `-NoZip` | off | skip zipping |

What it does:

1. Copies `client/unity` to `<WorkDir>\unity` (Unity locks an open project; the copy also keeps its
   `Library\` cache, so later builds skip the package import).
2. Puts the sample themes into `Assets\StreamingAssets\SampleThemes\`.
3. Runs `Unity -batchmode -nographics -quit -buildTarget Win64 -executeMethod ReverieBuild.BuildWindowsThemes`.
   For each theme, [`ReverieBuild`](../client/unity/Assets/Scripts/Runtime/Editor/ReverieBuild.cs)
   moves the other themes aside, points `FolderConfig.asset` at the theme, builds the game scene
   with `REVERIE_PLAYER_ONLY`, then adds `PLAYER_README.txt` and `THIRD_PARTY.md` from `docs/`.
4. Zips each player with `tar.exe` into `<WorkDir>\dist\`.

Expected output (measured on an RTX 5070 laptop, Windows 11, first build including package import):

```
==> Unity 6000.0.56f1 at C:\Program Files\Unity\Hub\Editor\6000.0.56f1\Editor\Unity.exe
==> Copying ...\client\unity -> ...\_build\windows\unity
==> Building (log: ...\_build\windows\build.log). The first run imports packages and takes 15-40 min.
[Reverie] Reverie-Forest-Windows built: ...\unity\Builds\Reverie-Forest-Windows\Reverie.exe (630 MB)
[Reverie] Reverie-Beach-Windows built: ...\unity\Builds\Reverie-Beach-Windows\Reverie.exe (632 MB)
==> Built in 13 min
==> Zipping ...\dist\Reverie-Forest-Windows.zip
==> Zipping ...\dist\Reverie-Beach-Windows.zip
Reverie-Beach-Windows.zip  478 MB  sha256 ...
Reverie-Forest-Windows.zip  462 MB  sha256 ...
==> Players: ...\unity\Builds\Reverie-<Theme>-Windows\Reverie.exe
```

Each player folder:

```
Reverie-Forest-Windows/
  Reverie.exe                 the game
  Reverie_Data/               includes StreamingAssets/SampleThemes/forest (the 3DGS scene)
  UnityPlayer.dll, UnityCrashHandler64.exe, MonoBleedingEdge/, D3D12/
  PLAYER_README.txt           controls, Quest 3 steps
  THIRD_PARTY.md              credits and licenses
```

#### Check a build

Double-click `Reverie.exe` and press `1`–`5`: `2` shows the panorama skybox, `3` the 3DGS scene
(the first load takes a few seconds), `4` the tool and target, `5` the base room. `T` switches theme
only when the player contains more than one theme, so it does nothing in these one-theme builds.

The player log is `%USERPROFILE%\AppData\LocalLow\Reverie\Reverie Forest\Player.log` (`Reverie Beach` for the other). A good run contains

```
[Gaussian] 2,097,152 splats loaded from scene.asset
[FolderEnv] Tool placed: tool_axe_Axe.glb
```

and no `Exception`. Without a headset, OpenXR lines such as `xrGetSystem: XR_ERROR_FORM_FACTOR_UNAVAILABLE`,
`OnInstanceCreate returned an error` and `Failed to initialize subsystem OpenXR Display` are expected:
the game falls back to the desktop camera.

#### Build from the Unity editor

Open `client/unity` in Unity 6000.0.56f1 with the themes in `Assets/StreamingAssets/SampleThemes/`,
switch the platform to *Windows, Mac, Linux → Windows x86_64*, then **Reverie → Build → One Game per
Theme**. Output goes to `client/unity/Builds/Reverie-<Theme>-Windows/` (gitignored).

### Unity cannot resolve the git package

Opening `client/unity` ends with:

```
An error occurred while resolving packages:
  Project has invalid dependencies:
    org.nesnausk.gaussian-splatting: Error when executing git command. fatal: not in a git directory
```

UnityGaussianSplatting is a git dependency (`Packages/manifest.json`), and before cloning it UPM runs
`git config core.longpaths true` in a scratch folder that is not a repository. Git for Windows 2.49 and
newer refuse an implicit local `git config` there and exit 128, so the clone never starts. Nothing in the
project triggers it: `git config core.longpaths true` fails the same way in any empty folder. The same
message also appears when the work folder sits on a drive without file ownership (exFAT, some network
shares), where git reports dubious ownership instead.

Either fix works:

- **Vendor the package.** Clone it beside the project and copy its `package/` folder in; an embedded
  package wins over the manifest entry, so UPM stops cloning. Leave `manifest.json` as it is.

  ```powershell
  git clone https://github.com/aras-p/UnityGaussianSplatting.git
  git -C UnityGaussianSplatting checkout v1.1.2
  robocopy UnityGaussianSplatting\package client\unity\Packages\org.nesnausk.gaussian-splatting /E
  ```

- **Use a Git that still allows it**, 2.48 or earlier, on `PATH` for the Unity process.

On an ownership-less drive, set `GIT_CONFIG_COUNT=1`, `GIT_CONFIG_KEY_0=safe.directory`,
`GIT_CONFIG_VALUE_0=<path>/*` for the Unity process, or move the project to an NTFS drive.
`scripts\build_windows.ps1` already does this for its own copy.

### Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `Unity 6000.0.56f1 not found` | Install that exact version in Unity Hub (*Installs → Install Editor → Archive*), or pass `-UnityPath`. |
| `has no Windows Build Support module` | Unity Hub → *Installs → ⚙ → Add modules → Windows Build Support (Mono)*. |
| `Sample themes missing in ...` | Pass `-ThemesZip` or `-ThemesDir`; the zip must contain `SampleThemes/forest/scene.asset` etc. |
| `upm.log` shows `git config core.longpaths true ... fatal: not in a git directory` | Git 2.49+ refuses that command, or the work folder is on a drive without file ownership (exFAT, some network shares). See [Unity cannot resolve the git package](#unity-cannot-resolve-the-git-package). |
| `Unity exited with 1`, `error CS0246 ... GaussianSplatting` | UnityGaussianSplatting failed to download (network or git). Check `%LOCALAPPDATA%\Unity\Editor\upm.log`. |
| `Shader not found: glTF-pbrMetallicRoughness` | glTFast did not import (OpenUPM unreachable). Re-run with network access. |
| `Player doc not found, not packaged` warning | You ran Unity on a copied project without `REVERIE_DOCS_DIR`; the script sets it to `<repo>\docs`. |
| The player starts but the scene is grey or missing | GPU without DirectX 12, or old driver. The players use D3D12 only (DX11 cannot run the splat compute shaders). |
