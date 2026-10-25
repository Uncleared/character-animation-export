using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CharacterAnimationEvents
{
    [Serializable]
    public sealed class EventValue
    {
        public float time, number;
        public int integer;
        public string function, text, objectGuid;
        public long objectLocalId;
        public SendMessageOptions receiver;

        public static EventValue From(AnimationEvent item)
        {
            var result = new EventValue { time = item.time, number = item.floatParameter, integer = item.intParameter,
                function = item.functionName, text = item.stringParameter, receiver = item.messageOptions };
            if (item.objectReferenceParameter != null)
            {
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(item.objectReferenceParameter, out result.objectGuid, out result.objectLocalId))
                    throw new InvalidOperationException("Animation event objects must be saved project assets.");
            }
            return result;
        }

        public AnimationEvent ToEvent()
        {
            UnityEngine.Object value = null;
            if (!string.IsNullOrEmpty(objectGuid))
            {
                value = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(objectGuid)).FirstOrDefault(item =>
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(item, out string _guid, out long id) && id == objectLocalId);
                if (value == null) throw new InvalidOperationException("An event's saved object reference is missing. Restore it or choose another asset.");
            }
            return new AnimationEvent { time = time, floatParameter = number, intParameter = integer, functionName = function,
                stringParameter = text, objectReferenceParameter = value, messageOptions = receiver };
        }
    }

    public static class AnimationEventClipIO
    {
        public static AnimationEvent Clone(AnimationEvent item, float factor = 1) => new AnimationEvent
        {
            time = item.time * factor, functionName = item.functionName, stringParameter = item.stringParameter,
            floatParameter = item.floatParameter, intParameter = item.intParameter,
            objectReferenceParameter = item.objectReferenceParameter, messageOptions = item.messageOptions
        };

        public static string Snapshot(AnimationClip clip)
        {
            if (clip == null) return "";
            var result = new StringBuilder();
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long id);
            result.Append(guid).Append(':').Append(id).Append(':').Append(clip.length.ToString("R", CultureInfo.InvariantCulture));
            foreach (var item in AnimationUtility.GetAnimationEvents(clip)) result.Append('\n').Append(JsonUtility.ToJson(EventValue.From(item)));
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(result.ToString()))).Replace("-", "");
        }

        public static void Save(AnimationClip clip, AnimationEvent[] events, string expectedSnapshot)
        {
            if (clip == null) throw new InvalidOperationException("Choose an animation clip.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before saving events.");
            if (Snapshot(clip) != expectedSnapshot) throw new InvalidOperationException("This clip changed in another editor. Reload its events before saving this draft.");
            foreach (var item in events)
            {
                if (string.IsNullOrWhiteSpace(item.functionName)) throw new InvalidOperationException("Give each event a function name.");
                if (float.IsNaN(item.time) || float.IsInfinity(item.time) || item.time < 0 || item.time > clip.length + 0.0001f)
                    throw new InvalidOperationException("Keep event times inside the clip.");
                EventValue.From(item);
            }
            var path = AssetDatabase.GetAssetPath(clip);
            if (path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            {
                Undo.RecordObject(clip, "Edit animation events");
                AnimationUtility.SetAnimationEvents(clip, events.Select(item => Clone(item)).ToArray());
                EditorUtility.SetDirty(clip);
                AssetDatabase.SaveAssetIfDirty(clip);
                return;
            }
            if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || !(AssetImporter.GetAtPath(path) is ModelImporter importer))
                throw new InvalidOperationException("Choose an editable .anim or an imported FBX clip.");
            var definitions = importer.clipAnimations;
            if (definitions.Length == 0) definitions = importer.defaultClipAnimations;
            var matches = definitions.Where(item => item.name == clip.name).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("The selected FBX take is ambiguous. Give it a unique clip name in Import Settings.");
            var selected = matches[0];
            Undo.RecordObject(importer, "Edit FBX animation events");
            selected.events = events.Select(item => Clone(item, clip.length > 0 ? 1 / clip.length : 1)).ToArray();
            importer.clipAnimations = definitions;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }
    }
}
