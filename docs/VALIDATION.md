# Verification for 0.2.0

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

GitHub CI checks distribution and pure Python logic. It does not run Blender or Unity. This version is a prototype prerelease awaiting the animator's functional check and the final independent implementation review and human acceptance.
