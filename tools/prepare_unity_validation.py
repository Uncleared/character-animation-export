"""Create a NEW disposable Unity project for native checks; never edit a game project."""
import argparse
import json
import shutil
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]


def prepare(destination, fixtures=None, mode="direct", unity_version="6000.1.7f1"):
    destination = Path(destination).expanduser().resolve()
    if destination.exists() and any(destination.iterdir()):
        raise ValueError("Choose a new empty directory for the disposable validation project.")
    if any(part.lower() == "assets" for part in destination.parts):
        raise ValueError("Do not create a validation project inside Assets.")
    fixture_source = None
    if mode == "direct":
        if fixtures is None:
            raise ValueError("Direct checks need --fixtures from TestDirectExport.py's report.")
        fixture_source = Path(fixtures).expanduser().resolve()
        for name in ("Base", "Extended", "NewChild", "Short"):
            if not (fixture_source / name).is_dir():
                raise ValueError("Missing generated fixture folder: " + name)

    destination.mkdir(parents=True, exist_ok=True)
    (destination / "character-animation-export-validation.json").write_text(
        json.dumps({"disposable": True, "mode": mode}, indent=2), encoding="utf-8"
    )
    (destination / "Packages").mkdir()
    modules = ("animation", "audio", "imageconversion", "imgui", "jsonserialize", "physics", "ui", "uielements")
    (destination / "Packages/manifest.json").write_text(
        json.dumps({"dependencies": {"com.unity.modules." + module: "1.0.0" for module in modules}}, indent=2),
        encoding="utf-8",
    )
    (destination / "ProjectSettings").mkdir()
    (destination / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: " + unity_version + "\n", encoding="utf-8")
    source = REPO / "src/AnimationExport/Editor"
    target = destination / "Assets/AnimationExport/Editor"
    if mode == "direct":
        shutil.copytree(source, target, ignore=shutil.ignore_patterns("__pycache__"))
        shutil.copytree(fixture_source, destination / "ExternalFixtures")
        scripts = ("DirectSmoke.cs", "AutomaticDirectSmoke.cs")
    else:
        shutil.copytree(source / "Events", target / "Events")
        if mode == "preview":
            shutil.copyfile(REPO / "tools/validation/EventPreviewSmoke.cs", target / "Events/EventPreviewSmoke.cs")
            scripts = ()
        else:
            scripts = ("StandaloneEventSmoke.cs",)
    editor = destination / "Assets/Editor"
    editor.mkdir(parents=True, exist_ok=True)
    for name in scripts:
        shutil.copyfile(REPO / "tools/validation" / name, editor / name)
    return destination


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--fixtures", type=Path)
    parser.add_argument("--mode", choices=("direct", "events", "preview"), default="direct")
    parser.add_argument("--unity-version", default="6000.1.7f1")
    args = parser.parse_args()
    print(prepare(args.destination, args.fixtures, args.mode, args.unity_version))
