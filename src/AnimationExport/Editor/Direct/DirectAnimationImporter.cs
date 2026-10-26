using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CharacterAnimationExport.Direct
{
    public sealed class DirectAnimationImporter : AssetPostprocessor
    {
        [Serializable]
        sealed class AppliedState
        {
            public string profileId, clipId, hash, blockedSignature;
            public float fps, duration;
        }

        const string StateBegin = "\n[CharacterAnimationExport/2:";
        const string StateEnd = "]\n";
        static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> attempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static bool scheduled;
        public static int ReconcileCount { get; private set; }

        // Deliberately no model preprocess callbacks: installing/changing those
        // invalidates the import cache for every model in a large project.

        static void ConfigureRig(ModelImporter importer, AnimationImportSettings settings)
        {
            var human = settings != null && settings.rig == CharacterRig.Humanoid;
            if (human && (settings.sourceAvatar == null || !settings.sourceAvatar.isValid || !settings.sourceAvatar.isHuman))
                throw new InvalidOperationException("Choose the matching Humanoid Avatar in this character's Animation Import Settings.");
            importer.animationType = human ? ModelImporterAnimationType.Human : ModelImporterAnimationType.Generic;
            importer.avatarSetup = human ? ModelImporterAvatarSetup.CopyFromOther : ModelImporterAvatarSetup.CreateFromThisModel;
            importer.sourceAvatar = human ? settings.sourceAvatar : null;
            if (settings != null && !string.IsNullOrWhiteSpace(settings.motionNode)) importer.motionNodeName = settings.motionNode;
        }

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom, bool didDomainReload)
        {
            foreach (var path in imported.Concat(moved))
            {
                if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) Queue(path);
                else if (IsMetadata(path)) QueueFolder(Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace('\\', '/'));
            }
            foreach (var path in deleted.Concat(movedFrom))
                if (IsMetadata(path)) QueueFolder(Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace('\\', '/'));
            var changed = new HashSet<string>(imported.Concat(moved).Concat(deleted).Concat(movedFrom), StringComparer.OrdinalIgnoreCase);
            foreach (var guid in AssetDatabase.FindAssets("t:AnimationImportSettings"))
            {
                var settingsPath = AssetDatabase.GUIDToAssetPath(guid);
                var settings = AssetDatabase.LoadAssetAtPath<AnimationImportSettings>(settingsPath);
                if (settings != null && settings.sourceAvatar != null && changed.Contains(AssetDatabase.GetAssetPath(settings.sourceAvatar)))
                    QueueFolder(Path.GetDirectoryName(Path.GetDirectoryName(settingsPath)).Replace('\\', '/'));
            }
            if (didDomainReload) ScheduleDiscovery();
        }

        static bool IsMetadata(string path) => path.Contains("/AnimationExportData/") &&
            (path.EndsWith(".animation-export.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".settings.asset", StringComparison.OrdinalIgnoreCase));

        [InitializeOnLoadMethod]
        static void ScheduleDiscovery()
        {
            EditorApplication.delayCall += () =>
            {
                foreach (var guid in AssetDatabase.FindAssets("glob:\"**/*.animation-export.json\""))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (IsMetadata(path)) QueueFolder(Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace('\\', '/'));
                }
            };
        }

        public static void QueueFolder(string folder)
        {
            try { foreach (var source in DirectAnimationMetadata.Sources(folder)) if (File.Exists(source.assetPath)) Queue(source.assetPath); }
            catch (Exception error) { Debug.LogError("Animation export: " + error.Message); }
        }

        public static void Queue(string path)
        {
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(Path.GetDirectoryName(path) + "/AnimationExportData")) return;
            pending.Add(path);
            if (!scheduled) { scheduled = true; EditorApplication.delayCall += ProcessPending; }
        }

        public static void ProcessPending()
        {
            scheduled = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                if (pending.Count > 0) { scheduled = true; EditorApplication.delayCall += ProcessPending; }
                return;
            }
            var paths = pending.ToArray();
            pending.Clear();
            foreach (var path in paths)
            {
                try { Reconcile(path); failed.Remove(path); }
                catch (Exception error)
                {
                    if (!failed.TryGetValue(path, out var previous) || previous != error.Message)
                    { Debug.LogError("Animation export — " + path + ": " + error.Message); failed[path] = error.Message; }
                }
            }
        }

        static AppliedState ReadState(string userData)
        {
            var begin = (userData ?? "").IndexOf(StateBegin, StringComparison.Ordinal);
            if (begin < 0) return new AppliedState();
            var end = userData.IndexOf(StateEnd, begin + StateBegin.Length, StringComparison.Ordinal);
            if (end < 0) return new AppliedState();
            try { return JsonUtility.FromJson<AppliedState>(Encoding.UTF8.GetString(Convert.FromBase64String(userData.Substring(begin + StateBegin.Length, end - begin - StateBegin.Length)))) ?? new AppliedState(); }
            catch (FormatException) { return new AppliedState(); }
        }

        static string WriteState(string userData, AppliedState state)
        {
            userData = userData ?? "";
            var begin = userData.IndexOf(StateBegin, StringComparison.Ordinal);
            var end = begin < 0 ? -1 : userData.IndexOf(StateEnd, begin + StateBegin.Length, StringComparison.Ordinal);
            if (end >= 0) userData = userData.Remove(begin, end + StateEnd.Length - begin);
            return userData + StateBegin + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonUtility.ToJson(state))) + StateEnd;
        }

        static void ForgetAttempts(string path)
        {
            foreach (var key in attempts.Keys.Where(key => key.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase)).ToArray()) attempts.Remove(key);
        }

        public static AnimationEvent CloneEvent(AnimationEvent source, float factor = 1) => new AnimationEvent
        {
            time = source.time * factor, functionName = source.functionName, floatParameter = source.floatParameter,
            intParameter = source.intParameter, stringParameter = source.stringParameter,
            objectReferenceParameter = source.objectReferenceParameter, messageOptions = source.messageOptions
        };

        public static bool Reconcile(string path)
        {
            var source = DirectAnimationMetadata.Find(path);
            if (source == null) return false;
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (importer == null || model == null) return false;
            var defaults = importer.defaultClipAnimations;
            if (defaults.Length != 1) throw new InvalidDataException("Expected one animation take. Export this clip again from Blender.");
            var duration = (defaults[0].lastFrame - defaults[0].firstFrame) / source.clip.fps;
            if (duration <= 0 || Math.Abs(duration - source.clip.duration) > Math.Max(0.001f, 0.05f / source.clip.fps))
                throw new InvalidDataException("The imported range differs from the exported range.");
            var definitions = importer.clipAnimations;
            if (definitions.Length > 1) throw new InvalidOperationException("This FBX has custom multiple clips. Keep those settings or export to another filename.");
            var definition = definitions.Length == 1 ? definitions[0] : defaults[0];
            var settings = source.Settings;
            if (settings != null && EditorUtility.IsDirty(settings)) AssetDatabase.SaveAssetIfDirty(settings);
            var state = ReadState(importer.userData);
            if (!string.IsNullOrEmpty(state.clipId) && (state.clipId != source.clip.id || state.profileId != source.profile.profileId))
                throw new InvalidDataException("The FBX belongs to a different clip identity.");
            var before = EditorJsonUtility.ToJson(importer);
            importer.importAnimation = true;
            importer.optimizeGameObjects = false;
            importer.preserveHierarchy = true;
            var eventKey = new StringBuilder().Append(definition.firstFrame.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(definition.lastFrame.ToString("R", CultureInfo.InvariantCulture));
            foreach (var item in definition.events ?? Array.Empty<AnimationEvent>())
            {
                var objectKey = "";
                if (item.objectReferenceParameter != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(item.objectReferenceParameter, out string guid, out long localId)) objectKey = guid + ":" + localId;
                eventKey.Append('|').Append(item.time.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(item.functionName).Append(':')
                    .Append(item.stringParameter).Append(':').Append(item.floatParameter.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(item.intParameter).Append(':').Append(objectKey).Append(':').Append((int)item.messageOptions);
            }
            var settingsKey = settings == null ? "defaults" : JsonUtility.ToJson(settings);
            var signature = source.clip.sha256 + ":" + DirectAnimationMetadata.HashText(eventKey + settingsKey);
            var oldDuration = state.fps > 0 ? (definition.lastFrame - definition.firstFrame) / state.fps : duration;
            var events = (definition.events ?? Array.Empty<AnimationEvent>()).Select(item => CloneEvent(item)).ToArray();
            var factor = oldDuration > 0 ? oldDuration / duration : 1;
            var blocked = events.Any(item => item.time < 0 || float.IsNaN(item.time) || float.IsInfinity(item.time) || item.time * factor > 1.00001f);
            if (blocked)
            {
                if (state.blockedSignature == signature) { ForgetAttempts(path); return false; }
                var recovery = Path.Combine(Path.GetDirectoryName(Application.dataPath), ".animation-export-recovery", "DirectEvents", source.clip.id);
                Directory.CreateDirectory(recovery);
                var record = Path.Combine(recovery, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".meta.backup");
                File.Copy(path + ".meta", record, false);
                state.blockedSignature = signature;
                importer.userData = WriteState(importer.userData, state);
                // Source motion is already on disk. Retain the previous bounds and event data.
                EditorUtility.SetDirty(importer);
                AssetDatabase.WriteImportSettingsIfDirty(path);
                Debug.LogWarning("Animation export — " + source.clip.name + ": an event is outside the new duration. Previous event data and import bounds were retained. Adjust the events in Unity's Animation import settings. Recovery: " + record);
                return false;
            }
            ConfigureRig(importer, settings);
            if (importer.animationType == ModelImporterAnimationType.Generic && (settings == null || string.IsNullOrWhiteSpace(settings.motionNode)))
            {
                var roots = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(item => item.rootBone).Where(item => item != null && item != model.transform).Distinct().ToArray();
                if (roots.Length == 1) importer.motionNodeName = AnimationUtility.CalculateTransformPath(roots[0], model.transform);
                else
                {
                    var namedRoots = model.GetComponentsInChildren<Transform>(true).Where(item => !string.IsNullOrEmpty(source.profile.rootBone) && item.name == source.profile.rootBone).ToArray();
                    if (namedRoots.Length == 1) importer.motionNodeName = AnimationUtility.CalculateTransformPath(namedRoots[0], model.transform);
                    else if (model.transform.childCount == 1) importer.motionNodeName = model.transform.GetChild(0).name;
                    else throw new InvalidOperationException("Choose the Generic motion root once in Animation Import Settings.");
                }
            }
            var change = settings == null ? null : settings.overrides.FirstOrDefault(item => item.clip != null && AssetDatabase.GetAssetPath(item.clip) == path);
            definition.takeName = defaults[0].takeName;
            if (definitions.Length == 0 || string.IsNullOrEmpty(state.clipId)) definition.name = source.clip.name;
            definition.firstFrame = defaults[0].firstFrame;
            definition.lastFrame = defaults[0].lastFrame;
            definition.events = events.Select(item => CloneEvent(item, factor)).ToArray();
            definition.lockRootRotation = change != null && change.overrideRoot ? change.bakeRotation : settings == null || settings.bakeRotation;
            definition.lockRootHeightY = change != null && change.overrideRoot ? change.bakeY : settings == null || settings.bakeY;
            definition.lockRootPositionXZ = change != null && change.overrideRoot ? change.bakeXZ : settings == null || settings.bakeXZ;
            definition.keepOriginalOrientation = change != null && change.overrideRoot ? change.originalOrientation : settings == null || settings.originalOrientation;
            definition.keepOriginalPositionXZ = change != null && change.overrideRoot ? change.originalPositionXZ : settings == null || settings.originalPositionXZ;
            if (change != null && change.overrideMask)
            {
                if (change.mask == null) throw new InvalidOperationException("Assign the override mask or disable its override.");
                definition.maskType = ClipAnimationMaskType.CopyFromOther;
                definition.maskSource = change.mask;
                definition.ConfigureClipFromMask(change.mask);
            }
            else if ((settings == null || settings.includeAllTransforms) && !(definition.maskType == ClipAnimationMaskType.CopyFromOther && definition.maskSource != null))
            {
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
            importer.clipAnimations = new[] { definition };
            state.profileId = source.profile.profileId; state.clipId = source.clip.id; state.hash = source.clip.sha256;
            state.fps = source.clip.fps; state.duration = duration; state.blockedSignature = null;
            importer.userData = WriteState(importer.userData, state);
            if (before == EditorJsonUtility.ToJson(importer)) { ForgetAttempts(path); return false; }
            var key = path + ":" + source.clip.sha256 + ":" + DirectAnimationMetadata.HashText(settingsKey);
            attempts.TryGetValue(key, out var count);
            if (count >= 2) throw new InvalidOperationException("Automatic import did not converge. Existing event data was retained; review this clip's import settings.");
            attempts[key] = count + 1;
            EditorUtility.SetDirty(importer);
            ReconcileCount++;
            importer.SaveAndReimport();
            return true;
        }
    }
}
