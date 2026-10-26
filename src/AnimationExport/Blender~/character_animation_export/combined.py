"""Version-bounded native FBX adapter. No exporter hook survives a generator yield."""
import inspect
import math
from types import SimpleNamespace

import bpy
from bpy_extras.io_utils import axis_conversion
import io_scene_fbx
from io_scene_fbx import export_fbx_bin as native, parse_fbx


def take_name(clip_id):
    return 'CAE_' + clip_id


def check_compatibility():
    supported = {(5, 0): (5, 14, 0), (5, 2): (5, 15, 0)}
    expected = supported.get(tuple(bpy.app.version[:2]))
    signatures = {
        'fbx_data_from_scene': ('scene', 'depsgraph', 'settings'),
        'fbx_animations': ('scene_data',),
        'fbx_animations_do': ('scene_data', 'ref_id', 'f_start', 'f_end', 'start_zero', 'objects', 'force_keep'),
        'fbx_scene_data_cleanup': ('scene_data',),
    }
    if expected != tuple(io_scene_fbx.bl_info.get('version', ())):
        raise ValueError('Combined export supports the bundled FBX exporter in Blender 5.0 and 5.2. Use Separate or a supported Blender version.')
    for name, parameters in signatures.items():
        function = getattr(native, name, None)
        if function is None or tuple(inspect.signature(function).parameters) != parameters:
            raise ValueError('The installed FBX exporter is incompatible with Combined export. Use Separate or the bundled exporter.')
    required = {'context_objects', 'global_matrix', 'bake_anim_use_nla_strips', 'bake_anim_use_all_actions'}
    if not required.issubset(inspect.signature(native.save_single).parameters):
        raise ValueError('The installed FBX exporter has unsupported settings.')


def stack_names(path):
    tree, _version = parse_fbx.parse(str(path))
    objects = next(item for item in tree.elems if item.id == b'Objects')
    return [item.props[1].split(b'\x00\x01')[0].decode('utf-8')
            for item in objects.elems if item.id == b'AnimationStack']


def export_steps(context, settings, path, records, included, prepare, cancel=None):
    check_compatibility()
    if any(obj.mode == 'EDIT' for obj in included):
        raise ValueError('Exit Edit Mode before exporting animations.')
    options = dict(context_objects=included,
        global_matrix=axis_conversion(to_forward='-Z', to_up='Y').to_4x4(),
        object_types={'ARMATURE', 'MESH', 'EMPTY'}, bake_anim=True,
        bake_anim_use_all_actions=False, bake_anim_use_nla_strips=False,
        bake_anim_use_all_bones=True, bake_anim_force_startend_keying=True,
        bake_anim_step=1.0, bake_anim_simplify_factor=0.0, global_scale=1.0,
        apply_unit_scale=True, apply_scale_options=settings.scale_mode,
        use_space_transform=True, bake_space_transform=False,
        add_leaf_bones=settings.leaf_bones, use_armature_deform_only=False,
        axis_forward='-Z', axis_up='Y', use_mesh_edges=False, use_tspace=False,
        path_mode='AUTO', embed_textures=False)
    operator = SimpleNamespace(report=lambda levels, message: None)
    scene = context.scene
    original_prepare, original_animations = native.fbx_data_from_scene, native.fbx_animations
    captured = []
    temporary = []
    prepared = None

    class CapturedSettings(Exception):
        pass

    def capture(scene, depsgraph, export_settings):
        captured.append((scene, depsgraph, export_settings))
        raise CapturedSettings()

    def without_animations(data):
        temporary.append(data)
        return (), set(), scene.frame_start, scene.frame_end

    try:
        prepare(records[0])
        depsgraph = context.evaluated_depsgraph_get()
        # Let Blender construct its own settings instead of copying version-specific defaults.
        try:
            native.fbx_data_from_scene = capture
            try:
                native.save_single(operator, scene, depsgraph, str(path), **options)
            except CapturedSettings:
                pass
        finally:
            native.fbx_data_from_scene = original_prepare
        if len(captured) != 1:
            raise ValueError('Could not prepare the native FBX exporter.')
        try:
            native.fbx_animations = without_animations
            prepared = original_prepare(*captured[0])
        finally:
            native.fbx_animations = original_animations
        animations, animated = [], set()
        for index, record in enumerate(records):
            if cancel and cancel(index):
                raise InterruptedError('Export cancelled; previous outputs were preserved.')
            prepare(record)
            reference = SimpleNamespace(name=take_name(record.clip_id), library=None,
                                        rna_type=SimpleNamespace(name='Action'))
            # Include the exact endpoint after NLA scaling, using at least one
            # sample per source frame instead of dropping a fractional last frame.
            span = record.end - record.start
            sampling = prepared._replace(settings=prepared.settings._replace(bake_anim_step=span / math.ceil(span)))
            animation = native.fbx_animations_do(sampling, reference, record.start, record.end,
                                                  True, force_keep=True)
            if animation is None:
                raise ValueError('No animation could be baked for ' + record.export_name)
            animations.append(animation)
            for element, (_unused, nodes) in animation[1].items():
                animated.update((element, prop) for prop in nodes)
            context.window_manager.progress_update(index + 1)
            yield record.export_name
        if cancel and cancel(len(records)):
            raise InterruptedError('Export cancelled; previous outputs were preserved.')
        native.fbx_scene_data_cleanup(prepared)
        prepared = None
        temporary.clear()
        native.ObjectWrapper.cache_clear()
        prepare(records[0])

        def cached_animations(data):
            return animations, animated, 0.0, max(animation[5] for animation in animations)

        try:
            native.fbx_animations = cached_animations
            result = native.save_single(operator, scene, context.evaluated_depsgraph_get(), str(path), **options)
        finally:
            native.fbx_animations = original_animations
        expected = [take_name(record.clip_id) for record in records]
        actual = stack_names(path)
        if result != {'FINISHED'} or len(actual) != len(expected) or set(actual) != set(expected):
            raise ValueError('The combined FBX contains unexpected animation takes; previous output was preserved.')
    finally:
        native.fbx_data_from_scene = original_prepare
        native.fbx_animations = original_animations
        try:
            if prepared is not None or temporary:
                native.fbx_scene_data_cleanup(prepared if prepared is not None else temporary[-1])
        finally:
            native.ObjectWrapper.cache_clear()
