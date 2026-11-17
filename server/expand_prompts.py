#!/usr/bin/env python3
"""
expand_prompts.py: expand a theme word into prompts with Llama 3.1, without Unity.

Sends the same prompts as Unity's Theme Tools, through Ollama. Needs only Python and a running Ollama
with the model pulled (`ollama pull llama3.1`).

OUTPUT (outputs/<slug>/, or --out)
  prompts.txt          the prompts, to read or copy
  llm_expansion.json   the same prompts, and how they were made
  props.json           suggested tool and target, read by fetch_props.py

USAGE
  python expand_prompts.py --theme forest
  python expand_prompts.py --theme "tropical beach" --seed 42
  python expand_prompts.py --theme forest --no-props --out path/to/theme

ENV
  OLLAMA_HOST       Ollama server (default http://localhost:11434)
  OLLAMA_MODEL      model tag (default llama3.1:latest)
  REVERIE_OUTPUTS   output root (default outputs/ next to this script)
"""
import argparse
import json
import os
import re
import time
from datetime import datetime
from pathlib import Path
from urllib.parse import urlparse

from log_times import ollama_post, slug

HERE = Path(__file__).resolve().parent
OUTPUTS = Path(os.environ.get("REVERIE_OUTPUTS", str(HERE / "outputs"))).resolve()


def is_local(url):
    """Prompts stay on this machine, so the Ollama server must be too."""
    return urlparse(url).hostname in ("localhost", "127.0.0.1", "::1", "0.0.0.0")


def read_prompt(name):
    return (HERE / name).read_text(encoding="utf-8").strip()


def chat(url, model, system, user, options, json_mode):
    payload = {"model": model, "stream": False, "options": options,
               "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}]}
    if json_mode:
        payload["format"] = "json"
    return ollama_post(url, "/api/chat", payload)["message"]["content"]


def expand(theme, url, model, temperature, seed):
    """Prompt expansion, as Theme Tools does it: returns the llm_expansion.json record."""
    options = {"temperature": temperature}
    if seed is not None:
        options["seed"] = seed
    started = time.time()
    reply = json.loads(chat(url, model, read_prompt("llm_system_prompt.txt"), theme, options, True))
    setting = reply.get("setting") or theme
    return {
        "theme": theme,
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "llm_endpoint": url.rstrip("/") + "/api/chat",
        "model": model,
        "temperature": temperature,
        "seed": seed,
        "setting": setting,
        "visual_prompt": reply.get("visualDescription") or f"Photorealistic environment: {setting}.",
        "audio_prompt": reply.get("audioDescription") or f"Ambient sounds of {setting}.",
        "wall_clock_s": round(time.time() - started, 2),
    }


def parse_props(raw):
    """The model reasons before its JSON, so the answer is the last {...} in the reply."""
    matches = re.findall(r"\{[^{}]*\}", raw.replace("```json", "").replace("```", ""))
    if not matches:
        raise SystemExit(f"Llama returned no JSON:\n{raw}")
    props = json.loads(matches[-1])
    if not str(props.get("tool", "")).strip() or not str(props.get("target", "")).strip():
        raise SystemExit(f"Llama's reply has no tool or target:\n{raw}")
    return {"tool": props["tool"].strip(), "target": props["target"].strip(),
            "reasoning": str(props.get("reasoning", "")).strip()}


def suggest_props(theme, url, model, seed):
    """Tool and target suggestion. Like Theme Tools, this uses the model's default temperature."""
    options = {} if seed is None else {"seed": seed}
    user = read_prompt("llm_tool_target_user.txt").replace("{theme}", theme)
    return parse_props(chat(url, model, read_prompt("llm_tool_target_prompt.txt"), user, options, False))


def prompts_text(record, props=None):
    """The contents of prompts.txt."""
    sections = [("Theme", record["theme"]), ("Setting", record["setting"]),
                ("Visual prompt", record["visual_prompt"]), ("Audio prompt", record["audio_prompt"])]
    if props:
        sections += [("Tool", props["tool"]), ("Target", props["target"])]
    return "\n\n".join(f"{label}:\n{text}" for label, text in sections) + "\n"


def write_json(path, data):
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Wrote {path}")


def main():
    ap = argparse.ArgumentParser(description="Expand a theme word into prompts with Llama 3.1.",
                                 formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__)
    ap.add_argument("--theme", required=True, help="theme word, as you would type it in Unity")
    ap.add_argument("--out", help="output folder (default: outputs/<slug>/)")
    ap.add_argument("--no-props", action="store_true", help="skip the tool and target suggestion")
    ap.add_argument("--model", default=os.environ.get("OLLAMA_MODEL", "llama3.1:latest"))
    ap.add_argument("--ollama-url", default=os.environ.get("OLLAMA_HOST", "http://localhost:11434"))
    ap.add_argument("--temperature", type=float, default=0.4, help="prompt expansion temperature (study: 0.4)")
    ap.add_argument("--seed", type=int, default=None, help="fix the sampling seed (study: none)")
    a = ap.parse_args()

    theme = a.theme.strip()
    name = slug(theme)
    if not name:
        ap.error(f"--theme '{a.theme}' has no letters or digits")
    url = a.ollama_url if "://" in a.ollama_url else "http://" + a.ollama_url
    if not is_local(url):
        ap.error(f"--ollama-url must be on this machine, not {url}")
    out = Path(a.out).resolve() if a.out else OUTPUTS / name
    out.mkdir(parents=True, exist_ok=True)

    record = expand(theme, url, a.model, a.temperature, a.seed)
    print(f"Setting: {record['setting']}\nVisual:  {record['visual_prompt']}\nAudio:   {record['audio_prompt']}")
    write_json(out / "llm_expansion.json", record)

    props = None
    if not a.no_props:
        props = suggest_props(theme, url, a.model, a.seed)
        print(f"Tool:    {props['tool']}\nTarget:  {props['target']}")
        write_json(out / "props.json", props)

    (out / "prompts.txt").write_text(prompts_text(record, props), encoding="utf-8")
    print(f"Wrote {out / 'prompts.txt'}")


if __name__ == "__main__":
    main()
