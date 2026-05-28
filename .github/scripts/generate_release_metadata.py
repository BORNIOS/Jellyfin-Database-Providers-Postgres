import hashlib
import json
import os
import re
from datetime import datetime, timezone
from pathlib import Path


def _extract_scalar(text: str, key: str) -> str:
    match = re.search(rf'^{re.escape(key)}:\s*"?([^"\n]+)"?\s*$', text, flags=re.MULTILINE)
    if not match:
        raise ValueError(f"Missing '{key}' in build.yaml")
    return match.group(1).strip()


def _extract_block(text: str, key: str) -> str:
    pattern = rf'^{re.escape(key)}:\s*>\s*$'
    match = re.search(pattern, text, flags=re.MULTILINE)
    if not match:
        raise ValueError(f"Missing block '{key}: >' in build.yaml")

    lines = text[match.end():].splitlines()
    block_lines: list[str] = []
    for line in lines:
        if not line.strip():
            if block_lines:
                break
            continue

        if line.startswith("  "):
            block_lines.append(line.strip())
            continue

        break

    if not block_lines:
        raise ValueError(f"Block '{key}' is empty in build.yaml")

    return " ".join(block_lines)


def load_build_metadata(path: Path) -> dict:
    text = path.read_text(encoding="utf-8")
    return {
        "name": _extract_scalar(text, "name"),
        "guid": _extract_scalar(text, "guid"),
        "targetAbi": _extract_scalar(text, "targetAbi"),
        "overview": _extract_scalar(text, "overview"),
        "owner": _extract_scalar(text, "owner"),
        "category": _extract_scalar(text, "category"),
        "description": _extract_block(text, "description"),
    }


def main() -> None:
    version = os.environ.get("VERSION", "0.0.0")
    if version.startswith("v"):
        version = version[1:]

    build_meta = load_build_metadata(Path("build.yaml"))

    asset_name = "Jellyfin.Database.Providers.Postgres.zip"
    source_url = (
        "https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/"
        f"releases/download/v{version}/{asset_name}"
    )
    image_url = (
        "https://raw.githubusercontent.com/BORNIOS/Jellyfin-Database-Providers-Postgres/"
        f"v{version}/Jellyfin.Database.Providers.Postgres/Resources/logo.png"
    )
    release_dir = Path("release")
    zip_path = release_dir / asset_name

    if not zip_path.exists():
        raise FileNotFoundError(f"Missing release asset: {zip_path}")

    checksum = hashlib.md5(zip_path.read_bytes()).hexdigest()

    new_version_entry = {
        "version": version,
        "changelog": "",
        "targetAbi": build_meta["targetAbi"],
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
            "guid": build_meta["guid"],
            "name": build_meta["name"],
            "description": build_meta["description"],
            "overview": build_meta["overview"],
            "owner": build_meta["owner"],
            "category": build_meta["category"],
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
