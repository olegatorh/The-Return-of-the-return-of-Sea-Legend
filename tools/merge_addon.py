"""Build the effective script tree of the addon: base PROGRAM overlaid with the files unpacked from stormex2.dll.
Usage: merge_addon.py <base PROGRAM> <unpacked addon PROGRAM> <out PROGRAM>"""
import os, shutil, sys
base, add, out = sys.argv[1:4]
if os.path.exists(out): shutil.rmtree(out)
shutil.copytree(base, out)
n = 0
for dp, _, fs in os.walk(add):
    for f in fs:
        src = os.path.join(dp, f)
        dst = os.path.join(out, os.path.relpath(src, add))
        # case-insensitive match with an existing file: overwrite that one so no duplicates differing in case appear
        d = os.path.dirname(dst)
        os.makedirs(d, exist_ok=True)
        for e in os.listdir(d):
            if e.lower() == f.lower() and e != f:
                os.remove(os.path.join(d, e))
        shutil.copyfile(src, dst); n += 1
print('overlaid', n, 'files ->', out)
