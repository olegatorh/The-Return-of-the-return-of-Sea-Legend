"""How much of the script text is translated, by layer (manual / machine / still Russian), per script area.
Usage: coverage.py <PROGRAM dir> <strings dir>"""
import collections
import os
import sys

root, d = sys.argv[1:3]
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.argv = ['x', root]
import extract_strings as ex  # noqa: E402


def load(n):
    m = {}
    p = os.path.join(d, n)
    if os.path.exists(p):
        for line in open(p, encoding='utf-8').read().split('\n'):
            if '\t' in line:
                a, b = line.split('\t', 1)
                m[a] = b
    return m


manual = {}
for f in os.listdir(d):
    if f.endswith('.tsv') and not f.startswith('_'):
        manual.update(load(f))
common, glob, mt = load('_common.tsv'), load('_global.tsv'), load('_mt.tsv')
tot = cov_m = cov_mt = 0
areas = collections.defaultdict(lambda: [0, 0, 0])
for dp, _, fs in os.walk(root):
    for f in fs:
        if f.lower().endswith(('.c', '.h')):
            area = os.path.relpath(os.path.join(dp, f), root).replace(chr(92), '/').split('/')[0]
            for s in ex.lits(os.path.join(dp, f)):
                tot += 1
                a = areas[area]
                a[0] += 1
                if s in manual or s in common or s in glob:
                    cov_m += 1; a[1] += 1
                elif s in mt:
                    cov_mt += 1; a[2] += 1
rest = tot - cov_m - cov_mt
print(f'Cyrillic strings (unique per file): {tot}; manual {cov_m} ({100*cov_m//tot}%); machine {cov_mt} ({100*cov_mt//tot}%); still Russian {rest} ({100*rest//tot}%)')
for k, (n, m, t) in sorted(areas.items(), key=lambda kv: -kv[1][0])[:7]:
    print(f'  {k:16s} {n:6d}  manual {m:5d}  machine {t:5d}  Russian {n-m-t:5d}')
