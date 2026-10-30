# Character Animation Export

**Folder → Combined FBX → Check clips → Export.**

Export Blender character animations into Unity, including facial and animated prop motion. NLA strips supply the clip ranges automatically.

## Install

Download and extract the [**0.4.1 bundle**](https://github.com/Uncleared/character-animation-export/releases/download/v0.4.1/CharacterAnimationExport-Prototype-0.4.1.zip).

- **Blender:** Preferences → Add-ons → Install from Disk. Install the inner **CharacterAnimationExport-0.4.1.zip** and enable it.
- **Unity:** Assets → Import Package → Custom Package. Install **CharacterAnimationExport-Unity-0.4.1.unitypackage**.

**Already on 0.4.0?** Only update the Unity importer for this fix. No re-export is needed.

**Upgrading from 0.3 or older? Update both packages together.** Combined and Lightweight are supported on Blender 5.0 and 5.2.

0.4.1 fixes repeated animation processing after unrelated prefab/scene saves. A startup check after opening Unity or recompiling scripts is still normal.

## Export one combined FBX

1. In Blender’s 3D View, press **N → Animation Export**.
2. Pick your **Folder** inside the Unity project’s **Assets** folder.
3. Choose **Combined FBX**, set **File Name** once, check the clips, and press **Export**.

![Combined FBX controls in Blender](docs/images/blender-combined.png)

Save the blend to remember your choices. After that, just press **Export**. Add, move or resize NLA strips normally; no manual start/end ranges or Unity clip cutting. Body, facial animation and props share one FBX with geometry included once.

Unchecking or removing a clip asks once before removing it from the combined FBX. Surviving clips keep their references and events; rechecking the same clip restores its saved events/settings. Deliberately removed clips need their game references updated.

To use an alternate take **without changing a published clip’s identity**, uncheck the alternate, open **Settings**, and use the original clip’s **link button → Replace Source**. Renaming a source strip also keeps its published identity.

Changing layout or File Name creates separate outputs; update game references when switching.

## Smaller separate FBXs

Choose **Separate FBXs → Lightweight → Export**. To convert existing separate exports, keep the same **Folder**. Events and clip references stay attached. Uncheck **Lightweight** and export to restore full geometry.

Lightweight keeps body, facial and prop animation with small mesh carriers. **Use the full character prefab for rendering and preview**; drag it into Unity’s preview or the event editor’s **Preview Model**. Keep display-model assets separate from animation exports.

Only checked clips update. Unchecked files stay as they are; a brief hint identifies remaining Full or Lightweight files. Combined export still includes geometry once.

Measured on 200 generated character clips: **1.58 GB → 0.80 GB (49.1% smaller)**. [Size comparison](docs/BENCHMARKS.md).

## Unity setup, once per character

Select the export folder → **Assets → Animation Import Settings**. Choose **Generic**, or **Humanoid** and its matching **Source Avatar**.

Future exports import automatically with **Bake Rotation/Y/XZ**, **full prop masks**, **Original Facing** and **Original XZ Origin** enabled by default. Native custom masks and per-clip overrides are preserved. Use the imported clips in your existing animation system.

**Animation Import Settings** also defaults to **Optimal** compression with **0.01 Rotation Error**, **0.01 Position Error** and **0.5 Scale Error**. **Loop Idle Clips** enables Loop Time for Idle names, ignoring case (including `StandingIdle` and `Idle.001`). Use **Clip Overrides → Override Loop / Loop Time** for exceptions such as a one-shot `IdleToSit`. Turning either automation off keeps its current values and allows native Unity editing. Loop Pose stays as authored.

Compression uses Unity's native compressor and can change interpolation between baked frames. Choose **Off** when you need uncompressed curves.

**Upgrading from 0.3:** existing folders also receive these compression and Idle-loop defaults on installation. Differing settings cause a reimport and replace native compression/Idle-loop choices. Disable **Set Compression** or **Loop Idle Clips** before making native edits you want to keep.

## Events

Continue using Unity’s native **FBX → Animations → Events**. Updates preserve event seconds and payloads. Shortening past an event warns and retains its previous data for correction. Moving a whole strip does not shift its local event times; retiming the gesture itself may require reviewing its cues.

The optional **AnimationEventEditor-0.4.1.unitypackage** provides **Tools → Animation Event Editor**, independently of this exporter. Its preview supports pan, orbit, zoom and frame stepping, and remembers your chosen prefab when switching clips. [Preview controls](docs/EVENTS.md).

## When experimenting

- **Save As:** use **Keep Existing Outputs** to continue, or **New Character Export** for a different character. These buttons appear when needed.
- **Layered tracks:** overlaps are baked as authored and reported; mute an alternate track if you do not want it included. Push Down or unlink an active rig action before NLA export.
- **Props outside the rig:** choose their collection under **Settings → Objects and Props**.
- **Handing files over:** copy the whole output folder. Its automatic support data stays beside the FBX; there is no JSON workflow to manage.
- **Moving the export folder:** move the whole folder in Unity's Project window, then choose its new location in Blender. This preserves references and events. Choosing an empty folder in Blender creates new assets; existing game references keep using the old files.
- **Cancel or export failure:** previous files and Unity metadata are preserved.

[Verification and limits](docs/VALIDATION.md) · [Maintenance](docs/MAINTAINING.md) · [Changelog](CHANGELOG.md) · [Report a bug](https://github.com/Uncleared/character-animation-export/issues/new/choose)

Existing 0.1 libraries remain under **Tools → Legacy → Character Animation Library**. [Legacy guide](docs/LEGACY-0.1.md).

The screenshot uses the supplied generated example. Both Unity tools are Editor-only. No open-source license has been selected.
