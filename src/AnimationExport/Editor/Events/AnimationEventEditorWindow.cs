using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace CharacterAnimationEvents
{
    public sealed class AnimationEventEditorWindow : EditorWindow
    {
        [Serializable] sealed class Draft { public string baseline; public List<EventValue> events = new List<EventValue>(); }
        [SerializeField] AnimationClip clip;
        [SerializeField] GameObject previewModel;
        Draft draft;
        bool dirty, playing;
        float time;
        double clock;
        VisualElement rows;
        ObjectField clipField;
        Slider timeline;
        HelpBox notice;
        EventClipPreview preview;
        string SessionKey
        {
            get
            {
                if (clip == null) return "";
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long id);
                return "CharacterAnimationEvents.Draft." + guid + "." + id;
            }
        }

        [MenuItem("Tools/Animation Event Editor")]
        public static void Open()
        {
            var window = GetWindow<AnimationEventEditorWindow>();
            window.titleContent = new GUIContent("Animation Events");
            window.minSize = new Vector2(440, 520);
            window.Show();
        }

        void OnEnable() { EditorApplication.update += Tick; }
        void OnDisable() { StoreDraft(); EditorApplication.update -= Tick; preview?.Dispose(); preview = null; }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            var path = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            var styles = AssetDatabase.LoadAssetAtPath<StyleSheet>(Path.GetDirectoryName(path).Replace('\\', '/') + "/AnimationEventEditor.uss");
            if (styles != null) rootVisualElement.styleSheets.Add(styles);
            rootVisualElement.AddToClassList("event-editor");
            clipField = new ObjectField("Animation Clip") { objectType = typeof(AnimationClip), allowSceneObjects = false };
            rootVisualElement.Add(clipField);
            clipField.RegisterValueChangedCallback(change =>
            {
                if (!LeaveDraft()) { clipField.SetValueWithoutNotify(clip); return; }
                clip = change.newValue as AnimationClip; LoadClip();
            });
            var modelField = new ObjectField("Preview Model") { objectType = typeof(GameObject), allowSceneObjects = false, value = previewModel };
            rootVisualElement.Add(modelField);
            modelField.RegisterValueChangedCallback(change => { previewModel = change.newValue as GameObject; ResetPreview(); });
            var image = new IMGUIContainer(() =>
            {
                var rect = GUILayoutUtility.GetRect(1, 175, GUILayout.ExpandWidth(true));
                if (preview != null && clip != null) preview.Draw(rect, time);
            });
            image.AddToClassList("clip-preview"); rootVisualElement.Add(image);
            timeline = new Slider("Time", 0, 1) { showInputField = true };
            timeline.RegisterValueChangedCallback(change => { time = change.newValue; Repaint(); });
            rootVisualElement.Add(timeline);
            var controls = new VisualElement(); controls.AddToClassList("button-row"); rootVisualElement.Add(controls);
            controls.Add(new Button(() => { playing = !playing; clock = EditorApplication.timeSinceStartup; }) { text = "Play / Pause" });
            controls.Add(new Button(() => { if (clip == null) return; draft.events.Add(new EventValue { time = time, function = "", text = "" }); Changed(); RenderEvents(); }) { text = "Add Event" });
            controls.Add(new Button(() => SaveDraft()) { text = "Save" });
            controls.Add(new Button(DiscardChanges) { text = "Reload / Discard" });
            notice = new HelpBox("Choose an .anim or FBX animation clip.", HelpBoxMessageType.Info);
            rootVisualElement.Add(notice);
            var scroll = new ScrollView(); scroll.AddToClassList("event-list"); rootVisualElement.Add(scroll);
            rows = new VisualElement(); scroll.Add(rows);
            var copy = new ObjectField("Copy Events From") { objectType = typeof(AnimationClip), allowSceneObjects = false };
            rootVisualElement.Add(copy);
            rootVisualElement.Add(new Button(() =>
            {
                if (clip == null || !(copy.value is AnimationClip source)) return;
                draft.events = AnimationUtility.GetAnimationEvents(source).Select(EventValue.From).ToList();
                Changed(); RenderEvents();
            }) { text = "Copy into Draft" });
            if (clip == null && Selection.activeObject is AnimationClip selected) clip = selected;
            clipField.SetValueWithoutNotify(clip);
            LoadClip();
        }

        void LoadClip()
        {
            playing = false; time = 0; dirty = false; hasUnsavedChanges = false;
            if (clip == null) { draft = new Draft(); RenderEvents(); ResetPreview(); return; }
            try
            {
                var stored = SessionState.GetString(SessionKey, "");
                draft = string.IsNullOrEmpty(stored) ? new Draft { baseline = AnimationEventClipIO.Snapshot(clip), events = AnimationUtility.GetAnimationEvents(clip).Select(EventValue.From).ToList() } : JsonUtility.FromJson<Draft>(stored);
                if (!string.IsNullOrEmpty(stored)) { dirty = true; hasUnsavedChanges = true; }
                timeline.highValue = Mathf.Max(0.001f, clip.length);
                timeline.SetValueWithoutNotify(0);
                notice.text = dirty ? "Restored an unsaved draft. Save or reload it." : "Events use seconds from the start of this clip.";
                notice.messageType = HelpBoxMessageType.Info;
                if (AssetDatabase.GetAssetPath(clip).EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    previewModel = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(clip));
                RenderEvents(); ResetPreview();
            }
            catch (Exception error) { notice.text = error.Message; notice.messageType = HelpBoxMessageType.Error; }
        }

        void ResetPreview()
        {
            preview?.Dispose(); preview = null;
            if (clip != null && previewModel != null) preview = new EventClipPreview(previewModel, clip);
            Repaint();
        }

        void RenderEvents()
        {
            rows?.Clear();
            if (rows == null || draft == null) return;
            foreach (var entry in draft.events.ToArray())
            {
                var item = entry;
                var row = new Foldout { text = (string.IsNullOrEmpty(item.function) ? "New event" : item.function) + " @ " + item.time.ToString("0.###") + " s", value = true };
                rows.Add(row);
                var position = new FloatField("Time (seconds)") { value = item.time }; row.Add(position);
                position.RegisterValueChangedCallback(change => { item.time = change.newValue; Changed(); });
                var function = new TextField("Function") { value = item.function }; row.Add(function);
                function.RegisterValueChangedCallback(change => { item.function = change.newValue; Changed(); });
                var parameters = new Foldout { text = "Parameters", value = false }; row.Add(parameters);
                var text = new TextField("String") { value = item.text }; parameters.Add(text);
                text.RegisterValueChangedCallback(change => { item.text = change.newValue; Changed(); });
                var number = new FloatField("Float") { value = item.number }; parameters.Add(number);
                number.RegisterValueChangedCallback(change => { item.number = change.newValue; Changed(); });
                var integer = new IntegerField("Integer") { value = item.integer }; parameters.Add(integer);
                integer.RegisterValueChangedCallback(change => { item.integer = change.newValue; Changed(); });
                Object current = null; try { current = item.ToEvent().objectReferenceParameter; } catch (InvalidOperationException) { }
                var reference = new ObjectField("Object") { objectType = typeof(Object), allowSceneObjects = false, value = current }; parameters.Add(reference);
                reference.RegisterValueChangedCallback(change =>
                {
                    item.objectGuid = null; item.objectLocalId = 0;
                    if (change.newValue != null) AssetDatabase.TryGetGUIDAndLocalFileIdentifier(change.newValue, out item.objectGuid, out item.objectLocalId);
                    Changed();
                });
                var receiver = new EnumField("Receiver", item.receiver); parameters.Add(receiver);
                receiver.RegisterValueChangedCallback(change => { item.receiver = (SendMessageOptions)change.newValue; Changed(); });
                row.Add(new Button(() => { draft.events.Remove(item); Changed(); RenderEvents(); }) { text = "Remove" });
            }
        }

        void Changed()
        {
            dirty = true; hasUnsavedChanges = true;
            saveChangesMessage = "Save the animation event draft?";
            StoreDraft();
        }

        void StoreDraft() { if (dirty && clip != null && draft != null) SessionState.SetString(SessionKey, JsonUtility.ToJson(draft)); }

        bool SaveDraft()
        {
            if (!dirty) return true;
            try
            {
                AnimationEventClipIO.Save(clip, draft.events.OrderBy(item => item.time).Select(item => item.ToEvent()).ToArray(), draft.baseline);
                SessionState.EraseString(SessionKey);
                dirty = false; hasUnsavedChanges = false;
                draft.baseline = AnimationEventClipIO.Snapshot(clip);
                notice.text = "Events saved."; notice.messageType = HelpBoxMessageType.Info;
                return true;
            }
            catch (Exception error) { notice.text = error.Message; notice.messageType = HelpBoxMessageType.Warning; return false; }
        }

        bool LeaveDraft()
        {
            if (!dirty) return true;
            var result = EditorUtility.DisplayDialogComplex("Animation Events", "Save this clip's event draft?", "Save", "Cancel", "Discard");
            if (result == 1) return false;
            if (result == 0) return SaveDraft();
            SessionState.EraseString(SessionKey); dirty = false; hasUnsavedChanges = false; return true;
        }

        public override void SaveChanges() { if (SaveDraft()) base.SaveChanges(); }
        public override void DiscardChanges()
        {
            if (!string.IsNullOrEmpty(SessionKey)) SessionState.EraseString(SessionKey);
            dirty = false; hasUnsavedChanges = false; base.DiscardChanges();
            if (rows != null) LoadClip();
        }

        void Tick()
        {
            if (!playing || clip == null || clip.length <= 0) return;
            var now = EditorApplication.timeSinceStartup;
            time = (time + (float)(now - clock)) % clip.length; clock = now;
            timeline?.SetValueWithoutNotify(time); Repaint();
        }
    }

    sealed class EventClipPreview : IDisposable
    {
        readonly PreviewRenderUtility preview;
        readonly GameObject model;
        readonly AnimationClip clip;
        public EventClipPreview(GameObject source, AnimationClip animation)
        {
            preview = new PreviewRenderUtility(); preview.cameraFieldOfView = 30;
            preview.camera.nearClipPlane = 0.001f; preview.camera.farClipPlane = 10000;
            preview.camera.stereoTargetEye = StereoTargetEyeMask.None;
            preview.lights[0].intensity = 1.1f; preview.lights[0].transform.rotation = Quaternion.Euler(35, 30, 0);
            preview.lights[1].intensity = 0.8f; preview.lights[1].transform.rotation = Quaternion.Euler(330, 210, 0);
            model = preview.InstantiatePrefabInScene(source); clip = animation;
        }
        public void Draw(Rect rect, float time)
        {
            if (model == null || clip == null || rect.width <= 0 || rect.height <= 0) return;
            clip.SampleAnimation(model, Mathf.Clamp(time, 0, clip.length));
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            var bounds = renderers.Length > 0 ? renderers[0].bounds : new Bounds(Vector3.zero, Vector3.one);
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            var radius = Mathf.Max(0.05f, bounds.extents.magnitude);
            var distance = radius / Mathf.Sin(preview.cameraFieldOfView * Mathf.Deg2Rad * 0.5f) * 1.2f;
            preview.camera.transform.position = bounds.center + new Vector3(0, radius * 0.1f, -distance);
            preview.camera.transform.LookAt(bounds.center);
            preview.BeginPreview(rect, GUIStyle.none); preview.Render(true);
            GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.ScaleToFit, false);
        }
        public void Dispose() => preview.Cleanup();
    }
}
