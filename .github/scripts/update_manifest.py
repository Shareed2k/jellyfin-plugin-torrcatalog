#!/usr/bin/env python3
"""Add (or replace) one release in a Jellyfin plugin repository manifest.

Jellyfin reads the manifest as a JSON array of packages, each with a list of versions
(MediaBrowser.Model/Updates/PackageInfo.cs, VersionInfo.cs); the zip's MD5 is verified on install.
Older versions stay listed so a rollback is one click in the catalog.
"""
import argparse
import json
import pathlib
from datetime import datetime, timezone

PLUGIN = {
    "guid": "4f0c6a3e-9b8d-4c52-a1f7-3e2d5b6c7a81",
    "name": "TorrCatalog",
    "overview": "TMDB catalogs as virtual libraries, played from torrents via Prowlarr and TorrServer",
    "description": "Shows TMDB catalogs (trending, new releases, TV, ...) as virtual movie and TV libraries "
                   "and plays them through Prowlarr + TorrServer, with a release picker, watch progress and "
                   "a transcoding fallback. Requires running Prowlarr and TorrServer instances.",
    "owner": "Shareed2k",
    "category": "General",
}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=pathlib.Path)
    parser.add_argument("--version", required=True, help="4-part version, e.g. 0.2.0.0")
    parser.add_argument("--target-abi", required=True, help="Jellyfin ABI, e.g. 12.1.0.0")
    parser.add_argument("--source-url", required=True)
    parser.add_argument("--checksum", required=True, help="MD5 of the zip")
    parser.add_argument("--changelog", default="")
    args = parser.parse_args()

    packages = json.loads(args.manifest.read_text()) if args.manifest.exists() else []
    package = next((p for p in packages if p.get("guid") == PLUGIN["guid"]), None)
    if package is None:
        package = {**PLUGIN, "versions": []}
        packages.append(package)
    package.update(PLUGIN)  # metadata edits in this script win

    version = {
        "version": args.version,
        "changelog": args.changelog.strip(),
        "targetAbi": args.target_abi,
        "sourceUrl": args.source_url,
        "checksum": args.checksum.lower(),
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    versions = [v for v in package["versions"] if v["version"] != args.version] + [version]
    package["versions"] = sorted(versions, key=lambda v: tuple(int(p) for p in v["version"].split(".")), reverse=True)

    args.manifest.parent.mkdir(parents=True, exist_ok=True)
    args.manifest.write_text(json.dumps(packages, indent=2, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main()
