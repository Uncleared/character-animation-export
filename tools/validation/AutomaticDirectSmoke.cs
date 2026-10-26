using System;
using System.IO;
using System.Linq;
using CharacterAnimationExport.Direct;
using UnityEditor;
using UnityEngine;

public static class AutomaticDirectSmoke
{
    const string Root="Assets/Generated/Automatic";
    static int stage,quietFrames;
    static DateTime deadline;
    static string path,identity,lastMeta;
    [Serializable]sealed class Result{public bool success;public string error;public bool automaticFirstImport,automaticNewProp,stableReference,converged;}
    static readonly Result result=new Result();
    public static void Run()
    {
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), "character-animation-export-validation.json"))) throw new InvalidOperationException("Run only in a disposable validation project prepared by tools/prepare_unity_validation.py.");
        try
        {
            var project=Path.GetDirectoryName(Application.dataPath);
            var absolute=Path.GetFullPath(Path.Combine(project,Root));
            if(!absolute.StartsWith(Path.GetFullPath(Application.dataPath)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Invalid isolated test root");
            if(AssetDatabase.IsValidFolder(Root))AssetDatabase.DeleteAsset(Root);
            Copy("Base");
            var source=DirectAnimationMetadata.Sources(Root).First();path=source.assetPath;
            var settings=ScriptableObject.CreateInstance<AnimationImportSettings>();settings.profileId=source.profile.profileId;settings.characterName=source.profile.characterName;
            settings.rig=CharacterRig.Generic;
            AssetDatabase.CreateAsset(settings,source.SettingsPath);AssetDatabase.SaveAssetIfDirty(settings);
            deadline=DateTime.UtcNow.AddSeconds(60);EditorApplication.update+=Tick;
        }
        catch(Exception error){Finish(error);}
    }
    static void Copy(string name)
    {
        var source=Path.Combine(Path.GetDirectoryName(Application.dataPath),"ExternalFixtures",name);
        foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
        {var target=Path.Combine(Root,file.Substring(source.Length+1));Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(file,target,true);}
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        // No calls to Queue, ProcessPending or Reconcile: only real import callbacks run.
    }
    static void Tick()
    {
        try
        {
            if(DateTime.UtcNow>deadline)throw new Exception("Automatic import did not complete before the deadline.");
            if(EditorApplication.isUpdating||EditorApplication.isCompiling)return;
            var importer=AssetImporter.GetAtPath(path) as ModelImporter;
            var clip=AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(item=>!item.name.StartsWith("__preview__"));
            if(importer==null||clip==null||importer.clipAnimations.Length!=1)return;
            var definition=importer.clipAnimations.Single();
            if(!definition.lockRootRotation||!definition.lockRootHeightY||!definition.lockRootPositionXZ||!importer.userData.Contains("CharacterAnimationExport/2"))return;
            if(stage==0)
            {
                result.automaticFirstImport=true;AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip,out string guid,out long id);identity=guid+":"+id;
                stage=1;Copy("NewChild");return;
            }
            if(!AnimationUtility.GetCurveBindings(clip).Any(item=>item.path.Contains("New Nested Prop")&&item.propertyName.Contains("Rotation")))return;
            var mask=new AvatarMask();bool active=false;
            try{definition.ConfigureMaskFromClip(ref mask);active=Enumerable.Range(0,mask.transformCount).Any(i=>mask.GetTransformPath(i).Contains("New Nested Prop")&&mask.GetTransformActive(i));}
            finally{UnityEngine.Object.DestroyImmediate(mask);}
            if(!active)return;
            result.automaticNewProp=true;AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip,out string finalGuid,out long finalId);result.stableReference=identity==finalGuid+":"+finalId;
            var current=DirectAnimationMetadata.HashFile(path+".meta");if(current!=lastMeta){lastMeta=current;quietFrames=0;}else quietFrames++;
            if(quietFrames<12)return;result.converged=true;result.success=result.stableReference;Finish(null);
        }
        catch(Exception error){Finish(error);}
    }
    static void Finish(Exception error)
    {EditorApplication.update-=Tick;if(error!=null){result.error=error.ToString();Debug.LogException(error);}File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath),"automatic-result.json"),JsonUtility.ToJson(result,true));EditorApplication.Exit(result.success?0:1);}
}
