"""Tiny software renderer: shaded preview of a .gm file to PNG (sanity check only).
Usage: preview_gm.py <in.gm> <out.png> [yaw_deg] [pitch_deg]
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gm  # noqa: E402
from gm2gltf import read_vertices, STRIDE  # noqa: E402
from tx2png import write_png  # noqa: E402

W, H = 640, 480


def main():
    src, dst = sys.argv[1], sys.argv[2]
    yaw = math.radians(float(sys.argv[3]) if len(sys.argv) > 3 else 35)
    pitch = math.radians(float(sys.argv[4]) if len(sys.argv) > 4 else 20)
    g = gm.parse(open(src, 'rb').read())
    tris = []
    cache = {}
    for o in g.objects:
        vbi = o['vertex_buff']
        if vbi >= len(g.vbs) or g.vbs[vbi]['type'] not in STRIDE:
            continue
        vb = g.vbs[vbi]
        if vbi not in cache:
            cache[vbi] = read_vertices(vb, vb['size'] // STRIDE[vb['type']])[0]
        pos = cache[vbi]
        t = g.triangles[o['striangle'] * 3:(o['striangle'] + o['ntriangles']) * 3]
        for k in range(0, len(t), 3):
            tris.append(tuple(pos[o['svertex'] + t[k + j]] for j in range(3)))
    if not tris:
        print('no triangles'); return
    pts = [p for tr in tris for p in tr]
    cx = sum(p[0] for p in pts) / len(pts); cy = sum(p[1] for p in pts) / len(pts); cz = sum(p[2] for p in pts) / len(pts)
    r = max(math.dist(p, (cx, cy, cz)) for p in pts) or 1.0
    cyw, syw, cp, sp = math.cos(yaw), math.sin(yaw), math.cos(pitch), math.sin(pitch)

    def tf(p):
        x, y, z = p[0] - cx, p[1] - cy, p[2] - cz
        x, z = x * cyw + z * syw, -x * syw + z * cyw
        y, z = y * cp - z * sp, y * sp + z * cp
        return x, y, z

    scale = min(W, H) * 0.45 / r
    proj = []
    for tr in tris:
        v = [tf(p) for p in tr]
        # normal for shading
        ax, ay, az = (v[1][i] - v[0][i] for i in range(3))
        bx, by, bz = (v[2][i] - v[0][i] for i in range(3))
        nx, ny, nz = ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx
        ln = math.sqrt(nx * nx + ny * ny + nz * nz) or 1
        shade = abs(nz / ln) * 0.8 + 0.2
        depth = sum(p[2] for p in v) / 3
        sc = [(W / 2 + p[0] * scale, H / 2 - p[1] * scale) for p in v]
        proj.append((depth, sc, shade))
    proj.sort(key=lambda t: -t[0])
    buf = bytearray([30, 34, 44, 255] * (W * H))
    for depth, sc, shade in proj[:60000]:
        (x0, y0), (x1, y1), (x2, y2) = sc
        minx = max(int(min(x0, x1, x2)), 0); maxx = min(int(max(x0, x1, x2)) + 1, W - 1)
        miny = max(int(min(y0, y1, y2)), 0); maxy = min(int(max(y0, y1, y2)) + 1, H - 1)
        den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
        if abs(den) < 1e-9:
            continue
        c = int(40 + 200 * shade)
        for y in range(miny, maxy + 1):
            for x in range(minx, maxx + 1):
                a = ((y1 - y2) * (x - x2) + (x2 - x1) * (y - y2)) / den
                b = ((y2 - y0) * (x - x2) + (x0 - x2) * (y - y2)) / den
                if a >= 0 and b >= 0 and a + b <= 1:
                    o = (y * W + x) * 4
                    buf[o] = c; buf[o + 1] = c; buf[o + 2] = int(c * 0.9)
    write_png(dst, W, H, buf)
    print('triangles', len(tris), 'center', (round(cx, 2), round(cy, 2), round(cz, 2)), 'radius', round(r, 2))


if __name__ == '__main__':
    main()
