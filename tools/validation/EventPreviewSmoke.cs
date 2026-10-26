using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CharacterAnimationEvents
{
    // Installed by the disposable-project helper only; excluded from installers.
    public static class EventPreviewSmoke
    {
        static readonly List<string> checks = new List<string>();
        static AnimationEventEditorWindow window;
        static AnimationClip clip;
        static PreviewCameraState state;
        static EventClipPreview preview;
        static IMGUIContainer image;
        static string baselineEvents, baselineSource;
        static int stage;
        static double next, deadline;
        static Rect rect;
        static Vector3 pivot;
        static Vector2 angles;
        static float distance;
        static string Project => Path.GetDirectoryName(Application.dataPath);
        [Serializable] sealed class Result { public bool success; public int passed; public string unity, error; public string[] checks; }
        static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        static object Call(string name, params object[] values) => typeof(AnimationEventEditorWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, values);
        static void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
        static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.00001f;
        static void Remember() { pivot = state.pivot; angles = state.angles; distance = state.distance; }

        public static void Run()
        {
            if (!File.Exists(Path.Combine(Project, "character-animation-export-validation.json"))) throw new InvalidOperationException("Use tools/prepare_unity_validation.py in a new disposable project.");
            try
            {
                MathChecks();
                Check(AppDomain.CurrentDomain.GetAssemblies().All(item => !item.GetName().Name.StartsWith("CharacterAnimationExport", StringComparison.Ordinal)), "preview works without exporter/importer assemblies");
                var root = new GameObject("Preview Character");
                var body = GameObject.CreatePrimitive(PrimitiveType.Cube); body.name = "Body"; body.transform.SetParent(root.transform); body.transform.localPosition = Vector3.up; body.transform.localScale = new Vector3(0.6f, 2, 0.5f);
                var prop = GameObject.CreatePrimitive(PrimitiveType.Sphere); prop.name = "Prop"; prop.transform.SetParent(root.transform); prop.transform.localPosition = new Vector3(0.65f, 1, 0); prop.transform.localScale = Vector3.one * 0.25f;
                var model = PrefabUtility.SaveAsPrefabAsset(root, "Assets/PreviewCharacter.prefab"); UnityEngine.Object.DestroyImmediate(root);
                clip = new AnimationClip { frameRate = 30 };
                clip.SetCurve("", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, 2));
                clip.SetCurve("Prop", typeof(Transform), "m_LocalPosition.y", AnimationCurve.Linear(0, 1, 1, 1.5f));
                AssetDatabase.CreateAsset(clip, "Assets/PreviewMotion.anim");
                AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 0.25f, functionName = "PreservedCue", intParameter = 12, stringParameter = "preview must not save" } });
                AssetDatabase.SaveAssetIfDirty(clip);
                baselineEvents = AnimationEventClipIO.Snapshot(clip); baselineSource = DirectHash("Assets/PreviewCharacter.prefab");
                window = ScriptableObject.CreateInstance<AnimationEventEditorWindow>();
                typeof(AnimationEventEditorWindow).GetField("clip", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, clip);
                typeof(AnimationEventEditorWindow).GetField("previewModel", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, model);
                window.position = new Rect(40, 40, 760, 850); window.Show();
                deadline = EditorApplication.timeSinceStartup + 45; next = 0;
                EditorApplication.update += Tick;
            }
            catch (Exception error) { Finish(error); }
        }

        static void MathChecks()
        {
            Check(EventClipPreview.Gesture(0, false, false, false) == PreviewGesture.Pan, "native left drag pans");
            Check(EventClipPreview.Gesture(2, false, false, false) == PreviewGesture.Pan, "middle drag pans");
            Check(EventClipPreview.Gesture(1, false, false, false) == PreviewGesture.Orbit, "native right drag orbits");
            Check(EventClipPreview.Gesture(0, true, false, false) == PreviewGesture.Orbit, "Alt-left drag orbits");
            Check(EventClipPreview.Gesture(1, true, false, false) == PreviewGesture.Zoom, "Alt-right drag zooms");
            Check(EventClipPreview.Gesture(0, false, false, true) == PreviewGesture.Zoom, "macOS control-left zooms");
            foreach (var scale in new[] { 0.01f, 1, 100 })
            {
                var camera = new GameObject("Camera").AddComponent<Camera>();
                try
                {
                    var bounds = new Bounds(new Vector3(3, 2, -1) * scale, new Vector3(1, 3, 0.75f) * scale);
                    foreach (var aspect in new[] { 0.35f, 1, 2.5f })
                    {
                        var view = new PreviewCameraState(); view.Frame(bounds, 30, aspect, true); camera.fieldOfView = 30; camera.aspect = aspect; view.Apply(camera, bounds);
                        var min = bounds.min; var max = bounds.max;
                        for (var i = 0; i < 8; i++)
                        {
                            var point = camera.WorldToViewportPoint(new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z));
                            if (point.z <= 0 || point.x < 0 || point.x > 1 || point.y < 0 || point.y > 1) throw new Exception("Frame clipped a bound corner.");
                        }
                        Check(true, "Frame fits all bound corners at scale " + scale + " aspect " + aspect);
                        view.Zoom(-10000); view.Apply(camera, bounds); Check(view.distance > 0 && camera.nearClipPlane > 0 && camera.farClipPlane > camera.nearClipPlane, "zoom-in/clipping safe at scale " + scale);
                        view.Zoom(10000); Check(!float.IsInfinity(view.distance) && !float.IsNaN(view.distance), "zoom-out remains finite at scale " + scale);
                        view.Orbit(new Vector2(100000, 100000)); Check(Mathf.Abs(view.angles.y) <= 89, "orbit pitch cannot flip at scale " + scale);
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(camera.gameObject); }
            }
        }

        static void Send(EventType type, int button = 0, Vector2? delta = null, bool alt = false, Vector2? point = null, KeyCode key = KeyCode.None)
        {
            window.SendEvent(new Event { type = type, button = button, mousePosition = point ?? rect.center, delta = delta ?? Vector2.zero, alt = alt, keyCode = key });
        }
        static void Drag(int button, Vector2 delta, bool alt = false)
        {
            Send(EventType.MouseDown, button, alt: alt); Send(EventType.MouseDrag, button, delta, alt); Send(EventType.MouseUp, button, alt: alt, point: new Vector2(rect.xMax + 10, rect.yMax + 10));
        }
        static string DirectHash(string path) { using (var sha = System.Security.Cryptography.SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path))); }

        static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new Exception("Native preview UI test timed out at stage " + stage);
                if (EditorApplication.timeSinceStartup < next || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                window.Repaint();
                if (stage == 0)
                {
                    preview = Get<EventClipPreview>(window, "preview"); state = Get<PreviewCameraState>(window, "cameraState"); image = window.rootVisualElement.Q<IMGUIContainer>();
                    if (preview == null || image == null || !state.initialized || image.worldBound.height < 100) { window.SendEvent(new Event { type = EventType.Repaint }); return; }
                    rect = image.worldBound;
                    Check(window.rootVisualElement.Q<TwoPaneSplitView>() != null && rect.height >= 150, "resizable native preview area is laid out");
                    Capture("preview-before.png"); Remember(); Drag(0, new Vector2(30, 12)); stage++;
                }
                else if (stage == 1)
                {
                    Check(!Near(pivot, state.pivot), "actual left-drag UI event pans camera"); Check(GUIUtility.hotControl == 0, "mouse release outside preview releases capture");
                    Remember(); Get<Slider>(window, "timeline").value = 0.7f; stage++;
                }
                else if (stage == 2)
                {
                    Check(Near(pivot, state.pivot) && state.angles == angles && state.distance == distance, "scrubbing moving motion does not reset pan/orbit/zoom");
                    Capture("preview-panned.png"); Remember(); Drag(1, new Vector2(55, 20)); stage++;
                }
                else if (stage == 3)
                {
                    Check(state.angles != angles, "actual right-drag UI event orbits"); Remember(); Send(EventType.ScrollWheel, delta: new Vector2(0, -3)); stage++;
                }
                else if (stage == 4)
                {
                    Check(state.distance < distance, "actual wheel UI event zooms in"); Capture("preview-orbit-zoom.png"); Remember(); Send(EventType.ScrollWheel, delta: new Vector2(0, -3), point: new Vector2(rect.center.x, rect.yMax + 50)); stage++;
                }
                else if (stage == 5)
                {
                    Check(state.distance == distance, "scroll outside preview does not move camera"); Remember(); Drag(0, new Vector2(20, -10), true); stage++;
                }
                else if (stage == 6)
                {
                    Check(state.angles != angles, "actual Alt-left UI drag orbits"); Remember(); Drag(1, new Vector2(30, -10), true); stage++;
                }
                else if (stage == 7)
                {
                    Check(state.distance < distance, "actual Alt-right UI drag zooms"); Remember(); Send(EventType.KeyDown, key: KeyCode.F); stage++;
                }
                else if (stage == 8)
                {
                    Check(!Near(state.pivot, pivot) && state.angles == angles, "F frames current animated pose without changing orbit"); Send(EventType.KeyDown, key: KeyCode.Home); stage++;
                }
                else if (stage == 9)
                {
                    Check(state.angles == new Vector2(15, 8), "Home resets orientation and framing");
                    Call("SetTime", 0f); Call("Step", 1); Check(Mathf.Abs(Get<float>(window, "time") - 1f / 30) < 0.00001f, "next-frame advances exactly one frame");
                    Get<IntegerField>(window, "frameField").value = 24; Check(Mathf.Abs(Get<float>(window, "time") - 0.8f) < 0.00001f, "frame-number input seeks exact frame");
                    typeof(AnimationEventEditorWindow).GetField("loopPlayback", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, false);
                    Call("SetTime", 0.99f); Call("SetPlaying", true);
                    typeof(AnimationEventEditorWindow).GetField("clock", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, EditorApplication.timeSinceStartup - 0.2);
                    Call("Tick"); Check(!Get<bool>(window, "playing") && Get<float>(window, "time") == clip.length, "nonlooping playback stops on last frame");
                    Check(!Get<bool>(window, "dirty") && AnimationEventClipIO.Snapshot(clip) == baselineEvents && DirectHash("Assets/PreviewCharacter.prefab") == baselineSource, "preview controls leave event draft, source clip and prefab untouched");
                    Finish(null); return;
                }
                next = EditorApplication.timeSinceStartup + 0.25;
            }
            catch (Exception error) { Finish(error); }
        }

        static void Capture(string name)
        {
            var texture = Get<Texture>(preview, "image") as RenderTexture;
            Check(texture != null && texture.width > 10 && texture.height > 10, "native preview has a rendered texture: " + name);
            var old = RenderTexture.active; var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            try { RenderTexture.active = texture; copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); copy.Apply(); File.WriteAllBytes(Path.Combine(Project, name), copy.EncodeToPNG()); }
            finally { RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(copy); }
        }

        static void Finish(Exception error)
        {
            EditorApplication.update -= Tick;
            if (window != null) window.Close();
            var result = new Result { success = error == null, passed = checks.Count, unity = Application.unityVersion, checks = checks.ToArray(), error = error?.ToString() };
            File.WriteAllText(Path.Combine(Project, "preview-result.json"), JsonUtility.ToJson(result, true));
            if (error != null) Debug.LogException(error); EditorApplication.Exit(error == null ? 0 : 1);
        }
    }
}
