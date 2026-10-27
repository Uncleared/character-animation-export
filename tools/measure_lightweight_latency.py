"""Blender --background --python this_file -- --full <Full FBX folder> --output <new JSON>.

Measure reduction/validation separately from baking, without changing input files.
Use comparable local Full exports; timings vary by machine and animation length.
"""
import argparse
import json
from pathlib import Path
import shutil
import statistics
import sys
import tempfile
import bpy

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO/'src/AnimationExport/Blender~'))
from character_animation_export import core, lightweight

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--full', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args(sys.argv[sys.argv.index('--')+1:])
if args.output.exists():
    raise ValueError('Choose a new output report; existing data will not be overwritten.')
files = sorted(args.full.glob('*.fbx'))
if not files:
    raise ValueError('No Full FBXs found.')
rows = []
with tempfile.TemporaryDirectory(prefix='animation-reduction-timing-') as temporary:
    for index, source in enumerate(files, 1):
        before = core.digest(source)
        target = Path(temporary)/source.name
        shutil.copy2(source, target)
        result = lightweight.reduce_file(target)
        if core.digest(source) != before:
            raise AssertionError('Input source changed while measuring.')
        rows.append(dict(index=index, file=source.name, reduceSeconds=result['seconds'],
                         fullBytes=result['originalBytes'], lightweightBytes=result['bytes'],
                         warnings=result['warnings'], sourceUnchanged=True))
        target.unlink()
seconds = [row['reduceSeconds'] for row in rows]
report = dict(success=True, blender=bpy.app.version_string,
              method='Actual validated reduction of temporary copies of Full FBXs; excludes baking/copying and is a separate run from total export timing.',
              files=rows, minimumSeconds=min(seconds), medianSeconds=statistics.median(seconds),
              maximumSeconds=max(seconds), totalSeconds=sum(seconds))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps({key:value for key,value in report.items() if key!='files'}, indent=2))
