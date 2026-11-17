#!/usr/bin/env python3
"""
smoke_test.py: checks the theme-generation pipeline end to end in about a minute, without a GPU.

  cd server
  pip install -r requirements.txt
  python smoke_test.py

1. Runs the unit and HTTP API tests (tests/).
2. Bakes a theme with bake_theme.py, using the DreamScene360 stand-in (tests/dreamscene360_no_gpu)
   and AUDIO_MODE=mock, into a temporary folder.
3. Checks that the theme folder has the files the server hands to the Unity client.

It never calls DreamScene360, AudioLDM 2, Ollama or Sketchfab. For the real models, see docs/SERVER.md.
Exit code 0 means every check passed.
"""
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
# What reverie_server.py serves as /panorama, /ply, /audio and /meta.
EXPECTED = ("panorama.jpg", "point_cloud/iteration_30000/point_cloud.ply", "audio.wav", "meta.json")


def run_tests() -> bool:
    suite = unittest.defaultTestLoader.discover(str(HERE / "tests"))
    result = unittest.TextTestRunner(verbosity=1).run(suite)
    # Skips are silent in unittest's summary, so an unprepared environment can look like a pass.
    if result.skipped:
        print(f"WARNING: {len(result.skipped)} test(s) skipped, so part of the server is unchecked. "
              f"First reason: {result.skipped[0][1].splitlines()[0]}")
    return result.wasSuccessful()


def run_bake(outputs: Path) -> bool:
    env = {**os.environ,
           "DREAMSCENE360_DIR": str(HERE / "tests" / "dreamscene360_no_gpu"),
           "DREAMSCENE360_PYTHON": sys.executable,
           "AUDIO_MODE": "mock",
           "REVERIE_OUTPUTS": str(outputs)}
    cmd = [sys.executable, str(HERE / "bake_theme.py"), "--theme", "smoke",
           "--prompt", "a quiet forest at dawn", "--audio-prompt", "wind in the trees"]
    result = subprocess.run(cmd, cwd=HERE, env=env)
    if result.returncode != 0:
        print(f"FAIL: bake_theme.py exited with {result.returncode}")
        return False
    theme = outputs / "smoke"
    missing = [name for name in EXPECTED if not (theme / name).is_file()]
    if missing:
        print(f"FAIL: {theme} is missing {', '.join(missing)}. It has: "
              f"{sorted(p.name for p in theme.iterdir()) if theme.is_dir() else 'nothing'}")
        return False
    print(f"OK: bake wrote {', '.join(Path(n).name for n in EXPECTED)}")
    return True


def main() -> int:
    ok = run_tests()
    with tempfile.TemporaryDirectory(prefix="reverie-smoke-") as tmp:
        ok = run_bake(Path(tmp)) and ok
    print("SMOKE TEST PASSED" if ok else "SMOKE TEST FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
