using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace CharacterAnimationExport.Direct
{
    [CustomEditor(typeof(AnimationImportSettings))]
    public sealed class AnimationImportSettingsEditor : Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var settings = (AnimationImportSettings)target;
            var root = new VisualElement();
            root.Add(new Label(settings.characterName + " — Animation Import"));
            root.Add(new PropertyField(serializedObject.FindProperty("rig"), "Rig"));
            root.Add(new PropertyField(serializedObject.FindProperty("sourceAvatar"), "Source Avatar"));
            root.Add(new PropertyField(serializedObject.FindProperty("loopIdleClips"), "Loop Idle Clips"));
            root.Add(new PropertyField(serializedObject.FindProperty("setCompression"), "Set Animation Compression"));
            var compression = new VisualElement { name = "compressionControls" };
            root.Add(compression);
            compression.Add(new PropertyField(serializedObject.FindProperty("compression"), "Anim. Compression"));
            var errors = new VisualElement { name = "compressionErrors" };
            compression.Add(errors);
            errors.Add(new FloatField("Rotation Error") { name = "rotationError", bindingPath = "rotationError", isDelayed = true });
            errors.Add(new FloatField("Position Error") { name = "positionError", bindingPath = "positionError", isDelayed = true });
            errors.Add(new FloatField("Scale Error") { name = "scaleError", bindingPath = "scaleError", isDelayed = true });
            void UpdateCompressionControls()
            {
                compression.SetEnabled(serializedObject.FindProperty("setCompression").boolValue);
                errors.SetEnabled(serializedObject.FindProperty("compression").intValue != (int)AnimationCompression.Off);
            }
            root.TrackPropertyValue(serializedObject.FindProperty("setCompression"), _ => UpdateCompressionControls());
            root.TrackPropertyValue(serializedObject.FindProperty("compression"), _ => UpdateCompressionControls());
            UpdateCompressionControls();
            var advanced = new Foldout { text = "Defaults", value = false };
            root.Add(advanced);
            advanced.Add(new PropertyField(serializedObject.FindProperty("motionNode"), "Generic Motion Root"));
            advanced.Add(new PropertyField(serializedObject.FindProperty("bakeRotation"), "Bake Rotation into Pose"));
            advanced.Add(new PropertyField(serializedObject.FindProperty("bakeY"), "Bake Y into Pose"));
            advanced.Add(new PropertyField(serializedObject.FindProperty("bakeXZ"), "Bake XZ into Pose"));
            advanced.Add(new PropertyField(serializedObject.FindProperty("originalOrientation"), "Keep Original Facing"));
            advanced.Add(new PropertyField(serializedObject.FindProperty("originalPositionXZ"), "Keep Original XZ Origin"));
            advanced.Add(new PropertyField(serializedObject.FindProperty("includeAllTransforms"), "Include All Transforms and Props"));
            var overrides = new Foldout { text = "Clip Overrides", value = false };
            root.Add(overrides);
            overrides.Add(new PropertyField(serializedObject.FindProperty("overrides"), "Overrides"));
            var notice = new HelpBox("Changes apply automatically to this character's exported FBXs.", HelpBoxMessageType.Info);
            root.Add(notice);
            return root;
        }

        [MenuItem("Tools/Character Animation Export")]
        [MenuItem("Assets/Animation Import Settings", false, 2000)]
        public static void OpenSettings()
        {
            var selected = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!string.IsNullOrEmpty(selected) && !AssetDatabase.IsValidFolder(selected)) selected = Path.GetDirectoryName(selected).Replace('\\', '/');
            if (string.IsNullOrEmpty(selected) || !Directory.Exists(selected + "/AnimationExportData"))
            {
                var chosen = EditorUtility.OpenFolderPanel("Choose the Blender export folder", Application.dataPath, "");
                if (string.IsNullOrEmpty(chosen)) return;
                var assets = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(chosen).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
                { EditorUtility.DisplayDialog("Animation Import Settings", "Choose an export folder inside this project's Assets.", "OK"); return; }
                selected = "Assets/" + Path.GetRelativePath(Application.dataPath, chosen).Replace('\\', '/');
                selected = selected.TrimEnd('/');
            }
            try
            {
                var profiles = DirectAnimationMetadata.Sources(selected).GroupBy(item => item.profile.profileId).Select(group => group.First()).ToArray();
                if (profiles.Length == 0)
                { EditorUtility.DisplayDialog("Animation Import Settings", "Export from Blender into this folder first.", "OK"); return; }
                if (profiles.Length == 1) SelectSettings(profiles[0]);
                else
                {
                    var menu = new GenericMenu();
                    foreach (var source in profiles) { var row = source; menu.AddItem(new GUIContent(row.profile.characterName), false, () => SelectSettings(row)); }
                    menu.ShowAsContext();
                }
            }
            catch (Exception error) { EditorUtility.DisplayDialog("Animation Import Settings", error.Message, "OK"); }
        }

        static void SelectSettings(DirectSource source)
        {
            var settings = source.Settings;
            if (settings == null)
            {
                settings = CreateInstance<AnimationImportSettings>();
                settings.profileId = source.profile.profileId;
                settings.characterName = source.profile.characterName;
                AssetDatabase.CreateAsset(settings, source.SettingsPath);
                AssetDatabase.SaveAssetIfDirty(settings);
            }
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }
    }
}
