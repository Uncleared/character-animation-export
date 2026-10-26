"""Native Blender regression/Unity fixtures. Run with --background --factory-startup --python.
Optional arguments after --: --output <new empty directory>.
"""
import argparse
import bpy
import json
from pathlib import Path
import shutil
import sys
import tempfile

BASE = Path(__file__).resolve().parents[1]
REPO = BASE.parents[2]
sys.path.insert(0, str(BASE))
import character_animation_export as addon
from character_animation_export import core, combined
from io_scene_fbx import export_fbx_bin as native

parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path)
options = parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
OUT = options.output or Path(tempfile.mkdtemp(prefix='CharacterCombined-'))
OUT.mkdir(parents=True, exist_ok=True)
if any(OUT.iterdir()): raise ValueError('Choose an empty output directory.')
checks = []
def check(value, label):
    if not value: raise AssertionError(label)
    checks.append(label)
def load():
    bpy.ops.wm.open_mainfile(filepath=str(REPO/'examples/ExampleCharacter.blend'))
    return bpy.context.scene, addon.settings_for(bpy.context.scene)
def hashes():
    return {str(p.relative_to(root)):core.digest(p) for p in root.rglob('*') if p.is_file()}
def snapshot(name):
    shutil.copytree(root, OUT/'Fixtures'/name, ignore=shutil.ignore_patterns('*.meta'))
def export(**kwargs):
    return addon.export_direct(bpy.context, profile, **kwargs)
def fails(call, message):
    try: call()
    except (ValueError, InterruptedError, PermissionError) as error:
        check(message.lower() in str(error).lower(), message)
    else: raise AssertionError('Expected rejection: '+message)
def shift_timeline(delta):
    actions = set()
    for datablock in [*bpy.data.objects, *bpy.data.shape_keys]:
        animation = datablock.animation_data
        if animation is None: continue
        for track in animation.nla_tracks:
            # Move the leading edge first, as Blender prevents strips from overlapping.
            for strip in sorted(track.strips, key=lambda strip:strip.frame_start, reverse=delta>0):
                start, end = strip.frame_start, strip.frame_end
                strip.frame_start_ui += delta
                if abs(strip.frame_start-start-delta)>1e-5 or abs(strip.frame_end-end-delta)>1e-5:
                    raise AssertionError('The test did not move the entire NLA selection intact.')
        if animation.action is not None: actions.add(animation.action)
    for action in actions:
        for slot in action.slots:
            for curve in addon._channels(action, slot):
                for point in curve.keyframe_points:
                    point.co.x += delta; point.handle_left.x += delta; point.handle_right.x += delta
    addon.refresh(profile)

addon.register()
scene, profile = load()
check(profile.export_layout == 'SEPARATE', 'saved v0.2.3 profile stays Separate')
(OUT/'Project/ProjectSettings').mkdir(parents=True)
root = OUT/'Project/Assets/Animations'
profile.output_folder = str(root); profile.delivery = 'DIRECT'; profile.source_file = ''
addon.refresh(profile)
separate = export()
snapshot('Separate')
profile.output_folder = str(OUT/'CombinedProject/Assets/Animations')
(OUT/'CombinedProject/ProjectSettings').mkdir(parents=True)
root = Path(profile.output_folder)
profile.export_layout = 'COMBINED'; profile.combined_name = 'ExampleAnimations'
before = (scene.frame_current, scene.frame_subframe, scene.frame_start, scene.frame_end,
          bpy.context.view_layer.objects.active, tuple(bpy.context.selected_objects), len(bpy.data.meshes))
manifest = export()
check(manifest['schemaVersion'] == 3 and len(list(root.glob('*.fbx'))) == 1, 'one combined FBX with schema3')
check(len(manifest['clips']) == 2 and len(set(row['file'] for row in manifest['clips'])) == 1, 'two clips share file ownership')
check(set(combined.stack_names(root/'ExampleAnimations.fbx')) == {'CAE_'+row['id'] for row in manifest['clips']}, 'stable identity-derived takes')
check(before == (scene.frame_current, scene.frame_subframe, scene.frame_start, scene.frame_end,
          bpy.context.view_layer.objects.active, tuple(bpy.context.selected_objects), len(bpy.data.meshes)), 'scene and temporary meshes restored')
snapshot('Base')
identities = [row.clip_id for row in profile.clips]
first, second = profile.clips[0], profile.clips[1]
def originals():
    return tuple(next(row for row in profile.clips if row.clip_id == identity) for identity in identities)
inventory = addon.sources(profile)
source_first = next(s for s in inventory if s['action'] == first.action and s['name'] == first.source_name)
strip, track = source_first['strip'], profile.rig.animation_data.nla_tracks[0]
old_end = strip.frame_end
meta = root/'ExampleAnimations.fbx.meta'; meta.write_bytes(b'Unity metadata sentinel')
export()
check(meta.read_bytes() == b'Unity metadata sentinel', 'existing Unity metadata untouched')
meta.unlink(); snapshot('Same')
strip.frame_end = old_end+10; export(); snapshot('Extended')
strip.frame_end = old_end
shift_timeline(100); moved = export(); snapshot('Moved')
check([row.clip_id for row in profile.clips] == identities, 'moving strips retains clip identities')
check(all(abs(a['duration']-b['duration']) < 1e-6 for a,b in zip(manifest['clips'], moved['clips'])), 'moving timeline keeps local clip duration')
shift_timeline(-100)
profile.clips.move(0,1); export(); snapshot('Reordered')
first, second = originals()
new_action = first.action.copy()
new_strip = track.strips.new('NewClip', 200, new_action)
addon.refresh(profile); export(); snapshot('Added')
first, second = originals()
check(len(combined.stack_names(root/'ExampleAnimations.fbx')) == 3, 'new strip becomes a named take automatically')
first.enabled = False
before_hash = hashes()
fails(export, 'Confirm removing')
check(hashes() == before_hash, 'unconfirmed removal preserves every published byte')
export(confirm_removals={first.clip_id}); snapshot('Removed')
first.enabled = True; export(); snapshot('Restored')
check(first.clip_id == identities[0], 'rechecking restores the same identity')
saved_action, saved_name, saved_start, saved_end = strip.action, strip.name, strip.frame_start, strip.frame_end
track.strips.remove(strip); addon.refresh(profile)
fails(export, 'Confirm removing')
export(confirm_removals={first.clip_id}); snapshot('Deleted')
strip = track.strips.new(saved_name, int(saved_start), saved_action)
strip.frame_end = saved_end
addon.refresh(profile); export(); snapshot('Readded')
check(first.clip_id == identities[0] and first.state == 'READY', 'deleted/readded original strip relinks without changing identity')
second_strip = next(s['strip'] for s in addon.sources(profile) if s['action'] == second.action and s['name'] == second.source_name)
second_end = second_strip.frame_end
strip.frame_end = strip.frame_start+3; second_strip.frame_end = second_end+10
export(); snapshot('ShortAndOtherExtended')
strip.frame_end = saved_end; second_strip.frame_end = second_end; export()
before_hash = hashes(); original_hooks = (native.fbx_data_from_scene,native.fbx_animations)
steps = addon.export_package_steps(bpy.context, profile, direct=True)
next(steps)
check((native.fbx_data_from_scene,native.fbx_animations) == original_hooks, 'native hooks restored between modal yields')
steps.close()
check(hashes() == before_hash and not addon._busy and len(bpy.data.meshes) == before[-1], 'cancel between takes preserves output and cleans prepared meshes')
fails(lambda: export(cancel=lambda index:index == 1), 'cancelled')
check(hashes() == before_hash and (native.fbx_data_from_scene,native.fbx_animations) == original_hooks, 'callback cancellation restores hooks and files')
metadata = root/core.metadata_file(profile.profile_id)
old_metadata = metadata.read_bytes()
steps = addon.export_package_steps(bpy.context, profile, direct=True)
next(steps)
changed = json.loads(old_metadata); changed['generation'] += 1
metadata.write_text(json.dumps(changed))
try:
    fails(lambda:list(steps), 'changed during export')
finally:
    steps.close(); metadata.write_bytes(old_metadata)
check(hashes() == before_hash, 'concurrent publication cannot be overwritten')
bpy.ops.mesh.primitive_cube_add(size=0.02)
new_prop = bpy.context.object; new_prop.name = 'New Nested Prop'
new_prop.parent = next(obj for obj in scene.objects if obj.name == 'Nested Moving Lid')
new_prop.rotation_euler.y = 0; new_prop.keyframe_insert('rotation_euler',index=1,frame=first.start)
new_prop.rotation_euler.y = 1; new_prop.keyframe_insert('rotation_euler',index=1,frame=first.end)
export(); snapshot('NewChild')
old_metadata = metadata.read_bytes(); changed = json.loads(old_metadata); changed['generation'] += 1
first.enabled = False
removed, confirmed = addon._combined_plan(bpy.context, profile)
metadata.write_text(json.dumps(changed))
try:
    fails(lambda: export(confirm_removals={row['id'] for row in removed}, confirmed_publication=confirmed), 'changed after confirmation')
finally: metadata.write_bytes(old_metadata)
first.enabled = True
first.export_name = 'ExplicitNewName'
fails(export, 'Confirm removing')
old_identity = first.clip_id
export(confirm_removals={old_identity}); snapshot('Renamed')
check(first.clip_id != old_identity and first.published_name == 'ExplicitNewName', 'explicit rename gets a new identity after successful publication')
first.export_name = saved_name
fails(lambda: export(confirm_removals={first.clip_id}), 'belonged to a removed clip')
first.export_name = 'ExplicitNewName'
profile.combined_name = 'AnotherPack'
export(); snapshot('NewFilename')
check((root/'ExampleAnimations.fbx').is_file() and (root/'AnotherPack.fbx').is_file(), 'new combined filename preserves old output')
profile.export_layout = 'SEPARATE'; export(); snapshot('SwitchSeparate')
check((root/'AnotherPack.fbx').is_file(), 'layout switch preserves previous combined file')
profile.export_layout = 'COMBINED'; export()
check(not addon._busy, 'all synchronous export paths release busy state')

# Actions source with no previous animation_data must restore its original absence.
scene, profile = load()
profile.rig.animation_data_clear()
profile.source_mode='ACTIONS'; profile.export_layout='COMBINED'; profile.source_file=''
profile.output_folder=str(OUT/'ActionExports'); profile.combined_name='Actions'
addon.refresh(profile); export()
check(profile.rig.animation_data is None, 'Actions export restores absent animation_data')

# Inventory stress uses short generated strips, not duplicated production character assets.
scene, profile = load()
action = addon.sources(profile)[0]['action']
profile.rig.animation_data_clear(); animation = profile.rig.animation_data_create()
track = animation.nla_tracks.new(); track.name = 'Clips'
for index in range(200):
    strip = track.strips.new('Clip%03d'%index, index*10+1, action)
    strip.action_frame_end = strip.action_frame_start+2
    strip.frame_end = strip.frame_start+2
profile.clips.clear(); profile.source_mode='NLA'; profile.track_index='ALL'
profile.export_layout='COMBINED'; profile.output_folder=str(OUT/'Scale'); profile.combined_name='Scale200'; profile.source_file=''
addon.refresh(profile); scale = export()
check(len(scale['clips']) == 200 and len(combined.stack_names(OUT/'Scale/Scale200.fbx')) == 200, '200 selected strips export as 200 stable named takes')
snapshot_source = OUT/'Fixtures/Scale200'; shutil.copytree(OUT/'Scale', snapshot_source)
selector = scene.character_animation_export
selector.profiles.clear()
new_profile = addon.settings_for(scene)
check(new_profile.export_layout == 'COMBINED', 'genuinely new profiles default Combined')
report = dict(success=True,blender=bpy.app.version_string,checks=checks,fixtures=str(OUT/'Fixtures'),
    scaleBytes=(OUT/'Scale/Scale200.fbx').stat().st_size,sourceBlendSaved=False)
(OUT/'result.json').write_text(json.dumps(report,indent=2))
print('COMBINED_EXPORT_RESULT',json.dumps(report))
