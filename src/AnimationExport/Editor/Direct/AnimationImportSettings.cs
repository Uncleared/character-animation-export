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

    [Serializable]
    public sealed class ClipImportOverride
    {
        public AnimationClip clip;
        public bool overrideRoot;
        public bool bakeRotation = true, bakeY = true, bakeXZ = true;
        public bool originalOrientation = true, originalPositionXZ = true;
        public bool overrideMask;
        public AvatarMask mask;
    }

    public sealed class AnimationImportSettings : ScriptableObject
    {
        [HideInInspector] public string profileId, characterName;
        public CharacterRig rig = CharacterRig.Unset;
        public Avatar sourceAvatar;
        public string motionNode;
        public bool bakeRotation = true, bakeY = true, bakeXZ = true, includeAllTransforms = true;
        [Tooltip("Use the authored facing instead of calculating a different body-facing reference for each clip.")]
        public bool originalOrientation = true;
        [Tooltip("Keep the authored horizontal origin so separately exported clips share the same reference.")]
        public bool originalPositionXZ = true;
        public List<ClipImportOverride> overrides = new List<ClipImportOverride>();

        void OnValidate()
        {
            var path = AssetDatabase.GetAssetPath(this);
            if (!string.IsNullOrEmpty(path)) DirectAnimationImporter.QueueFolder(Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace('\\', '/'));
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
        public string profileId, publicationId, characterName, rootBone;
        public DirectClip[] clips;
    }

    public sealed class DirectSource
    {
        public string folder, metadataPath, assetPath;
        public DirectProfile profile;
        public DirectClip clip;
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

        public static IEnumerable<DirectSource> Sources(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal) && folder != "Assets") yield break;
            var directory = folder + "/AnimationExportData";
            if (!Directory.Exists(directory)) yield break;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(directory, "*.animation-export.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                var profile = JsonUtility.FromJson<DirectProfile>(File.ReadAllText(path));
                if (profile == null || profile.schemaVersion != 2 || !Id(profile.profileId) || !Id(profile.publicationId) || profile.clips == null ||
                    Path.GetFileName(path) != profile.profileId + ".animation-export.json")
                    throw new InvalidDataException("Invalid animation export metadata: " + path);
                var ids = new HashSet<string>();
                foreach (var clip in profile.clips)
                {
                    if (clip == null || !Id(clip.id) || !ids.Add(clip.id)) throw new InvalidDataException("Invalid clip identity in " + path);
                    if (string.IsNullOrEmpty(clip.file)) { if (clip.exported) throw new InvalidDataException("Missing animation filename."); else continue; }
                    if (Path.GetFileName(clip.file) != clip.file || clip.file.Contains('\\') || clip.file.Contains('/') || clip.file.Contains(':') ||
                        !clip.file.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || !seen.Add(clip.file) ||
                        string.IsNullOrWhiteSpace(clip.name) || clip.sha256 == null || clip.sha256.Length != 64 ||
                        !clip.sha256.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f') ||
                        !Finite(clip.fps) || clip.fps <= 0 || !Finite(clip.duration) || clip.duration <= 0)
                        throw new InvalidDataException("Invalid animation ownership or timing in " + path);
                    yield return new DirectSource { folder = folder, metadataPath = path.Replace('\\', '/'), assetPath = folder + "/" + clip.file, profile = profile, clip = clip };
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
