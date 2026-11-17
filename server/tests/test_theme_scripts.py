"""Run from server/: python -m unittest discover tests"""
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import expand_prompts
import fetch_props


def model(name, uid="a" * 32, slug=None):
    """A model as the API returns it. Search results carry no slug, so their viewerUrl reads
    .../none-<uid>; the model endpoint returns the canonical one."""
    return {"uid": uid, "name": name,
            "viewerUrl": f"https://sketchfab.com/3d-models/{slug or 'none'}-{uid}",
            "user": {"username": "maker", "displayName": "Max Maker"},
            "license": {"label": "CC Attribution", "slug": "by"}}


class PickBestMatch(unittest.TestCase):
    def test_prefers_the_exact_name(self):
        models = [model("Old axe with a very long extra name here"), model("Axe")]
        self.assertEqual(fetch_props.pick_best_match(models, "axe")["name"], "Axe")

    def test_drops_character_models(self):
        models = [model("Axe warrior"), model("Rusty axe")]
        self.assertEqual(fetch_props.pick_best_match(models, "axe")["name"], "Rusty axe")

    def test_keeps_characters_when_nothing_else_is_left(self):
        self.assertEqual(fetch_props.pick_best_match([model("Axe warrior")], "axe")["name"], "Axe warrior")

    def test_first_model_wins_a_tie(self):
        models = [model("Log one"), model("Log two")]
        self.assertEqual(fetch_props.pick_best_match(models, "log")["name"], "Log one")


class Fetch(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.out = Path(folder.name)

    def test_uid_skips_the_search(self):
        uid = "0123456789abcdef0123456789abcdef"
        with mock.patch.object(fetch_props, "get_json", return_value=model("Axe", uid)) as get:
            fetch_props.find_model(uid, "token")
        self.assertIn(f"/models/{uid}", get.call_args[0][0])

    def test_saves_a_plain_name_and_attribution(self):
        # search -> resolve the pick by uid -> download link
        replies = [{"results": [model("Axe")]}, model("Axe", slug="axe"),
                   {"glb": {"url": "https://files/axe.glb"}}]
        with mock.patch.object(fetch_props, "get_json", side_effect=replies), \
                mock.patch.object(fetch_props, "download") as download:
            entry = fetch_props.fetch("tool", "axe", self.out, "token")
        self.assertEqual(download.call_args[0][1], self.out / "tool.glb")
        self.assertEqual(entry["file"], "tool.glb")
        # The record is the attribution: credited name, licence code, canonical model page.
        self.assertEqual(entry["author"], "Max Maker")
        self.assertEqual(entry["license"], "CC-BY-4.0")
        self.assertEqual(entry["url"], "https://sketchfab.com/3d-models/axe-" + "a" * 32)

    def test_license_falls_back_to_the_label(self):
        self.assertEqual(
            fetch_props.license_of({"license": {"label": "Standard", "slug": "st"}}), "Standard")

    def test_search_result_is_resolved_by_uid(self):
        uid = "b" * 32
        replies = [{"results": [model("Axe", uid)]}, model("Axe", uid, slug="axe")]
        with mock.patch.object(fetch_props, "get_json", side_effect=replies) as get:
            found = fetch_props.find_model("axe", "token")
        self.assertIn("/models/" + uid, get.call_args[0][0])
        self.assertEqual(found["viewerUrl"], "https://sketchfab.com/3d-models/axe-" + uid)

    def test_no_results_returns_nothing(self):
        with mock.patch.object(fetch_props, "get_json", return_value={"results": []}):
            self.assertIsNone(fetch_props.fetch("tool", "nothing", self.out, "token"))

    def test_record_keeps_the_other_role(self):
        fetch_props.save_record(self.out, {"tool": {"file": "tool.glb"}})
        fetch_props.save_record(self.out, {"target": {"file": "target.glb"}})
        record = json.loads((self.out / "sketchfab.json").read_text(encoding="utf-8"))
        self.assertEqual(set(record), {"tool", "target"})

    def test_queries_fall_back_to_props_json(self):
        (self.out / "props.json").write_text('{"tool": "axe", "target": "log"}', encoding="utf-8")
        self.assertEqual(fetch_props.queries("hammer", None, self.out), {"tool": "hammer", "target": "log"})

    def test_queries_need_something_to_fetch(self):
        with self.assertRaises(SystemExit):
            fetch_props.queries(None, None, self.out)


class ExpandPrompts(unittest.TestCase):
    def test_only_local_ollama_is_allowed(self):
        self.assertTrue(expand_prompts.is_local("http://localhost:11434"))
        self.assertTrue(expand_prompts.is_local("http://127.0.0.1:11434"))
        self.assertFalse(expand_prompts.is_local("https://api.example.com"))

    def test_props_come_from_the_last_json(self):
        raw = 'Example {"tool": "a", "target": "b"}\nFinal:\n```json\n{"tool": "axe", "target": "log"}\n```'
        self.assertEqual(expand_prompts.parse_props(raw)["tool"], "axe")

    def test_missing_target_is_an_error(self):
        with self.assertRaises(SystemExit):
            expand_prompts.parse_props('{"tool": "axe"}')

    def test_record_matches_theme_tools_fields(self):
        reply = {"message": {"content": json.dumps(
            {"setting": "woods", "visualDescription": "trees", "audioDescription": "wind"})}}
        with mock.patch.object(expand_prompts, "ollama_post", return_value=reply) as post:
            record = expand_prompts.expand("forest", "http://localhost:11434", "llama3.1:latest", 0.4, 7)
        self.assertEqual(post.call_args[0][2]["options"], {"temperature": 0.4, "seed": 7})
        self.assertEqual(post.call_args[0][2]["format"], "json")
        self.assertEqual(set(record), {"theme", "created_at", "llm_endpoint", "model", "temperature", "seed",
                                       "setting", "visual_prompt", "audio_prompt", "wall_clock_s"})

    def test_prompts_text_lists_every_prompt(self):
        record = {"theme": "forest", "setting": "woods", "visual_prompt": "trees", "audio_prompt": "wind"}
        text = expand_prompts.prompts_text(record, {"tool": "axe", "target": "log"})
        self.assertEqual(text, "Theme:\nforest\n\nSetting:\nwoods\n\nVisual prompt:\ntrees\n\n"
                               "Audio prompt:\nwind\n\nTool:\naxe\n\nTarget:\nlog\n")

    def test_prompts_text_without_props(self):
        record = {"theme": "forest", "setting": "woods", "visual_prompt": "trees", "audio_prompt": "wind"}
        self.assertNotIn("Tool", expand_prompts.prompts_text(record))

    def test_missing_descriptions_use_the_setting(self):
        reply = {"message": {"content": '{"setting": "woods"}'}}
        with mock.patch.object(expand_prompts, "ollama_post", return_value=reply):
            record = expand_prompts.expand("forest", "http://x", "m", 0.4, None)
        self.assertIn("woods", record["visual_prompt"])


if __name__ == "__main__":
    unittest.main()
