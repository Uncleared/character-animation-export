using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CharacterAnimationExport;
using CharacterAnimationExport.Direct;
using CharacterAnimationEvents;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

public static class LightweightSmoke
{
    [Serializable] public sealed class SavedClip { public string path, identity, flags, events, motionRoot; }
    [Serializable] public sealed class SavedReferences { public string path; public string[] identities; }
    [Serializable] public sealed class RestartState { public SavedClip[] clips; public SavedReferences[] references; }
    static readonly List<SavedClip> savedClips = new List<SavedClip>();
    static readonly List<SavedReferences> savedReferences = new List<SavedReferences>();
    [Serializable] public sealed class Measurement
    {
        public string label;
        public int samples, facialBindings;
        public float quaternionError, positionError, scaleError, facialError, bodyMotion, facialMotion, capMotion, leverMotion;
        public float rootPositionError, rootQuaternionError, rootTravel;
    }
    [Serializable] public sealed class Result
    {
        public bool success;
        public string unity, error;
        public string[] checks;
        public Measurement[] measurements;
    }
    static readonly List<string> checks = new List<string>();
    static readonly List<Measurement> measurements = new List<Measurement>();
    static string Project => Path.GetDirectoryName(Application.dataPath);
    static string Fixtures => Path.Combine(Project, "LightweightFixtures");
    const string Root = "Assets/LightweightTests";
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
    static void Folder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = Path.GetDirectoryName(path).Replace('\\', '/'); Folder(parent); AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
    static string Identity(Object item)
    {
        if (item == null) return "";
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(item, out string guid, out long id); return guid + ":" + id;
    }
    static AnimationClip Clip(string path) => AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Single(item => !item.name.StartsWith("__preview__"));
    static void Copy(string source, string target)
    {
        Folder(target);
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var file in files)
        {
            var relative = file.Substring(source.Length + 1).Replace('\\', '/');
            var destination = target + "/" + relative; Folder(Path.GetDirectoryName(destination).Replace('\\', '/'));
            File.Copy(file, destination, true);
        }
        foreach (var file in files.OrderBy(file => file.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            AssetDatabase.ImportAsset(target + "/" + file.Substring(source.Length + 1).Replace('\\', '/'), ImportAssetOptions.ForceSynchronousImport);
        DirectAnimationImporter.QueueFolder(target); DirectAnimationImporter.ProcessPending();
    }
    static AnimationImportSettings Configure(string target, bool humanoid)
    {
        var source = DirectAnimationMetadata.Sources(target).First();
        var settings = source.Settings;
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<AnimationImportSettings>(); settings.profileId = source.profile.profileId; settings.characterName = source.profile.characterName;
            AssetDatabase.CreateAsset(settings, source.SettingsPath);
        }
        settings.rig = humanoid ? CharacterRig.Humanoid : CharacterRig.Generic;
        settings.sourceAvatar = humanoid ? AssetDatabase.LoadAssetAtPath<Avatar>("Assets/MatchingAvatar.asset") : null;
        settings.compression = AnimationCompression.Off; settings.loopIdleClips = false;
        EditorUtility.SetDirty(settings); AssetDatabase.SaveAssetIfDirty(settings);
        Reconcile(target); return settings;
    }
    static void Reconcile(string folder)
    {
        DirectAnimationImporter.QueueFolder(folder); DirectAnimationImporter.ProcessPending();
        foreach (var source in DirectAnimationMetadata.Sources(folder)) DirectAnimationImporter.Reconcile(source.assetPath);
    }
    static ModelImporter Importer(string path) => (ModelImporter)AssetImporter.GetAtPath(path);
    static string TransformSignature(GameObject model)
    {
        return string.Join("|", model.GetComponentsInChildren<Transform>(true).Select(t => AnimationUtility.CalculateTransformPath(t, model.transform) + ":" +
            t.localPosition.ToString("R") + ":" + t.localRotation.ToString("R") + ":" + t.localScale.ToString("R")));
    }
    static string SkinSignature(GameObject model)
    {
        return string.Join("|", model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r =>
            AnimationUtility.CalculateTransformPath(r.transform, model.transform) + ":" + (r.rootBone == null ? "null" : AnimationUtility.CalculateTransformPath(r.rootBone, model.transform)) + ":" +
            string.Join(",", r.bones.Select(b => b == null ? "null" : AnimationUtility.CalculateTransformPath(b, model.transform)))));
    }
    static string AvatarSignature(string path)
    {
        var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
        return avatar == null ? "no avatar" : JsonUtility.ToJson(avatar.humanDescription);
    }
    static string Flags(ModelImporterClipAnimation clip)
    {
        var mask = new AvatarMask();
        try
        {
            clip.ConfigureMaskFromClip(ref mask);
            return string.Join("|", new[] {clip.name, clip.takeName, clip.firstFrame.ToString("R"), clip.lastFrame.ToString("R"), clip.loopTime.ToString(), clip.loopPose.ToString(), clip.mirror.ToString(), clip.cycleOffset.ToString("R"),
                clip.lockRootRotation.ToString(), clip.lockRootHeightY.ToString(), clip.lockRootPositionXZ.ToString(), clip.keepOriginalOrientation.ToString(), clip.keepOriginalPositionXZ.ToString(),
                clip.maskType.ToString(), Identity(clip.maskSource), string.Join(",", Enumerable.Range(0, mask.transformCount).Select(i => mask.GetTransformPath(i) + ":" + mask.GetTransformActive(i))),
                string.Join(",", (clip.curves ?? Array.Empty<ClipAnimationInfoCurve>()).Select(c => c.name + ":" + string.Join(";", c.curve.keys.Select(k => k.time.ToString("R") + ":" + k.value.ToString("R")))))});
        }
        finally { Object.DestroyImmediate(mask); }
    }
    static string Events(AnimationClip clip) => string.Join("|", AnimationUtility.GetAnimationEvents(clip).Select(e => e.time.ToString("R") + ":" + e.functionName + ":" +
        e.stringParameter + ":" + e.floatParameter.ToString("R") + ":" + e.intParameter + ":" + e.messageOptions + ":" + Identity(e.objectReferenceParameter)));
    static string Binding(EditorCurveBinding b) => b.path + ":" + b.type.FullName + ":" + b.propertyName;
    static float QuaternionError(Quaternion a, Quaternion b)
    {
        if (Quaternion.Dot(a, b) < 0) b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
        return Mathf.Max(Mathf.Abs(a.x-b.x), Mathf.Abs(a.y-b.y), Mathf.Abs(a.z-b.z), Mathf.Abs(a.w-b.w));
    }
    static AnimationClipPlayable Play(PlayableGraph graph, GameObject model, AnimationClip clip)
    {
        var animator = model.GetComponent<Animator>();
        if (animator == null) animator = model.AddComponent<Animator>();
        if (clip.isHumanMotion && (animator.avatar == null || !animator.avatar.isHuman))
            animator.avatar = Importer(AssetDatabase.GetAssetPath(clip)).sourceAvatar;
        animator.runtimeAnimatorController = null; animator.fireEvents = false; animator.applyRootMotion = true; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.Rebind();
        var playable = AnimationClipPlayable.Create(graph, clip); playable.SetApplyFootIK(false); playable.SetApplyPlayableIK(false);
        AnimationPlayableOutput.Create(graph, "Comparison", animator).SetSourcePlayable(playable); return playable;
    }
    static void Compare(string label, GameObject target, AnimationClip full, AnimationClip lightweight, bool requireMotion)
    {
        var aBindings = AnimationUtility.GetCurveBindings(full); var bBindings = AnimationUtility.GetCurveBindings(lightweight);
        Check(aBindings.Select(Binding).OrderBy(x => x).SequenceEqual(bBindings.Select(Binding).OrderBy(x => x)), label + ": identical native binding inventory");
        Check(full.length == lightweight.length && full.frameRate == lightweight.frameRate && full.isHumanMotion == lightweight.isHumanMotion, label + ": identical duration, FPS and rig type");
        var report = new Measurement { label = label, facialBindings = aBindings.Count(b => b.propertyName.StartsWith("blendShape.")) }; measurements.Add(report);
        var scene = EditorSceneManager.NewPreviewScene();
        var graph = PlayableGraph.Create("Full versus Lightweight"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        try
        {
            var a = Object.Instantiate(target); var b = Object.Instantiate(target);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(a, scene); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(b, scene);
            var aTransforms = a.GetComponentsInChildren<Transform>(true); var bTransforms = b.GetComponentsInChildren<Transform>(true);
            var aSkins = a.GetComponentsInChildren<SkinnedMeshRenderer>(true); var bSkins = b.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var ap = Play(graph, a, full); var bp = Play(graph, b, lightweight); graph.Play();
            ap.SetTime(0); bp.SetTime(0); graph.Evaluate(0);
            var initialRotations = aTransforms.Select(t => t.localRotation).ToArray();
            var initialPositions = aTransforms.Select(t => t.localPosition).ToArray();
            var initialShapes = aSkins.Select(r => Enumerable.Range(0, r.sharedMesh.blendShapeCount).Select(r.GetBlendShapeWeight).ToArray()).ToArray();
            var samples = Math.Max(120, Mathf.CeilToInt(full.length * full.frameRate * 2));
            for (var sample = 0; sample <= samples; sample++)
            {
                var time = full.length * sample / samples; ap.SetTime(time); bp.SetTime(time); graph.Evaluate(0); report.samples++;
                for (var index = 0; index < aTransforms.Length; index++)
                {
                    var at = aTransforms[index]; var bt = bTransforms[index];
                    report.quaternionError = Mathf.Max(report.quaternionError, QuaternionError(at.localRotation, bt.localRotation));
                    report.positionError = Mathf.Max(report.positionError, Vector3.Distance(at.localPosition, bt.localPosition));
                    report.scaleError = Mathf.Max(report.scaleError, Vector3.Distance(at.localScale, bt.localScale));
                    var angle = Quaternion.Angle(initialRotations[index], at.localRotation);
                    report.bodyMotion = Mathf.Max(report.bodyMotion, angle, Vector3.Distance(initialPositions[index], at.localPosition));
                    if (at.name == "Cap") report.capMotion = Mathf.Max(report.capMotion, angle);
                    if (at.name == "SmallLever") report.leverMotion = Mathf.Max(report.leverMotion, angle);
                }
                for (var renderer = 0; renderer < aSkins.Length; renderer++)
                    for (var shape = 0; shape < aSkins[renderer].sharedMesh.blendShapeCount; shape++)
                    {
                        var value = aSkins[renderer].GetBlendShapeWeight(shape);
                        report.facialError = Mathf.Max(report.facialError, Mathf.Abs(value - bSkins[renderer].GetBlendShapeWeight(shape)));
                        report.facialMotion = Mathf.Max(report.facialMotion, Mathf.Abs(value-initialShapes[renderer][shape]));
                    }
            }
            Check(report.quaternionError <= 0.00001f && report.positionError <= 0.0001f && report.scaleError <= 0.00001f && report.facialError <= 0.001f, label + ": dense applied body/face/prop parity");
            ap.SetTime(0); bp.SetTime(0); a.GetComponent<Animator>().Rebind(); b.GetComponent<Animator>().Rebind(); graph.Evaluate(0);
            a.transform.position = Vector3.zero; b.transform.position = Vector3.zero;
            for (var step = 0; step < 120; step++)
            {
                graph.Evaluate(full.length / 120);
                report.rootPositionError = Mathf.Max(report.rootPositionError, Vector3.Distance(a.transform.position, b.transform.position));
                report.rootQuaternionError = Mathf.Max(report.rootQuaternionError, QuaternionError(a.transform.rotation, b.transform.rotation));
                report.rootTravel = Mathf.Max(report.rootTravel, a.transform.position.magnitude);
            }
            Check(report.rootPositionError <= 0.0001f && report.rootQuaternionError <= 0.00001f, label + ": advancing root-motion trajectory parity");
            if (label.StartsWith("NoFace/")) Check(report.rootTravel > 0.0001f, label + ": unbaked root motion is nonzero");
            if (requireMotion) Check(report.bodyMotion > 0.01f, label + ": nonzero applied body motion");
            if (label.Contains("Smoking")) Check(report.capMotion > 100 && report.leverMotion > 50 && report.facialMotion > 0.1f, label + ": nonzero smoking lid, lever and face motion");
        }
        finally { graph.Destroy(); EditorSceneManager.ClosePreviewScene(scene); }
    }
    static string Reference(string sourcePath, string folder, string name)
    {
        Folder(folder); var path = folder + "/" + name + ".fbx"; File.Copy(sourcePath, path); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var importer = Importer(path); var original = Importer(sourcePath);
        importer.animationType = original.animationType; importer.avatarSetup = original.avatarSetup; importer.sourceAvatar = original.sourceAvatar;
        importer.preserveHierarchy = original.preserveHierarchy; importer.optimizeGameObjects = original.optimizeGameObjects;
        importer.importAnimation = original.importAnimation; importer.motionNodeName = original.motionNodeName;
        importer.animationCompression = original.animationCompression; importer.animationRotationError = original.animationRotationError;
        importer.animationPositionError = original.animationPositionError; importer.animationScaleError = original.animationScaleError;
        importer.clipAnimations = original.clipAnimations; importer.SaveAndReimport(); return path;
    }
    static void PreviewChoice(GameObject model, AnimationClip a, AnimationClip b)
    {
        var type = typeof(AnimationEventEditorWindow); var window = ScriptableObject.CreateInstance<AnimationEventEditorWindow>();
        Action<string, object> set = (name, value) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, value);
        Func<string, object> get = name => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
        Action reload = () => type.GetMethod("LoadClip", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
        try
        {
            set("clip", a); window.Show(); window.CreateGUI();
            var field = window.rootVisualElement.Query<ObjectField>().ToList().Single(item => item.label == "Preview Model");
            Check(field.panel != null, "preview field is attached to a native Editor panel");
            field.value = model;
            Check(get("previewModel") == model && !(bool)get("previewModelIsAuto"), "real preview field callback remembers explicit prefab");
            set("clip", b); reload(); Check(get("previewModel") == model, "clip change preserves preview prefab");
            window.DiscardChanges();
            Check(get("previewModel") == model, "draft discard preserves preview prefab");
            window.CreateGUI(); Check(get("previewModel") == model, "UI recreation preserves preview prefab");
            var serialized = EditorJsonUtility.ToJson(window);
            var restored = ScriptableObject.CreateInstance<AnimationEventEditorWindow>();
            try { EditorJsonUtility.FromJsonOverwrite(serialized, restored); restored.Show(); restored.CreateGUI(); Check(type.GetField("previewModel", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(restored) == model, "serialized window reload preserves prefab"); }
            finally { Object.DestroyImmediate(restored); }
        }
        finally { Object.DestroyImmediate(window); }
    }
    static void Case(string name, bool human)
    {
        var source = Path.Combine(Fixtures, name); var target = Root + "/" + name;
        Copy(Path.Combine(source, "Full"), target); var settings = Configure(target, human);
        if (name == "NoFace")
        {
            settings.bakeRotation = settings.bakeY = settings.bakeXZ = false;
            EditorUtility.SetDirty(settings); AssetDatabase.SaveAssetIfDirty(settings); Reconcile(target);
        }
        var rows = DirectAnimationMetadata.Sources(target).OrderBy(r => r.assetPath).ToArray();
        var firstPath = rows.First().assetPath;
        var payload = new AvatarMask(); AssetDatabase.CreateAsset(payload, target + "/Payload.mask");
        foreach (var row in rows)
        {
            var importer = Importer(row.assetPath); var definition = importer.clipAnimations.Single();
            definition.loopTime = true; definition.mirror = true; definition.cycleOffset = 0.125f;
            definition.events = new[] { new AnimationEvent { time = 0.4f, functionName = "PreservedCue", stringParameter = "all payloads", intParameter = 42, floatParameter = 0.75f, objectReferenceParameter = payload, messageOptions = SendMessageOptions.DontRequireReceiver } };
            definition.curves = new[] { new ClipAnimationInfoCurve { name = "CustomMotion", curve = AnimationCurve.Linear(0, 0, 1, 1) } };
            if (row == rows[0])
            {
                var custom = new AvatarMask(); var model = AssetDatabase.LoadAssetAtPath<GameObject>(row.assetPath);
                var transforms = model.GetComponentsInChildren<Transform>(true); custom.transformCount = transforms.Length;
                for (var index = 0; index < transforms.Length; index++)
                {
                    custom.SetTransformPath(index, AnimationUtility.CalculateTransformPath(transforms[index], model.transform));
                    custom.SetTransformActive(index, transforms[index].name != "Unused");
                }
                for (var index = 0; index < (int)AvatarMaskBodyPart.LastBodyPart; index++) custom.SetHumanoidBodyPartActive((AvatarMaskBodyPart)index, true);
                AssetDatabase.CreateAsset(custom, target + "/Custom.mask");
                definition.maskType = ClipAnimationMaskType.CopyFromOther; definition.maskSource = custom; definition.ConfigureClipFromMask(custom);
            }
            importer.clipAnimations = new[] { definition }; importer.SaveAndReimport();
        }
        Reconcile(target);
        var identities = rows.Select(r => Identity(Clip(r.assetPath))).ToArray();
        var flags = rows.Select(r => Flags(Importer(r.assetPath).clipAnimations.Single())).ToArray();
        var events = rows.Select(r => Events(Clip(r.assetPath))).ToArray();
        var roots = rows.Select(r => Importer(r.assetPath).motionNodeName).ToArray();
        var trs = rows.Select(r => TransformSignature(AssetDatabase.LoadAssetAtPath<GameObject>(r.assetPath))).ToArray();
        var skins = rows.Select(r => SkinSignature(AssetDatabase.LoadAssetAtPath<GameObject>(r.assetPath))).ToArray();
        var avatars = rows.Select(r => AvatarSignature(r.assetPath)).ToArray();
        var holder = ScriptableObject.CreateInstance<AnimationExportLibrary>();
        holder.clips.AddRange(rows.Select(r => new ManagedAnimationClip { clip = Clip(r.assetPath) }));
        AssetDatabase.CreateAsset(holder, target + "/ClipReferences.asset");
        var controller = AnimatorController.CreateAnimatorControllerAtPath(target + "/Motion.controller");
        foreach (var row in rows) controller.layers[0].stateMachine.AddState(Path.GetFileNameWithoutExtension(row.assetPath)).motion = Clip(row.assetPath);
        EditorUtility.SetDirty(controller); AssetDatabase.SaveAssetIfDirty(controller);
        var overrides = new AnimatorOverrideController(controller); AssetDatabase.CreateAsset(overrides, target + "/Overrides.overrideController");
        overrides[Clip(rows[0].assetPath).name] = Clip(rows.Last().assetPath); EditorUtility.SetDirty(overrides); AssetDatabase.SaveAssetIfDirty(overrides);
        var references = rows.Select((r, i) => Reference(r.assetPath, Root + "/References/" + name, "Full" + i)).ToArray();
        var fullModel = AssetDatabase.LoadAssetAtPath<GameObject>(references[0]);
        var prefabObject = Object.Instantiate(fullModel); var prefab = PrefabUtility.SaveAsPrefabAsset(prefabObject, target + "/Display.prefab"); Object.DestroyImmediate(prefabObject);
        Copy(Path.Combine(source, "Lightweight"), target); Reconcile(target);
        for (var index = 0; index < rows.Length; index++)
        {
            var path = rows[index].assetPath;
            Check(Identity(Clip(path)) == identities[index], name + ": in-place GUID/local ID " + index);
            Check(Flags(Importer(path).clipAnimations.Single()) == flags[index] && Events(Clip(path)) == events[index], name + ": native flags, masks, curves and event payloads " + index);
            Check(Importer(path).motionNodeName == roots[index], name + ": in-place motion root " + index);
            Check(TransformSignature(AssetDatabase.LoadAssetAtPath<GameObject>(path)) == trs[index], name + ": default transform TRS " + index);
            Check(SkinSignature(AssetDatabase.LoadAssetAtPath<GameObject>(path)) == skins[index], name + ": SMR roots and bone inventory " + index);
            Check(AvatarSignature(path) == avatars[index], name + ": Avatar rest skeleton " + index);
            Check(Identity(holder.clips[index].clip) == identities[index], name + ": serialized clip array reference " + index);
            Compare(name + "/Off/" + Clip(path).name, prefab, Clip(references[index]), Clip(path), !human);
        }
        Check(controller.layers[0].stateMachine.states.All(s => holder.clips.Any(c => c.clip == s.state.motion)), name + ": actual controller references survive");
        Check(Identity(overrides[Clip(rows[0].assetPath).name]) == identities.Last(), name + ": actual override-controller reference survives");
        PreviewChoice(prefab, Clip(rows[0].assetPath), Clip(rows.Last().assetPath));
        var fresh = Root + "/" + name + "Fresh"; Copy(Path.Combine(source, "Lightweight"), fresh); Configure(fresh, human);
        foreach (var row in DirectAnimationMetadata.Sources(fresh))
        {
            var index = Array.FindIndex(rows, r => Path.GetFileName(r.assetPath) == Path.GetFileName(row.assetPath));
            Check(Importer(row.assetPath).motionNodeName == roots[index], name + ": fresh Full/Lightweight motion-root agreement " + index);
        }
        settings.compression = AnimationCompression.Optimal; EditorUtility.SetDirty(settings); AssetDatabase.SaveAssetIfDirty(settings); Reconcile(target);
        for (var index = 0; index < rows.Length; index++)
        {
            var importer = Importer(references[index]); importer.animationCompression = ModelImporterAnimationCompression.Optimal;
            importer.animationRotationError = 0.01f; importer.animationPositionError = 0.01f; importer.animationScaleError = 0.5f; importer.SaveAndReimport();
            Compare(name + "/Optimal/" + Clip(rows[index].assetPath).name, prefab, Clip(references[index]), Clip(rows[index].assetPath), !human);
        }
        Copy(Path.Combine(source, "Full"), target); Reconcile(target);
        for (var index = 0; index < rows.Length; index++)
            Check(Identity(Clip(rows[index].assetPath)) == identities[index] && Events(Clip(rows[index].assetPath)) == events[index] && Flags(Importer(rows[index].assetPath).clipAnimations.Single()) == flags[index], name + ": restore Full retains metadata/references " + index);
        var before = rows.Select(r => File.ReadAllBytes(r.assetPath + ".meta")).ToArray();
        Check(rows.All(r => !DirectAnimationImporter.Reconcile(r.assetPath)), name + ": repeat reconcile performs no imports");
        Check(rows.Select((r, i) => before[i].SequenceEqual(File.ReadAllBytes(r.assetPath + ".meta"))).All(v => v), name + ": repeat reconcile metadata byte-stable");
        Remember(rows.Select(row => row.assetPath), target + "/ClipReferences.asset");
    }
    static void Remember(IEnumerable<string> paths, string referencePath)
    {
        foreach (var path in paths) savedClips.Add(new SavedClip { path = path, identity = Identity(Clip(path)), flags = Flags(Importer(path).clipAnimations.Single()), events = Events(Clip(path)), motionRoot = Importer(path).motionNodeName });
        var holder = AssetDatabase.LoadAssetAtPath<AnimationExportLibrary>(referencePath);
        savedReferences.Add(new SavedReferences { path = referencePath, identities = holder.clips.Select(c => Identity(c.clip)).ToArray() });
        File.WriteAllText(Path.Combine(Project, "lightweight-restart-state.json"), JsonUtility.ToJson(new RestartState { clips = savedClips.ToArray(), references = savedReferences.ToArray() }, true));
    }
    public static void Resume()
    {
        try
        {
            var state = JsonUtility.FromJson<RestartState>(File.ReadAllText(Path.Combine(Project, "lightweight-restart-state.json")));
            foreach (var entry in state.clips)
            {
                Check(Identity(Clip(entry.path)) == entry.identity && Flags(Importer(entry.path).clipAnimations.Single()) == entry.flags && Events(Clip(entry.path)) == entry.events && Importer(entry.path).motionNodeName == entry.motionRoot, "cold Editor restart retains clip/native data: " + entry.path);
                Check(!DirectAnimationImporter.Reconcile(entry.path), "cold Editor restart converges: " + entry.path);
            }
            foreach (var entry in state.references)
            {
                var holder = AssetDatabase.LoadAssetAtPath<AnimationExportLibrary>(entry.path);
                Check(holder != null && holder.clips.Select(c => Identity(c.clip)).SequenceEqual(entry.identities), "cold Editor restart retains serialized clip array: " + entry.path);
            }
            File.WriteAllText(Path.Combine(Project, "lightweight-restart-result.json"), JsonUtility.ToJson(new Result { success = true, unity = Application.unityVersion, checks = checks.ToArray(), measurements = measurements.ToArray() }, true));
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(Project, "lightweight-restart-result.json"), JsonUtility.ToJson(new Result { success = false, error = error.ToString(), checks = checks.ToArray() }, true));
            Debug.LogException(error); EditorApplication.Exit(1);
        }
    }
    public static void RunScale()
    {
        try
        {
            Check(File.Exists(Path.Combine(Project, "character-animation-export-validation.json")), "disposable project required");
            var source = Path.Combine(Fixtures, "Scale200"); var target = Root + "/Scale200";
            Copy(Path.Combine(source, "Full"), target); Configure(target, false);
            var rows = DirectAnimationMetadata.Sources(target).OrderBy(r => r.assetPath).ToArray();
            Check(rows.Length == 200, "actual 200 native FBX sources");
            var holder = ScriptableObject.CreateInstance<AnimationExportLibrary>();
            var payload = new AvatarMask(); AssetDatabase.CreateAsset(payload, target + "/Payload.mask");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(target + "/Motion.controller");
            for (var index = 0; index < rows.Length; index++)
            {
                var importer = Importer(rows[index].assetPath); var definition = importer.clipAnimations.Single();
                definition.events = new[] { new AnimationEvent { time = 0.5f, functionName = "ScaleCue", stringParameter = "clip " + index, intParameter = index, floatParameter = index * 0.01f, objectReferenceParameter = payload, messageOptions = SendMessageOptions.DontRequireReceiver } };
                importer.clipAnimations = new[] { definition }; importer.SaveAndReimport();
                holder.clips.Add(new ManagedAnimationClip { clip = Clip(rows[index].assetPath) });
                controller.layers[0].stateMachine.AddState("Motion" + index).motion = Clip(rows[index].assetPath);
            }
            AssetDatabase.CreateAsset(holder, target + "/ClipReferences.asset");
            EditorUtility.SetDirty(controller); AssetDatabase.SaveAssetIfDirty(controller); Reconcile(target);
            var identities = rows.Select(r => Identity(Clip(r.assetPath))).ToArray();
            var events = rows.Select(r => Events(Clip(r.assetPath))).ToArray();
            var flags = rows.Select(r => Flags(Importer(r.assetPath).clipAnimations.Single())).ToArray();
            foreach (var mode in new[] { "Lightweight", "Full", "Lightweight" })
            {
                Copy(Path.Combine(source, mode), target); Reconcile(target);
                for (var index = 0; index < rows.Length; index++)
                    Check(Identity(Clip(rows[index].assetPath)) == identities[index] && Events(Clip(rows[index].assetPath)) == events[index] && Flags(Importer(rows[index].assetPath).clipAnimations.Single()) == flags[index] && Identity(holder.clips[index].clip) == identities[index], "200-clip " + mode + " identity/events/settings/reference: " + index);
                Check(controller.layers[0].stateMachine.states.All(s => holder.clips.Any(c => c.clip == s.state.motion)), "200-clip " + mode + " actual controller references");
            }
            Check(rows.All(r => !DirectAnimationImporter.Reconcile(r.assetPath)), "200-clip repeat reconcile performs no imports");
            Remember(rows.Select(r => r.assetPath), target + "/ClipReferences.asset"); Finish(null);
        }
        catch (Exception error) { Finish(error); }
    }
    public static void Run()
    {
        try
        {
            Check(File.Exists(Path.Combine(Project, "character-animation-export-validation.json")), "disposable project required");
            foreach (var name in Directory.GetDirectories(Fixtures).Select(Path.GetFileName).OrderBy(x => x)) Case(name, name == "Valencia");
            Finish(null);
        }
        catch (Exception error) { Finish(error); }
    }
    public static void Diagnose()
    {
        var paths = new[] { Root + "/References/Example/Full0.fbx", Root + "/Example/Idle A.fbx" };
        foreach (var path in paths)
            File.WriteAllText(Path.Combine(Project, Path.GetFileNameWithoutExtension(path)+"-bindings.txt"), string.Join("\n", AnimationUtility.GetCurveBindings(Clip(path)).Select(Binding).OrderBy(x => x)) + "\nIMPORTER\n" + EditorJsonUtility.ToJson(Importer(path)));
        EditorApplication.Exit(0);
    }
    static void Finish(Exception error)
    {
        File.WriteAllText(Path.Combine(Project, "lightweight-result.json"), JsonUtility.ToJson(new Result { success = error == null, unity = Application.unityVersion, error = error?.ToString(), checks = checks.ToArray(), measurements = measurements.ToArray() }, true));
        if (error != null) Debug.LogException(error); EditorApplication.Exit(error == null ? 0 : 1);
    }
}
