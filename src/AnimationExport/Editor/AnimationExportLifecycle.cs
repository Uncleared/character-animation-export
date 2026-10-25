using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CharacterAnimationExport
{
    public sealed class AnimationUsage
    {
        public bool complete;
        public string error;
        public readonly List<string> assetReferences = new List<string>();
        public readonly List<string> nameCandidates = new List<string>();
        public readonly List<string> timingDependencies = new List<string>();
        public string Summary => (complete ? "Scan completed" : "Scan incomplete: " + error) +
            "\nSerialized asset uses: " + assetReferences.Count + "\nName candidates: " + nameCandidates.Count +
            "\nConstructed runtime names and external files still require human review.";
    }

    [Serializable] public sealed class RecoveryFile { public string assetPath, guid, backup, sha256, metaSha256; }
    [Serializable] public sealed class AnimationRecovery
    {
        public string libraryGuid, clipId, operation;
        public List<RecoveryFile> files = new List<RecoveryFile>();
    }

    public static class AnimationExportLifecycle
    {
        static bool IsReferenceAsset(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return new[] { ".asset", ".prefab", ".unity", ".controller", ".overridecontroller", ".playable", ".anim", ".fbx" }.Contains(extension);
        }

        public static AnimationUsage Scan(AnimationExportLibrary library, ManagedAnimationClip entry, string searchRoot = "Assets")
        {
            AnimationExportImport.AssertOwned(library, entry);
            var report = new AnimationUsage();
            var clipPath = entry.clip != null ? AssetDatabase.GetAssetPath(entry.clip) : entry.nativePath;
            var libraryPath = AssetDatabase.GetAssetPath(library);
            var targets = new[] { clipPath, entry.sourceFbx }.Where(path => !string.IsNullOrEmpty(path)).ToArray();
            var paths = AssetDatabase.GetAllAssetPaths().Where(path => path.StartsWith(searchRoot.TrimEnd('/') + "/", StringComparison.Ordinal)).ToArray();
            try
            {
                for (var index = 0; index < paths.Length; index++)
                {
                    var path = paths[index];
                    if (index % 100 == 0 && EditorUtility.DisplayCancelableProgressBar("Animation usage", path, (float)index / Math.Max(1, paths.Length)))
                        throw new OperationCanceledException("Usage scan cancelled; deletion remains blocked.");
                    if (path == libraryPath || targets.Contains(path) || AssetDatabase.IsValidFolder(path)) continue;
                    if (IsReferenceAsset(path) && AssetDatabase.GetDependencies(path, false).Any(targets.Contains))
                    {
                        report.assetReferences.Add(path);
                        if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var obj in ObjectsAt(path))
                            {
                                if (obj == null) continue;
                                var serialized = new SerializedObject(obj);
                                var iterator = serialized.GetIterator();
                                while (iterator.Next(true))
                                {
                                    if (iterator.propertyType != SerializedPropertyType.ObjectReference || iterator.objectReferenceValue != entry.clip) continue;
                                    var prefix = iterator.propertyPath.EndsWith("._Clip", StringComparison.Ordinal)
                                        ? iterator.propertyPath.Substring(0, iterator.propertyPath.Length - 6) : null;
                                    var times = prefix == null ? null : serialized.FindProperty(prefix + "._Events._NormalizedTimes");
                                    if (times != null && times.isArray && times.arraySize > 0)
                                        report.timingDependencies.Add(path + " :: " + prefix + " has " + times.arraySize + " normalized transition events (not edited).");
                                }
                            }
                        }
                    }
                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    if ((extension == ".cs" || extension == ".nani" || extension == ".json") && File.Exists(path) && !string.IsNullOrEmpty(entry.gameplayName))
                    {
                        if (File.ReadLines(path).Any(line => line.IndexOf(entry.gameplayName, StringComparison.Ordinal) >= 0))
                            report.nameCandidates.Add(path);
                    }
                }
                for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
                {
                    var scene = SceneManager.GetSceneAt(sceneIndex);
                    if (!scene.isLoaded) continue;
                    var sceneUse = false;
                    foreach (var component in scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Component>(true)))
                    {
                        if (component == null) continue;
                        var serialized = new SerializedObject(component);
                        var iterator = serialized.GetIterator();
                        while (iterator.Next(true))
                        {
                            if (iterator.propertyType != SerializedPropertyType.ObjectReference || iterator.objectReferenceValue == null) continue;
                            if (iterator.objectReferenceValue == entry.clip || targets.Contains(AssetDatabase.GetAssetPath(iterator.objectReferenceValue)))
                            { sceneUse = true; break; }
                        }
                        if (sceneUse) break;
                    }
                    if (sceneUse)
                    {
                        var path = string.IsNullOrEmpty(scene.path) ? "Unsaved scene: " + scene.name : scene.path;
                        if (!report.assetReferences.Contains(path)) report.assetReferences.Add(path);
                    }
                }
                report.complete = true;
            }
            catch (Exception exception) { report.error = exception.Message; }
            finally { EditorUtility.ClearProgressBar(); }
            return report;
        }

        static IEnumerable<Object> ObjectsAt(string path)
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is GameObject gameObject)
                {
                    yield return gameObject;
                    foreach (var component in gameObject.GetComponentsInChildren<Component>(true))
                        if (component != null) yield return component;
                }
                else if (asset != null) yield return asset;
            }
        }

        public static string Backup(AnimationExportLibrary library, ManagedAnimationClip entry, string operation, IEnumerable<string> paths)
        {
            var libraryGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(library));
            if (!AnimationExportImport.ValidId(libraryGuid)) throw new InvalidOperationException("Save the library before creating recovery data.");
            var folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), ".animation-export-recovery", libraryGuid, Guid.NewGuid().ToString("N"));
            AnimationExportImport.RejectLinks(folder);
            Directory.CreateDirectory(folder);
            var record = new AnimationRecovery { libraryGuid = libraryGuid, clipId = entry.id, operation = operation };
            foreach (var path in paths.Distinct())
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                AnimationExportImport.RejectLinks(path);
                var file = new RecoveryFile { assetPath = path, guid = AssetDatabase.AssetPathToGUID(path), backup = record.files.Count.ToString() };
                File.Copy(path, Path.Combine(folder, file.backup));
                file.sha256 = AnimationExportImport.Hash(path);
                if (File.Exists(path + ".meta"))
                {
                    File.Copy(path + ".meta", Path.Combine(folder, file.backup + ".meta"));
                    file.metaSha256 = AnimationExportImport.Hash(path + ".meta");
                }
                record.files.Add(file);
            }
            var recordPath = Path.Combine(folder, "recovery.json");
            File.WriteAllText(recordPath, JsonUtility.ToJson(record, true));
            return recordPath;
        }

        public static void Archive(AnimationExportLibrary library, ManagedAnimationClip entry, bool archived)
        {
            Undo.RecordObject(library, archived ? "Archive animation" : "Restore archived animation");
            entry.archived = archived;
            AnimationExportImport.Save(library);
        }

        public static void Rollback(AnimationExportLibrary library, string recordPath, IEnumerable<string> allowedPaths)
        {
            AnimationExportImport.RejectLinks(recordPath);
            var record = JsonUtility.FromJson<AnimationRecovery>(File.ReadAllText(recordPath));
            if (record.libraryGuid != AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(library))) throw new InvalidDataException("Recovery library mismatch.");
            var allowed = new HashSet<string>(allowedPaths);
            var folder = Path.GetDirectoryName(recordPath);
            foreach (var file in record.files)
            {
                if (!allowed.Contains(file.assetPath)) throw new InvalidDataException("Recovery includes an unexpected file.");
                var backup = AnimationExportImport.PackageFile(folder, file.backup);
                if (AnimationExportImport.Hash(backup) != file.sha256 ||
                    (!string.IsNullOrEmpty(file.metaSha256) && AnimationExportImport.Hash(backup + ".meta") != file.metaSha256))
                    throw new InvalidDataException("Recovery backup changed.");
            }
            foreach (var path in allowed)
                if (!record.files.Any(file => file.assetPath == path) && (File.Exists(path) || File.Exists(path + ".meta")))
                    if (!AssetDatabase.DeleteAsset(path)) throw new IOException("Could not remove the incomplete new asset: " + path);
            foreach (var file in record.files)
            {
                var backup = AnimationExportImport.PackageFile(folder, file.backup);
                File.Copy(backup, file.assetPath, true);
                if (!string.IsNullOrEmpty(file.metaSha256)) File.Copy(backup + ".meta", file.assetPath + ".meta", true);
            }
            foreach (var file in record.files) AssetDatabase.ImportAsset(file.assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        public static void DeleteUnused(AnimationExportLibrary library, ManagedAnimationClip entry, bool reviewedNameUsage, string searchRoot = "Assets")
        {
            if (!reviewedNameUsage) throw new InvalidOperationException("Review name-based/runtime usage before deleting this clip.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before deleting clips.");
            var usage = Scan(library, entry, searchRoot);
            if (!usage.complete) throw new InvalidOperationException(usage.Summary);
            if (usage.assetReferences.Count > 0 || usage.nameCandidates.Count > 0)
                throw new InvalidOperationException("Clip still has asset uses or name candidates. Replace or resolve those first; Archive is reversible.");
            AnimationExportImport.AssertOwned(library, entry);
            var expectedFbx = library.generatedFolder + "/Sources/" + entry.id + ".fbx";
            if (entry.sourceFbx != expectedFbx) throw new InvalidOperationException("Unexpected managed FBX path.");
            var paths = new[] { AssetDatabase.GetAssetPath(entry.clip), entry.sourceFbx }.Where(path => !string.IsNullOrEmpty(path)).ToArray();
            var recovery = Backup(library, entry, "Delete", paths);
            entry.recoveryRecord = recovery;
            AnimationExportImport.Save(library);
            try
            {
                foreach (var path in paths)
                    if (File.Exists(path) && !AssetDatabase.DeleteAsset(path)) throw new IOException("Could not delete " + path);
            }
            catch
            {
                Rollback(library, recovery, paths);
                entry.clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(entry.nativePath);
                AnimationExportImport.Save(library);
                throw;
            }
            entry.clip = null;
            entry.deleted = true;
            entry.deletedHash = entry.sourceHash;
            entry.status = "Deleted (suppressed)";
            AnimationExportImport.Save(library);
        }

        public static void Restore(AnimationExportLibrary library, ManagedAnimationClip entry)
        {
            if (!entry.deleted || string.IsNullOrEmpty(entry.recoveryRecord)) throw new InvalidOperationException("No deleted-clip recovery record.");
            AnimationExportImport.AssertOwned(library, entry);
            AnimationExportImport.RejectLinks(entry.recoveryRecord);
            var record = JsonUtility.FromJson<AnimationRecovery>(File.ReadAllText(entry.recoveryRecord));
            if (record.clipId != entry.id || record.libraryGuid != AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(library)))
                throw new InvalidDataException("Recovery identity does not match this library/clip.");
            var folder = Path.GetDirectoryName(entry.recoveryRecord);
            foreach (var file in record.files)
            {
                var expectedFbx = library.generatedFolder + "/Sources/" + entry.id + ".fbx";
                if (file.assetPath != entry.nativePath && file.assetPath != expectedFbx) throw new InvalidDataException("Recovery path is not owned by this clip.");
                var saved = AnimationExportImport.PackageFile(folder, file.backup);
                if (AnimationExportImport.Hash(saved) != file.sha256 ||
                    (!string.IsNullOrEmpty(file.metaSha256) && AnimationExportImport.Hash(saved + ".meta") != file.metaSha256))
                    throw new InvalidDataException("Recovery data changed.");
                // Unity can retain a deleted GUID's old path until the next refresh.
                var assignedPath = AssetDatabase.GUIDToAssetPath(file.guid);
                var guidIsLive = !string.IsNullOrEmpty(assignedPath) && (File.Exists(assignedPath) || File.Exists(assignedPath + ".meta"));
                if (File.Exists(file.assetPath) || File.Exists(file.assetPath + ".meta") || guidIsLive)
                    throw new IOException("Recovery destination or original GUID is already in use.");
            }
            foreach (var file in record.files)
            {
                var saved = AnimationExportImport.PackageFile(folder, file.backup);
                Directory.CreateDirectory(Path.GetDirectoryName(file.assetPath));
                File.Copy(saved, file.assetPath);
                if (!string.IsNullOrEmpty(file.metaSha256)) File.Copy(saved + ".meta", file.assetPath + ".meta");
            }
            foreach (var file in record.files) AssetDatabase.ImportAsset(file.assetPath, ImportAssetOptions.ForceSynchronousImport);
            entry.clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(entry.nativePath);
            entry.deleted = false; entry.archived = false; entry.status = "Restored";
            AnimationExportImport.Save(library);
        }

        public static void Rename(AnimationExportLibrary library, ManagedAnimationClip entry, string newName, bool reviewedNameUsage, string searchRoot = "Assets")
        {
            AnimationExportImport.AssertOwned(library, entry);
            AnimationExportImport.ValidateName(newName);
            if (!reviewedNameUsage) throw new InvalidOperationException("Review gameplay-name usage before renaming.");
            var usage = Scan(library, entry, searchRoot);
            if (!usage.complete || usage.nameCandidates.Count > 0)
                throw new InvalidOperationException("Resolve the reported story/code name candidates before renaming. The existing gameplay name has been retained.");
            if (library.clips.Any(other => other != entry && !other.deleted && other.gameplayName == newName))
                throw new InvalidOperationException("That gameplay name already exists in this library.");
            var oldPath = AssetDatabase.GetAssetPath(entry.clip);
            entry.recoveryRecord = Backup(library, entry, "Rename", new[] { oldPath });
            var error = AssetDatabase.RenameAsset(oldPath, newName);
            if (!string.IsNullOrEmpty(error)) throw new IOException(error);
            entry.gameplayName = newName;
            entry.keepGameplayName = false;
            entry.nativePath = AssetDatabase.GetAssetPath(entry.clip);
            entry.status = "Renamed";
            AnimationExportImport.Save(library);
        }

        public static int ReplaceReferences(AnimationExportLibrary library, ManagedAnimationClip entry, AnimationClip replacement, IEnumerable<string> selectedPaths)
        {
            AnimationExportImport.AssertOwned(library, entry);
            if (replacement == null || replacement == entry.clip) throw new InvalidOperationException("Choose another animation clip.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before replacing references.");
            var paths = selectedPaths.Distinct().ToArray();
            if (paths.Length == 0) throw new InvalidOperationException("Select at least one referenced asset.");
            if (paths.Any(path => !path.StartsWith("Assets/", StringComparison.Ordinal) || !File.Exists(path)))
                throw new InvalidOperationException("Save unsaved scenes/assets before replacing their references.");
            var targets = new[] { AssetDatabase.GetAssetPath(entry.clip), entry.sourceFbx };
            if (paths.Any(path => !IsReferenceAsset(path) || !AssetDatabase.GetDependencies(path, false).Any(targets.Contains)))
                throw new InvalidOperationException("A selected asset no longer uses this clip or its source.");
            foreach (var path in paths.Where(path => path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)))
            {
                var scene = SceneManager.GetSceneByPath(path);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty)
                    throw new InvalidOperationException("Save existing scene edits before replacing its references: " + path);
            }
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(entry.clip, out string previousGuid, out long previousId) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(replacement, out string replacementGuid, out long replacementId))
                throw new InvalidOperationException("Both clips must be saved assets.");
            var pattern = new Regex(@"(?m)(?<prefix>^\s*(?:[^#\r\n:]+:\s*|-\s*))(?<reference>\{fileID:\s*" +
                previousId + @",\s*guid:\s*" + previousGuid + @",\s*type:\s*\d+\})", RegexOptions.IgnoreCase);
            var replacementType = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(replacement)) is ModelImporter ? 3 : 2;
            var plans = new List<ReferencePatch>();
            foreach (var path in paths)
            {
                AnimationExportImport.RejectLinks(path);
                var bytes = File.ReadAllBytes(path);
                var text = new UTF8Encoding(false, true).GetString(bytes);
                if (!text.TrimStart('\uFEFF').StartsWith("%YAML", StringComparison.Ordinal))
                    throw new InvalidOperationException("This binary asset needs a native Inspector reference edit: " + path);
                var matches = pattern.Matches(text).Count;
                if (matches == 0) throw new InvalidOperationException("This asset depends on the model but has no exact selected clip reference: " + path);
                var changed = pattern.Replace(text, match =>
                {
                    var value = match.Groups["reference"].Value;
                    value = Regex.Replace(value, @"(?<prefix>fileID:\s*)-?\d+", item => item.Groups["prefix"].Value + replacementId);
                    value = Regex.Replace(value, @"(?<prefix>guid:\s*)[0-9a-f]+", item => item.Groups["prefix"].Value + replacementGuid, RegexOptions.IgnoreCase);
                    value = Regex.Replace(value, @"(?<prefix>type:\s*)\d+", item => item.Groups["prefix"].Value + replacementType);
                    return match.Groups["prefix"].Value + value;
                });
                plans.Add(new ReferencePatch { path = path, before = bytes, after = Encoding.UTF8.GetBytes(changed), count = matches });
            }
            var recovery = Backup(library, entry, "Replace references", paths);
            var count = plans.Sum(plan => plan.count);
            var undoGroup = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            try
            {
            foreach (var plan in plans)
            {
                if (!File.ReadAllBytes(plan.path).SequenceEqual(plan.before))
                    throw new IOException("The selected asset changed after preview: " + plan.path);
                if (plan.path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    var scene = SceneManager.GetSceneByPath(plan.path);
                    if (scene.IsValid() && scene.isLoaded)
                    {
                        var objects = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Component>(true)).Where(item => item != null).Cast<Object>();
                        ReplaceObjects(objects, entry.clip, replacement);
                    }
                }
                File.WriteAllBytes(plan.path, plan.after);
                AssetDatabase.ImportAsset(plan.path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                if (!File.ReadAllBytes(plan.path).SequenceEqual(plan.after)) throw new IOException("An import changed more than the selected references: " + plan.path);
            }
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup + 1);
                Rollback(library, recovery, paths);
                throw;
            }
            entry.recoveryRecord = recovery;
            entry.status = "Replaced " + count + " selected serialized references; review remaining name/asset uses";
            AnimationExportImport.Save(library);
            return count;
        }

        sealed class ReferencePatch { public string path; public byte[] before, after; public int count; }

        static int ReplaceObjects(IEnumerable<Object> objects, AnimationClip previous, AnimationClip replacement)
        {
            var count = 0;
            foreach (var obj in objects.Distinct())
            {
                var serialized = new SerializedObject(obj);
                var iterator = serialized.GetIterator();
                var changed = false;
                while (iterator.Next(true))
                {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference || iterator.objectReferenceValue != previous) continue;
                    iterator.objectReferenceValue = replacement;
                    count++; changed = true;
                }
                if (changed) serialized.ApplyModifiedProperties();
            }
            return count;
        }
    }
}
