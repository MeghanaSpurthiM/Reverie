# Replication Guide

Step-by-step replication guide for *Reverie: personalized generative theming of virtual reality games*. For an overview of the project, see the [main README](../README.md). For other guides, see the [documentation index](../README.md#documentation).

This document lets a reviewer:
- (i) test the pipeline's logic and play the game in under 30 minutes without a GPU;
- (ii) rebuild the full system and generate new themes;
- (iii) replicate the generation-time measurements (Table 1) reported in the paper.

This repository contains the application (generation server and Unity client). The survey data and analysis for Tables 2–6 will be released on acceptance (§11).

---

## Contents

1. [Artifact overview](#1-artifact-overview)
2. [Claims supported by this artifact](#2-claims-supported-by-this-artifact)
3. [Requirements](#3-requirements)
4. [Quick evaluation for reviewers (no GPU, under 30 min)](#4-quick-evaluation-for-reviewers-no-gpu-under-30-min)
5. [Full installation](#5-full-installation)
6. [Generating a theme end to end](#6-generating-a-theme-end-to-end)
7. [Running the VR game](#7-running-the-vr-game)
8. [Replicating the paper's results](#8-replicating-the-papers-results)
9. [Sources of variation and determinism](#9-sources-of-variation-and-determinism)
10. [Troubleshooting](#10-troubleshooting)
11. [Data availability and ethics](#11-data-availability-and-ethics)
12. [Third-party components and licenses](#12-third-party-components-and-licenses)
13. [Citation and contact](#13-citation-and-contact)

---

## 1. Artifact overview

### Architecture

```
 CLIENT: Windows PC + Meta Quest 3                     SERVER: Linux or WSL2, NVIDIA GPU
 ┌───────────────────────────────────────────┐         ┌──────────────────────────────────────┐
 │ Ollama: llama3.1:latest (Llama 3.1 8B)    │         │ reverie_server.py (HTTP, port 8000)  │
 │      ▲ 1 Expand → visual + audio prompts  │  bake   │   one bake at a time                 │
 │ Unity: Theme Studio / Theme Tools window  │ ──────► │   bake_theme.py                      │
 │        2 Bake on server, poll status      │         │   ├─ DreamScene360: StitchDiffusion  │
 │          download panorama, .ply, audio   │ ◄────── │   │    panorama → 3DGS scene         │
 │        3 Download tool + target           │  files  │   └─ generator.py: AudioLDM 2 audio  │
 │          (Sketchfab)                      │         │   outputs/<theme>/                   │
 │ Assets/StreamingAssets/Themes/<theme>/    │         └──────────────────────────────────────┘
 │      ▼ Unity game → Quest Link → Quest 3  │
 └───────────────────────────────────────────┘
 Server URL in the client: http://localhost:8000 (same machine), http://<host>:8000 (network),
                           or http://localhost:<port> through an SSH tunnel (remote cluster)
```

Reverie is split into a **client** (the Unity project and Ollama, on the Windows PC that drives the headset) and a **server** (the GPU generation pipeline). The client reaches the server only through its URL, so the server can run on the same PC (under WSL2), on another machine on the network, or on a remote GPU cluster (§5.1). All generation runs offline, before a play session, because one 3DGS bake takes about 160 min on an A100 (Table 1). The game runs on the client PC, and the headset only displays it.

### Repository layout

| Path | Role | Paper section |
|---|---|---|
| `server/reverie_server.py` | HTTP server: queues bakes (one at a time), reports progress, serves the results | 2.5 |
| `server/bake_theme.py` | Bakes one theme: panorama + 3DGS via DreamScene360, then audio | 2.2, 2.3 |
| `server/generator.py` | AudioLDM 2 wrapper (10 s clip, 200 inference steps) | 2.3 |
| `server/expand_prompts.py`, `server/fetch_props.py` | Prompt expansion and tool/target download, without Unity | 2.1, 2.4 |
| `server/log_times.py` | Per-stage wall-clock timing, GPU checks, summary for Table 1 | 2.5, Table 1 |
| `server/llm_system_prompt.txt` | LLM system prompt, identical to the one Theme Tools sends | 2.1 |
| `server/environment.yml` | Conda env `reverie-audio` (pinned versions) | — |
| `server/requirements.txt` | The no-GPU subset of those pins: server, bake orchestration, tests | — |
| `server/tests/` | Unit tests, no-GPU bake and HTTP API tests; `dreamscene360_no_gpu/` stands in for DreamScene360 | — |
| `client/unity/Assets/Scripts/Generation/ThemeTools/ThemeToolsCore.cs` | Theme Tools (Editor window and Theme Studio scene): prompt expansion (Ollama), server bake and download, Sketchfab retrieval, provenance records | 2.1, 2.4 |
| `client/unity/Assets/Scripts/Runtime/RuntimeSplatLoader.cs` | Loads 3DGS `.ply` files and prebaked splats at runtime, in the Editor and in builds | 2.4 |
| `client/unity/Assets/Scripts/Runtime/` | BubbleBurst game, theme loading, per-component toggles (no generation code) | 2.4, 2.7 |
| `client/unity/Assets/Scripts/Generation/` | Theme generation: Theme Tools, LLM and Sketchfab clients, API config | 2.1, 2.4 |
| `client/unity/Assets/StreamingAssets/api_config.example.json` | Client settings: server URL and token, Sketchfab token | — |

The repository contains the source code and the complete Unity project. The sample themes are a separate download (README, *Sample themes*). External repositories, model weights, third-party asset packs and participant data are excluded; Sections 5 and 11 explain how to obtain them.

---

## 2. Claims supported by this artifact

| Paper claim | How to check | Time | Hardware |
|---|---|---|---|
| The pipeline turns a theme word into a panorama, a 3DGS scene, audio and a themed tool/target, using only open models (Section 2.1–2.4) | §6 | ~3 h per theme | server + client |
| Generation times by stage (Table 1) | §8.1 | ~3 h per run | server (A100 for comparable times) |
| Prompt expansion takes seconds on a laptop (Table 1) | §4.2, §8.1 | 5 min | Any machine with Ollama |
| Per-component toggling of audio, skybox, 3DGS and tool/target (Section 2.7) | §7 | — | client + Quest 3 |

---

## 3. Requirements

### Hardware used in the study

| Role | Configuration in the paper | Minimum we expect to work |
|---|---|---|
| Server | 12 CPU cores, 64 GB RAM, 1 × NVIDIA A100 80 GB, Linux | NVIDIA GPU ≥ 16 GB (measured peak: 14.8 GB), CUDA 12.x, ~30 GB disk; Linux, or Windows with WSL2 |
| Client | Laptop: Intel Core i9, NVIDIA RTX 5070, 32 GB RAM, Windows | Windows PC with a Quest Link–compatible NVIDIA GPU |
| Headset | Meta Quest 3, Quest Link (USB-C) | Any Quest supported by Quest Link |

### Software

| Component | Version | Where |
|---|---|---|
| Conda (Miniconda or Mambaforge) | any recent | server |
| DreamScene360 | commit `4a7664e4c4b2891c84c2bc0bede771049271b341` | server |
| FastAPI / Uvicorn | 0.115.5 / 0.32.1 | server, `reverie-audio` |
| PyTorch | 2.4.0 + CUDA 12.4 | server (both envs) |
| diffusers / transformers / accelerate | 0.30.3 / 4.44.2 / 0.34.2 | server, `reverie-audio` |
| Ollama + `llama3.1:latest` | Llama 3.1 8B Instruct, Q4_K_M | client |
| Unity (URP) | 6000.0.56f1, URP 17.0.4 | client |
| Unity packages | see §5.4 | client |
| Python | 3.10 | server |

---

## 4. Quick evaluation for reviewers (no GPU, under 30 min)

These checks test the non-GPU parts of the pipeline. Each runs on an ordinary laptop. To play the game itself, download a Windows build from the repository's Releases page.

```bash
git clone https://github.com/MeghanaSpurthiM/Reverie.git reverie && cd reverie
```

### 4.1 Server pipeline control flow (~1 min)

```bash
cd server
pip install -r requirements.txt
python -m unittest discover tests
python log_times.py bake --theme test --dry-run
python log_times.py summary --include-dry-run
```

**Expected:** the tests pass, including a full bake and the server's HTTP API, both using a stand-in for DreamScene360 and a mock audio tone. The next two commands then show four logged stages (`panorama`, `3dgs`, `pano+3dgs`, `audio`) with simulated durations, followed by a summary table and LaTeX rows. This checks stage ordering, CSV logging and the summary that produces Table 1, without loading any model. Set `TIMINGS_CSV=/tmp/test.csv` to keep the dry-run rows out of `timings.csv`. To run the HTTP server itself without a GPU, follow [Generation Server: Quick test](SERVER.md#4-quick-test-no-gpu-5-min).

### 4.2 LLM prompt expansion (~10 min, CPU is fine)

Install [Ollama](https://ollama.com), then:

```bash
ollama pull llama3.1
cd server
python log_times.py llm --theme forest --repeats 3 --save-json forest_prompts.jsonl
```

**Expected:**
- The header reports `llama3.1:latest (llama 8.0B, Q4_K_M)`.
- Each run prints a `setting`, a `visualDescription` and an `audioDescription`.
- The rows are appended to `timings.csv`.

The request is identical to the one the Unity window sends: same system prompt, temperature 0.4, JSON output. On a CPU-only machine each run takes tens of seconds. The paper's 5.4–5.9 s was measured on the RTX 5070 laptop with the model already loaded. Add `--cold` to include model loading in the time.

---

## 5. Full installation

### 5.1 Choose where the server runs

The client needs only the server's URL (and a token, if one is set). Pick one option:

| Option | When | Start the server | Server URL in the client |
|---|---|---|---|
| **A. Same machine** | The client PC has an NVIDIA GPU with ≥ 16 GB. Run the server in **WSL2** (Ubuntu) on Windows; WSL2 passes the GPU through and forwards `localhost`. | `python reverie_server.py` | `http://localhost:8000` |
| **B. Another machine on the network** | A Linux GPU workstation reachable from the client | `REVERIE_TOKEN=<secret> python reverie_server.py --host 0.0.0.0` | `http://<server-ip>:8000`, with the same token |
| **C. Remote cluster or firewalled server** (as in the study) | No open ports; SSH access only | `python reverie_server.py` on the GPU node | Run `ssh -N -L 8000:localhost:8000 <user>@<server>` on the client, then use `http://localhost:8000` |

Option C keeps the server private: it listens only on the GPU node's localhost, and the SSH tunnel carries the traffic. Use option B only on a trusted network, and always set `REVERIE_TOKEN`, because anyone who can reach the port can start GPU jobs. In option A, the GPU is shared by 3DGS training (~15 GB) and, later, by Unity and the headset, so bake before you play. GPUs of the NVIDIA RTX 50 series (Blackwell) need PyTorch ≥ 2.7 with CUDA 12.8, which is newer than DreamScene360's pinned PyTorch 2.4; this combination is untested.

### 5.2 Server

**Step 1. Get this repository, and clone DreamScene360 beside `server/`.** DreamScene360 is git-ignored, so it is never committed here.

```bash
git clone https://github.com/MeghanaSpurthiM/Reverie.git reverie && cd reverie
git clone https://github.com/ShijieZhou-UCLA/DreamScene360
git -C DreamScene360 checkout 4a7664e        # the commit the paper used
```

If DreamScene360 is elsewhere, set `export DREAMSCENE360_DIR=/path/to/DreamScene360`.

**Step 2. Set up DreamScene360 exactly as its README describes.** [Generation Server §2.2 and §3.1](SERVER.md#22-dreamscene360-env) repeat its commands at the pinned commit. That covers:
- the `dreamscene360` conda env (Python 3.8, PyTorch 2.4.0 + CUDA 12.4, tiny-cuda-nn);
- its `requirements.txt` and the two CUDA submodules (`diff-gaussian-rasterization-depth`, `simple-knn`);
- the checkpoints: Omnidata depth (`pre_checkpoints/omnidata_dpt_depth_v2.ckpt`), and the Stable Diffusion 2.1 base model, VAE and panorama LoRA for StitchDiffusion (`stitch_diffusion/pretrained_model/`).

Keep the env name `dreamscene360`, because `bake_theme.py` finds it by that name. The study does **not** use DreamScene360's GPT-4V self-refinement, so no OpenAI key is needed.

**Step 3. Create the audio and orchestration environment.**

```bash
conda env create -f server/environment.yml
conda activate reverie-audio
python -c "import torch; print(torch.cuda.is_available())"     # must print True
```

The AudioLDM 2 weights (`cvssp/audioldm2`, 4.5 GB) download from Hugging Face on first use. To download them now, see [Generation Server §3.2](SERVER.md#32-audioldm-2).

**Step 4. Check the installation** with a short real bake, audio only (a few minutes):

```bash
cd server
python bake_theme.py --theme forest --audio-only --audio-prompt "gentle wind through tall trees, birdsong, no music, no voices"
```

It should end with `Audio saved: .../outputs/forest/audio.wav`. Before a timed bake, `log_times.py` also confirms that `nvidia-smi` sees a GPU and that PyTorch can use CUDA in **both** environments, and stops if either fails. This guards against a silent CPU fallback.

**Step 5. Start the server** (in `server/`, with `reverie-audio` active), using the command for your option in §5.1:

```bash
python reverie_server.py                                    # A, C: listens on localhost:8000
REVERIE_TOKEN=<secret> python reverie_server.py --host 0.0.0.0 --port 8000    # B
curl http://localhost:8000/health                           # → {"ok": true, "gpu": "...", ...}
```

The server writes each theme to `server/outputs/<theme>/`, with the full bake log in `bake.log`. On a shared cluster, run it inside `tmux` or a batch job so it survives logging out. Set `REVERIE_OUTPUTS` to put outputs on a larger disk.

### 5.3 Client: Ollama

```bash
ollama pull llama3.1        # llama3.1:latest → Llama 3.1 8B Instruct, 4-bit Q4_K_M
ollama list                 # confirm the model is present; Ollama serves on :11434
```

### 5.4 Client: Unity project

**Step 1.** Open `client/unity/` with Unity 6000.0.56f1 from Unity Hub. The first open downloads the packages listed in `client/unity/Packages/manifest.json` (pinned in `packages-lock.json`), so it needs network access, and then imports the TextMesh Pro Essential Resources. None of these packages is stored in the repository:

| Package | Version |
|---|---|
| Universal Render Pipeline | 17.0.4 |
| Meta XR Core SDK | 85.0.0 |
| [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) | 1.1.2 |
| glTFast | 6.1.0 |
| Newtonsoft Json | 3.2.2 |

**Step 2.** Open `client/unity/Assets/Scenes/3DGS_Unity.unity`. It should compile with no errors.

**Step 3.** Run the tests with *Window → General → Test Runner*. All EditMode and PlayMode tests should pass once the sample themes are in place (Step 4).

**Step 4.** The sample themes are too large for this repository. Copy `Reverie_Data/StreamingAssets/SampleThemes/` from a Windows build into `client/unity/Assets/StreamingAssets/`.

**Step 5.** Open **Reverie → Theme Tools**, expand *API keys*, paste the server token (if any) and a Sketchfab token, and click **Save keys on this machine**; set the server URL (§5.1) under *Settings*. Or copy `client/unity/Assets/StreamingAssets/api_config.example.json` to `api_config.json` in the same folder and fill it in. That file is git-ignored; keep it out of version control.

A Sketchfab token is free with an account (*Settings → Password & API*). It is needed only for downloading the tool and target.

---

## 6. Generating a theme end to end

Example theme: `forest`. Approximate time: 3 h, almost all of it 3DGS training. Everything happens in **Theme Tools**: *Reverie → Theme Tools* in the Editor, or the `ThemeStudio` scene (also in the *Theme Studio + Game* build). Themes are written to `client/unity/Assets/StreamingAssets/Themes/<theme>/` (Editor) or `Application.persistentDataPath/Themes/<theme>/` (build).

**Step 1. Expand (client, ~6 s).** Enter `forest` as the theme word. On the *Audio / Visual* tab, keep bake mode **Full** and click **Expand Prompts**. The *setting*, *visual prompt* and *audio prompt* appear and can be edited. The system prompt is identical to `server/llm_system_prompt.txt`; temperature (0.4) and seed (none) are under *Settings*. The panel writes `llm_expansion.json`, recording the endpoint, model, temperature, seed, prompts and timing.

**Step 2. Bake (server, ~165 min).** Click **Check server**: it reports the server's GPU and queue, and whether your token is accepted. Then click **Bake on Server + Download**. The server runs `bake_theme.py` with the prompts:

| Stage | Output on the server | Typical time (A100) |
|---|---|---|
| StitchDiffusion panorama | `outputs/forest/panorama.jpg` | 1–2 min |
| 3DGS training | `outputs/forest/point_cloud/.../point_cloud.ply` | ~160 min |
| AudioLDM 2 | `outputs/forest/audio.wav` (10 s ambience, looped in Unity) | 0.4–2 min |

The panel shows the stage and progress. Bakes are queued and run one at a time. Other bake modes redo part of a theme: *Pano+3DGS*, *Audio*, and *3DGS from pano* (trains from the theme's `panorama.jpg`, uploading it if you have it locally). Tick **Rebake existing** to redo stages the server already has. If you close Unity during a bake, click **Bake on Server + Download** again later: the server reports the finished bake and the panel downloads it.

**Step 3. Download (automatic).** When the bake finishes, the panel downloads `panorama.jpg`, `scene.ply`, `audio.wav` and `bake_meta.json` into the theme folder. The 3DGS layer converts `scene.ply` at runtime (`RuntimeSplatLoader`, same data as the *Very High* import preset), in the Editor and in builds. To load it faster, import it first: see [Add a 3DGS scene](GAME.md#add-a-3dgs-scene).

*Manual alternative, without the HTTP server:* run `python bake_theme.py --theme forest --prompt "<visual>" --audio-prompt "<audio>"` in `server/` on the GPU machine and copy `server/outputs/forest/` into the theme folder (for example with `scp`). `--3dgs-only`, `--audio-only` and `--no-audio` redo single stages; `--list-steps` shows what a run will do.

**Step 4. Tool and target (client, 6–33 s).** On the *Tool / Target* tab, click **Suggest Tool / Target** (or type your own; each can be a search phrase such as `axe` or an exact 32-character Sketchfab model UID), then **Fetch Tool/Target GLBs**. This saves `tool_<name>.glb`, `target_<name>.glb` and `sketchfab.json`, which records each model's UID, author, URL and license for attribution.

The theme folder is then complete:

```
client/unity/Assets/StreamingAssets/Themes/forest/
  llm_expansion.json   panorama.jpg   audio.wav   bake_meta.json   scene.ply
  tool_<name>.glb   target_<name>.glb   sketchfab.json
```

Click **Use in game** (or **Play this theme** in a Studio build) and play `3DGS_Unity`; **Make project default** writes it into `client/unity/Assets/FolderConfig.asset` instead.

**Study themes.**
| Theme | Visual prompt | Tool | Target |
|---|---|---|---|
| Forest | *A dense, lush forest bathed in the soft, dappled light of early morning, with beams of sunlight piercing through the thick canopy of towering trees. The ground is a rich tapestry of moss and fallen leaves, with a gentle mist hovering close to the earth, creating an ethereal atmosphere. The sky above is a pale blue, barely visible through the thick foliage.* | `5038f07f634345f5ac9bfa74fb821c97` | `27624ee05fea49899ba2a63e2f6d1e34` |
| Beach | *beach* | `58873069d37f4dc18993d99d45189b05` | `410be9cdc3d84f6a9f139189309b4891` |

The tool and target values are Sketchfab model UIDs, which you can pass to `fetch_props.py` or Theme Tools. The generated panoramas, splats and audio are included in the Windows builds.

---

## 7. Running the VR game

Open `client/unity/Assets/Scenes/3DGS_Unity.unity`. For headset setup and the keyboard controls, see [Play the game](GAME.md#play-the-game).

The base scene in this repository is a plain placeholder, because the study's living room was built from a commercial asset pack that cannot be redistributed. To use your own room, see [Replace the base room](GAME.md#replace-the-base-room).

**Running a session:**
1. Connect the Quest 3 with Quest Link, or use the keyboard and mouse.
2. Enter Play mode. Play begins in the base scene.
3. Load a theme with **Reverie → Theme Folder → Choose...**.
4. Switch layers with the in-game toggle panel, or with keys `1`–`5` (audio, skybox, 3DGS, tool/target, room).

This follows the study procedure in Section 2.6: base scene, then first theme, base scene, second theme, with layers toggled in any order.

---

## 8. Replicating the paper's results

### 8.1 Generation times (Table 1)

**Server (A100), three runs.** Each run uses a fresh output folder, so it starts from scratch and includes model loading:

```bash
python log_times.py bake --theme forest --prompt "<visual>" --audio-prompt "<audio>" --repeats 3
```

**Laptop.** Time prompt expansion with the model already loaded, as in the paper:

```bash
python log_times.py llm --theme forest --repeats 3
```

**Sketchfab tool/target retrieval (optional; needs `SKETCHFAB_TOKEN`).** Add `--tool axe --target log` to the server command.

**Summary.** Collect all `timings.csv` rows in one file, then run:

```bash
python log_times.py summary
```

**Reported values** (forest theme; minimum–maximum, number of runs in parentheses):

| Stage | Paper (Table 1) |
|---|---|
| Prompt expansion | 5.4–5.9 s (3) |
| Panorama | 1.0–2.1 min (2) |
| 3DGS, after the panorama | 158.8–160.1 min (2) |
| Full DreamScene360 run | 159.8–162.2 min |
| Audio | 0.4–1.8 min (2) |
| Tool + target retrieval | 6–33 s (6) |

Times depend on the GPU, disk and network. On a different GPU, expect the 3DGS stage to scale roughly with GPU throughput. The panorama and audio times include model loading, which dominates the first run on a cold disk cache.

---

## 9. Sources of variation and determinism

Reviewers who regenerate themes should expect *qualitatively similar* assets, not identical ones:

| Stage | Seed | Effect |
|---|---|---|
| LLM expansion | none by default (temperature 0.4) | Different wording on each run. Fix it with **Seed** in the Unity window or `--seed` in `log_times.py llm`. Ollama output can still differ across Ollama versions and hardware. |
| StitchDiffusion panorama | set by DreamScene360's StitchDiffusion config | Fixed for a given config and GPU; it can change across GPUs and library versions. |
| 3DGS training | DreamScene360 seeds Python, NumPy and PyTorch with 0 | CUDA rasterization is not bit-deterministic, so final splats differ slightly between runs. |
| AudioLDM 2 | none | A different clip every run. The negative prompt is fixed in `generator.py`. |
| Sketchfab search | — | Search results change over time. Use model UIDs to get exact assets. |

To match the study environment exactly, use the released prompts and UIDs (§6, *Study themes*).

---

## 10. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Unity: `org.nesnausk.gaussian-splatting: Error when executing git command. fatal: not in a git directory` | The project is on a drive that doesn't record file ownership (exFAT/FAT32, some network drives), so git's *dubious ownership* check blocks Unity's package download. Trust the project folder and its subfolders: `git config --global --add safe.directory '<path-to-repo>/*'` (git ≥ 2.46), then click **Retry**. |
| `GPU preflight failed` in `log_times.py` | A CUDA build of PyTorch is missing in `reverie-audio` or in `dreamscene360`. Reinstall it with the CUDA 12.4 wheel index. |
| `conda env 'dreamscene360' not found` (bake preflight) | The env has a different name. Rename it, create it as `dreamscene360`, or set `DREAMSCENE360_PYTHON` to its Python. |
| DreamScene360 fails building `diff-gaussian-rasterization-depth` | The CUDA toolkit doesn't match PyTorch's CUDA version. See the DreamScene360 README ("PS: If fail to compile the CUDA rasterizer"). |
| `No panorama found` with `--3dgs-only` | Run a full bake first, or put `panorama.png` in `outputs/<theme>/`. |
| `Cannot reach Ollama` / `model not found` | Start Ollama, then run `ollama pull llama3.1`. Check the LLM endpoint under *Settings* in Theme Tools. |
| Unity: `GaussianSplatting` namespace not found | The Gaussian Splatting package is missing or is the wrong version. Install v1.1.2 (§5.4). |
| `Cannot reach the Reverie server` | The server isn't running, the URL is wrong, or (option C) the SSH tunnel is closed. Test with `curl <url>/health` on the client. |
| `Server rejected the token` | The client's token differs from the server's `REVERIE_TOKEN`. |
| Bake `failed` | The window shows the last log lines; the full log is `server/outputs/<theme>/bake.log`. |
| Splat not shown in Play mode | The theme folder needs `scene.ply`, or `scene.asset` with its `scene_*.bytes` files. The graphics API must be D3D12 or Vulkan. |
| Splat appears rotated | Adjust `GaussianSpawner.splatRotationOffset` (default −90° about X). |
| Tool/target missing | The theme folder needs `tool.glb` and `target.glb`, or `tool_*.glb` and `target_*.glb`. Check the Sketchfab token. |

---

## 11. Data availability and ethics

The user study was approved by the Institutional Review Board of the University at Albany, State University of New York (protocol STUDY00007853). Participants gave informed consent electronically.

The anonymized survey responses are stored on University at Albany institutional storage and will be made publicly available upon acceptance. No participant data are included in this repository.

---

## 12. Third-party components and licenses

See [THIRD_PARTY.md](THIRD_PARTY.md) for the credits and licenses of all third-party components. Models, DreamScene360 and other external dependencies keep their own licenses.

The code in this repository is released under the [MIT License](../LICENSE). Running the full pipeline also binds you to the non-commercial terms of the 3DGS-derived components.

---

## 13. Citation and contact

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

Corresponding author: Meghana Spurthi Maadugundu, Department of Computer Science, University at Albany, State University of New York (mmaadugundu@albany.edu). Please report issues through the repository's issue tracker.
