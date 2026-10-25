using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CharacterAnimationExport.Tests
{
    public sealed class AnimationExportTests
    {
        string root;
        string fixtures;
        AnimationExportLibrary library;

        [SetUp]
        public void SetUp()
        {
            var script = AssetDatabase.FindAssets("AnimationExportTests t:MonoScript").Select(AssetDatabase.GUIDToAssetPath)
                .First(path => path.EndsWith("/Tests/Editor/AnimationExportTests.cs", StringComparison.Ordinal));
            var toolRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(script)));
            var bundledFixtures = Path.GetFullPath(Path.Combine(toolRoot, "Blender~", "Fixtures"));
            fixtures = Path.Combine(Path.GetTempPath(), "CharacterAnimationExportTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtures);
            foreach (var file in Directory.GetFiles(bundledFixtures, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(fixtures, file.Substring(bundledFixtures.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
            }
            root = toolRoot.Replace('\\', '/') + "/Tests/Generated/" + Guid.NewGuid().ToString("N");
            AnimationExportImport.AssetFolder(root);
            library = ScriptableObject.CreateInstance<AnimationExportLibrary>();
            // These deliberately unskinned rigs have props beside their single bone.
            library.motionNode = "MotionRoot";
            AssetDatabase.CreateAsset(library, root + "/Library.asset");
        }

        [TearDown]
        public void TearDown()
        {
            Assert.That(root, Does.Contain("/AnimationExport/Tests/Generated/"));
            if (AssetDatabase.IsValidFolder(root)) AssetDatabase.DeleteAsset(root);
            var temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CharacterAnimationExportTests")) + Path.DirectorySeparatorChar;
            Assert.That(Path.GetFullPath(fixtures).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase), Is.True);
            Assert.That(Guid.TryParseExact(Path.GetFileName(fixtures), "N", out _), Is.True);
            if (Directory.Exists(fixtures)) Directory.Delete(fixtures, true);
        }

        string Package(string name) => Path.Combine(fixtures, name, "animation-export.json");

        [TestCase("../outside.fbx")]
        [TestCase("C:/outside.fbx")]
        [TestCase("/outside.fbx")]
        [TestCase("clips\\outside.fbx")]
        public void PackagePathsCannotEscape(string relative)
        {
            Assert.Throws<InvalidDataException>(() => AnimationExportImport.PackageFile(fixtures, relative));
        }

        [TestCase("")]
        [TestCase("../Bad")]
        [TestCase("CON")]
        [TestCase("Trailing.")]
        public void UnsafeNamesAreRejected(string name)
        {
            Assert.Throws<InvalidDataException>(() => AnimationExportImport.ValidateName(name));
        }

        [TestCase("FirstHero")]
        [TestCase("Robot_B")]
        [TestCase("DifferentRig")]
        public void DifferentRigsImportBodyPropsAndShapeKeys(string fixture)
        {
            AnimationExportImport.Import(library, Package(fixture));
            Assert.That(library.clips.Count, Is.EqualTo(2));
            var first = library.clips.First();
            Assert.That(first.clip.name, Is.EqualTo(first.gameplayName));
            Assert.That(first.clip.length, Is.EqualTo(11f / 30).Within(0.001));
            var bindings = AnimationUtility.GetCurveBindings(first.clip);
            Assert.That(bindings.Any(binding => binding.path.Contains("Nested Moving Lid") && binding.propertyName.Contains("Rotation")), Is.True, "Nested prop rotation was lost.");
            Assert.That(bindings.Any(binding => binding.propertyName == "blendShape.Expression"), Is.True, "Shape-key motion was lost.");
            var importer = (ModelImporter)AssetImporter.GetAtPath(first.sourceFbx);
            var clip = importer.clipAnimations.Single();
            Assert.That(clip.lockRootRotation && clip.lockRootHeightY && clip.lockRootPositionXZ, Is.True);
            var mask = new AvatarMask();
            try
            {
                clip.ConfigureMaskFromClip(ref mask);
                Assert.That(Enumerable.Range(0, mask.transformCount).All(mask.GetTransformActive), Is.True);
                Assert.That(Enumerable.Range(0, mask.transformCount).Any(index => mask.GetTransformPath(index).Contains("Nested Moving Lid")), Is.True);
            }
            finally { Object.DestroyImmediate(mask); }
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(first.sourceFbx);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, preview);
                var bone = instance.GetComponentsInChildren<Transform>(true).Single(transform => transform.name == "MotionRoot");
                first.clip.SampleAnimation(instance, 0);
                var start = bone.position.x;
                first.clip.SampleAnimation(instance, 5f / 30);
                // The FBX/Unity handedness conversion maps Blender +X to Unity -X.
                Assert.That(bone.position.x - start, Is.EqualTo(-0.2f).Within(0.005f), "Rig scale changed the authored translation.");
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        [Test]
        public void EventEditsAndReimportPreserveIdentityPayloadsAndOverrides()
        {
            var importWatch = Stopwatch.StartNew();
            AnimationExportImport.Import(library, Package("FirstHero"));
            importWatch.Stop();
            var entry = library.clips[0];
            var guid = AssetDatabase.AssetPathToGUID(entry.nativePath);
            var payload = new Texture2D(2, 2);
            AssetDatabase.CreateAsset(payload, root + "/Payload.asset");
            var fbxHash = AnimationExportImport.Hash(entry.sourceFbx);
            var fbxMetaHash = AnimationExportImport.Hash(entry.sourceFbx + ".meta");
            var dependency = AssetDatabase.GetAssetDependencyHash(entry.sourceFbx);
            var eventWatch = Stopwatch.StartNew();
            AnimationExportEvents.Save(entry.clip, new[] { new AnimationEvent { time = 0.2f, functionName = "Cue", stringParameter = "LidOpen", floatParameter = 0.75f, intParameter = 7, objectReferenceParameter = payload, messageOptions = SendMessageOptions.DontRequireReceiver } });
            eventWatch.Stop();
            Assert.That(AnimationExportImport.Hash(entry.sourceFbx), Is.EqualTo(fbxHash));
            Assert.That(AnimationExportImport.Hash(entry.sourceFbx + ".meta"), Is.EqualTo(fbxMetaHash));
            Assert.That(AssetDatabase.GetAssetDependencyHash(entry.sourceFbx), Is.EqualTo(dependency));
            var settings = AnimationUtility.GetAnimationClipSettings(entry.clip);
            settings.loopTime = true; settings.loopBlend = true; settings.cycleOffset = 0.1f; settings.mirror = true;
            AnimationUtility.SetAnimationClipSettings(entry.clip, settings);
            EditorUtility.SetDirty(entry.clip); AssetDatabase.SaveAssetIfDirty(entry.clip);
            entry.overrideRoot = true; entry.bakeY = false;
            AnimationExportImport.Import(library, Package("FirstHero"));
            Assert.That(AssetDatabase.AssetPathToGUID(entry.nativePath), Is.EqualTo(guid));
            Assert.That(entry.clip.name, Is.EqualTo(entry.gameplayName));
            var item = AnimationUtility.GetAnimationEvents(entry.clip).Single();
            Assert.That(item.time, Is.EqualTo(0.2f)); Assert.That(item.functionName, Is.EqualTo("Cue"));
            Assert.That(item.stringParameter, Is.EqualTo("LidOpen")); Assert.That(item.intParameter, Is.EqualTo(7));
            Assert.That(item.floatParameter, Is.EqualTo(0.75f)); Assert.That(item.objectReferenceParameter, Is.EqualTo(payload));
            Assert.That(item.messageOptions, Is.EqualTo(SendMessageOptions.DontRequireReceiver));
            settings = AnimationUtility.GetAnimationClipSettings(entry.clip);
            Assert.That(settings.loopTime && settings.loopBlend && settings.mirror, Is.True);
            Assert.That(settings.cycleOffset, Is.EqualTo(0.1f));
            Assert.That(((ModelImporter)AssetImporter.GetAtPath(entry.sourceFbx)).clipAnimations.Single().lockRootHeightY, Is.False);
            UnityEngine.Debug.Log("ANIMATION_EXPORT_METRICS eventEditMs=" + eventWatch.Elapsed.TotalMilliseconds + " initialImportMs=" + importWatch.Elapsed.TotalMilliseconds +
                " nativeBytes=" + new FileInfo(entry.nativePath).Length + " fbxBytes=" + new FileInfo(entry.sourceFbx).Length);
        }

        [Test]
        public void DurationReviewAndNewChildMaskAreExplicit()
        {
            AnimationExportImport.Import(library, Package("FirstHero"));
            var entry = library.clips[0];
            var guid = AssetDatabase.AssetPathToGUID(entry.nativePath);
            AnimationExportEvents.Save(entry.clip, new[] { new AnimationEvent { time = 0.2f, functionName = "Cue", intParameter = 11 } });
            var oldLength = entry.clip.length;
            AnimationExportImport.Import(library, Package("FirstHeroExtended"));
            Assert.That(entry.pendingJson, Is.Not.Empty);
            Assert.That(entry.clip.length, Is.EqualTo(oldLength));
            AnimationExportImport.ApplyPending(library, entry, EventTimingChoice.ScaleProportionally);
            Assert.That(entry.clip.length, Is.EqualTo(16f / 30).Within(0.001));
            Assert.That(AnimationUtility.GetAnimationEvents(entry.clip).Single().time, Is.EqualTo(0.2f * 16 / 11).Within(0.001));
            AnimationExportImport.Import(library, Package("FirstHeroNewChild"));
            Assert.That(AssetDatabase.AssetPathToGUID(entry.nativePath), Is.EqualTo(guid));
            Assert.That(AnimationUtility.GetCurveBindings(entry.clip).Any(binding => binding.path.Contains("New Animated Child")), Is.True);
            Assert.That(AnimationUtility.GetAnimationEvents(entry.clip).Single().intParameter, Is.EqualTo(11));
        }

        [Test]
        public void DeleteReplaceRestoreAndRenameKeepOtherData()
        {
            AnimationExportImport.Import(library, Package("FirstHero"));
            var entry = library.clips[0]; var replacement = library.clips[1];
            var guid = AssetDatabase.AssetPathToGUID(entry.nativePath);
            var fixture = ScriptableObject.CreateInstance<AnimationExportReferenceFixture>();
            fixture.animations = new[] { new FixtureTransition { _Clip = entry.clip } };
            var referencePath = root + "/Reference.asset";
            AssetDatabase.CreateAsset(fixture, referencePath);
            var usage = AnimationExportLifecycle.Scan(library, entry, root);
            Assert.That(usage.complete && usage.assetReferences.Contains(referencePath), Is.True);
            Assert.That(usage.timingDependencies.Count, Is.GreaterThan(0));
            Assert.Throws<InvalidOperationException>(() => AnimationExportLifecycle.DeleteUnused(library, entry, true, root));
            Assert.That(AnimationExportLifecycle.ReplaceReferences(library, entry, replacement.clip, new[] { referencePath }), Is.EqualTo(1));
            Assert.That(fixture.animations[0]._Clip, Is.EqualTo(replacement.clip));
            Assert.That(fixture.animations[0]._FadeDuration, Is.EqualTo(0.37f));
            Assert.That(fixture.animations[0]._Speed, Is.EqualTo(0.8f));
            Assert.That(fixture.animations[0]._Events._NormalizedTimes.Single(), Is.EqualTo(0.3f));
            Assert.That(fixture.untouched, Is.EqualTo("keep this value"));
            File.WriteAllText(root + "/NameUse.json", "{\"clip\":\"" + entry.gameplayName + "\"}");
            AssetDatabase.ImportAsset(root + "/NameUse.json", ImportAssetOptions.ForceSynchronousImport);
            Assert.Throws<InvalidOperationException>(() => AnimationExportLifecycle.DeleteUnused(library, entry, true, root));
            AssetDatabase.DeleteAsset(root + "/NameUse.json");
            AnimationExportLifecycle.DeleteUnused(library, entry, true, root);
            Assert.That(entry.deleted && entry.clip == null, Is.True);
            AnimationExportImport.Import(library, Package("FirstHero"));
            Assert.That(entry.deleted && entry.clip == null, Is.True, "An old package resurrected a deleted clip.");
            AssetDatabase.CreateAsset(new AnimationClip(), entry.nativePath);
            Assert.Throws<IOException>(() => AnimationExportLifecycle.Restore(library, entry), "Recovery must not overwrite a live destination.");
            AssetDatabase.DeleteAsset(entry.nativePath);
            AnimationExportLifecycle.Restore(library, entry);
            Assert.That(AssetDatabase.AssetPathToGUID(entry.nativePath), Is.EqualTo(guid));
            var renamed = "Renamed_" + Guid.NewGuid().ToString("N");
            AnimationExportLifecycle.Rename(library, entry, renamed, true, root);
            Assert.That(entry.clip.name, Is.EqualTo(renamed));
            Assert.That(AssetDatabase.AssetPathToGUID(entry.nativePath), Is.EqualTo(guid));
            Assert.That(replacement.clip != null && fixture.animations[0]._Clip == replacement.clip, Is.True);
        }
    }
}
