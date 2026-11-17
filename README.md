# Reverie

**Text prompt → themed VR game world.**

Research code for *Reverie: Personalized Generative Theming of Virtual Reality Games* (Frontiers in Virtual Reality). A theme word such as `forest` yields a 360° panorama, a 3D Gaussian splatting (3DGS) scene, ambient audio, and a tool/target prop pair, which the game loads as independently switchable layers. Unity client plus Python generation server.

Browser demo: [huggingface.co/spaces/MeghanaSpurthiM/ReverieDemo](https://huggingface.co/spaces/MeghanaSpurthiM/ReverieDemo) · Windows builds and sample themes: [Releases](../../releases/latest)

<img src="assets/teaser.jpg" width="100%" alt="Forest and beach themes in the VR game. Columns: base scene, skybox, 3DGS scene, and 3DGS scene with themed tool and target.">

*The same game in its base scene and in two generated themes, shown with each generated layer: skybox, 3DGS scene, and themed tool and target.*

## Requirements

| | |
|---|---|
| Desktop play | Windows 10/11, Direct3D 12 GPU |
| VR play | plus Meta Quest 3 over Quest Link (study: RTX 5070 laptop) |
| Unity | 6000.0.56f1, URP 17.0.4 |
| Generation | Linux or WSL2, NVIDIA GPU ≥ 16 GB, CUDA 12.4, ~30 GB disk |

## Pipeline

<div align="center">
<img src="assets/pipeline.png" width="85%" alt="Reverie pipeline: a text input is expanded by Llama 3.1 into visual, audio and 3D-asset prompts, which drive StitchDiffusion and DreamScene360 (panorama and 3DGS), AudioLDM 2 (audio) and Sketchfab retrieval, all assembled in a Unity scene.">
</div>

| Stage | Model or tool | Output | Where |
|---|---|---|---|
| Prompt expansion | Llama 3.1 8B via Ollama | visual and audio prompts, tool and target | local |
| Panorama | StitchDiffusion (DreamScene360) | `panorama.jpg` | server, 1–2 min |
| 3DGS scene | DreamScene360 | `point_cloud.ply` | server, ~160 min on an A100 |
| Ambient audio, 200 steps | AudioLDM 2 | `audio.wav` | server, < 2 min |
| Props | Sketchfab API | `tool.glb`, `target.glb` | local |

| Paper result | Reproduce with |
|---|---|
| Generation pipeline (Sections 2.1–2.4) | [Generate](#generate) |
| Per-stage times (Table 1) | `log_times.py`, [Replication Guide §8.1](docs/REPLICATION_GUIDE.md#81-generation-times-table-1) |
| Prompt expansion time (Table 1) | [Replication Guide §4.2](docs/REPLICATION_GUIDE.md#42-llm-prompt-expansion-10-min-cpu-is-fine) |
| Per-layer toggling (Section 2.7) | [Play](#play) |

Survey data for Tables 2–6 will be released on acceptance.

## Layout

```
client/unity/   Scenes/, Scripts/{Runtime,Generation}/, Tests/, Packages/ (pinned)
server/         reverie_server.py, bake_theme.py, generator.py, expand_prompts.py,
                fetch_props.py, log_times.py, smoke_test.py, tests/
scripts/        build_windows.ps1
docs/           guides
```

## Dependencies

Nothing third-party is vendored.

| Component | Version | Source |
|---|---|---|
| [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) | `v1.1.2` (`ebd7613`) | UPM git dependency in `Packages/manifest.json` |
| Meta XR Core SDK | 85.0.0 | Unity registry |
| glTFast | 6.1.0 | OpenUPM, scoped registry in the manifest |
| OpenXR · Input System · Newtonsoft Json · Test Framework | 1.15.1 · 1.14.2 · 3.2.2 · 1.5.1 | Unity registry |
| TMP Essential Resources | uGUI 2.0.0 | imported on first open and before each build (`TmpEssentialsImporter`) |
| [DreamScene360](https://github.com/ShijieZhou-UCLA/DreamScene360) | `4a7664e` | clone beside `server/` |
| SD 2.1 base · SD VAE ft-MSE · StitchDiffusion LoRA · Omnidata depth | per DreamScene360 | [docs/SERVER.md §3](docs/SERVER.md#3-download-weights) |
| [AudioLDM 2](https://huggingface.co/cvssp/audioldm2) | `cvssp/audioldm2` | Hugging Face Hub, on first use |
| Python | pinned | `server/environment.yml`, `server/requirements.txt` |
| Llama 3.1 8B | `llama3.1:latest`, Q4_K_M | `ollama pull llama3.1` |

## Setup

The figures above are in Git LFS, so `git lfs install` before cloning or they arrive as pointer files.

Client: open `client/unity/` in 6000.0.56f1. UPM resolves the packages on first open, so `git` must be on `PATH`. Git for Windows 2.49 and newer refuse UPM's `git config core.longpaths true` outside a repository, which fails the UnityGaussianSplatting dependency with `fatal: not in a git directory`: [docs/BUILD.md](docs/BUILD.md#unity-cannot-resolve-the-git-package) has the fix.

Sample themes are not in git (~470 MB of splat data per theme): unzip [`Reverie-SampleThemes.zip`](../../releases/latest/download/Reverie-SampleThemes.zip) into `client/unity/Assets/StreamingAssets/`.

Server:

```bash
git clone https://github.com/MeghanaSpurthiM/Reverie.git && cd Reverie
git clone https://github.com/ShijieZhou-UCLA/DreamScene360
git -C DreamScene360 checkout 4a7664e        # the commit the paper used
conda env create -f server/environment.yml      # reverie-audio
```

DreamScene360's own `dreamscene360` env and the weights: [docs/SERVER.md §2–3](docs/SERVER.md#2-install).

Prompt expansion runs Llama 3.1 locally: install [Ollama](https://ollama.com), then `ollama pull llama3.1`. Start it (`ollama serve`, or the tray app) before generating.

## Smoke test

```bash
conda create -n reverie-smoke python=3.10 && conda activate reverie-smoke
cd server && pip install -r requirements.txt && python smoke_test.py
```

Tests plus one bake against the DreamScene360 stand-in and mock audio. No GPU, no weights.

## Generate

```bash
cd server   # reverie-audio
python expand_prompts.py --theme forest            # Llama 3.1 writes the prompts below
python bake_theme.py --theme forest --prompt "<visual>" --audio-prompt "<audio>"
```

Or run `reverie_server.py` and drive it from **Reverie → Theme Tools** in Unity: prompt expansion, editable prompts and prop names, server bake with download, Sketchfab retrieval. Tokens live under its *API keys* section, outside the project. CLI equivalents: `expand_prompts.py`, `fetch_props.py` ([docs/SERVER.md](docs/SERVER.md#configuration-and-prompt-expansion)).

A theme is one folder under `Assets/StreamingAssets/Themes/<theme>/`, which Theme Tools writes directly. By hand, copy `outputs/<theme>/` across: `panorama.jpg`, `audio.wav`, `tool.glb` and `target.glb` keep their names, DreamScene360's final `point_cloud.ply` becomes `scene.ply`. **Reverie → Theme Folder → Import scene.ply as Splat...** bakes the splat asset, otherwise it is converted on every load; **Choose...** selects the folder to play. Accepted names: [docs/GAME.md](docs/GAME.md#theme-format).

## Play

A [release build](../../releases/latest), one per theme, or `Assets/Scenes/3DGS_Unity.unity` in the Editor. For VR, make Quest Link the active OpenXR runtime and start it before the game.

`1`–`5` toggle audio, skybox, 3DGS, tool/target and base room; skybox, 3DGS and base room replace each other, and a layer with no file reports `no file`. Left click, `A`, `D` or Quest right trigger, `B`, `A` kick. Full map: [docs/GAME.md](docs/GAME.md#play-the-game).

The study's base room comes from two Asset Store packs ([Vintage Retro Room URP](https://assetstore.unity.com/packages/slug/288108), [Interior House Assets | URP](https://assetstore.unity.com/packages/slug/257122)) that cannot be redistributed, so the project ships a primitive placeholder instead. The [demo](https://huggingface.co/spaces/MeghanaSpurthiM/ReverieDemo) shows the original; [docs/BASE_ROOM.md](docs/BASE_ROOM.md#the-study-room) rebuilds it or swaps in your own.

## Tests

`server/smoke_test.py` (57 unit and HTTP tests, then a no-GPU bake). Unity EditMode and PlayMode run from the Test Runner; `ThemeLayerTests.DefaultTheme_*` need the sample themes.

## Documentation

| Guide | Covers |
|---|---|
| [Replication Guide](docs/REPLICATION_GUIDE.md) | Reproducing the paper's results |
| [Game](docs/GAME.md) | Client architecture, controls, theme format |
| [Server](docs/SERVER.md) | Setup, weights, HTTP API, configuration, CLI scripts |
| [Base Room](docs/BASE_ROOM.md) | Custom base rooms |
| [Build](docs/BUILD.md) | Windows players |
| [Third-party credits](docs/THIRD_PARTY.md) | Credits and licenses |

## Citation

```bibtex
@article{maadugundu2026reverie,
  title   = {Reverie: Personalized Generative Theming of Virtual Reality Games},
  author  = {Maadugundu, Meghana Spurthi and Kumar, Hiranya and Alghofaili, Rawan and
             Corace, Jason and Prabhakaran, Balakrishnan},
  journal = {Frontiers in Virtual Reality},
  year    = {2026},
  note    = {Under review}
}
```

## Data availability

The user study was approved by the Institutional Review Board of the University at Albany, SUNY (protocol STUDY00007853). Anonymized survey responses will be made publicly available upon acceptance.

## License

Code: [MIT](LICENSE). Third-party components keep their own licenses. DreamScene360, 3D Gaussian Splatting and any theme generated with them are non-commercial research only; AudioLDM 2 weights are CC BY-NC-SA 4.0. See [docs/THIRD_PARTY.md](docs/THIRD_PARTY.md).

## Acknowledgments

Reverie builds on [DreamScene360](https://github.com/ShijieZhou-UCLA/DreamScene360), [3D Gaussian Splatting](https://github.com/graphdeco-inria/gaussian-splatting), [AudioLDM 2](https://github.com/haoheliu/AudioLDM2), [Llama 3.1](https://www.llama.com), [Ollama](https://ollama.com), [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting), [glTFast](https://github.com/atteneder/glTFast), the [Sketchfab](https://sketchfab.com) API, and the Asset Store packs [Vintage Retro Room URP](https://assetstore.unity.com/packages/slug/288108) and [Interior House Assets | URP](https://assetstore.unity.com/packages/slug/257122). We thank their authors.

## Authors

Meghana Spurthi Maadugundu ([contact](mailto:mmaadugundu@albany.edu)), Hiranya Kumar, Jason Corace and Balakrishnan Prabhakaran (University at Albany, SUNY), and Rawan Alghofaili (The University of Texas at Dallas).
