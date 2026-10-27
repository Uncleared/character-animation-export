"""Blender background safety cases: -- --fixtures <TestLightweightExport output> --output <new folder>."""
import argparse
import json
from pathlib import Path
import shutil
import sys
from types import SimpleNamespace
from unittest.mock import patch
import bpy

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from character_animation_export import core, lightweight as lite
from io_scene_fbx import parse_fbx, encode_bin

p = argparse.ArgumentParser(); p.add_argument('--fixtures', type=Path, required=True); p.add_argument('--output', type=Path, required=True)
args = p.parse_args(sys.argv[sys.argv.index('--')+1:]); args.output.mkdir(parents=True, exist_ok=True)
if any(args.output.iterdir()):
    raise ValueError('Choose an empty safety output folder.')
source = next((args.fixtures/'Variants/NoFace/Full').glob('*.fbx'))
checks = []
def check(value, label):
    if not value:
        raise AssertionError(label)
    checks.append(label)
def fixture(name, mutate=None):
    path = args.output/(name+'.fbx')
    if mutate:
        parsed, version = parse_fbx.parse(str(source)); encoded = lite._copy(parsed, encode_bin)
        records, connections = lite._records(parsed)
        outputs = dict(zip(records, lite.child(encoded, b'Objects').elems))
        mutate(encoded, records, outputs, connections)
        encode_bin.write(str(path), encoded, version)
    else:
        shutil.copy2(source, path)
    return path
def retained(path, label):
    before = core.digest(path); result = lite.reduce_file(path)
    check(core.digest(path) == before and result['bytes'] == result['originalBytes'] and result['warnings'], label)
def failure(path, label, kind=ValueError):
    before = core.digest(path)
    try:
        lite.reduce_file(path)
    except kind:
        check(core.digest(path) == before and not path.with_name(path.name+'.lightweight-tmp').exists(), label)
    else:
        raise AssertionError(label+' did not reject')
def unknown(root, records, outputs, links):
    uid = next(uid for uid, item in records.items() if item.id == b'Geometry' and item.props[2] == b'Mesh')
    outputs[uid].elems.append(encode_bin.FBXElem(b'FutureVertexLayer'))
retained(fixture('UnknownGeometry', unknown), 'unknown mesh data retains the original whole FBX')
def material(root, records, outputs, links):
    animation = next(uid for uid, item in records.items() if item.id == b'AnimationCurveNode')
    target = next(uid for uid, item in records.items() if item.id == b'Material')
    link = encode_bin.FBXElem(b'C'); link.add_string(b'OP'); link.add_int64(animation); link.add_int64(target); link.add_string(b'DiffuseColor')
    lite.child(root, b'Connections').elems.append(link)
retained(fixture('AnimatedMaterial', material), 'animated representation target retains all dependencies without deleting curves')
def dangling(root, records, outputs, links):
    link = encode_bin.FBXElem(b'C'); link.add_string(b'OO'); link.add_int64(9223372036854770000); link.add_int64(0)
    lite.child(root, b'Connections').elems.append(link)
failure(fixture('Dangling', dangling), 'dangling endpoint rejected without changing the source')
def weights(root, records, outputs, links):
    uid = next(uid for uid, item in records.items() if item.id == b'Deformer' and item.props[2] == b'Cluster' and lite._active_cluster(item))
    outputs[uid].elems[:] = [item for item in outputs[uid].elems if item.id != b'Weights']
failure(fixture('MissingWeights', weights), 'incomplete cluster rejected without changing the source')
path = fixture('PrivateApi')
with patch.object(lite.inspect, 'signature', return_value=SimpleNamespace(parameters={})):
    failure(path, 'unknown Blender codec API rejected with original file intact')
path = fixture('CorruptValidation')
with patch.object(lite, 'animation_signature', side_effect=['before', 'after']):
    failure(path, 'failed animation invariant discards staged candidate')
path = fixture('LockedReplace')
with patch.object(lite.os, 'replace', side_effect=PermissionError('simulated staging lock')):
    failure(path, 'staging replace failure cleans candidate and preserves original', PermissionError)
path = args.output/'Corrupt.fbx'; path.write_bytes(b'not an FBX')
before = core.digest(path)
try:
    lite.reduce_file(path)
except Exception:
    check(core.digest(path) == before, 'corrupt FBX parse never changes its source')
else:
    raise AssertionError('corrupt input accepted')
result = dict(success=True, blender=bpy.app.version_string, passed=len(checks), checks=checks)
(args.output/'result.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
print('LIGHTWEIGHT_SAFETY_RESULT '+json.dumps(result), flush=True)
