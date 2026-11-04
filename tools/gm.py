"""Reader for Storm Engine .gm geometry files (RDF version '1.05').

Layout (little endian, packed):
    HEAD(11 x i32 + 7 x f32) | names blob | name offsets[names] | textures | materials |
    lights | labels | objects | triangles | vertex buffer descriptors | vertex data | bsp
"""
import struct

HEAD = struct.Struct('<11i7f')
MATERIAL = struct.Struct('<2i4f4i4i')            # 56
LIGHT = struct.Struct('<4i4f3f3f3f3f')           # 80
LABEL = struct.Struct('<3i16f4i4f')              # 108
OBJECT = struct.Struct('<3i3ff i4i i8i4i i')     # 104
VERSION = 0x312E3035


class Gm:
    pass


def _name(blob, offs, idx):
    if idx < 0 or idx >= len(offs):
        return ''
    o = offs[idx]
    e = blob.index(b'\0', o)
    return blob[o:e].decode('cp1251', 'replace')


def parse(data):
    g = Gm()
    h = HEAD.unpack_from(data, 0)
    (g.version, g.flags, name_size, nnames, ntex, nmat, nlight, nlabel, nobj, ntri, nvb) = h[:11]
    g.bbox_size = h[11:14]
    g.bbox_center = h[14:17]
    g.radius = h[17]
    if g.version != VERSION:
        raise ValueError('bad version %x' % g.version)
    p = HEAD.size
    blob = data[p:p + name_size]
    p += name_size
    offs = list(struct.unpack_from('<%di' % nnames, data, p))
    p += 4 * nnames
    tex_names = struct.unpack_from('<%di' % ntex, data, p)
    p += 4 * ntex
    g.textures = [_name(blob, offs, i) for i in tex_names]

    g.materials = []
    for _ in range(nmat):
        m = MATERIAL.unpack_from(data, p)
        p += MATERIAL.size
        g.materials.append({
            'group': _name(blob, offs, m[0]), 'name': _name(blob, offs, m[1]),
            'diffuse': m[2], 'specular': m[3], 'gloss': m[4], 'selfillum': m[5],
            'texture_type': m[6:10], 'texture': m[10:14],
        })
    p += LIGHT.size * nlight

    g.labels = []
    for _ in range(nlabel):
        l = LABEL.unpack_from(data, p)
        p += LABEL.size
        g.labels.append({
            'group': _name(blob, offs, l[0]), 'name': _name(blob, offs, l[1]), 'flags': l[2],
            'm': l[3:19], 'bones': l[19:23], 'weight': l[23:27],
        })

    g.objects = []
    for _ in range(nobj):
        o = OBJECT.unpack_from(data, p)
        p += OBJECT.size
        g.objects.append({
            'group': _name(blob, offs, o[0]), 'name': _name(blob, offs, o[1]), 'flags': o[2],
            'center': o[3:6], 'radius': o[6], 'vertex_buff': o[7],
            'ntriangles': o[8], 'striangle': o[9], 'nvertices': o[10], 'svertex': o[11],
            'material': o[12], 'lights': o[13:21], 'bones': o[21:25], 'atriangles': o[25],
        })

    g.triangles = struct.unpack_from('<%dH' % (ntri * 3), data, p)
    p += 6 * ntri
    g.vbs = []
    for _ in range(nvb):
        t, s = struct.unpack_from('<2i', data, p)
        p += 8
        g.vbs.append({'type': t, 'size': s})
    for vb in g.vbs:
        vb['data'] = data[p:p + vb['size']]
        p += vb['size']
    g.tail = len(data) - p
    return g
