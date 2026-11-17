"""Stand-in for DreamScene360's train.py, so the bake pipeline and server run without a GPU.

Takes the same arguments bake_theme.py passes, writes a tiny panorama and an empty .ply where the
real train.py would, and prints progress lines like the real one. It trains nothing.

Point DREAMSCENE360_DIR at this folder (see docs/SERVER.md, "Quick test"). Words in the visual prompt
change its behaviour, for the tests:
  [slow]  waits 3 s, so a bake stays "running" long enough to inspect
  [fail]  exits with code 3, like a crashed training run
"""
import argparse
import sys
import time
from pathlib import Path

from PIL import Image

ap = argparse.ArgumentParser()
for flag in ("-s", "-m", "--iterations", "--save_iterations", "--test_iterations", "--ip", "--port"):
    ap.add_argument(flag)
a = ap.parse_args()

source = Path(a.s)
prompt = " ".join(p.read_text(encoding="utf-8") for p in source.glob("*_PROMPT.txt"))
if "[fail]" in prompt:
    print("fake train.py: failing on purpose ([fail] in the prompt)", flush=True)
    sys.exit(3)

iterations = int(a.iterations)
# DreamScene360 writes the panorama into the data folder in text mode; leave a panorama input alone.
if not any(source.glob("*_panorama.png")):
    Image.new("RGB", (8, 4), (40, 90, 40)).save(source / "image.png")
for i in (iterations // 3, 2 * iterations // 3, iterations):
    print(f"Training progress: {i}/{iterations}", flush=True)
    if "[slow]" in prompt:
        time.sleep(1)

ply = Path(a.m) / "point_cloud" / f"iteration_{iterations}" / "point_cloud.ply"
ply.parent.mkdir(parents=True, exist_ok=True)
ply.write_text("ply\n", encoding="utf-8")
