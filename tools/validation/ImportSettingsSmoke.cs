using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CharacterAnimationExport.Direct;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

public static class ImportSettingsSmoke
{
    static readonly List<string> checks = new List<string>();
    static readonly List<string> measurements = new List<string>();
    static string Project => Path.GetDirectoryName(Application.dataPath);
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
    static AnimationClip[] Clips(string path) => AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(value => !value.name.StartsWith("__preview__")).ToArray();
    static ModelImporter Importer(string path) => (ModelImporter)AssetImporter.GetAtPath(path);
    static string Identity(Object value) { AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long id); return guid + ":" + id; }
    static bool Idle(string name) => (bool)typeof(DirectAnimationImporter).GetMethod("IsIdleName", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { name });
    static void Save(AnimationImportSettings settings) { EditorUtility.SetDirty(settings); AssetDatabase.SaveAssetIfDirty(settings); }
    static void Reconcile(string folder)
    {
        DirectAnimationImporter.QueueFolder(folder); DirectAnimationImporter.ProcessPending();
        foreach (var source in DirectAnimationMetadata.Sources(folder)) DirectAnimationImporter.Reconcile(source.assetPath);
    }
    static void Copy(string fixture, string folder)
    {
        var source = Path.Combine(Project, "ExternalFixtures", fixture);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).OrderBy(value => value.EndsWith(".json") ? 0 : 1))
        {
            if (file.EndsWith(".meta")) continue;
            var target = folder + "/" + file.Substring(source.Length + 1).Replace('\\', '/');
            if (File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(file))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target, true);
            AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceSynchronousImport);
        }
    }
    static AnimationImportSettings Setup(string fixture, string folder, bool uncompressed = false)
    {
        Copy(fixture, folder);
        var source = DirectAnimationMetadata.Sources(folder).First();
        var settings = ScriptableObject.CreateInstance<AnimationImportSettings>();
        Check(settings.setCompression && settings.compression == AnimationCompression.Optimal && settings.rotationError == 0.01f && settings.positionError == 0.01f && settings.scaleError == 0.5f && settings.loopIdleClips, fixture + " fresh defaults");
        settings.profileId = source.profile.profileId; settings.characterName = source.profile.characterName; settings.rig = CharacterRig.Generic;
        if (uncompressed) settings.compression = AnimationCompression.Off;
        AssetDatabase.CreateAsset(settings, source.SettingsPath); Save(settings); Reconcile(folder);
        return settings;
    }
    static void Compression(string folder, ModelImporterAnimationCompression mode, float rotation, float position, float scale, string label)
    {
        foreach (var source in DirectAnimationMetadata.Sources(folder))
        {
            var value = Importer(source.assetPath);
            Check(value.animationCompression == mode && value.animationRotationError == rotation && value.animationPositionError == position && value.animationScaleError == scale, label + " " + Path.GetFileName(source.assetPath));
        }
    }
    static void NoOp(string folder, string label)
    {
        DirectAnimationImporter.ProcessPending();
        var paths = DirectAnimationMetadata.Sources(folder).Select(value => value.assetPath).ToArray();
        var bytes = paths.ToDictionary(value => value, value => File.ReadAllBytes(value + ".meta"));
        var count = DirectAnimationImporter.ReconcileCount;
        foreach (var path in paths) Check(!DirectAnimationImporter.Reconcile(path), label + " no reconcile import");
        Check(count == DirectAnimationImporter.ReconcileCount && paths.All(path => bytes[path].SequenceEqual(File.ReadAllBytes(path + ".meta"))), label + " byte-stable metadata and userData");
    }
    static string Mask(ModelImporterClipAnimation definition)
    {
        var mask = new AvatarMask();
        try { definition.ConfigureMaskFromClip(ref mask); return string.Join("|", Enumerable.Range(0, mask.transformCount).Select(i => mask.GetTransformPath(i) + ":" + mask.GetTransformActive(i))); }
        finally { Object.DestroyImmediate(mask); }
    }
    static void Events(AnimationEvent[] expected, AnimationClip clip, string label)
    {
        var actual = AnimationUtility.GetAnimationEvents(clip);
        Check(expected.Length == actual.Length && expected.Zip(actual, (a,b) => Math.Abs(a.time-b.time)<0.00003f && a.functionName==b.functionName && a.stringParameter==b.stringParameter && a.floatParameter==b.floatParameter && a.intParameter==b.intParameter && a.messageOptions==b.messageOptions && a.objectReferenceParameter==b.objectReferenceParameter).All(value=>value), label + " event seconds and full payload");
    }
    static void TestCase(string fixture, string folder)
    {
        var settings = Setup(fixture, folder);
        Compression(folder, ModelImporterAnimationCompression.Optimal, 0.01f, 0.01f, 0.5f, fixture + " requested defaults");
        foreach (var source in DirectAnimationMetadata.Sources(folder))
            Check(Importer(source.assetPath).clipAnimations.All(value => value.loopTime == Idle(value.name)), fixture + " automatic Idle per take");
        NoOp(folder, fixture + " initial");

        var selected = DirectAnimationMetadata.Sources(folder).First(value => Importer(value.assetPath).clipAnimations.Any(item => Idle(item.name)));
        var path = selected.assetPath; var importer = Importer(path); var definitions = importer.clipAnimations;
        var definition = definitions.First(value => Idle(value.name)); var name = definition.name;
        var payload = new AvatarMask(); AssetDatabase.CreateAsset(payload, folder + "/Payload.mask");
        definition.loopTime = false; definition.loopPose = false; definition.mirror = true; definition.cycleOffset = 0.17f;
        definition.events = new[] { new AnimationEvent { time=0.2f, functionName="Cue", stringParameter="settings feature", floatParameter=0.125f, intParameter=42, objectReferenceParameter=payload, messageOptions=SendMessageOptions.DontRequireReceiver } };
        var nativeMask = new AvatarMask();
        definition.ConfigureMaskFromClip(ref nativeMask);
        var lid = Enumerable.Range(0, nativeMask.transformCount).First(i => nativeMask.GetTransformPath(i).Contains("Nested Moving Lid"));
        nativeMask.SetTransformActive(lid, false); definition.ConfigureClipFromMask(nativeMask); Object.DestroyImmediate(nativeMask);
        importer.clipAnimations = definitions; importer.SaveAndReimport();
        var clip = Clips(path).Single(value => value.name == name); var identity = Identity(clip); var events = AnimationUtility.GetAnimationEvents(clip);
        var maskSignature = Mask(Importer(path).clipAnimations.Single(value => value.name == name));
        var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Reference.controller");
        var state = controller.layers[0].stateMachine.AddState("Selected"); state.motion = clip; EditorUtility.SetDirty(controller); AssetDatabase.SaveAssetIfDirty(controller);
        Reconcile(folder);
        definition = Importer(path).clipAnimations.Single(value => value.name == name);
        Check(definition.loopTime && !definition.loopPose && definition.mirror && definition.cycleOffset == 0.17f && Mask(definition) == maskSignature, fixture + " Idle rule preserves pose, mirror, cycle and native mask");
        Events(events, Clips(path).Single(value => value.name == name), fixture + " native edits");

        foreach (var error in new[] {0f, 0.01f, 100f})
        {
            settings.rotationError=error; settings.positionError=error; settings.scaleError=error; Save(settings); Reconcile(folder);
            Compression(folder, ModelImporterAnimationCompression.Optimal, error, error, error, fixture + " native round trip " + error);
            NoOp(folder, fixture + " boundary " + error);
        }
        settings.compression = AnimationCompression.KeyframeReduction;
        settings.rotationError=0.01f; settings.positionError=0.01f; settings.scaleError=0.5f; Save(settings); Reconcile(folder);
        Compression(folder, ModelImporterAnimationCompression.KeyframeReduction,0.01f,0.01f,0.5f, fixture + " keyframe reduction");
        settings.compression=AnimationCompression.Off; settings.rotationError=9; settings.positionError=8; settings.scaleError=7; Save(settings); Reconcile(folder);
        Compression(folder, ModelImporterAnimationCompression.Off,0.01f,0.01f,0.5f, fixture + " Off retains native error fields");
        NoOp(folder,fixture+" Off");

        settings.setCompression=false; settings.loopIdleClips=false; Save(settings);
        importer=Importer(path); importer.animationCompression=ModelImporterAnimationCompression.KeyframeReduction;
        importer.animationRotationError=0.25f; importer.animationPositionError=0.75f; importer.animationScaleError=1;
        definitions=importer.clipAnimations; definitions.Single(value=>value.name==name).loopTime=false;
        foreach(var item in definitions.Where(value=>!Idle(value.name)))item.loopTime=true;
        importer.clipAnimations=definitions; importer.SaveAndReimport(); Reconcile(folder);
        importer=Importer(path);
        Check(importer.animationCompression==ModelImporterAnimationCompression.KeyframeReduction && importer.animationRotationError==0.25f && importer.animationPositionError==0.75f && importer.animationScaleError==1 && !importer.clipAnimations.Single(value=>value.name==name).loopTime && importer.clipAnimations.Where(value=>!Idle(value.name)).All(value=>value.loopTime), fixture+" disabled policies preserve native edits");
        NoOp(folder,fixture+" disabled");

        settings.setCompression=true; settings.loopIdleClips=true; settings.compression=AnimationCompression.Optimal;
        settings.rotationError=0.01f; settings.positionError=0.01f; settings.scaleError=0.5f;
        settings.overrides.Add(new ClipImportOverride{clip=Clips(path).Single(value=>value.name==name),overrideLoop=true,loopTime=false}); Save(settings); Reconcile(folder);
        Check(!Importer(path).clipAnimations.Single(value=>value.name==name).loopTime,fixture+" non-looping Idle override wins");
        settings.loopIdleClips=false; settings.overrides[0].loopTime=true; Save(settings); Reconcile(folder);
        Check(Importer(path).clipAnimations.Single(value=>value.name==name).loopTime,fixture+" explicit loop override works with automatic rule disabled");
        settings.overrides.Clear(); settings.loopIdleClips=true; Save(settings); Reconcile(folder);
        clip=Clips(path).Single(value=>value.name==name);
        Check(Identity(clip)==identity && state.motion==clip,fixture+" GUID/local ID and Animator reference retained");
        Events(events,clip,fixture+" compression and loop edits");
        Check(Mask(Importer(path).clipAnimations.Single(value=>value.name==name))==maskSignature,fixture+" native prop mask retained across policies");

        var unchanged=File.ReadAllBytes(path+".meta"); settings.rotationError=float.NaN;
        var rejected=false;try{DirectAnimationImporter.Reconcile(path);}catch(InvalidOperationException error){rejected=error.Message.Contains("finite");}
        Check(rejected&&unchanged.SequenceEqual(File.ReadAllBytes(path+".meta")),fixture+" non-finite input rejected before mutation");
        var messages=0;Application.LogCallback onLog=(message,trace,type)=>{if(type==LogType.Error&&message.Contains("compression errors must be finite"))messages++;};
        Application.logMessageReceived+=onLog;
        try{DirectAnimationImporter.Queue(path);DirectAnimationImporter.ProcessPending();DirectAnimationImporter.Queue(path);DirectAnimationImporter.ProcessPending();}
        finally{Application.logMessageReceived-=onLog;}
        Check(messages==1&&unchanged.SequenceEqual(File.ReadAllBytes(path+".meta")),fixture+" invalid input logs once without repeat imports");
        settings.rotationError=0.01f; settings.compression=(AnimationCompression)2;rejected=false;
        try{DirectAnimationImporter.Reconcile(path);}catch(InvalidOperationException error){rejected=error.Message.Contains("Optimal");}
        Check(rejected&&unchanged.SequenceEqual(File.ReadAllBytes(path+".meta")),fixture+" legacy-only enum rejected before mutation");
        settings.compression=AnimationCompression.Optimal;
        var serialized=new SerializedObject(settings);serialized.FindProperty("positionError").floatValue=-3;serialized.FindProperty("scaleError").floatValue=1000;serialized.ApplyModifiedPropertiesWithoutUndo();
        Check(settings.positionError==0&&settings.scaleError==100,fixture+" serialized finite values clamp to native-tested bounds");
        settings.positionError=0.01f;settings.scaleError=0.5f;Save(settings);Reconcile(folder);
        Undo.IncrementCurrentGroup();Undo.RecordObject(settings,"Compression test");settings.rotationError=0.02f;EditorUtility.SetDirty(settings);Undo.FlushUndoRecordObjects();Save(settings);Reconcile(folder);
        Check(Importer(path).animationRotationError==0.02f,fixture+" edited error applies");
        Undo.PerformUndo();Reconcile(folder);
        Check(settings.rotationError==0.01f&&Importer(path).animationRotationError==0.01f,fixture+" Undo restores error and importer");
        importer=Importer(path);definitions=importer.clipAnimations;definition=definitions.Single(value=>value.name==name);
        definition.name="Nonlooping Stance";definition.loopTime=false;importer.clipAnimations=definitions;importer.SaveAndReimport();Reconcile(folder);
        Check(!Importer(path).clipAnimations.Single(value=>value.name=="Nonlooping Stance").loopTime,fixture+" rename away from Idle respects native non-looping value");
        importer=Importer(path);definitions=importer.clipAnimations;definition=definitions.Single(value=>value.name=="Nonlooping Stance");definition.name="Character_IDLE_02";
        importer.clipAnimations=definitions;importer.SaveAndReimport();Reconcile(folder);
        Check(Importer(path).clipAnimations.Single(value=>value.name=="Character_IDLE_02").loopTime,fixture+" native rename to Idle is detected");
        NoOp(folder,fixture+" final");
    }
    static void TestMotion()
    {
        const string referenceFolder="Assets/Settings/MotionReference", compressedFolder="Assets/Settings/MotionOptimal";
        Setup("Base",referenceFolder,true);Setup("Base",compressedFolder);
        var referencePath=DirectAnimationMetadata.Sources(referenceFolder).Single().assetPath;
        var compressedPath=DirectAnimationMetadata.Sources(compressedFolder).Single().assetPath;
        Check(Importer(referencePath).animationCompression==ModelImporterAnimationCompression.Off,"motion reference truly uncompressed");
        var referenceModel=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(referencePath));
        var compressedModel=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(compressedPath));
        try
        {
            var before=referenceModel.GetComponentsInChildren<Transform>(true).ToDictionary(value=>AnimationUtility.CalculateTransformPath(value,referenceModel.transform));
            var after=compressedModel.GetComponentsInChildren<Transform>(true).ToDictionary(value=>AnimationUtility.CalculateTransformPath(value,compressedModel.transform));
            var referenceClips=Clips(referencePath).ToDictionary(value=>value.name);
            var anyLidMotion=false;
            foreach(var clip in Clips(compressedPath))
            {
                var angle=0f;var position=0f;var scale=0f;var face=0f;var lidMotion=0f;Quaternion? firstLid=null;
                var bindings=AnimationUtility.GetCurveBindings(clip);
                Check(bindings.Any(value=>value.path.Contains("Nested Moving Lid")&&value.propertyName.Contains("Rotation"))&&bindings.Any(value=>value.propertyName=="blendShape.Expression"),clip.name+" Optimal retains animated lid and face bindings");
                for(var sample=0;sample<=120;sample++)
                {
                    referenceClips[clip.name].SampleAnimation(referenceModel,referenceClips[clip.name].length*sample/120f);
                    clip.SampleAnimation(compressedModel,clip.length*sample/120f);
                    foreach(var pair in before)
                    {
                        var next=after[pair.Key];angle=Mathf.Max(angle,Quaternion.Angle(pair.Value.localRotation,next.localRotation));
                        position=Mathf.Max(position,Vector3.Distance(pair.Value.localPosition,next.localPosition));scale=Mathf.Max(scale,Vector3.Distance(pair.Value.localScale,next.localScale));
                        if(pair.Key.Contains("Nested Moving Lid")){if(firstLid==null)firstLid=next.localRotation;lidMotion=Mathf.Max(lidMotion,Quaternion.Angle(firstLid.Value,next.localRotation));}
                        var a=pair.Value.GetComponent<SkinnedMeshRenderer>();var b=next.GetComponent<SkinnedMeshRenderer>();
                        if(a!=null&&b!=null)for(var i=0;i<a.sharedMesh.blendShapeCount;i++)face=Mathf.Max(face,Mathf.Abs(a.GetBlendShapeWeight(i)-b.GetBlendShapeWeight(i)));
                    }
                }
                var measurement=clip.name+": angle="+angle+", position="+position+", scale="+scale+", face="+face+", lid excursion="+lidMotion;measurements.Add(measurement);
                anyLidMotion|=lidMotion>1;
                // Unity Optimal changes between-frame interpolation even when
                // all source keys survive. Check baked frames against Off, then
                // dense sampling against an independently set native Optimal.
                angle=position=scale=face=0;
                var frames=Mathf.RoundToInt(referenceClips[clip.name].length*referenceClips[clip.name].frameRate);
                for(var sample=0;sample<=frames;sample++)
                {
                    var time=sample/referenceClips[clip.name].frameRate;
                    referenceClips[clip.name].SampleAnimation(referenceModel,time);clip.SampleAnimation(compressedModel,time);
                    foreach(var pair in before)
                    {
                        var next=after[pair.Key];angle=Mathf.Max(angle,Quaternion.Angle(pair.Value.localRotation,next.localRotation));
                        position=Mathf.Max(position,Vector3.Distance(pair.Value.localPosition,next.localPosition));scale=Mathf.Max(scale,Vector3.Distance(pair.Value.localScale,next.localScale));
                        var a=pair.Value.GetComponent<SkinnedMeshRenderer>();var b=next.GetComponent<SkinnedMeshRenderer>();
                        if(a!=null&&b!=null)for(var i=0;i<a.sharedMesh.blendShapeCount;i++)face=Mathf.Max(face,Mathf.Abs(a.GetBlendShapeWeight(i)-b.GetBlendShapeWeight(i)));
                    }
                }
                measurement=clip.name+" baked frames: angle="+angle+", position="+position+", scale="+scale+", face="+face;measurements.Add(measurement);
                Check(angle<=0.05f&&position<=0.0001f&&scale<=0.0001f&&face<=0.01f,"Optimal preserves uncompressed baked poses: "+measurement);
            }
            Check(anyLidMotion,"Optimal lid opens during the animated Idle clip");
            var nativeSettings=DirectAnimationMetadata.Sources(referenceFolder).Single().Settings;
            nativeSettings.setCompression=false;Save(nativeSettings);
            var native=Importer(referencePath);native.animationCompression=ModelImporterAnimationCompression.Optimal;
            native.animationRotationError=0.01f;native.animationPositionError=0.01f;native.animationScaleError=0.5f;native.SaveAndReimport();
            referenceClips=Clips(referencePath).ToDictionary(value=>value.name);
            foreach(var clip in Clips(compressedPath))
            {
                var angle=0f;var position=0f;var scale=0f;var face=0f;
                for(var sample=0;sample<=120;sample++)
                {
                    referenceClips[clip.name].SampleAnimation(referenceModel,referenceClips[clip.name].length*sample/120f);clip.SampleAnimation(compressedModel,clip.length*sample/120f);
                    foreach(var pair in before)
                    {
                        var next=after[pair.Key];angle=Mathf.Max(angle,Quaternion.Angle(pair.Value.localRotation,next.localRotation));
                        position=Mathf.Max(position,Vector3.Distance(pair.Value.localPosition,next.localPosition));scale=Mathf.Max(scale,Vector3.Distance(pair.Value.localScale,next.localScale));
                        var a=pair.Value.GetComponent<SkinnedMeshRenderer>();var b=next.GetComponent<SkinnedMeshRenderer>();
                        if(a!=null&&b!=null)for(var i=0;i<a.sharedMesh.blendShapeCount;i++)face=Mathf.Max(face,Mathf.Abs(a.GetBlendShapeWeight(i)-b.GetBlendShapeWeight(i)));
                    }
                }
                var measurement=clip.name+" versus manually configured native Optimal: angle="+angle+", position="+position+", scale="+scale+", face="+face;measurements.Add(measurement);
                Check(angle<=0.05f&&position<=0.0001f&&scale<=0.0001f&&face<=0.01f,"tool matches native Optimal between frames: "+measurement);
            }
        }
        finally{Object.DestroyImmediate(referenceModel);Object.DestroyImmediate(compressedModel);}
        NoOp(compressedFolder,"motion Optimal");
    }
    static void TestLegacySettings()
    {
        const string folder="Assets/Settings/OldSettings";Copy("Base",folder);
        var source=DirectAnimationMetadata.Sources(folder).First();
        var prototype=ScriptableObject.CreateInstance<AnimationImportSettings>();prototype.setCompression=false;prototype.loopIdleClips=false;prototype.rotationError=5;prototype.positionError=6;prototype.scaleError=7;
        prototype.profileId=source.profile.profileId;prototype.characterName=source.profile.characterName;prototype.rig=CharacterRig.Generic;
        AssetDatabase.CreateAsset(prototype,source.SettingsPath);Save(prototype);
        Reconcile(folder);
        foreach(var item in DirectAnimationMetadata.Sources(folder))
        {
            var native=Importer(item.assetPath);native.animationCompression=ModelImporterAnimationCompression.KeyframeReduction;
            native.animationRotationError=.8f;native.animationPositionError=.7f;native.animationScaleError=.6f;
            var definitions=native.clipAnimations;
            foreach(var definition in definitions)
            {
                definition.loopTime=false;definition.mirror=true;
                definition.events=new[]{new AnimationEvent{time=.2f,functionName="UpgradeCue",stringParameter=definition.name,intParameter=17,messageOptions=SendMessageOptions.DontRequireReceiver}};
            }
            native.clipAnimations=definitions;native.SaveAndReimport();
        }
        var paths=DirectAnimationMetadata.Sources(folder).Select(item=>item.assetPath).ToArray();
        var identities=paths.SelectMany(Clips).ToDictionary(clip=>clip.name,Identity);
        var events=paths.SelectMany(Clips).ToDictionary(clip=>clip.name,AnimationUtility.GetAnimationEvents);
        Check(paths.All(path=>Importer(path).animationCompression==ModelImporterAnimationCompression.KeyframeReduction&&Importer(path).clipAnimations.All(clip=>!clip.loopTime)),"legacy upgrade begins with differing native compression and non-looping Idle settings");
        var text=File.ReadAllText(source.SettingsPath);
        foreach(var field in new[]{"setCompression","compression","rotationError","positionError","scaleError","loopIdleClips"})
            text=System.Text.RegularExpressions.Regex.Replace(text,@"^  "+field+@":.*\r?\n","",System.Text.RegularExpressions.RegexOptions.Multiline);
        File.WriteAllText(source.SettingsPath,text);Resources.UnloadAsset(prototype);AssetDatabase.ImportAsset(source.SettingsPath,ImportAssetOptions.ForceSynchronousImport);
        var old=source.Settings;
        Check(old.setCompression&&old.compression==AnimationCompression.Optimal&&old.rotationError==0.01f&&old.positionError==0.01f&&old.scaleError==0.5f&&old.loopIdleClips,"pre-feature settings deserialize with requested defaults");
        old.rig=CharacterRig.Generic;Save(old);Reconcile(folder);
        Compression(folder,ModelImporterAnimationCompression.Optimal,.01f,.01f,.5f,"documented existing-folder upgrade");
        foreach(var path in paths)
        {
            Check(Importer(path).clipAnimations.All(clip=>clip.loopTime==Idle(clip.name)&&clip.mirror),"legacy upgrade applies Idle defaults and retains native mirror");
            foreach(var clip in Clips(path))
            {
                Check(Identity(clip)==identities[clip.name],"legacy upgrade retains existing clip identity");
                Events(events[clip.name],clip,"legacy upgrade");
            }
        }
        NoOp(folder,"pre-feature settings");
        var editor=Editor.CreateEditor(old);var panel=editor.CreateInspectorGUI();
        try
        {
            Check(panel.Q("compressionControls")!=null&&panel.Q("compressionErrors")!=null,"Inspector exposes compression controls");
            var flags=BindingFlags.Public|BindingFlags.Instance;
            Check(new[]{"rotationError","positionError","scaleError"}.All(value=>typeof(AnimationImportSettings).GetField(value,flags).IsDefined(typeof(DelayedAttribute),false)),"all three numeric controls use delayed binding");
            Check(new[]{"rotationError","positionError","scaleError"}.All(value=>panel.Q<FloatField>(value).isDelayed&&panel.Q<FloatField>(value).bindingPath==value),"actual Inspector FloatFields commit delayed through serialized bindings");
            old.setCompression=false;editor.serializedObject.Update();var disabled=editor.CreateInspectorGUI();Check(!disabled.Q("compressionControls").enabledSelf,"Inspector disables managed controls when off");
            old.setCompression=true;old.compression=AnimationCompression.Off;editor.serializedObject.Update();var noErrors=editor.CreateInspectorGUI();Check(!noErrors.Q("compressionErrors").enabledSelf,"Inspector disables errors for Off mode");
            old.compression=AnimationCompression.Optimal;Save(old);
        }
        finally{Object.DestroyImmediate(editor);}
    }
    public static void Run()
    {
        if(!File.Exists(Path.Combine(Project,"character-animation-export-validation.json")))throw new InvalidOperationException("Use a disposable validation project.");
        try
        {
            Check(Enum.GetValues(typeof(AnimationCompression)).Length==3,"only three supported compression modes exposed");
            foreach(var name in new[]{"Idle","idle","IDLE","Valencia_Standing_Idle","StandingIdle","IdleLookAround","IDLE_LookAround","Idle01","Idle.001","IdleToSit","Walk_To_Idle"})Check(Idle(name),"Idle word matches "+name);
            foreach(var name in new[]{"Bridle","Idleness","Idles","STANDINGIDLE","Walk",""})Check(!Idle(name),"Idle word rejects "+name);
            var sourceFile=Directory.GetFiles(Path.Combine(Project,"ExternalFixtures/Base"),"*.fbx").Single();
            Directory.CreateDirectory("Assets/Unrelated");File.Copy(sourceFile,"Assets/Unrelated/Unmanaged.fbx");AssetDatabase.ImportAsset("Assets/Unrelated/Unmanaged.fbx",ImportAssetOptions.ForceSynchronousImport);
            var unrelated=File.ReadAllBytes("Assets/Unrelated/Unmanaged.fbx.meta");
            TestCase("Base","Assets/Settings/Combined");TestCase("Separate","Assets/Settings/Separate");TestMotion();TestLegacySettings();TestLegacyState();TestLifecycle();
            Check(unrelated.SequenceEqual(File.ReadAllBytes("Assets/Unrelated/Unmanaged.fbx.meta")),"unrelated FBX metadata unchanged throughout");
            File.WriteAllText(Path.Combine(Project,"settings-result.json"),JsonUtility.ToJson(new Result{success=true,checks=checks.ToArray(),measurements=measurements.ToArray()},true));EditorApplication.Exit(0);
        }
        catch(Exception error){File.WriteAllText(Path.Combine(Project,"settings-result.json"),JsonUtility.ToJson(new Result{success=false,error=error.ToString(),checks=checks.ToArray(),measurements=measurements.ToArray()},true));Debug.LogException(error);EditorApplication.Exit(1);}
    }
    [Serializable]sealed class Result{public bool success;public string error;public string[] checks,measurements;}
    [Serializable]sealed class LegacyState{public string profileId,clipId,hash,blockedSignature;public string[] fullMaskPaths;public bool preserveNativeMask;public float fps,duration;}
    [Serializable]sealed class State3{public string profileId,hash;public ClipState3[] clips;public string[] sharedMaskPaths;}
    [Serializable]sealed class ClipState3{public string clipId,blockedSignature;public string[] fullMaskPaths;public bool preserveNativeMask,sharedMask;public float fps,duration;}
    static void TestLegacyState()
    {
        const string folder="Assets/Settings/LegacyV2";Setup("Separate",folder);
        foreach(var source in DirectAnimationMetadata.Sources(folder))
        {
            var importer=Importer(source.assetPath);var begin="\n[CharacterAnimationExport/3:";
            var start=importer.userData.IndexOf(begin,StringComparison.Ordinal);var end=importer.userData.IndexOf("]\n",start,StringComparison.Ordinal);
            var state=JsonUtility.FromJson<State3>(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(importer.userData.Substring(start+begin.Length,end-start-begin.Length))));
            var clip=state.clips.Single();var legacy=new LegacyState{profileId=state.profileId,clipId=clip.clipId,hash=state.hash,fps=clip.fps,duration=clip.duration,fullMaskPaths=clip.sharedMask?state.sharedMaskPaths:clip.fullMaskPaths,preserveNativeMask=clip.preserveNativeMask,blockedSignature=clip.blockedSignature};
            importer.userData=importer.userData.Substring(0,start)+"\n[CharacterAnimationExport/2:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(legacy)))+"]\n";importer.SaveAndReimport();
            var old=importer.userData;var bytes=File.ReadAllBytes(source.assetPath+".meta");var count=DirectAnimationImporter.ReconcileCount;
            DirectAnimationImporter.ProcessPending();Check(!DirectAnimationImporter.Reconcile(source.assetPath)&&count==DirectAnimationImporter.ReconcileCount&&Importer(source.assetPath).userData==old&&bytes.SequenceEqual(File.ReadAllBytes(source.assetPath+".meta")),"pre-feature V2 state remains byte-stable when new defaults already match");
        }
    }
    static void TestLifecycle()
    {
        const string folder="Assets/Settings/Lifecycle";var settings=Setup("Base",folder);var source=DirectAnimationMetadata.Sources(folder).Single();var path=source.assetPath;
        var payload=new AvatarMask();AssetDatabase.CreateAsset(payload,folder+"/Payload.mask");
        var importer=Importer(path);var definitions=importer.clipAnimations;
        foreach(var definition in definitions){definition.events=new[]{new AnimationEvent{time=0.8f,functionName="Lifecycle",objectReferenceParameter=payload,intParameter=7,stringParameter=definition.name}};definition.mirror=true;definition.cycleOffset=0.17f;}
        importer.clipAnimations=definitions;importer.SaveAndReimport();Reconcile(folder);
        var identities=Clips(path).ToDictionary(value=>value.name,Identity);var events=Clips(path).ToDictionary(value=>value.name,AnimationUtility.GetAnimationEvents);
        foreach(var label in new[]{"Moved","Removed","Readded"})
        {
            settings.rotationError+=0.01f;Save(settings);Copy(label,folder);Reconcile(folder);
            foreach(var clip in Clips(path).Where(value=>identities.ContainsKey(value.name)))
            {
                Check(Identity(clip)==identities[clip.name],label+" with changing compression retains clip identity");Events(events[clip.name],clip,label+" with changing compression");
                var definition=Importer(path).clipAnimations.Single(value=>value.name==clip.name);
                Check(definition.mirror&&definition.cycleOffset==0.17f&&(!Idle(clip.name)||definition.loopTime),label+" restores native settings with Idle policy");
            }
            NoOp(folder,label+" lifecycle");
        }
        settings.positionError=0.02f;Save(settings);Copy("ShortAndOtherExtended",folder);Reconcile(folder);
        foreach(var clip in Clips(path).Where(value=>events.ContainsKey(value.name)))Events(events[clip.name],clip,"shortening with compression change");
        var after=DirectAnimationMetadata.Find(path);
        Check(after.clips.Any(value=>Idle(value.name)&&Clips(path).Single(clip=>clip.name==value.name).length>value.duration+0.01f)&&Importer(path).animationPositionError==0.02f,"blocked short clip retains bounds while file-wide compression updates");
        NoOp(folder,"blocked settings lifecycle");
    }
}
