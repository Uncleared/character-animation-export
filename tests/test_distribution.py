import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


packaging = load("release_packaging", ROOT / "tools" / "build_release.py")
core = load("export_core", packaging.SOURCE / "Blender~" / "character_animation_export" / "core.py")


class DistributionTests(unittest.TestCase):
    def test_installers_are_reproducible_and_contain_current_source(self):
        with tempfile.TemporaryDirectory() as folder:
            _, first = packaging.build(folder)
            _, second = packaging.build(folder)
        self.assertEqual(first, second)
        release = packaging.version()
        with zipfile.ZipFile(io.BytesIO(first[f"CharacterAnimationExport-{release}.zip"])) as archive:
            self.assertIsNone(archive.testzip())
            self.assertTrue(all(item.create_system == 3 for item in archive.infolist()))
            self.assertEqual(set(archive.namelist()), {
                "character_animation_export/__init__.py", "character_animation_export/core.py"})
            for name in archive.namelist():
                self.assertEqual(archive.read(name), (packaging.SOURCE / "Blender~" / name).read_bytes())
        with zipfile.ZipFile(io.BytesIO(first[f"CharacterAnimationExport-Prototype-{release}.zip"])) as archive:
            self.assertIn("ExampleCharacter.blend", archive.namelist())
            self.assertIn("README.md", archive.namelist())
            self.assertIsNone(archive.testzip())

    def test_unity_package_keeps_metadata_and_excludes_fixtures(self):
        names = []
        with tarfile.open(fileobj=io.BytesIO(packaging.unity_package()), mode="r:gz") as archive:
            for member in archive.getmembers():
                if member.name.endswith("/pathname"):
                    path = archive.extractfile(member).read().decode()
                    names.append(path)
                    self.assertTrue(path == packaging.UNITY_ROOT or
                                    path.startswith(packaging.UNITY_ROOT + "/Editor") or
                                    path == packaging.UNITY_ROOT + "/QuickStart.txt")
                    prefix = member.name.rsplit("/", 1)[0]
                    self.assertIn(prefix + "/asset.meta", archive.getnames())
                    asset_name = prefix + "/asset"
                    if asset_name in archive.getnames():
                        relative = path[len(packaging.UNITY_ROOT):].lstrip("/")
                        self.assertEqual(archive.extractfile(asset_name).read(),
                                         (packaging.SOURCE / relative).read_bytes())
        self.assertEqual(len(names), len(set(names)))
        self.assertIn(packaging.UNITY_ROOT + "/Editor/AnimationExportImport.cs", names)
        for path in packaging.SOURCE.rglob("*.asmdef"):
            self.assertEqual(json.loads(path.read_text())["includePlatforms"], ["Editor"])

    def test_package_paths_and_unity_destinations_are_guarded(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for invalid in ("../outside.fbx", "C:/outside.fbx", "/outside.fbx", "clips\\outside.fbx"):
                with self.assertRaises(ValueError):
                    core.safe_file(root, invalid)
            (root / "ProjectSettings").mkdir()
            (root / "Assets").mkdir()
            with self.assertRaises(ValueError):
                core.export_root(root / "Assets" / "Exports")

    def test_example_fixture_inventory_has_verified_fbx_files(self):
        fixtures = packaging.SOURCE / "Blender~" / "Fixtures"
        for manifest in fixtures.glob("*/animation-export.json"):
            data = json.loads(manifest.read_text())
            self.assertEqual(data["schemaVersion"], 1)
            self.assertTrue(core.valid_id(data["profileId"]))
            for clip in data["clips"]:
                if clip.get("exported"):
                    path = core.safe_file(manifest.parent, clip["file"])
                    self.assertEqual(core.digest(path), clip["sha256"])


if __name__ == "__main__":
    unittest.main()
