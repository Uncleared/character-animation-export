# Verification for 0.2.3

The earlier 0.2.2 authored-facing fix passed 42 direct Unity semantics checks, including default root basis, opt-outs, per-clip overrides, native event preservation and convergence. A representative nine-clip Humanoid comparison removed facing offsets of approximately 20–24 degrees: sampled Smoking yaw differs by at most 0.041 degrees from the original and the largest sampled difference across nine clips is 0.280 degrees. Full action clips and short idle samples in the game again confirmed spawned prop animation and event effects. These are desktop Editor results; final visual acceptance remains with the animator.

The folder-picker change and direct-export regression passed 24 checks on each of Blender 5.0.1 and 5.2.1 LTS, including saved relative paths and stable export identities.

The preview behavior below is unchanged from 0.2.1.

The preview-navigation revision passed 61 checks in Unity 6000.1.7f1 with real UI mouse events and rendered preview images. Checks cover native-style gestures, capture release outside the preview, stable camera state while scrubbing, framing at three model scales/aspect ratios, frame seeking/stepping, nonlooping playback, and unchanged event/source data. The optional editor is installed without exporter/importer assemblies.

A separate event-only project compared a representative Humanoid preview against an initialized Animator graph: all 51 mapped bones and sampled prop transforms matched, preview event firing was disabled, source events were unchanged, and the graph was cleaned up. The Humanoid Animator is explicitly initialized before sampling so the pose is also correct across later UI frames.

The following export checks remain from 0.2.0; the exporter behavior is unchanged in 0.2.1.

Checked on Windows with Blender 5.0.1, Blender 5.2.1 LTS and Unity 6000.1.7f1. The new direct workflow was exercised in disposable Unity projects before installation in the larger game project.

| Check | Observed result |
| --- | --- |
| Direct Blender export | 23 checks passed on each Blender version: saved folder/identity, friendly files inside Assets, strip ranges/rename, nested props, cancellation, unknown-output protection and transient/permanent file locks. |
| Legacy Blender regression | 49 checks passed on Blender 5.0.1 after the direct-workflow changes. |
| Direct Unity semantics | 38 assertions passed: three bake defaults, full masks/new props, shape keys, exact native event seconds and payloads, loop/mirror preservation, stable GUID/local IDs and Animator/asset references. |
| Automatic Unity callbacks | First import and new-prop update configured themselves without calling reconciliation from the test; metadata then remained stable. |
| Shortened motion | Out-of-range events retained their old data/import bounds, recovery metadata was saved outside Assets, and repeated processing converged. |
| Optional event editor | Native .anim and selected FBX take edits passed, other-take events/settings stayed unchanged, and stale drafts were blocked. A separate project with only the event installer successfully saved a native event. |
| Representative Humanoid | Valid copied Avatar; nested lid/lever rotation curves retained. Sampled lid opened about 132 degrees during the 15.87-second motion. |
| Distribution | Five Python checks passed for reproducible packages, GUIDs, direct Assets support, legacy fixtures and independent event-installer boundaries. |

The representative Humanoid, production assets, Avatar mapping and local recovery data are not distributed. The public example and fixture generator use original generated geometry.

The importer uses post-import callbacks and deferred, targeted configuration. Adding model preprocessing callbacks to the original prototype invalidated the large host project's FBX import cache; those callbacks were removed. An early path filter does not prevent that Unity cache invalidation. The final automatic-import behavior was verified separately in an isolated project.

Native importer event times are normalized internally; the tool explicitly preserves seconds when clip duration changes. This protects timing data, but cannot infer whether an edited gesture now needs a differently placed cue. A shortened clip that excludes a cue requires correction; retaining its old import bounds cannot restore motion removed from the FBX.

Material animation, arbitrary Blender-only behavior/simulations, fractional strip boundaries, ambiguous reused-action relinks, other app versions and final animator/gameplay usability are outside these results. Custom Copy From Other masks are preserved rather than automatically replaced. Automatic reconfiguration waits until Edit Mode.

The 0.1 legacy workflow previously passed 14 native Unity tests and 49 Blender checks on both Blender versions; see the legacy guide. Its native-clip size and full-project reference-scan cost still apply to that workflow.

GitHub CI checks distribution and pure Python logic. It does not run Blender or Unity. The Smoking facing retest was accepted by the user. Two implementation reviews ran with Claude Opus5.5. Final setup/modal/convergence fixes were applied and tested after the second review; they were not re-reviewed. Final human acceptance remains pending.

Maintenance review validation: native custom Create From This Model masks, unconfigured-folder non-mutation, delayed setup convergence, and blocked-event restart stability are covered by focused native checks. Blender failed/cancelled-rename rollback tests pass on both tested versions. Combined/lightweight geometry experiments are separate from this released workflow.

0.2.3 final checks:49 native Unity assertions; configured Humanoid input preserved while setup is unset or its Avatar is missing; unchanged Humanoid settings converge without another import.30 direct-export checks pass on Blender5.0.1 and5.2.1LTS, including failed rename and pre-bake cancellation. Real GUI button exports and immediate second-click rejection pass on both Blender versions. Blocked-event recovery with a persisted clip reference remains unchanged across two Editor sessions. Automatic first import/new-prop callbacks preserve references and converge.

Export names are ownership keys. Returning to a filename retained by an older published clip identity is blocked rather than reclaiming it automatically. Same-basename copies retain profile identity; a new character should use a distinct saved source/export profile. Sidecar/hash mismatches are ignored until matching source and metadata arrive; manually replacing an owned FBX is outside normal export. Native event seconds do not automatically follow a retimed gesture.
