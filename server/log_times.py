#!/usr/bin/env python3
"""
log_times.py — measure and log Reverie generation times, one row per stage per run.

Put this file next to bake_theme.py on the server and run it from that directory.
It runs the SAME route used for the study themes (bake_theme.py, DreamScene360
text-to-3D mode), times every stage, and appends the results to timings.csv.

STAGES LOGGED
  panorama   DreamScene360 panorama generation (start of train.py -> panorama file appears)
  3dgs       Gaussian training only (panorama file appears -> train.py exits)
  pano+3dgs  the whole DreamScene360 run (what the timing table reports today)
  audio      AudioLDM 2 soundscape (bake_theme.py --audio-only)
  objects    Sketchfab search + download for the tool and target (optional; needs a token)
  llm        LLM prompt expansion with the local Llama model served by Ollama
             (`llm` command; run it on the laptop, where Ollama and Unity run)

Every stage is wall-clock time and includes model loading, because each stage starts
a fresh process, exactly as in the study pipeline. That is recorded in the CSV.

GPU
  Before any bake, a preflight check confirms that nvidia-smi sees a GPU and that
  PyTorch can use CUDA in BOTH Python environments the pipeline uses: the one running
  this script (AudioLDM 2 via generator.py, which silently falls back to CPU if CUDA
  is missing) and the `dreamscene360` conda env (train.py). The bake stops if either
  check fails, unless --allow-cpu is given. During every stage, GPU utilisation and
  memory are sampled every 2 s; the peak values and a gpu_used flag go into the CSV.
  Use --gpu-id to pin all stages to one GPU (sets CUDA_VISIBLE_DEVICES).

USAGE
  # 1. Time a full bake (fresh output folder each run, so nothing is skipped)
  python log_times.py bake --theme forest \
      --prompt "lush enchanted forest, ..." --audio-prompt "ambient forest soundscape, ..." \
      --tool "axe" --target "log"            # --tool/--target optional, need SKETCHFAB_TOKEN

  # Repeat for each theme, ideally 3+ runs per theme
  python log_times.py bake --theme beach --prompt "..." --audio-prompt "..." --repeats 3

  # 2. Time LLM prompt expansion (on the laptop; Ollama must be running, model pulled:
  #    `ollama pull llama3.1`). Same system prompt as Unity's Theme Tools panel,
  #    read from llm_system_prompt.txt next to this script.
  python log_times.py llm --theme forest --repeats 3            # model already in memory
  python log_times.py llm --theme forest --repeats 3 --cold     # unload first: includes model load

  # Add a time you measured elsewhere
  python log_times.py add --theme forest --stage objects --seconds 21.0 --note "by hand"

  # 3. Import times already on disk (runtimes.json / meta.json from earlier bakes)
  python log_times.py import --outputs outputs

  # 4. Summary for the paper: n runs, mean, min, max per stage (+ LaTeX rows)
  python log_times.py summary

  # Dry run without a GPU (fake stages) to check the script works
  python log_times.py bake --theme test --dry-run

ENV
  DREAMSCENE360_DIR   path to the DreamScene360 repo (same as bake_theme.py)
  SKETCHFAB_TOKEN     Sketchfab API token, only for the optional objects stage
  OLLAMA_HOST         Ollama server for the llm stage (default http://localhost:11434)
  OLLAMA_MODEL        model tag for the llm stage (default llama3.1:latest = Llama 3.1 8B, Q4_K_M)
  TIMINGS_CSV         log file (default: timings.csv next to this script)
"""

import argparse
import csv
import json
import os
import platform
import re
import socket
import statistics
import subprocess
import sys
import threading
import time
import uuid
from datetime import datetime
from pathlib import Path

HERE = Path(__file__).resolve().parent
CSV_PATH = Path(os.environ.get("TIMINGS_CSV", str(HERE / "timings.csv")))
DS360_DIR = Path(os.environ.get("DREAMSCENE360_DIR", str(HERE.parent / "DreamScene360")))
FIELDS = ["run_id", "logged_at", "theme", "stage", "seconds", "minutes", "status",
          "iterations", "includes_model_load", "host", "gpu", "gpu_used",
          "gpu_peak_util_pct", "gpu_peak_mem_mb", "source", "note"]
STAGE_ORDER = ["llm", "panorama", "3dgs", "pano+3dgs", "audio", "objects"]
PANO_NAMES = ["image.png", "diffusion_img.png"]  # plus *_panorama.png / *_PANORAMA.png


# ── helpers ───────────────────────────────────────────────────────────────────

def slug(name: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")


GPU_INDEX = None   # set from --gpu-id


def nvsmi(query: str):
    """Rows of nvidia-smi output for the selected GPU (or all GPUs)."""
    cmd = ["nvidia-smi", f"--query-gpu={query}", "--format=csv,noheader,nounits"]
    if GPU_INDEX is not None:
        cmd += ["-i", str(GPU_INDEX)]
    out = subprocess.run(cmd, capture_output=True, text=True, timeout=10)
    if out.returncode != 0:
        raise RuntimeError(out.stderr.strip() or "nvidia-smi failed")
    return [[c.strip() for c in line.split(",")] for line in out.stdout.strip().splitlines()]


def gpu_name() -> str:
    try:
        return nvsmi("name")[0][0]
    except Exception:
        return "unknown"


def conda_python(env_name: str):
    """Python of a conda env (same lookup idea as bake_theme.py)."""
    import shutil
    exe = shutil.which("conda") or shutil.which("mamba")
    if exe:
        try:
            r = subprocess.run([exe, "run", "-n", env_name, "which", "python"],
                               capture_output=True, text=True, timeout=30)
            p = r.stdout.strip().splitlines()[-1] if r.stdout.strip() else ""
            if p and Path(p).exists():
                return p
        except Exception:
            pass
    for base in ("miniconda3", "anaconda3", "conda"):
        p = Path.home() / base / "envs" / env_name / "bin" / "python"
        if p.exists():
            return str(p)
    p = Path("/opt/conda/envs") / env_name / "bin" / "python"
    return str(p) if p.exists() else None


def torch_cuda_check(python: str, env: dict) -> str:
    code = ("import torch,sys; ok=torch.cuda.is_available(); "
            "print('OK '+torch.cuda.get_device_name(0)+' torch '+torch.__version__ "
            "if ok else 'NO CUDA torch '+torch.__version__); sys.exit(0 if ok else 1)")
    r = subprocess.run([python, "-c", code], capture_output=True, text=True, timeout=180, env=env)
    err = r.stderr.strip().splitlines()
    msg = r.stdout.strip() or (err[-1] if err else "no output")
    if r.returncode != 0:
        raise RuntimeError(f"{python}: {msg}")
    return msg


def gpu_preflight(env: dict):
    """Stop unless the GPU is visible and PyTorch can use CUDA in both pipeline envs."""
    print("GPU preflight")
    rows = nvsmi("index,name,memory.total,memory.used,utilization.gpu")
    for r in rows:
        print(f"  nvidia-smi: GPU {r[0]} {r[1]}, {r[3]}/{r[2]} MiB used, {r[4]}% busy")
    print("  audio env (" + sys.executable + "): " + torch_cuda_check(sys.executable, env))
    ds_py = conda_python("dreamscene360")
    if ds_py is None:
        raise RuntimeError("conda env 'dreamscene360' not found; bake_theme.py would fall "
                           "back to the current Python for train.py")
    print("  dreamscene360 env (" + ds_py + "): " + torch_cuda_check(ds_py, env))


class GPUMonitor:
    """Samples GPU utilisation and memory every `every` seconds while a stage runs."""
    def __init__(self, every=2.0):
        self.every, self.util, self.mem = every, [], []
        self.stop = threading.Event()
        self.base_mem = None
        self.t = threading.Thread(target=self._run, daemon=True)

    def _run(self):
        while not self.stop.is_set():
            try:
                rows = nvsmi("utilization.gpu,memory.used")
                self.util.append(max(float(r[0]) for r in rows))
                self.mem.append(max(float(r[1]) for r in rows))
            except Exception:
                pass
            self.stop.wait(self.every)

    def __enter__(self):
        try:
            self.base_mem = max(float(r[0]) for r in nvsmi("memory.used"))
        except Exception:
            self.base_mem = None
        self.t.start()
        return self

    def __exit__(self, *exc):
        self.stop.set()
        self.t.join(timeout=5)

    def fields(self):
        if not self.util:
            return dict(gpu_used="unknown", gpu_peak_util_pct="", gpu_peak_mem_mb="")
        peak_u, peak_m = max(self.util), max(self.mem)
        grew = self.base_mem is not None and peak_m - self.base_mem > 500
        used = "yes" if (peak_u >= 20 or grew) else "NO"
        return dict(gpu_used=used, gpu_peak_util_pct=round(peak_u), gpu_peak_mem_mb=round(peak_m))


def log_row(**kw):
    new = not CSV_PATH.exists()
    row = {f: kw.get(f, "") for f in FIELDS}
    if row["seconds"] != "":
        row["seconds"] = round(float(row["seconds"]), 1)
        row["minutes"] = round(float(row["seconds"]) / 60, 2)
    row["logged_at"] = datetime.now().isoformat(timespec="seconds")
    row.setdefault("host", socket.gethostname())
    with open(CSV_PATH, "a", newline="") as f:
        w = csv.DictWriter(f, fieldnames=FIELDS)
        if new:
            w.writeheader()
        w.writerow(row)
    print(f"  logged  {row['theme']:<20} {row['stage']:<10} {row['seconds']:>9} s  ({row['status']})")


def watch_for_panorama(data_dir: Path, t0: float, found: dict, stop: threading.Event):
    """Poll for the panorama DreamScene360 writes; record the first time one appears."""
    while not stop.is_set():
        cands = []
        for d in (data_dir, DS360_DIR):
            if d.exists():
                cands += [d / n for n in PANO_NAMES]
                cands += list(d.glob("*_panorama.png")) + list(d.glob("*_PANORAMA.png"))
        for p in cands:
            try:
                if p.exists() and p.stat().st_mtime >= t0:
                    found["t"] = time.time()
                    found["path"] = str(p)
                    return
            except FileNotFoundError:
                pass
        time.sleep(2)


def run_streamed(cmd, cwd, env=None):
    """Run a command, echo its output, return the exit code."""
    proc = subprocess.Popen(cmd, cwd=str(cwd), env=env, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True, bufsize=1)
    for line in proc.stdout:
        print("    | " + line.rstrip())
    return proc.wait()


# ── stages ────────────────────────────────────────────────────────────────────

def time_pano_3dgs(run_id, theme, run_theme, prompt, base):
    """bake_theme.py --no-audio: DreamScene360 text mode (panorama + Gaussians)."""
    data_dir = DS360_DIR / "data" / slug(run_theme)
    found, stop = {}, threading.Event()
    t0 = time.time()
    watcher = threading.Thread(target=watch_for_panorama, args=(data_dir, t0, found, stop),
                               daemon=True)
    watcher.start()
    with GPUMonitor() as mon:
        code = run_streamed([sys.executable, str(HERE / "bake_theme.py"), "--theme", run_theme,
                             "--prompt", prompt, "--no-audio"], HERE, env=base_env())
    t_end = time.time()
    stop.set()
    status = "ok" if code == 0 else f"failed (exit {code})"
    g = mon.fields()
    log_row(**base, **g, stage="pano+3dgs", seconds=t_end - t0, status=status, iterations=30000)
    if "t" in found:
        # split the GPU samples at the moment the panorama appeared
        n_pano = max(1, int((found["t"] - t0) / mon.every))
        def part(u, m):
            if not u:
                return dict(gpu_used="unknown", gpu_peak_util_pct="", gpu_peak_mem_mb="")
            return dict(gpu_used="yes" if max(u) >= 20 else "NO",
                        gpu_peak_util_pct=round(max(u)), gpu_peak_mem_mb=round(max(m)))
        log_row(**base, **part(mon.util[:n_pano], mon.mem[:n_pano]), stage="panorama",
                seconds=found["t"] - t0, status=status,
                note=f"panorama file: {Path(found['path']).name}")
        log_row(**base, **part(mon.util[n_pano:], mon.mem[n_pano:]), stage="3dgs",
                seconds=t_end - found["t"], status=status, iterations=30000)
    else:
        print("  ! panorama file was not detected, so panorama and 3dgs could not be split;"
              " only pano+3dgs was logged")


def time_audio(run_id, theme, run_theme, prompt, audio_prompt, base):
    cmd = [sys.executable, str(HERE / "bake_theme.py"), "--theme", run_theme,
           "--prompt", prompt, "--audio-only"]
    if audio_prompt:
        cmd += ["--audio-prompt", audio_prompt]
    t0 = time.time()
    with GPUMonitor() as mon:
        code = run_streamed(cmd, HERE, env=base_env())
    log_row(**base, **mon.fields(), stage="audio", seconds=time.time() - t0,
            status="ok" if code == 0 else f"failed (exit {code})",
            note="AudioLDM 2, 10 s clip, 200 steps")


def time_objects(tool, target, base):
    """Sketchfab search + download-link request for the tool and target."""
    token = os.environ.get("SKETCHFAB_TOKEN", "")
    if not token:
        print("  ! SKETCHFAB_TOKEN not set; skipping the objects stage")
        return
    import urllib.parse
    import urllib.request

    def get(url, auth=False):
        req = urllib.request.Request(url)
        if auth:
            req.add_header("Authorization", f"Token {token}")
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read().decode())

    t0 = time.time()
    status = "ok"
    try:
        for q in (tool, target):
            res = get("https://api.sketchfab.com/v3/search?type=models&downloadable=true&q="
                      + urllib.parse.quote(q))
            uid = res["results"][0]["uid"]
            dl = get(f"https://api.sketchfab.com/v3/models/{uid}/download", auth=True)
            url = (dl.get("glb") or dl.get("gltf") or {}).get("url")
            if url:
                with urllib.request.urlopen(url, timeout=120) as r:
                    while r.read(1 << 20):
                        pass
    except Exception as e:
        status = f"failed ({e})"
    log_row(**{**base, "includes_model_load": "n/a"}, gpu_used="n/a (network)",
            stage="objects", seconds=time.time() - t0, status=status, note=f"Sketchfab search + download: {tool}, {target}")


# ── LLM prompt expansion (Ollama) ─────────────────────────────────────────────

def ollama_post(url, path, payload, timeout=300):
    import urllib.error
    import urllib.request
    req = urllib.request.Request(url.rstrip("/") + path, data=json.dumps(payload).encode(),
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        body = e.read().decode(errors="replace")
        if e.code == 404:
            raise SystemExit(f"Ollama: model not found ({body}). Run: ollama pull {payload.get('model')}")
        raise SystemExit(f"Ollama returned {e.code}: {body}")
    except urllib.error.URLError as e:
        raise SystemExit(f"Cannot reach Ollama at {url} ({e.reason}). Is `ollama serve` running?")


def cmd_llm(a):
    """Time prompt expansion with the same system prompt and temperature as Unity's Theme Tools (Ollama /api/chat)."""
    if "://" not in a.ollama_url:          # OLLAMA_HOST is often set as host:port
        a.ollama_url = "http://" + a.ollama_url
    prompt_file = Path(a.system_prompt)
    if not prompt_file.exists():
        sys.exit(f"System prompt not found: {prompt_file}\n"
                 "It ships as server/llm_system_prompt.txt (identical to the prompt Theme Tools sends); "
                 "copy it next to this script or pass --system-prompt.")
    system = prompt_file.read_text(encoding="utf-8")

    info = ollama_post(a.ollama_url, "/api/show", {"model": a.model}, timeout=30)
    d = info.get("details", {})
    model_desc = f"{a.model} ({d.get('family', '?')} {d.get('parameter_size', '?')}, " \
                 f"{d.get('quantization_level', '?')})"
    print(f"LLM: {model_desc} at {a.ollama_url}; temperature {a.temperature}"
          + (f", seed {a.seed}" if a.seed is not None else ""))

    options = {"temperature": a.temperature}
    if a.seed is not None:
        options["seed"] = a.seed
    gpu = gpu_name()
    run_id = datetime.now().strftime("%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:4]
    for i in range(a.repeats):
        if a.cold:   # unload so this run includes loading the model, like a fresh start
            ollama_post(a.ollama_url, "/api/generate", {"model": a.model, "keep_alive": 0}, timeout=60)
            time.sleep(1)
        print(f"\n[{a.theme}] llm run {i + 1}/{a.repeats}{' (cold)' if a.cold else ''}")
        t0 = time.time()
        r = ollama_post(a.ollama_url, "/api/chat", {
            "model": a.model, "stream": False, "format": "json", "options": options,
            "messages": [{"role": "system", "content": system},
                         {"role": "user", "content": a.theme}]})
        wall = time.time() - t0
        load = r.get("load_duration", 0) / 1e9
        status = "ok"
        try:
            out = json.loads(r["message"]["content"])
            missing = [k for k in ("setting", "visualDescription", "audioDescription") if not out.get(k)]
            if missing:
                status = f"incomplete ({', '.join(missing)} missing)"
        except Exception as e:
            out, status = {}, f"failed (bad JSON: {e})"
        for k in ("setting", "visualDescription", "audioDescription"):
            if out.get(k):
                print(f"  {k}: {out[k]}")
        log_row(run_id=f"{run_id}-{i + 1}", theme=a.theme, stage="llm", seconds=wall, status=status,
                includes_model_load="yes" if load >= 1.0 else "no", host=socket.gethostname(),
                gpu=gpu, gpu_used="n/a (Ollama)", source="measured",
                note=f"{model_desc}; temp {a.temperature}; Ollama total "
                     f"{r.get('total_duration', 0) / 1e9:.2f} s, load {load:.2f} s, "
                     f"{r.get('eval_count', '?')} output tokens")
        if a.save_json:
            path = Path(a.save_json)
            rec = {"theme": a.theme, "model": a.model, "details": d, "temperature": a.temperature,
                   "seed": a.seed, "run": i + 1, "wall_clock_s": round(wall, 2),
                   "model_load_s": round(load, 2), "output": out}
            with open(path, "a", encoding="utf-8") as f:
                f.write(json.dumps(rec, ensure_ascii=False) + "\n")


# ── commands ──────────────────────────────────────────────────────────────────

def base_env():
    env = dict(os.environ)
    if GPU_INDEX is not None:
        env["CUDA_VISIBLE_DEVICES"] = str(GPU_INDEX)
    env.setdefault("AUDIO_MODE", "audioldm2")   # never time the mock audio path
    if env.get("AUDIO_MODE") == "mock":
        raise SystemExit("AUDIO_MODE=mock would time a silent placeholder, not AudioLDM 2")
    return env


def cmd_bake(a):
    global GPU_INDEX
    GPU_INDEX = a.gpu_id
    if not a.dry_run:
        try:
            gpu_preflight(base_env())
        except Exception as e:
            if a.allow_cpu:
                print(f"  ! GPU check failed but --allow-cpu was given: {e}")
            else:
                sys.exit(f"GPU check failed, nothing was baked: {e}\n"
                         "Fix CUDA (or pass --allow-cpu to time a CPU run on purpose).")
    gpu = gpu_name()
    for rep in range(a.repeats):
        run_id = datetime.now().strftime("%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:4]
        # a fresh folder per run so bake_theme.py never skips a stage
        run_theme = f"{a.theme}-timing-{run_id}"
        prompt = a.prompt or a.theme
        base = dict(run_id=run_id, theme=a.theme, includes_model_load="yes",
                    host=socket.gethostname(), gpu=gpu, source="measured")
        print(f"\n=== {a.theme}: run {rep + 1} of {a.repeats} (run_id {run_id}) ===")
        if a.dry_run:
            for st, s in [("panorama", 1.0), ("3dgs", 2.0), ("pano+3dgs", 3.0), ("audio", 0.5)]:
                time.sleep(0.1)
                log_row(**{**base, "source": "dry-run"}, stage=st, seconds=s, status="ok",
                        note="dry run, not a real measurement")
            continue
        if not a.skip_visual:
            time_pano_3dgs(run_id, a.theme, run_theme, prompt, base)
        if not a.skip_audio:
            time_audio(run_id, a.theme, run_theme, prompt, a.audio_prompt, base)
        if a.tool and a.target:
            time_objects(a.tool, a.target, base)


def cmd_add(a):
    log_row(run_id="manual-" + uuid.uuid4().hex[:6], theme=a.theme, stage=a.stage,
            seconds=a.seconds, status="ok", includes_model_load=a.model_load,
            host=socket.gethostname(), gpu=a.gpu or "", source="manual", note=a.note or "")


def cmd_import(a):
    """Pull timings already on disk: runtimes.json (ds360/audio servers) and meta.json."""
    keymap = {"after_img": "panorama", "after_3dgs": "3dgs", "after_30k": "3dgs",
              "after_audio": "audio"}
    n = 0
    for d in sorted(Path(a.outputs).iterdir()):
        if not d.is_dir():
            continue
        rt = d / "runtimes.json"
        if rt.exists():
            for k, v in json.loads(rt.read_text()).items():
                if k in keymap:
                    log_row(run_id=f"import-{d.name}", theme=d.name, stage=keymap[k], seconds=v,
                            status="ok", includes_model_load="unknown", source=f"import:{rt.name}",
                            note=f"checkpoint {k}")
                    n += 1
        meta = d / "meta.json"
        if meta.exists():
            m = json.loads(meta.read_text())
            if m.get("elapsed_min") is not None:
                log_row(run_id=f"import-{d.name}", theme=d.name, stage="bake-total",
                        seconds=float(m["elapsed_min"]) * 60, status="ok",
                        iterations=m.get("iterations", ""), includes_model_load="yes",
                        source="import:meta.json", note=f"baked_at {m.get('baked_at', '')}")
                n += 1
    print(f"Imported {n} rows from {a.outputs}")


def fmt(sec):
    # Table 1 reports sub-minute stages to a tenth of a second, so keep that precision.
    return f"{sec:.1f} s" if sec < 90 else f"{sec / 60:.1f} min"


def cmd_summary(a):
    if not CSV_PATH.exists():
        sys.exit(f"No log yet at {CSV_PATH}")
    rows = list(csv.DictReader(open(CSV_PATH)))
    rows = [r for r in rows if r["status"] == "ok" and r["seconds"] and
            (a.include_dry_run or r["source"] != "dry-run")]
    cpu = [r for r in rows if r.get("gpu_used") == "NO"]
    if cpu:
        print(f"Excluding {len(cpu)} row(s) where the GPU was NOT used "
              "(see gpu_used column):")
        for r in cpu:
            print(f"  {r['run_id']} {r['theme']} {r['stage']} {r['seconds']} s")
        rows = [r for r in rows if r.get("gpu_used") != "NO"]
    by = {}
    for r in rows:
        by.setdefault(r["stage"], []).append(float(r["seconds"]))
    stages = [s for s in STAGE_ORDER if s in by] + sorted(set(by) - set(STAGE_ORDER))
    print(f"\nTimings from {CSV_PATH} (successful runs only)\n")
    print(f"{'stage':<12}{'runs':>5}{'mean':>12}{'min':>12}{'max':>12}{'SD':>10}")
    for s in stages:
        v = by[s]
        sd = statistics.stdev(v) if len(v) > 1 else 0.0
        print(f"{s:<12}{len(v):>5}{fmt(statistics.mean(v)):>12}{fmt(min(v)):>12}"
              f"{fmt(max(v)):>12}{fmt(sd):>10}")
    print("\nLaTeX rows (time column as min--max, with mean):")
    for s in stages:
        v = by[s]
        print(f"  {s:<10} & {fmt(min(v))}--{fmt(max(v))} (mean {fmt(statistics.mean(v))}, n={len(v)}) \\\\")
    themes = sorted({r["theme"] for r in rows})
    print(f"\nThemes: {', '.join(themes)}")
    gpus = sorted({r["gpu"] for r in rows if r.get("gpu") and r["gpu"] != "unknown"})
    if gpus:
        print(f"GPU(s): {', '.join(gpus)}")
    print("All times are wall-clock and include model loading unless marked otherwise.")


def main():
    ap = argparse.ArgumentParser(description="Log Reverie generation times to a CSV")
    sub = ap.add_subparsers(dest="cmd", required=True)

    b = sub.add_parser("bake", help="run and time a bake")
    b.add_argument("--theme", required=True)
    b.add_argument("--prompt", default=None, help="visual prompt (defaults to the theme)")
    b.add_argument("--audio-prompt", default=None)
    b.add_argument("--tool", default=None, help="Sketchfab query for the tool (optional)")
    b.add_argument("--target", default=None, help="Sketchfab query for the target (optional)")
    b.add_argument("--repeats", type=int, default=1)
    b.add_argument("--skip-visual", action="store_true")
    b.add_argument("--skip-audio", action="store_true")
    b.add_argument("--dry-run", action="store_true", help="fake stages, no GPU needed")
    b.add_argument("--gpu-id", type=int, default=None,
                   help="pin every stage to this GPU (sets CUDA_VISIBLE_DEVICES)")
    b.add_argument("--allow-cpu", action="store_true",
                   help="continue even if the GPU check fails (not for paper timings)")
    b.set_defaults(fn=cmd_bake)

    l = sub.add_parser("llm", help="time LLM prompt expansion via a local Ollama model")
    l.add_argument("--theme", required=True, help="theme word, as typed in Unity")
    l.add_argument("--model", default=os.environ.get("OLLAMA_MODEL", "llama3.1:latest"))
    l.add_argument("--ollama-url", default=os.environ.get("OLLAMA_HOST", "http://localhost:11434"))
    l.add_argument("--repeats", type=int, default=1)
    l.add_argument("--cold", action="store_true",
                   help="unload the model before each run, so the time includes model loading")
    l.add_argument("--temperature", type=float, default=0.4, help="same as Unity (0.4)")
    l.add_argument("--seed", type=int, default=None, help="fix the sampling seed (optional)")
    l.add_argument("--system-prompt", default=str(HERE / "llm_system_prompt.txt"))
    l.add_argument("--save-json", default=None,
                   help="append each run's prompts to this .jsonl file")
    l.set_defaults(fn=cmd_llm)

    m = sub.add_parser("add", help="log a time measured elsewhere")
    m.add_argument("--theme", required=True)
    m.add_argument("--stage", required=True, help="e.g. llm, objects, panorama")
    m.add_argument("--seconds", type=float, required=True)
    m.add_argument("--model-load", default="unknown", help="yes / no / unknown")
    m.add_argument("--gpu", default=None)
    m.add_argument("--note", default=None)
    m.set_defaults(fn=cmd_add)

    i = sub.add_parser("import", help="import runtimes.json / meta.json already on disk")
    i.add_argument("--outputs", default="outputs")
    i.set_defaults(fn=cmd_import)

    s = sub.add_parser("summary", help="summarise the log for the paper")
    s.add_argument("--include-dry-run", action="store_true")
    s.set_defaults(fn=cmd_summary)

    a = ap.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()