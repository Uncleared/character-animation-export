using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CharacterAnimationExport
{
    public enum ExportRigType { Generic, Humanoid }
    public enum EventTimingChoice { ReviewFirst, KeepSeconds, ScaleProportionally }

    [Serializable]
    public sealed class ExportPackage
    {
        public int schemaVersion;
        public string profileId, publicationId, characterName, blenderVersion;
        public int generation;
        public float fps;
        public string[] warnings;
        public ExportedClip[] clips;
    }

    [Serializable]
    public sealed class ExportedClip
    {
        public string id, name, state, file, sha256, takeName;
        public bool enabled, exported;
        public float startFrame, endFrame, fps, duration;
        public string[] objectPaths;
    }

    [Serializable]
    public sealed class ManagedAnimationClip
    {
        public string id, incomingName, gameplayName, sourceHash, sourceFbx, nativePath;
        public string status, pendingJson, deletedHash, recoveryRecord;
        public AnimationClip clip;
        public bool archived, deleted, keepGameplayName, overrideRoot, bakeRotation = true, bakeY = true, bakeXZ = true;
        public bool overrideMask;
        public AvatarMask customMask;
    }

    [CreateAssetMenu(menuName = "Animation Export/Character Library", fileName = "CharacterAnimations")]
    public sealed class AnimationExportLibrary : ScriptableObject
    {
        public string profileId, characterName, lastManifest, generatedFolder;
        public int generation;
        public ExportRigType rigType;
        public Avatar sourceAvatar;
        public string motionNode;
        public bool autoConfigureImport = true, bakeRotation = true, bakeY = true, bakeXZ = true;
        public bool importAllTransforms = true;
        public List<ManagedAnimationClip> clips = new List<ManagedAnimationClip>();
    }

    public static class AnimationExportEvents
    {
        public static AnimationEvent Clone(AnimationEvent source, float multiplier = 1)
        {
            return new AnimationEvent
            {
                time = source.time * multiplier, functionName = source.functionName,
                floatParameter = source.floatParameter, intParameter = source.intParameter,
                stringParameter = source.stringParameter, objectReferenceParameter = source.objectReferenceParameter,
                messageOptions = source.messageOptions
            };
        }

        public static void Save(AnimationClip clip, AnimationEvent[] events)
        {
            if (clip == null || !AssetDatabase.GetAssetPath(clip).EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a managed editable animation clip.");
            foreach (var item in events)
            {
                if (string.IsNullOrWhiteSpace(item.functionName)) throw new InvalidOperationException("Each event needs a function name.");
                if (float.IsNaN(item.time) || item.time < 0 || item.time > clip.length + 0.0001f)
                    throw new InvalidOperationException("Event times must be inside the clip.");
            }
            Undo.RecordObject(clip, "Edit animation events");
            AnimationUtility.SetAnimationEvents(clip, events);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssetIfDirty(clip);
        }
    }
}
