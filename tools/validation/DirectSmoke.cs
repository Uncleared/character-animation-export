using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CharacterAnimationExport;
using CharacterAnimationExport.Direct;
using CharacterAnimationEvents;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object=UnityEngine.Object;

public static class DirectSmoke
{
    static readonly List<string> checks=new List<string>();
    const string Root="Assets/Generated/Character";
    static string Project=>Path.GetDirectoryName(Application.dataPath);
    static void Check(bool value,string label) {if(!value)throw new Exception(label);checks.Add(label);}
    static void Folder(string path) {if(AssetDatabase.IsValidFolder(path))return;Folder(Path.GetDirectoryName(path).Replace('\\','/'));AssetDatabase.CreateFolder(Path.GetDirectoryName(path).Replace('\\','/'),Path.GetFileName(path));}
    static void Copy(string name)
    {
        var source=Path.Combine(Project,"ExternalFixtures",name);
        foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
        {
            var target=Path.Combine(Root,file.Substring(source.Length+1));
            Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(file,target,true);
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        DirectAnimationImporter.QueueFolder(Root);
        DirectAnimationImporter.ProcessPending();
        foreach(var sourceRow in DirectAnimationMetadata.Sources(Root))DirectAnimationImporter.Reconcile(sourceRow.assetPath);
    }
    static AnimationClip Clip(string path)=>AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Single(item=>!item.name.StartsWith("__preview__"));
    static string Identity(AnimationClip clip){AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip,out string guid,out long id);return guid+":"+id;}
    static void Mask(ModelImporterClipAnimation definition,string required)
    {
        var mask=new AvatarMask();
        try{definition.ConfigureMaskFromClip(ref mask);Check(Enumerable.Range(0,mask.transformCount).All(mask.GetTransformActive),"all imported transform mask entries enabled: "+required);Check(Enumerable.Range(0,mask.transformCount).Any(index=>mask.GetTransformPath(index).Contains(required)),"mask includes "+required);}
        finally{Object.DestroyImmediate(mask);}
    }
    static void SameEvents(AnimationEvent[] before,AnimationEvent[] after,string label)
    {
        Check(before.Length==after.Length,label+": event count");
        for(var i=0;i<before.Length;i++)Check(Math.Abs(before[i].time-after[i].time)<0.00002f&&before[i].functionName==after[i].functionName&&before[i].stringParameter==after[i].stringParameter&&before[i].floatParameter==after[i].floatParameter&&before[i].intParameter==after[i].intParameter&&before[i].objectReferenceParameter==after[i].objectReferenceParameter&&before[i].messageOptions==after[i].messageOptions,label+": event seconds and every payload field");
    }
    public static void Run()
    {
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), "character-animation-export-validation.json"))) throw new InvalidOperationException("Run only in a disposable validation project prepared by tools/prepare_unity_validation.py.");
        try
        {
            Folder(Root);
            Copy("Base");
            var rows=DirectAnimationMetadata.Sources(Root).ToArray();
            Check(rows.Length==2,"two direct FBXs imported without a library/window");
            var source=rows.First();var path=source.assetPath;var clip=Clip(path);
            Check(clip.name==source.clip.name,"friendly clip name");
            Check(Math.Abs(clip.length-source.clip.duration)<0.001f,"source strip duration");
            var importer=(ModelImporter)AssetImporter.GetAtPath(path);var definition=importer.clipAnimations.Single();
            Check(definition.lockRootRotation&&definition.lockRootHeightY&&definition.lockRootPositionXZ,"three automatic Bake Into Pose defaults");
            Mask(definition,"Nested Moving Lid");
            var bindings=AnimationUtility.GetCurveBindings(clip);
            Check(bindings.Any(item=>item.path.Contains("Nested Moving Lid")&&item.propertyName.Contains("Rotation")),"nested lid rotation curves");
            Check(bindings.Any(item=>item.propertyName=="blendShape.Expression"),"shape key curves");
            var payload=new AvatarMask();AssetDatabase.CreateAsset(payload,Root+"/EventPayload.mask");
            definition.loopTime=true;definition.mirror=true;
            definition.events=new[]{new AnimationEvent{time=0.5f,functionName="TestCue",stringParameter="all payloads",floatParameter=0.25f,intParameter=42,objectReferenceParameter=payload,messageOptions=SendMessageOptions.DontRequireReceiver}};
            importer.clipAnimations=new[]{definition};importer.SaveAndReimport();
            clip=Clip(path);var initialEvents=AnimationUtility.GetAnimationEvents(clip);
            Check(Math.Abs(initialEvents.Single().time-clip.length*0.5f)<0.00002f,"native FBX importer event time is normalized");
            var identity=Identity(clip);
            var controller=AnimatorController.CreateAnimatorControllerAtPath(Root+"/Reference.controller");
            var state=controller.layers[0].stateMachine.AddState("Motion");state.motion=clip;EditorUtility.SetDirty(controller);AssetDatabase.SaveAssetIfDirty(controller);
            var holder=ScriptableObject.CreateInstance<AnimationExportLibrary>();holder.clips.Add(new ManagedAnimationClip{clip=clip});AssetDatabase.CreateAsset(holder,Root+"/NativeReference.asset");AssetDatabase.SaveAssetIfDirty(holder);
            Check(!DirectAnimationImporter.Reconcile(path),"repeat importer converges without reimport");
            Copy("Extended");
            clip=Clip(path);Check(Identity(clip)==identity,"GUID and local clip file ID survive range update");
            SameEvents(initialEvents,AnimationUtility.GetAnimationEvents(clip),"lengthened clip");
            importer=(ModelImporter)AssetImporter.GetAtPath(path);definition=importer.clipAnimations.Single();
            Check(definition.loopTime&&definition.mirror,"loop and mirror settings retained");
            Check(state.motion==clip&&holder.clips.Single().clip==clip,"Animator and serialized asset references retained");
            Copy("NewChild");clip=Clip(path);
            Check(Identity(clip)==identity,"new prop retains clip identity");
            Mask(((ModelImporter)AssetImporter.GetAtPath(path)).clipAnimations.Single(),"New Nested Prop");
            Check(AnimationUtility.GetCurveBindings(clip).Any(item=>item.path.Contains("New Nested Prop")&&item.propertyName.Contains("Rotation")),"new nested prop rotation imported");
            SameEvents(initialEvents,AnimationUtility.GetAnimationEvents(clip),"new prop update");
            var snapshot=AnimationEventClipIO.Snapshot(clip);
            var edited=AnimationUtility.GetAnimationEvents(clip).Select(item=>AnimationEventClipIO.Clone(item)).ToArray();edited[0].time=0.2f;
            AnimationEventClipIO.Save(clip,edited,snapshot);clip=Clip(path);
            SameEvents(edited,AnimationUtility.GetAnimationEvents(clip),"independent FBX event editor");
            var conflict=false;try{AnimationEventClipIO.Save(clip,edited,snapshot);}catch(InvalidOperationException){conflict=true;}
            Check(conflict,"stale event drafts cannot overwrite native edits");
            Copy("Short");clip=Clip(path);importer=(ModelImporter)AssetImporter.GetAtPath(path);
            SameEvents(edited,AnimationUtility.GetAnimationEvents(clip),"shortened clip recovery fallback");
            Check(importer.clipAnimations.Single().lastFrame>importer.defaultClipAnimations.Single().lastFrame,"out-of-range cue retains old importer bounds");
            Check(Directory.GetFiles(Path.Combine(Project,".animation-export-recovery/DirectEvents"),"*.meta.backup",SearchOption.AllDirectories).Length>0,"out-of-range recovery metadata retained outside Assets");
            Check(!DirectAnimationImporter.Reconcile(path),"blocked range converges without repeated warning/import");
            Copy("NewChild");
            var settings=ScriptableObject.CreateInstance<AnimationImportSettings>();settings.profileId=source.profile.profileId;settings.characterName=source.profile.characterName;settings.bakeXZ=false;
            AssetDatabase.CreateAsset(settings,source.SettingsPath);AssetDatabase.SaveAssetIfDirty(settings);
            DirectAnimationImporter.Reconcile(path);importer=(ModelImporter)AssetImporter.GetAtPath(path);
            Check(!importer.clipAnimations.Single().lockRootPositionXZ,"folder defaults opt-out applied");
            var custom=new AvatarMask();custom.transformCount=1;custom.SetTransformPath(0,"");custom.SetTransformActive(0,true);AssetDatabase.CreateAsset(custom,Root+"/Custom.mask");
            definition=importer.clipAnimations.Single();definition.maskType=ClipAnimationMaskType.CopyFromOther;definition.maskSource=custom;definition.ConfigureClipFromMask(custom);importer.clipAnimations=new[]{definition};importer.SaveAndReimport();
            DirectAnimationImporter.Reconcile(path);Check(((ModelImporter)AssetImporter.GetAtPath(path)).clipAnimations.Single().maskSource==custom,"native custom mask retained");
            var other=Root+"/Unrelated.fbx";File.Copy(path,other,true);AssetDatabase.ImportAsset(other,ImportAssetOptions.ForceSynchronousImport);
            var otherMeta=File.ReadAllBytes(other+".meta");DirectAnimationImporter.QueueFolder(Root);DirectAnimationImporter.ProcessPending();
            Check(otherMeta.SequenceEqual(File.ReadAllBytes(other+".meta")),"unowned FBX in same folder is untouched");
            NativeEvents();
            var independent=typeof(AnimationEventClipIO).Assembly.GetReferencedAssemblies().All(item=>!item.Name.StartsWith("CharacterAnimationExport",StringComparison.Ordinal));
            Check(independent,"event tool assembly has no exporter/importer dependency");
            var result=new {success=true,unity=Application.unityVersion,passed=checks.Count,checks=checks.ToArray(),reconciles=DirectAnimationImporter.ReconcileCount};
            File.WriteAllText(Path.Combine(Project,"result.json"),JsonUtility.ToJson(new Result{success=true,unity=Application.unityVersion,passed=checks.Count,checks=checks.ToArray()},true));
            Debug.Log("DIRECT_SMOKE_PASS "+checks.Count);EditorApplication.Exit(0);
        }
        catch(Exception error){File.WriteAllText(Path.Combine(Project,"result.json"),JsonUtility.ToJson(new Result{success=false,unity=Application.unityVersion,passed=checks.Count,checks=checks.ToArray(),error=error.ToString()},true));Debug.LogException(error);EditorApplication.Exit(1);}
    }
    static void NativeEvents()
    {
        var clip=new AnimationClip{frameRate=30};clip.SetCurve("",typeof(Transform),"m_LocalPosition.x",AnimationCurve.Linear(0,0,1,1));AssetDatabase.CreateAsset(clip,Root+"/Native.anim");
        var item=new AnimationEvent{time=0.25f,functionName="NativeCue",stringParameter="text",intParameter=7,floatParameter=0.75f,messageOptions=SendMessageOptions.DontRequireReceiver};
        AnimationEventClipIO.Save(clip,new[]{item},AnimationEventClipIO.Snapshot(clip));SameEvents(new[]{item},AnimationUtility.GetAnimationEvents(clip),"standalone native event editor");
        var path=Root+"/Unrelated.fbx";var importer=(ModelImporter)AssetImporter.GetAtPath(path);var original=importer.defaultClipAnimations.Single();
        importer.clipAnimations=new[]{new ModelImporterClipAnimation{name="Part A",takeName=original.takeName,firstFrame=original.firstFrame,lastFrame=original.firstFrame+5},new ModelImporterClipAnimation{name="Part B",takeName=original.takeName,firstFrame=original.firstFrame+6,lastFrame=original.lastFrame,loopTime=true,events=new[]{new AnimationEvent{time=0.4f,functionName="OtherTake"}}}};
        importer.SaveAndReimport();var selected=AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Single(value=>value.name=="Part A");
        var other=importer.clipAnimations.Single(value=>value.name=="Part B");var otherRaw=other.events.Single().time;
        var added=new AnimationEvent{time=selected.length*0.3f,functionName="SelectedTake",intParameter=19};
        AnimationEventClipIO.Save(selected,new[]{added},AnimationEventClipIO.Snapshot(selected));
        selected=AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Single(value=>value.name=="Part A");SameEvents(new[]{added},AnimationUtility.GetAnimationEvents(selected),"independent unowned multi-take FBX");
        other=((ModelImporter)AssetImporter.GetAtPath(path)).clipAnimations.Single(value=>value.name=="Part B");Check(other.loopTime&&other.events.Single().time==otherRaw&&other.events.Single().functionName=="OtherTake","other FBX take events/settings unchanged");
    }
    [Serializable]sealed class Result{public bool success;public string unity,error;public int passed;public string[]checks;}
}
