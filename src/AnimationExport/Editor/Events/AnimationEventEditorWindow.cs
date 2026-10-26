using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace CharacterAnimationEvents
{
    public sealed class AnimationEventEditorWindow : EditorWindow
    {
        [Serializable] sealed class Draft { public string baseline; public List<EventValue> events = new List<EventValue>(); }
        [SerializeField] AnimationClip clip;
        [SerializeField] GameObject previewModel;
        [SerializeField] GameObject framedModel;
        [SerializeField] PreviewCameraState cameraState = new PreviewCameraState();
        [SerializeField] float playbackSpeed = 1;
        [SerializeField] bool loopPlayback = true;
        Draft draft;
        bool dirty, playing;
        float time;
        double clock;
        VisualElement rows;
        ObjectField clipField;
        ObjectField modelField;
        Slider timeline;
        IntegerField frameField;
        Button playButton;
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
            window.minSize = new Vector2(480, 640);
            window.Show();
        }

        void OnEnable() { EditorApplication.update += Tick; }
        void OnDisable() { StoreDraft(); EditorApplication.update -= Tick; preview?.Dispose(); preview = null; }
        void OnLostFocus() { preview?.CancelDrag(); }

        public void CreateGUI()
        {
            minSize = new Vector2(480, 640);
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
            modelField = new ObjectField("Preview Model") { objectType = typeof(GameObject), allowSceneObjects = false, value = previewModel };
            rootVisualElement.Add(modelField);
            modelField.RegisterValueChangedCallback(change => { previewModel = change.newValue as GameObject; ResetPreview(); });
            var navigation = new VisualElement(); navigation.AddToClassList("preview-toolbar"); rootVisualElement.Add(navigation);
            var caption = new Label("Preview"); caption.AddToClassList("preview-caption"); navigation.Add(caption);
            navigation.Add(new Button(() => { preview?.Frame(false); Repaint(); }) { text = "Frame", tooltip = "Fit the current pose to the preview (F)." });
            navigation.Add(new Button(() => { preview?.Frame(true); Repaint(); }) { text = "Reset View", tooltip = "Reset orientation, pan and zoom, then fit the current pose (Home)." });
            var split = new TwoPaneSplitView(0, 280, TwoPaneSplitViewOrientation.Vertical) { viewDataKey = "animation-event-preview-split" };
            split.AddToClassList("preview-split"); rootVisualElement.Add(split);
            var image = new IMGUIContainer(() =>
            {
                var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                if (preview != null && clip != null) preview.Draw(rect, time);
                else if (Event.current.type == EventType.Repaint) GUI.Label(rect, clip == null ? "Choose an animation clip." : "Choose a Preview Model.", EditorStyles.centeredGreyMiniLabel);
            });
            image.focusable = true;
            image.tooltip = "Pan: left/middle drag. Orbit: right drag or Alt + left drag. Zoom: scroll or Alt + right drag. F: frame. Home: reset.";
            image.AddToClassList("clip-preview"); split.Add(image);
            var details = new VisualElement(); details.AddToClassList("event-details"); split.Add(details);
            timeline = new Slider("Time", 0, 1) { showInputField = true };
            timeline.RegisterValueChangedCallback(change => SetTime(change.newValue));
            details.Add(timeline);
            var playback = new VisualElement(); playback.AddToClassList("playback-row"); details.Add(playback);
            playback.Add(new Button(() => Step(-1)) { text = "<", tooltip = "Previous frame" });
            playButton = new Button(() => SetPlaying(!playing)) { text = "Play", tooltip = "Play / Pause" }; playback.Add(playButton);
            playback.Add(new Button(() => Step(1)) { text = ">", tooltip = "Next frame" });
            var speeds = new List<float> { 0.25f, 0.5f, 1, 2 };
            var speed = new PopupField<float>(speeds, speeds.IndexOf(playbackSpeed) >= 0 ? speeds.IndexOf(playbackSpeed) : 2, value => value.ToString("0.##") + "x", value => value.ToString("0.##") + "x") { tooltip = "Preview playback speed" };
            speed.RegisterValueChangedCallback(change => playbackSpeed = change.newValue); playback.Add(speed);
            var loop = new Toggle("Loop") { value = loopPlayback };
            loop.RegisterValueChangedCallback(change => loopPlayback = change.newValue); playback.Add(loop);
            frameField = new IntegerField("Frame"); frameField.AddToClassList("frame-field"); playback.Add(frameField);
            frameField.RegisterValueChangedCallback(change => { SetPlaying(false); SetTime(Mathf.Clamp(change.newValue, 0, LastFrame) / FrameRate); });
            var controls = new VisualElement(); controls.AddToClassList("button-row"); details.Add(controls);
            controls.Add(new Button(() => { if (clip == null) return; draft.events.Add(new EventValue { time = time, function = "", text = "" }); Changed(); RenderEvents(); }) { text = "Add Event" });
            controls.Add(new Button(() => SaveDraft()) { text = "Save" });
            controls.Add(new Button(DiscardChanges) { text = "Reload / Discard" });
            notice = new HelpBox("Choose an .anim or FBX animation clip.", HelpBoxMessageType.Info);
            details.Add(notice);
            var scroll = new ScrollView(); scroll.AddToClassList("event-list"); details.Add(scroll);
            rows = new VisualElement(); scroll.Add(rows);
            var copy = new ObjectField("Copy Events From") { objectType = typeof(AnimationClip), allowSceneObjects = false };
            details.Add(copy);
            details.Add(new Button(() =>
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
            SetPlaying(false); SetTime(0); dirty = false; hasUnsavedChanges = false;
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
                modelField?.SetValueWithoutNotify(previewModel);
                RenderEvents(); ResetPreview();
            }
            catch (Exception error) { notice.text = error.Message; notice.messageType = HelpBoxMessageType.Error; }
        }

        void ResetPreview()
        {
            preview?.Dispose(); preview = null;
            if (cameraState == null) cameraState = new PreviewCameraState();
            if (previewModel != framedModel) cameraState.initialized = false;
            try
            {
                if (clip != null && previewModel != null) preview = new EventClipPreview(previewModel, clip, cameraState, Repaint);
            }
            catch (Exception error)
            {
                if (notice != null) { notice.text = error.Message; notice.messageType = HelpBoxMessageType.Warning; }
            }
            framedModel = previewModel;
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

        float FrameRate => clip != null && clip.frameRate > 0 ? clip.frameRate : 30;
        int LastFrame => clip != null ? Mathf.RoundToInt(clip.length * FrameRate) : 0;

        void SetTime(float value)
        {
            time = Mathf.Clamp(value, 0, clip != null ? clip.length : 0);
            timeline?.SetValueWithoutNotify(time);
            frameField?.SetValueWithoutNotify(Mathf.RoundToInt(time * FrameRate));
            Repaint();
        }

        void SetPlaying(bool value)
        {
            playing = value && clip != null && clip.length > 0;
            if (playing && time >= clip.length) SetTime(0);
            clock = EditorApplication.timeSinceStartup;
            if (playButton != null) playButton.text = playing ? "Pause" : "Play";
        }

        void Step(int direction)
        {
            SetPlaying(false);
            SetTime(Mathf.Clamp(Mathf.RoundToInt(time * FrameRate) + direction, 0, LastFrame) / FrameRate);
        }

        void Tick()
        {
            if (!playing || clip == null || clip.length <= 0) return;
            var now = EditorApplication.timeSinceStartup;
            var next = time + (float)(now - clock) * playbackSpeed; clock = now;
            if (next >= clip.length && !loopPlayback) { SetTime(clip.length); SetPlaying(false); }
            else SetTime(next % clip.length);
        }
    }

    [Serializable]
    sealed class PreviewCameraState
    {
        public bool initialized;
        public Vector3 pivot;
        public Vector2 angles = new Vector2(15, 8);
        public float distance = 1, radius = 1;
        public Quaternion Rotation => Quaternion.Euler(angles.y, angles.x, 0);

        public void Frame(Bounds bounds, float fieldOfView, float aspect, bool reset)
        {
            if (reset) angles = new Vector2(15, 8);
            pivot = bounds.center;
            radius = Mathf.Max(0.0001f, bounds.extents.magnitude);
            var vertical = fieldOfView * Mathf.Deg2Rad * 0.5f;
            var horizontal = Mathf.Atan(Mathf.Tan(vertical) * Mathf.Max(0.01f, aspect));
            distance = radius * 1.1f / Mathf.Sin(Mathf.Min(vertical, horizontal));
            initialized = true;
        }

        public void Orbit(Vector2 delta)
        {
            angles.x = Mathf.Repeat(angles.x + delta.x * 0.5f + 180, 360) - 180;
            angles.y = Mathf.Clamp(angles.y + delta.y * 0.5f, -89, 89);
        }

        public void Pan(Vector2 delta, float height, float fieldOfView)
        {
            var unitsPerPixel = 2 * distance * Mathf.Tan(fieldOfView * Mathf.Deg2Rad * 0.5f) / Mathf.Max(1, height);
            pivot += Rotation * new Vector3(-delta.x, delta.y, 0) * unitsPerPixel;
        }

        public void Zoom(float amount)
        {
            distance = Mathf.Clamp(distance * Mathf.Exp(Mathf.Clamp(amount, -20, 20)), Mathf.Max(0.00001f, radius * 0.0025f), radius * 1000);
        }

        public void Apply(Camera camera, Bounds bounds)
        {
            var rotation = Rotation;
            camera.transform.SetPositionAndRotation(pivot - rotation * Vector3.forward * distance, rotation);
            camera.nearClipPlane = Mathf.Max(0.000001f, Mathf.Min(distance, radius) * 0.005f);
            camera.farClipPlane = Mathf.Max(camera.nearClipPlane * 100, distance + Vector3.Distance(pivot, bounds.center) + Mathf.Max(radius, bounds.extents.magnitude) * 4);
        }
    }

    enum PreviewGesture { None, Pan, Orbit, Zoom }

    sealed class EventClipPreview : IDisposable
    {
        readonly PreviewRenderUtility preview;
        readonly GameObject model;
        readonly AnimationClip clip;
        readonly Renderer[] renderers;
        readonly PreviewCameraState state;
        readonly Action repaint;
        PlayableGraph graph;
        AnimationClipPlayable motion;
        PreviewGesture drag;
        Texture image;
        int control;
        bool frame, reset, disposed;

        public EventClipPreview(GameObject source, AnimationClip animation, PreviewCameraState cameraState, Action requestRepaint)
        {
            state = cameraState; repaint = requestRepaint;
            preview = new PreviewRenderUtility(); preview.cameraFieldOfView = 30;
            preview.camera.stereoTargetEye = StereoTargetEyeMask.None;
            preview.lights[0].intensity = 1.1f; preview.lights[0].transform.rotation = Quaternion.Euler(35, 30, 0);
            preview.lights[1].intensity = 0.8f; preview.lights[1].transform.rotation = Quaternion.Euler(330, 210, 0);
            try
            {
                model = preview.InstantiatePrefabInScene(source); clip = animation;
                renderers = model.GetComponentsInChildren<Renderer>(true);
                foreach (var skin in renderers.OfType<SkinnedMeshRenderer>()) skin.updateWhenOffscreen = true;
                if (!clip.legacy)
                {
                    var animator = model.GetComponentInChildren<Animator>(true);
                    if (animator == null) animator = model.AddComponent<Animator>();
                    if (clip.isHumanMotion && (animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman))
                    {
                        var path = AssetDatabase.GetAssetPath(source);
                        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                        var avatar = importer != null ? importer.sourceAvatar : null;
                        if (avatar == null || !avatar.isValid || !avatar.isHuman)
                            avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault(a => a.isValid && a.isHuman);
                        if (avatar == null) throw new InvalidOperationException("Choose a preview model with a matching, valid Humanoid Avatar.");
                        animator.avatar = avatar;
                    }
                    animator.runtimeAnimatorController = null;
                    animator.enabled = true; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.fireEvents = false; animator.applyRootMotion = false; animator.Rebind();
                    graph = PlayableGraph.Create("Animation Events Preview"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    motion = AnimationClipPlayable.Create(graph, clip); motion.SetApplyFootIK(false); motion.SetApplyPlayableIK(false);
                    var output = AnimationPlayableOutput.Create(graph, "Preview", animator); output.SetSourcePlayable(motion); graph.Play();
                    graph.Evaluate(0); animator.Update(0);
                }
            }
            catch { if (graph.IsValid()) graph.Destroy(); preview.Cleanup(); throw; }
        }

        public void Frame(bool resetOrientation)
        {
            frame = true; reset |= resetOrientation;
            repaint?.Invoke();
        }

        Bounds CurrentBounds()
        {
            var bounds = new Bounds(model.transform.position, Vector3.one * 0.1f);
            var found = false;
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (!found) { bounds = renderer.bounds; found = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return bounds;
        }

        internal static PreviewGesture Gesture(int button, bool alt, bool actionKey, bool controlOnMac)
        {
            if (button == 2) return PreviewGesture.Pan;
            if (button == 1) return alt ? PreviewGesture.Zoom : PreviewGesture.Orbit;
            if (button != 0) return PreviewGesture.None;
            if (actionKey) return PreviewGesture.Pan;
            if (controlOnMac) return PreviewGesture.Zoom;
            return alt ? PreviewGesture.Orbit : PreviewGesture.Pan;
        }

        internal bool HandleInput(Event input, Rect rect, int id)
        {
            control = id;
            var inside = rect.Contains(input.mousePosition);
            var owned = GUIUtility.hotControl == id;
            var gesture = Gesture(input.button, input.alt, EditorGUI.actionKey, input.control && Application.platform == RuntimePlatform.OSXEditor);
            var cursor = drag != PreviewGesture.None ? drag : gesture;
            EditorGUIUtility.AddCursorRect(rect, cursor == PreviewGesture.Orbit ? MouseCursor.Orbit : cursor == PreviewGesture.Zoom ? MouseCursor.Zoom : MouseCursor.Pan, id);
            if (owned && (input.rawType == EventType.MouseUp || input.type == EventType.KeyDown && input.keyCode == KeyCode.Escape))
            {
                CancelDrag(); input.Use(); return true;
            }
            if (input.type == EventType.MouseDown && inside && GUIUtility.hotControl == 0 && gesture != PreviewGesture.None)
            {
                drag = gesture; GUIUtility.hotControl = id; GUIUtility.keyboardControl = id;
                EditorGUIUtility.SetWantsMouseJumping(1);
                input.Use(); return true;
            }
            if (input.type == EventType.MouseDrag && owned)
            {
                if (drag == PreviewGesture.Pan) state.Pan(input.delta, rect.height, preview.cameraFieldOfView);
                else if (drag == PreviewGesture.Orbit) state.Orbit(input.delta);
                else if (drag == PreviewGesture.Zoom) state.Zoom((input.delta.y - input.delta.x) * 0.01f * (input.shift ? 2 : 1));
                input.Use(); repaint?.Invoke(); return true;
            }
            if (input.type == EventType.ScrollWheel && inside && (GUIUtility.hotControl == 0 || owned))
            {
                state.Zoom(input.delta.y * 0.1f * (input.shift ? 2 : 1));
                input.Use(); repaint?.Invoke(); return true;
            }
            if (input.type == EventType.KeyDown && (inside || GUIUtility.keyboardControl == id) && !EditorGUIUtility.editingTextField && GUIUtility.hotControl == 0 && (input.keyCode == KeyCode.F || input.keyCode == KeyCode.Home))
            {
                Frame(input.keyCode == KeyCode.Home); input.Use(); return true;
            }
            if (input.type == EventType.ContextClick && inside) { input.Use(); return true; }
            return false;
        }

        public void CancelDrag()
        {
            if (control != 0 && GUIUtility.hotControl == control)
            {
                GUIUtility.hotControl = 0;
                EditorGUIUtility.SetWantsMouseJumping(0);
            }
            drag = PreviewGesture.None;
        }

        public void Draw(Rect rect, float time)
        {
            if (disposed || model == null || clip == null || rect.width <= 1 || rect.height <= 1) return;
            var id = GUIUtility.GetControlID("CharacterAnimationEvents.Preview".GetHashCode(), FocusType.Keyboard, rect);
            HandleInput(Event.current, rect, id);
            if (Event.current.type != EventType.Repaint) return;
            var sampleTime = Mathf.Clamp(time, 0, clip.length);
            if (graph.IsValid()) { motion.SetTime(sampleTime); graph.Evaluate(0); }
            else clip.SampleAnimation(model, sampleTime);
            var bounds = CurrentBounds();
            if (!state.initialized || frame) state.Frame(bounds, preview.cameraFieldOfView, rect.width / rect.height, reset);
            frame = reset = false;
            state.Apply(preview.camera, bounds);
            preview.BeginPreview(rect, GUIStyle.none);
            try { preview.Render(true); }
            finally { image = preview.EndPreview(); }
            GUI.DrawTexture(rect, image, ScaleMode.ScaleToFit, false);
        }

        public void Dispose()
        {
            if (disposed) return;
            CancelDrag(); if (graph.IsValid()) graph.Destroy(); preview.Cleanup(); image = null; disposed = true;
        }
    }
}
