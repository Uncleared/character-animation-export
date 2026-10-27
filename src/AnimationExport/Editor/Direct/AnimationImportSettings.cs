using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CharacterAnimationExport.Direct
{
    public enum CharacterRig { [InspectorName("Choose Rig")] Unset = -1, Generic = 0, Humanoid = 1 }
    public enum AnimationCompression { Off = 0, KeyframeReduction = 1, Optimal = 3 }

    [Serializable]
    public sealed class ClipImportOverride
    {
        public AnimationClip clip;
        public bool overrideRoot;
        public bool bakeRotation = true, bakeY = true, bakeXZ = true;
        public bool originalOrientation = true, originalPositionXZ = true;
        public bool overrideMask;
        public AvatarMask mask;
        [Tooltip("Use this clip's Loop Time instead of the automatic Idle rule.")]
        public bool overrideLoop;
        public bool loopTime;
    }

    public sealed class AnimationImportSettings : ScriptableObject
    {
        [HideInInspector] public string profileId, characterName;
        public CharacterRig rig = CharacterRig.Unset;
        public Avatar sourceAvatar;
        public string motionNode;
        [Tooltip("Apply these compression settings to this character's exported FBXs. Disable to edit compression in Unity's native importer.")]
        public bool setCompression = true;
        public AnimationCompression compression = AnimationCompression.Optimal;
        [Delayed] public float rotationError = 0.01f;
        [Delayed] public float positionError = 0.01f;
        [Delayed] public float scaleError = 0.5f;
        [Tooltip("Enable Loop Time for Idle names, ignoring case. Use Clip Overrides for a non-looping exception. Disabling this leaves current loop values unchanged.")]
        public bool loopIdleClips = true;
        public bool bakeRotation = true, bakeY = true, bakeXZ = true, includeAllTransforms = true;
        [Tooltip("Use the authored facing instead of calculating a different body-facing reference for each clip.")]
        public bool originalOrientation = true;
        [Tooltip("Keep the authored horizontal origin so separately exported clips share the same reference.")]
        public bool originalPositionXZ = true;
        public List<ClipImportOverride> overrides = new List<ClipImportOverride>();

        void OnValidate()
        {
            NormalizeCompressionErrors();
            var path = AssetDatabase.GetAssetPath(this);
            if (!string.IsNullOrEmpty(path)) DirectAnimationImporter.QueueFolder(Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace('\\', '/'));
        }

        internal bool NormalizeCompressionErrors()
        {
            var previous = new Vector3(rotationError, positionError, scaleError);
            if (!float.IsNaN(rotationError) && !float.IsInfinity(rotationError)) rotationError = Mathf.Clamp(rotationError, 0, 100);
            if (!float.IsNaN(positionError) && !float.IsInfinity(positionError)) positionError = Mathf.Clamp(positionError, 0, 100);
            if (!float.IsNaN(scaleError) && !float.IsInfinity(scaleError)) scaleError = Mathf.Clamp(scaleError, 0, 100);
            return previous.x != rotationError || previous.y != positionError || previous.z != scaleError;
        }

        internal ModelImporterAnimationCompression CompressionMode()
        {
            switch (compression)
            {
                case AnimationCompression.Off: return ModelImporterAnimationCompression.Off;
                case AnimationCompression.KeyframeReduction: return ModelImporterAnimationCompression.KeyframeReduction;
                case AnimationCompression.Optimal: return ModelImporterAnimationCompression.Optimal;
                default: throw new InvalidOperationException("Choose Off, Keyframe Reduction or Optimal in Animation Import Settings.");
            }
        }

        internal void ValidateCompressionErrors()
        {
            if (new[] { rotationError, positionError, scaleError }.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
                throw new InvalidOperationException("Animation compression errors must be finite numbers. Correct Rotation, Position and Scale Error in Animation Import Settings.");
        }
    }

    [Serializable]
    public sealed class DirectClip
    {
        public string id, name, state, file, sha256, takeName;
        public bool enabled, exported;
        public float startFrame, endFrame, fps, duration;
        public string[] objectPaths;
    }

    [Serializable]
    public sealed class DirectProfile
    {
        public int schemaVersion, generation;
        public string profileId, publicationId, characterName, rootBone, layout;
        public DirectClip[] clips;
        public DirectOutput[] outputs;
        public DirectRetiredName[] retiredNames;
    }

    [Serializable]
    public sealed class DirectOutput
    {
        public string id, file, sha256, kind;
    }

    [Serializable]
    public sealed class DirectRetiredName
    {
        public string id, file, name;
    }

    public sealed class DirectSource
    {
        public string folder, metadataPath, assetPath;
        public DirectProfile profile;
        public DirectClip clip;
        public DirectClip[] clips;
        public string outputId;
        public bool combined;
        public string SettingsPath => folder + "/AnimationExportData/" + profile.profileId + ".settings.asset";
        public AnimationImportSettings Settings => AssetDatabase.LoadAssetAtPath<AnimationImportSettings>(SettingsPath);
    }

    public static class DirectAnimationMetadata
    {
        public static string HashFile(string path)
        {
            using (var file = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }

        public static string HashText(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        static bool Id(string value) => value != null && value.Length == 32 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Hash(string value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        static bool Filename(string value) => !string.IsNullOrEmpty(value) && Path.GetFileName(value) == value &&
            !value.Contains('\\') && !value.Contains('/') && !value.Contains(':') && value.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> invalidProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static DirectSource[] ParseProfile(string folder, string path)
        {
            var profile = JsonUtility.FromJson<DirectProfile>(File.ReadAllText(path));
            if (profile == null || (profile.schemaVersion != 2 && profile.schemaVersion != 3) || !Id(profile.profileId) || !Id(profile.publicationId) || profile.clips == null ||
                Path.GetFileName(path) != profile.profileId + ".animation-export.json")
                throw new InvalidDataException("Invalid animation export metadata: " + path);
            var ids = new HashSet<string>();
            var outputs = new Dictionary<string, DirectOutput>(StringComparer.OrdinalIgnoreCase);
            if (profile.schemaVersion == 3)
            {
                foreach (var output in profile.outputs ?? Array.Empty<DirectOutput>())
                {
                    if (output == null || !Id(output.id) || !Filename(output.file) || !Hash(output.sha256) ||
                        (output.kind != "combined" && output.kind != "separate") || outputs.ContainsKey(output.file))
                        throw new InvalidDataException("Invalid FBX output ownership in " + path);
                    outputs.Add(output.file, output);
                }
            }
            var active = new List<DirectClip>();
            foreach (var clip in profile.clips)
            {
                if (clip == null || !Id(clip.id) || !ids.Add(clip.id)) throw new InvalidDataException("Invalid clip identity in " + path);
                if (string.IsNullOrEmpty(clip.file))
                {
                    if (clip.exported) throw new InvalidDataException("Missing animation filename.");
                    continue;
                }
                if (!Filename(clip.file) || string.IsNullOrWhiteSpace(clip.name) || string.IsNullOrWhiteSpace(clip.takeName) || !Hash(clip.sha256) ||
                    !Finite(clip.fps) || clip.fps <= 0 || !Finite(clip.duration) || clip.duration <= 0)
                    throw new InvalidDataException("Invalid animation ownership or timing in " + path);
                active.Add(clip);
            }
            var result = new List<DirectSource>();
            foreach (var group in active.GroupBy(clip => clip.file, StringComparer.OrdinalIgnoreCase))
            {
                var clips = group.ToArray();
                var first = clips[0];
                var isCombined = false;
                var outputId = first.id;
                if (profile.schemaVersion == 3)
                {
                    if (!outputs.TryGetValue(group.Key, out var output) || clips.Any(clip => clip.sha256 != output.sha256))
                        throw new InvalidDataException("Clip and FBX ownership differ in " + path);
                    outputId = output.id;
                    isCombined = output.kind == "combined";
                }
                if ((!isCombined && clips.Length != 1) || clips.Select(clip => clip.takeName).Distinct(StringComparer.Ordinal).Count() != clips.Length ||
                    clips.Select(clip => clip.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != clips.Length)
                    throw new InvalidDataException("Duplicate animation takes or names in " + path);
                result.Add(new DirectSource { folder = folder, metadataPath = path.Replace('\\', '/'), assetPath = folder + "/" + group.Key,
                    profile = profile, clip = first, clips = clips, outputId = outputId, combined = isCombined });
            }
            return result.ToArray();
        }

        public static IEnumerable<DirectSource> Sources(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal) && folder != "Assets") yield break;
            var directory = folder + "/AnimationExportData";
            if (!Directory.Exists(directory)) yield break;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(directory, "*.animation-export.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                DirectSource[] sources;
                try
                {
                    sources = ParseProfile(folder, path);
                    invalidProfiles.Remove(path);
                }
                catch (Exception error)
                {
                    if (!invalidProfiles.TryGetValue(path, out var previous) || previous != error.Message)
                    { Debug.LogWarning("Animation export — ignored invalid profile: " + error.Message); invalidProfiles[path] = error.Message; }
                    continue;
                }
                foreach (var source in sources)
                {
                    if (!seen.Add(source.assetPath)) throw new InvalidDataException("Multiple export profiles claim " + source.assetPath);
                    yield return source;
                }
            }
        }

        public static DirectSource Find(string assetPath, bool verifyHash = true)
        {
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) || !assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return null;
            var source = Sources(Path.GetDirectoryName(assetPath)).SingleOrDefault(row => row.assetPath.Equals(assetPath, StringComparison.OrdinalIgnoreCase));
            if (source == null || !File.Exists(assetPath)) return null;
            if (verifyHash && HashFile(assetPath) != source.clip.sha256) return null;
            return source;
        }
    }
}
