using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CharacterAnimationExport
{
    public static class AnimationExportImport
    {
        public static bool ValidId(string value) => value != null && Regex.IsMatch(value, "^[0-9a-f]{32}$");

        public static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static void RejectLinks(string path)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Choose a folder without symbolic links or junctions: " + current);
                var parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }

        public static string PackageFile(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.Contains("\\") || relative.Contains(":") || Path.IsPathRooted(relative))
                throw new InvalidDataException("Invalid package-relative filename.");
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A package path escapes its folder.");
            RejectLinks(full);
            return full;
        }

        public static ExportPackage ReadPackage(string path)
        {
            RejectLinks(path);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            for (var ancestor = new DirectoryInfo(folder); ancestor != null; ancestor = ancestor.Parent)
                if (ancestor.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(ancestor.Parent.FullName, "ProjectSettings")))
                    throw new InvalidDataException("Import a package from outside Unity Assets.");
            var package = JsonUtility.FromJson<ExportPackage>(File.ReadAllText(path));
            if (package == null || package.schemaVersion != 1 || !ValidId(package.profileId) || !ValidId(package.publicationId) || package.generation < 1 || package.clips == null)
                throw new InvalidDataException("Unsupported or incomplete animation package.");
            var ids = new HashSet<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in package.clips)
            {
                if (row == null || !ValidId(row.id) || !ids.Add(row.id) || (row.state != "present" && row.state != "removed"))
                    throw new InvalidDataException("Duplicate or invalid clip identity/state.");
                ValidateName(row.name);
                if (row.state == "present" && !names.Add(row.name))
                    throw new InvalidDataException("Duplicate animation name: " + row.name);
                if (!row.exported) continue;
                if (row.state != "present" || !row.enabled || string.IsNullOrEmpty(row.takeName) || row.duration <= 0 || float.IsInfinity(row.duration) || float.IsNaN(row.duration) || row.fps <= 0)
                    throw new InvalidDataException("Invalid exported clip timing or state: " + row.name);
                if (row.file != "clips/" + row.id + ".fbx" || !Regex.IsMatch(row.sha256 ?? "", "^[0-9a-f]{64}$"))
                    throw new InvalidDataException("Invalid FBX filename or checksum.");
                var file = PackageFile(folder, row.file);
                if (!File.Exists(file) || Hash(file) != row.sha256)
                    throw new InvalidDataException("FBX missing or checksum changed: " + row.name);
            }
            return package;
        }

        public static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".") ||
                Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Choose a clip name that is also a valid filename: " + name);
        }

        public static string EnsureFolder(AnimationExportLibrary library)
        {
            var path = AssetDatabase.GetAssetPath(library);
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Save the library asset first.");
            var expected = Path.GetDirectoryName(path).Replace('\\', '/') + "/" + Path.GetFileNameWithoutExtension(path) + " Generated";
            if (!string.IsNullOrEmpty(library.generatedFolder) && library.generatedFolder != expected)
                throw new InvalidOperationException("The library moved. Restore its original location before updating managed assets.");
            RejectLinks(expected);
            library.generatedFolder = expected;
            AssetFolder(expected);
            return expected;
        }

        public static void AssetFolder(string path)
        {
            path = path.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            if (parent == path || string.IsNullOrEmpty(parent)) throw new IOException("Invalid asset folder.");
            AssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        public static void AssertOwned(AnimationExportLibrary library, ManagedAnimationClip entry)
        {
            if (!ValidId(entry.id)) throw new InvalidDataException("Invalid managed clip ID.");
            var root = EnsureFolder(library) + "/Clips/" + entry.id + "/";
            var actual = entry.clip != null ? AssetDatabase.GetAssetPath(entry.clip) : entry.nativePath;
            if (!string.IsNullOrEmpty(actual) && (!actual.StartsWith(root, StringComparison.Ordinal) || !actual.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This clip is outside its managed folder; no edits were made.");
            if (!string.IsNullOrEmpty(actual)) RejectLinks(actual);
        }

        public static string Import(AnimationExportLibrary library, string manifestPath, EventTimingChoice choice = EventTimingChoice.ReviewFirst)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before updating animation assets.");
            var package = ReadPackage(manifestPath);
            if (!string.IsNullOrEmpty(library.profileId) && library.profileId != package.profileId)
                throw new InvalidOperationException("This package belongs to another character profile. Create another library.");
            if (package.generation < library.generation) throw new InvalidOperationException("This is an older package. Choose the latest publication.");
            if (library.rigType == ExportRigType.Humanoid && (library.sourceAvatar == null || !library.sourceAvatar.isValid || !library.sourceAvatar.isHuman))
                throw new InvalidOperationException("Choose a valid matching Humanoid source Avatar.");
            EnsureFolder(library);
            Undo.RecordObject(library, "Update animation library");
            library.profileId = package.profileId;
            library.characterName = package.characterName;
            library.lastManifest = Path.GetFullPath(manifestPath);
            var updated = 0;
            foreach (var source in package.clips)
            {
                var entry = library.clips.Find(item => item.id == source.id);
                if (entry == null)
                {
                    if (library.clips.Any(item => item.clip != null && !item.deleted && item.gameplayName == source.name))
                        throw new InvalidOperationException("This name already belongs to a retained clip. Relink the Blender source or review its replacement first: " + source.name);
                    entry = new ManagedAnimationClip { id = source.id, incomingName = source.name, gameplayName = source.name, status = "New" };
                    library.clips.Add(entry);
                }
                entry.incomingName = source.name;
                if (entry.deleted)
                {
                    entry.status = source.sha256 == entry.deletedHash ? "Deleted (suppressed)" : "Deleted — re-add available";
                    continue;
                }
                if (source.state == "removed") { entry.status = "Removed in Blender — retained"; continue; }
                if (!source.exported) { entry.status = source.enabled ? "Retained (not in this export)" : "Excluded from export"; continue; }
                ImportOne(library, entry, source, Path.GetDirectoryName(library.lastManifest), choice);
                Save(library);
                if (entry.status.StartsWith("Updated") || entry.status == "Imported") updated++;
            }
            foreach (var entry in library.clips)
                if (!entry.deleted && !package.clips.Any(source => source.id == entry.id)) entry.status = "Removed in Blender — retained";
            library.generation = package.generation;
            Save(library);
            return updated + " clips imported/updated. Review timing, name and removed-clip statuses below." +
                (package.warnings != null && package.warnings.Length > 0 ? "\nBlender warnings: " + string.Join("\n", package.warnings) : "");
        }

        static void Configure(AnimationExportLibrary library, ManagedAnimationClip entry, ExportedClip source)
        {
            var importer = AssetImporter.GetAtPath(entry.sourceFbx) as ModelImporter;
            if (importer == null) throw new InvalidDataException("The source is not an FBX model.");
            importer.importAnimation = true;
            importer.animationType = library.rigType == ExportRigType.Humanoid ? ModelImporterAnimationType.Human : ModelImporterAnimationType.Generic;
            importer.avatarSetup = library.rigType == ExportRigType.Humanoid ? ModelImporterAvatarSetup.CopyFromOther : ModelImporterAvatarSetup.CreateFromThisModel;
            importer.sourceAvatar = library.sourceAvatar;
            importer.optimizeGameObjects = false;
            importer.animationCompression = ModelImporterAnimationCompression.Optimal;
            if (library.rigType == ExportRigType.Generic)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(entry.sourceFbx);
                if (string.IsNullOrWhiteSpace(library.motionNode))
                {
                    var skinRoots = model.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                        .Select(renderer => renderer.rootBone).Where(root => root != null).Distinct().ToArray();
                    if (skinRoots.Length == 1 && skinRoots[0] != model.transform)
                        library.motionNode = AnimationUtility.CalculateTransformPath(skinRoots[0], model.transform);
                }
                if (string.IsNullOrWhiteSpace(library.motionNode) && model.transform.childCount == 1)
                    library.motionNode = model.transform.GetChild(0).name;
                if (string.IsNullOrWhiteSpace(library.motionNode))
                    throw new InvalidOperationException("Choose a Generic motion-node path in Setup. The model has multiple roots.");
                importer.motionNodeName = library.motionNode;
            }
            var definitions = importer.defaultClipAnimations;
            if (definitions.Length != 1 || definitions[0].takeName != source.takeName)
                throw new InvalidDataException("Expected exactly one take matching the exported manifest.");
            var definition = importer.clipAnimations.Length == 1 ? importer.clipAnimations[0] : definitions[0];
            definition.takeName = source.takeName;
            definition.name = entry.gameplayName;
            // FBX retains the authored take's frame origin; native event times start at zero.
            definition.firstFrame = definitions[0].firstFrame;
            definition.lastFrame = definitions[0].lastFrame;
            if (library.autoConfigureImport)
            {
                definition.lockRootRotation = entry.overrideRoot ? entry.bakeRotation : library.bakeRotation;
                definition.lockRootHeightY = entry.overrideRoot ? entry.bakeY : library.bakeY;
                definition.lockRootPositionXZ = entry.overrideRoot ? entry.bakeXZ : library.bakeXZ;
                if (entry.overrideMask)
                {
                    definition.maskType = ClipAnimationMaskType.CopyFromOther;
                    definition.maskSource = entry.customMask;
                    if (entry.customMask == null) throw new InvalidOperationException("Assign the custom mask or disable its override.");
                    definition.ConfigureClipFromMask(entry.customMask);
                }
                else if (library.importAllTransforms)
                {
                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(entry.sourceFbx);
                    var mask = new AvatarMask();
                    try
                    {
                        var transforms = model.GetComponentsInChildren<Transform>(true);
                        mask.transformCount = transforms.Length;
                        for (var index = 0; index < transforms.Length; index++)
                        {
                            mask.SetTransformPath(index, AnimationUtility.CalculateTransformPath(transforms[index], model.transform));
                            mask.SetTransformActive(index, true);
                        }
                        for (var part = 0; part < (int)AvatarMaskBodyPart.LastBodyPart; part++) mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)part, true);
                        definition.maskType = ClipAnimationMaskType.CreateFromThisModel;
                        definition.maskSource = null;
                        definition.ConfigureClipFromMask(mask);
                    }
                    finally { Object.DestroyImmediate(mask); }
                }
            }
            importer.clipAnimations = new[] { definition };
            importer.SaveAndReimport();
            if (library.rigType == ExportRigType.Generic)
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(entry.sourceFbx);
                if (root.transform.Find(library.motionNode) == null)
                    throw new InvalidOperationException("Generic motion node was not found: " + library.motionNode);
            }
        }

        static void ImportOne(AnimationExportLibrary library, ManagedAnimationClip entry, ExportedClip source, string packageRoot, EventTimingChoice choice)
        {
            AssertOwned(library, entry);
            var oldState = JsonUtility.ToJson(entry);
            var native = entry.clip != null ? AssetDatabase.GetAssetPath(entry.clip) :
                library.generatedFolder + "/Clips/" + entry.id + "/" + entry.gameplayName + ".anim";
            var paths = new[] { library.generatedFolder + "/Sources/" + entry.id + ".fbx", native };
            var recovery = AnimationExportLifecycle.Backup(library, entry, "Update motion", paths);
            try { ImportOneCore(library, entry, source, packageRoot, choice); }
            catch (Exception error)
            {
                try
                {
                    AnimationExportLifecycle.Rollback(library, recovery, paths);
                    JsonUtility.FromJsonOverwrite(oldState, entry);
                    if (!string.IsNullOrEmpty(entry.nativePath)) entry.clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(entry.nativePath);
                    Save(library);
                }
                catch (Exception rollbackError)
                {
                    throw new IOException(error.Message + " Recovery needs attention: " + recovery + " :: " + rollbackError.Message, error);
                }
                throw;
            }
        }

        static void ImportOneCore(AnimationExportLibrary library, ManagedAnimationClip entry, ExportedClip source, string packageRoot, EventTimingChoice choice)
        {
            AssertOwned(library, entry);
            entry.sourceFbx = library.generatedFolder + "/Sources/" + entry.id + ".fbx";
            var sourcePath = PackageFile(packageRoot, source.file);
            if (Hash(sourcePath) != source.sha256) throw new InvalidDataException("Source FBX changed since validation.");
            AssetFolder(Path.GetDirectoryName(entry.sourceFbx));
            File.Copy(sourcePath, entry.sourceFbx, true);
            AssetDatabase.ImportAsset(entry.sourceFbx, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Configure(library, entry, source);
            var clips = AssetDatabase.LoadAllAssetsAtPath(entry.sourceFbx).OfType<AnimationClip>()
                .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal)).ToArray();
            if (clips.Length != 1) throw new InvalidDataException("Expected one imported animation clip.");
            var incoming = clips[0];
            if (Math.Abs(incoming.length - source.duration) > Math.Max(0.001, 0.02 / source.fps))
                throw new InvalidDataException("Imported duration does not match the exported range.");
            var existing = entry.clip;
            var wasNew = existing == null;
            var events = existing == null ? AnimationUtility.GetAnimationEvents(incoming) : AnimationUtility.GetAnimationEvents(existing);
            var oldLength = existing == null ? incoming.length : existing.length;
            var durationChanged = Math.Abs(oldLength - incoming.length) > 0.0001f;
            if (existing != null && events.Length > 0 && durationChanged && choice == EventTimingChoice.ReviewFirst)
            {
                entry.pendingJson = JsonUtility.ToJson(source);
                entry.status = "Timing review: " + oldLength.ToString("0.###") + " → " + incoming.length.ToString("0.###") + " s";
                return;
            }
            var factor = durationChanged && choice == EventTimingChoice.ScaleProportionally && oldLength > 0 ? incoming.length / oldLength : 1;
            events = events.Select(item => AnimationExportEvents.Clone(item, factor)).ToArray();
            if (events.Any(item => item.time > incoming.length + 0.0001f))
                throw new InvalidOperationException("An event would fall outside the updated clip. Review it before applying motion.");
            if (existing == null)
            {
                ValidateName(entry.gameplayName);
                var folder = library.generatedFolder + "/Clips/" + entry.id;
                AssetFolder(folder);
                entry.nativePath = folder + "/" + entry.gameplayName + ".anim";
                if (File.Exists(entry.nativePath) || File.Exists(entry.nativePath + ".meta"))
                    throw new IOException("A native clip already exists at this path; restore or relink it rather than replacing its identity.");
                existing = Object.Instantiate(incoming);
                existing.name = entry.gameplayName;
                existing.hideFlags = HideFlags.None;
                AssetDatabase.CreateAsset(existing, entry.nativePath);
                entry.clip = existing;
            }
            else
            {
                var settings = AnimationUtility.GetAnimationClipSettings(existing);
                var loopTime = settings.loopTime;
                var loopBlend = settings.loopBlend;
                var cycle = settings.cycleOffset;
                var mirror = settings.mirror;
                Undo.RecordObject(existing, "Update animation motion");
                EditorUtility.CopySerialized(incoming, existing);
                existing.name = entry.gameplayName;
                existing.hideFlags = HideFlags.None;
                settings = AnimationUtility.GetAnimationClipSettings(existing);
                settings.loopTime = loopTime; settings.loopBlend = loopBlend; settings.cycleOffset = cycle; settings.mirror = mirror;
                AnimationUtility.SetAnimationClipSettings(existing, settings);
            }
            AnimationUtility.SetAnimationEvents(existing, events);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssetIfDirty(existing);
            if (existing.name != entry.gameplayName) throw new InvalidDataException("The native gameplay clip name changed unexpectedly.");
            entry.sourceHash = source.sha256;
            entry.pendingJson = null;
            entry.status = wasNew ? "Imported" : source.name != entry.gameplayName && !entry.keepGameplayName ? "Updated — rename pending" : (durationChanged ? "Updated — check event cues" : "Updated — check changed motion cues");
        }

        public static void ApplyPending(AnimationExportLibrary library, ManagedAnimationClip entry, EventTimingChoice choice)
        {
            if (choice == EventTimingChoice.ReviewFirst || string.IsNullOrEmpty(entry.pendingJson)) return;
            ImportOne(library, entry, JsonUtility.FromJson<ExportedClip>(entry.pendingJson), Path.GetDirectoryName(library.lastManifest), choice);
            Save(library);
        }

        public static void RefreshSelected(AnimationExportLibrary library, ManagedAnimationClip entry)
        {
            var package = ReadPackage(library.lastManifest);
            var source = package.clips.Single(item => item.id == entry.id);
            if (!source.exported || source.state != "present") throw new InvalidOperationException("Export this clip again before refreshing its import settings.");
            ImportOne(library, entry, source, Path.GetDirectoryName(library.lastManifest), EventTimingChoice.ReviewFirst);
            Save(library);
        }

        public static void Save(AnimationExportLibrary library)
        {
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssetIfDirty(library);
        }
    }
}
