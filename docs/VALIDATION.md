# Verification for 0.3.0

Tested on Windows with Blender 5.0.1, Blender 5.2.1 LTS and Unity 6000.1.7f1.

| Check | Observed result |
| --- | --- |
| Combined Blender export | 30 checks on each supported version: stable takes, automatic ranges, add/remove/restore/reorder, exact removal consent, cancellation/Undo cleanup, file ownership, layout/name history and 200 takes. |
| Fractional endpoint regression | 110 checks on each Blender version: 100 native sample grids and five FBX exports verify final key times and take inventory, including negative and large starting frames. |
| Artist iteration | 20 focused checks on each version: unsaved identity recovery, reverted rename state, lost setup recovery, alternate-source replacement, single-user actions, shared-action track rename, active-action guard, overlap warning, fractional scaled ranges with exact final sample and combined-to-separate rename. |
| Existing Separate export | 30 checks on each Blender version, including native folder persistence, failed/cancelled rename, file locks and original output protection. |
| Real Blender UI | Double-click guard, modal export, native Undo cancellation before cached IDs are freed, clean hooks and subsequent export passed in both versions. The guide shows the actual generated-example panel. |
| Unity Generic combined | 135 checks: body/prop/face sampling against Separate; native event seconds/payloads, GUID/local IDs, Animator references and settings through clip lifecycle; new-child/full masks, custom per-take masks, selected-take independent event editing, shortened-event recovery and 200 takes. Unchanged reconciliation requests zero reimports. |
| Artist recovery in Unity | 13 checks before restart and 12 after: recovered Blender IDs, alternate-source identity/events, exact fractional durations, removed/readded clips, prefab-child event payload, loop/custom curves and surviving references. Deleting a payload while a clip is retired warns without blocking the other clips. |
| Existing Separate Unity | 49 native contract checks passed again on the final importer. |
| Upgrade import cost | Two ordinary 0.2.3 outputs upgrade with zero metadata rewrites and zero native model-import callbacks. A real settings change still migrates and preserves native event seconds/payloads. |
| Folder relocation | Moving the whole folder through Unity preserves identities, settings and native events for combined (nine clips/16 events) and separate outputs. |
| Representative Humanoid | 490 checks across eight temporary source configurations. All nine clips retain the 64 expected facial channels and lid/lever curves, and all 16 native events survive. A neutral extra source overlay channel is retained. Maximum sampled body/prop rotation difference was zero; largest position difference was 0.081mm and facial weight difference about 0.0011. The lighter lid opened about 132 degrees in native Animator evaluation. |
| Actual game controller | All nine product-generated combined clips played through the live character controller. Beer/packet/cigarette/lighter spawning, flame/smoke events and lid/lever motion passed. Captured game camera showed the expected facing; the lid opened about 137.75 degrees during gameplay. The final exporter passed all 490 Humanoid checks again, and all 22,068 animation curve payloads match the FBX used in that game proof. The source blend, prefab and authored scenes were restored to their pre-test hashes. |
| Scale | 200 short generated takes exported/imported successfully in one approximately 4.37MB FBX. The generated-rig import metadata was approximately 478KB. These are test-fixture measurements, not estimates for 200 production animations or player build size. |
| Distribution | Five reproducibility/source/metadata/independent-installer checks passed. The actual Blender ZIP exported two takes after isolated extraction. The actual Unity package imported into a fresh project, configured two takes and converged; the optional event editor remained absent. Initial hierarchy setup uses at most two configuration passes; subsequent unchanged checks request zero reimports. |

These are desktop Editor checks, not headset measurements or final artist acceptance. Production characters, gameplay assets, local evidence and recovery data are not distributed.

## Behavior and limits

- Native event seconds and payloads survive motion updates and moving complete strips. The tool cannot infer where a cue should move when the gesture itself is retimed. A shortened take cannot restore motion beyond its new end; the old event data and bounds remain available for correction.
- Readding the same identity restores saved native event/settings data from the ordinary FBX metadata. Deleted payload assets become empty references with a warning. Full recovery backups remain outside Assets.
- Unsaved-state identity recovery requires a unique published source binding and name. Ambiguous source changes require an explicit Replace Source choice; the exporter does not guess which game reference to replace.
- Removed names are protected against accidental rebinding. Use Replace Source to keep the original identity when choosing a different take. Explicit Export Name changes create new identities.
- Combined contains checked clips. Separate leaves unchecked files on disk. Changing filename/layout/folder preserves old files but does not migrate game references.
- NLA layers, overlap and extrapolation are sampled as authored. Overlaps warn; an active rig action or NLA tweak/solo state must be resolved before export. Prop transform animation and relative shape keys are supported; arbitrary Blender simulations, material effects and custom logic do not automatically become FBX animation.
- Exact fractional NLA ranges are supported in Combined; Separate requires whole-frame boundaries. Combined uses a version-bounded native adapter for the bundled exporters in Blender 5.0 and 5.2.
- Native custom masks are preserved. Custom extra/split native clip definitions fail clearly instead of being overwritten. Concurrent publication from multiple Blender processes is not supported; changed on-disk publications are detected.
- An old 0.2.3 clip already blocked by an out-of-range event may receive one format-only metadata migration/import during upgrade. Its events and bounds remain retained.
- Fractional endpoint regression checks verify final key times; a distinct final-pose-value check at an NLA boundary is not claimed.
- Both Blender and Unity packages must be updated together for combined metadata. GitHub CI checks packaging and Python logic; native Blender/Unity checks are recorded maintenance runs.

Independent Claude Opus 5.5 reviewed the design, artist iteration cases, implementation and focused fixes. Reproduced endpoint and ordinary-upgrade import issues were fixed and tested. Publication was explicitly authorized; these checks do not imply a human artist acceptance test.

Previous behavior and fixes are retained in the [changelog](../CHANGELOG.md). [Maintenance checks](MAINTAINING.md).
