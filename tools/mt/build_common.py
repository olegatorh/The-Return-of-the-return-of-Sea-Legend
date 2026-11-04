"""Build lang/<language>/strings/_common.tsv (hand-translated frequent strings, applied to every script file)
from a numbered frequency list (frequent.py output) and a work file "index<TAB>translation". Usage: build_common.py <freq.tsv> <work file> <out tsv>"""
import re, sys
freq, work, out = sys.argv[1:4]
src = {}
for l in open(freq, encoding='utf-8').read().split('\n'):
    p = l.split('\t', 2)
    if len(p) == 3 and p[0].isdigit():
        src[int(p[0])] = p[2][:-1] if p[2].endswith('\u00b6') else p[2]
n = 0
with open(out, 'w', encoding='utf-8') as f:
    for l in open(work, encoding='utf-8').read().split('\n'):
        m = re.match(r'(\d+)\t(.*)$', l.rstrip('\r'))
        if not m: continue
        t = m.group(2)
        t = t[:-1] if t.endswith('\u00b6') else t
        ru = src[int(m.group(1))]
        if (ru != ru.strip()) != (t != t.strip()): print('whitespace mismatch at', m.group(1), repr(ru), repr(t))
        f.write(ru + '\t' + t + '\n'); n += 1
print(n, 'common strings ->', out)
