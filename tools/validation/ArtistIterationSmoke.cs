using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CharacterAnimationExport.Direct;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class ArtistIterationSmoke
{
    static readonly List<string> checks=new List<string>();
    static string Project=>Path.GetDirectoryName(Application.dataPath);
    static void Check(bool value,string label){if(!value)throw new Exception(label);checks.Add(label);}
    static AnimationClip[] Clips(string path)=>AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(clip=>!clip.name.StartsWith("__preview__")).ToArray();
    static string Identity(AnimationClip clip){AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip,out string guid,out long id);return guid+":"+id;}
    static string Copy(string label,string root)
    {
        var from=Path.Combine(Project,"ArtistFixtures",label);
        foreach(var file in Directory.GetFiles(from,"*",SearchOption.AllDirectories).OrderBy(file=>file.EndsWith(".json")?0:1))
        {
            if(file.EndsWith(".meta"))continue;
            var target=root+"/"+file.Substring(from.Length+1).Replace('\\','/');
            if(File.Exists(target)&&File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(target)))continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(file,target,true);AssetDatabase.ImportAsset(target,ImportAssetOptions.ForceSynchronousImport);
        }
        var source=DirectAnimationMetadata.Sources(root).Single();
        var settings=source.Settings;
        if(settings==null){settings=ScriptableObject.CreateInstance<AnimationImportSettings>();settings.profileId=source.profile.profileId;settings.rig=CharacterRig.Generic;AssetDatabase.CreateAsset(settings,source.SettingsPath);AssetDatabase.SaveAssetIfDirty(settings);}
        DirectAnimationImporter.Reconcile(source.assetPath);return source.assetPath;
    }
    static void Seed(string path,UnityEngine.Object payload=null)
    {
        var importer=(ModelImporter)AssetImporter.GetAtPath(path);var definitions=importer.clipAnimations;
        foreach(var definition in definitions)
        {
            definition.events=new[]{new AnimationEvent{time=0.2f,functionName="ArtistCue",stringParameter=definition.name,objectReferenceParameter=payload}};
            definition.loopTime=true;definition.cycleOffset=0.23f;
            definition.curves=new[]{new ClipAnimationInfoCurve{name="ArtistCurve",curve=AnimationCurve.Linear(0,0.2f,1,0.8f)}};
        }
        importer.clipAnimations=definitions;importer.SaveAndReimport();
    }
    static void Cue(AnimationClip clip,float seconds,UnityEngine.Object payload=null)
    {
        var events=AnimationUtility.GetAnimationEvents(clip);
        Check(events.Length==1&&Math.Abs(events[0].time-seconds)<0.00003f&&events[0].functionName=="ArtistCue"&&events[0].objectReferenceParameter==payload,clip.name+" exact event seconds and saved object");
    }
    static void Finish(bool success,Exception error=null)
    {
        File.WriteAllText(Path.Combine(Project,"artist-result.json"),JsonUtility.ToJson(new Result{success=success,error=error?.ToString(),checks=checks.ToArray()},true));
        if(error!=null)Debug.LogException(error);EditorApplication.Exit(success?0:1);
    }
    public static void Run()
    {
        if(!File.Exists(Path.Combine(Project,"character-animation-export-validation.json")))throw new Exception("Use a disposable validation project.");
        try
        {
            if(AssetDatabase.IsValidFolder("Assets/Artist"))AssetDatabase.DeleteAsset("Assets/Artist");
            var path=Copy("Base","Assets/Artist/Identity");Seed(path);
            var before=Clips(path).ToDictionary(clip=>clip.name,Identity);
            path=Copy("RecoveredIdentity","Assets/Artist/Identity");
            foreach(var clip in Clips(path)){Check(Identity(clip)==before[clip.name],"unsaved Blender identity recovery preserves Unity ID "+clip.name);Cue(clip,clip.length*0.2f);}
            path=Copy("AlternateBase","Assets/Artist/Alternate");Seed(path);
            before=Clips(path).ToDictionary(clip=>clip.name,Identity);
            path=Copy("AlternateReplaced","Assets/Artist/Alternate");
            foreach(var clip in Clips(path)){Check(Identity(clip)==before[clip.name],"alternate source keeps existing Unity ID "+clip.name);Cue(clip,clip.length*0.2f);}
            path=Copy("FractionalBase","Assets/Artist/Fractional");Seed(path);
            var seconds=Clips(path).ToDictionary(clip=>clip.name,clip=>AnimationUtility.GetAnimationEvents(clip).Single().time);
            path=Copy("FractionalScaled","Assets/Artist/Fractional");
            foreach(var clip in Clips(path))Cue(clip,seconds[clip.name]);
            foreach(var row in DirectAnimationMetadata.Find(path).clips)Check(Math.Abs(Clips(path).Single(clip=>clip.name==row.name).length-row.duration)<0.00004f,"fractional NLA range imports without trimming "+row.name);
            var recoveryRoot="Assets/Artist/Recovery";
            path=Copy("RecoveryBase",recoveryRoot);
            var parent=new GameObject("Payload");var child=new GameObject("Child");child.transform.SetParent(parent.transform);child.AddComponent<BoxCollider>();
            var prefab=PrefabUtility.SaveAsPrefabAsset(parent,recoveryRoot+"/Payload.prefab");UnityEngine.Object.DestroyImmediate(parent);
            var payload=prefab.GetComponentInChildren<BoxCollider>();Seed(path,payload);
            var controller=AnimatorController.CreateAnimatorControllerAtPath(recoveryRoot+"/Reference.controller");
            foreach(var clip in Clips(path))controller.layers[0].stateMachine.AddState(clip.name).motion=clip;
            EditorUtility.SetDirty(controller);AssetDatabase.SaveAssetIfDirty(controller);
            var original=Clips(path).ToDictionary(clip=>clip.name,Identity);
            File.WriteAllText(Path.Combine(Project,"restart-identities.json"),JsonUtility.ToJson(new Identities{names=original.Keys.ToArray(),ids=original.Values.ToArray()},true));
            Copy("RecoveryRemoved",recoveryRoot);
            Check(Clips(path).Length==1,"retired clip and its native settings archived before restart");
            Finish(true);
        }
        catch(Exception error){Finish(false,error);}
    }
    public static void CompleteRestart()
    {
        try
        {
            var root="Assets/Artist/Recovery";var path=Copy("RecoveryRestored",root);
            var identities=JsonUtility.FromJson<Identities>(File.ReadAllText(Path.Combine(Project,"restart-identities.json")));
            var payload=AssetDatabase.LoadAssetAtPath<GameObject>(root+"/Payload.prefab").GetComponentInChildren<BoxCollider>();
            var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(root+"/Reference.controller");
            foreach(var clip in Clips(path))
            {
                Check(Identity(clip)==identities.ids[Array.IndexOf(identities.names,clip.name)],"restart restoration keeps GUID/local ID "+clip.name);
                Cue(clip,clip.length*0.2f,payload);
                Check(controller.layers[0].stateMachine.states.Single(state=>state.state.name==clip.name).state.motion==clip,"restart restoration repairs same-ID Animator reference "+clip.name);
                var definition=((ModelImporter)AssetImporter.GetAtPath(path)).clipAnimations.Single(item=>item.name==clip.name);
                Check(definition.loopTime&&Math.Abs(definition.cycleOffset-0.23f)<0.00001f&&definition.curves.Length==1&&Math.Abs(definition.curves[0].curve.Evaluate(0.5f)-0.5f)<0.00001f,"restart restores native loop and custom curve "+clip.name);
            }
            Copy("RecoveryRemoved",root);AssetDatabase.DeleteAsset(root+"/Payload.prefab");Copy("RecoveryRestored",root);
            Check(Clips(path).Length==2,"deleted payload does not block other clips or readdition");
            foreach(var clip in Clips(path))Cue(clip,clip.length*0.2f);
            var count=DirectAnimationImporter.ReconcileCount;Check(!DirectAnimationImporter.Reconcile(path)&&count==DirectAnimationImporter.ReconcileCount,"restored missing payload converges without repeated reimports");
            Finish(true);
        }
        catch(Exception error){Finish(false,error);}
    }
    [Serializable]sealed class Result{public bool success;public string error;public string[] checks;}
    [Serializable]sealed class Identities{public string[] names,ids;}
}
