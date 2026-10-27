"""Blender background benchmark; never saves or modifies the source .blend on disk.

Open a character blend with Blender, then --python this_file -- --output <new folder>
--rig <object name> [--selection <JSON array of source strip names>] [--count 200].
Generated NLA strips reuse authored source actions with varied crop lengths;
they are actual separate exports, not 200 independently authored animations.
"""
import argparse
import bpy
import hashlib
import json
import math
from pathlib import Path
import shutil
import statistics
import sys
import time

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO/'src/AnimationExport/Blender~'))
import character_animation_export as addon
from character_animation_export import core, lightweight
from io_scene_fbx import parse_fbx

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--output', type=Path, required=True)
p.add_argument('--rig', required=True)
p.add_argument('--selection', type=Path)
p.add_argument('--count', type=int, default=200)
args = p.parse_args(sys.argv[sys.argv.index('--')+1:])
out = args.output
out.mkdir(parents=True, exist_ok=True)
if any(out.iterdir()) or not 1 <= args.count <= 200:
    raise ValueError('Use an empty output folder and 1..200 clips.')
source = Path(bpy.data.filepath)
source_hash = core.digest(source)
addon.register()
scene = bpy.context.scene
scene.character_animation_export.rig = bpy.data.objects[args.rig]
profile = addon.settings_for(scene)
profile.delivery = 'DIRECT'; profile.export_layout = 'SEPARATE'; profile.source_mode = 'NLA'
profile.track_index = 'ALL'; profile.source_file = ''
addon.refresh(profile)
selected = set(json.loads(args.selection.read_text(encoding='utf-8'))) if args.selection else None
for row in profile.clips:
    row.enabled = row.state == 'READY' and not row.retired and (selected is None or row.source_name in selected)
bases = [dict(action=row.action, track=row.track_name, source=row.source_name,
              start=row.start, end=row.end, name=row.export_name) for row in profile.clips if row.enabled]
for basis in bases:
    strip = next(source['strip'] for source in addon.sources(profile) if source['name'] == basis['source'] and source['track'] == basis['track'])
    if strip.repeat != 1 or strip.use_animated_time:
        raise ValueError('This benchmark generator needs non-repeated source strips without animated strip time.')
    basis.update(actionStart=strip.action_frame_start, actionEnd=strip.action_frame_end, reverse=strip.use_reverse)
if not bases or (selected is not None and len(bases) != len(selected)):
    raise ValueError('The exact source-clip selection is unavailable.')
results = []

def phase(label):
    folder = out/label/'Working'
    profile.output_folder = str(folder)
    profile.lightweight = False
    times = {}
    for mode in ('Full', 'Lightweight'):
        profile.lightweight = mode == 'Lightweight'
        start = time.perf_counter()
        for progress in addon.export_package_steps(bpy.context, profile, direct=True):
            print('BENCHMARK_PROGRESS', label, mode, progress, flush=True)
        times[mode] = time.perf_counter()-start
        shutil.copytree(folder, out/label/mode)
    full, lite = out/label/'Full', out/label/'Lightweight'
    rows = []
    manifest_path = next((full/'AnimationExportData').glob('*.animation-export.json'))
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    expected_count = len(bases) if label == 'Authored' else args.count
    if len([row for row in manifest['clips'] if row.get('exported')]) != expected_count:
        raise AssertionError('The benchmark did not export its expected number of clips: '+str(expected_count))
    for clip in sorted((row for row in manifest['clips'] if row.get('exported')), key=lambda row: row['name']):
        a, b = full/clip['file'], lite/clip['file']
        original, _ = parse_fbx.parse(str(a)); reduced, _ = parse_fbx.parse(str(b))
        if lightweight.animation_signature(original) != lightweight.animation_signature(reduced):
            raise AssertionError('Animation mismatch: '+clip['name'])
        if lightweight._protected_signature(original) != lightweight._protected_signature(reduced):
            raise AssertionError('Rig/bind mismatch: '+clip['name'])
        records, _ = lightweight._records(original)
        curves = [item for item in records.values() if item.id == b'AnimationCurve']
        keys = sum(len(lightweight.child(item, b'KeyTime').props[0]) for item in curves)
        rows.append(dict(name=clip['name'], fullBytes=a.stat().st_size, lightweightBytes=b.stat().st_size,
                         duration=clip['duration'], curves=len(curves), keys=keys,
                         canonicalAnimationIdentical=True, rigBindIdentical=True))
    cumulative = []
    full_support = sum(path.stat().st_size for path in full.rglob('*') if path.is_file() and path.suffix != '.fbx')
    lite_support = sum(path.stat().st_size for path in lite.rglob('*') if path.is_file() and path.suffix != '.fbx')
    for count in range(1, len(rows)+1):
        a = sum(row['fullBytes'] for row in rows[:count]); b = sum(row['lightweightBytes'] for row in rows[:count])
        cumulative.append(dict(clips=count, fullFbxBytes=a, lightweightFbxBytes=b,
                               savedBytes=a-b, savedPercent=100*(a-b)/a))
    # Support bytes are the actual complete publication sizes. Partial prefixes do not
    # invent metadata exports that were never performed.
    report = dict(label=label, actualExports=len(rows)*2, files=rows, cumulative=cumulative,
                  fullPublicationSupportBytes=full_support, lightweightPublicationSupportBytes=lite_support,
                  timingsSeconds=times,
                  savingsPercentDistribution=dict(zip(('min','median','max'),
                      (min(values := [100*(r['fullBytes']-r['lightweightBytes'])/r['fullBytes'] for r in rows]),
                       statistics.median(values), max(values)))))
    results.append(report)
    return report

phase('Authored')
rig = profile.rig
for track in rig.animation_data.nla_tracks:
    track.mute = True
generated = rig.animation_data.nla_tracks.new(); generated.name = 'Generated Benchmark Clips'
retimed = []
for datablock in [*bpy.data.objects, *bpy.data.shape_keys]:
    if datablock == rig or datablock.animation_data is None or datablock.animation_data.action is None:
        continue
    animation = datablock.animation_data
    action, slot = animation.action, animation.action_slot
    animation.action = None
    for track in animation.nla_tracks:
        track.mute = True
    track = animation.nla_tracks.new(); track.name = 'Generated Benchmark Retiming'
    retimed.append((track, action, slot))
cursor = int(max(basis['end'] for basis in bases))+100
generated_names = set()
for index in range(args.count):
    basis = bases[index % len(bases)]
    duration = max(1, math.floor((basis['end']-basis['start']) * (0.25+0.25*(index % 4))))
    name = f"Bench_{index+1:03d}_{basis['name']}"; generated_names.add(name)
    strip = generated.strips.new(name, cursor, basis['action'])
    strip.action_frame_start = basis['actionStart']
    strip.action_frame_end = basis['actionStart'] + (basis['actionEnd']-basis['actionStart']) * duration/(basis['end']-basis['start'])
    strip.frame_end = cursor+duration; strip.use_reverse = basis['reverse']; strip.extrapolation = 'NOTHING'
    for track, action, slot in retimed:
        prop = track.strips.new(name, cursor, action); prop.action_slot = slot
        prop.action_frame_start = basis['start']; prop.action_frame_end = basis['start']+duration
        prop.frame_end = cursor+duration; prop.extrapolation = 'NOTHING'
    cursor += duration+2
addon.refresh(profile)
for row in profile.clips:
    row.enabled = row.track_name == generated.name and row.source_name in generated_names
if len([row for row in profile.clips if row.enabled]) != args.count:
    raise AssertionError('The generated NLA inventory is not exactly the requested count.')
phase('Generated'+str(args.count))
if core.digest(source) != source_hash:
    raise AssertionError('The source blend changed on disk.')
report = dict(success=True, blender=bpy.app.version_string, sourceUnchanged=True,
              sourceSha256=source_hash, generatedDefinitionCount=args.count,
              generatedMethod='Actual unique NLA strips reusing the selected authored actions with four crop-length factors; active object/shape actions are retimed alongside them. All files exported through the actual Separate add-on path; these are not independently authored animations.',
              reports=results)
(out/'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print('LIGHTWEIGHT_BENCHMARK_RESULT '+json.dumps(dict(success=True, reports=[dict(label=r['label'], total=r['cumulative'][-1]) for r in results])), flush=True)
