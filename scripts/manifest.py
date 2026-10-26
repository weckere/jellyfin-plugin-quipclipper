#!/usr/bin/env python3
"""Build the Jellyfin repository manifest from the GitHub Releases.

    scripts/manifest.py RELEASES_DIR > manifest.json

RELEASES_DIR holds one folder per release tag (``v0.1.0.0/``), each with that release's
``version.json`` asset (written by scripts/package.py). The manifest is a JSON array with
one package; its ``versions`` are newest first, each pointing ``sourceUrl`` at the zip
attached to the release. Built from the releases each time, so nothing is committed to main.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
REPO = "weckere/jellyfin-plugin-quipclipper"


def build(releases_dir: Path) -> list[dict]:
    meta = json.loads((ROOT / "plugin-meta.json").read_text())
    versions = []
    for d in sorted(p for p in releases_dir.iterdir() if p.is_dir()):
        entry = json.loads((d / "version.json").read_text())
        v = entry["version"]
        if d.name != f"v{v}":
            raise SystemExit(f"manifest: {d.name}/version.json says {v}")
        versions.append({
            "version": v,
            "changelog": entry["changelog"],
            "targetAbi": entry["targetAbi"],
            "sourceUrl": f"https://github.com/{REPO}/releases/download/v{v}/jellyfin-plugin-quipclipper_{v}.zip",
            "checksum": entry["checksum"],
            "timestamp": entry["timestamp"],
        })
    versions.sort(key=lambda e: tuple(int(x) for x in e["version"].split(".")), reverse=True)
    return [{
        "guid": meta["guid"],
        "name": meta["name"],
        "description": meta["description"],
        "overview": meta["overview"],
        "owner": meta["owner"],
        "category": meta["category"],
        "imageUrl": f"https://raw.githubusercontent.com/{REPO}/main/images/logo.png",
        "versions": versions,
    }]


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    json.dump(build(Path(argv[1])), sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
