"""Native add-on regression. Blender --background --factory-startup --python <file> -- --output <new folder>."""
import argparse
import bpy
import json
from pathlib import Path
import shutil
import sys
import tempfile
from unittest.mock import patch

BASE = Path(__file__).resolve().parents[1]
REPO = BASE.parents[2]
sys.path.insert(0, str(BASE))
import character_animation_export as addon
from character_animation_export import core, lightweight
from io_scene_fbx import parse_fbx, encode_bin

parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path)
args = parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
OUT = args.output or Path(tempfile.mkdtemp(prefix='CharacterLightweight-'))
OUT.mkdir(parents=True, exist_ok=True)
if any(OUT.iterdir()):
    raise ValueError('Choose an empty output folder.')
checks = []
def check(value, label):
    if not value:
        raise AssertionError(label)
    checks.append(label)
def hashes():
    return {p.relative_to(root).as_posix(): core.digest(p) for p in root.rglob('*') if p.is_file()}
def state():
    return (scene.frame_current, scene.frame_subframe, scene.frame_start, scene.frame_end,
            tuple(bpy.context.selected_objects), bpy.context.view_layer.objects.active,
            len(bpy.data.meshes), len(bpy.data.actions), profile.rig.animation_data.action)
def snapshot(label):
    shutil.copytree(root, OUT/'Fixtures'/label, ignore=shutil.ignore_patterns('*.meta'))
def export():
    return addon.export_direct(bpy.context, profile)
def signatures(folder):
    return {path.name: lightweight.animation_signature(parse_fbx.parse(str(path))[0]) for path in folder.glob('*.fbx')}

addon.register()
bpy.ops.wm.open_mainfile(filepath=str(REPO/'examples/ExampleCharacter.blend'))
scene = bpy.context.scene
profile = addon.settings_for(scene)
profile.export_layout = 'SEPARATE'; profile.delivery = 'DIRECT'; profile.source_file = ''
root = OUT/'Project/Assets/Animations'
(OUT/'Project/ProjectSettings').mkdir(parents=True)
profile.output_folder = str(root)
addon.refresh(profile)
check(not profile.lightweight, 'existing profile defaults to Full')
before = state()
full = export(); snapshot('Full')
identities = [(row.clip_id, row.export_name) for row in profile.clips]
check(state() == before, 'Full restores scene and artist datablocks')
for path in root.glob('*.fbx'):
    parsed, version = parse_fbx.parse(str(path))
    target = OUT/(path.stem+'.roundtrip.fbx')
    encode_bin.write(str(target), lightweight._copy(parsed, encode_bin), version)
    reparsed, _ = parse_fbx.parse(str(target))
    check(lightweight.fingerprint(parsed.elems) == lightweight.fingerprint(reparsed.elems), 'native codec round trip: '+path.stem)
    (root/(path.name+'.meta')).write_bytes(b'Unity metadata sentinel')
full_signatures = signatures(root)
profile.lightweight = True
lite = export(); snapshot('Lightweight')
check(signatures(root) == full_signatures, 'Full and Lightweight canonical animation and targets match exactly')
check(state() == before, 'Lightweight restores scene and artist datablocks')
check(identities == [(row.clip_id, row.export_name) for row in profile.clips], 'Lightweight retains clip IDs and filenames')
check(all(row.get('geometry') == 'lightweight' for row in lite['clips'] if row.get('exported')), 'per-clip Lightweight metadata')
check(all(path.read_bytes() == b'Unity metadata sentinel' for path in root.glob('*.meta')), 'Full to Lightweight preserves Unity metadata')
measurements = []
for path in root.glob('*.fbx'):
    original = OUT/'Fixtures/Full'/path.name
    a, _ = parse_fbx.parse(str(original)); b, _ = parse_fbx.parse(str(path))
    check(lightweight._protected_signature(a) == lightweight._protected_signature(b), 'all hierarchy, bind and root records preserved: '+path.stem)
    check(path.stat().st_size <= original.stat().st_size, 'Lightweight never increases source FBX size: '+path.stem)
    measurements.append(dict(name=path.stem, full=original.stat().st_size, lightweight=path.stat().st_size))
check(any(row['full'] > row['lightweight'] for row in measurements), 'actual geometry reduction saves bytes')
first, second = profile.clips[0], profile.clips[1]
second.enabled = False
unchanged = core.digest(root/(second.export_name+'.fbx'))
profile.lightweight = False
mixed = export(); snapshot('Mixed')
check(core.digest(root/(second.export_name+'.fbx')) == unchanged, 'unchecked Lightweight file stays byte-identical')
check(next(row for row in mixed['clips'] if row['id'] == second.clip_id)['geometry'] == 'lightweight', 'unchecked representation metadata is retained')
check(addon._geometry_hint(profile) == '1 file remains Lightweight.', 'brief mixed-mode hint describes remaining file')
second.enabled = True
restored = export(); snapshot('RestoredFull')
check(signatures(root) == full_signatures, 'returning to Full restores identical animation')
check(all(row.get('geometry') == 'full' for row in restored['clips'] if row.get('exported')), 'Full restore metadata')
check(not addon._geometry_hint(profile), 'no extra hint for uniformly selected mode')
profile.lightweight = True
before_files = hashes()
try:
    with patch.object(lightweight, 'reduce_file', side_effect=ValueError('simulated carrier validation failure')):
        export()
    raise AssertionError('invalid staged carrier was accepted')
except ValueError as error:
    check('simulated' in str(error), 'carrier failure propagated')
check(hashes() == before_files and state() == before and not addon._busy, 'failed reducer preserves publication and scene state')
try:
    addon.export_direct(bpy.context, profile, cancel=lambda index: index == 1)
    raise AssertionError('cancel after first reduced clip accepted')
except InterruptedError:
    pass
check(hashes() == before_files and state() == before and not addon._busy, 'cancel after first bake/reduction publishes nothing')
track = profile.rig.animation_data.nla_tracks[0]
track.strips[0].name += ' renamed source'
addon.refresh(profile)
check(identities == [(row.clip_id, row.export_name) for row in profile.clips], 'source rename retains identities across modes')
export()
saved = OUT/'SavedCharacter.blend'
bpy.ops.wm.save_as_mainfile(filepath=str(saved))
bpy.ops.wm.open_mainfile(filepath=str(saved))
profile = addon.settings_for(bpy.context.scene)
check(profile.lightweight and profile.output_folder == str(root), 'Lightweight checkbox and folder survive save/reopen')
check(identities == [(row.clip_id, row.export_name) for row in profile.clips], 'clip identities survive save/reopen')

def rig_variant(label, face=True, multi=False, negative=False, shared=False, shape_prop=False):
    global scene, profile, root
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    bpy.ops.object.armature_add()
    rig = bpy.context.object; rig.name = 'TestRig'
    bpy.ops.object.mode_set(mode='EDIT')
    bones = rig.data.edit_bones
    bones[0].name = 'Root'
    hips = bones.new('Hips'); hips.head = (0, 0, 1); hips.tail = (0, 0, 2); hips.parent = bones['Root']
    head = bones.new('Head'); head.head = (0, 0, 2); head.tail = (0, 0, 3); head.parent = hips
    unused = bones.new('Unused'); unused.head = (0, 0, 3); unused.tail = (0, 0, 4); unused.parent = head
    bpy.ops.object.mode_set(mode='OBJECT')
    for frame, angle in ((1, 0), (16, 0.8)):
        rig.pose.bones['Hips'].rotation_mode = 'XYZ'
        rig.pose.bones['Hips'].rotation_euler.z = angle
        rig.pose.bones['Hips'].keyframe_insert('rotation_euler', frame=frame)
        rig.location.x = angle * 0.5; rig.keyframe_insert('location', frame=frame)
    action = rig.animation_data.action; action.name = 'Motion Idle'
    rig.animation_data.action = None
    track = rig.animation_data.nla_tracks.new(); track.name = 'Character Clips'
    track.strips.new('Motion Idle', 1, action)
    def mesh(name, skin=True, shapes=True):
        bpy.ops.mesh.primitive_uv_sphere_add(segments=12, ring_count=8)
        obj = bpy.context.object; obj.name = name; obj.parent = rig
        if skin:
            for name in ('Root', 'Hips', 'Head', 'Unused'):
                obj.vertex_groups.new(name=name)
            for vertex in obj.data.vertices:
                obj.vertex_groups['Hips' if vertex.index % 2 else 'Head'].add([vertex.index], 1, 'REPLACE')
            obj.modifiers.new('Skin', 'ARMATURE').object = rig
        if shapes:
            obj.shape_key_add(name='Basis')
            key = obj.shape_key_add(name='Éye_眼'); key.data[0].co.x += 0.3
            for frame, value in ((1, 0), (16, 1)):
                key.value = value; key.keyframe_insert('value', frame=frame)
            obj.shape_key_add(name='Static Empty')
        material = bpy.data.materials.new('Export Material'); obj.data.materials.append(material)
        return obj
    body = mesh('Body', shapes=face)
    if multi:
        other = mesh('Second Face')
        for group in other.vertex_groups:
            group.remove(list(range(len(other.data.vertices))))
        other.vertex_groups['Head'].add(list(range(len(other.data.vertices))), 1, 'REPLACE')
    if shape_prop:
        prop = mesh('Unskinned Facial Prop', skin=False)
        group = prop.vertex_groups.new(name='PartialShape')
        group.add(list(range(len(prop.data.vertices))), 0.25, 'REPLACE')
        prop.data.shape_keys.key_blocks['Éye_眼'].vertex_group = group.name
    if shared:
        copy = body.copy(); copy.data = body.data; scene.collection.objects.link(copy); copy.name = 'Shared Mesh Instance'
    if negative:
        rig.scale = (-1, 1.5, 0.75); body.scale = (1, 0.8, 1.2)
    bpy.context.view_layer.update()
    scene.character_animation_export.rig = rig
    profile = addon.settings_for(scene); profile.export_layout = 'SEPARATE'; profile.source_mode = 'NLA'
    root = OUT/'Variants'/label/'Working'; profile.output_folder = str(root)
    addon.refresh(profile)
    profile.lightweight = False; export()
    shutil.copytree(root, OUT/'Variants'/label/'Full')
    originals = signatures(root)
    profile.lightweight = True; export()
    shutil.copytree(root, OUT/'Variants'/label/'Lightweight')
    check(originals == signatures(root), label+': identical canonical animation on actual export')
    for path in root.glob('*.fbx'):
        a, _ = parse_fbx.parse(str(OUT/'Variants'/label/'Full'/path.name)); b, _ = parse_fbx.parse(str(path))
        check(lightweight._protected_signature(a) == lightweight._protected_signature(b), label+': identical bind/root records')
        records, _ = lightweight._records(a)
        clusters = [item for item in records.values() if item.id == b'Deformer' and item.props[2] == b'Cluster']
        check(any(not lightweight._active_cluster(item) for item in clusters), label+': fixture includes unused clusters')
    return profile

rig_variant('NoFace', face=False)
rig_variant('MultiFace', multi=True)
rig_variant('ShapeProp', shape_prop=True)
rig_variant('SharedNegative', negative=True, shared=True)
profile.source_mode = 'ACTIONS'; addon.refresh(profile)
root = OUT/'Variants/Actions/Working'; profile.output_folder = str(root)
before_actions = state()
profile.lightweight = False; export(); shutil.copytree(root, OUT/'Variants/Actions/Full')
action_signatures = signatures(root)
profile.lightweight = True; export(); shutil.copytree(root, OUT/'Variants/Actions/Lightweight')
check(action_signatures == signatures(root), 'chosen Actions export retains canonical animation with All Actions/NLA export flags off')
check(state() == before_actions, 'Actions Lightweight export restores active rig action, NLA, selection and scene range')
result = dict(success=True, blender=bpy.app.version_string, passed=len(checks), checks=checks,
              measurements=measurements, output=str(OUT))
(OUT/'result.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
print('LIGHTWEIGHT_EXPORT_RESULT '+json.dumps(result), flush=True)
