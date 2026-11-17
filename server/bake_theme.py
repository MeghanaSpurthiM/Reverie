#!/usr/bin/env python3
"""
bake_theme.py: bake one Reverie theme as a fixed sequence of steps.

Each step checks what it needs, does one thing, and stops with a clear error if
something is missing. Nothing is guessed silently.

STEPS (full bake)
  1. preflight         check DreamScene360, its conda env, generator.py, PIL, AUDIO_MODE
  2. prepare-input     write data/<slug>/<slug>_PROMPT.txt (text mode)
                       or copy the panorama to data/<slug>/<slug>_panorama.png (--3dgs-only)
  3. dreamscene360     run train.py (StitchDiffusion panorama + 3DGS)
  4. collect-ply       copy output/<slug>/point_cloud/iteration_30000/point_cloud.ply
  5. export-panorama   save the panorama DreamScene360 used as panorama.jpg
  6. audio             AudioLDM 2 soundscape -> audio.wav
  7. meta              write meta.json (what is actually on disk + timings)

  --3dgs-only   trains from an existing panorama (--panorama, else outputs/<slug>/panorama.jpg|png)
                instead of generating one; audio still runs unless --no-audio
  --audio-only  steps 1, 6, 7
  --no-audio    skips step 6
  Steps whose output already exists are skipped; --overwrite redoes them.

OUTPUT (outputs/<slug>/, served by reverie_server.py)
  panorama.jpg
  point_cloud/iteration_30000/point_cloud.ply
  audio.wav
  meta.json

USAGE
  python bake_theme.py --theme "tropical beach" --prompt "<visual prompt>" --audio-prompt "<audio prompt>"
  python bake_theme.py --theme "tropical beach" --3dgs-only [--panorama path/to/pano.png]
  python bake_theme.py --theme "tropical beach" --audio-only
  python bake_theme.py --theme "tropical beach" --no-audio

ENV
  DREAMSCENE360_DIR     DreamScene360 checkout   (default: ../DreamScene360 next to server/)
  DREAMSCENE360_PYTHON  its Python interpreter   (default: looked up from the `dreamscene360` conda env)
  REVERIE_OUTPUTS       output root              (default: server/outputs, same as reverie_server.py)
  AUDIO_MODE            audioldm2 (default) | mock   (read by generator.py)
  AUDIOLDM2_MODEL_ID    AudioLDM 2 weights: Hub ID or local folder (default: cvssp/audioldm2)

Run inside the reverie-audio conda env. Typical time on an A100: DreamScene360 ~160 min
(including ~1-2 min panorama), AudioLDM 2 ~3 min.

reverie_server.py parses this script's stdout for progress. Keep the "<n>/30000" lines from
train.py, "PLY copied" and "Audio:" wording intact.
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Callable, Optional

HERE = Path(__file__).resolve().parent
DREAMSCENE360_DIR = Path(os.environ.get("DREAMSCENE360_DIR", str(HERE.parent / "DreamScene360"))).resolve()
OUTPUTS_DIR = Path(os.environ.get("REVERIE_OUTPUTS", str(HERE / "outputs"))).resolve()
DS360_ENV = "dreamscene360"
ITERATIONS = 30000
AUDIO_MODES = ("audioldm2", "mock")


class BakeError(Exception):
    """A step could not run. The message says what is missing and how to fix it."""


def log(msg: str):
    print(f"[{datetime.now():%H:%M:%S}] {msg}", flush=True)


def slugify(name: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")


# ── Bake state shared by the steps ────────────────────────────────────────────

@dataclass
class Bake:
    theme: str
    prompt: str                       # visual prompt for DreamScene360
    audio_prompt: Optional[str]
    panorama_in: Optional[Path]       # --3dgs-only: panorama to train from
    overwrite: bool
    slug: str = ""
    theme_dir: Path = Path()
    ds360_python: Optional[str] = None
    timings: dict = field(default_factory=dict)

    def __post_init__(self):
        self.slug = slugify(self.theme)
        if not self.slug:
            raise BakeError(f"--theme '{self.theme}' has no letters or digits to build a folder name from.")
        self.theme_dir = OUTPUTS_DIR / self.slug

    # DreamScene360 paths (scene name = slug, used for both data/ and output/)
    @property
    def ds_data(self) -> Path:   return DREAMSCENE360_DIR / "data" / self.slug
    @property
    def ds_output(self) -> Path: return DREAMSCENE360_DIR / "output" / self.slug
    @property
    def ds_ply(self) -> Path:    return self.ds_output / "point_cloud" / f"iteration_{ITERATIONS}" / "point_cloud.ply"

    # Theme outputs
    @property
    def ply(self) -> Path:       return self.theme_dir / "point_cloud" / f"iteration_{ITERATIONS}" / "point_cloud.ply"
    @property
    def panorama(self) -> Path:  return self.theme_dir / "panorama.jpg"
    @property
    def audio(self) -> Path:     return self.theme_dir / "audio.wav"
    @property
    def meta(self) -> Path:      return self.theme_dir / "meta.json"


# ── Steps ─────────────────────────────────────────────────────────────────────

def find_ds360_python() -> str:
    """DREAMSCENE360_PYTHON, else the `dreamscene360` conda env. Never falls back to this Python."""
    explicit = os.environ.get("DREAMSCENE360_PYTHON")
    if explicit:
        if not Path(explicit).exists():
            raise BakeError(f"DREAMSCENE360_PYTHON={explicit} does not exist.")
        return explicit

    tried = []
    conda = shutil.which("conda") or shutil.which("mamba")
    if conda:
        tried.append(f"{conda} run -n {DS360_ENV} which python")
        try:
            r = subprocess.run([conda, "run", "-n", DS360_ENV, "which", "python"],
                               capture_output=True, text=True, timeout=30)
            p = r.stdout.strip().splitlines()[-1] if r.stdout.strip() else ""
            if p and Path(p).exists():
                return p
        except (subprocess.TimeoutExpired, OSError):
            pass

    roots = [Path.home() / d for d in ("miniconda3", "anaconda3", "miniforge3", "mambaforge", "conda")]
    roots.append(Path("/opt/conda"))
    if os.environ.get("CONDA_PREFIX"):
        prefix = Path(os.environ["CONDA_PREFIX"])
        roots.append(prefix.parent.parent if prefix.parent.name == "envs" else prefix)
    for root in roots:
        p = root / "envs" / DS360_ENV / "bin" / "python"
        tried.append(str(p))
        if p.exists():
            return str(p)

    raise BakeError(f"conda env '{DS360_ENV}' not found. Create it from the DreamScene360 README, "
                    f"or set DREAMSCENE360_PYTHON. Tried:\n  " + "\n  ".join(tried))


def step_preflight(b: Bake, needs_3dgs: bool, needs_pil: bool, needs_audio: bool):
    problems = []
    if needs_3dgs:
        if not (DREAMSCENE360_DIR / "train.py").exists():
            problems.append(f"DreamScene360 not found: {DREAMSCENE360_DIR / 'train.py'} is missing. "
                            "Clone it there or set DREAMSCENE360_DIR.")
        try:
            b.ds360_python = find_ds360_python()
            log(f"DreamScene360 Python: {b.ds360_python}")
        except BakeError as e:
            problems.append(str(e))
    if needs_3dgs or needs_pil:
        try:
            import PIL  # noqa: F401  (panorama conversion)
        except ImportError:
            problems.append("Pillow is not installed in this env (pip install Pillow).")
    if needs_audio:
        if not (HERE / "generator.py").exists():
            problems.append(f"{HERE / 'generator.py'} is missing.")
        mode = os.environ.get("AUDIO_MODE", "audioldm2")
        if mode not in AUDIO_MODES:
            problems.append(f"AUDIO_MODE={mode} is not one of {', '.join(AUDIO_MODES)}.")
        else:
            log(f"Audio mode: {mode}")
        # Import now, so a broken audio env fails here and not after ~160 min of 3DGS training.
        sys.path.insert(0, str(HERE))
        try:
            import generator
            generator.check_environment()
        except Exception as e:
            problems.append(f"generator.py failed to import ({e.__class__.__name__}: {e}). "
                            "Run inside the reverie-audio conda env (server/environment.yml).")
    if problems:
        raise BakeError("Preflight failed:\n  - " + "\n  - ".join(problems))
    b.theme_dir.mkdir(parents=True, exist_ok=True)
    log(f"Output folder: {b.theme_dir}")


def step_prepare_input(b: Bake):
    """Give DreamScene360 exactly one input: a prompt file (text mode) or a panorama (pano mode)."""
    b.ds_data.mkdir(parents=True, exist_ok=True)
    # Leftovers from an earlier run would make train.py reuse stale cameras/images.
    for leftover in (b.ds_data / "images", b.ds_data / "sparse"):
        if leftover.exists():
            shutil.rmtree(leftover)
            log(f"Removed leftover {leftover.relative_to(DREAMSCENE360_DIR)}/")

    if b.panorama_in is None:
        # Text mode: a .png in data/<slug>/ would switch train.py to pano mode, so remove any.
        for png in b.ds_data.glob("*.png"):
            png.unlink()
            log(f"Removed {png.name} (a .png here would switch DreamScene360 to panorama mode)")
        prompt_file = b.ds_data / f"{b.slug}_PROMPT.txt"
        prompt_file.write_text(b.prompt, encoding="utf-8")
        log(f"Text mode: wrote {prompt_file.relative_to(DREAMSCENE360_DIR)}")
    else:
        # Panorama mode: drop a prompt file from an earlier text-mode run so there is one input.
        for prompt_file in b.ds_data.glob("*_PROMPT.txt"):
            prompt_file.unlink()
            log(f"Removed {prompt_file.name} (panorama mode uses the panorama, not a prompt)")
        # An earlier text-mode run leaves its generated diffusion_img.png here; train.py could pick it instead.
        for png in b.ds_data.glob("*.png"):
            png.unlink()
            log(f"Removed {png.name} (DreamScene360 must find only the panorama to train from)")
        from PIL import Image
        # DreamScene360 trains from the first .png in data/<slug>/ (any name), so this must be the only one.
        target = b.ds_data / f"{b.slug}_panorama.png"
        Image.open(b.panorama_in).convert("RGB").save(target, "PNG")
        log(f"Panorama mode: {b.panorama_in} -> {target.relative_to(DREAMSCENE360_DIR)}")


def step_dreamscene360(b: Bake):
    cmd = [b.ds360_python, str(DREAMSCENE360_DIR / "train.py"),
           "-s", str(b.ds_data), "-m", str(b.ds_output),
           "--iterations", str(ITERATIONS), "--save_iterations", str(ITERATIONS),
           "--test_iterations", str(ITERATIONS), "--ip", "127.0.0.1", "--port", "0"]
    # The no-GPU stand-in returns at once, so only promise the real runtime for the real thing.
    expect = "" if "no_gpu" in DREAMSCENE360_DIR.name else " (~150 min on an A100)"
    log(f"Running DreamScene360{expect}: {' '.join(cmd)}")
    t0 = time.time()
    result = subprocess.run(cmd, cwd=str(DREAMSCENE360_DIR), check=False,
                            env={**os.environ, "CONDA_DEFAULT_ENV": DS360_ENV})
    log(f"DreamScene360 finished in {(time.time() - t0) / 60:.1f} min (exit {result.returncode})")
    if result.returncode != 0:
        raise BakeError(f"DreamScene360 exited with {result.returncode}. See its output above.")


def step_collect_ply(b: Bake):
    if not b.ds_ply.exists():
        found = sorted(str(p.relative_to(b.ds_output)) for p in b.ds_output.rglob("*.ply")) \
            if b.ds_output.exists() else []
        raise BakeError(f"Expected {b.ds_ply} after {ITERATIONS} iterations, but it is missing. "
                        f".ply files under {b.ds_output}: {found or 'none'}")
    b.ply.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(b.ds_ply, b.ply)
    log(f"PLY copied: {b.ply} ({b.ply.stat().st_size // 1_048_576} MB)")


# Files DreamScene360 may leave in data/<slug>/, in order of preference.
def ds360_panorama_candidates(b: Bake) -> list:
    return [b.ds_data / f"{b.slug}_panorama.png",
            b.ds_data / f"{b.slug}_PANORAMA.png",
            b.ds_data / "image.png",
            b.ds_data / "diffusion_img.png"]


def step_export_panorama(b: Bake):
    from PIL import Image
    candidates = ds360_panorama_candidates(b)
    src = next((p for p in candidates if p.exists()), None)
    if src is None:
        # Not fatal: the 3DGS is usable without it, but Unity's skybox layer will be empty.
        log("WARNING: no panorama found to export (panorama layer will 404). Looked for:\n  "
            + "\n  ".join(str(p) for p in candidates))
        return
    Image.open(src).convert("RGB").save(b.panorama, "JPEG", quality=92)
    log(f"Panorama exported from {src.name}: {b.panorama}")


def step_audio(b: Bake):
    if b.audio_prompt:
        prompt = b.audio_prompt
        log(f"Audio prompt (given): {prompt}")
    else:
        prompt = (f"ambient soundscape, {b.prompt}, immersive 360 environment, no music, "
                  f"natural sounds, loopable, high quality")
        log(f"Audio prompt (built from visual prompt, no --audio-prompt given): {prompt}")

    from generator import EnvironmentGenerator  # reads AUDIO_MODE; path set in preflight
    gen = EnvironmentGenerator()
    gen._load_audio_pipeline()
    gen._generate_audio(prompt=prompt, output_path=b.audio,
                        progress_callback=lambda p: log(f"  Audio: {p:.0%}"))
    if not b.audio.exists():
        raise BakeError(f"Audio generation returned but {b.audio} was not written.")
    log(f"Audio saved: {b.audio} ({b.audio.stat().st_size // 1024} KB)")


def step_meta(b: Bake, started: float):
    def present(p: Path) -> Optional[str]:
        return p.name if p.exists() else None
    meta = {
        "slug": b.slug,
        "prompt": b.prompt,
        "audio_prompt": b.audio_prompt,
        "baked_at": datetime.now().isoformat(),
        "pano_path": present(b.panorama),
        "ply_path": present(b.ply),
        "wav_path": present(b.audio),
        "step_seconds": b.timings,
        "elapsed_min": round((time.time() - started) / 60, 1),
    }
    b.meta.write_text(json.dumps(meta, indent=2), encoding="utf-8")
    log(f"Meta written: {b.meta}")


# ── Runner ────────────────────────────────────────────────────────────────────

@dataclass
class Step:
    name: str
    run: Callable[[], None]
    output: Optional[Path] = None     # if this exists (and not --overwrite) the step is skipped


def plan(b: Bake, mode: str, with_audio: bool, started: float) -> list:
    """The ordered steps for this run. mode: full | 3dgs | audio."""
    do_3dgs = mode in ("full", "3dgs")
    do_audio = mode == "audio" or with_audio
    # Preflight only demands tools for steps that will actually run (not skipped as done).
    will_train = do_3dgs and (b.overwrite or not b.ply.exists())
    will_export = do_3dgs and (b.overwrite or not b.panorama.exists())
    will_audio = do_audio and (b.overwrite or not b.audio.exists())
    steps = [Step("preflight", lambda: step_preflight(b, will_train, will_export, will_audio))]
    if do_3dgs:
        steps += [
            Step("prepare-input",   lambda: step_prepare_input(b),  output=b.ply),
            Step("dreamscene360",   lambda: step_dreamscene360(b),  output=b.ply),
            Step("collect-ply",     lambda: step_collect_ply(b),    output=b.ply),
            Step("export-panorama", lambda: step_export_panorama(b), output=b.panorama),
        ]
    if do_audio:
        steps.append(Step("audio", lambda: step_audio(b), output=b.audio))
    steps.append(Step("meta", lambda: step_meta(b, started)))
    return steps


def resolve_input_panorama(b: Bake, given: Optional[str]) -> Path:
    if given:
        p = Path(given).expanduser().resolve()
        if not p.exists():
            raise BakeError(f"--panorama {p} does not exist.")
        return p
    for p in (b.theme_dir / "panorama.jpg", b.theme_dir / "panorama.png"):
        if p.exists():
            return p
    raise BakeError(f"--3dgs-only needs a panorama: pass --panorama PATH, or put panorama.jpg/.png in {b.theme_dir}.")


def main() -> int:
    ap = argparse.ArgumentParser(description="Bake one theme: panorama + 3DGS (DreamScene360) + audio (AudioLDM 2).",
                                 formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__)
    ap.add_argument("--theme", required=True, help="theme name; its slug names the output folder")
    ap.add_argument("--prompt", help="visual prompt for DreamScene360 (default: --theme)")
    ap.add_argument("--audio-prompt", help="audio prompt (default: built from the visual prompt)")
    mode = ap.add_mutually_exclusive_group()
    mode.add_argument("--audio-only", action="store_true", help="steps 1, 6, 7 only")
    mode.add_argument("--3dgs-only", dest="dgs_only", action="store_true",
                      help="train 3DGS from an existing panorama instead of generating one")
    ap.add_argument("--panorama", help="--3dgs-only: panorama to train from")
    ap.add_argument("--no-audio", action="store_true", help="skip step 6")
    ap.add_argument("--overwrite", action="store_true", help="redo steps whose output already exists")
    ap.add_argument("--list-steps", action="store_true", help="print the steps this run would execute and exit")
    # Accepted for old command lines; they never had any effect.
    ap.add_argument("--api-key", help=argparse.SUPPRESS)
    ap.add_argument("--audio-negative-prompt", help=argparse.SUPPRESS)
    a = ap.parse_args()

    if a.api_key or a.audio_negative_prompt:
        log("NOTE: --api-key and --audio-negative-prompt are ignored (the negative prompt is fixed in generator.py).")
    if a.panorama and not a.dgs_only:
        ap.error("--panorama only applies with --3dgs-only")
    if a.audio_only and a.no_audio:
        ap.error("--audio-only and --no-audio contradict each other")

    started = time.time()
    try:
        b = Bake(theme=a.theme, prompt=(a.prompt or a.theme).strip(), audio_prompt=a.audio_prompt,
                 panorama_in=None, overwrite=a.overwrite)
        run_mode = "audio" if a.audio_only else "3dgs" if a.dgs_only else "full"
        if a.dgs_only:
            b.panorama_in = resolve_input_panorama(b, a.panorama)
        steps = plan(b, run_mode, with_audio=not a.no_audio, started=started)

        log(f"=== Baking '{a.theme}' -> {b.theme_dir} ({run_mode}) ===")
        for i, s in enumerate(steps, 1):
            log(f"  {i}. {s.name}")
        if a.list_steps:
            return 0

        # Once the final .ply exists, prepare/train/collect are all done: skip them together.
        for i, s in enumerate(steps, 1):
            tag = f"[step {i}/{len(steps)}] {s.name}"
            if s.output is not None and s.output.exists() and not b.overwrite:
                log(f"{tag}: skipped, {s.output.name} already exists (use --overwrite to redo)")
                continue
            log(f"{tag}")
            t0 = time.time()
            s.run()
            b.timings[s.name] = round(time.time() - t0, 1)
    except BakeError as e:
        log(f"FAILED: {e}")
        return 1

    log(f"=== Done: {b.slug} in {(time.time() - started) / 60:.1f} min ===")
    for label, p in (("Panorama", b.panorama), ("PLY", b.ply), ("Audio", b.audio), ("Meta", b.meta)):
        log(f"    {label:9}{p if p.exists() else '(missing) ' + str(p)}")
    log(f"Served at GET /themes/{b.slug}/panorama | /ply | /audio | /meta")
    return 0


if __name__ == "__main__":
    sys.exit(main())
