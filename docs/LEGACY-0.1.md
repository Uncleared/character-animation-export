# Character Animation Export

Export selected Blender character animations to Unity without repeatedly setting frame ranges, forgetting animated props, or rebuilding animation events.

**Prototype 0.1.0.** Tested with Blender **5.0.1 / 5.2.1** and Unity **6000.1.7f1**. The tool works with different character names and rigs; it has no game-specific runtime dependency.

## Download and install

Open [Releases](https://github.com/Uncleared/character-animation-export/releases) and download an **Assets** file. GitHub's automatic **Source code** archives are for developers.

| Download | Use |
| --- | --- |
| `CharacterAnimationExport-Prototype-0.1.0.zip` | Everything: both installers, the example character, and guides. Start here. |
| `CharacterAnimationExport-0.1.0.zip` | Blender add-on installer. Install this ZIP without extracting it. |
| `CharacterAnimationExport-Unity-0.1.0.unitypackage` | Unity Editor companion. |
| `SHA256SUMS.txt` | Checksums of release downloads. |

**Blender:** Preferences → Add-ons → menu → **Install from Disk**. Select the Blender add-on ZIP and enable **Character Animation Export**. Do not select the outer all-in-one ZIP. Open the 3D View, press **N**, and choose the **Animation Export** tab.

**Unity:** Assets → Import Package → Custom Package. Select the `.unitypackage`, import its contents, and open **Tools → Character Animation Export**. No runtime component is required.

Already using the original prototype in your project? It is installed. You do not need to import it again merely to follow this guide.

## First export: animator

1. Open a character `.blend`, or the included `ExampleCharacter.blend`.
2. In **Animation Export**, choose the character rig. **Find Character Rig** helps with a single-rig file.
3. Open **Setup**. Choose **NLA Strips** and **All Character Tracks**. Optionally choose an **Objects and Props** collection for props outside the rig hierarchy.
4. Choose an **Export Folder outside the Unity project's Assets folder**. Choose matching model transform settings once per character.
5. Check the clips you want and click **Export Checked Clips**.
6. Save the `.blend` normally to retain the character profile and clip identities.

Each strip on the chosen rig defines a clip range. Moving or resizing that strip updates its range automatically. Object and prop tracks are sampled at the same scene frames. Child props, including a lid nested under another prop, are included recursively.

### Which NLA settings are on?

| Setting | Tool behavior |
| --- | --- |
| Authoring NLA tracks in Blender | Evaluated normally. Keep your character and prop animation here. |
| FBX export **NLA Strips** | **Off**. The tool exports one combined scene bake for each checked clip. |
| FBX export **All Actions** | **Off**. Only checked clips are exported. |
| FBX export **Bake Animation** | **On**. Applied automatically. |
| Scene start/end range | Temporarily derived from each clip and restored afterward. |

The NLA **source mode** and the FBX exporter's **NLA Strips** checkbox are different things. You can use strips to organize and cut animations while leaving the export checkbox off so body and prop motion stay in the same take.

The output is a folder containing `animation-export.json` and `clips/*.fbx`. Keep that folder together. UUID filenames are intentional; friendly clip names appear in Unity.

## First import: Unity

1. Open **Tools → Character Animation Export** and click **New Library**. Save it in a dedicated character folder.
2. Open **Setup and automatic import defaults**. Choose **Humanoid** with the matching model's **Source Avatar**, or **Generic**. Generic usually detects its motion root; enter a motion-node path once if the hierarchy is ambiguous.
3. Click **Import Animation Package…** and select the exported `animation-export.json`.
4. Select a clip in the focused list. Scrub **Preview Time** or play the isolated preview.
5. Use the generated native `.anim` clips in your Animator, animation system, or prefab when you are ready. Import alone does not replace existing game references.

Defaults are on: **Bake Root Rotation, Y and XZ into Pose**, and **include every transform in the animation mask**. New prop children are included on subsequent managed imports. Per-clip root and custom-mask overrides are available.

The library owns a sibling **Generated** folder. Keep the library and its generated assets in their original location in this prototype.

## Edit events and update motion

- Scrub to the cue, choose **Add Event at Preview Time**, enter the function and parameters, then **Save Events**.
- **Copy Events From** copies Unity animation events from an existing clip into an editable draft. Review and save it.
- Event edits save the native clip and do **not** reimport its FBX.
- Re-export from Blender, then choose **Update from Last Package** in Unity.
- Existing native GUIDs, gameplay names, Unity events and payload object references are preserved.
- If duration changes, choose **Keep Event Seconds** or **Scale Events with Duration**. Events outside the new duration block the update until resolved.

For example, a cue at 1 second stays at 1 second with **Keep Event Seconds**. If a 2-second clip becomes 4 seconds, **Scale Events with Duration** moves that cue to 2 seconds. A cue can still need human review when the motion changes without changing duration.

Animancer transition events are separate normalized-time data. The tool reports those dependencies and does not edit them.

## Remove and rename clips safely

| Action | Result |
| --- | --- |
| Uncheck a clip in Blender | Exclude it from this export; existing Unity clips remain. |
| Remove a source strip/action | Mark it removed; Unity does not silently delete its assets. |
| Archive in Unity | Hide it while keeping references. Show archived/deleted to restore it. |
| Rename a Blender clip | Keep its stable identity. Unity asks about retaining the existing gameplay name. |
| Replace selected references | Recheck and change only the selected serialized references; retain transition settings and other fields. |
| Delete unused clip | Require a completed usage scan and review. Referenced clips are blocked. |
| Restore deleted clip | Restore backed-up files and original identity if the destination is free. |

Recovery copies are kept outside `Assets` in `.animation-export-recovery` at the Unity project root. Do not delete those if you still need recovery. An old export package does not automatically resurrect a deleted clip.

## Troubleshooting

- **A prop is missing or static:** include its parent/root in **Objects and Props**, verify that its source track evaluates within the character strip's range, and check missing linked-library warnings. Unavailable source data cannot be exported.
- **Wrong size or hierarchy:** match the character model's scale and end-bone settings. The tool does not automatically rescale or rekey the rig.
- **Clip names differ:** incoming display names and gameplay names are deliberately separate. Use the rename review or keep the existing gameplay name.
- **Export failed/cancelled:** the previous published package remains available. Cancellation occurs between clip bakes.
- **Usage scan takes time:** scanning a large Unity project can take minutes. It shows progress and can be cancelled; an incomplete scan does not permit deletion.
- **Blender Add-on button in Unity:** the button locates a ZIP in a full source installation. With only the Unity `.unitypackage` installed, download the Blender ZIP directly from Releases.

## Prototype limits

Armature/object transforms and relative shape keys are supported. Arbitrary Blender material effects, simulations and custom behavior do not automatically become FBX animation. Fractional strip boundaries and ambiguous reused-action relinks need explicit correction/review. Actions mode is available for action-only files; prop tracks are evaluated at those actions' source frames.

Native editable clips can be large. A representative 15.87-second Humanoid sample produced a **21.1 MB `.anim`** and **12.7 MB FBX**; event saving took about **1.48 seconds**, compared with about **6.43 seconds** for first import on the test project. These are observations, not guarantees.

## Development and support

- [Maintenance and releases](docs/MAINTAINING.md)
- [Verification and limits](docs/VALIDATION.md)
- [Changelog](CHANGELOG.md)
- [Report a bug](https://github.com/Uncleared/character-animation-export/issues/new/choose)

Source lives in `src/AnimationExport`. The Blender add-on is under `Blender~/character_animation_export`; Unity ignores `Blender~`. `Editor` contains the Unity companion. `Tests` and generated fixtures are for development, and are excluded from the normal Unity installer.

No open-source license has been selected for this prototype.
