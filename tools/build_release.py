"""Build both installers using only Python's standard library; no Unity required."""
import ast
import gzip
import hashlib
import io
import re
import tarfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "AnimationExport"
UNITY_ROOT = "Assets/MainAssets/Runtime/AnimationExport"


def zip_bytes(files):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, contents in sorted(files.items()):
            info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            info.create_system = 3  # Keep ZIP metadata identical on Windows and Linux.
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, contents)
    return output.getvalue()


def version():
    value = (ROOT / "VERSION").read_text().strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?", value):
        raise ValueError("VERSION must contain a semantic version.")
    tree = ast.parse((SOURCE / "Blender~" / "character_animation_export" / "__init__.py").read_text())
    info = next(ast.literal_eval(node.value) for node in tree.body if isinstance(node, ast.Assign)
                and any(isinstance(target, ast.Name) and target.id == "bl_info" for target in node.targets))
    if tuple(map(int, value.split("-")[0].split("."))) != info["version"]:
        raise ValueError("VERSION and Blender bl_info version differ.")
    return value


def unity_package():
    files = {}
    seen = set()
    metadata = [SOURCE.with_suffix(".meta"), SOURCE / "Editor.meta", SOURCE / "QuickStart.txt.meta"]
    metadata += sorted((SOURCE / "Editor").rglob("*.meta"))
    for meta in metadata:
        source = meta.with_suffix("")
        relative = "" if source == SOURCE else "/" + source.relative_to(SOURCE).as_posix()
        asset_path = UNITY_ROOT + relative
        match = re.search(rb"(?m)^guid: ([0-9a-f]{32})\s*$", meta.read_bytes())
        if match is None or match.group(1) in seen:
            raise ValueError("Invalid or duplicated Unity source GUID: " + str(meta))
        guid = match.group(1)
        seen.add(guid)
        prefix = guid.decode() + "/"
        files[prefix + "pathname"] = asset_path.encode()
        files[prefix + "asset.meta"] = meta.read_bytes()
        if source.is_file():
            files[prefix + "asset"] = source.read_bytes()
        elif not source.is_dir():
            raise ValueError("Metadata has no corresponding source: " + str(meta))
    output = io.BytesIO()
    with gzip.GzipFile(fileobj=output, mode="wb", mtime=0, filename="") as compressed:
        with tarfile.open(fileobj=compressed, mode="w") as archive:
            for name, contents in sorted(files.items()):
                info = tarfile.TarInfo(name)
                info.size = len(contents)
                info.mode = 0o644
                info.mtime = 0
                archive.addfile(info, io.BytesIO(contents))
    return output.getvalue()


def build(destination=None):
    release = version()
    destination = Path(destination) if destination else ROOT / "dist"
    destination.mkdir(parents=True, exist_ok=True)
    addon = SOURCE / "Blender~" / "character_animation_export"
    downloads = {
        f"CharacterAnimationExport-{release}.zip": zip_bytes({
            "character_animation_export/" + path.name: path.read_bytes()
            for path in sorted(addon.glob("*.py"))}),
        f"CharacterAnimationExport-Unity-{release}.unitypackage": unity_package(),
    }
    bundle = dict(downloads)
    bundle.update({
        "README.md": (ROOT / "README.md").read_bytes(),
        "QuickStart.txt": (SOURCE / "QuickStart.txt").read_bytes(),
        "ExampleCharacter.blend": (ROOT / "examples" / "ExampleCharacter.blend").read_bytes(),
        "CHANGELOG.md": (ROOT / "CHANGELOG.md").read_bytes(),
    })
    downloads[f"CharacterAnimationExport-Prototype-{release}.zip"] = zip_bytes(bundle)
    hashes = "".join(hashlib.sha256(contents).hexdigest() + "  " + name + "\n"
                     for name, contents in sorted(downloads.items()))
    downloads["SHA256SUMS.txt"] = hashes.encode()
    for name, contents in downloads.items():
        (destination / name).write_bytes(contents)
    return destination, downloads


if __name__ == "__main__":
    folder, files = build()
    print("Built " + version() + " in " + str(folder))
    for name, data in sorted(files.items()):
        print(name + " (" + str(len(data)) + " bytes)")
