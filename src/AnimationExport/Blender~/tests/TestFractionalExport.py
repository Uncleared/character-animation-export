"""Native fractional endpoint regression: Blender --background --python <this> -- --output <empty folder>."""
import argparse
import bpy
import json
import math
import numpy as np
from pathlib import Path
import sys

BASE = Path(__file__).resolve().parents[1]
REPO = BASE.parents[2]
sys.path.insert(0, str(BASE))
import character_animation_export as addon
from character_animation_export import combined
from io_scene_fbx import parse_fbx
from io_scene_fbx.fbx_utils import FBX_KTIME

parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, required=True)
OUT = parser.parse_args(sys.argv[sys.argv.index('--') + 1:]).output
OUT.mkdir(parents=True, exist_ok=True)
if any(OUT.iterdir()):
    raise ValueError('Choose an empty test output folder.')
checks = []
def check(value, label):
    if not value:
        raise AssertionError(label)
    checks.append(label)

for start in (-1000, -20.25, 0, 0.1, 0.25, 1, 10.3, 100.25, 1000.125, 10000.75):
    for span in (0.1, 0.3, 0.7, 1.1, 3.45, 10.1, 57.25, 101.15, 476.3, 999.999):
        end = start + span
        step, padded_end = combined.sampling_range(start, end)
        frames = np.arange(start, np.nextafter(padded_end, np.inf), step=step)
        check(len(frames) == math.ceil(end - start) + 1 and abs(frames[-1] - end) < 1e-7,
              'native sample grid includes endpoint ' + str((start, span)))

addon.register()
bpy.ops.wm.open_mainfile(filepath=str(REPO / 'examples/ExampleCharacter.blend'))
profile = addon.settings_for(bpy.context.scene)
profile.export_layout = 'COMBINED'
profile.combined_name = 'Fractional'
profile.source_file = ''
profile.output_folder = str(OUT / 'Exports')
addon.refresh(profile)
identity = profile.clips[0].clip_id
for record in profile.clips:
    record.enabled = record.clip_id == identity
row = next(item for item in addon.sources(profile) if item['action'] == profile.clips[0].action and item['name'] == profile.clips[0].source_name)
strip = row['strip']
for start, span in ((0.1, 0.3), (-1000, 999.999), (0.25, 3.45), (10.3, 57.25), (1000.125, 1.1)):
    # Move the leading boundary before resizing so Blender does not clamp it.
    strip.frame_start_ui = start
    strip.scale = span / (strip.action_frame_end - strip.action_frame_start)
    addon.refresh(profile)
    result = addon.export_direct(bpy.context, profile)
    record = next(item for item in result['clips'] if item.get('exported'))
    tree, _version = parse_fbx.parse(str(OUT / 'Exports/Fractional.fbx'))
    objects = next(element for element in tree.elems if element.id == b'Objects')
    last = max(int(max(child.props[0])) for element in objects.elems if element.id == b'AnimationCurve'
               for child in element.elems if child.id == b'KeyTime')
    expected = int(record['duration'] * FBX_KTIME)
    check(abs(last - expected) <= 2, 'native FBX has final key at exact take duration ' + str((start, span)))
    check(len(combined.stack_names(OUT / 'Exports/Fractional.fbx')) == 1, 'native take inventory ' + str((start, span)))

report = dict(success=True, blender=bpy.app.version_string, checks=checks)
(OUT / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print('FRACTIONAL_EXPORT_RESULT', json.dumps(dict(success=True, blender=bpy.app.version_string, checks=len(checks))))
