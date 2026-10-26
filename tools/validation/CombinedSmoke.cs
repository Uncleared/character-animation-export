using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CharacterAnimationExport.Direct;
using CharacterAnimationEvents;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class CombinedSmoke
{
    const string Root = "Assets/Generated/Combined";
    static readonly List<string> checks = new List<string>();
    static string Project => Path.GetDirectoryName(Application.dataPath);
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks.Add(label); }
    static AnimationClip[] Clips(string path) => AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(clip => !clip.name.StartsWith("__preview__")).ToArray();
    static string Identity(AnimationClip clip) { AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long id); return guid + ":" + id; }
    static string Copy(string label, string target = Root)
    {
        var from = Path.Combine(Project, "ExternalFixtures", label);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories).OrderBy(file => file.EndsWith(".json") ? 0 : 1))
        {
            if (file.EndsWith(".meta")) continue;
            var destination = target + "/" + file.Substring(from.Length + 1).Replace('\\', '/');
            if (File.Exists(destination) && File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(destination))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(file, destination, true);
            AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
        }
        DirectAnimationImporter.QueueFolder(target); DirectAnimationImporter.ProcessPending();
        foreach (var row in DirectAnimationMetadata.Sources(target)) DirectAnimationImporter.Reconcile(row.assetPath);
        return DirectAnimationMetadata.Sources(target).Single().assetPath;
    }
    static AnimationImportSettings Configure(string folder)
    {
        var source = DirectAnimationMetadata.Sources(folder).First();
        var settings = source.Settings;
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<AnimationImportSettings>(); settings.profileId = source.profile.profileId;
            settings.characterName = source.profile.characterName; settings.rig = CharacterRig.Generic;
            AssetDatabase.CreateAsset(settings, source.SettingsPath); AssetDatabase.SaveAssetIfDirty(settings);
        }
        DirectAnimationImporter.QueueFolder(folder); DirectAnimationImporter.ProcessPending();
        foreach (var row in DirectAnimationMetadata.Sources(folder)) DirectAnimationImporter.Reconcile(row.assetPath);
        return settings;
    }
    static void Events(AnimationEvent[] expected, AnimationClip clip, string label)
    {
        var actual = AnimationUtility.GetAnimationEvents(clip);
        Check(expected.Length == actual.Length, label + " event count");
        for (var i = 0; i < expected.Length; i++)
            Check(Math.Abs(expected[i].time - actual[i].time) < 0.00003f && expected[i].functionName == actual[i].functionName &&
                expected[i].stringParameter == actual[i].stringParameter && expected[i].floatParameter == actual[i].floatParameter &&
                expected[i].intParameter == actual[i].intParameter && expected[i].objectReferenceParameter == actual[i].objectReferenceParameter &&
                expected[i].messageOptions == actual[i].messageOptions, label + " event seconds/payload retained");
    }
    static string Binding(EditorCurveBinding binding) => binding.path + "|" + binding.type.FullName + "|" + binding.propertyName;
    static void Compare(AnimationClip reference, AnimationClip current, string label)
    {
        var source = AnimationUtility.GetCurveBindings(reference).ToDictionary(Binding);
        var target = AnimationUtility.GetCurveBindings(current).ToDictionary(Binding);
        Check(source.Keys.OrderBy(key => key).SequenceEqual(target.Keys.OrderBy(key => key)), label + " curve inventory");
        var maximum = 0f;
        var worst = "";
        foreach (var pair in source)
        {
            var before = AnimationUtility.GetEditorCurve(reference, pair.Value);
            var after = AnimationUtility.GetEditorCurve(current, target[pair.Key]);
            for (var sample = 0; sample <= 120; sample++)
            {
                var error = Mathf.Abs(before.Evaluate(reference.length * sample / 120f) - after.Evaluate(current.length * sample / 120f));
                if (error > maximum) { maximum = error; worst = pair.Key; }
            }
        }
        var referenceModel = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(reference)));
        var currentModel = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(current)));
        var angle = 0f; var position = 0f; var shape = 0f; var scale = 0f;
        try
        {
            var oldTransforms = referenceModel.GetComponentsInChildren<Transform>(true).ToDictionary(item => AnimationUtility.CalculateTransformPath(item, referenceModel.transform));
            var newTransforms = currentModel.GetComponentsInChildren<Transform>(true).ToDictionary(item => AnimationUtility.CalculateTransformPath(item, currentModel.transform));
            for (var sample = 0; sample <= 120; sample++)
            {
                reference.SampleAnimation(referenceModel, reference.length * sample / 120f);
                current.SampleAnimation(currentModel, current.length * sample / 120f);
                foreach (var pair in oldTransforms)
                {
                    var next = newTransforms[pair.Key];
                    angle = Mathf.Max(angle, Quaternion.Angle(pair.Value.localRotation,next.localRotation));
                    position = Mathf.Max(position,Vector3.Distance(pair.Value.localPosition,next.localPosition));
                    scale = Mathf.Max(scale,Vector3.Distance(pair.Value.localScale,next.localScale));
                    var beforeMesh = pair.Value.GetComponent<SkinnedMeshRenderer>(); var afterMesh = next.GetComponent<SkinnedMeshRenderer>();
                    if(beforeMesh != null && afterMesh != null)
                        for(var index=0;index<beforeMesh.sharedMesh.blendShapeCount;index++)
                            shape=Mathf.Max(shape,Mathf.Abs(beforeMesh.GetBlendShapeWeight(index)-afterMesh.GetBlendShapeWeight(index)));
                }
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(referenceModel);UnityEngine.Object.DestroyImmediate(currentModel); }
        Check(angle<=0.05f&&position<=0.0001f&&shape<=0.01f&&scale<=0.0001f,
            label+" sampled poses: angle="+angle+", position="+position+", face="+shape+", scale="+scale+"; raw max="+maximum+" at "+worst);
    }
    public static void Run()
    {
        if (!File.Exists(Path.Combine(Project, "character-animation-export-validation.json"))) throw new InvalidOperationException("Use a new disposable validation project.");
        try
        {
            if(AssetDatabase.IsValidFolder("Assets/Generated"))AssetDatabase.DeleteAsset("Assets/Generated");
            Copy("Base"); var settings = Configure(Root);
            var source = DirectAnimationMetadata.Sources(Root).Single(); var path = source.assetPath;
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.animationCompression = ModelImporterAnimationCompression.Off; importer.SaveAndReimport();
            var clips = Clips(path);
            Check(clips.Length == 2 && source.combined, "one FBX imports two friendly clips");
            var names = source.clips.Select(item => item.name).ToArray();
            Check(clips.Select(clip => clip.name).OrderBy(name => name).SequenceEqual(names.OrderBy(name => name)), "friendly names map to stable internal takes");
            foreach (var clip in clips)
            {
                var bindings = AnimationUtility.GetCurveBindings(clip);
                Check(bindings.Any(binding => binding.path.Contains("Nested Moving Lid") && binding.propertyName.Contains("Rotation")), clip.name + " nested lid animation");
                Check(bindings.Any(binding => binding.propertyName == "blendShape.Expression"), clip.name + " facial animation");
            }
            var separateRoot = "Assets/Generated/Separate";
            var from = Path.Combine(Project, "ExternalFixtures/Separate");
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            { var destination = separateRoot + "/" + file.Substring(from.Length+1).Replace('\\','/'); Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(file,destination,true); AssetDatabase.ImportAsset(destination,ImportAssetOptions.ForceSynchronousImport); }
            Configure(separateRoot);
            var references = new Dictionary<string,AnimationClip>();
            foreach (var row in DirectAnimationMetadata.Sources(separateRoot))
            { var separateImporter=(ModelImporter)AssetImporter.GetAtPath(row.assetPath);separateImporter.animationCompression=ModelImporterAnimationCompression.Off;separateImporter.SaveAndReimport();var clip=Clips(row.assetPath).Single();references.Add(clip.name,clip); }
            foreach (var clip in clips) Compare(references[clip.name],clip,"combined versus separate "+clip.name);
            var payload= new AvatarMask();AssetDatabase.CreateAsset(payload,Root+"/Payload.mask");
            var definitions=importer.clipAnimations;
            for(var i=0;i<definitions.Length;i++)
            {
                definitions[i].loopTime=true;definitions[i].mirror=i==0;definitions[i].cycleOffset=0.17f;
                definitions[i].events=new[]{new AnimationEvent{time=i==0?0.8f:0.4f,functionName="Cue"+i,stringParameter="payload"+i,floatParameter=0.125f+i,intParameter=10+i,objectReferenceParameter=payload,messageOptions=SendMessageOptions.DontRequireReceiver}};
            }
            importer.clipAnimations=definitions;importer.SaveAndReimport();
            foreach(var definition in definitions)
            {
                var referencePath=AssetDatabase.GetAssetPath(references[definition.name]);
                var referenceImporter=(ModelImporter)AssetImporter.GetAtPath(referencePath);
                var referenceDefinition=referenceImporter.clipAnimations.Single();
                referenceDefinition.loopTime=definition.loopTime;referenceDefinition.mirror=definition.mirror;referenceDefinition.cycleOffset=definition.cycleOffset;
                referenceImporter.clipAnimations=new[]{referenceDefinition};referenceImporter.SaveAndReimport();
                references[definition.name]=Clips(referencePath).Single();
            }
            clips=Clips(path);
            var identities=clips.ToDictionary(clip=>clip.name,Identity);
            var events=clips.ToDictionary(clip=>clip.name,AnimationUtility.GetAnimationEvents);
            var controller=AnimatorController.CreateAnimatorControllerAtPath(Root+"/Reference.controller");
            var states=new Dictionary<string,AnimatorState>();
            foreach(var clip in clips){var state=controller.layers[0].stateMachine.AddState(clip.name);state.motion=clip;states.Add(clip.name,state);}
            EditorUtility.SetDirty(controller);AssetDatabase.SaveAssetIfDirty(controller);
            foreach(var label in new[]{"Same","Extended","Moved","Reordered","Added","Removed","Restored","Deleted","Readded"})
            {
                Copy(label);clips=Clips(path);
                var expected=DirectAnimationMetadata.Find(path).clips;
                Check(clips.Length==expected.Length,label+" exact take inventory");
                foreach(var clip in clips.Where(clip=>identities.ContainsKey(clip.name)))
                {
                    Check(Identity(clip)==identities[clip.name],label+" stable GUID/local ID "+clip.name);
                    Events(events[clip.name],clip,label+" "+clip.name);
                    Check(states[clip.name].motion==clip,label+" Animator reference "+clip.name);
                    var definition=((ModelImporter)AssetImporter.GetAtPath(path)).clipAnimations.Single(item=>item.name==clip.name);
                    Check(definition.loopTime&&Math.Abs(definition.cycleOffset-0.17f)<0.00001f,label+" native loop/cycle retained "+clip.name);
                }
                if(label=="Moved")foreach(var clip in clips)Compare(references[clip.name],clip,"timeline moved "+clip.name);
                var count=DirectAnimationImporter.ReconcileCount;
                Check(!DirectAnimationImporter.Reconcile(path)&&DirectAnimationImporter.ReconcileCount==count,label+" no-op reimport count zero");
            }
            Copy("NewChild");
            var childDefinitions=((ModelImporter)AssetImporter.GetAtPath(path)).clipAnimations;
            foreach(var definition in childDefinitions)
            {
                var mask=new AvatarMask();
                try
                {
                    definition.ConfigureMaskFromClip(ref mask);
                    var child=Enumerable.Range(0,mask.transformCount).Where(index=>mask.GetTransformPath(index).EndsWith("New Nested Prop")).ToArray();
                    Check(child.Length==1&&mask.GetTransformActive(child[0]),"new prop automatically included in "+definition.name);
                }
                finally {UnityEngine.Object.DestroyImmediate(mask);}
            }
            var customDefinition=childDefinitions.First();
            var customMask=new AvatarMask();customDefinition.ConfigureMaskFromClip(ref customMask);
            var customIndex=Enumerable.Range(0,customMask.transformCount).First(index=>customMask.GetTransformPath(index).Contains("Nested Moving Lid"));
            var customPath=customMask.GetTransformPath(customIndex);
            customMask.SetTransformActive(customIndex,false);customDefinition.ConfigureClipFromMask(customMask);UnityEngine.Object.DestroyImmediate(customMask);
            importer=(ModelImporter)AssetImporter.GetAtPath(path);importer.clipAnimations=childDefinitions;importer.SaveAndReimport();
            DirectAnimationImporter.Reconcile(path);
            foreach(var definition in ((ModelImporter)AssetImporter.GetAtPath(path)).clipAnimations)
            {
                var mask=new AvatarMask();
                try {definition.ConfigureMaskFromClip(ref mask);var index=Enumerable.Range(0,mask.transformCount).First(item=>mask.GetTransformPath(item)==customPath);Check(mask.GetTransformActive(index)==(definition.name!=customDefinition.name),"native custom mask retained only on selected take "+definition.name);}
                finally {UnityEngine.Object.DestroyImmediate(mask);}
            }
            var eventClip=Clips(path).First(clip=>clip.name==names[0]);
            var eventClipName=eventClip.name;
            var originalEvents=AnimationUtility.GetAnimationEvents(eventClip);
            var newEvent=new AnimationEvent{time=0.1f,functionName="IndependentEditor",stringParameter="selected take"};
            AnimationEventClipIO.Save(eventClip,new[]{newEvent},AnimationEventClipIO.Snapshot(eventClip));
            var eventEdited=Clips(path).Single(clip=>clip.name==eventClipName);
            Events(new[]{newEvent},eventEdited,"independent editor saves selected combined take");
            foreach(var clip in Clips(path).Where(clip=>clip.name!=eventEdited.name&&events.ContainsKey(clip.name)))Events(events[clip.name],clip,"independent editor leaves other takes");
            AnimationEventClipIO.Save(eventEdited,originalEvents,AnimationEventClipIO.Snapshot(eventEdited));
            Copy("ShortAndOtherExtended");
            var afterShort=Clips(path);var sourceShort=DirectAnimationMetadata.Find(path);
            foreach(var clip in afterShort.Where(clip=>events.ContainsKey(clip.name))) Events(events[clip.name],clip,"shortened recovery "+clip.name);
            Check(sourceShort.clips.Any(row=>afterShort.Single(clip=>clip.name==row.name).length>row.duration+0.01f),"blocked clip retains old native bounds");
            Check(sourceShort.clips.Any(row=>afterShort.Single(clip=>clip.name==row.name).length>references.GetValueOrDefault(row.name,afterShort.Single(clip=>clip.name==row.name)).length+0.1f),"other clip extends while one is blocked");
            var retainedRanges=sourceShort.clips.Select(row=>new{name=row.name,exportedDuration=row.duration,nativeLength=afterShort.Single(clip=>clip.name==row.name).length}).ToArray();
            var backupFolder=Path.Combine(Project,".animation-export-recovery/DirectEvents");
            var backups=Directory.GetFiles(backupFolder,"*.meta.backup",SearchOption.AllDirectories).Length;
            var noOp=DirectAnimationImporter.ReconcileCount;
            Check(!DirectAnimationImporter.Reconcile(path)&&DirectAnimationImporter.ReconcileCount==noOp&&Directory.GetFiles(backupFolder,"*.meta.backup",SearchOption.AllDirectories).Length==backups,"blocked recheck creates no imports or backups");
            Copy("Readded");
            var firstClip=Clips(path).First(clip=>clip.name==names[0]);
            settings.overrides.Add(new ClipImportOverride{clip=firstClip,overrideRoot=true,bakeRotation=false});
            EditorUtility.SetDirty(settings);AssetDatabase.SaveAssetIfDirty(settings);DirectAnimationImporter.Reconcile(path);
            var overrides=((ModelImporter)AssetImporter.GetAtPath(path)).clipAnimations;
            Check(!overrides.Single(item=>item.name==firstClip.name).lockRootRotation&&overrides.Where(item=>item.name!=firstClip.name).All(item=>item.lockRootRotation),"root override applies to exact subasset only");
            settings.overrides.Clear();EditorUtility.SetDirty(settings);AssetDatabase.SaveAssetIfDirty(settings);DirectAnimationImporter.Reconcile(path);
            Copy("Renamed");
            var renamed=Clips(path);Check(renamed.Any(clip=>clip.name=="ExplicitNewName")&&!renamed.Any(clip=>clip.name==names[0]),"explicit rename retires old clip and creates new identity");
            foreach(var clip in renamed.Where(clip=>identities.ContainsKey(clip.name)))Events(events[clip.name],clip,"other clip survives explicit rename");
            var scaleRoot="Assets/Generated/Scale";Copy("Scale200",scaleRoot);Configure(scaleRoot);
            var scaleSource=DirectAnimationMetadata.Sources(scaleRoot).Single();Check(Clips(scaleSource.assetPath).Length==200,"200 takes import as 200 named clips");
            var scaleCount=DirectAnimationImporter.ReconcileCount;Check(!DirectAnimationImporter.Reconcile(scaleSource.assetPath)&&DirectAnimationImporter.ReconcileCount==scaleCount,"200-clip reconciliation converges");
            File.WriteAllText(Path.Combine(Project,"result.json"),JsonUtility.ToJson(new Result{success=true,checks=checks.ToArray(),retainedRanges=retainedRanges.Select(item=>item.name+": exported="+item.exportedDuration+", imported="+item.nativeLength).ToArray()},true));
            EditorApplication.Exit(0);
        }
        catch(Exception error)
        { File.WriteAllText(Path.Combine(Project,"result.json"),JsonUtility.ToJson(new Result{success=false,error=error.ToString(),checks=checks.ToArray()},true));Debug.LogException(error);EditorApplication.Exit(1); }
    }
    [Serializable] sealed class Result { public bool success;public string error;public string[] checks,retainedRanges; }
}
