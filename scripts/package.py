#!/usr/bin/env python3
"""Package a built plugin for a Jellyfin repository.

    scripts/package.py VERSION BUILD_DIR OUT_DIR

Writes, into OUT_DIR:
  jellyfin-plugin-quipclipper_VERSION.zip  the DLL (and its .pdb) with meta.json
  version.json                             this version's manifest entry, minus sourceUrl:
                                           version, changelog, targetAbi, checksum (the
                                           zip's MD5, what Jellyfin checks), timestamp
The changelog is the version's CHANGELOG.md section.
"""

from __future__ import annotations

import hashlib
import json
import re
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def changelog_section(version: str, text: str) -> str:
    """The body of ``## [VERSION] - date`` in CHANGELOG.md, without the heading."""
    m = re.search(rf"^## \[{re.escape(version)}\][^\n]*\n(.*?)(?=^## \[|\Z)", text, re.M | re.S)
    if not m:
        raise SystemExit(f"package: CHANGELOG.md has no section for {version}")
    return m.group(1).strip()


def main(argv: list[str]) -> int:
    if len(argv) != 4:
        print(__doc__, file=sys.stderr)
        return 2
    version, build_dir, out_dir = argv[1], Path(argv[2]), Path(argv[3])
    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
        raise SystemExit(f"package: not a four-part version: {version}")
    meta = json.loads((ROOT / "plugin-meta.json").read_text())
    changelog = changelog_section(version, (ROOT / "CHANGELOG.md").read_text())
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    files = []
    for artifact in meta["artifacts"]:
        dll = build_dir / artifact
        if not dll.is_file():
            raise SystemExit(f"package: {dll} is missing (build first)")
        files.append(dll)
        if (pdb := dll.with_suffix(".pdb")).is_file():
            files.append(pdb)

    plugin_meta = {
        "category": meta["category"],
        "changelog": changelog,
        "description": meta["description"],
        "guid": meta["guid"],
        "name": meta["name"],
        "overview": meta["overview"],
        "owner": meta["owner"],
        "targetAbi": meta["targetAbi"],
        "timestamp": timestamp,
        "version": version,
    }

    out_dir.mkdir(parents=True, exist_ok=True)
    zip_path = out_dir / f"jellyfin-plugin-quipclipper_{version}.zip"
    # Fixed dates and order, so the same build packs to the same bytes.
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for f in files:
            info = zipfile.ZipInfo(f.name, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, f.read_bytes())
        info = zipfile.ZipInfo("meta.json", date_time=(2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, json.dumps(plugin_meta, indent=2) + "\n")

    checksum = hashlib.md5(zip_path.read_bytes()).hexdigest()  # noqa: S324 - Jellyfin's manifest checksum is MD5
    entry = {"version": version, "changelog": changelog, "targetAbi": meta["targetAbi"],
             "checksum": checksum, "timestamp": timestamp}
    (out_dir / "version.json").write_text(json.dumps(entry, indent=2) + "\n")
    print(f"{zip_path.name} md5 {checksum}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
