using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
            public string[] fullMaskPaths;
            public bool preserveNativeMask;
            public float fps, duration;
            public string outputId;
            public ClipState[] clips;
            public RetiredName[] retiredNames;
            public string[] sharedMaskPaths;
        }

        [Serializable]
        sealed class ClipState
        {
            public string clipId, takeName, name, blockedSignature;
            public string[] fullMaskPaths;
            public bool preserveNativeMask;
            public float fps, duration, firstFrame, lastFrame;
            public bool retired;
            public bool sharedMask;
            public ClipSnapshot restore;
        }

        [Serializable]
        sealed class RetiredName
        {
            public string clipId, name;
        }

        [Serializable]
        sealed class AssetReference
        {
            public string guid, globalId;
            public long localId;
            public static AssetReference Capture(Object value)
            {
                if (value == null) return null;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long id))
                    throw new InvalidOperationException("An event or mask references an unsaved object. Save that asset before removing the clip.");
                return new AssetReference { guid = guid, localId = id, globalId = GlobalObjectId.GetGlobalObjectIdSlow(value).ToString() };
            }
            public Object Resolve(List<string> warnings)
            {
                if(string.IsNullOrEmpty(guid))return null;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault(value =>
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string foundGuid, out long id) && foundGuid == guid && id == localId);
                if (asset == null && !string.IsNullOrEmpty(globalId) && GlobalObjectId.TryParse(globalId, out var identifier))
                    asset = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(identifier);
                if (asset == null) warnings.Add("A restored clip references a deleted event or mask asset (" + guid + "). Its missing reference was left empty; other clips still update. Restore the asset or choose a replacement in Unity.");
                return asset;
            }
        }

        [Serializable]
        sealed class EventSnapshot
        {
            public float time, floatParameter;
            public int intParameter;
            public string functionName, stringParameter;
            public SendMessageOptions messageOptions;
            public AssetReference objectReference;
            public static EventSnapshot Capture(AnimationEvent value) => new EventSnapshot {
                time=value.time, floatParameter=value.floatParameter,intParameter=value.intParameter,
                functionName=value.functionName,stringParameter=value.stringParameter,messageOptions=value.messageOptions,
                objectReference=AssetReference.Capture(value.objectReferenceParameter) };
            public AnimationEvent Restore(List<string> warnings) => new AnimationEvent {
                time=time,floatParameter=floatParameter,intParameter=intParameter,functionName=functionName,
                stringParameter=stringParameter,messageOptions=messageOptions,
                objectReferenceParameter=objectReference==null?null:objectReference.Resolve(warnings) };
        }

        [Serializable]
        sealed class CurveSnapshot { public string name; public AnimationCurve curve; }

        [Serializable]
        sealed class ClipSnapshot
        {
            public bool valid;
            public string takeName,name;
            public float firstFrame,lastFrame,rotationOffset,heightOffset,cycleOffset,additiveReferencePoseFrame;
            public WrapMode wrapMode;
            public bool loop,loopTime,loopPose,lockRootRotation,lockRootHeightY,lockRootPositionXZ,
                keepOriginalOrientation,keepOriginalPositionY,keepOriginalPositionXZ,heightFromFeet,mirror,hasAdditiveReferencePose;
            public ClipAnimationMaskType maskType;
            public AssetReference maskSource;
            public bool[] bodyMask,transformActive;
            public string[] transformPaths;
            public EventSnapshot[] events;
            public CurveSnapshot[] curves;
            public static ClipSnapshot Capture(ModelImporterClipAnimation value)
            {
                var result=new ClipSnapshot {valid=true,takeName=value.takeName,name=value.name,firstFrame=value.firstFrame,lastFrame=value.lastFrame,
                    rotationOffset=value.rotationOffset,heightOffset=value.heightOffset,cycleOffset=value.cycleOffset,
                    additiveReferencePoseFrame=value.additiveReferencePoseFrame,wrapMode=value.wrapMode,loop=value.loop,
                    loopTime=value.loopTime,loopPose=value.loopPose,lockRootRotation=value.lockRootRotation,
                    lockRootHeightY=value.lockRootHeightY,lockRootPositionXZ=value.lockRootPositionXZ,
                    keepOriginalOrientation=value.keepOriginalOrientation,keepOriginalPositionY=value.keepOriginalPositionY,
                    keepOriginalPositionXZ=value.keepOriginalPositionXZ,heightFromFeet=value.heightFromFeet,mirror=value.mirror,
                    hasAdditiveReferencePose=value.hasAdditiveReferencePose,maskType=value.maskType,maskSource=AssetReference.Capture(value.maskSource),
                    events=(value.events??Array.Empty<AnimationEvent>()).Select(EventSnapshot.Capture).ToArray(),
                    curves=(value.curves??Array.Empty<ClipAnimationInfoCurve>()).Select(curve=>new CurveSnapshot{name=curve.name,curve=curve.curve}).ToArray()};
                var mask=new AvatarMask();
                try
                {
                    value.ConfigureMaskFromClip(ref mask);
                    result.bodyMask=Enumerable.Range(0,(int)AvatarMaskBodyPart.LastBodyPart).Select(index=>mask.GetHumanoidBodyPartActive((AvatarMaskBodyPart)index)).ToArray();
                    result.transformPaths=Enumerable.Range(0,mask.transformCount).Select(mask.GetTransformPath).ToArray();
                    result.transformActive=Enumerable.Range(0,mask.transformCount).Select(mask.GetTransformActive).ToArray();
                }
                finally {Object.DestroyImmediate(mask);}
                return result;
            }
            public ModelImporterClipAnimation Restore(List<string> warnings)
            {
                var result=new ModelImporterClipAnimation {takeName=takeName,name=name,firstFrame=firstFrame,lastFrame=lastFrame,
                    rotationOffset=rotationOffset,heightOffset=heightOffset,cycleOffset=cycleOffset,additiveReferencePoseFrame=additiveReferencePoseFrame,
                    wrapMode=wrapMode,loop=loop,loopTime=loopTime,loopPose=loopPose,lockRootRotation=lockRootRotation,
                    lockRootHeightY=lockRootHeightY,lockRootPositionXZ=lockRootPositionXZ,keepOriginalOrientation=keepOriginalOrientation,
                    keepOriginalPositionY=keepOriginalPositionY,keepOriginalPositionXZ=keepOriginalPositionXZ,heightFromFeet=heightFromFeet,
                    mirror=mirror,hasAdditiveReferencePose=hasAdditiveReferencePose,
                    events=(events??Array.Empty<EventSnapshot>()).Select(value=>value.Restore(warnings)).ToArray(),
                    curves=(curves??Array.Empty<CurveSnapshot>()).Select(value=>new ClipAnimationInfoCurve{name=value.name,curve=value.curve}).ToArray()};
                var mask=new AvatarMask();
                try
                {
                    for(var index=0;index<(bodyMask?.Length??0);index++)mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)index,bodyMask[index]);
                    mask.transformCount=transformPaths?.Length??0;
                    for(var index=0;index<mask.transformCount;index++) {mask.SetTransformPath(index,transformPaths[index]);mask.SetTransformActive(index,transformActive[index]);}
                    result.ConfigureClipFromMask(mask);
                }
                finally {Object.DestroyImmediate(mask);}
                result.maskType=maskType;result.maskSource=maskSource==null?null:(AvatarMask)maskSource.Resolve(warnings);
                return result;
            }
        }

        const string StateBegin = "\n[CharacterAnimationExport/3:";
        const string LegacyBegin = "\n[CharacterAnimationExport/2:";
        const string StateEnd = "]\n";
        static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> attempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> setupNotified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool scheduled;
        static readonly Regex nameWords = new Regex(@"[^\p{L}]+|(?<=\p{Ll})(?=\p{Lu})|(?<=\p{Lu})(?=\p{Lu}\p{Ll})");
        public static int ReconcileCount { get; private set; }

        static bool IsIdleName(string name) => !string.IsNullOrEmpty(name) &&
            nameWords.Split(name).Any(word => string.Equals(word, "Idle", StringComparison.OrdinalIgnoreCase));

        static void ConfigureCompression(ModelImporter importer, AnimationImportSettings settings)
        {
            if (!settings.setCompression) return;
            var mode = settings.CompressionMode();
            if (importer.animationCompression != mode) importer.animationCompression = mode;
            if (mode == ModelImporterAnimationCompression.Off) return;
            if (importer.animationRotationError != settings.rotationError) importer.animationRotationError = settings.rotationError;
            if (importer.animationPositionError != settings.positionError) importer.animationPositionError = settings.positionError;
            if (importer.animationScaleError != settings.scaleError) importer.animationScaleError = settings.scaleError;
        }

        // Deliberately no model preprocess callbacks: installing/changing those
        // invalidates the import cache for every model in a large project.

        static void ConfigureRig(ModelImporter importer, AnimationImportSettings settings)
        {
            var human = settings != null && settings.rig == CharacterRig.Humanoid;
            if (human && (settings.sourceAvatar == null || !settings.sourceAvatar.isValid || !settings.sourceAvatar.isHuman))
                throw new InvalidOperationException("Choose the matching Humanoid Avatar in this character's Animation Import Settings.");
            var rig = human ? ModelImporterAnimationType.Human : ModelImporterAnimationType.Generic;
            var setup = human ? ModelImporterAvatarSetup.CopyFromOther : ModelImporterAvatarSetup.CreateFromThisModel;
            var avatar = human ? settings.sourceAvatar : null;
            if (importer.animationType != rig) importer.animationType = rig;
            if (importer.avatarSetup != setup) importer.avatarSetup = setup;
            if (importer.sourceAvatar != avatar) importer.sourceAvatar = avatar;
            if (settings != null && !string.IsNullOrWhiteSpace(settings.motionNode) && importer.motionNodeName != settings.motionNode) importer.motionNodeName = settings.motionNode;
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
                    { Debug.LogError("Animation export - " + path + ": " + error.Message); failed[path] = error.Message; }
                }
            }
        }

        static AppliedState ReadState(string userData)
        {
            var begin = (userData ?? "").IndexOf(StateBegin, StringComparison.Ordinal);
            var marker = StateBegin;
            if (begin < 0) { marker = LegacyBegin; begin = (userData ?? "").IndexOf(marker, StringComparison.Ordinal); }
            if (begin < 0) return new AppliedState();
            var end = userData.IndexOf(StateEnd, begin + marker.Length, StringComparison.Ordinal);
            if (end < 0) return new AppliedState();
            try
            {
                var state=JsonUtility.FromJson<AppliedState>(Encoding.UTF8.GetString(Convert.FromBase64String(userData.Substring(begin + marker.Length, end - begin - marker.Length)))) ?? new AppliedState();
                foreach(var clip in state.clips??Array.Empty<ClipState>())if(clip.sharedMask)clip.fullMaskPaths=state.sharedMaskPaths;
                return state;
            }
            catch (FormatException) { return new AppliedState(); }
        }

        static string WriteState(string userData, AppliedState state)
        {
            // Ordinary full masks share one path inventory across all takes.
            var common=(state.clips??Array.Empty<ClipState>()).Where(clip=>clip.fullMaskPaths!=null)
                .GroupBy(clip=>string.Join("\n",clip.fullMaskPaths),StringComparer.Ordinal).OrderByDescending(group=>group.Count()).FirstOrDefault();
            state.sharedMaskPaths=common!=null&&common.Count()>1?common.First().fullMaskPaths:null;
            foreach(var clip in state.clips??Array.Empty<ClipState>())
            {
                clip.sharedMask=state.sharedMaskPaths!=null&&clip.fullMaskPaths!=null&&clip.fullMaskPaths.SequenceEqual(state.sharedMaskPaths);
                if(clip.sharedMask)clip.fullMaskPaths=null;
            }
            userData = userData ?? "";
            foreach (var marker in new[] { StateBegin, LegacyBegin })
            {
                var begin = userData.IndexOf(marker, StringComparison.Ordinal);
                var end = begin < 0 ? -1 : userData.IndexOf(StateEnd, begin + marker.Length, StringComparison.Ordinal);
                if (end >= 0) userData = userData.Remove(begin, end + StateEnd.Length - begin);
            }
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

        static string EventSignature(ModelImporterClipAnimation definition, string fileHash, string settingsKey)
        {
            var key = new StringBuilder().Append(definition.firstFrame.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(definition.lastFrame.ToString("R", CultureInfo.InvariantCulture));
            foreach (var item in definition.events ?? Array.Empty<AnimationEvent>())
            {
                var objectKey = "";
                if (item.objectReferenceParameter != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(item.objectReferenceParameter, out string guid, out long localId)) objectKey = guid + ":" + localId;
                key.Append('|').Append(item.time.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(item.functionName).Append(':')
                    .Append(item.stringParameter).Append(':').Append(item.floatParameter.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(item.intParameter).Append(':').Append(objectKey).Append(':').Append((int)item.messageOptions);
            }
            return fileHash + ":" + DirectAnimationMetadata.HashText(key + settingsKey);
        }

        static string BackupEvents(string path, string identity)
        {
            var folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), ".animation-export-recovery", "DirectEvents", identity);
            Directory.CreateDirectory(folder);
            var record = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".meta.backup");
            File.Copy(path + ".meta", record, false);
            return record;
        }

        static void ConfigureMask(ModelImporterClipAnimation definition, ClipState state, GameObject model, bool existing, ClipImportOverride change, AnimationImportSettings settings)
        {
            if (change != null && change.overrideMask)
            {
                if (change.mask == null) throw new InvalidOperationException("Assign the override mask or disable its override.");
                definition.maskType = ClipAnimationMaskType.CopyFromOther;
                definition.maskSource = change.mask;
                definition.ConfigureClipFromMask(change.mask);
                return;
            }
            if (!settings.includeAllTransforms || (definition.maskType == ClipAnimationMaskType.CopyFromOther && definition.maskSource != null)) return;
            var mask = new AvatarMask();
            try
            {
                definition.ConfigureMaskFromClip(ref mask);
                var known = state.fullMaskPaths == null ? null : new HashSet<string>(state.fullMaskPaths, StringComparer.Ordinal);
                var edited = state.preserveNativeMask;
                if (existing)
                {
                    for (var part = 0; part < (int)AvatarMaskBodyPart.LastBodyPart; part++) edited |= !mask.GetHumanoidBodyPartActive((AvatarMaskBodyPart)part);
                    for (var index = 0; index < mask.transformCount; index++)
                        if ((known == null || known.Contains(mask.GetTransformPath(index))) && !mask.GetTransformActive(index)) edited = true;
                }
                if (edited) { state.preserveNativeMask = true; return; }
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
                state.fullMaskPaths = Enumerable.Range(0, mask.transformCount).Select(mask.GetTransformPath).ToArray();
                state.preserveNativeMask = false;
            }
            finally { Object.DestroyImmediate(mask); }
        }

        public static bool Reconcile(string path)
        {
            var source = DirectAnimationMetadata.Find(path);
            if (source == null) return false;
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (importer == null || model == null) return false;
            var defaults = importer.defaultClipAnimations;
            if (defaults.Length != source.clips.Length) throw new InvalidDataException("The FBX take list differs from its export data. Export it again from Blender.");
            var definitions = importer.clipAnimations;
            var settings = source.Settings;
            if (settings == null || settings.rig == CharacterRig.Unset ||
                (settings.rig == CharacterRig.Humanoid && (settings.sourceAvatar == null || !settings.sourceAvatar.isValid || !settings.sourceAvatar.isHuman)))
            {
                if (setupNotified.Add(source.SettingsPath)) Debug.LogWarning("Animation export - choose the rig and matching Avatar once for " + source.folder + " using Assets > Animation Import Settings.");
                return false;
            }
            setupNotified.Remove(source.SettingsPath);
            if (settings.setCompression && settings.CompressionMode() != ModelImporterAnimationCompression.Off)
            {
                settings.ValidateCompressionErrors();
                if (settings.NormalizeCompressionErrors()) EditorUtility.SetDirty(settings);
            }
            if (EditorUtility.IsDirty(settings)) AssetDatabase.SaveAssetIfDirty(settings);
            var state = ReadState(importer.userData);
            if ((!string.IsNullOrEmpty(state.profileId) && state.profileId != source.profile.profileId) ||
                (!string.IsNullOrEmpty(state.outputId) && state.outputId != source.outputId))
                throw new InvalidDataException("This FBX belongs to a different export identity.");
            var oldStates = state.clips ?? Array.Empty<ClipState>();
            var legacySingle = source.profile.schemaVersion == 2 && !source.combined &&
                oldStates.Length == 0 && !string.IsNullOrEmpty(state.clipId);
            if (oldStates.Length == 0 && !string.IsNullOrEmpty(state.clipId))
            {
                if (source.clips.Length != 1 || source.clip.id != state.clipId) throw new InvalidDataException("This FBX belongs to a different clip identity.");
                oldStates = new[] { new ClipState { clipId = state.clipId, takeName = defaults[0].takeName,
                    fps = state.fps, duration = state.duration, fullMaskPaths = state.fullMaskPaths,
                    preserveNativeMask = state.preserveNativeMask, blockedSignature = state.blockedSignature } };
            }
            var managedTakes = new HashSet<string>(oldStates.Select(item => item.takeName), StringComparer.Ordinal);
            var nativeTakes = new HashSet<string>(defaults.Select(item => item.takeName), StringComparer.Ordinal);
            if (definitions.Any(item => !nativeTakes.Contains(item.takeName) && !managedTakes.Contains(item.takeName)))
                throw new InvalidOperationException("This FBX has custom clip definitions. Keep those settings or export to another filename.");
            var retired = (state.retiredNames ?? Array.Empty<RetiredName>()).ToList();
            var removed = oldStates.Where(item => !item.retired && !source.clips.Any(clip => clip.id == item.clipId)).ToArray();
            var nextDefinitions = new List<ModelImporterClipAnimation>();
            var nextStates = new List<ClipState>();
            var settingsKey = EditorJsonUtility.ToJson(settings);
            var before = EditorJsonUtility.ToJson(importer);
            var warnings = new List<string>();
            foreach (var old in removed)
            {
                var definition = definitions.FirstOrDefault(item => item.takeName == old.takeName);
                if(definition!=null)old.restore=ClipSnapshot.Capture(definition);
                old.retired=true;
                var name = definition == null ? old.name : definition.name;
                if (!string.IsNullOrEmpty(name) && !retired.Any(item => item.clipId == old.clipId && item.name == name))
                    retired.Add(new RetiredName { clipId = old.clipId, name = name });
            }
            foreach (var clip in source.clips)
            {
                var candidates = defaults.Where(item => item.takeName == clip.takeName || item.takeName.EndsWith("|" + clip.takeName, StringComparison.Ordinal)).ToArray();
                if (!source.combined && defaults.Length == 1) candidates = defaults;
                if (candidates.Length != 1) throw new InvalidDataException("Cannot identify take " + clip.name + ". Export it again from Blender.");
                var native = candidates[0];
                var duration = (native.lastFrame - native.firstFrame) / clip.fps;
                var frameSpan=clip.duration*clip.fps;
                var fractional=Math.Abs(frameSpan-Mathf.Round(frameSpan))>0.0001f;
                // Unity rounds an FBX take's default stop frame upwards. Its
                // explicit clip definition supports the exact fractional bound.
                var roundedEnvelope=source.combined&&fractional&&Math.Abs(native.lastFrame-native.firstFrame-Mathf.Ceil(frameSpan))<0.0001f;
                if (duration <= 0 || (!roundedEnvelope&&Math.Abs(duration - clip.duration) > Math.Max(0.001f, 0.05f / clip.fps)))
                    throw new InvalidDataException("The imported range differs from the exported range for " + clip.name + ": Unity " + native.firstFrame + " to " + native.lastFrame + " (" + duration + "s), exported " + clip.duration + "s.");
                if(roundedEnvelope)duration=clip.duration;
                var lastFrame=roundedEnvelope?native.firstFrame+frameSpan:native.lastFrame;
                var matches = definitions.Where(item => item.takeName == native.takeName).ToArray();
                if (matches.Length > 1) throw new InvalidOperationException("This FBX has custom multiple clips for " + clip.name + ". Keep those settings or use another filename.");
                var existing = matches.Length == 1;
                var definition = existing ? matches[0] : native;
                var old = oldStates.FirstOrDefault(item => item.clipId == clip.id);
                if(!existing&&old!=null&&old.retired&&old.restore!=null&&old.restore.valid) {definition=old.restore.Restore(warnings);existing=true;}
                var next = old ?? new ClipState { clipId = clip.id };
                next.retired=false;
                var oldDuration = old == null ? duration :
                    old.lastFrame > old.firstFrame ? old.duration * (definition.lastFrame - definition.firstFrame) / (old.lastFrame - old.firstFrame) :
                    old.fps > 0 ? (definition.lastFrame - definition.firstFrame) / old.fps : old.duration;
                var factor = oldDuration > 0 ? oldDuration / duration : 1;
                var events = (definition.events ?? Array.Empty<AnimationEvent>()).Select(item => CloneEvent(item)).ToArray();
                var signature = EventSignature(definition, clip.sha256, settingsKey);
                var blocked = events.Any(item => item.time < 0 || float.IsNaN(item.time) || float.IsInfinity(item.time) || item.time * factor > 1.00001f);
                if (blocked)
                {
                    if (next.blockedSignature != signature)
                    {
                        var record = BackupEvents(path, clip.id);
                        warnings.Add(clip.name + ": an event is outside the new duration. Its previous event data and import bounds were retained; the new take cannot restore motion beyond its end. Adjust the cue in Unity. Recovery: " + record);
                    }
                    next.blockedSignature = signature;
                    next.takeName = native.takeName;
                    next.name = definition.name;
                    nextDefinitions.Add(definition); nextStates.Add(next);
                    continue;
                }
                if (!existing || (old == null && definition.name == native.name)) definition.name = clip.name;
                if (retired.Any(item => item.clipId != clip.id && string.Equals(item.name, definition.name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The name " + definition.name + " belonged to another removed clip. Choose a new export name to protect old references.");
                var changes = settings.overrides.Where(item => item.clip != null && AssetDatabase.GetAssetPath(item.clip) == path && item.clip.name == definition.name).ToArray();
                if (changes.Length > 1) throw new InvalidOperationException("Multiple overrides target " + definition.name);
                var change = changes.FirstOrDefault();
                definition.takeName = native.takeName;
                definition.firstFrame = native.firstFrame; definition.lastFrame = lastFrame;
                definition.events = events.Select(item => CloneEvent(item, factor)).ToArray();
                if (change != null && change.overrideLoop) definition.loopTime = change.loopTime;
                else if (settings.loopIdleClips && IsIdleName(definition.name)) definition.loopTime = true;
                definition.lockRootRotation = change != null && change.overrideRoot ? change.bakeRotation : settings.bakeRotation;
                definition.lockRootHeightY = change != null && change.overrideRoot ? change.bakeY : settings.bakeY;
                definition.lockRootPositionXZ = change != null && change.overrideRoot ? change.bakeXZ : settings.bakeXZ;
                definition.keepOriginalOrientation = change != null && change.overrideRoot ? change.originalOrientation : settings.originalOrientation;
                definition.keepOriginalPositionXZ = change != null && change.overrideRoot ? change.originalPositionXZ : settings.originalPositionXZ;
                ConfigureMask(definition, next, model, existing, change, settings);
                next.takeName = native.takeName; next.name = definition.name;
                next.fps = clip.fps; next.duration = duration;
                next.firstFrame = native.firstFrame; next.lastFrame = lastFrame;
                next.blockedSignature = null;
                next.restore = null;
                nextDefinitions.Add(definition); nextStates.Add(next);
            }
            if (nextDefinitions.Select(item => item.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != nextDefinitions.Count)
                throw new InvalidOperationException("Two imported clips have the same name. Rename the conflicting clip in Unity or Blender.");
            if (removed.Length > 0)
                warnings.Add("Removed " + removed.Length + " confirmed clips. Previous native event/import data was backed up: " + BackupEvents(path, source.outputId));
            ConfigureRig(importer, settings);
            ConfigureCompression(importer, settings);
            if (importer.animationType == ModelImporterAnimationType.Generic && string.IsNullOrWhiteSpace(settings.motionNode) &&
                (string.IsNullOrEmpty(importer.motionNodeName) || model.transform.Find(importer.motionNodeName) == null))
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
            importer.importAnimation = true; importer.optimizeGameObjects = false; importer.preserveHierarchy = true;
            importer.clipAnimations = nextDefinitions.ToArray();
            var requiresImport = before != EditorJsonUtility.ToJson(importer);
            // A format-only userdata migration also schedules a native model
            // import after refresh. Keep unchanged v2 separate files byte-for-byte
            // until their inputs, settings or recovery state actually change.
            var single = nextStates.Count == 1 ? nextStates[0] : null;
            var keepLegacy = legacySingle && !requiresImport && single != null &&
                state.profileId == source.profile.profileId && state.hash == source.clip.sha256 &&
                state.fps == single.fps && state.duration == single.duration &&
                state.preserveNativeMask == single.preserveNativeMask &&
                (state.blockedSignature ?? "") == (single.blockedSignature ?? "") &&
                (state.fullMaskPaths ?? Array.Empty<string>()).SequenceEqual(single.fullMaskPaths ?? Array.Empty<string>());
            state.profileId = source.profile.profileId; state.outputId = source.outputId; state.hash = source.clip.sha256;
            state.clips = nextStates.Concat(oldStates.Where(item=>item.retired&&!nextStates.Any(next=>next.clipId==item.clipId))).ToArray(); state.retiredNames = retired.ToArray();
            // Clear legacy fields after migration; per-take state is the single timing authority.
            state.clipId = null; state.blockedSignature = null; state.fullMaskPaths = null;
            state.fps = state.duration = 0; state.preserveNativeMask = false;
            var userData = keepLegacy ? importer.userData : WriteState(importer.userData, state);
            if (importer.userData != userData)
            {
                importer.userData = userData;
                EditorUtility.SetDirty(importer);
                if (!requiresImport) AssetDatabase.WriteImportSettingsIfDirty(path);
            }
            foreach (var warning in warnings) Debug.LogWarning("Animation export - " + warning);
            if (!requiresImport) { ForgetAttempts(path); return false; }
            var key = path + ":" + source.clip.sha256 + ":" + DirectAnimationMetadata.HashText(settingsKey);
            attempts.TryGetValue(key, out var count);
            if (count >= 2) throw new InvalidOperationException("Automatic import did not converge. Existing event data was retained; review this FBX's import settings.");
            attempts[key] = count + 1;
            EditorUtility.SetDirty(importer); ReconcileCount++;
            importer.SaveAndReimport();
            return true;
        }
    }
}
