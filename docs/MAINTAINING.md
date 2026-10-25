# Maintenance

The repository is the source for future versions. Release installers are built from src/AnimationExport. Keep .meta files with Unity source so updates retain their identities.

## Packaging

Use Python 3.12 or newer; no packages are needed.

```shell
python -m unittest discover -s tests -v
python tools/build_release.py
```

dist contains the Blender add-on, Unity importer/legacy package, independent optional event-editor package, complete bundle and checksums. The example is generated geometry. Test code and fixture data are excluded from installers.

## Native checks

Run the direct regression script with a supported Blender executable:

```shell
blender --background --factory-startup --disable-autoexec --python-exit-code 1 --python "src/AnimationExport/Blender~/tests/TestDirectExport.py"
```

The final DIRECT_EXPORT_TEST_REPORT line gives the generated Fixtures folder. Prepare a **new disposable** Unity project outside a game project's Assets folder:

```shell
python tools/prepare_unity_validation.py /path/to/NewValidationProject --fixtures /path/from/report/Fixtures
Unity -batchmode -nographics -projectPath /path/to/NewValidationProject -executeMethod DirectSmoke.Run -logFile /path/to/direct.log
```

Wait for Unity to exit and inspect result.json; the process launching successfully is not a passing result. The 38-assertion semantic test is intended to run once per newly prepared project. In that project, run AutomaticDirectSmoke.Run in a second Unity session to test actual automatic callbacks; inspect automatic-result.json. These entry points exit Unity and require the helper's disposable-project marker.

For event-only installation, prepare another new project:

```shell
python tools/prepare_unity_validation.py /path/to/NewEventValidation --mode events
Unity -batchmode -nographics -projectPath /path/to/NewEventValidation -executeMethod StandaloneEventSmoke.Run -logFile /path/to/events.log
```

Inspect result.json. Also trial the built installers in a fresh project before release; source installation alone does not prove installer completeness.

Run the retained Blender legacy regression with TestBlenderPrototype.py in the same tests folder. For native legacy tests, put the full src/AnimationExport under a disposable Unity project's Assets, including Blender~ and Tests. With Unity Test Framework installed, run CharacterAnimationExport.Tests.AnimationExportTests in EditMode.

After motion/import changes, check a representative copied-Avatar Humanoid and nested prop. Check event seconds and payloads, new-child masks, source/clip identities, scale and existing references. Verify the actual UI with the animator.

## Import performance

Do not add model preprocess callbacks or GetVersion casually: they can invalidate all FBX caches even with a path filter. Keep configuration scoped to owned metadata, compare settings before SaveAndReimport, and require convergence. Stage complete source changes and validate them in a small project before installing into a large game. Do not refresh/reimport a whole project just to test packaging or documentation.

## Publish

1. Update VERSION, add-on bl_info/version, CHANGELOG and README download examples.
2. Run packaging and relevant native checks; record precise results and remaining limits.
3. Commit and push main.
4. Create and push the matching new tag, such as v0.2.1.
5. The Release workflow validates the tag/version, tests, builds, uploads assets to a draft and publishes it. Versions beginning with 0. and versions with a suffix are prereleases.

Never move a published tag. Inspect failed release logs/drafts before retrying. Verify public download hashes after publication.

## Repository rules

- Keep production characters, game prefabs, user export folders, credentials, machine paths and local recovery backups out of Git.
- Keep the public example free of external libraries.
- Both tools are Editor-only; clips require no custom runtime framework.
- Passing packaging checks is not a native animation or in-game test.
- No open-source license has been selected; the maintainer decides separately.
