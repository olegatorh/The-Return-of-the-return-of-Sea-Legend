"""Unpack the script archive that the addon "Return of the Sea Legend" hides inside stormex2.dll.

Layout (found by analysis, not documented):
  * a normal PE file (code 9 KB) followed by an overlay
  * overlay = zlib streams, one per file, back to back, starting at the end of the PE (0x3c00 here)
  * after the last stream: the index, to the end of the file; each entry is
        u32 name_length | name (cp1251/ascii, backslash paths) | u64 uncompressed_size | u64 offset_of_zlib_stream

Usage: unpack_stormex2.py <stormex2.dll> <out dir> [--list]
"""
import os
import struct
import sys
import zlib


def find_index_start(d, first_stream):
    """The index follows the last zlib stream: walk the streams until one fails."""
    off = first_stream
    while off < len(d) - 2 and d[off] == 0x78 and d[off + 1] in (0x01, 0x5E, 0x9C, 0xDA):
        o = zlib.decompressobj()
        try:
            o.decompress(d[off:])
        except zlib.error:
            break
        off = len(d) - len(o.unused_data)
    return off


def parse_index(d, p):
    entries = []
    while p + 4 <= len(d):
        (n,) = struct.unpack_from('<I', d, p)
        if n == 0 or n > 400 or p + 4 + n + 16 > len(d):
            break
        name = d[p + 4:p + 4 + n].decode('cp1251')
        size, off = struct.unpack_from('<QQ', d, p + 4 + n)
        entries.append((name, size, off))
        p += 4 + n + 16
    return entries, p


def main():
    dll, out = sys.argv[1], sys.argv[2]
    d = open(dll, 'rb').read()
    pe = struct.unpack_from('<I', d, 0x3C)[0]
    nsec = struct.unpack_from('<H', d, pe + 6)[0]
    opt = struct.unpack_from('<H', d, pe + 20)[0]
    sec = pe + 24 + opt
    first = max(struct.unpack_from('<I', d, sec + 40 * i + 16)[0] + struct.unpack_from('<I', d, sec + 40 * i + 20)[0] for i in range(nsec))
    idx = find_index_start(d, first)
    entries, end = parse_index(d, idx)
    print(f'overlay at {first:#x}, index at {idx:#x}, {len(entries)} entries, parsed to {end:#x} of {len(d):#x}')
    bad = 0
    total = 0
    for name, size, off in entries:
        try:
            data = zlib.decompressobj().decompress(d[off:])
        except zlib.error:
            data = b''
        if len(data) != size:
            bad += 1
            print('  size mismatch:', name, size, len(data))
        total += len(data)
        if '--list' not in sys.argv:
            path = os.path.join(out, *name.split(chr(92)))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, 'wb') as f:
                f.write(data)
    print(f'{len(entries) - bad}/{len(entries)} entries consistent, {total} bytes of script source')


if __name__ == '__main__':
    main()
