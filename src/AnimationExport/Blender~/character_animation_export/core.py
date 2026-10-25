"""Package validation and publishing shared by the add-on and its tests."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
import uuid

SCHEMA_VERSION = 1


def valid_id(value):
    return bool(re.fullmatch(r"[0-9a-f]{32}", value or ""))


def digest(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def export_root(path):
    root = Path(path).expanduser().resolve()
    for ancestor in (root, *root.parents):
        if ancestor.name.lower() == "assets" and (ancestor.parent / "ProjectSettings").is_dir():
            raise ValueError("Choose an export folder outside Unity's Assets folder. Import the package with Animation Export in Unity.")
    if root.exists() and not root.is_dir():
        raise ValueError("The destination must be a folder.")
    return root


def safe_file(root, relative):
    root = Path(root).resolve()
    if not relative or "\\" in relative or ":" in relative or Path(relative).is_absolute():
        raise ValueError("Invalid relative package filename.")
    candidate = (root / relative).resolve()
    if not candidate.is_relative_to(root) or candidate == root:
        raise ValueError("Package filename escapes its folder.")
    return candidate


def validate_names(records):
    seen = set()
    for item in records:
        if item.retired or item.state != "READY":
            continue
        name = item.export_name.strip()
        if not name or name != item.export_name or any(c in name for c in '<>:"/\\|?*') or any(ord(c) < 32 for c in name) or name[-1:] in (".", " ") or re.match(r'^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)', name, re.I):
            raise ValueError(f"Give '{item.source_name}' a non-empty export name without filename characters.")
        if name.casefold() in seen:
            raise ValueError(f"Duplicate export name '{name}'. Rename one clip.")
        seen.add(name.casefold())


def publish(root, manifest, staged_files):
    """Publish only after all bakes succeed, with rollback if a replacement fails."""
    root = export_root(root)
    root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".animation-publish-", dir=root) as working:
        working = Path(working)
        staged_manifest = working / "manifest.json"
        staged_manifest.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        replacements = list(staged_files.items()) + [("animation-export.json", staged_manifest)]
        backups = []
        try:
            for index, (relative, source) in enumerate(replacements):
                destination = safe_file(root, relative)
                destination.parent.mkdir(parents=True, exist_ok=True)
                previous = working / f"backup-{index}"
                existed = destination.exists()
                if existed:
                    shutil.copy2(destination, previous)
                backups.append((destination, previous, existed))
                os.replace(source, destination)
        except BaseException:
            for destination, previous, existed in reversed(backups):
                if existed:
                    os.replace(previous, destination)
                elif destination.exists():
                    destination.unlink()
            raise


def new_id():
    return uuid.uuid4().hex
