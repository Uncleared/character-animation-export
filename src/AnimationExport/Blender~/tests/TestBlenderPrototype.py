import bpy
import importlib
import json
from pathlib import Path
import sys
import shutil

BASE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(BASE))
import character_animation_export as addon
from character_animation_export import core

OUT = Path(__file__).resolve().parents[3] / 'prototype-test-output'
assert OUT.resolve().parent == Path(__file__).resolve().parents[3] and OUT.name == 'prototype-test-output'
if OUT.exists():
    shutil.rmtree(OUT)
OUT.mkdir(exist_ok=True)
addon.register()
checks = []


def check(condition, label):
    if not condition:
        raise AssertionError(label)
    checks.append(label)


def push(obj, name, start, end, path, axis=0, amount=1.0):
    obj.animation_data_create()
    obj.animation_data.action = None
    for frame, value in ((start, 0.0), ((start + end) // 2, amount), (end, 0.0)):
        if path == 'location':
            obj.location[axis] = value
        else:
            obj.rotation_euler[axis] = value
        obj.keyframe_insert(path, index=axis, frame=frame)
    action = obj.animation_data.action
    action.name = name
    action.use_fake_user = True
    obj.animation_data.action = None
    return action


def build(name, scale):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.fps = 30
    bpy.ops.object.armature_add()
    rig = bpy.context.object
    rig.name = name
    rig.data.bones[0].name = 'MotionRoot'
    rig.scale = (scale,) * 3
    first = push(rig, 'Idle A', 1, 12, 'location', 0, 0.2)
    second = push(rig, 'Use Item', 21, 32, 'rotation_euler', 2, 0.3)
    track = rig.animation_data.nla_tracks.new()
    track.name = 'Character Clips'
    strip = track.strips.new('Idle A', 1, first)
    track.strips.new('Use Item', 21, second)
    bpy.ops.mesh.primitive_cube_add(size=0.1)
    prop = bpy.context.object
    prop.name = 'Portable Object'
    prop.parent = rig
    bpy.ops.mesh.primitive_cube_add(size=0.05)
    lid = bpy.context.object
    lid.name = 'Nested Moving Lid'
    lid.parent = prop
    lid_action = push(lid, 'Open Lid', 1, 12, 'rotation_euler', 1, 1.2)
    lid.animation_data.nla_tracks.new().strips.new('Open Lid', 1, lid_action)
    prop.shape_key_add(name='Basis')
    smile = prop.shape_key_add(name='Expression')
    smile.data[0].co.x += 0.1
    for frame, value in ((1, 0.0), (6, 1.0), (12, 0.0)):
        smile.value = value
        smile.keyframe_insert('value', frame=frame)
    scene.character_animation_export.rig = rig
    settings = addon.settings_for(scene)
    settings.output_folder = str(OUT / name)
    settings.track_index = '0'
    addon.refresh(settings)
    return rig, track, strip, settings, lid


for name, scale in [('FirstHero', 0.01), ('Robot_B', 1.0), ('DifferentRig', 2.5)]:
    rig, track, strip, settings, lid = build(name, scale)
    check(len(settings.clips) == 2, name + ': discovered two clips')
    ids = [r.clip_id for r in settings.clips]
    settings.clips[1].enabled = False
    bpy.context.scene.frame_start, bpy.context.scene.frame_end = 7, 70
    bpy.context.scene.frame_set(8, subframe=0.25)
    before = (7, 70, 8, 0.25, bpy.context.view_layer.objects.active.name)
    manifest = addon.export_package(bpy.context, settings)
    check(len([r for r in manifest['clips'] if r['exported']]) == 1, name + ': checked subset only')
    check(manifest['clips'][1]['state'] == 'present' and not manifest['clips'][1]['enabled'], name + ': excluded differs from removed')
    after = (bpy.context.scene.frame_start, bpy.context.scene.frame_end, bpy.context.scene.frame_current,
             bpy.context.scene.frame_subframe, bpy.context.view_layer.objects.active.name)
    check(before == after, name + ': scene frame/range/selection restored')
    check(abs(manifest['clips'][0]['duration'] - 11 / 30) < 1e-6, name + ': duration uses frame intervals')
    check(manifest['clips'][0]['takeName'], name + ': actual single take recorded')
    settings.clips[1].enabled = True
    manifest = addon.export_package(bpy.context, settings)
    source_file = OUT / (name + '.blend')
    bpy.ops.wm.save_as_mainfile(filepath=str(source_file))
    package = Path(settings.output_folder) / 'animation-export.json'
    saved_manifest = package.read_bytes()
    hashes = [core.digest(Path(settings.output_folder) / row['file']) for row in manifest['clips']]
    steps = addon.export_package_steps(bpy.context, settings)
    next(steps)
    steps.close()
    check(package.read_bytes() == saved_manifest, name + ': cancellation keeps old manifest')
    check(hashes == [core.digest(Path(settings.output_folder) / row['file']) for row in manifest['clips']], name + ': cancellation keeps old FBXs')
    check(not addon._busy, name + ': cancellation releases exporter state')
    strip.name = 'Renamed Idle'
    strip.frame_start += 40
    strip.frame_end += 40
    addon.refresh(settings)
    check(settings.clips[0].clip_id == ids[0] and settings.clips[0].source_name == strip.name, name + ': rename/move keeps identity')
    old_action = strip.action
    track.strips.remove(strip)
    addon.refresh(settings)
    check(settings.clips[0].state == 'MISSING' and settings.clips[1].clip_id == ids[1], name + ': deletion keeps other identity')
    strip = track.strips.new('Reused A', 40, old_action)
    track.strips.new('Reused B', 60, old_action)
    addon.refresh(settings)
    check(settings.clips[0].state == 'AMBIGUOUS', name + ': ambiguous reused action requires relink')
    try:
        addon.export_package(bpy.context, settings)
        raise AssertionError('Ambiguity must block')
    except ValueError as exc:
        check('ambiguous' in str(exc), name + ': ambiguous source never silently remapped')
    for relative in ('../bad.fbx', 'C:/bad.fbx', '/bad.fbx', 'clips\\bad.fbx'):
        try:
            core.safe_file(OUT, relative)
            raise AssertionError('Traversal accepted')
        except ValueError:
            pass
    check(True, name + ': unsafe package paths rejected')

rig, track, strip, settings, lid = build('ModesRig', 0.25)
settings.source_mode = 'ACTIONS'
addon.refresh(settings)
for record in settings.clips:
    record.enabled = record.source_name == 'Idle A'
old_action, old_nla = rig.animation_data.action, rig.animation_data.use_nla
manifest = addon.export_package(bpy.context, settings)
check(sum(row['exported'] for row in manifest['clips']) == 1, 'Actions mode: only selected action exported')
check(rig.animation_data.action == old_action and rig.animation_data.use_nla == old_nla, 'Actions mode: action and NLA state restored')
original_profile = settings.profile_id
original_ids = [record.clip_id for record in settings.clips]
other = rig.copy()
other.data = rig.data.copy()
other.name = 'Another Character'
bpy.context.scene.collection.objects.link(other)
bpy.context.scene.character_animation_export.rig = other
other_settings = addon.settings_for(bpy.context.scene)
addon.refresh(other_settings)
check(other_settings.profile_id != original_profile, 'Multiple characters: independent profile IDs')
bpy.context.scene.character_animation_export.rig = rig
settings = addon.settings_for(bpy.context.scene)
check(settings.profile_id == original_profile and [record.clip_id for record in settings.clips] == original_ids, 'Multiple characters: switching back keeps configuration and clip IDs')
fake_project = OUT / 'FakeUnityProject'
(fake_project / 'ProjectSettings').mkdir(parents=True)
try:
    core.export_root(fake_project / 'Assets' / 'Exports')
    raise AssertionError('Unity Assets destination accepted')
except ValueError:
    check(True, 'Unity Assets export destinations blocked')
root = Path(settings.output_folder)
old_manifest = (root / 'animation-export.json').read_bytes()
row = next(row for row in manifest['clips'] if row['exported'])
old_fbx_hash = core.digest(root / row['file'])
replacement = OUT / 'replacement.fbx'
replacement.write_bytes(b'publisher rollback test')
real_replace = core.os.replace
failed = [False]
def fail_manifest_once(source, destination):
    if Path(destination).name == 'animation-export.json' and not failed[0]:
        failed[0] = True
        raise OSError('Simulated interrupted publication')
    return real_replace(source, destination)
core.os.replace = fail_manifest_once
try:
    try:
        core.publish(root, manifest, {row['file']: replacement})
        raise AssertionError('Forced publication failure did not occur')
    except OSError:
        pass
finally:
    core.os.replace = real_replace
check((root / 'animation-export.json').read_bytes() == old_manifest, 'Publisher failure restores previous manifest')
check(core.digest(root / row['file']) == old_fbx_hash, 'Publisher failure restores previous FBX')

report = {'blender': bpy.app.version_string, 'passed': len(checks), 'checks': checks, 'output': str(OUT)}
(OUT / 'blender-test-report.json').write_text(json.dumps(report, indent=2))
print('PMIX_TEST_REPORT ' + json.dumps(report))
