using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace CharacterAnimationExport
{
    public sealed class AnimationExportWindow : EditorWindow
    {
        AnimationExportLibrary library;
        ManagedAnimationClip selected;
        List<ManagedAnimationClip> visible = new List<ManagedAnimationClip>();
        List<AnimationEvent> eventDraft;
        AnimationUsage usage;
        HashSet<string> replacePaths = new HashSet<string>();
        ObjectField libraryField;
        ListView clipList;
        VisualElement body, details, eventRows;
        HelpBox message;
        string search = "";
        bool showArchived, reviewedNames;
        bool draftDirty, changingSelection;
        AnimationClip draftClip;
        AnimationExportPreview preview;
        Slider previewSlider;
        float previewTime;
        bool previewPlaying;
        double previewClock;
        string Preference => "CharacterAnimationExport.Library." + Application.dataPath;

        [MenuItem("Tools/Legacy/Character Animation Library")]
        public static void Open()
        {
            var window = GetWindow<AnimationExportWindow>();
            window.titleContent = new GUIContent("Animation Export");
            window.minSize = new Vector2(760, 520);
            window.Show();
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            var sourcePath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            var stylesheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(Path.GetDirectoryName(sourcePath).Replace('\\', '/') + "/AnimationExportWindow.uss");
            if (stylesheet != null) rootVisualElement.styleSheets.Add(stylesheet);
            rootVisualElement.AddToClassList("animation-export");
            var title = new Label("Character Animation Export"); title.AddToClassList("title"); rootVisualElement.Add(title);
            var toolbar = Row(rootVisualElement);
            libraryField = new ObjectField("Library") { objectType = typeof(AnimationExportLibrary), allowSceneObjects = false };
            libraryField.AddToClassList("grow"); toolbar.Add(libraryField);
            toolbar.Add(new Button(NewLibrary) { text = "New Library" });
            toolbar.Add(new Button(ShowBlenderPackage) { text = "Blender Add-on" });
            message = new HelpBox("Choose a library, then import animation-export.json from the Blender export folder.", HelpBoxMessageType.Info);
            rootVisualElement.Add(message);
            body = new VisualElement(); body.AddToClassList("grow"); rootVisualElement.Add(body);
            var path = AssetDatabase.GUIDToAssetPath(EditorPrefs.GetString(Preference, ""));
            library = AssetDatabase.LoadAssetAtPath<AnimationExportLibrary>(path);
            libraryField.SetValueWithoutNotify(library);
            libraryField.RegisterValueChangedCallback(change =>
            {
                if (!TrySaveDraft()) { libraryField.SetValueWithoutNotify(library); return; }
                library = change.newValue as AnimationExportLibrary; selected = null;
                EditorPrefs.SetString(Preference, library == null ? "" : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(library)));
                BuildBody();
            });
            BuildBody();
        }

        static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement(); row.AddToClassList("row"); parent.Add(row); return row;
        }

        void Run(Action action)
        {
            if (!TrySaveDraft()) return;
            try { action(); message.messageType = HelpBoxMessageType.Info; }
            catch (Exception exception) { message.text = exception.Message; message.messageType = HelpBoxMessageType.Error; Debug.LogException(exception); }
        }

        void NewLibrary()
        {
            if (!TrySaveDraft()) return;
            var path = EditorUtility.SaveFilePanelInProject("New Character Animation Library", "CharacterAnimations", "asset", "Choose a dedicated character library location.");
            if (string.IsNullOrEmpty(path)) return;
            var created = CreateInstance<AnimationExportLibrary>();
            AssetDatabase.CreateAsset(created, path);
            libraryField.value = created;
        }

        void ShowBlenderPackage()
        {
            var sourcePath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            var directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath), "..", "Blender~", "dist"));
            if (Directory.Exists(directory)) EditorUtility.RevealInFinder(directory);
            else message.text = "The Blender ZIP is included in the tool's Blender~/dist folder.";
        }

        void BuildBody()
        {
            preview?.Dispose(); preview = null; previewPlaying = false;
            body.Clear();
            if (library == null) return;
            var setup = new Foldout { text = "Setup and automatic import defaults", value = string.IsNullOrEmpty(library.profileId) };
            body.Add(setup);
            var rig = new EnumField("Rig Type", library.rigType); setup.Add(rig);
            rig.RegisterValueChangedCallback(change => { library.rigType = (ExportRigType)change.newValue; AnimationExportImport.Save(library); });
            var avatar = new ObjectField("Source Avatar") { objectType = typeof(Avatar), allowSceneObjects = false, value = library.sourceAvatar }; setup.Add(avatar);
            avatar.RegisterValueChangedCallback(change => { library.sourceAvatar = change.newValue as Avatar; AnimationExportImport.Save(library); });
            var motion = new TextField("Generic Motion Node") { value = library.motionNode, tooltip = "Path below the imported model root. A single root is suggested automatically." }; setup.Add(motion);
            motion.RegisterValueChangedCallback(change => { library.motionNode = change.newValue; AnimationExportImport.Save(library); });
            ToggleSetting(setup, "Automatically configure imports", library.autoConfigureImport, value => library.autoConfigureImport = value);
            ToggleSetting(setup, "Bake Root Rotation into Pose", library.bakeRotation, value => library.bakeRotation = value);
            ToggleSetting(setup, "Bake Root Position Y into Pose", library.bakeY, value => library.bakeY = value);
            ToggleSetting(setup, "Bake Root Position XZ into Pose", library.bakeXZ, value => library.bakeXZ = value);
            ToggleSetting(setup, "Include all transforms / props", library.importAllTransforms, value => library.importAllTransforms = value);
            var actions = Row(body);
            actions.Add(new Button(() => Run(() =>
            {
                var path = EditorUtility.OpenFilePanel("Import Blender animation package", Path.GetDirectoryName(library.lastManifest ?? ""), "json");
                if (string.IsNullOrEmpty(path)) return;
                message.text = AnimationExportImport.Import(library, path); BuildBody();
            })) { text = "Import Animation Package…" });
            actions.Add(new Button(() => Run(() => { message.text = AnimationExportImport.Import(library, library.lastManifest); BuildBody(); })) { text = "Update from Last Package" });
            var split = Row(body); split.AddToClassList("grow");
            var left = new VisualElement(); left.AddToClassList("clip-column"); split.Add(left);
            var filter = new ToolbarSearchField { value = search }; left.Add(filter);
            filter.RegisterValueChangedCallback(change => { search = change.newValue; RefreshList(); });
            var archived = new Toggle("Show archived / deleted") { value = showArchived }; left.Add(archived);
            archived.RegisterValueChangedCallback(change => { showArchived = change.newValue; RefreshList(); });
            clipList = new ListView { fixedItemHeight = 48, selectionType = SelectionType.Single, makeItem = () => new Label() };
            clipList.AddToClassList("grow");
            clipList.bindItem = (element, index) => { var entry = visible[index]; ((Label)element).text = (entry.archived ? "[Archived] " : "") + entry.gameplayName + "\n" + entry.status; };
            clipList.selectionChanged += values =>
            {
                if (changingSelection) return;
                if (!TrySaveDraft())
                {
                    changingSelection = true;
                    if (selected != null && visible.Contains(selected)) clipList.SetSelection(visible.IndexOf(selected));
                    changingSelection = false;
                    return;
                }
                selected = values.FirstOrDefault() as ManagedAnimationClip; usage = null; replacePaths.Clear(); reviewedNames = false; BuildDetails();
            };
            left.Add(clipList);
            details = new ScrollView(); details.AddToClassList("details"); split.Add(details);
            RefreshList();
        }

        void ToggleSetting(VisualElement parent, string label, bool current, Action<bool> set)
        {
            var control = new Toggle(label) { value = current }; parent.Add(control);
            control.RegisterValueChangedCallback(change => { Undo.RecordObject(library, "Change animation import defaults"); set(change.newValue); AnimationExportImport.Save(library); });
        }

        void RefreshList()
        {
            visible = library.clips.Where(entry => (showArchived || (!entry.archived && !entry.deleted)) &&
                ((entry.gameplayName ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || (entry.status ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            clipList.itemsSource = visible; clipList.Rebuild();
            var index = selected == null ? -1 : visible.IndexOf(selected);
            if (index >= 0) clipList.SetSelection(index);
            else { selected = null; BuildDetails(); }
        }

        void BuildDetails()
        {
            preview?.Dispose(); preview = null; previewPlaying = false;
            details.Clear();
            if (selected == null) { details.Add(new Label("Select a clip to preview, edit events or manage its lifecycle.")); return; }
            var entry = selected;
            var title = new Label(entry.gameplayName); title.AddToClassList("subtitle"); details.Add(title);
            details.Add(new HelpBox(entry.status ?? "", HelpBoxMessageType.Info));
            if (entry.deleted)
            {
                details.Add(new Button(() => Run(() => { AnimationExportLifecycle.Restore(library, entry); RefreshList(); message.text = "Restored the original clip and its GUID. Import again to review newer motion."; })) { text = "Restore Deleted Clip" });
                return;
            }
            if (entry.clip == null) { details.Add(new Label("Export and import this clip to create its Unity animation.")); return; }
            var actions = Row(details);
            actions.Add(new Button(() => { Selection.activeObject = entry.clip; EditorUtility.OpenPropertyEditor(entry.clip); }) { text = "Preview / Inspect Clip" });
            actions.Add(new Button(() => Run(() => { AnimationExportLifecycle.Archive(library, entry, !entry.archived); RefreshList(); })) { text = entry.archived ? "Unarchive" : "Archive" });
            details.Add(new Label("Duration: " + entry.clip.length.ToString("0.###") + " s | Native: " + Size(AssetDatabase.GetAssetPath(entry.clip)) + " | FBX: " + Size(entry.sourceFbx)));
            BuildPreview(entry);
            if (!string.IsNullOrEmpty(entry.pendingJson))
            {
                details.Add(new HelpBox("New motion is staged. Choose an event timing policy before updating the gameplay clip.", HelpBoxMessageType.Warning));
                var timing = Row(details);
                timing.Add(new Button(() => ApplyTiming(entry, EventTimingChoice.KeepSeconds)) { text = "Keep Event Seconds" });
                timing.Add(new Button(() => ApplyTiming(entry, EventTimingChoice.ScaleProportionally)) { text = "Scale Events with Duration" });
            }
            var overrides = new Foldout { text = "Per-clip import overrides" }; details.Add(overrides);
            ToggleSetting(overrides, "Override root settings", entry.overrideRoot, value => entry.overrideRoot = value);
            ToggleSetting(overrides, "Bake Rotation", entry.bakeRotation, value => entry.bakeRotation = value);
            ToggleSetting(overrides, "Bake Y", entry.bakeY, value => entry.bakeY = value);
            ToggleSetting(overrides, "Bake XZ", entry.bakeXZ, value => entry.bakeXZ = value);
            ToggleSetting(overrides, "Use custom mask", entry.overrideMask, value => entry.overrideMask = value);
            var mask = new ObjectField("Mask") { objectType = typeof(AvatarMask), allowSceneObjects = false, value = entry.customMask }; overrides.Add(mask);
            mask.RegisterValueChangedCallback(change => { entry.customMask = change.newValue as AvatarMask; AnimationExportImport.Save(library); });
            overrides.Add(new Button(() => Run(() => { AnimationExportImport.RefreshSelected(library, entry); RefreshList(); })) { text = "Apply Import Settings to This Clip" });
            BuildEvents(entry);
            BuildLifecycle(entry);
        }

        void BuildPreview(ManagedAnimationClip entry)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(entry.sourceFbx);
            if (model == null) return;
            try { preview = new AnimationExportPreview(model, entry.clip); }
            catch (Exception exception) { details.Add(new HelpBox("Use Preview / Inspect Clip: " + exception.Message, HelpBoxMessageType.Info)); return; }
            var image = new IMGUIContainer(() =>
            {
                var rect = GUILayoutUtility.GetRect(10, 220, GUILayout.ExpandWidth(true));
                preview?.Draw(rect, previewTime);
            });
            image.AddToClassList("clip-preview"); details.Add(image);
            previewTime = Mathf.Clamp(previewTime, 0, entry.clip.length);
            previewSlider = new Slider("Preview Time", 0, entry.clip.length) { value = previewTime };
            previewSlider.RegisterValueChangedCallback(change => { previewTime = change.newValue; Repaint(); });
            details.Add(previewSlider);
            details.Add(new Button(() => { previewPlaying = !previewPlaying; previewClock = EditorApplication.timeSinceStartup; }) { text = "Play / Pause Preview" });
        }

        void Update()
        {
            if (!previewPlaying || selected?.clip == null) return;
            var now = EditorApplication.timeSinceStartup;
            previewTime = Mathf.Repeat(previewTime + (float)(now - previewClock), Mathf.Max(0.001f, selected.clip.length));
            previewClock = now;
            previewSlider?.SetValueWithoutNotify(previewTime);
            Repaint();
        }

        static string Size(string path) => !string.IsNullOrEmpty(path) && File.Exists(path) ? (new FileInfo(path).Length / 1024f).ToString("0.#") + " KB" : "—";

        void ApplyTiming(ManagedAnimationClip entry, EventTimingChoice choice)
        {
            Run(() => { AnimationExportImport.ApplyPending(library, entry, choice); RefreshList(); message.text = "Updated motion and preserved event payloads. Preview the cues before testing in game."; });
        }

        void BuildEvents(ManagedAnimationClip entry)
        {
            var events = new Foldout { text = "Unity Animation Events", value = true }; details.Add(events);
            if (!draftDirty || draftClip != entry.clip)
                eventDraft = AnimationUtility.GetAnimationEvents(entry.clip).Select(item => AnimationExportEvents.Clone(item)).ToList();
            draftClip = entry.clip;
            eventRows = new VisualElement(); events.Add(eventRows); RenderEvents();
            var row = Row(events);
            row.Add(new Button(() => { eventDraft.Add(new AnimationEvent { time = previewTime }); DraftChanged(); RenderEvents(); }) { text = "Add Event at Preview Time" });
            row.Add(new Button(() => { if (TrySaveDraft()) message.text = "Events saved. The source FBX was not reimported."; }) { text = "Save Events" });
            row.Add(new Button(() => { draftDirty = false; hasUnsavedChanges = false; eventDraft = AnimationUtility.GetAnimationEvents(entry.clip).Select(item => AnimationExportEvents.Clone(item)).ToList(); RenderEvents(); }) { text = "Discard Draft" });
            var copyFrom = new ObjectField("Copy Events From") { objectType = typeof(AnimationClip), allowSceneObjects = false }; events.Add(copyFrom);
            events.Add(new Button(() => Run(() =>
            {
                if (!(copyFrom.value is AnimationClip source)) throw new InvalidOperationException("Choose a source clip first.");
                eventDraft = AnimationUtility.GetAnimationEvents(source).Select(item => AnimationExportEvents.Clone(item)).ToList(); DraftChanged(); RenderEvents();
                message.text = "Copied into the draft. Check timing, then Save Events.";
            })) { text = "Copy into Draft" });
        }

        void RenderEvents()
        {
            eventRows.Clear();
            for (var index = 0; index < eventDraft.Count; index++)
            {
                var captured = index; var item = eventDraft[index];
                var row = new Foldout { text = (index + 1) + ". " + (item.functionName ?? "New event") + " @ " + item.time.ToString("0.###") + " s", value = true };
                eventRows.Add(row);
                var time = new FloatField("Time (seconds)") { value = item.time }; row.Add(time); time.RegisterValueChangedCallback(change => { item.time = change.newValue; DraftChanged(); });
                var function = new TextField("Function") { value = item.functionName }; row.Add(function); function.RegisterValueChangedCallback(change => { item.functionName = change.newValue; DraftChanged(); });
                var text = new TextField("String") { value = item.stringParameter }; row.Add(text); text.RegisterValueChangedCallback(change => { item.stringParameter = change.newValue; DraftChanged(); });
                var number = new FloatField("Float") { value = item.floatParameter }; row.Add(number); number.RegisterValueChangedCallback(change => { item.floatParameter = change.newValue; DraftChanged(); });
                var integer = new IntegerField("Integer") { value = item.intParameter }; row.Add(integer); integer.RegisterValueChangedCallback(change => { item.intParameter = change.newValue; DraftChanged(); });
                var obj = new ObjectField("Object") { objectType = typeof(Object), allowSceneObjects = false, value = item.objectReferenceParameter }; row.Add(obj); obj.RegisterValueChangedCallback(change => { item.objectReferenceParameter = change.newValue; DraftChanged(); });
                var options = new EnumField("Receiver", item.messageOptions); row.Add(options); options.RegisterValueChangedCallback(change => { item.messageOptions = (SendMessageOptions)change.newValue; DraftChanged(); });
                row.Add(new Button(() => { eventDraft.RemoveAt(captured); DraftChanged(); RenderEvents(); }) { text = "Remove Event" });
            }
        }

        void DraftChanged()
        {
            draftDirty = true; hasUnsavedChanges = true;
            saveChangesMessage = "Save the animation event draft before closing?";
        }

        bool TrySaveDraft()
        {
            if (!draftDirty) return true;
            try
            {
                var owner = library == null ? null : library.clips.Find(entry => entry.clip == draftClip);
                if (owner == null) throw new InvalidOperationException("The event draft's managed clip is no longer available.");
                AnimationExportImport.AssertOwned(library, owner);
                AnimationExportEvents.Save(draftClip, eventDraft.OrderBy(item => item.time).ToArray());
                draftDirty = false; hasUnsavedChanges = false;
                return true;
            }
            catch (Exception exception)
            {
                if (message != null) { message.text = exception.Message + " Complete or discard the draft before switching clips."; message.messageType = HelpBoxMessageType.Warning; }
                return false;
            }
        }

        public override void SaveChanges() { if (TrySaveDraft()) base.SaveChanges(); }
        public override void DiscardChanges() { draftDirty = false; base.DiscardChanges(); }
        void OnDisable() { TrySaveDraft(); preview?.Dispose(); preview = null; }

        void BuildLifecycle(ManagedAnimationClip entry)
        {
            var lifecycle = new Foldout { text = "Rename, replace or delete", value = false }; details.Add(lifecycle);
            lifecycle.Add(new Button(() => Run(() => { usage = AnimationExportLifecycle.Scan(library, entry); replacePaths.Clear(); BuildDetails(); })) { text = "Scan Clip Usage" });
            if (usage != null)
            {
                lifecycle.value = true;
                lifecycle.Add(new HelpBox(usage.Summary, usage.complete ? HelpBoxMessageType.Info : HelpBoxMessageType.Warning));
                foreach (var path in usage.assetReferences)
                {
                    var captured = path; var toggle = new Toggle(path) { value = replacePaths.Contains(path) }; lifecycle.Add(toggle);
                    toggle.RegisterValueChangedCallback(change => { if (change.newValue) replacePaths.Add(captured); else replacePaths.Remove(captured); });
                }
                foreach (var path in usage.nameCandidates) lifecycle.Add(new Label("Name candidate — resolve manually: " + path));
                foreach (var dependency in usage.timingDependencies) lifecycle.Add(new HelpBox(dependency, HelpBoxMessageType.Warning));
            }
            var reviewed = new Toggle("I reviewed name-based and runtime usage") { value = reviewedNames }; lifecycle.Add(reviewed);
            reviewed.RegisterValueChangedCallback(change => reviewedNames = change.newValue);
            var rename = new TextField("Gameplay Name") { value = entry.incomingName ?? entry.gameplayName }; lifecycle.Add(rename);
            if (entry.incomingName != entry.gameplayName)
                lifecycle.Add(new Button(() => Run(() => { entry.keepGameplayName = true; entry.status = "Keeping existing gameplay name"; AnimationExportImport.Save(library); RefreshList(); })) { text = "Keep Existing Gameplay Name" });
            lifecycle.Add(new Button(() => Run(() => { AnimationExportLifecycle.Rename(library, entry, rename.value, reviewedNames); RefreshList(); })) { text = "Rename while Keeping References" });
            var replacement = new ObjectField("Replace With") { objectType = typeof(AnimationClip), allowSceneObjects = false }; lifecycle.Add(replacement);
            lifecycle.Add(new Button(() => Run(() =>
            {
                if (replacePaths.Count == 0) throw new InvalidOperationException("Scan usage and check the exact assets to update.");
                if (!EditorUtility.DisplayDialog("Replace selected animation references", "Update " + replacePaths.Count + " selected assets? Recovery copies will be retained. Story/code name candidates remain manual.", "Replace", "Cancel")) return;
                var count = AnimationExportLifecycle.ReplaceReferences(library, entry, replacement.value as AnimationClip, replacePaths);
                message.text = "Replaced " + count + " serialized references. The original clip remains available."; usage = null; BuildDetails();
            })) { text = "Replace Selected References" });
            lifecycle.Add(new Button(() => Run(() =>
            {
                if (!EditorUtility.DisplayDialog("Delete unused managed clip", "A fresh usage scan must pass. The clip and source FBX will be removed with recoverable original metadata.", "Delete Unused Clip", "Cancel")) return;
                AnimationExportLifecycle.DeleteUnused(library, entry, reviewedNames); RefreshList();
            })) { text = "Delete Unused Clip…" });
            if (!string.IsNullOrEmpty(entry.recoveryRecord)) lifecycle.Add(new Button(() => EditorUtility.RevealInFinder(entry.recoveryRecord)) { text = "Show Recovery Data" });
        }
    }
}
