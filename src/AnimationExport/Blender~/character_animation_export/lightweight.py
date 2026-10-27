"""Reduce staged Blender FBXs without changing animation or artist datablocks."""
from collections import Counter
import hashlib
import inspect
import os
from pathlib import Path
import struct
import time


_METHODS = {ord(k): v for k, v in (
    ('B', 'add_bool'), ('C', 'add_char'), ('Z', 'add_int8'), ('Y', 'add_int16'),
    ('I', 'add_int32'), ('L', 'add_int64'), ('F', 'add_float32'), ('D', 'add_float64'),
    ('R', 'add_bytes'), ('S', 'add_string'), ('i', 'add_int32_array'),
    ('l', 'add_int64_array'), ('f', 'add_float32_array'), ('d', 'add_float64_array'),
    ('b', 'add_bool_array'), ('c', 'add_byte_array'))}
_SCALARS = {ord(k): v for k, v in (
    ('B', '?'), ('Z', 'b'), ('Y', 'h'), ('I', 'i'), ('L', 'q'), ('F', 'f'), ('D', 'd'))}
_MESH_DATA = {b'Vertices', b'PolygonVertexIndex', b'Edges', b'Layer',
    b'LayerElementNormal', b'LayerElementBinormal', b'LayerElementTangent',
    b'LayerElementSmoothing', b'LayerElementEdgeCrease', b'LayerElementColor',
    b'LayerElementUV', b'LayerElementMaterial'}
_MESH_PROPERTIES = {b'Properties70', b'GeometryVersion', b'Smoothness', b'BoundaryRule',
    b'PreviewDivisionLevels', b'RenderDivisionLevels', b'PreserveBorders',
    b'PreserveHardEdges', b'PropagateEdgeHardness'}


def _api():
    try:
        from io_scene_fbx import parse_fbx, encode_bin
        if not {'fn', 'elem_root', 'version'}.issubset(inspect.signature(encode_bin.write).parameters):
            raise ValueError('Unsupported FBX writer signature')
        if not hasattr(parse_fbx, 'parse') or any(not hasattr(encode_bin.FBXElem, v) for v in _METHODS.values()):
            raise ValueError('Unsupported FBX element API')
        return parse_fbx, encode_bin
    except (ImportError, AttributeError, TypeError, ValueError) as error:
        raise ValueError('Lightweight export is unavailable with this Blender FBX module. '
                         'Turn off Lightweight to export Full files. ' + str(error)) from error


def child(node, name):
    return next((item for item in node.elems if item.id == name), None)


def _records(tree):
    objects = child(tree, b'Objects')
    connections = child(tree, b'Connections')
    if objects is None or connections is None:
        raise ValueError('The staged FBX has no object/connection table.')
    result = {item.props[0]: item for item in objects.elems}
    if len(result) != len(objects.elems):
        raise ValueError('The staged FBX has duplicate object IDs.')
    return result, connections.elems


def _value_bytes(kind, value):
    if kind in _SCALARS:
        return struct.pack('<' + _SCALARS[kind], value)
    if kind in (ord('S'), ord('R'), ord('C')):
        return bytes(value)
    if kind in _METHODS:
        return memoryview(value).tobytes()
    raise ValueError('Unknown FBX property type: ' + str(kind))


def fingerprint(nodes):
    """Typed structural hash, including exact stored animation sample values."""
    digest = hashlib.sha256()
    def put(value):
        digest.update(struct.pack('<Q', len(value))); digest.update(value)
    def visit(node):
        put(node.id); put(bytes(node.props_type))
        for kind, value in zip(node.props_type, node.props):
            put(_value_bytes(kind, value))
        digest.update(struct.pack('<Q', len(node.elems)))
        for item in node.elems:
            visit(item)
    for node in nodes:
        visit(node)
    return digest.hexdigest()


def animation_signature(tree):
    records, connections = _records(tree)
    animation = {uid for uid, item in records.items() if item.id.startswith(b'Animation')}
    links = [item for item in connections if item.props[1] in animation or item.props[2] in animation]
    targets = {item.props[2] for item in links if item.props[1] in animation and item.props[2] not in animation}
    target_records = []
    for uid in sorted(targets):
        if uid not in records:
            continue
        item = records[uid]
        # Blender also targets the named shape-weight property on the Mesh itself.
        # Preserve its identity and Properties70 exactly, independently of vertices.
        if item.id == b'Geometry' and item.props[2] == b'Mesh':
            item = _without(item, _MESH_DATA)
        target_records.append(item)
    return fingerprint([records[uid] for uid in sorted(animation)] + links + target_records)


def _protected_signature(tree):
    records, connections = _records(tree)
    protected = [child(tree, b'GlobalSettings')]
    for uid in sorted(records):
        item = records[uid]
        if item.id in (b'Model', b'NodeAttribute', b'Pose') or item.id == b'Deformer':
            if item.id == b'Deformer' and item.props[2] == b'Cluster':
                # Only vertex indices and weights change; link/bind matrices stay exact.
                item = _without(item, {b'Indexes', b'Weights'})
            protected.append(item)
    ids = {uid for uid, item in records.items() if item.id in (b'Model', b'NodeAttribute', b'Deformer', b'Pose')}
    protected += [item for item in connections if item.props[1] in ids and item.props[2] in ids | {0}]
    return fingerprint(item for item in protected if item is not None)


def _without(node, excluded):
    from types import SimpleNamespace
    return SimpleNamespace(id=node.id, props=node.props, props_type=node.props_type,
                           elems=[item for item in node.elems if item.id not in excluded])


def _copy(node, encode):
    result = encode.FBXElem(node.id)
    for kind, value in zip(node.props_type, node.props):
        if kind not in _METHODS:
            raise ValueError('Unsupported FBX property type: ' + str(kind))
        getattr(result, _METHODS[kind])(value)
    result.elems = [_copy(item, encode) for item in node.elems]
    return result


def _array(node, name, values, method, encode):
    node.elems[:] = [item for item in node.elems if item.id != name]
    item = encode.FBXElem(name); getattr(item, method)(values); node.elems.append(item)


def _count(node, value, encode):
    node.props[:] = []; node.props_type[:] = []
    node.add_int32(value)


def _active_cluster(item):
    indices, weights = child(item, b'Indexes'), child(item, b'Weights')
    if indices is None and weights is None:
        return False  # Blender retains unused bone clusters without either array.
    if indices is None or weights is None:
        raise ValueError('A skin cluster has incomplete vertex weights.')
    if len(indices.props[0]) != len(weights.props[0]):
        raise ValueError('A skin cluster has mismatched vertex weights.')
    return any(weight != 0 for weight in weights.props[0])


def _endpoints(tree):
    records, connections = _records(tree)
    allowed = set(records) | {0}
    documents = child(tree, b'Documents')
    if documents:
        allowed.update(item.props[0] for item in documents.elems if item.id == b'Document')
    for item in connections:
        if item.props[1] not in allowed or item.props[2] not in allowed:
            raise ValueError('The FBX contains a dangling object connection.')


def _definitions(original, result, removed):
    before, after = child(original, b'Definitions'), child(result, b'Definitions')
    if before is None or after is None:
        raise ValueError('Missing FBX definitions.')
    before_types = {item.props[0]: child(item, b'Count').props[0] for item in before.elems if item.id == b'ObjectType'}
    after_types = {item.props[0]: child(item, b'Count').props[0] for item in after.elems if item.id == b'ObjectType'}
    if after_types != {name: count - removed[name] for name, count in before_types.items()}:
        raise ValueError('Invalid reduced FBX definition counts.')
    if child(after, b'Count').props[0] != child(before, b'Count').props[0] - sum(removed.values()):
        raise ValueError('Invalid reduced FBX total definition count.')


def reduce_file(path):
    """Rewrite only staging. Return reduction statistics after reparsed invariants pass."""
    started = time.perf_counter()
    parse, encode = _api()
    path = Path(path)
    original_bytes = path.stat().st_size
    original, version = parse.parse(str(path))
    if version not in (7400, 7500):
        raise ValueError('Unsupported FBX version for Lightweight export: ' + str(version))
    records, connections = _records(original)
    _endpoints(original)
    links = [item.props for item in connections]
    meshes = {uid: item for uid, item in records.items() if item.id == b'Geometry' and item.props[2] == b'Mesh'}
    shapes = {uid: item for uid, item in records.items() if item.id == b'Geometry' and item.props[2] == b'Shape'}
    representation = {uid for uid, item in records.items() if item.id in (b'Material', b'Texture', b'Video')}
    animation = {uid for uid, item in records.items() if item.id.startswith(b'Animation')}
    targets = {link[2] for link in links if link[1] in animation and link[2] not in animation}
    warnings = []
    unsupported = any(set(item.id for item in mesh.elems) - _MESH_DATA - _MESH_PROPERTIES for mesh in meshes.values())
    for mesh in meshes.values():
        vertices, polygons = child(mesh, b'Vertices'), child(mesh, b'PolygonVertexIndex')
        unsupported |= vertices is None or polygons is None or len(vertices.props[0]) < 9 or len(polygons.props[0]) < 3
    unsupported |= any(item.id == b'Geometry' and item.props[2] not in (b'Mesh', b'Shape') for item in records.values())
    # Animated representation or an unknown geometry structure is conservatively retained whole.
    unsafe_mesh_targets = False
    for uid, mesh in meshes.items():
        properties = child(mesh, b'Properties70')
        names = {item.props[0] for item in properties.elems if item.id == b'P'} if properties else set()
        unsafe_mesh_targets |= any(link[1] in animation and link[2] == uid and
                                   (len(link) < 4 or link[3] not in names) for link in links)
    if unsupported or unsafe_mesh_targets or targets & (representation | set(shapes)):
        warnings.append('Retained original geometry: this FBX contains an unsupported or directly animated representation.')
        return dict(originalBytes=original_bytes, bytes=original_bytes, meshes=0,
                    warnings=warnings, seconds=time.perf_counter()-started)
    for shape in shapes.values():
        if set(item.id for item in shape.elems) - {b'Properties70', b'Version', b'Indexes', b'Vertices', b'Normals'}:
            raise ValueError('Unsupported shape structure; the previous export was preserved.')
    root = _copy(original, encode)
    # Writer properties contain packed bytes rather than parsed scalar values.
    output_records = dict(zip(records, child(root, b'Objects').elems))
    cluster_ids = {uid for uid, item in records.items() if item.id == b'Deformer' and item.props[2] == b'Cluster'}
    skin_ids = {uid for uid, item in records.items() if item.id == b'Deformer' and item.props[2] == b'Skin'}
    seen_clusters = set()
    for uid in meshes:
        skins = {link[1] for link in links if link[0] == b'OO' and link[2] == uid and link[1] in skin_ids}
        if len(skins) > 1:
            raise ValueError('Multiple skins on one mesh are not supported by Lightweight export. Turn off Lightweight for this file.')
        clusters = [link[1] for link in links if link[0] == b'OO' and link[2] in skins and link[1] in cluster_ids]
        if seen_clusters.intersection(clusters):
            raise ValueError('A shared skin cluster cannot be reduced safely.')
        seen_clusters.update(clusters)
        vertices, polygons = [], []
        for cluster_uid in clusters:
            cluster = output_records[cluster_uid]
            active = _active_cluster(records[cluster_uid])
            if not active and child(records[cluster_uid], b'Indexes') is None:
                continue
            first = len(vertices)//3
            if active:
                vertices += [0., 0., 0., .01, 0., 0., 0., .01, 0.]
                polygons += [first, first+1, -(first+3)]
            _array(cluster, b'Indexes', [first, first+1, first+2] if active else [], 'add_int32_array', encode)
            _array(cluster, b'Weights', [1., 1., 1.] if active else [], 'add_float64_array', encode)
        if not vertices:
            vertices = [0., 0., 0., .01, 0., 0., 0., .01, 0.]
            polygons = [0, 1, -3]
        mesh = output_records[uid]
        mesh.elems[:] = [item for item in mesh.elems if item.id not in _MESH_DATA]
        _array(mesh, b'Vertices', vertices, 'add_float64_array', encode)
        _array(mesh, b'PolygonVertexIndex', polygons, 'add_int32_array', encode)
    for uid in shapes:
        shape = output_records[uid]
        _array(shape, b'Indexes', [0], 'add_int32_array', encode)
        _array(shape, b'Vertices', [.001, 0., 0.], 'add_float64_array', encode)
        if child(shape, b'Normals') is not None:
            _array(shape, b'Normals', [0., 0., 0.], 'add_float64_array', encode)
    objects = child(root, b'Objects')
    objects.elems[:] = [output_records[uid] for uid in records if uid not in representation]
    child(root, b'Connections').elems[:] = [_copy(item, encode) for item in connections
        if item.props[1] not in representation and item.props[2] not in representation]
    removed = Counter(records[uid].id for uid in representation)
    definitions = child(root, b'Definitions')
    if definitions is None:
        raise ValueError('Missing FBX definitions.')
    for previous, item in zip(child(original, b'Definitions').elems, definitions.elems):
        if item.id == b'ObjectType':
            count = child(item, b'Count')
            _count(count, child(previous, b'Count').props[0]-removed[previous.props[0]], encode)
    count = child(definitions, b'Count')
    _count(count, child(child(original, b'Definitions'), b'Count').props[0]-sum(removed.values()), encode)
    candidate = path.with_name(path.name + '.lightweight-tmp')
    try:
        encode.write(str(candidate), root, version)
        result, result_version = parse.parse(str(candidate))
        _endpoints(result); _definitions(original, result, removed)
        expected_links = [item for item in connections if item.props[1] not in representation and item.props[2] not in representation]
        if fingerprint(expected_links) != fingerprint(child(result, b'Connections').elems):
            raise ValueError('Lightweight validation detected changed hierarchy, skin or shape connections.')
        if result_version != version or animation_signature(original) != animation_signature(result):
            raise ValueError('Lightweight validation detected changed animation data.')
        if _protected_signature(original) != _protected_signature(result):
            raise ValueError('Lightweight validation detected changed rig, hierarchy or bind data.')
        result_records, _ = _records(result)
        if set(result_records) != set(records)-representation:
            raise ValueError('Lightweight validation detected changed object identities.')
        if any(_active_cluster(records[uid]) != _active_cluster(result_records[uid]) for uid in seen_clusters):
            raise ValueError('Lightweight validation detected changed bone influences.')
        if candidate.stat().st_size >= original_bytes:
            warnings.append('Retained original geometry: reducing this already small FBX would not save space.')
        else:
            os.replace(candidate, path)
    finally:
        candidate.unlink(missing_ok=True)
    return dict(originalBytes=original_bytes, bytes=path.stat().st_size, meshes=len(meshes),
                warnings=warnings, seconds=time.perf_counter()-started)
