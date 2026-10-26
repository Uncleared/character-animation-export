# Character Animation Export

**Choose a folder. Check your clips. Press Export.**

Export Blender character animations and their props straight into Unity. NLA strips supply the ranges; Unity applies import defaults automatically.

## Download and install

Get **CharacterAnimationExport-Prototype-0.2.1.zip** from [Releases](https://github.com/Uncleared/character-animation-export/releases), then extract it.

| Installer inside the bundle | Install in |
| --- | --- |
| CharacterAnimationExport-0.2.1.zip | Blender: Preferences → Add-ons → Install from Disk. Select this inner ZIP and enable it. |
| CharacterAnimationExport-Unity-0.2.1.unitypackage | Unity: Assets → Import Package → Custom Package. |
| AnimationEventEditor-0.2.1.unitypackage | Optional independent Unity event editor. |

## Animator: everyday use

1. Open your character blend. In the 3D View, press **N → Animation Export**.
2. Choose **Export Folder** with the folder button. **Inside Unity's Assets is supported.**
3. Check your clips and press **Export**.

Save the blend to remember the folder and clip choices. Repeat Export whenever motion changes. New/resized strips supply their own ranges. If several rigs exist, choose the character once under Settings.

You get ordinary ClipName.fbx files. AnimationExportData is maintained automatically; you never need to open or import its JSON files. When handing exports to another developer, copy the whole output folder.

## Unity: once per character

Select the output folder and choose **Assets → Animation Import Settings** (also under **Tools → Character Animation Export**). Choose Generic, or Humanoid with the matching Source Avatar.

Future FBXs import normally. **Bake Rotation/Y/XZ into Pose and the full transform/prop mask default to ON**, including new nested props. Defaults and per-clip overrides are tucked under foldouts.

Use the FBX clips directly in your existing Animator or animation system. Exporting does not automatically replace existing game references.

## Events

Use Unity's native **FBX Import Settings → Animations → Events**. Saved event seconds and payloads are preserved on motion updates. If a shortened clip excludes an event, the tool warns and retains the old event data/import bounds for correction, with recovery outside Assets.

Or install the optional event package and open **Tools → Animation Event Editor**. It edits .anim and FBX clips **without the exporter/importer installed**. Save/Discard are explicit; an outdated draft cannot overwrite newer native edits. No export metadata or JSON file is needed.

The preview uses Unity-style navigation: **left/middle drag pans**, **right drag or Alt-left drag orbits**, and **scroll or Alt-right drag zooms**. **F** frames the current pose; **Home** resets the view. Drag the divider for more space. Frame stepping, a frame-number field, speed and looping help place cues precisely. Humanoid preview uses Animator evaluation; previewing does not fire animation events.

<details>
<summary>Props, NLA and advanced options</summary>

Keep authoring NLA tracks. The add-on reads strip ranges and samples the evaluated scene, including body, prop and shape-key motion. FBX NLA Strips and All Actions are OFF; Bake Animation is ON internally. Authored overlaps and extrapolation remain part of the result.

Props below the rig are included recursively. Add an external prop collection under Settings → Objects and Props. Scaling, end bones and action-only source mode are also under Settings; match these to the character model once.

Relative // folders require a saved blend. Renaming a source strip retains its published filename/identity. An explicit Export Name change creates a new FBX and keeps the old one. A copied profile asks once whether to keep existing outputs or become a new character export.

</details>

<details>
<summary>Removal and troubleshooting</summary>

Unchecking/removing a source stops exporting it; existing FBXs and references remain. Delete unwanted assets deliberately through Unity.

- Static prop: include its parent/collection and verify motion evaluates within the chosen range.
- Humanoid: select the matching valid Source Avatar once; names alone cannot identify it safely.
- Ambiguous Generic root: choose Generic Motion Root in the settings Inspector.
- Changed duration: review event cues. Preserving seconds cannot detect changes in the motion's meaning.
- Conflicting filename: choose another name/folder. Unowned FBXs and externally modified outputs are protected.
- Failed/cancelled export: old outputs remain. Failed rollback recovery is kept outside Assets and its location reported.

Armature/object transforms and relative shape keys are supported. Material effects, arbitrary simulations and custom Blender logic do not automatically become FBX animation. Fractional strip boundaries and ambiguous reused-action relinks still need correction. Normal Unity refresh imports files; automatic reconfiguration waits for Edit Mode.

</details>

## Existing projects and development

Version 0.1 libraries remain usable under **Tools → Legacy → Character Animation Library**. [Legacy guide](docs/LEGACY-0.1.md). They are not silently migrated to FBX references.

- [Verification and current limits](docs/VALIDATION.md)
- [Maintenance and release automation](docs/MAINTAINING.md)
- [Changelog](CHANGELOG.md)
- [Report a bug](https://github.com/Uncleared/character-animation-export/issues/new/choose)

Source: src/AnimationExport. Both Unity tools are Editor-only with no game runtime dependency. The example is generated test data. No open-source license has been selected for this prototype.
