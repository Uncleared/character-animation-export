using System;
using System.IO;
using System.Linq;
using CharacterAnimationEvents;
using UnityEditor;
using UnityEngine;
public static class StandaloneEventSmoke
{
    [Serializable]sealed class Result{public bool success,independent;public string error;public int events;}
    public static void Run()
    {
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), "character-animation-export-validation.json"))) throw new InvalidOperationException("Run only in a disposable validation project prepared by tools/prepare_unity_validation.py.");
        var result=new Result();
        try
        {
            result.independent=AppDomain.CurrentDomain.GetAssemblies().All(item=>!item.GetName().Name.StartsWith("CharacterAnimationExport",StringComparison.Ordinal));
            if(!result.independent)throw new Exception("Exporter/importer unexpectedly installed.");
            var clip=new AnimationClip();clip.SetCurve("",typeof(Transform),"m_LocalPosition.x",AnimationCurve.Linear(0,0,1,1));AssetDatabase.CreateAsset(clip,"Assets/Native.anim");
            var snapshot=AnimationEventClipIO.Snapshot(clip);AnimationEventClipIO.Save(clip,new[]{new AnimationEvent{time=0.3f,functionName="IndependentCue",intParameter=31}},snapshot);
            var events=AnimationUtility.GetAnimationEvents(clip);result.events=events.Length;
            if(events.Length!=1||Math.Abs(events[0].time-0.3f)>0.00001f||events[0].intParameter!=31)throw new Exception("Independent native event save failed.");
            result.success=true;
        }
        catch(Exception error){result.error=error.ToString();Debug.LogException(error);}
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath),"result.json"),JsonUtility.ToJson(result,true));EditorApplication.Exit(result.success?0:1);
    }
}
