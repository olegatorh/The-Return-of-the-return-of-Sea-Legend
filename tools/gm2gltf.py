"""Convert Storm .gm models to glTF 2.0 (.gltf + .bin), pure Python.

Static vertex types (0, 1) and skinned type 4 (exported in bind pose, no skin yet).
Storm/D3D is left-handed: z is negated on export (this also flips triangle winding to CCW).

Usage: gm2gltf.py <in.gm|dir> <out.gltf|dir> [textures_dir_rel]
"""
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gm  # noqa: E402

STRIDE = {0: 36, 1: 44, 4: 44}


def read_vertices(vb, nverts):
    """returns lists pos, norm, uv for a vertex buffer"""
    t = vb['type']
    d = vb['data']
    stride = STRIDE[t]
    pos, nrm, uv = [], [], []
    for i in range(nverts):
        o = i * stride
        if t == 4:   # pos, weight, boneid, norm, color, uv
            px, py, pz = struct.unpack_from('<3f', d, o)
            nx, ny, nz = struct.unpack_from('<3f', d, o + 20)
            u, v = struct.unpack_from('<2f', d, o + 36)
        else:        # pos, norm, color, uv0 [, uv1]
            px, py, pz, nx, ny, nz = struct.unpack_from('<6f', d, o)
            u, v = struct.unpack_from('<2f', d, o + 28)
        pos.append((px, py, -pz))
        nrm.append((nx, ny, -nz))
        uv.append((u, v))
    return pos, nrm, uv


def convert(src, dst, tex_dir='textures'):
    g = gm.parse(open(src, 'rb').read())
    bin_ = bytearray()
    views, accs, prims_by_obj = [], [], []

    def add_view(blob, target):
        while len(bin_) % 4:
            bin_.append(0)
        views.append({'buffer': 0, 'byteOffset': len(bin_), 'byteLength': len(blob), 'target': target})
        bin_.extend(blob)
        return len(views) - 1

    cache = {}
    materials, images, textures = [], [], []
    mat_index = {}

    def get_material(mi):
        if mi in mat_index:
            return mat_index[mi]
        m = g.materials[mi] if 0 <= mi < len(g.materials) else None
        entry = {'name': m['name'] if m else 'default', 'doubleSided': True,
                 'pbrMetallicRoughness': {'metallicFactor': 0.0, 'roughnessFactor': 1.0}}
        if m:
            ti = m['texture'][0]
            if 0 <= ti < len(g.textures) and g.textures[ti]:
                uri = tex_dir + '/' + g.textures[ti] + '.png'
                images.append({'uri': uri})
                textures.append({'source': len(images) - 1})
                entry['pbrMetallicRoughness']['baseColorTexture'] = {'index': len(textures) - 1}
            entry['extras'] = {'texture': [g.textures[i] for i in m['texture'] if 0 <= i < len(g.textures)]}
        materials.append(entry)
        mat_index[mi] = len(materials) - 1
        return mat_index[mi]

    nodes, meshes = [], []
    for oi, o in enumerate(g.objects):
        vbi = o['vertex_buff']
        if vbi >= len(g.vbs) or g.vbs[vbi]['type'] not in STRIDE or o['ntriangles'] == 0:
            continue
        vb = g.vbs[vbi]
        total = vb['size'] // STRIDE[vb['type']]
        if vbi not in cache:
            cache[vbi] = read_vertices(vb, total)
        pos, nrm, uv = cache[vbi]
        tri = g.triangles[o['striangle'] * 3:(o['striangle'] + o['ntriangles']) * 3]
        idx = [o['svertex'] + i for i in tri]
        if max(idx) >= total:
            raise ValueError(f'index out of range obj {oi}')
        # compact vertex range used by this object
        lo, hi = o['svertex'], o['svertex'] + o['nvertices']
        if hi > total:
            hi = total
        p = pos[lo:hi]; n = nrm[lo:hi]; t = uv[lo:hi]
        idx = [i - lo for i in idx]
        if min(idx) < 0 or max(idx) >= len(p):
            lo2 = min(idx) + lo
            raise ValueError(f'index range mismatch obj {oi}')
        pb = b''.join(struct.pack('<3f', *x) for x in p)
        nb = b''.join(struct.pack('<3f', *x) for x in n)
        tb = b''.join(struct.pack('<2f', *x) for x in t)
        ib = struct.pack('<%dI' % len(idx), *idx)
        mn = [min(c[k] for c in p) for k in range(3)]
        mx = [max(c[k] for c in p) for k in range(3)]
        vp = add_view(pb, 34962); accs.append({'bufferView': vp, 'componentType': 5126, 'count': len(p), 'type': 'VEC3', 'min': mn, 'max': mx}); a_p = len(accs) - 1
        vn = add_view(nb, 34962); accs.append({'bufferView': vn, 'componentType': 5126, 'count': len(n), 'type': 'VEC3'}); a_n = len(accs) - 1
        vt = add_view(tb, 34962); accs.append({'bufferView': vt, 'componentType': 5126, 'count': len(t), 'type': 'VEC2'}); a_t = len(accs) - 1
        vi = add_view(ib, 34963); accs.append({'bufferView': vi, 'componentType': 5125, 'count': len(idx), 'type': 'SCALAR'}); a_i = len(accs) - 1
        meshes.append({'name': o['name'] or f'obj{oi}', 'primitives': [{
            'attributes': {'POSITION': a_p, 'NORMAL': a_n, 'TEXCOORD_0': a_t},
            'indices': a_i, 'material': get_material(o['material'])}]})
        nodes.append({'name': o['name'] or f'obj{oi}', 'mesh': len(meshes) - 1,
                      'extras': {'group': o['group'], 'flags': o['flags']}})

    # locators / labels as empty nodes (matrix is row-major 4x4 in D3D convention)
    for lb in g.labels:
        m = lb['m']
        # position row 3; negate z for handedness
        nodes.append({'name': lb['name'], 'translation': [m[12], m[13], -m[14]],
                      'extras': {'group': lb['group'], 'locator': True}})

    doc = {'asset': {'version': '2.0', 'generator': 'seadogs port gm2gltf'},
           'scene': 0, 'scenes': [{'nodes': list(range(len(nodes)))}], 'nodes': nodes,
           'meshes': meshes, 'materials': materials, 'images': images, 'textures': textures,
           'accessors': accs, 'bufferViews': views,
           'buffers': [{'uri': os.path.basename(dst)[:-5] + '.bin', 'byteLength': len(bin_)}]}
    if not images: doc.pop('images'); doc.pop('textures')
    if not materials: doc.pop('materials')
    os.makedirs(os.path.dirname(os.path.abspath(dst)), exist_ok=True)
    with open(dst, 'w') as f:
        json.dump(doc, f)
    with open(dst[:-5] + '.bin', 'wb') as f:
        f.write(bin_)
    return len(meshes), sum(len(a) for a in [g.triangles]) // 3


def _safe(a):
    s, d, td = a
    try:
        return convert(s, d, td) and True
    except Exception as e:  # noqa
        return f'{type(e).__name__}: {e}'


def main():
    src, dst = sys.argv[1], sys.argv[2]
    td = sys.argv[3] if len(sys.argv) > 3 else 'textures'
    if os.path.isfile(src):
        print(convert(src, dst, td))
        return
    from multiprocessing import Pool
    jobs = []
    for dp, _, fs in os.walk(src):
        for f in fs:
            if f.lower().endswith('.gm'):
                s = os.path.join(dp, f)
                rel = os.path.relpath(s, src)
                depth = rel.count(os.sep)
                jobs.append((s, os.path.join(dst, rel[:-3] + '.gltf'), '../' * (depth + 1) + 'textures'))
    with Pool() as p:
        res = p.map(_safe, jobs, chunksize=16)
    bad = [(j[0], r) for j, r in zip(jobs, res) if r is not True]
    print(f'{len(jobs) - len(bad)}/{len(jobs)} ok')
    for s, e in bad[:25]:
        print('FAIL', os.path.relpath(s, src), e)


if __name__ == '__main__':
    main()
