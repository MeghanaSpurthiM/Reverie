"""Runs bake_theme.py end to end with a stand-in for DreamScene360, so no GPU is needed."""
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SERVER = Path(__file__).resolve().parent.parent
FAKE_DS360 = Path(__file__).resolve().parent / "dreamscene360_no_gpu"


def bake_env(root: Path) -> dict:
    """Environment for a no-GPU bake: a private copy of the no-GPU DreamScene360 stand-in and mock audio."""
    ds360 = root / "DreamScene360"
    ds360.mkdir(exist_ok=True)
    shutil.copy(FAKE_DS360 / "train.py", ds360 / "train.py")
    return {**os.environ, "DREAMSCENE360_DIR": str(ds360), "DREAMSCENE360_PYTHON": sys.executable,
            "REVERIE_OUTPUTS": str(root / "outputs"), "AUDIO_MODE": "mock", "PYTHONIOENCODING": "utf-8"}


def run_bake(env: dict, *args, theme="Test Forest"):
    return subprocess.run([sys.executable, str(SERVER / "bake_theme.py"), "--theme", theme, *args],
                          env=env, capture_output=True, text=True, encoding="utf-8")


class TempRoot(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.root = Path(folder.name)
        self.outputs = self.root / "outputs"
        self.env = bake_env(self.root)

    def bake(self, *args, **kw):
        return run_bake(self.env, *args, **kw)


@unittest.skipUnless(importlib.util.find_spec("PIL"), "needs Pillow (pip install -r requirements.txt)")
class BakePipeline(TempRoot):
    def test_full_bake_writes_every_output(self):
        result = self.bake("--prompt", "trees", "--audio-prompt", "wind")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

        theme = self.outputs / "test-forest"
        for name in ("panorama.jpg", "audio.wav", "meta.json",
                     "point_cloud/iteration_30000/point_cloud.ply"):
            self.assertTrue((theme / name).exists(), name)

        meta = json.loads((theme / "meta.json").read_text(encoding="utf-8"))
        self.assertEqual(meta["prompt"], "trees")
        self.assertEqual(meta["audio_prompt"], "wind")

    def test_finished_steps_are_skipped(self):
        self.bake("--prompt", "trees")
        result = self.bake("--prompt", "trees")
        self.assertIn("dreamscene360: skipped", result.stdout)
        self.assertIn("audio: skipped", result.stdout)

    def test_overwrite_redoes_finished_steps(self):
        self.bake("--prompt", "trees")
        result = self.bake("--prompt", "trees", "--overwrite")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertNotIn("skipped", result.stdout)

    def test_missing_dreamscene360_stops_at_preflight(self):
        self.env["DREAMSCENE360_DIR"] = str(self.root / "missing")
        result = self.bake("--prompt", "trees")
        self.assertEqual(result.returncode, 1)
        self.assertIn("Preflight failed", result.stdout)

    def test_failed_training_fails_the_bake(self):
        result = self.bake("--prompt", "trees [fail]")
        self.assertEqual(result.returncode, 1)
        self.assertIn("DreamScene360 exited with 3", result.stdout)
        self.assertFalse((self.outputs / "test-forest" / "meta.json").exists())

    def test_3dgs_only_trains_from_the_given_panorama(self):
        from PIL import Image
        pano = self.root / "pano.png"
        Image.new("RGB", (8, 4), (200, 0, 0)).save(pano)
        result = self.bake("--3dgs-only", "--panorama", str(pano), "--no-audio")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        theme = self.outputs / "test-forest"
        self.assertTrue((theme / "point_cloud/iteration_30000/point_cloud.ply").exists())
        self.assertFalse((theme / "audio.wav").exists())
        ds_input = self.root / "DreamScene360" / "data" / "test-forest"
        self.assertEqual([p.name for p in ds_input.iterdir()], ["test-forest_panorama.png"])
        with Image.open(theme / "panorama.jpg") as exported:   # the input panorama, not a new one
            self.assertGreater(exported.getpixel((0, 0))[0], 150)

    def test_3dgs_only_after_a_text_bake_leaves_one_png(self):
        from PIL import Image
        ds_input = self.root / "DreamScene360" / "data" / "test-forest"
        self.bake("--prompt", "trees", "--no-audio")
        Image.new("RGB", (8, 4)).save(ds_input / "diffusion_img.png")    # what the real train.py leaves
        pano = self.root / "pano.png"
        Image.new("RGB", (8, 4), (200, 0, 0)).save(pano)
        result = self.bake("--3dgs-only", "--panorama", str(pano), "--no-audio", "--overwrite")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(sorted(p.name for p in ds_input.iterdir()), ["test-forest_panorama.png"])

    def test_audio_only_writes_audio_and_meta_only(self):
        result = self.bake("--audio-only")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        theme = self.outputs / "test-forest"
        self.assertEqual(sorted(p.name for p in theme.iterdir()), ["audio.wav", "meta.json"])
        meta = json.loads((theme / "meta.json").read_text(encoding="utf-8"))
        self.assertEqual(meta["prompt"], "Test Forest")    # --prompt defaults to --theme
        self.assertIn("Audio prompt (built from visual prompt", result.stdout)


class BakeArguments(TempRoot):
    """Argument handling and --list-steps. No step runs, so these need nothing installed."""

    def steps(self, *args):
        result = self.bake(*args, "--list-steps")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return [line.split(". ", 1)[1] for line in result.stdout.splitlines() if ". " in line and "===" not in line]

    def test_full_bake_has_seven_steps(self):
        self.assertEqual(self.steps("--prompt", "trees"),
                         ["preflight", "prepare-input", "dreamscene360", "collect-ply", "export-panorama",
                          "audio", "meta"])

    def test_audio_only_steps(self):
        self.assertEqual(self.steps("--audio-only"), ["preflight", "audio", "meta"])

    def test_no_audio_steps(self):
        self.assertNotIn("audio", self.steps("--no-audio"))

    def test_3dgs_only_steps(self):
        pano = self.root / "pano.jpg"
        pano.write_bytes(b"\xff\xd8\xff")      # only its existence is checked by --list-steps
        self.assertEqual(self.steps("--3dgs-only", "--panorama", str(pano)),
                         ["preflight", "prepare-input", "dreamscene360", "collect-ply", "export-panorama",
                          "audio", "meta"])

    def test_list_steps_creates_nothing(self):
        self.steps("--prompt", "trees")
        self.assertFalse(self.outputs.exists())

    def assert_usage_error(self, *args, message):
        result = self.bake(*args)
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn(message, result.stderr)

    def test_theme_is_required(self):
        result = subprocess.run([sys.executable, str(SERVER / "bake_theme.py")], env=self.env,
                                capture_output=True, text=True)
        self.assertEqual(result.returncode, 2)
        self.assertIn("--theme", result.stderr)

    def test_panorama_needs_3dgs_only(self):
        self.assert_usage_error("--panorama", "x.png", message="--panorama only applies with --3dgs-only")

    def test_audio_only_and_no_audio_contradict(self):
        self.assert_usage_error("--audio-only", "--no-audio", message="contradict")

    def test_audio_only_and_3dgs_only_are_exclusive(self):
        self.assert_usage_error("--audio-only", "--3dgs-only", message="not allowed with")

    def test_theme_without_letters_is_refused(self):
        result = self.bake("--list-steps", theme="!!!")
        self.assertEqual(result.returncode, 1)
        self.assertIn("no letters or digits", result.stdout)

    def test_3dgs_only_without_a_panorama_is_refused(self):
        result = self.bake("--3dgs-only", "--list-steps")
        self.assertEqual(result.returncode, 1)
        self.assertIn("--3dgs-only needs a panorama", result.stdout)

    def test_missing_panorama_file_is_refused(self):
        result = self.bake("--3dgs-only", "--panorama", str(self.root / "nope.png"), "--list-steps")
        self.assertEqual(result.returncode, 1)
        self.assertIn("does not exist", result.stdout)

    def test_unknown_audio_mode_fails_preflight(self):
        self.env["AUDIO_MODE"] = "loud"
        result = self.bake("--audio-only")
        self.assertEqual(result.returncode, 1)
        self.assertIn("AUDIO_MODE=loud", result.stdout)

    def test_old_flags_are_accepted_and_ignored(self):
        result = self.bake("--api-key", "x", "--list-steps")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("ignored", result.stdout)


if __name__ == "__main__":
    unittest.main()
