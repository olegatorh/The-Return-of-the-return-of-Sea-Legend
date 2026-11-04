"""Build lang/<language>/strings/<script>.tsv from a work file of "index<TAB>translation" lines.
Usage: build_strings.py <PROGRAM dir> <relative script> <work file> <out tsv>
The Russian side is taken from the script itself (same extraction as extract_strings.py), so keys always match exactly.
Checks: every index exists; "%1"-style and #...# placeholders must be identical in source and translation."""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
root, rel, work, out = sys.argv[1:5]
sys.argv = [sys.argv[0], root]            # extract_strings reads argv[1] as the program dir
import extract_strings as ex
src = ex.lits(os.path.join(root, rel))
tr = {}
for n, line in enumerate(open(work, encoding='utf-8').read().split('\n'), 1):
    line = line.rstrip('\r')
    if not line.strip(): continue
    m = re.match(r'(\d+)\t(.*)$', line)
    if not m: print(f'line {n}: bad format'); continue
    i = int(m.group(1)); t = m.group(2)
    if t.endswith('\u00b6'): t = t[:-1]                       # optional end marker
    if i >= len(src): print(f'line {n}: index {i} out of range ({len(src)})'); continue
    ph = lambda s: sorted(re.findall(r'%\d|#[^#\s]+#|\n', s))
    if ph(src[i]) != ph(t): print(f'index {i}: placeholders differ: {ph(src[i])} vs {ph(t)}')
    tr[i] = t
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(out, 'w', encoding='utf-8') as f:
    for i in sorted(tr): f.write(src[i] + '\t' + tr[i] + '\n')
print(f'{len(tr)} of {len(src)} literals translated -> {out}')
