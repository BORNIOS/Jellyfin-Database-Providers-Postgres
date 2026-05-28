import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path


def main() -> None:
    version = os.environ.get("VERSION", "0.0.0")
    if version.startswith("v"):
        version = version[1:]

    asset_name = "Jellyfin.Database.Providers.Postgres.zip"
    source_url = (
        "https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/"
        f"releases/download/v{version}/{asset_name}"
    )
    image_url = (
        "https://raw.githubusercontent.com/BORNIOS/Jellyfin-Database-Providers-Postgres/"
        f"v{version}/Resources/logo.png"
    )
    release_dir = Path("release")
    zip_path = release_dir / asset_name

    if not zip_path.exists():
        raise FileNotFoundError(f"Missing release asset: {zip_path}")

    checksum = hashlib.md5(zip_path.read_bytes()).hexdigest()

    new_version_entry = {
        "version": version,
        "changelog": "",
        "targetAbi": "10.11.10.0",
        "sourceUrl": source_url,
        "checksum": checksum,
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    # Load existing manifest from the repo root and preserve version history.
    # Only entries for OTHER versions are kept; the current version is replaced/added
    # at the front so Jellyfin always sees the latest release first.
    existing_versions: list = []
    manifest_path = Path("manifest.json")
    if manifest_path.exists():
        try:
            existing_manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            if isinstance(existing_manifest, list) and existing_manifest:
                existing_versions = [
                    v for v in existing_manifest[0].get("versions", [])
                    if v.get("version") != version
                ]
        except (json.JSONDecodeError, KeyError, IndexError):
            existing_versions = []

    # New version goes first; older versions follow in their original order.
    all_versions = [new_version_entry] + existing_versions

    manifest = [
        {
            "guid": "a2b5f3e8-4c1d-4f7a-9e6b-8d0c2f1a3b5e",
            "name": "PostgreSQL Database Provider",
            "description": "Provides PostgreSQL as the Jellyfin database backend via PLUGIN_PROVIDER.",
            "overview": "Use PostgreSQL as Jellyfin's database backend",
            "owner": "BORNIOS",
            "category": "Database",
            "imageUrl": image_url,
            "versions": all_versions,
        }
    ]

    release_dir.mkdir(exist_ok=True)

    (release_dir / "manifest.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    manifest_path.write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


if __name__ == "__main__":
    main()
