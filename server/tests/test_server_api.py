"""The HTTP API that Unity's Theme Tools relies on, tested against a running reverie_server.py.

The server runs as in docs/SERVER.md ("Quick test"): a stand-in for DreamScene360 and AUDIO_MODE=mock,
so no GPU, model weights or network are needed. Needs the packages in requirements.txt.
"""
import importlib.util
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from test_bake_pipeline import SERVER, bake_env  # noqa: E402

HAVE_SERVER_PACKAGES = all(importlib.util.find_spec(m) for m in ("fastapi", "uvicorn", "pydantic", "PIL"))
SKIP_REASON = "needs fastapi, uvicorn, pydantic and Pillow (pip install -r requirements.txt)"
TOKEN = "test-token"


def png_bytes() -> bytes:
    import io
    from PIL import Image
    buffer = io.BytesIO()
    Image.new("RGB", (8, 4), (0, 120, 200)).save(buffer, "PNG")
    return buffer.getvalue()


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


@unittest.skipUnless(HAVE_SERVER_PACKAGES, SKIP_REASON)
class ServerApi(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.tmp.name)
        cls.outputs = cls.root / "outputs"
        env = {**bake_env(cls.root), "REVERIE_TOKEN": TOKEN, "PYTHONUNBUFFERED": "1"}
        port = free_port()
        cls.url = f"http://127.0.0.1:{port}"
        cls.log = open(cls.root / "server.log", "w+", encoding="utf-8")
        cls.proc = subprocess.Popen([sys.executable, str(SERVER / "reverie_server.py"), "--port", str(port)],
                                    cwd=str(SERVER), env=env, stdout=cls.log, stderr=subprocess.STDOUT)
        deadline = time.time() + 30
        while True:
            try:
                urllib.request.urlopen(cls.url + "/health", timeout=1).close()
                break
            except OSError:
                if cls.proc.poll() is not None or time.time() > deadline:
                    cls.tearDownClass()
                    raise RuntimeError("reverie_server.py did not start:\n" + cls.server_log())
                time.sleep(0.2)

    @classmethod
    def tearDownClass(cls):
        cls.proc.terminate()
        try:
            cls.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            cls.proc.kill()
            cls.proc.wait()
        cls.log.close()
        cls.tmp.cleanup()

    @classmethod
    def server_log(cls) -> str:
        cls.log.flush()
        cls.log.seek(0)
        return cls.log.read()

    # ── HTTP helpers ─────────────────────────────────────────────────────────

    def call(self, method, path, body=None, token=TOKEN, raw=False):
        """Returns (status, body). body is parsed JSON unless raw=True."""
        data, headers = None, {}
        if isinstance(body, (bytes, bytearray)):
            data = bytes(body)
        elif body is not None:
            data, headers["Content-Type"] = json.dumps(body).encode(), "application/json"
        if token:
            headers["Authorization"] = f"Bearer {token}"
        request = urllib.request.Request(self.url + path, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=30) as r:
                status, payload, self.last_headers = r.status, r.read(), r.headers
        except urllib.error.HTTPError as e:
            status, payload, self.last_headers = e.code, e.read(), e.headers
        return status, payload if raw else json.loads(payload or b"null")

    def bake(self, slug, **body):
        return self.call("POST", f"/themes/{slug}/bake", body)

    def wait(self, slug, timeout=60):
        deadline = time.time() + timeout
        while time.time() < deadline:
            status, job = self.call("GET", f"/themes/{slug}/status")
            if job.get("status") not in ("queued", "running"):
                return job
            time.sleep(0.2)
        self.fail(f"{slug} still {job.get('status')} after {timeout} s\n" + self.server_log())

    def bake_and_wait(self, slug, **body):
        status, job = self.bake(slug, **body)
        self.assertEqual(status, 200, job)
        return self.wait(slug)

    # ── Tests ───────────────────────────────────────────────────────────────

    def test_health_needs_no_token(self):
        status, health = self.call("GET", "/health", token=None)
        self.assertEqual(status, 200)
        self.assertTrue(health["ok"])
        self.assertTrue(health["token_required"])
        self.assertEqual(set(health), {"ok", "gpu", "running", "queued", "outputs", "token_required"})

    def test_other_endpoints_need_the_token(self):
        for method, path in (("GET", "/themes"), ("GET", "/themes/forest/status"), ("GET", "/themes/forest/ply"),
                             ("POST", "/themes/forest/bake"), ("PUT", "/themes/forest/panorama")):
            for token in (None, "wrong"):
                with self.subTest(method=method, path=path, token=token):
                    status, _ = self.call(method, path, body={} if method != "GET" else None, token=token)
                    self.assertEqual(status, 401)

    def test_full_bake_then_download_every_file(self):
        job = self.bake_and_wait("api-forest", prompt="a forest at dawn", audio_prompt="birdsong")
        self.assertEqual(job["status"], "done", job.get("error"))
        self.assertEqual(job["progress"], 1.0)
        self.assertTrue(job["has_panorama"] and job["has_ply"] and job["has_audio"])

        expected = {"panorama": ("image/jpeg", b"\xff\xd8\xff"), "ply": ("application/octet-stream", b"ply"),
                    "audio": ("audio/wav", b"RIFF"), "meta": ("application/json", b"{")}
        for name, (media, magic) in expected.items():
            with self.subTest(name=name):
                status, body = self.call("GET", f"/themes/api-forest/{name}", raw=True)
                self.assertEqual(status, 200)
                self.assertTrue(self.last_headers["Content-Type"].startswith(media))
                self.assertTrue(body.startswith(magic))
        meta = json.loads(self.call("GET", "/themes/api-forest/meta", raw=True)[1])
        self.assertEqual((meta["prompt"], meta["audio_prompt"]), ("a forest at dawn", "birdsong"))
        self.assertTrue((self.outputs / "api-forest" / "bake.log").exists())

        status, themes = self.call("GET", "/themes")
        self.assertIn({"slug": "api-forest", "has_panorama": True, "has_ply": True, "has_audio": True},
                      themes["themes"])

    def test_finished_bake_is_not_redone_without_overwrite(self):
        self.bake_and_wait("api-again", prompt="hills", audio=False)
        status, job = self.bake("api-again", prompt="hills", audio=False)
        self.assertEqual((status, job["status"]), (200, "done"))
        self.assertIn("overwrite", job["note"])

    def test_status_while_running_and_duplicate_submit(self):
        status, job = self.bake("api-slow", prompt="slow valley [slow]", audio=False)
        self.assertEqual(status, 200)
        self.assertIn(job["status"], ("queued", "running"))
        self.assertEqual(self.bake("api-slow", prompt="other")[1]["prompt"], "slow valley [slow]")
        status, _ = self.call("PUT", "/themes/api-slow/panorama", png_bytes())
        self.assertEqual(status, 409)
        self.assertEqual(self.wait("api-slow")["status"], "done")

    def test_failed_bake_reports_the_log_tail(self):
        job = self.bake_and_wait("api-broken", prompt="cave [fail]", audio=False)
        self.assertEqual(job["status"], "failed")
        self.assertIn("bake_theme.py exited 1", job["error"])
        self.assertIn("DreamScene360 exited with 3", job["error"])
        status, _ = self.call("GET", "/themes/api-broken/ply")
        self.assertEqual(status, 404)

    def test_audio_only_bake(self):
        job = self.bake_and_wait("api-audio", scene=False, audio_prompt="waves")
        self.assertEqual(job["status"], "done", job.get("error"))
        self.assertEqual((job["has_audio"], job["has_ply"]), (True, False))

    def test_upload_panorama_then_train_from_it(self):
        status, stored = self.call("PUT", "/themes/api-pano/panorama", png_bytes())
        self.assertEqual(status, 200, stored)
        self.assertEqual(stored["stored"], "panorama.jpg")
        self.assertTrue(stored["has_panorama"])
        job = self.bake_and_wait("api-pano", use_existing_panorama=True, audio=False)
        self.assertEqual(job["status"], "done", job.get("error"))
        self.assertTrue(job["has_ply"])

    def test_upload_rejects_non_images(self):
        status, _ = self.call("PUT", "/themes/api-pano-bad/panorama", b"not an image")
        self.assertEqual(status, 415)

    def test_bake_request_validation(self):
        cases = [("Bad_Slug", {"prompt": "x"}, 400),
                 ("api-x", {}, 400),                                         # no prompt for a scene
                 ("api-x", {"scene": False, "audio": False}, 400),           # nothing to bake
                 ("api-x", {"use_existing_panorama": True}, 409),            # no panorama on the server
                 ("api-x", {"prompt": "x", "scene": "maybe"}, 422)]          # wrong type
        for slug, body, expected in cases:
            with self.subTest(slug=slug, body=body):
                status, reply = self.bake(slug, **body)
                self.assertEqual(status, expected, reply)
        self.assertIn("PUT /themes/api-x/panorama", self.bake("api-x", use_existing_panorama=True)[1]["detail"])

    def test_unknown_theme(self):
        self.assertEqual(self.call("GET", "/themes/nothing-here/status")[0], 404)
        for name in ("panorama", "ply", "audio", "meta"):
            with self.subTest(name=name):
                self.assertEqual(self.call("GET", f"/themes/nothing-here/{name}")[0], 404)


@unittest.skipUnless(HAVE_SERVER_PACKAGES, SKIP_REASON)
class BakeCommand(unittest.TestCase):
    """How a bake request's options map onto bake_theme.py flags."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(SERVER))
        import reverie_server
        cls.server = reverie_server

    def flags(self, **overrides):
        job = {"prompt": "trees", "audio_prompt": "", "scene": True, "audio": True,
               "use_existing_panorama": False, "overwrite": False, **overrides}
        return self.server.bake_command("forest", job)[2:]

    def test_full_bake(self):
        self.assertEqual(self.flags(), ["--theme", "forest", "--prompt", "trees"])

    def test_audio_only(self):
        self.assertIn("--audio-only", self.flags(scene=False))

    def test_scene_without_audio(self):
        self.assertIn("--no-audio", self.flags(audio=False))

    def test_existing_panorama(self):
        flags = self.flags(use_existing_panorama=True, prompt="")
        self.assertIn("--3dgs-only", flags)
        self.assertNotIn("--prompt", flags)

    def test_audio_prompt_and_overwrite(self):
        flags = self.flags(audio_prompt="wind", overwrite=True)
        self.assertEqual(flags[flags.index("--audio-prompt") + 1], "wind")
        self.assertIn("--overwrite", flags)


class Requirements(unittest.TestCase):
    """requirements.txt (no-GPU subset) and environment.yml must pin the same versions."""

    @staticmethod
    def pins(path):
        found = {}
        for line in path.read_text(encoding="utf-8").splitlines():
            line = line.split("#")[0].strip().lstrip("- ").strip()
            if "==" in line and not line.startswith("--"):
                name, version = line.split("==")
                found[name.strip().lower()] = version.strip()
        return found

    def test_pins_agree(self):
        reqs, env = self.pins(SERVER / "requirements.txt"), self.pins(SERVER / "environment.yml")
        self.assertTrue(reqs)
        for name, version in reqs.items():
            self.assertEqual(env.get(name), version, f"{name} differs between requirements.txt and environment.yml")

    def test_env_name_matches_the_docs(self):
        text = (SERVER / "environment.yml").read_text(encoding="utf-8")
        self.assertIn("\nname: reverie-audio\n", text)


if __name__ == "__main__":
    unittest.main()
