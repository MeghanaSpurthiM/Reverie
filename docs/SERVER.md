# Generation Server

The server turns a theme's prompts into a 360° panorama (StitchDiffusion), a 3D Gaussian splatting scene (DreamScene360) and ambient audio (AudioLDM 2). The Unity client sends it a bake request over HTTP and downloads the results into a [theme folder](GAME.md#theme-format).

You only need the server to generate **new** themes. The sample themes play without it, and prompt expansion and prop download run on your own PC (see [Configuration](#configuration-and-prompt-expansion)). For the paper's full procedure, see the [Replication Guide](REPLICATION_GUIDE.md).

| Section | What you get | GPU needed |
|---|---|---|
| [1. Requirements](#1-requirements) | hardware and pinned software | — |
| [2. Install](#2-install) | the `dreamscene360` and `reverie-audio` conda envs | no |
| [3. Download weights](#3-download-weights) | every checkpoint, with its source and size | no |
| [4. Quick test](#4-quick-test-no-gpu-5-min) | tests and a running server with stand-ins for the models | no |
| [5. Full run](#5-full-run) | a real audio clip (minutes), then a full theme (~165 min) | yes |
| [6. Outputs](#6-outputs) | output files, HTTP API, flags and settings | — |
| [7. Troubleshooting](#7-troubleshooting) | known problems and fixes | — |

Commands are for **bash on Linux or WSL2**, run from the repository root unless a step says `cd server`.

## 1. Requirements

| | Used in the paper | Minimum expected to work |
|---|---|---|
| GPU | 1 × NVIDIA A100 80 GB | NVIDIA GPU with ≥ 16 GB (measured peak 14.8 GB), driver ≥ 550 (CUDA 12.4) |
| CPU, RAM | 12 cores, 64 GB | 8 GB RAM |
| Disk | — | ~30 GB: two conda envs ~12 GB, weights ~11 GB, ~0.5 GB per theme |
| OS | Linux | Linux, or Windows 11 with WSL2 (Ubuntu). Native Windows and macOS are not supported for baking. |
| Software | Conda, git, CUDA toolkit 12.4 and GCC ≤ 13 (to compile DreamScene360's CUDA extensions) | same; §2.2 installs both into the env with conda |
| Internet at bake time | yes: DreamScene360 fetches DINOv2 and CLIP on its first bake | or pre-fetch them (§3.3) |

GPUs of the RTX 50 series (Blackwell, `sm_120`) need PyTorch ≥ 2.7 with CUDA 12.8 and driver ≥ 570, newer than the PyTorch 2.4 pinned here and by DreamScene360. With those, the three CUDA extensions build (on an RTX 5070 Laptop GPU), but a full bake on Blackwell is untested. CUDA 13 builds of PyTorch need driver ≥ 580.

Pinned versions:

| Component | Version | Env |
|---|---|---|
| [DreamScene360](https://github.com/ShijieZhou-UCLA/DreamScene360) | commit `4a7664e4c4b2891c84c2bc0bede771049271b341` | `dreamscene360` (Python 3.8) |
| PyTorch | 2.4.0 + CUDA 12.4 | both |
| [DINOv2](https://github.com/facebookresearch/dinov2) (fetched by DreamScene360 through `torch.hub`) | commit `b48308a394a04ccb9c4dd3a1f0a4daa1ce0579b8`, the last that runs on Python 3.8 (§2.2) | `dreamscene360` |
| diffusers / transformers / accelerate | 0.30.3 / 4.44.2 / 0.34.2 | `reverie-audio` (Python 3.10) |
| FastAPI / Uvicorn / pydantic | 0.115.5 / 0.32.1 / 2.9.2 | `reverie-audio` |

All pins are in [`server/environment.yml`](../server/environment.yml). [`server/requirements.txt`](../server/requirements.txt) repeats the subset that runs without a GPU.

## 2. Install

### 2.1 Get the code

```bash
git clone https://github.com/MeghanaSpurthiM/Reverie.git reverie
cd reverie
git clone https://github.com/ShijieZhou-UCLA/DreamScene360
git -C DreamScene360 checkout 4a7664e        # the commit the paper used
```

DreamScene360 must sit next to `server/` (it is git-ignored there). To keep it elsewhere, set `export DREAMSCENE360_DIR=/path/to/DreamScene360`.

### 2.2 DreamScene360 env

These are the commands from DreamScene360's README at the pinned commit. Keep the env name `dreamscene360`, because `bake_theme.py` finds it by that name.

```bash
cd DreamScene360
conda create -y --name dreamscene360 python=3.8
conda activate dreamscene360
pip install torch==2.4.0 torchvision==0.19.0 torchaudio==2.4.0 --index-url https://download.pytorch.org/whl/cu124

# CUDA 12.4 toolkit and GCC 11 inside the env, so the build doesn't depend on the system's
# (skip these if `nvcc --version` already prints 12.4 and `gcc --version` is 13 or older)
conda install -y -c nvidia/label/cuda-12.4.1 cuda-nvcc cuda-cudart-dev cuda-libraries-dev cuda-cccl cuda-driver-dev
conda install -y -c conda-forge "gxx_linux-64=11.*" "gcc_linux-64=11.*"
export CUDA_HOME="$CONDA_PREFIX"
export CC="$CONDA_PREFIX/bin/x86_64-conda-linux-gnu-gcc" CXX="$CONDA_PREFIX/bin/x86_64-conda-linux-gnu-g++"
export LIBRARY_PATH="$CONDA_PREFIX/lib/stubs:$CONDA_PREFIX/lib:${LIBRARY_PATH:-}"   # libcuda.so stub for tiny-cuda-nn's link step
export TORCH_CUDA_ARCH_LIST="8.0;8.6;8.9;9.0"   # optional: only your GPU's architecture builds faster

pip install --no-build-isolation git+https://github.com/NVlabs/tiny-cuda-nn/#subdirectory=bindings/torch
pip install -r requirements.txt
pip install --no-build-isolation submodules/diff-gaussian-rasterization-depth
pip install --no-build-isolation submodules/simple-knn
python -c "import torch, tinycudann, diff_gaussian_rasterization, simple_knn; print(torch.cuda.is_available())"   # must print True

# DINOv2 at the last commit that supports Python 3.8 (newer ones use `float | None`, Python 3.10).
# DreamScene360 calls torch.hub.load('facebookresearch/dinov2', ...), which reuses this checkout.
HUB="$(python -c 'import torch; print(torch.hub.get_dir())')"
git clone https://github.com/facebookresearch/dinov2 "$HUB/facebookresearch_dinov2_main"
git -C "$HUB/facebookresearch_dinov2_main" checkout b48308a394a04ccb9c4dd3a1f0a4daa1ce0579b8
conda deactivate
cd ..
```

The two `submodules/` packages and tiny-cuda-nn compile CUDA code. The conda lines above provide the CUDA 12.4 toolkit, GCC 11 and the `libcuda.so` link stub; with a system toolkit instead, you need CUDA 12.4 (`nvcc --version`), GCC ≤ 13 and, on Ubuntu, `sudo apt-get install -y build-essential libglm-dev`. If you set `TORCH_HOME`, set it to the same value when you run the server, because the DINOv2 checkout lives under it. The study does **not** use DreamScene360's GPT-4V self-refinement, so no OpenAI key is needed.

### 2.3 Reverie server env

```bash
conda env create -f server/environment.yml          # creates the env `reverie-audio`
conda activate reverie-audio
python -c "import torch; print(torch.__version__, torch.cuda.is_available())"   # 2.4.0+cu124 True
python -c "from diffusers import AudioLDM2Pipeline; import fastapi; print('ok')"
```

Use `reverie-audio` for every command in `server/` from here on.

## 3. Download weights

| Weights | Used for | Size | Source | License |
|---|---|---|---|---|
| Stable Diffusion 2.1 base (`v2-1_512-ema-pruned`) | StitchDiffusion panorama | 5.2 GB | [Hugging Face: sd2-community/stable-diffusion-2-1-base](https://huggingface.co/sd2-community/stable-diffusion-2-1-base) | CreativeML Open RAIL++-M |
| SD VAE ft-MSE (`vae-ft-mse-840000-ema-pruned`) | StitchDiffusion panorama | 0.3 GB | [Hugging Face: stabilityai/sd-vae-ft-mse-original](https://huggingface.co/stabilityai/sd-vae-ft-mse-original) | MIT |
| StitchDiffusion panorama LoRA | StitchDiffusion panorama | small | Google Drive, from the DreamScene360 authors | see DreamScene360 |
| Omnidata DPT depth v2 | depth for 3DGS initialisation | ~1 GB | Dropbox, from the [PERF](https://github.com/perf-project/PeRF/tree/master/pre_checkpoints) authors | see Omnidata |
| AudioLDM 2 | ambient audio | 4.5 GB (safetensors only) | [Hugging Face: cvssp/audioldm2](https://huggingface.co/cvssp/audioldm2) | CC BY-NC-SA 4.0 |

### 3.1 DreamScene360 checkpoints

These are DreamScene360's own download commands, run inside `DreamScene360/`:

```bash
cd DreamScene360
mkdir -p pre_checkpoints
tmp_dir="$(mktemp -d)"
wget -O "$tmp_dir/pre_checkpoints.zip" "https://www.dropbox.com/scl/fo/348s01x0trt0yxb934cwe/h?rlkey=a96g2incso7g53evzamzo0j0y&dl=1"
unzip -o "$tmp_dir/pre_checkpoints.zip" -d "$tmp_dir/pre_checkpoints" || test -f "$tmp_dir/pre_checkpoints/omnidata_dpt_depth_v2.ckpt"
find "$tmp_dir/pre_checkpoints" -name "omnidata_dpt_depth_v2.ckpt" -exec cp {} pre_checkpoints/ \;
rm -rf "$tmp_dir"

cd stitch_diffusion/pretrained_model
wget https://huggingface.co/sd2-community/stable-diffusion-2-1-base/resolve/main/v2-1_512-ema-pruned.safetensors -O stable-diffusion-2-1-base.safetensors
cd ../vae
wget https://huggingface.co/stabilityai/sd-vae-ft-mse-original/resolve/main/vae-ft-mse-840000-ema-pruned.ckpt -O stablediffusion.vae.pt
cd ..
wget --no-check-certificate "https://drive.google.com/uc?export=download&id=1MiaG8v0ZmkTwwrzIEFtVoBj-Jjqi_5lz" -O lora.safetensors
cd ../..
```

Check that all four files are in place:

```bash
ls -lh DreamScene360/pre_checkpoints/omnidata_dpt_depth_v2.ckpt \
       DreamScene360/stitch_diffusion/pretrained_model/stable-diffusion-2-1-base.safetensors \
       DreamScene360/stitch_diffusion/vae/stablediffusion.vae.pt \
       DreamScene360/stitch_diffusion/lora.safetensors
```

If the Google Drive or Dropbox link stops working, check the [DreamScene360 README](https://github.com/ShijieZhou-UCLA/DreamScene360) for current links.

### 3.2 AudioLDM 2

`generator.py` downloads AudioLDM 2 from the Hugging Face Hub on first use. Download it now instead, so the first bake doesn't stall:

```bash
conda activate reverie-audio
python -c "from diffusers import AudioLDM2Pipeline; AudioLDM2Pipeline.from_pretrained('cvssp/audioldm2')"
```

This fetches only the `.safetensors` files the pipeline needs. The weights go to the Hugging Face cache (`~/.cache/huggingface`). Set `HF_HOME` to put the cache on another disk, and `AUDIOLDM2_MODEL_ID` to load a different copy (a Hub ID or a local folder).

### 3.3 Models fetched at bake time

On its first bake DreamScene360 also downloads, into the caches of the `dreamscene360` env:

| Model | Fetched by | Cache |
|---|---|---|
| DINOv2 ViT-B/14 code and weights (~350 MB) | `torch.hub` in `utils/feature_extractor.py` | `$TORCH_HOME/hub` (default `~/.cache/torch/hub`) |
| CLIP ViT-L/14 text encoder (~1.7 GB) | `transformers` in StitchDiffusion | `$HF_HOME/hub` (default `~/.cache/huggingface/hub`) |

If the GPU nodes have no internet, run one bake on a node that does, or copy both caches over, and keep `TORCH_HOME` and `HF_HOME` the same when serving.

## 4. Quick test (no GPU, ~5 min)

This checks the server, the bake steps and the HTTP API without any model. A stand-in for DreamScene360 (`server/tests/dreamscene360_no_gpu/train.py`) writes a tiny panorama and an empty `.ply`, and `AUDIO_MODE=mock` writes a 60 Hz test tone. It works on any machine with Python 3.10, including Windows and macOS.

**Step 1. Run the tests.**

```bash
cd server
pip install -r requirements.txt
python -m unittest discover tests
```

Expected: `OK` after under a minute. If `fastapi` or `uvicorn` is missing, the HTTP tests are reported as skipped instead of run.

**Step 2. Start the server with the stand-ins** (in `server/`):

```bash
DREAMSCENE360_DIR=tests/dreamscene360_no_gpu DREAMSCENE360_PYTHON="$(python -c 'import sys; print(sys.executable)')" AUDIO_MODE=mock python reverie_server.py
```

In PowerShell:

```powershell
$env:DREAMSCENE360_DIR="tests/dreamscene360_no_gpu"; $env:DREAMSCENE360_PYTHON=(python -c "import sys; print(sys.executable)"); $env:AUDIO_MODE="mock"; python reverie_server.py
```

Expected: `Reverie server on http://127.0.0.1:8000`.

**Step 3. In a second terminal, bake and download a theme:**

```bash
curl -s http://localhost:8000/health
curl -s -X POST http://localhost:8000/themes/demo/bake -H "Content-Type: application/json" -d '{"prompt": "a quiet forest at dawn", "audio_prompt": "wind in the trees, birdsong"}'
curl -s http://localhost:8000/themes/demo/status
curl -s -o panorama.jpg http://localhost:8000/themes/demo/panorama
curl -s -o scene.ply http://localhost:8000/themes/demo/ply
curl -s -o audio.wav http://localhost:8000/themes/demo/audio
curl -s http://localhost:8000/themes/demo/meta
```

Expected: `/health` returns `{"ok":true,...}` and the bake request returns `"status":"running"` or `"queued"`. The stand-in bake takes about a second: repeat the `/status` request until it returns `"status":"done"`, with `has_panorama`, `has_ply` and `has_audio` all `true`. Then download the files. The full log is in `server/outputs/demo/bake.log`.

**Step 4. Clean up.** Stop the server with `Ctrl+C`, then:

```bash
rm -rf outputs/demo panorama.jpg scene.ply audio.wav
```

## 5. Full run

### 5.1 Check the GPU

```bash
cd server
conda activate reverie-audio
nvidia-smi
python -c "import torch; print(torch.cuda.get_device_name(0))"
python bake_theme.py --theme forest --prompt "a forest" --list-steps
```

`--list-steps` prints the seven steps a full bake runs and changes nothing. `log_times.py bake` (§5.5) also checks, before it starts, that PyTorch sees CUDA in **both** envs.

### 5.2 Audio only (a few minutes)

Start with the shortest real bake, to check AudioLDM 2 and the GPU:

```bash
python bake_theme.py --theme forest --audio-only --audio-prompt "gentle wind through tall trees, birdsong, rustling leaves, no music, no voices"
```

Expected: `Audio saved: .../outputs/forest/audio.wav (861 KB)`, a 10 s clip at 44.1 kHz. On an A100 this takes 0.4–2 min, most of it loading the model.

### 5.3 A full theme (~165 min)

From the command line:

```bash
python bake_theme.py --theme forest \
  --prompt "A dense, lush forest bathed in the soft, dappled light of early morning, with beams of sunlight piercing through the thick canopy of towering trees. The ground is a rich tapestry of moss and fallen leaves, with a gentle mist hovering close to the earth, creating an ethereal atmosphere. The sky above is a pale blue, barely visible through the thick foliage." \
  --audio-prompt "gentle wind through tall trees, birdsong, rustling leaves, no music, no voices"
```

This is the study's *Forest* prompt. To get prompts for your own theme, run `python expand_prompts.py --theme <word>` on a machine with Ollama (see [Configuration](#expand-a-theme)).

| Step | Output | Time on an A100 |
|---|---|---|
| StitchDiffusion panorama | `outputs/forest/panorama.jpg` | 1–2 min |
| DreamScene360 | `outputs/forest/point_cloud/.../point_cloud.ply` | ~160 min |
| AudioLDM 2 | `outputs/forest/audio.wav` | 0.4–2 min |

A step whose output already exists is skipped, so an interrupted bake resumes where it stopped. Add `--overwrite` to redo everything.

### 5.4 Through the HTTP server

To bake from Unity's Theme Tools, start the server where the GPU is and point the client's `server.url` at it ([Configuration](#set-up-api_configjson)):

| Server runs on | Start the server (in `server/`) | `server.url` on the client |
|---|---|---|
| The client PC, under WSL2 | `python reverie_server.py` | `http://localhost:8000` |
| A GPU node reached by SSH (as in the study) | `python reverie_server.py`, then on the client `ssh -N -L 8000:localhost:8000 <user>@<server>` | `http://localhost:8000` |
| A Linux machine on your network | see below | `http://<server-ip>:8000`, and `server.token` |

On a network, always set a token, because anyone who can reach the port can start GPU jobs:

```bash
export REVERIE_TOKEN="$(python -c 'import secrets; print(secrets.token_urlsafe(24))')"
echo "$REVERIE_TOKEN"            # copy this into server.token on the client
python reverie_server.py --host 0.0.0.0
```

On a shared cluster, run the server inside `tmux` or a batch job so it survives logging out.

### 5.5 Timing (Table 1)

```bash
python log_times.py bake --theme forest --prompt "<visual prompt>" --audio-prompt "<audio prompt>" --repeats 3
python log_times.py summary
```

Each run bakes into a fresh folder, so no step is skipped. See [Replication Guide §8.1](REPLICATION_GUIDE.md#81-generation-times-table-1).

## 6. Outputs

### 6.1 Files

```
server/outputs/<theme>/
├── panorama.jpg                                  # StitchDiffusion panorama (skybox)
├── point_cloud/.../point_cloud.ply               # 3DGS scene; the client saves it as scene.ply
├── audio.wav                                     # AudioLDM 2, 10 s, 44.1 kHz, 16-bit mono
├── meta.json                                     # prompts, file paths, per-step seconds; the client saves it as bake_meta.json
└── bake.log                                      # full log, when baked through the server
```

`<theme>` is the theme name in lowercase, with every run of other characters replaced by `-` (`Tropical Beach` → `tropical-beach`). DreamScene360's own working files go to `DreamScene360/data/<theme>/` and `DreamScene360/output/<theme>/`.

To play a theme, copy the files into a theme folder as described in [Configuration](#where-theme-files-go).

### 6.2 HTTP API

Every endpoint except `/health` needs `Authorization: Bearer <REVERIE_TOKEN>` when the server was started with `REVERIE_TOKEN`. `<slug>` must already be lowercase letters, digits and hyphens.

| Request | Purpose | Responses |
|---|---|---|
| `GET /health` | GPU, running and queued bakes, whether a token is required | 200 |
| `GET /themes` | themes on disk and which files each has | 200 |
| `POST /themes/<slug>/bake` | queue a bake (JSON body below); returns the job | 200; 400 bad slug, no prompt or nothing to bake; 409 no panorama for `use_existing_panorama`; 422 wrong types |
| `GET /themes/<slug>/status` | `queued`, `running`, `done` or `failed`, with `stage`, `progress` (0–1), `error` and `has_*` | 200; 404 unknown theme |
| `PUT /themes/<slug>/panorama` | upload a raw JPEG or PNG to train 3DGS from | 200; 409 while baking; 415 not an image |
| `GET /themes/<slug>/panorama`, `/ply`, `/audio`, `/meta` | download a result | 200; 404 not ready |

Bake request body (every field optional):

| Field | Default | Effect |
|---|---|---|
| `prompt` | `""` | visual prompt; required unless `scene` is false or `use_existing_panorama` is true |
| `audio_prompt` | `""` | audio prompt; if empty, one is built from `prompt` |
| `scene` | `true` | panorama + 3DGS |
| `audio` | `true` | AudioLDM 2 |
| `use_existing_panorama` | `false` | train 3DGS from the theme's uploaded or earlier panorama |
| `overwrite` | `false` | redo steps whose output exists; without it, a finished theme is returned as `done` |

Bakes run one at a time. Jobs are kept in memory, so after a server restart `/status` reports what is on disk (`done` or `partial`).

### 6.3 `bake_theme.py`

| Flag | Effect |
|---|---|
| `--theme` | theme name; its slug names the output folder (required) |
| `--prompt` | visual prompt (default: the theme name) |
| `--audio-prompt` | audio prompt (default: built from the visual prompt) |
| `--3dgs-only [--panorama PATH]` | train from an existing panorama (default `outputs/<theme>/panorama.jpg`) instead of generating one |
| `--audio-only` | only audio |
| `--no-audio` | skip audio |
| `--overwrite` | redo steps whose output exists |
| `--list-steps` | print the steps this run would execute, and exit |

The steps are `preflight` (checks DreamScene360, its env, Pillow and the audio packages), `prepare-input`, `dreamscene360`, `collect-ply`, `export-panorama`, `audio` and `meta`. Each stops with a clear error instead of guessing. Exit code 0 means success, 1 a failed step, 2 bad arguments.

### 6.4 Environment variables

| Variable | Effect | Default |
|---|---|---|
| `REVERIE_TOKEN` | require `Authorization: Bearer <token>` | no token |
| `REVERIE_OUTPUTS` | output folder | `server/outputs` |
| `DREAMSCENE360_DIR` | DreamScene360 checkout | `DreamScene360/` next to `server/` |
| `DREAMSCENE360_PYTHON` | Python of the DreamScene360 env | found from the `dreamscene360` conda env |
| `AUDIO_MODE` | `audioldm2`, or `mock` for a test tone | `audioldm2` |
| `AUDIOLDM2_MODEL_ID` | AudioLDM 2 weights: Hub ID or local folder | `cvssp/audioldm2` |
| `HF_HOME` | Hugging Face cache folder | `~/.cache/huggingface` |

## 7. Troubleshooting

| Problem | Fix |
|---|---|
| `EnvironmentNameNotFound: reverie` | The env is called `reverie-audio`: `conda activate reverie-audio`. |
| `torch.cuda.is_available()` prints `False` | Install the CUDA build: `pip install torch==2.4.0 --index-url https://download.pytorch.org/whl/cu124`. Check the driver with `nvidia-smi`. |
| `no kernel image is available for execution on the device` | The GPU is newer than PyTorch 2.4 supports (for example RTX 50 series). See [Requirements](#1-requirements). |
| `Preflight failed: DreamScene360 not found` | Clone it next to `server/` (§2.1) or set `DREAMSCENE360_DIR`. |
| `conda env 'dreamscene360' not found` | Create it with that name (§2.2), or set `DREAMSCENE360_PYTHON` to its Python. |
| `generator.py failed to import` | Run the bake from the `reverie-audio` env, not `dreamscene360` or base. |
| DreamScene360 fails building `diff-gaussian-rasterization-depth` | The CUDA toolkit doesn't match PyTorch's CUDA 12.4. Install toolkit 12.4 and `sudo apt-get install -y libglm-dev`, or use the conda toolkit (§2.2). |
| `unsupported GNU version! gcc versions later than 13 are not supported` | CUDA 12.4 needs GCC ≤ 13. Install GCC 11 into the env and set `CC`/`CXX` (§2.2). |
| tiny-cuda-nn fails with `cannot find -lcuda` | The build node has no driver library to link against. Install `cuda-driver-dev` and put `$CONDA_PREFIX/lib/stubs` on `LIBRARY_PATH` (§2.2). |
| Bake fails in DreamScene360 while loading `dinov2`, with a `TypeError` about `\|` (Python 3.10 type syntax) | `torch.hub` fetched a DINOv2 that needs Python 3.10. Check out commit `b48308a` in `$TORCH_HOME/hub/facebookresearch_dinov2_main` (§2.2). |
| `FileNotFoundError` for `omnidata_dpt_depth_v2.ckpt` or `lora.safetensors` | A checkpoint is missing. Run the `ls` check in §3.1. |
| `--3dgs-only needs a panorama` | Run a full bake first, upload one with `PUT /themes/<slug>/panorama`, or pass `--panorama PATH`. |
| CUDA out of memory | 3DGS training needs ~15 GB. Close other GPU programs, including Unity, or use a larger GPU. |
| `401` from every endpoint | Send `Authorization: Bearer <token>`, with the same token as the server's `REVERIE_TOKEN`. |
| `address already in use` | Another program uses port 8000: `python reverie_server.py --port 8001`, and change `server.url`. |
| Bake `failed` | `/status` shows the last 15 log lines in `error`; the full log is `server/outputs/<theme>/bake.log`. |

## Configuration and prompt expansion

This guide explains how to set up `api_config.json` and how to run Llama 3.1 prompt expansion, either in Unity or from the command line.

You only need this guide to generate new themes. The sample themes play without any configuration.

### Set up `api_config.json`

1. Copy `client/unity/Assets/StreamingAssets/api_config.example.json` to `api_config.json` in the same folder.
2. Fill in the sections you need. Every section is optional, and `//` comments are allowed.

Git ignores `api_config.json`, so your keys stay out of the repository.

| Key | When you need it |
|---|---|
| `server.url` | To bake on the Reverie server. Use `http://localhost:8000` on the same machine or through an SSH tunnel. |
| `server.token` | When the server was started with `REVERIE_TOKEN`. |
| `sketchfab.api_token` | To download tools and targets. Get a free token from *sketchfab.com → Settings → Password & API*. You can also set the `SKETCHFAB_TOKEN` environment variable. |

The LLM endpoint and model are not set in this file. They are set in Theme Tools (see [Change the LLM settings](#change-the-llm-settings)).

#### Compiled builds

Builds never contain your keys, because they are blanked for the duration of every build. To add keys to a *Theme Studio + Game* build, enter them in Theme Tools under *Settings*, then click **Save server + Sketchfab settings on this device**. This writes `api_config.json` to `Application.persistentDataPath`, which overrides the shipped file.

#### Server settings

The server is configured with environment variables:

| Variable | Effect | Default |
|---|---|---|
| `REVERIE_TOKEN` | Requires `Authorization: Bearer <token>` on requests | No token |
| `REVERIE_OUTPUTS` | Output folder | `server/outputs` |
| `DREAMSCENE360_DIR` | DreamScene360 checkout | `../DreamScene360` |
| `DREAMSCENE360_PYTHON` | Python interpreter for DreamScene360 | Found from the `dreamscene360` conda env |
| `AUDIO_MODE` | `audioldm2`, or `mock` for a test tone without a GPU | `audioldm2` |
| `AUDIOLDM2_MODEL_ID` | AudioLDM 2 weights: Hugging Face ID or local folder | `cvssp/audioldm2` |

See [Generation Server](#64-environment-variables) for the full list.

### Set up Llama 3.1

Prompt expansion runs locally through [Ollama](https://ollama.com). It works on a CPU, but more slowly.

1. Install Ollama.
2. Download the model:

   ```bash
   ollama pull llama3.1
   ```

3. Confirm that it is available:

   ```bash
   ollama list
   ```

   The list should include `llama3.1:latest`. Ollama serves it on port 11434.

### Run prompt expansion in Unity

1. Open **Reverie → Theme Tools**.
2. Enter a theme word, such as `forest`.
3. On the *Audio / Visual* tab, click **Expand Prompts**.
4. Review the *setting*, *visual prompt* and *audio prompt*. You can edit all three before baking.

The panel saves `llm_expansion.json` in the theme folder, recording the endpoint, model, temperature, seed, prompts and timing.

#### Change the LLM settings

These are under *Settings* in Theme Tools:

| Setting | Default | Notes |
|---|---|---|
| LLM endpoint | `http://localhost:11434/v1/chat/completions` | Must be on your machine. |
| LLM model | `llama3.1:latest` | Any model you have pulled in Ollama. |
| Temperature | `0.4` | The value used in the study. |
| Seed | `-1` | `-1` means no fixed seed, as in the study. |

### Run prompt expansion from the command line

You can run the same request without Unity. It uses the same system prompt, temperature and JSON output. You only need Python, with no extra packages.

```bash
cd server
python log_times.py llm --theme forest --repeats 3 --save-json forest_prompts.jsonl
```

Expected output:

```text
LLM: llama3.1:latest (llama 8.0B, Q4_K_M) at http://localhost:11434; temperature 0.4

[forest] llm run 1/3
  setting: ...
  visualDescription: ...
  audioDescription: ...
  logged  forest               llm             ... s  (ok)
```

| Option | Effect |
|---|---|
| `--theme` | Theme word, as you would type it in Unity (required) |
| `--repeats` | Number of runs |
| `--seed` | Fixes the sampling seed |
| `--temperature` | Sampling temperature (default `0.4`) |
| `--model` | Model tag (default `llama3.1:latest`, or `OLLAMA_MODEL`) |
| `--ollama-url` | Ollama server (default `http://localhost:11434`, or `OLLAMA_HOST`) |
| `--cold` | Unloads the model first, so the time includes loading it |
| `--save-json` | Appends the generated prompts to a `.jsonl` file |

Timings are appended to `server/timings.csv`. Set `TIMINGS_CSV` to write them elsewhere, and run `python log_times.py summary` to see the totals.

#### Timing and repeatability

- **Timing:** the first run after Ollama starts is slower because the model has to load. The paper's 5.4–5.9 s was measured with the model already loaded.
- **Repeatability:** without a seed, the wording changes on every run. A fixed seed helps, but the output can still differ across Ollama versions and hardware.

### Generate prompts and props from the command line

These two scripts run the theme pipeline's first steps without Unity. They need only Python, and they use the same prompts and model choice as Theme Tools.

#### Expand a theme

```bash
cd server
python expand_prompts.py --theme forest
```

This writes three files to `server/outputs/forest/`:

- `prompts.txt`, with the setting, visual prompt, audio prompt, tool and target, to read or copy.
- `llm_expansion.json`, with the same prompts and how they were made.
- `props.json`, with the suggested tool and target for `fetch_props.py`.

| Option | Effect |
|---|---|
| `--theme` | Theme word (required) |
| `--out` | Output folder (default `server/outputs/<theme>/`) |
| `--no-props` | Skips the tool and target suggestion |
| `--seed` | Fixes the sampling seed |
| `--temperature` | Prompt expansion temperature (default `0.4`) |
| `--model` | Model tag (default `llama3.1:latest`, or `OLLAMA_MODEL`) |
| `--ollama-url` | Ollama server on this machine (default `http://localhost:11434`, or `OLLAMA_HOST`) |

#### Download a tool and target

Set your Sketchfab token first. You can get one for free from *sketchfab.com → Settings → Password & API*.

```bash
export SKETCHFAB_TOKEN=<your token>       # PowerShell: $env:SKETCHFAB_TOKEN="<your token>"
cd server
python fetch_props.py --theme forest --tool axe --target log
```

This saves `tool.glb`, `target.glb` and `sketchfab.json`, which records each model's author, URL and license. To use the suggestions from `expand_prompts.py`, leave out `--tool` and `--target`.

| Option | Effect |
|---|---|
| `--theme` | Theme name, used for the output folder (required) |
| `--tool`, `--target` | Search phrase, or an exact 32-character Sketchfab model UID |
| `--out` | Output folder (default `server/outputs/<theme>/`) |
| `--search-only` | Shows the chosen models without downloading. Needs internet access, but no token. |

To play the result, copy the files into a theme folder (see [Customizing Reverie](GAME.md#add-your-own-tool-and-target)). Check each model's license before you share it.

#### Where theme files go

Each theme has one folder, and the scripts and the bake add to it:

```
server/outputs/forest/
├── prompts.txt           # expand_prompts.py
├── llm_expansion.json    # expand_prompts.py
├── props.json            # expand_prompts.py
├── tool.glb              # fetch_props.py
├── target.glb            # fetch_props.py
├── sketchfab.json        # fetch_props.py
├── panorama.jpg          # bake_theme.py
├── audio.wav             # bake_theme.py
├── meta.json             # bake_theme.py
└── point_cloud/.../point_cloud.ply               # bake_theme.py
```

To play a theme, copy its files into `client/unity/Assets/StreamingAssets/Themes/<theme>/`. Rename `point_cloud.ply` to `scene.ply`, then import it as described in [Add a 3DGS scene](GAME.md#add-a-3dgs-scene).

#### Run the script tests

```bash
cd server
pip install -r requirements.txt
python -m unittest discover tests
```

No GPU, model or network is needed. To also run the server without a GPU, see [Generation Server: Quick test](#4-quick-test-no-gpu-5-min).

### Troubleshooting

| Problem | Fix |
|---|---|
| `Is Ollama running?` or a connection error | Start the Ollama app, then check `ollama list`. |
| Model not found | Run `ollama pull llama3.1`. |
| `Reverie uses a local Llama` | Set the LLM endpoint under *Settings* to an address on your machine, such as `localhost`. |
| Editing the LLM URL in `api_config.json` has no effect | That file doesn't set it. Use *Settings* in Theme Tools. |
