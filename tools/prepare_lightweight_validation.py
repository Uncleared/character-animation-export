"""Prepare a new disposable Unity project from TestLightweightExport.py output.

Run LightweightSmoke.Run, then reopen with LightweightSmoke.Resume.
--scale200 instead prepares the small Generic lifecycle fixture for RunScale/Resume.
Production character fixtures are optional local inputs and are never redistributed.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import uuid

from prepare_unity_validation import prepare, REPO


def prepare_lightweight(destination, fixtures, scale200=False, character=None, avatar=None):
    fixtures = Path(fixtures).resolve()
    if not (fixtures/'Variants/NoFace/Full').is_dir():
        raise ValueError('Use the completed TestLightweightExport.py output folder.')
    if character is not None and avatar is None:
        raise ValueError('A Humanoid character fixture also needs its matching standalone Avatar asset.')
    destination = prepare(destination, mode='events')
    shutil.copytree(REPO/'src/AnimationExport/Editor', destination/'Assets/AnimationExport/Editor', dirs_exist_ok=True)
    shutil.copy2(REPO/'tools/validation/LightweightSmoke.cs', destination/'Assets/Editor/LightweightSmoke.cs')
    target = destination/'LightweightFixtures'
    if scale200:
        profile_id = uuid.uuid4().hex
        identities = [uuid.uuid4().hex for _ in range(200)]
        for mode in ('Full', 'Lightweight'):
            source = fixtures/'Variants/NoFace'/mode
            manifest = json.loads(next((source/'AnimationExportData').glob('*.animation-export.json')).read_text(encoding='utf-8'))
            template = next(row for row in manifest['clips'] if row.get('exported'))
            data = (source/template['file']).read_bytes()
            output = target/'Scale200'/mode; output.mkdir(parents=True)
            clips = []
            for index, identity in enumerate(identities, 1):
                row = dict(template, id=identity, name=f'Scale_{index:03d}', file=f'Scale_{index:03d}.fbx', sha256=hashlib.sha256(data).hexdigest())
                (output/row['file']).write_bytes(data); clips.append(row)
            manifest.update(profileId=profile_id, characterName='Generated Scale200', clips=clips)
            metadata = output/'AnimationExportData'; metadata.mkdir()
            (metadata/(profile_id+'.animation-export.json')).write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    else:
        for mode in ('Full', 'Lightweight'):
            shutil.copytree(fixtures/'Fixtures'/mode, target/'Example'/mode)
        for variant in (fixtures/'Variants').iterdir():
            for mode in ('Full', 'Lightweight'):
                shutil.copytree(variant/mode, target/variant.name/mode)
        if character is not None:
            for mode in ('Full', 'Lightweight'):
                shutil.copytree(Path(character)/mode, target/'Valencia'/mode)
            shutil.copy2(avatar, destination/'Assets/MatchingAvatar.asset')
    return destination


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=Path)
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--scale200', action='store_true')
    parser.add_argument('--character', type=Path)
    parser.add_argument('--avatar', type=Path)
    args = parser.parse_args()
    print(prepare_lightweight(args.destination, args.fixtures, args.scale200, args.character, args.avatar))
