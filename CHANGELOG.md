# Changelog

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
