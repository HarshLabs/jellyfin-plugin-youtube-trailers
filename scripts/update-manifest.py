#!/usr/bin/env python3
"""Add this release's per-ABI builds to manifest.json.

Why not `jprm repo add`? It de-duplicates by version string, so adding a second
artifact for the same version *replaces* the first. This plugin ships one build
per Jellyfin major (net9.0/ABI 10.11.0.0 and net10.0/ABI 12.0.0.0) under a
single version number, so jprm would silently drop one of them — leaving the
older-server audience stranded on the previous release.

Ordering matters. Jellyfin filters the version list to entries whose targetAbi
its own version satisfies, then sorts by version descending. That sort is
stable, so among entries sharing a version the original array order decides.
Emitting the HIGHEST targetAbi first therefore means:

  * a 12.x server sees both entries, and the 12 build wins the tie;
  * a 10.11 server never sees the 12 entry at all (its targetAbi is too high)
    and lands on the 10.11 build.

Usage:
  update-manifest.py --version 1.2.0.0 --changelog-file notes.txt \\
      --entry <zip>=<targetAbi>=<download-url> [--entry ...]
"""
import argparse
import hashlib
import json
import pathlib
import sys
from datetime import datetime, timezone


def md5(path: pathlib.Path) -> str:
    digest = hashlib.md5()  # noqa: S324 - matches the checksum format Jellyfin expects
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", default="manifest.json")
    ap.add_argument("--version", required=True)
    ap.add_argument("--changelog-file", required=True)
    ap.add_argument(
        "--entry",
        action="append",
        required=True,
        metavar="ZIP=ABI=URL",
        help="One per Jellyfin ABI.",
    )
    args = ap.parse_args()

    changelog = pathlib.Path(args.changelog_file).read_text().strip() + "\n"
    manifest_path = pathlib.Path(args.manifest)
    manifest = json.loads(manifest_path.read_text())
    if not manifest:
        print("manifest.json is empty", file=sys.stderr)
        return 1
    plugin = manifest[0]
    versions = plugin.setdefault("versions", [])

    entries = []
    for raw in args.entry:
        zip_part, abi, url = raw.split("=", 2)
        zip_path = pathlib.Path(zip_part)
        if not zip_path.is_file():
            print(f"missing artifact: {zip_path}", file=sys.stderr)
            return 1
        entries.append(
            {
                "version": args.version,
                "changelog": changelog,
                "targetAbi": abi,
                "sourceUrl": url,
                "checksum": md5(zip_path),
                "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            }
        )

    # Re-running a release must not accumulate duplicates.
    versions[:] = [v for v in versions if v.get("version") != args.version]

    # Highest targetAbi first — see the module docstring for why this is what
    # steers each server to the right build.
    entries.sort(key=lambda e: [int(p) for p in e["targetAbi"].split(".")], reverse=True)
    versions[:0] = entries

    manifest_path.write_text(json.dumps(manifest, indent=4) + "\n")
    for entry in entries:
        print(f"  added {entry['version']} targetAbi={entry['targetAbi']} -> {entry['sourceUrl']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
