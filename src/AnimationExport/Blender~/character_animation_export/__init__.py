bl_info = {"name": "Character Animation Export", "author": "PMIX", "version": (0, 1, 0),
           "blender": (5, 0, 0), "location": "View3D > Sidebar > Animation Export",
           "description": "Export checked character clips with synchronized prop animation", "category": "Import-Export"}

import bpy
from bpy.props import BoolProperty, CollectionProperty, EnumProperty, FloatProperty, IntProperty, PointerProperty, StringProperty
from pathlib import Path
import json
import math
import tempfile
from . import core

_busy = False
_signatures = {}
_relink_choices = []
_track_choices = {}


def settings_for(scene, create=True):
    selector = scene.character_animation_export
    if not selector.rig:
        return None
    profile = next((p for p in selector.profiles if p.rig == selector.rig), None)
    if profile is None and not create:
        return None
    if profile is None:
        profile = selector.profiles.add()
        profile.rig = selector.rig
    return profile


def _rig_poll(_self, obj):
    return obj.type == 'ARMATURE'


def _track_items(self, context):
    rig = self.rig
    tracks = rig.animation_data.nla_tracks if rig and rig.animation_data else []
    result = [('ALL', 'All Character Tracks', 'Choose clips from every NLA track on this character')]
    result += [(str(i), track.name, "Show and export checked clips on this track") for i, track in enumerate(tracks)]
    key = tuple(result)
    if key not in _track_choices:
        _track_choices[key] = result or [('NONE', 'No NLA track', 'Choose a rig with NLA strips, or use Actions')]
    return _track_choices[key]


def _rig_changed(self, context):
    if context and context.scene and self.rig:
        settings_for(context.scene)


def _channels(action, slot):
    for layer in action.layers:
        for strip in layer.strips:
            if strip.type == 'KEYFRAME':
                bag = strip.channelbag(slot)
                if bag:
                    yield from bag.fcurves


def _compatible_slot(action, rig):
    for slot in action.slots:
        if slot.target_id_type != 'OBJECT':
            continue
        paths = [curve.data_path for curve in _channels(action, slot)]
        if not paths:
            continue
        try:
            for path in paths:
                rig.path_resolve(path)
            return slot
        except (ValueError, TypeError):
            pass
    return None


def sources(settings):
    rig = settings.rig
    if not rig:
        return []
    if settings.source_mode == 'ACTIONS' and rig.library and not rig.override_library:
        raise ValueError('Use NLA mode for this linked rig, or create a local library override before assigning actions.')
    if settings.source_mode == 'ACTIONS':
        return [dict(action=a, track='', name=a.name, start=float(a.frame_range[0]), end=float(a.frame_range[1]), strip=None)
                for a in bpy.data.actions if _compatible_slot(a, rig)]
    animation = rig.animation_data
    if not animation:
        return []
    return [dict(action=strip.action, track=track.name, name=strip.name, start=float(strip.frame_start),
                 end=float(strip.frame_end), strip=strip) for track in animation.nla_tracks
            for strip in track.strips if strip.type == 'CLIP' and strip.action]


def _assign(record, source):
    old_name = record.source_name
    if not record.export_name or record.export_name == old_name:
        record.export_name = source['name']
    record.action = source['action']
    record.track_name = source['track']
    record.source_name = source['name']
    record.start = source['start']
    record.end = source['end']
    record.state = 'READY'


def refresh(settings):
    if not settings.profile_id:
        settings.profile_id = core.new_id()
    available = sources(settings)
    unmatched = list(available)
    old_unmatched = []
    for record in settings.clips:
        exact = [s for s in unmatched if s['action'] == record.action and s['track'] == record.track_name and s['name'] == record.source_name]
        if len(exact) == 1:
            _assign(record, exact[0])
            unmatched.remove(exact[0])
        else:
            record.state = 'MISSING'
            old_unmatched.append(record)
    for record in old_unmatched:
        group = [s for s in unmatched if s['action'] == record.action]
        old_group = [r for r in old_unmatched if r.action == record.action]
        if len(group) == 1 and len(old_group) == 1:
            _assign(record, group[0])
            unmatched.remove(group[0])
        elif group:
            record.state = 'AMBIGUOUS'
        elif any(s['track'] == record.track_name and s['name'] == record.source_name for s in unmatched):
            record.state = 'AMBIGUOUS'
    for source in unmatched:
        if any(r.state == 'AMBIGUOUS' and (r.action == source['action'] or (r.track_name == source['track'] and r.source_name == source['name'])) for r in settings.clips):
            continue
        record = settings.clips.add()
        record.clip_id = core.new_id()
        record.enabled = True
        _assign(record, source)
    return available


def _objects(scene, settings):
    rig = settings.rig
    selected = set(settings.export_collection.all_objects) if settings.export_collection else {rig, *rig.children_recursive}
    if not settings.export_collection:
        for obj in scene.objects:
            if any(getattr(mod, 'object', None) == rig for mod in obj.modifiers if mod.type == 'ARMATURE'):
                selected.add(obj)
                selected.update(obj.children_recursive)
    selected.add(rig)
    for obj in list(selected):
        selected.update(obj.children_recursive)
        ancestor = obj.parent
        while ancestor:
            selected.add(ancestor)
            ancestor = ancestor.parent
    return sorted((o for o in selected if o.type in {'ARMATURE', 'MESH', 'EMPTY'}), key=lambda o: o.name)


def _path(obj):
    return '/'.join([*_parents(obj), obj.name])


def _parents(obj):
    return [*_parents(obj.parent), obj.parent.name] if obj.parent else []


def _take_name(path):
    from io_scene_fbx import parse_fbx
    root, _version = parse_fbx.parse(str(path))
    objects = next(item for item in root.elems if item.id == b'Objects')
    stacks = [item for item in objects.elems if item.id == b'AnimationStack']
    if len(stacks) != 1:
        raise ValueError(f"Expected one combined animation take; found {len(stacks)}.")
    return stacks[0].props[1].split(b'\x00\x01')[0].decode('utf-8')


def export_package_steps(context, settings, cancel=None):
    global _busy
    if settings is None:
        raise ValueError('Choose a character armature in Setup.')
    refresh(settings)
    core.validate_names(settings.clips)
    scene = context.scene
    if not settings.rig:
        raise ValueError('Choose a character armature in Setup.')
    if not settings.output_folder:
        raise ValueError('Choose an output folder in Setup.')
    root = core.export_root(bpy.path.abspath(settings.output_folder))
    inventory = sources(settings)
    selected_track = None
    if settings.source_mode == 'NLA' and settings.track_index != 'ALL':
        tracks = settings.rig.animation_data.nla_tracks if settings.rig.animation_data else []
        if not settings.track_index.isdigit() or int(settings.track_index) >= len(tracks):
            raise ValueError('Choose All Character Tracks or an available NLA track.')
        selected_track = tracks[int(settings.track_index)].name
    checked = [r for r in settings.clips if r.enabled and not r.retired and r.state == 'READY'
               and (selected_track is None or r.track_name == selected_track)]
    if not checked:
        raise ValueError('Check at least one available clip to export.')
    if any(r.enabled and not r.retired and r.state == 'AMBIGUOUS' for r in settings.clips):
        raise ValueError('Relink ambiguous clip sources before exporting.')
    rig = settings.rig
    animation = rig.animation_data
    if animation and (animation.use_tweak_mode or any(t.is_solo for t in animation.nla_tracks)):
        raise ValueError('Exit NLA tweak/solo mode before exporting the evaluated character and props.')
    if settings.source_mode == 'NLA':
        if not animation or not animation.use_nla:
            raise ValueError('Enable the selected character NLA track for export.')
        for track in animation.nla_tracks:
            if any(r.track_name == track.name for r in checked):
                if track.mute:
                    raise ValueError("Enable NLA track '" + track.name + "' before exporting its clips.")
    included = _objects(scene, settings)
    unavailable = [o.name for o in included if getattr(o, 'is_missing', False) or getattr(o.data, 'is_missing', False)]
    if unavailable:
        raise ValueError('Resolve missing linked objects/data before exporting: ' + ', '.join(unavailable))
    missing = [o.name for o in included if o.name not in context.view_layer.objects]
    if missing:
        raise ValueError('Include these objects in the active view layer: ' + ', '.join(missing))
    previous = {}
    manifest_path = root / 'animation-export.json'
    if manifest_path.exists():
        previous = json.loads(manifest_path.read_text(encoding='utf-8'))
        if previous.get('profileId') != settings.profile_id:
            raise ValueError('This folder belongs to another export profile. Choose a different folder.')
    old_records = {r['id']: r for r in previous.get('clips', [])}
    fps = scene.render.fps / scene.render.fps_base
    manifest = dict(schemaVersion=1, profileId=settings.profile_id, characterName=rig.name,
                    publicationId=core.new_id(), generation=int(previous.get('generation', 0)) + 1,
                    blenderVersion=bpy.app.version_string, fps=fps, clips=[], warnings=[])
    missing_libraries = [library.filepath for library in bpy.data.libraries
                         if not Path(bpy.path.abspath(library.filepath, library=library.parent)).exists()]
    if missing_libraries:
        manifest['warnings'].append('Missing linked libraries: ' + '; '.join(missing_libraries))
    old_frame = (scene.frame_current, scene.frame_subframe, scene.frame_start, scene.frame_end)
    old_selection = list(context.selected_objects)
    old_active = context.view_layer.objects.active
    old_action = animation.action if animation else None
    old_slot = animation.action_slot if animation and animation.action else None
    old_use_nla = animation.use_nla if animation else True
    root.mkdir(parents=True, exist_ok=True)
    _busy = True
    context.window_manager.progress_begin(0, len(checked))
    try:
        with tempfile.TemporaryDirectory(prefix='.animation-bake-', dir=root) as staging:
            staging = Path(staging)
            staged = {}
            for index, record in enumerate(checked):
                if cancel and cancel(index):
                    raise InterruptedError('Export cancelled; the previous package was preserved.')
                if record.end <= record.start or any(abs(v - round(v)) > 1e-4 for v in (record.start, record.end)):
                    raise ValueError(f"'{record.export_name}' needs distinct whole-frame boundaries in this prototype.")
                source = next((s for s in inventory if s['action'] == record.action and s['track'] == record.track_name and s['name'] == record.source_name), None)
                if source is None or (source['strip'] and source['strip'].mute):
                    raise ValueError(f"Enable or relink '{record.export_name}' before export.")
                if settings.source_mode == 'ACTIONS':
                    rig.animation_data_create()
                    animation = rig.animation_data
                    animation.use_nla = False
                    animation.action = record.action
                    animation.action_slot = _compatible_slot(record.action, rig)
                scene.frame_start, scene.frame_end = round(record.start), round(record.end)
                scene.frame_set(scene.frame_start)
                for obj in context.view_layer.objects:
                    obj.select_set(False)
                for obj in included:
                    obj.select_set(True)
                context.view_layer.objects.active = rig
                relative = 'clips/' + record.clip_id + '.fbx'
                path = staging / (record.clip_id + '.fbx')
                bpy.ops.export_scene.fbx(filepath=str(path), check_existing=False, use_selection=True,
                    use_visible=False, use_active_collection=False, object_types={'ARMATURE', 'MESH', 'EMPTY'},
                    bake_anim=True, bake_anim_use_all_actions=False, bake_anim_use_nla_strips=False,
                    bake_anim_use_all_bones=True, bake_anim_force_startend_keying=True,
                    bake_anim_step=1.0, bake_anim_simplify_factor=0.0,
                    global_scale=1.0, apply_unit_scale=True, apply_scale_options=settings.scale_mode,
                    use_space_transform=True, bake_space_transform=False, add_leaf_bones=settings.leaf_bones,
                    use_armature_deform_only=False, axis_forward='-Z', axis_up='Y', path_mode='AUTO', embed_textures=False)
                take = _take_name(path)
                old_records[record.clip_id] = dict(id=record.clip_id, name=record.export_name, state='present', enabled=True,
                    exported=True, file=relative, sha256=core.digest(path), takeName=take, startFrame=record.start,
                    endFrame=record.end, fps=fps, duration=(record.end - record.start) / fps,
                    objectPaths=[_path(o) for o in included])
                staged[relative] = path
                context.window_manager.progress_update(index + 1)
                yield record.export_name
            for record in settings.clips:
                item = dict(old_records.get(record.clip_id, {}))
                item.update(id=record.clip_id, name=record.export_name, enabled=record.enabled,
                            state='removed' if record.retired or record.state != 'READY' else 'present',
                            exported=record in checked)
                manifest['clips'].append(item)
            core.publish(root, manifest, staged)
        settings.last_report = f"Exported {len(checked)} clips. Package: {root / 'animation-export.json'}"
        if manifest['warnings']:
            settings.last_report += '\nWarning: missing linked libraries; review the package report.'
        return manifest
    finally:
        if animation and settings.source_mode == 'ACTIONS':
            animation.action = old_action
            if old_action and old_slot:
                animation.action_slot = old_slot
            animation.use_nla = old_use_nla
        scene.frame_start, scene.frame_end = old_frame[2:]
        scene.frame_set(old_frame[0], subframe=old_frame[1])
        for obj in context.view_layer.objects:
            obj.select_set(False)
        for obj in old_selection:
            if obj.name in context.view_layer.objects:
                obj.select_set(True)
        context.view_layer.objects.active = old_active
        context.window_manager.progress_end()
        _busy = False


def export_package(context, settings, cancel=None):
    steps = export_package_steps(context, settings, cancel)
    while True:
        try:
            next(steps)
        except StopIteration as finished:
            return finished.value


class CAEClip(bpy.types.PropertyGroup):
    clip_id: StringProperty()
    action: PointerProperty(type=bpy.types.Action)
    source_name: StringProperty()
    track_name: StringProperty()
    export_name: StringProperty(name='Clip Name')
    enabled: BoolProperty(name='Export', default=True)
    retired: BoolProperty(default=False)
    state: StringProperty(default='READY')
    start: FloatProperty()
    end: FloatProperty()


class CAESettings(bpy.types.PropertyGroup):
    rig: PointerProperty(name='Character Rig', type=bpy.types.Object, poll=_rig_poll)
    export_collection: PointerProperty(name='Objects and Props', type=bpy.types.Collection,
        description='Optional collection containing character and associated props; descendants are included')
    source_mode: EnumProperty(name='Clip Source', items=[('NLA', 'NLA Strips', 'Read ranges from a character clip track'),
        ('ACTIONS', 'Actions', 'Use chosen actions; object/prop tracks are sampled at each action range')])
    track_index: EnumProperty(name='Clip Track', items=_track_items)
    output_folder: StringProperty(name='Export Folder', subtype='DIR_PATH')
    profile_id: StringProperty()
    clips: CollectionProperty(type=CAEClip)
    active_clip: IntProperty()
    scale_mode: EnumProperty(name='Scaling', items=[('FBX_SCALE_ALL', 'Unity Blend Compatible', 'Use FBX All scaling'),
        ('FBX_SCALE_NONE', 'All Local', 'Use local scaling for a matching model export')], default='FBX_SCALE_ALL')
    leaf_bones: BoolProperty(name='Export End Bones', default=True)
    show_setup: BoolProperty(name='Setup', default=True)
    show_removed: BoolProperty(name='Show Removed Clips', default=False)
    last_report: StringProperty()


class CAESceneSettings(bpy.types.PropertyGroup):
    rig: PointerProperty(name='Character Rig', type=bpy.types.Object, poll=_rig_poll, update=_rig_changed)
    profiles: CollectionProperty(type=CAESettings)


class CAE_OT_refresh(bpy.types.Operator):
    bl_idname = 'cae.refresh'
    bl_label = 'Refresh Clips'
    def execute(self, context):
        selector = context.scene.character_animation_export
        if not selector.rig:
            rigs = [o for o in context.scene.objects if o.type == 'ARMATURE']
            if len(rigs) == 1:
                selector.rig = rigs[0]
        settings = settings_for(context.scene)
        if settings is None:
            self.report({'ERROR'}, 'Choose a character rig first.')
            return {'CANCELLED'}
        refresh(settings)
        return {'FINISHED'}


class CAE_OT_export(bpy.types.Operator):
    bl_idname = 'cae.export_checked'
    bl_label = 'Export Checked Clips'
    def execute(self, context):
        if not bpy.app.background:
            self.steps = export_package_steps(context, settings_for(context.scene))
            self.timer = context.window_manager.event_timer_add(0.1, window=context.window)
            context.window_manager.modal_handler_add(self)
            return {'RUNNING_MODAL'}
        try:
            export_package(context, settings_for(context.scene))
            self.report({'INFO'}, settings_for(context.scene).last_report)
            return {'FINISHED'}
        except Exception as exc:
            settings_for(context.scene).last_report = str(exc)
            self.report({'ERROR'}, str(exc))
            return {'CANCELLED'}

    def modal(self, context, event):
        if event.type == 'ESC':
            self.steps.close()
            context.window_manager.event_timer_remove(self.timer)
            self.report({'INFO'}, 'Cancelled. Previous package preserved.')
            profile = settings_for(context.scene)
            if profile:
                profile.last_report = 'Cancelled. Previous package preserved.'
            return {'CANCELLED'}
        if event.type == 'TIMER' and event.timer == self.timer:
            try:
                next(self.steps)
                for area in context.screen.areas:
                    area.tag_redraw()
            except StopIteration:
                context.window_manager.event_timer_remove(self.timer)
                self.report({'INFO'}, settings_for(context.scene).last_report)
                return {'FINISHED'}
            except Exception as exc:
                self.steps.close()
                context.window_manager.event_timer_remove(self.timer)
                settings_for(context.scene).last_report = str(exc)
                self.report({'ERROR'}, str(exc))
                return {'CANCELLED'}
        return {'RUNNING_MODAL'}


class CAE_OT_clip(bpy.types.Operator):
    bl_idname = 'cae.clip_operation'
    bl_label = 'Clip Operation'
    bl_options = {'UNDO'}
    index: IntProperty()
    operation: StringProperty()
    def execute(self, context):
        settings = settings_for(context.scene)
        record = settings.clips[self.index]
        if self.operation == 'PREVIEW':
            context.scene.frame_set(round(record.start))
            self.report({'INFO'}, f"{record.export_name}: frames {record.start:g} to {record.end:g}")
        elif self.operation == 'RESTORE':
            record.retired = False
        else:
            record.retired = True
        return {'FINISHED'}


def _relink_items(_self, _context):
    return _relink_choices


class CAE_OT_relink(bpy.types.Operator):
    bl_idname = 'cae.relink'
    bl_label = 'Relink Missing Clip'
    bl_options = {'UNDO'}
    index: IntProperty()
    source: EnumProperty(name='Source', items=_relink_items)
    def invoke(self, context, _event):
        global _relink_choices
        candidates = sources(settings_for(context.scene))
        _relink_choices = [(str(i), s['track'] + ' / ' + s['name'], '') for i, s in enumerate(candidates)]
        return context.window_manager.invoke_props_dialog(self) if candidates else {'CANCELLED'}
    def execute(self, context):
        settings = settings_for(context.scene)
        candidate = sources(settings)[int(self.source)]
        if any(r.state == 'READY' and r.action == candidate['action'] and r.track_name == candidate['track'] and r.source_name == candidate['name'] for r in settings.clips):
            self.report({'ERROR'}, 'That source already belongs to another clip.')
            return {'CANCELLED'}
        _assign(settings.clips[self.index], candidate)
        refresh(settings)
        return {'FINISHED'}


class CAE_UL_clips(bpy.types.UIList):
    def filter_items(self, _context, data, propname):
        tracks = data.rig.animation_data.nla_tracks if data.rig and data.rig.animation_data else []
        selected = tracks[int(data.track_index)].name if data.source_mode == 'NLA' and data.track_index.isdigit() and int(data.track_index) < len(tracks) else None
        return [self.bitflag_filter_item if (data.show_removed or not item.retired) and (selected is None or item.track_name == selected) else 0 for item in getattr(data, propname)], []
    def draw_item(self, context, layout, _data, item, _icon, _active_data, _active_propname, index):
        row = layout.row(align=True)
        row.prop(item, 'enabled', text='')
        row.prop(item, 'export_name', text='', emboss=False)
        row.label(text='Removed' if item.retired else (f"{(item.end-item.start)/(context.scene.render.fps/context.scene.render.fps_base):.2f}s" if item.state == 'READY' else item.state.title()))
        op = row.operator('cae.clip_operation', text='', icon='HIDE_OFF')
        op.index, op.operation = index, 'PREVIEW'
        if item.state != 'READY':
            op = row.operator('cae.relink', text='', icon='LINKED')
            op.index = index
        op = row.operator('cae.clip_operation', text='', icon='LOOP_BACK' if item.retired else 'X')
        op.index, op.operation = index, 'RESTORE' if item.retired else 'REMOVE'


class CAE_PT_export(bpy.types.Panel):
    bl_label = 'Character Animation Export'
    bl_idname = 'CAE_PT_export'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'Animation Export'
    def draw(self, context):
        layout = self.layout
        layout.prop(context.scene.character_animation_export, 'rig')
        settings = settings_for(context.scene, create=False)
        if settings is None:
            layout.operator('cae.refresh', text='Find Character Rig')
            return
        layout.prop(settings, 'show_setup', emboss=False, icon='TRIA_DOWN' if settings.show_setup else 'TRIA_RIGHT')
        if settings.show_setup:
            box = layout.box()
            box.prop(settings, 'export_collection')
            box.prop(settings, 'source_mode')
            if settings.source_mode == 'NLA':
                box.prop(settings, 'track_index')
            else:
                box.label(text='Prop tracks use the action\'s source frames.', icon='INFO')
            box.prop(settings, 'output_folder')
            advanced = box.box()
            advanced.label(text='Matching model export settings')
            advanced.prop(settings, 'scale_mode')
            advanced.prop(settings, 'leaf_bones')
        layout.template_list('CAE_UL_clips', '', settings, 'clips', settings, 'active_clip', rows=7)
        layout.prop(settings, 'show_removed')
        layout.operator('cae.export_checked', icon='EXPORT')
        layout.operator('cae.refresh', icon='FILE_REFRESH')
        if settings.last_report:
            for part in settings.last_report.splitlines():
                layout.label(text=part[:100])
        layout.label(text='NLA/All Actions export off; combined bake on.')


def _timer():
    if not hasattr(bpy.types.Scene, 'character_animation_export'):
        return None
    if not _busy:
        for scene in bpy.data.scenes:
            settings = settings_for(scene)
            if settings and settings.rig:
                try:
                    signature = (settings.rig.as_pointer(), settings.source_mode, settings.track_index,
                        tuple((s['action'].as_pointer(), s['track'], s['name'], s['start'], s['end']) for s in sources(settings)))
                    if _signatures.get(scene.as_pointer()) != signature:
                        refresh(settings)
                        _signatures[scene.as_pointer()] = signature
                except (RuntimeError, ReferenceError, ValueError):
                    pass
    return 1.0


_classes = (CAEClip, CAESettings, CAESceneSettings, CAE_OT_refresh, CAE_OT_export, CAE_OT_clip, CAE_OT_relink, CAE_UL_clips, CAE_PT_export)


def register():
    for cls in _classes:
        bpy.utils.register_class(cls)
    bpy.types.Scene.character_animation_export = PointerProperty(type=CAESceneSettings)
    if not bpy.app.timers.is_registered(_timer):
        bpy.app.timers.register(_timer, first_interval=1, persistent=True)


def unregister():
    if bpy.app.timers.is_registered(_timer):
        bpy.app.timers.unregister(_timer)
    del bpy.types.Scene.character_animation_export
    for cls in reversed(_classes):
        bpy.utils.unregister_class(cls)
    _signatures.clear()
    _track_choices.clear()
