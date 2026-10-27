# Changelog

## 0.4.0 — Lightweight separate FBXs

- Add a Separate-only Lightweight checkbox that updates existing files in place and can restore Full geometry with the same identities.
- Retain animation curves, prop hierarchies, facial carriers and original skin/bind relationships; validate staged FBXs before publication.
- Preserve valid Generic motion roots and explicitly selected event-preview prefabs across clip/window reloads.
- Identify mixed Full/Lightweight folders without rewriting unchecked files.
- Add folder compression defaults: Optimal, rotation error 0.01, position error 0.01 and scale error 0.5, with native opt-out.
- Enable Loop Time for Idle names, with per-clip exceptions.
- Add native export, migration, animation-parity and measured-size checks.
- Upgrade note: existing managed folders receive the new compression and Idle-loop defaults on installation; differing native settings are replaced and cause a reimport. Disable the corresponding automation to keep subsequent native edits.

## 0.3.0 — Combined FBX

- Export checked clips into one combined FBX with face and prop animation, geometry once, and automatic NLA ranges including fractional boundaries.
- Preserve per-take events, masks, settings and surviving references; restore retired clips across Editor restarts.
- Recover uniquely identifiable unsaved export state and offer direct Continue Existing Export / Save As choices.
- Replace a published clip's source with an unchecked alternate take while keeping its identity.
- Guard active-action contamination, report overlapping tracks, and reject conflicting retired names before publication.
- Preserve old outputs on layout/name changes and preserve published files on cancellation or Undo.
- Share full-mask path data between takes to reduce import metadata growth.
- Include the last sample at fractional ranges that native sampling would otherwise omit.
- Avoid format-only metadata rewrites and model imports for unchanged ordinary 0.2.3 separate outputs.
- Put combined export first in the short illustrated guide, including safe folder relocation.

## 0.2.3

- Gate new rig setup until an explicit choice and valid Humanoid Avatar; preserve existing enum values.
- Prevent interleaved double-click exports and use supported Blender modal timer events.
- Avoid reassigning an unchanged Avatar, which resets native Humanoid scale and causes unnecessary reimports.

- Preserve native Create From This Model mask edits while still including new props in managed masks.
- Keep blocked-event recovery stable across Editor restarts.
- Wait for one-time folder rig setup without mutating every unconfigured FBX.
- Preserve clip identities when a renamed export fails or is cancelled.


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
