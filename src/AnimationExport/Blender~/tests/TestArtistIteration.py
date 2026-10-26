"""Focused artist-iteration regressions. Run in native Blender with -- --output <empty folder>."""
import argparse
import bpy
import json
from pathlib import Path
import shutil
import sys

BASE=Path(__file__).resolve().parents[1]
REPO=BASE.parents[2]
sys.path.insert(0,str(BASE))
import character_animation_export as addon
from character_animation_export import core, combined
from io_scene_fbx.fbx_utils import FBX_KTIME

parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True)
OUT=parser.parse_args(sys.argv[sys.argv.index('--')+1:]).output
OUT.mkdir(parents=True,exist_ok=True)
if any(OUT.iterdir()):raise ValueError('Choose an empty test output folder.')
checks=[]
def check(value,label):
    if not value:raise AssertionError(label)
    checks.append(label)
def fails(call,message):
    try:call()
    except ValueError as error:check(message.lower() in str(error).lower(),message)
    else:raise AssertionError('Expected rejection: '+message)
def load(label):
    global scene,profile,root
    bpy.ops.wm.open_mainfile(filepath=str(REPO/'examples/ExampleCharacter.blend'))
    scene=bpy.context.scene;profile=addon.settings_for(scene)
    profile.export_layout='COMBINED';profile.combined_name='ExampleAnimations';profile.track_index='ALL'
    profile.source_file='';root=OUT/label;profile.output_folder=str(root)
    addon.refresh(profile)
def export(**kwargs):return addon.export_direct(bpy.context,profile,**kwargs)
def hashes():return {str(path.relative_to(root)):core.digest(path) for path in root.rglob('*') if path.is_file()}
def fixture(label):shutil.copytree(root,OUT/'Fixtures'/label)
def source(record):return next(row for row in addon.sources(profile) if row['action']==record.action and row['track']==record.track_name and row['name']==record.source_name)

addon.register()
load('Identity');export();fixture('Base')
first=profile.clips[0];identity=first.clip_id
first.clip_id=core.new_id();first.published_name=''
export();fixture('RecoveredIdentity')
check(first.clip_id==identity,'unsaved clip identity recovers from unique published source binding')
first.export_name='RenamedIdle';export(confirm_removals={identity})
new_identity=first.clip_id
first.clip_id=identity;first.published_name='Idle A'
export();fixture('RecoveredRename')
check(first.clip_id==new_identity and first.published_name=='RenamedIdle','undone export state reconnects to the published rename without another retirement')
published_profile=profile.profile_id
for record in profile.clips:record.clip_id=core.new_id();record.published_name=''
profile.profile_id=core.new_id();profile.combined_name='LostSetup'
fails(export,'Continue Existing Export')
check(bpy.ops.cae.source_identity(new_profile=False)=={'FINISHED'},'one-click continue existing export executes')
check(profile.profile_id==published_profile and profile.combined_name=='ExampleAnimations','lost profile setup recovers the existing output identity and file name')
export()

load('Alternate');export();fixture('AlternateBase')
old_id=profile.clips[0].clip_id;old_name=profile.clips[0].export_name
original=source(profile.clips[0]);track=profile.rig.animation_data.nla_tracks.new();track.name='Alternate'
strip=track.strips.new('Alternate Take',int(original['start'])+200,original['action'].copy());strip.extrapolation='NOTHING'
addon.refresh(profile)
original_record=next(record for record in profile.clips if record.clip_id==old_id)
alternate=next(record for record in profile.clips if record.source_name=='Alternate Take')
alternate_id=alternate.clip_id;alternate.export_name=old_name;original_record.enabled=False;original_record.retired=True
before=hashes()
fails(lambda:export(confirm_removals={old_id}),'belonged to a removed clip')
check(hashes()==before,'same-export name reuse cannot publish a Unity-rejected FBX')
alternate.export_name='Alternate Take';alternate.enabled=False;original_record.enabled=True;original_record.retired=False
index=next(i for i,record in enumerate(profile.clips) if record.clip_id==old_id)
candidate=next(i for i,row in enumerate(addon.sources(profile)) if row['name']=='Alternate Take')
addon._relink_choices=[(str(i),row['name'],'') for i,row in enumerate(addon.sources(profile))]
check(bpy.ops.cae.relink(index=index,source=str(candidate))=={'FINISHED'},'replace source accepts an unchecked alternate take')
result=export();fixture('AlternateReplaced')
check(next(record for record in profile.clips if record.clip_id==old_id).source_name=='Alternate Take','replacement retains the published clip identity')
check(any(row['id']==old_id and row.get('exported') for row in result['clips']),'alternate source exports without removing the published clip')

load('Matching');export()
first=profile.clips[0];identity=first.clip_id;row=source(first);strip=row['strip'];track=profile.rig.animation_data.nla_tracks[0]
strip.action=strip.action.copy();addon.refresh(profile)
check(profile.clips[0].clip_id==identity and profile.clips[0].state=='READY','single-user action replacement keeps clip identity automatically')
shared=track.strips.new('Shared Action',200,strip.action);shared.extrapolation='NOTHING'
addon.refresh(profile);track.name='Renamed Character Track';addon.refresh(profile)
check(all(record.state=='READY' for record in profile.clips),'renaming a track with shared actions remains unambiguous')
animation=profile.rig.animation_data
animation.action=strip.action;animation.action_slot=addon._compatible_slot(strip.action,profile.rig)
before=hashes();fails(export,'Push Down or unlink')
check(hashes()==before and animation.action==strip.action,'active action contamination is rejected without changing source or outputs')
animation.action=None
layer=animation.nla_tracks.new();layer.name='Layered Variant'
overlay=layer.strips.new('Variant',int(strip.frame_start),strip.action);overlay.extrapolation='NOTHING'
addon.refresh(profile)
next(record for record in profile.clips if record.track_name==layer.name).enabled=False
result=export()
check(any('overlaps' in warning and 'Layered Variant' in warning for warning in result['warnings']),'unchecked overlapping track produces a visible layering warning')
layer.mute=True

load('Fractional');export();fixture('FractionalBase')
row=source(profile.clips[0]);strip=row['strip'];strip.frame_start_ui+=0.25;strip.scale*=1.15
addon.refresh(profile)
result=export();fixture('FractionalScaled')
record=next(item for item in result['clips'] if item.get('exported') and item['name']==profile.clips[0].export_name)
check(abs(record['startFrame']-strip.frame_start)<1e-5 and abs(record['endFrame']-strip.frame_end)<1e-5,'scaled fractional NLA range exported without manual trimming')
check(len(combined.stack_names(root/'ExampleAnimations.fbx'))==2,'fractional range retains exact take inventory')
from io_scene_fbx import parse_fbx
tree,_version=parse_fbx.parse(str(root/'ExampleAnimations.fbx'))
objects=next(element for element in tree.elems if element.id==b'Objects')
last_tick=max(int(max(child.props[0])) for element in objects.elems if element.id==b'AnimationCurve' for child in element.elems if child.id==b'KeyTime')
expected_tick=int(max(row['duration'] for row in result['clips'] if row.get('exported'))*FBX_KTIME)
check(abs(last_tick-expected_tick)<=2,'fractional range bakes its exact final pose instead of holding an earlier sample')

load('SwitchRename');export()
first=profile.clips[0];first.export_name='RenamedDuringSwitch';profile.export_layout='SEPARATE'
result=export()
check(all(not row.get('file','').endswith('ExampleAnimations.fbx') for row in result['clips']),'combined-to-separate rename leaves no dangling managed combined rows')
check((root/'ExampleAnimations.fbx').is_file(),'combined-to-separate rename preserves the previous combined output')
load('Recovery');export();fixture('RecoveryBase')
identity=profile.clips[0].clip_id;profile.clips[0].enabled=False
export(confirm_removals={identity});fixture('RecoveryRemoved')
profile.clips[0].enabled=True;export();fixture('RecoveryRestored')
report=dict(success=True,blender=bpy.app.version_string,checks=checks,fixtures=str(OUT/'Fixtures'))
(OUT/'result.json').write_text(json.dumps(report,indent=2))
print('ARTIST_ITERATION_RESULT',json.dumps(report))
