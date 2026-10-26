"""Run with Blender --background --factory-startup --python this_file."""
import bpy
import json
from pathlib import Path
import shutil
import sys
import tempfile

BASE=Path(__file__).resolve().parents[1]
REPO=BASE.parents[2]
sys.path.insert(0,str(BASE))
import character_animation_export as addon
from character_animation_export import core

OUT=Path(tempfile.mkdtemp(prefix='CharacterDirectExport-'))
addon.register()
bpy.ops.wm.open_mainfile(filepath=str(REPO/'examples/ExampleCharacter.blend'))
scene=bpy.context.scene
profile=addon.settings_for(scene)
root=OUT/'Fake Unity Project/Assets/Character Animations'
(OUT/'Fake Unity Project/ProjectSettings').mkdir(parents=True)
profile.output_folder=str(root)
profile.delivery='DIRECT'
profile.source_file=''
addon.refresh(profile)
checks=[]
def check(value,label):
    if not value: raise AssertionError(label)
    checks.append(label)

check(profile.bl_rna.properties['output_folder'].is_path_supports_blend_relative,'folder picker accepts blend-relative paths')
def snapshot(label):
    target=OUT/'Fixtures'/label
    shutil.copytree(root,target,ignore=shutil.ignore_patterns('*.meta'))
    return target
def hashes():
    return {str(path.relative_to(root)):core.digest(path) for path in root.rglob('*') if path.is_file()}

before=(scene.frame_current,scene.frame_start,scene.frame_end,bpy.context.view_layer.objects.active,tuple(bpy.context.selected_objects))
manifest=addon.export_direct(bpy.context,profile)
check(manifest['schemaVersion']==2,'direct metadata version')
check(len(list(root.glob('*.fbx')))==2,'friendly FBXs directly in Assets destination')
check(not (root/'clips').exists(),'no UUID clip subfolders')
check((root/core.metadata_file(profile.profile_id)).exists(),'automatic metadata publication')
check(core.staging_root(root).parent==OUT/'Fake Unity Project','staging outside Assets on same volume')
check(before==(scene.frame_current,scene.frame_start,scene.frame_end,bpy.context.view_layer.objects.active,tuple(bpy.context.selected_objects)),'all scene state restored')
check(not profile.show_setup,'advanced settings collapsed by default')
check(profile.source_file=='ExampleCharacter.blend','saved source identity')
original_ids=[row.clip_id for row in profile.clips]
original_names=[row.export_name for row in profile.clips]
base=snapshot('Base')
first=profile.clips[0]
first_file=root/(first.export_name+'.fbx')
meta=Path(str(first_file)+'.meta')
meta.write_bytes(b'native metadata sentinel')
addon.export_direct(bpy.context,profile)
check(meta.read_bytes()==b'native metadata sentinel','Blender never changes existing Unity meta bytes')
meta.unlink()
track=profile.rig.animation_data.nla_tracks[0]
strip=track.strips[0]
strip.name='Source renamed for animator'
addon.refresh(profile)
addon.export_direct(bpy.context,profile)
check([row.clip_id for row in profile.clips]==original_ids,'strip rename retains clip identities')
check([row.export_name for row in profile.clips]==original_names,'strip rename retains filenames and gameplay names')
for row in profile.clips: row.enabled=(row==first)
old_end=strip.frame_end
strip.frame_end=old_end+10
manifest=addon.export_direct(bpy.context,profile)
check(sum(row.get('exported',False) for row in manifest['clips'])==1,'only checked clips updated')
check(len(list(root.glob('*.fbx')))==2,'unchecked clips remain on disk')
check(abs(next(row for row in manifest['clips'] if row['exported'])['duration']-(strip.frame_end-strip.frame_start)/(scene.render.fps/scene.render.fps_base))<1e-6,'resized strip defines new range')
snapshot('Extended')

bpy.ops.mesh.primitive_cube_add(size=0.02)
child=bpy.context.object
child.name='New Nested Prop'
child.parent=next(obj for obj in scene.objects if obj.name=='Nested Moving Lid')
child.rotation_euler.y=0
child.keyframe_insert('rotation_euler',index=1,frame=strip.frame_start)
child.rotation_euler.y=1
child.keyframe_insert('rotation_euler',index=1,frame=strip.frame_end)
manifest=addon.export_direct(bpy.context,profile)
check(any('New Nested Prop' in path for row in manifest['clips'] if row.get('exported') for path in row['objectPaths']),'new nested props included recursively')
snapshot('NewChild')
strip.frame_end=strip.frame_start+3
addon.export_direct(bpy.context,profile)
snapshot('Short')
before_hashes=hashes()
try:
    addon.export_direct(bpy.context,profile,cancel=lambda _index: True)
    raise AssertionError('cancel accepted')
except InterruptedError: pass
check(hashes()==before_hashes,'cancellation preserves every published file')
check(not addon._busy,'cancellation releases busy state')
from types import SimpleNamespace
original_bpy=addon.bpy
timers=[]
wm=SimpleNamespace(event_timer_add=lambda *a,**kw: timers.append(object()) or timers[-1],modal_handler_add=lambda _op: None,event_timer_remove=lambda timer: timers.remove(timer))
operator_context=SimpleNamespace(scene=scene,window_manager=wm,window=None)
first_operator=SimpleNamespace(report=lambda *_:None)
second_operator=SimpleNamespace(report=lambda *_:None)
try:
    addon.bpy=SimpleNamespace(app=SimpleNamespace(background=False))
    started=addon.CAE_OT_export.execute(first_operator,operator_context)
    check(started=={'RUNNING_MODAL'} and addon._busy,'modal export reserves busy state before the first bake')
    check(addon.CAE_OT_export.execute(second_operator,operator_context)=={'CANCELLED'} and len(timers)==1,'second export cannot start before the first timer tick')
    check(addon.CAE_OT_export.modal(first_operator,operator_context,SimpleNamespace(type='ESC'))=={'CANCELLED'} and not addon._busy and not timers,'cancel before first bake releases operator and timer')
finally:
    addon.bpy=original_bpy
    addon._busy=False

foreign=root/'Foreign.fbx'
foreign.write_bytes(b'not owned by this exporter')
saved_name,saved_id,saved_published=first.export_name,first.clip_id,first.published_name
first.export_name='Foreign'
try:
    addon.export_direct(bpy.context,profile)
    raise AssertionError('colliding rename accepted')
except ValueError: pass
check((first.clip_id,first.published_name)==(saved_id,saved_published),'failed rename retains published identity')
first.export_name='CancelledRename'
try:
    addon.export_direct(bpy.context,profile,cancel=lambda _index: True)
    raise AssertionError('cancelled rename accepted')
except InterruptedError: pass
check((first.clip_id,first.published_name)==(saved_id,saved_published),'cancelled rename retains published identity')
first.export_name=saved_name
addon.export_direct(bpy.context,profile)
check(first.clip_id==saved_id and first.published_name==saved_published,'original filename remains usable after failed rename')
try:
    core.validate_direct_targets(root,profile.profile_id,[(first.clip_id,'Foreign')])
    raise AssertionError('foreign overwrite allowed')
except ValueError: pass
check(foreign.read_bytes()==b'not owned by this exporter','unknown FBX cannot be overwritten')
foreign.unlink()
try:
    core.validate_direct_targets(root,core.new_id(),[(first.clip_id,first.export_name)])
    raise AssertionError('cross-profile overwrite allowed')
except ValueError: pass
check(True,'cross-profile collision blocked')

real_replace=core.os.replace
count=[0]
def transient(source,destination):
    if str(destination).lower().endswith('.fbx') and count[0]<2:
        count[0]+=1
        raise PermissionError('simulated Unity sharing violation')
    return real_replace(source,destination)
core.os.replace=transient
try: addon.export_direct(bpy.context,profile)
finally: core.os.replace=real_replace
check(count[0]==2,'bounded retry succeeds after transient file locks')
before_hashes=hashes()
def locked(source,destination):
    if str(destination).lower().endswith('.fbx'): raise PermissionError('simulated locked FBX')
    return real_replace(source,destination)
core.os.replace=locked
try:
    try:
        addon.export_direct(bpy.context,profile)
        raise AssertionError('locked file accepted')
    except PermissionError: pass
finally: core.os.replace=real_replace
check(hashes()==before_hashes,'permanent file lock restores metadata and keeps old FBXs')

profile.source_file='Copied Variant.blend'
try:
    addon.export_direct(bpy.context,profile)
    raise AssertionError('copied profile silently reused')
except ValueError: pass
check(hashes()==before_hashes,'Save As identity conflict cannot overwrite outputs')
profile.source_file='ExampleCharacter.blend'
profile.output_folder=str(root)
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'SavedCharacter.blend'))
bpy.ops.wm.open_mainfile(filepath=str(OUT/'SavedCharacter.blend'))
profile=addon.settings_for(bpy.context.scene)
check(profile.output_folder==str(root) and [row.clip_id for row in profile.clips]==original_ids,'normal blend save/reopen retains folder and stable clip IDs')
report={'blender':bpy.app.version_string,'passed':len(checks),'checks':checks,'output':str(OUT),'fixtures':str(OUT/'Fixtures')}
(OUT/'direct-test-report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print('DIRECT_EXPORT_TEST_REPORT '+json.dumps(report))
