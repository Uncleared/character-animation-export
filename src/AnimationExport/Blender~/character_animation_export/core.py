"""Package validation and publishing shared by the add-on and its tests."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
import time
import uuid

SCHEMA_VERSION = 1


def valid_id(value):
    return bool(re.fullmatch(r"[0-9a-f]{32}", value or ""))


def digest(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def export_root(path, allow_assets=False):
    root = Path(path).expanduser().resolve()
    for ancestor in (root, *root.parents):
        if not allow_assets and ancestor.name.lower() == "assets" and (ancestor.parent / "ProjectSettings").is_dir():
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


DIRECT_SCHEMA = 2
COMBINED_SCHEMA = 3
DATA_FOLDER = "AnimationExportData"


def metadata_file(profile_id):
    if not valid_id(profile_id):
        raise ValueError("Invalid character export identity.")
    return f"{DATA_FOLDER}/{profile_id}.animation-export.json"


def staging_root(root):
    root = Path(root).resolve()
    for ancestor in (root, *root.parents):
        if ancestor.name.lower() == "assets" and (ancestor.parent / "ProjectSettings").is_dir():
            folder = ancestor.parent / ".animation-export-staging"
            break
    else:
        folder = root.parent / ".animation-export-staging"
    folder.mkdir(parents=True, exist_ok=True)
    return folder


def replace_file(source, destination):
    for attempt in range(6):
        try:
            os.replace(source, destination)
            return
        except PermissionError:
            if attempt == 5:
                raise
            time.sleep(0.05 * (attempt + 1))


def direct_inventory(root):
    records = {}
    folder = Path(root) / DATA_FOLDER
    if not folder.exists():
        return records
    for path in folder.glob("*.animation-export.json"):
        data = json.loads(path.read_text(encoding="utf-8"))
        if data.get("schemaVersion") not in (DIRECT_SCHEMA, COMBINED_SCHEMA) or not valid_id(data.get("profileId")):
            raise ValueError(f"Invalid export metadata: {path.name}")
        rows = data.get('outputs', []) if data.get('schemaVersion') == COMBINED_SCHEMA else data.get('clips', [])
        for row in rows:
            filename = row.get("file", "")
            if not filename and not row.get('exported'):
                continue
            if Path(filename).name != filename or not filename.lower().endswith('.fbx'):
                raise ValueError("Invalid FBX ownership record.")
            key = filename.casefold()
            owner = (data['profileId'], row.get('id'))
            if key in records and records[key][0] != owner:
                raise ValueError(f"Conflicting ownership for {filename}.")
            records[key] = (owner, row)
    return records


def validate_direct_targets(root, profile_id, rows):
    root = Path(root)
    inventory = direct_inventory(root)
    existing = {p.name.casefold(): p for p in root.iterdir()} if root.is_dir() else {}
    for clip_id, name in rows:
        filename = name + '.fbx'
        key = filename.casefold()
        owner = inventory.get(key)
        if owner and owner[0] != (profile_id, clip_id):
            if clip_id == profile_id:
                raise ValueError(f"Another character already uses File Name '{name}'. Choose a different File Name.")
            raise ValueError(f"{filename} belongs to another clip. Choose another export name.")
        target = existing.get(key)
        meta = existing.get((filename + '.meta').casefold())
        if (target or meta) and not owner:
            raise ValueError(f"{filename} already exists and is not owned by this export. Choose another name or folder.")
        if target and target.name != filename:
            raise ValueError(f"Use the existing filename spelling: {target.name}")
        if target and owner and digest(target) != owner[1].get('sha256'):
            raise ValueError(f"{filename} changed outside this exporter. Keep that version or choose another export name.")


def publish_direct(root, manifest, staged_files):
    """Publish complete same-volume files, preserving Unity metadata and failed recovery."""
    root = export_root(root, allow_assets=True)
    root.mkdir(parents=True, exist_ok=True)
    validate_direct_targets(root, manifest['profileId'], publication_targets(manifest, staged_files))
    work = Path(tempfile.mkdtemp(prefix='publish-', dir=staging_root(root)))
    replacements = []
    backups = []
    keep_recovery = False
    try:
        meta_relative = metadata_file(manifest['profileId'])
        prepared = work / 'metadata.json'
        prepared.write_text(json.dumps(manifest, indent=2), encoding='utf-8')
        # Ownership and ranges exist before Unity can observe any new FBX.
        replacements.append((meta_relative, prepared))
        for index, (relative, source) in enumerate(staged_files.items()):
            local = work / f'new-{index}.fbx'
            shutil.copyfile(source, local)
            replacements.append((relative, local))
        for index, (relative, source) in enumerate(replacements):
            target = safe_file(root, relative)
            target.parent.mkdir(parents=True, exist_ok=True)
            previous = work / f'old-{index}'
            existed = target.exists()
            old_hash = digest(target) if existed else None
            if existed:
                shutil.copy2(target, previous)
            backups.append((target, previous, existed, old_hash))
            replace_file(source, target)
    except BaseException as error:
        failures = []
        for target, previous, existed, old_hash in reversed(backups):
            try:
                if existed:
                    if not target.exists() or digest(target) != old_hash:
                        replace_file(previous, target)
                elif target.exists():
                    target.unlink()
            except OSError as restore_error:
                failures.append(f'{target.name}: {restore_error}')
        if failures:
            keep_recovery = True
            (work/'RECOVERY.json').write_text(json.dumps({
                'error': str(error), 'failedRestores': failures,
                'files': [{'destination': str(p), 'backup': str(b), 'existed': e, 'sha256': h}
                          for p,b,e,h in backups]}, indent=2), encoding='utf-8')
            raise OSError(f'Export failed. Recovery files kept at {work}: ' + '; '.join(failures)) from error
        raise
    finally:
        if not keep_recovery:
            shutil.rmtree(work)


def publication_targets(manifest, staged_files):
    rows = manifest.get('outputs', []) if manifest.get('schemaVersion') == COMBINED_SCHEMA else manifest['clips']
    result = []
    for filename in staged_files:
        matches = [row for row in rows if row.get('file') == filename]
        if len(matches) != 1 or not valid_id(matches[0].get('id')):
            raise ValueError('Invalid output ownership for ' + filename)
        result.append((matches[0]['id'], filename[:-4]))
    return result


def output_history(previous):
    if previous.get('schemaVersion') == COMBINED_SCHEMA:
        return {row['file']: dict(row) for row in previous.get('outputs', [])}
    return {row['file']: dict(id=row['id'], file=row['file'], sha256=row['sha256'], kind='separate')
            for row in previous.get('clips', []) if row.get('file')}


def require_unchanged_publication(path, previous):
    current = json.loads(path.read_text(encoding='utf-8')) if path.exists() else {}
    if current != previous:
        raise ValueError('These outputs changed during export. Export again to use the current clip list.')


def removals(previous, filename, checked_ids):
    return [dict(row) for row in previous.get('clips', [])
            if row.get('file') == filename and row['id'] not in checked_ids]
