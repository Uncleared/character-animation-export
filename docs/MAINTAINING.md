# Maintenance

The repository is the source for future versions. The release ZIPs are built from `src/AnimationExport`, rather than copied from an unrelated Unity project. Keep `.meta` files with Unity source files so updates retain their identities.

## Local packaging checks

Use Python 3.12 or newer. No Python packages are needed.

```shell
python -m unittest discover -s tests -v
python tools/build_release.py
```

Downloads appear in `dist/`: Blender add-on ZIP, Unity package, complete bundle and checksums. The example is generated geometry, not a production character.

## Native application checks

CI tests packaging and the pure Python package logic. It does not replace native Blender or Unity checks.

Run the Blender regression script with a supported Blender executable:

```shell
blender --background --factory-startup --disable-autoexec --python-exit-code 1 --python "src/AnimationExport/Blender~/tests/TestBlenderPrototype.py"
```

Put the full `src/AnimationExport` folder under a disposable Unity project's `Assets` folder, including its `Blender~` fixtures and `Tests`. Open the Unity Test Runner and run `CharacterAnimationExport.Tests.AnimationExportTests` in EditMode. The test project needs Unity Test Framework. The release installer intentionally excludes this test code/data.

After motion/import changes, also check a representative copied-Avatar Humanoid and animated nested prop. Check exact event times and payloads, new-child mask inclusion, motion size/scale, and rename/delete/recovery behavior. Trial installation from the built artifacts before release.

## Publish a version

1. Update `VERSION`, the add-on's `bl_info["version"]`, `CHANGELOG.md`, and versioned README download examples.
2. Run packaging checks and the relevant native tests. Record what ran in the release notes.
3. Commit and push `main`.
4. Create and push the matching version tag, for example:

   ```shell
   git tag v0.1.1
   git push origin v0.1.1
   ```

5. The **Release** GitHub Actions workflow validates the tag/version, runs checks, builds downloads, creates a draft release, attaches files, then publishes it. Versions beginning with `0.` and versions containing a suffix are marked prereleases.

Never move an existing published tag to different code. Fix issues in a new version. If a release job fails after creating its draft, inspect the draft and logs rather than deleting/replacing a published release automatically.

## Repository rules

- Keep production characters, game prefabs, user export folders, credentials and local recovery backups out of this repository.
- Keep the public example free of external libraries and machine-specific export paths.
- Keep the tool Editor-only; it should emit ordinary clips without requiring a new runtime framework.
- Describe validation honestly. Passing packaging checks is not a native animation or in-game test.
- No open-source license has been selected yet; the maintainer decides that separately.
