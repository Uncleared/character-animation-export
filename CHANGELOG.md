# Changelog

## 0.2.2 — Authored facing and illustrated guide

- Preserve original root orientation and horizontal origin by default instead of deriving a separate body-facing reference per clip.
- Expose folder and per-clip root-basis controls alongside bake options.
- Enable blend-relative paths in Blender’s folder picker.
- Add real Blender and Unity interface screenshots to the guide.

## 0.2.1 — Preview navigation

- Add persistent pan/orbit/zoom, current-pose framing and view reset.
- Add a resizable preview, frame stepping/seeking, playback speed and loop controls.
- Evaluate Humanoid preview through an isolated Animator graph and matching Avatar; prevent event firing and dispose the graph on close.
- Keep the optional event editor independent of exporter metadata and clarify folder handoff.

## 0.2.0 — 2026-10-26

- Replace everyday package/library steps with direct named FBX export into the chosen folder, including Assets.
- Keep Export Folder, clip selection and Export visible; move setup into Settings.
- Apply scoped Unity bake/mask defaults automatically through delayed post-import processing.
- Preserve event seconds/payloads, clip identities and references; warn and retain out-of-range cues.
- Split Animation Event Editor into an independent optional installer with stale-draft protection.
- Preserve the legacy library workflow and immutable 0.1.0 release.


## 0.1.0 — 2026-10-26

Initial prototype prerelease.

- Export checked character clips using NLA strip ranges or explicit action selection.
- Bake body and nested prop transforms together, with relative shape keys.
- Keep FBX All Actions and NLA Strips off, with animation baking on.
- Preserve scene/frame/selection state and the previous package on cancellation or failure.
- Add a Unity library/import/event/preview companion with automatic root baking and full transform masks.
- Preserve native clip identity, gameplay names, event payloads and loop/mirror settings on update.
- Review changed durations; archive, replace, delete and recover clips with usage checks.
- Include a generated example, guides, source, packaging checks and tagged-release automation.

Prototype usability and representative production workflows still need user feedback. Native clip size remains a known limitation.
