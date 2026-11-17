#!/usr/bin/env python3
"""
fetch_props.py: download a theme's tool and target from Sketchfab, without Unity.

Uses the same search and model choice as Unity's Theme Tools. Each of --tool and --target can be a
search phrase or an exact 32-character Sketchfab model UID. Without them, it reads props.json from
expand_prompts.py.

OUTPUT (outputs/<slug>/, or --out)
  tool.glb, target.glb   the models
  sketchfab.json         each model's UID, author, URL and license, for attribution

USAGE
  python fetch_props.py --theme forest --tool axe --target log
  python fetch_props.py --theme forest                          # uses props.json
  python fetch_props.py --theme forest --tool axe --search-only # shows the choice, no token needed

ENV
  SKETCHFAB_TOKEN   your API token: sketchfab.com -> Settings -> Password & API
  REVERIE_OUTPUTS   output root (default outputs/ next to this script)

Check each model's license before you share it. Most need credit, and some forbid commercial use.
"""
import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime
from pathlib import Path

from log_times import slug

API = "https://api.sketchfab.com/v3"
OUTPUTS = Path(os.environ.get("REVERIE_OUTPUTS", str(Path(__file__).resolve().parent / "outputs"))).resolve()
UID = re.compile(r"^[0-9a-f]{32}$")
CHARACTER_WORDS = (
    "girl", "boy", "man", "woman", "female", "male", "character", "human",
    "person", "avatar", "soldier", "warrior", "knight", "ninja", "robot",
    "alien", "zombie", "monster", "creature", "beast", "demon", "angel",
    "anime", "waifu", "figure", "bust", "portrait", "face", "head",
)


def get_json(url, token=""):
    request = urllib.request.Request(url)
    if token:
        request.add_header("Authorization", f"Token {token}")
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.loads(response.read().decode())
    except urllib.error.HTTPError as e:
        if e.code == 401:
            raise SystemExit("Sketchfab rejected the token. Check SKETCHFAB_TOKEN.")
        raise SystemExit(f"Sketchfab returned {e.code} for {url.split('?')[0]}")
    except urllib.error.URLError as e:
        raise SystemExit(f"Cannot reach Sketchfab ({e.reason}).")


def download(url, path):
    """Writes to a .part file first, so a failed download never replaces a working model."""
    part = path.with_name(path.name + ".part")
    with urllib.request.urlopen(url, timeout=300) as response, open(part, "wb") as f:
        while True:
            chunk = response.read(1 << 20)
            if not chunk:
                break
            f.write(chunk)
    part.replace(path)


def score(name, query):
    """Higher when the model's name matches the query closely and has few extra words."""
    name, query = name.lower(), query.lower()
    keywords = [w for w in re.split(r"[ \-_]", query) if w]
    words = [w for w in re.split(r"[ \-_()\[\]]", name) if w]
    total = sum(10 if k in words else 2 if k in name else 0 for k in keywords)
    if name == query:
        total += 20
    elif name.startswith(query):
        total += 8
    extra = len(words) - len(keywords)
    return total - extra * 2 if extra > 2 else total


def pick_best_match(models, query):
    """Drops character models unless nothing else is left, then takes the first best score."""
    props = [m for m in models if not any(w in m.get("name", "").lower() for w in CHARACTER_WORDS)]
    candidates = props or models
    return max(candidates, key=lambda m: score(m.get("name", ""), query))


def find_model(query, token):
    if UID.match(query):
        return get_json(f"{API}/models/{query}", token)
    params = urllib.parse.urlencode({"type": "models", "q": query, "downloadable": "true",
                                     "restricted": "false", "count": 24})
    models = get_json(f"{API}/search?{params}", token).get("results") or []
    if not models:
        return None
    best = pick_best_match(models, query)
    # Search results omit the slug, so their viewerUrl reads .../none-<uid>; the model endpoint
    # has the canonical url, the author's display name and the license slug.
    return get_json(f"{API}/models/{best['uid']}", token) if best else None


# Sketchfab labels a license "CC Attribution"; the credit files and docs/THIRD_PARTY.md use the
# code form, so record that and keep the label only for licenses outside this list.
LICENSE_CODES = {"by": "CC-BY-4.0", "by-sa": "CC-BY-SA-4.0", "by-nd": "CC-BY-ND-4.0",
                 "by-nc": "CC-BY-NC-4.0", "by-nc-sa": "CC-BY-NC-SA-4.0",
                 "by-nc-nd": "CC-BY-NC-ND-4.0", "cc0": "CC0-1.0"}


def license_of(model):
    value = model.get("license")
    if not isinstance(value, dict):
        return value
    return LICENSE_CODES.get(value.get("slug")) or value.get("label")


def author_of(model):
    """The name Sketchfab shows on the model page: "Axe by Yury Misiyuk (@Tim0)"."""
    user = model.get("user") or {}
    return user.get("displayName") or user.get("username")


def describe(model):
    return f"{model.get('name')} by {author_of(model)} ({license_of(model)}) {model.get('viewerUrl')}"


def fetch(role, query, out, token):
    """Downloads one model. Returns its sketchfab.json entry, or None if it couldn't be fetched."""
    model = find_model(query, token)
    if model is None:
        print(f"{role}: no downloadable models for '{query}'", file=sys.stderr)
        return None
    link = (get_json(f"{API}/models/{model['uid']}/download", token).get("glb") or {}).get("url")
    if not link:
        print(f"{role}: '{model.get('name')}' has no GLB download", file=sys.stderr)
        return None
    path = out / f"{role}.glb"
    download(link, path)
    print(f"{role}: {describe(model)}\n  saved {path}")
    return {"query": query, "file": path.name, "uid": model.get("uid"), "name": model.get("name"),
            "author": author_of(model), "url": model.get("viewerUrl"),
            "license": license_of(model), "downloaded_at": datetime.now().isoformat(timespec="seconds")}


def save_record(out, entries):
    path = out / "sketchfab.json"
    record = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
    record.update(entries)
    path.write_text(json.dumps(record, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Wrote {path}")


def queries(tool, target, out):
    """Explicit arguments first, then props.json for whichever is missing."""
    props_path = out / "props.json"
    props = json.loads(props_path.read_text(encoding="utf-8")) if props_path.exists() else {}
    chosen = {"tool": tool or props.get("tool"), "target": target or props.get("target")}
    if not any(chosen.values()):
        raise SystemExit(f"Nothing to fetch: pass --tool or --target, or run expand_prompts.py first ({props_path}).")
    return {role: query.strip() for role, query in chosen.items() if query}


def main():
    ap = argparse.ArgumentParser(description="Download a theme's tool and target from Sketchfab.",
                                 formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__)
    ap.add_argument("--theme", required=True, help="theme name; its slug names the output folder")
    ap.add_argument("--tool", help="search phrase or 32-character model UID")
    ap.add_argument("--target", help="search phrase or 32-character model UID")
    ap.add_argument("--out", help="output folder (default: outputs/<slug>/)")
    ap.add_argument("--search-only", action="store_true", help="show the chosen models without downloading")
    a = ap.parse_args()

    name = slug(a.theme)
    if not name:
        ap.error(f"--theme '{a.theme}' has no letters or digits")
    out = Path(a.out).resolve() if a.out else OUTPUTS / name
    wanted = queries(a.tool, a.target, out)
    token = os.environ.get("SKETCHFAB_TOKEN", "")

    if a.search_only:
        for role, query in wanted.items():
            model = find_model(query, token)
            print(f"{role}: {describe(model) if model else 'no downloadable models'}")
        return 0

    if not token:
        raise SystemExit("Set SKETCHFAB_TOKEN to download models (sketchfab.com -> Settings -> Password & API).")
    out.mkdir(parents=True, exist_ok=True)
    entries = {role: fetch(role, query, out, token) for role, query in wanted.items()}
    fetched = {role: entry for role, entry in entries.items() if entry}
    if fetched:
        save_record(out, fetched)
    return 0 if len(fetched) == len(wanted) else 1


if __name__ == "__main__":
    sys.exit(main())
