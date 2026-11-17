#!/usr/bin/env python3
"""
reverie_server.py: HTTP server that bakes Reverie themes and serves the results.

The Unity client (Theme Tools: Editor window or F1 panel in builds) sends a theme and its LLM-expanded
prompts here; the server runs bake_theme.py (StitchDiffusion panorama -> DreamScene360
3DGS -> AudioLDM 2 audio, the route used in the paper), and the client downloads the
results into its theme folder. Bakes run one at a time, because each one needs most of
a GPU for about 160 minutes.

RUN (inside the reverie-audio conda env, from this folder; see docs/SERVER.md)
  python reverie_server.py                          # same machine as Unity: localhost:8000
  python reverie_server.py --host 0.0.0.0 --port 8000   # reachable from other machines

  Set REVERIE_TOKEN to require the header `Authorization: Bearer <token>` on every request
  except /health. Recommended whenever the server listens on anything but localhost.

ENDPOINTS
  GET  /health                     server, GPU and queue status (no token needed)
  GET  /themes                     themes on disk and which assets each has
  POST /themes/{slug}/bake         queues a bake; JSON body (all optional except as noted):
                                     prompt                 visual prompt (needed to generate a panorama)
                                     audio_prompt           audio prompt
                                     scene=true             panorama + 3DGS (DreamScene360)
                                     audio=true             AudioLDM 2 soundscape
                                     use_existing_panorama  train 3DGS from the theme's panorama
                                     overwrite              redo stages that are already done
  GET  /themes/{slug}/status       queued | running | done | failed, with stage and progress
  PUT  /themes/{slug}/panorama     upload a JPEG/PNG panorama (raw body) for use_existing_panorama
  GET  /themes/{slug}/panorama     panorama.jpg
  GET  /themes/{slug}/ply          3DGS point_cloud.ply
  GET  /themes/{slug}/audio        audio.wav
  GET  /themes/{slug}/meta         meta.json (bake provenance)

ENV
  REVERIE_TOKEN       optional access token (see above)
  DREAMSCENE360_DIR   passed through to bake_theme.py
  REVERIE_OUTPUTS     output folder (default: outputs/ next to this script)
"""
import argparse
import hmac
import json
import os
import queue
import re
import subprocess
import sys
import threading
import time
from datetime import datetime
from pathlib import Path
from typing import Optional

import uvicorn
from fastapi import Depends, FastAPI, Header, HTTPException, Request
from fastapi.responses import FileResponse
from pydantic import BaseModel

HERE = Path(__file__).resolve().parent
OUTPUTS = Path(os.environ.get("REVERIE_OUTPUTS", str(HERE / "outputs"))).resolve()
TOKEN = os.environ.get("REVERIE_TOKEN", "")
ITERATIONS = 30000

app = FastAPI(title="Reverie theme server")
jobs: dict[str, dict] = {}
jobs_lock = threading.Lock()
bake_queue: "queue.Queue[str]" = queue.Queue()


# ── Helpers ───────────────────────────────────────────────────────────────────

def slugify(name: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")


def check_token(authorization: Optional[str] = Header(default=None)):
    if TOKEN and not hmac.compare_digest(authorization or "", f"Bearer {TOKEN}"):
        raise HTTPException(401, "Missing or wrong token (Authorization: Bearer <REVERIE_TOKEN>)")


def theme_dir(slug: str) -> Path:
    if slug != slugify(slug) or not slug:
        raise HTTPException(400, f"Invalid theme slug '{slug}' (use lowercase letters, digits, hyphens)")
    return OUTPUTS / slug


def find_ply(d: Path) -> Optional[Path]:
    exact = d / "point_cloud" / f"iteration_{ITERATIONS}" / "point_cloud.ply"
    if exact.exists():
        return exact
    plys = sorted(d.rglob("*.ply"))
    return plys[-1] if plys else None


def assets(slug: str) -> dict:
    d = OUTPUTS / slug
    return {"has_panorama": (d / "panorama.jpg").exists(),
            "has_ply": find_ply(d) is not None if d.exists() else False,
            "has_audio": (d / "audio.wav").exists()}


def update(slug: str, **kw):
    with jobs_lock:
        jobs[slug].update(kw)


# ── Bake worker (one bake at a time) ──────────────────────────────────────────

def bake_command(slug: str, job: dict) -> list:
    """bake_theme.py command for a job. scene/audio/use_existing_panorama map onto its flags."""
    cmd = [sys.executable, str(HERE / "bake_theme.py"), "--theme", slug]
    if job["prompt"]:
        cmd += ["--prompt", job["prompt"]]
    if job["audio_prompt"]:
        cmd += ["--audio-prompt", job["audio_prompt"]]
    if not job["scene"]:
        cmd.append("--audio-only")
    else:
        if job["use_existing_panorama"]:
            cmd.append("--3dgs-only")
        if not job["audio"]:
            cmd.append("--no-audio")
    if job["overwrite"]:
        cmd.append("--overwrite")
    return cmd


def run_bake(slug: str):
    job = jobs[slug]
    cmd = bake_command(slug, job)
    update(slug, status="running", stage="starting", progress=0.02,
           started_at=datetime.now().isoformat(timespec="seconds"))
    log_path = OUTPUTS / slug / "bake.log"
    log_path.parent.mkdir(parents=True, exist_ok=True)
    tail: list[str] = []
    with open(log_path, "w", encoding="utf-8") as log:
        proc = subprocess.Popen(cmd, cwd=str(HERE), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, bufsize=1, env={**os.environ, "PYTHONUNBUFFERED": "1"})
        for line in proc.stdout:
            log.write(line)
            line = line.rstrip()
            tail = (tail + [line])[-15:]
            low = line.lower()
            step = re.search(r"\[step (\d+)/(\d+)\] ([\w-]+)", line)
            m = re.search(rf"(\d+)\s*/\s*{ITERATIONS}", line)
            if step:  # bake_theme.py announces each step: "[step i/n] name"
                i, n, name = int(step.group(1)), int(step.group(2)), step.group(3)
                stage = {"dreamscene360": "3dgs", "prepare-input": "3dgs", "collect-ply": "3dgs",
                         "export-panorama": "panorama"}.get(name, name)
                with jobs_lock:
                    prog = max(jobs[slug].get("progress", 0.0), round((i - 1) / n, 3))
                update(slug, stage=stage, progress=prog)
            elif m:
                update(slug, stage="3dgs", progress=round(0.05 + 0.85 * int(m.group(1)) / ITERATIONS, 3))
        proc.wait()
    if proc.returncode == 0:
        update(slug, status="done", stage="done", progress=1.0, error=None,
               finished_at=datetime.now().isoformat(timespec="seconds"), **assets(slug))
    else:
        update(slug, status="failed", error=f"bake_theme.py exited {proc.returncode}:\n" + "\n".join(tail),
               finished_at=datetime.now().isoformat(timespec="seconds"), **assets(slug))


def worker():
    while True:
        slug = bake_queue.get()
        try:
            run_bake(slug)
        except Exception as e:  # keep the worker alive
            update(slug, status="failed", error=repr(e))
        finally:
            bake_queue.task_done()


# ── API ───────────────────────────────────────────────────────────────────────

class BakeRequest(BaseModel):
    prompt: str = ""                    # visual prompt; required when generating a new panorama
    audio_prompt: str = ""
    scene: bool = True                  # panorama + 3DGS (DreamScene360)
    audio: bool = True                  # AudioLDM 2 soundscape
    use_existing_panorama: bool = False  # train 3DGS from this theme's panorama.jpg instead of a prompt
    overwrite: bool = False             # redo stages whose output already exists


@app.get("/health")
def health():
    gpu = "unknown"
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader"],
                             capture_output=True, text=True, timeout=10)
        gpu = out.stdout.strip() or out.stderr.strip()
    except Exception as e:
        gpu = f"nvidia-smi unavailable ({e.__class__.__name__})"
    with jobs_lock:
        running = [s for s, j in jobs.items() if j["status"] == "running"]
        queued = [s for s, j in jobs.items() if j["status"] == "queued"]
    return {"ok": True, "gpu": gpu, "running": running, "queued": queued,
            "outputs": str(OUTPUTS), "token_required": bool(TOKEN)}


@app.get("/themes", dependencies=[Depends(check_token)])
def list_themes():
    if not OUTPUTS.exists():
        return {"themes": []}
    return {"themes": [{"slug": d.name, **assets(d.name)} for d in sorted(OUTPUTS.iterdir()) if d.is_dir()]}


@app.post("/themes/{slug}/bake", dependencies=[Depends(check_token)])
def bake(slug: str, req: BakeRequest):
    d = theme_dir(slug)
    if not req.scene and not req.audio:
        raise HTTPException(400, "nothing to bake: scene and audio are both false")
    if req.scene and not req.use_existing_panorama and not req.prompt.strip():
        raise HTTPException(400, "prompt is empty (needed to generate the panorama and 3DGS)")
    if req.scene and req.use_existing_panorama and not ((d / "panorama.jpg").exists() or (d / "panorama.png").exists()):
        raise HTTPException(409, f"use_existing_panorama: {slug} has no panorama on the server yet; "
                                 f"bake the scene from a prompt first, or upload one with PUT /themes/{slug}/panorama")
    with jobs_lock:
        cur = jobs.get(slug)
        if cur and cur["status"] in ("queued", "running"):
            return cur
        have = assets(slug)
        wanted = ([have["has_panorama"], have["has_ply"]] if req.scene else []) + ([have["has_audio"]] if req.audio else [])
        if all(wanted) and not req.overwrite:
            return {"slug": slug, "status": "done", "stage": "done", "progress": 1.0,
                    "note": "already baked; send overwrite=true to rebake", **have}
        jobs[slug] = {"slug": slug, "status": "queued", "stage": "queued", "progress": 0.0,
                      "prompt": req.prompt.strip(), "audio_prompt": req.audio_prompt.strip(),
                      "scene": req.scene, "audio": req.audio,
                      "use_existing_panorama": req.use_existing_panorama, "overwrite": req.overwrite,
                      "queued_at": datetime.now().isoformat(timespec="seconds"), "error": None}
        jobs[slug]["queue_position"] = bake_queue.qsize() + 1
        bake_queue.put(slug)
        return jobs[slug]


@app.get("/themes/{slug}/status", dependencies=[Depends(check_token)])
def status(slug: str):
    theme_dir(slug)
    with jobs_lock:
        if slug in jobs:
            return {**jobs[slug], **assets(slug)}
    have = assets(slug)
    if not any(have.values()):
        raise HTTPException(404, f"Theme '{slug}' not found and not queued")
    done = all(have.values())
    return {"slug": slug, "status": "done" if done else "partial", "progress": 1.0 if done else 0.5, **have}


def _file(path: Optional[Path], media: str, name: str):
    if path is None or not path.exists():
        raise HTTPException(404, f"{name} not ready")
    return FileResponse(str(path), media_type=media, filename=name)


@app.get("/themes/{slug}/panorama", dependencies=[Depends(check_token)])
def panorama(slug: str):
    return _file(theme_dir(slug) / "panorama.jpg", "image/jpeg", "panorama.jpg")


@app.put("/themes/{slug}/panorama", dependencies=[Depends(check_token)])
async def upload_panorama(slug: str, request: Request):
    """Body: raw JPEG or PNG bytes. Stored as the theme's panorama for use_existing_panorama bakes."""
    d = theme_dir(slug)
    with jobs_lock:
        if jobs.get(slug, {}).get("status") in ("queued", "running"):
            raise HTTPException(409, f"{slug} is baking; upload the panorama after it finishes")
    data = await request.body()
    if data[:3] == b"\xff\xd8\xff":
        name = "panorama.jpg"
    elif data[:8] == b"\x89PNG\r\n\x1a\n":
        name = "panorama.png"
    else:
        raise HTTPException(415, "body must be a JPEG or PNG image")
    d.mkdir(parents=True, exist_ok=True)
    for old in ("panorama.jpg", "panorama.png"):
        (d / old).unlink(missing_ok=True)
    (d / name).write_bytes(data)
    if name == "panorama.png":  # served and used as panorama.jpg
        from PIL import Image
        Image.open(d / name).convert("RGB").save(d / "panorama.jpg", "JPEG", quality=92)
        (d / name).unlink()
    return {"slug": slug, "stored": "panorama.jpg", "bytes": len(data), **assets(slug)}


@app.get("/themes/{slug}/ply", dependencies=[Depends(check_token)])
def ply(slug: str):
    d = theme_dir(slug)
    return _file(find_ply(d) if d.exists() else None, "application/octet-stream", "point_cloud.ply")


@app.get("/themes/{slug}/audio", dependencies=[Depends(check_token)])
def audio(slug: str):
    return _file(theme_dir(slug) / "audio.wav", "audio/wav", "audio.wav")


@app.get("/themes/{slug}/meta", dependencies=[Depends(check_token)])
def meta(slug: str):
    return _file(theme_dir(slug) / "meta.json", "application/json", "meta.json")


def main():
    ap = argparse.ArgumentParser(description="Reverie theme server")
    ap.add_argument("--host", default="127.0.0.1",
                    help="127.0.0.1 = this machine only (default); 0.0.0.0 = reachable from the network")
    ap.add_argument("--port", type=int, default=8000)
    a = ap.parse_args()
    if a.host not in ("127.0.0.1", "localhost") and not TOKEN:
        print("WARNING: listening on the network without REVERIE_TOKEN; anyone who can reach "
              f"port {a.port} can start GPU jobs. Set REVERIE_TOKEN, or use an SSH tunnel.", flush=True)
    OUTPUTS.mkdir(parents=True, exist_ok=True)
    threading.Thread(target=worker, daemon=True).start()
    print(f"Reverie server on http://{a.host}:{a.port}  outputs: {OUTPUTS}", flush=True)
    uvicorn.run(app, host=a.host, port=a.port, log_level="info")


if __name__ == "__main__":
    main()
