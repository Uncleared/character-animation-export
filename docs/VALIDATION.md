# Verification for 0.1.0

The original prototype was checked on Windows with Blender 5.0.1, Blender 5.2.1 LTS and Unity 6000.1.7f1.

| Check | Observed result |
| --- | --- |
| Blender exporter suite | 49 checks passed on each tested Blender version. |
| Unity native EditMode suite | 14/14 passed after fixes; final run 35.56 seconds. |
| Different generated rigs | Nested prop rotation and relative shape keys present; expected coordinate conversion and translation magnitude preserved at scales 0.01, 1 and 2.5. |
| Import settings | Root bake defaults, explicit overrides and all-transform masks checked, including a newly added child. |
| Event refresh | GUID, name, loop/mirror settings and full event payloads retained; event edits left FBX bytes and import dependency unchanged. |
| Duration changes | Pending review keeps the old native motion; explicit scaling applies the new duration and cue times. |
| Clip lifecycle | Used deletion blocked; selected replacement retained other transition fields; old packages did not resurrect deleted clips; occupied recovery blocked; free recovery and rename preserved GUIDs. |
| Representative Humanoid | A nested lid opened about 139 degrees; lid/lever curves and all 12 original Unity events survived native copied-Avatar import and refresh. |
| Unity UI | Library list, event fields and isolated preview rendered. |
| Player assembly inventory | Companion and test assemblies excluded from runtime assemblies. |

The representative Humanoid and its game-specific files are not distributed. The repository includes original generated geometry and fixtures instead.

The first native checks found and corrected an FBX take-origin slicing issue and Unity's cached path lookup for deleted GUIDs. Generic fixtures use an explicit motion root because they deliberately contain no skinned rig mesh. The final source retains ambiguous-root checks rather than guessing from unrelated prop roots.

Native `.anim` size is still a limitation: the representative file was about 21.1 MB, compared with a 12.7 MB FBX. Full-project reference scans can take minutes. Material animation, simulations, arbitrary Blender-only behavior, other app versions and final animator/gameplay usability are not covered by these results.

GitHub CI verifies the distribution and pure Python logic only. The prototype's final independent implementation review and human usability acceptance were still pending when 0.1.0 was published as a prerelease.
