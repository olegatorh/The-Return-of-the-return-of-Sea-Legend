"""How much Russian text do the scripts contain? Counts Cyrillic string literals per area, total and unique (after
removing duplicates across all files), and what a translation memory would save. Usage: measure_text.py <PROGRAM dir>"""
import collections, os, re, sys
root = sys.argv[1]
LIT = re.compile(r'"([^"\n]*)"')
CYR = re.compile('[А-Яа-яЁё]')
def strip_comments(t):
    t = re.sub(r'/\*.*?\*/', '', t, flags=re.S)
    return '\n'.join(l.split('//')[0] if '//' in l and l.count('"') % 2 == 0 and not re.search(r'"[^"]*//[^"]*"', l) else l for l in t.split('\n'))
areas = collections.defaultdict(lambda: [0, 0, set()])   # area -> [literals, chars, unique set]
allu = collections.Counter()
for dp, _, fs in os.walk(root):
    for f in fs:
        if not f.lower().endswith(('.c', '.h')): continue
        p = os.path.join(dp, f)
        rel = os.path.relpath(p, root).replace('\\', '/')
        area = rel.split('/')[0] if '/' in rel else '<root>'
        t = strip_comments(open(p, 'rb').read().decode('cp1251', 'replace'))
        seen = set()
        for m in LIT.finditer(t):
            s = m.group(1)
            if CYR.search(s) and s not in seen:
                seen.add(s)
                a = areas[area]; a[0] += 1; a[1] += len(s); a[2].add(s); allu[s] += 1
tot_l = sum(a[0] for a in areas.values()); tot_c = sum(a[1] for a in areas.values())
uniq = len(allu); uniq_c = sum(len(s) for s in allu)
print(f'Cyrillic literals (unique per file): {tot_l}, {tot_c} chars')
print(f'Unique across all files: {uniq}, {uniq_c} chars  ({100*uniq_c//tot_c}% of the text)')
print('by area (literals / chars):')
for k, (n, c, u) in sorted(areas.items(), key=lambda kv: -kv[1][1])[:14]:
    print(f'  {k:18s} {n:6d} {c:8d}')
words = sum(len(s.split()) for s in allu)
print('unique words total:', words)
