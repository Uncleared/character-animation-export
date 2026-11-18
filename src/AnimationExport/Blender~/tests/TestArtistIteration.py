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

load('NameChanges');profile.export_layout='SEPARATE';addon.refresh(profile)
first,second=profile.clips[0],profile.clips[1];first_name,second_name=first.export_name,second.export_name;wrong='Wrong Name'
def published():return json.loads((root/core.metadata_file(profile.profile_id)).read_text(encoding='utf-8'))
def row(clip_id):
    rows=[item for item in published()['clips'] if item['id']==clip_id]
    check(len(rows)==1,'clip id appears once in the published list');return rows[0]
def reuse_tokens():return {claim['token'] for claim in addon._reuse_plan(profile)}
def identities():return (first.clip_id,first.published_name,second.clip_id,second.published_name)
export();fixture('RenameBase');original,second_original=first.clip_id,second.clip_id
sentinel=root/(first_name+'.fbx.meta');sentinel.write_text('guid: keep\n',encoding='utf-8')
first.export_name=wrong;export();moved=first.clip_id;fixture('RenameMoved')
check(moved!=original and row(original)['state']=='removed','a first rename still keeps the old file for existing references')
first.export_name=first_name;first.enabled=False;export()
check(row(moved)['name']==wrong and row(moved)['file']==wrong+'.fbx','an unchecked rename does not relabel its published file')
first.enabled=True;second.enabled=False;before=hashes();state=identities()
try:export(cancel=lambda _index:True)
except InterruptedError:pass
else:raise AssertionError('Expected cancellation')
check(hashes()==before and identities()==state,'cancelled return keeps files and Blender identity')
owner_file=root/(first_name+'.fbx');owner_bytes=owner_file.read_bytes();owner_file.write_bytes(owner_bytes+b'x')
fails(export,'changed outside this exporter');owner_file.write_bytes(owner_bytes)
check(identities()==state,'an edited destination is refused without changing identity')
scene_name=scene.name;scene.name='Renamed Scene For Take'
fails(export,'different FBX take name');scene.name=scene_name
check(identities()==state and hashes()==before,'a changed take name is refused before publication')
result=export();fixture('RenameRestored')
check(first.clip_id==original and first.published_name==first_name,'returning to an earlier name reconnects the original clip automatically')
check(row(original)['state']=='present' and row(original)['exported'] and row(original)['sha256']==core.digest(owner_file),'restored row owns the replaced file')
check(row(moved)['state']=='removed' and (root/(wrong+'.fbx')).is_file(),'the abandoned name stays owned by its removed clip')
check(sentinel.read_text(encoding='utf-8')=='guid: keep\n','restoring keeps the Unity metadata file')
first.clip_id=moved;first.published_name=wrong;export()
check(first.clip_id==original and row(moved)['state']=='removed','a stale saved blend reconnects to the restored clip')
second.enabled=True;first.enabled=False;second.export_name=wrong;before=hashes();state=identities()
fails(export,'confirm reusing');check(hashes()==before and identities()==state,'unconfirmed reuse changes nothing')
tokens=reuse_tokens();check(len(tokens)==1,'reuse plan reports the one affected file')
strip=source(second)['strip'];strip.frame_end_ui-=1;addon.refresh(profile)
fails(lambda:export(confirm_takeovers=tokens),'confirm reusing');strip.frame_end_ui+=1;addon.refresh(profile)
check(hashes()==before,'consent does not cover a changed animation range')
check(bpy.ops.cae.export_checked('EXEC_DEFAULT',confirmed_takeovers=','.join(reuse_tokens()))=={'FINISHED'},'confirmed reuse exports through the operator')
fixture('RenameReused')
check(second.clip_id==moved and second.published_name==wrong and row(moved)['sourceBinding']['strip']==second.source_name,'confirmed reuse keeps the file identity with its new source')
check(row(second_original)['state']=='removed' and row(second_original)['file']==second_name+'.fbx','the reusing clip leaves its old file owned')
first.enabled=True;first.export_name=wrong;second.retired=True
fails(export,'belongs to another clip');second.retired=False
second.export_name=first_name;tokens=reuse_tokens();check(len(tokens)==2,'a same-export swap asks about both files')
export(confirm_takeovers=tokens)
check((first.clip_id,second.clip_id)==(moved,original) and len({item['id'] for item in published()['clips']})==len(published()['clips']),'two clips can exchange names without duplicate ids')
binding=dict(track='T',strip='S',action='A');old=dict(id='a'*32,file='N.fbx',state='removed',sha256='1',sourceBinding=binding);mine=dict(id='b'*32,file='M.fbx',state='present',sha256='2',sourceBinding=binding)
output=dict(id=old['id'],file='N.fbx',sha256='1',kind='separate')
check(core.classify_name_claim(dict(schemaVersion=3,clips=[old,mine],outputs=[output]),mine['id'],'N',binding,set(),set())['kind']=='restore','schema 3 separate history can be restored')
check(core.classify_name_claim(dict(schemaVersion=3,clips=[dict(old,file=''),mine],outputs=[output]),mine['id'],'N',binding,set(),set()) is None,'an output-only reservation is not taken over')
check(core.classify_name_claim(dict(schemaVersion=3,clips=[old,mine],outputs=[dict(output,kind='combined')]),mine['id'],'N',binding,set(),set()) is None,'a combined output is not taken over')
check(core.classify_name_claim(dict(schemaVersion=2,clips=[old,dict(mine,sourceBinding=dict(binding,strip='Other'))]),mine['id'],'N',binding,set(),set())['kind']=='reuse','a different published source must ask')
check(core.classify_name_claim(dict(schemaVersion=2,clips=[old]),'c'*32,'N',binding,set(),set())['kind']=='reuse','a clip with no published history must ask')

load('Recovery');export();fixture('RecoveryBase')
identity=profile.clips[0].clip_id;profile.clips[0].enabled=False
export(confirm_removals={identity});fixture('RecoveryRemoved')
profile.clips[0].enabled=True;export();fixture('RecoveryRestored')
report=dict(success=True,blender=bpy.app.version_string,checks=checks,fixtures=str(OUT/'Fixtures'))
(OUT/'result.json').write_text(json.dumps(report,indent=2))
print('ARTIST_ITERATION_RESULT',json.dumps(report))
