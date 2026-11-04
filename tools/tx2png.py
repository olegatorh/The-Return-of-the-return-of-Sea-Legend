"""Convert Storm Engine .tx textures to PNG (pure Python, no dependencies).

.tx layout (little endian):
    u32 flags | u32 width | u32 height | u32 mip_count | u32/4cc format | u32 level0_size
    followed by mip levels, largest first.
format is either a FourCC (DXT1/DXT3/DXT5) or a D3DFORMAT number.

Usage: tx2png.py <in.tx|dir> <out.png|dir>
"""
import os
import struct
import sys
import zlib

D3DFMT = {21: 'A8R8G8B8', 22: 'X8R8G8B8', 23: 'R5G6B5', 25: 'A1R5G5B5', 26: 'A4R4G4B4'}


def write_png(path, w, h, rgba):
    """rgba: bytearray w*h*4"""
    raw = bytearray()
    stride = w * 4
    for y in range(h):
        raw.append(0)
        raw += rgba[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        c = struct.pack('>I', len(data)) + tag + data
        return c + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)

    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(bytes(raw), 6)) + chunk(b'IEND', b'')
    with open(path, 'wb') as f:
        f.write(png)


def c565(c):
    r = (c >> 11) & 31
    g = (c >> 5) & 63
    b = c & 31
    return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2))


def color_table(c0, c1, force4):
    a = c565(c0)
    b = c565(c1)
    if c0 > c1 or force4:
        c2 = tuple((2 * a[i] + b[i]) // 3 for i in range(3))
        c3 = tuple((a[i] + 2 * b[i]) // 3 for i in range(3))
        return [a + (255,), b + (255,), c2 + (255,), c3 + (255,)]
    c2 = tuple((a[i] + b[i]) // 2 for i in range(3))
    return [a + (255,), b + (255,), c2 + (255,), (0, 0, 0, 0)]


def decode_dxt(data, w, h, kind):
    bs = 8 if kind == 1 else 16
    out = bytearray(w * h * 4)
    bw = (w + 3) // 4
    bh = (h + 3) // 4
    pos = 0
    for by in range(bh):
        for bx in range(bw):
            blk = data[pos:pos + bs]
            pos += bs
            if len(blk) < bs:
                return out
            alpha = None
            cb = blk[bs - 8:]
            if kind == 3:
                bits = int.from_bytes(blk[:8], 'little')
                alpha = [((bits >> (4 * i)) & 15) * 17 for i in range(16)]
            elif kind == 5:
                a0, a1 = blk[0], blk[1]
                if a0 > a1:
                    tab = [a0, a1] + [((7 - i) * a0 + i * a1) // 7 for i in range(1, 7)]
                else:
                    tab = [a0, a1] + [((5 - i) * a0 + i * a1) // 5 for i in range(1, 5)] + [0, 255]
                bits = int.from_bytes(blk[2:8], 'little')
                alpha = [tab[(bits >> (3 * i)) & 7] for i in range(16)]
            c0, c1, idx = struct.unpack('<HHI', cb)
            tbl = color_table(c0, c1, kind != 1)
            for i in range(16):
                px = bx * 4 + (i & 3)
                py = by * 4 + (i >> 2)
                if px >= w or py >= h:
                    continue
                r, g, b, a = tbl[(idx >> (2 * i)) & 3]
                if alpha is not None:
                    a = alpha[i]
                o = (py * w + px) * 4
                out[o] = r; out[o + 1] = g; out[o + 2] = b; out[o + 3] = a
    return out


def decode_raw(data, w, h, fmt):
    out = bytearray(w * h * 4)
    n = w * h
    if fmt in (21, 22):
        for i in range(n):
            b, g, r, a = data[i * 4:i * 4 + 4]
            o = i * 4
            out[o] = r; out[o + 1] = g; out[o + 2] = b; out[o + 3] = a if fmt == 21 else 255
        return out
    for i in range(n):
        v = data[i * 2] | (data[i * 2 + 1] << 8)
        o = i * 4
        if fmt == 23:
            r, g, b = c565(v); a = 255
        elif fmt == 25:
            r = ((v >> 10) & 31) * 255 // 31; g = ((v >> 5) & 31) * 255 // 31; b = (v & 31) * 255 // 31
            a = 255 if v & 0x8000 else 0
        else:  # 26 A4R4G4B4
            a = ((v >> 12) & 15) * 17; r = ((v >> 8) & 15) * 17; g = ((v >> 4) & 15) * 17; b = (v & 15) * 17
        out[o] = r; out[o + 1] = g; out[o + 2] = b; out[o + 3] = a
    return out


def convert(src, dst):
    b = open(src, 'rb').read()
    flags, w, h, mips, fmt, size0 = struct.unpack('<IIII4sI', b[:24])
    body = b[24:]
    fmtn = struct.unpack('<I', fmt)[0]
    if fmt in (b'DXT1', b'DXT3', b'DXT5'):
        kind = int(fmt[3:4])
        rgba = decode_dxt(body, w, h, kind)
    elif fmtn in D3DFMT:
        rgba = decode_raw(body, w, h, fmtn)
    else:
        raise ValueError(f'unsupported format {fmt!r} flags={flags}')
    write_png(dst, w, h, rgba)
    return w, h


def main():
    src, dst = sys.argv[1], sys.argv[2]
    if os.path.isfile(src):
        print(convert(src, dst))
        return
    from multiprocessing import Pool
    jobs = []
    for dp, _, fs in os.walk(src):
        for f in fs:
            if f.lower().endswith('.tx'):
                s = os.path.join(dp, f)
                d = os.path.join(dst, os.path.relpath(s, src))
                d = d[:-3]
                if not d.lower().endswith('.png'):
                    d += '.png'
                os.makedirs(os.path.dirname(d), exist_ok=True)
                jobs.append((s, d))
    with Pool() as p:
        res = p.starmap(_safe, jobs, chunksize=8)
    bad = [(j[0], r) for j, r in zip(jobs, res) if r is not True]
    print(f'{len(jobs) - len(bad)}/{len(jobs)} ok')
    for s, e in bad[:30]:
        print('FAIL', s, e)


def _safe(s, d):
    try:
        convert(s, d)
        return True
    except Exception as e:  # noqa
        return str(e)


if __name__ == '__main__':
    main()
