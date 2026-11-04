"""List the most frequent Cyrillic literals (how many script files contain them): translating these by hand has the biggest effect.
Usage: frequent.py <PROGRAM dir> <strings dir> [N]"""
import collections, os, sys
root, sdir = sys.argv[1:3]
top = int(sys.argv[3]) if len(sys.argv) > 3 else 150
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
sys.argv = [sys.argv[0], root]
import extract_strings as ex
manual = set()
for f in os.listdir(sdir):
    if f.endswith('.tsv') and not f.startswith('_mt'):
        for l in open(os.path.join(sdir, f), encoding='utf-8').read().split('\n'):
            if '\t' in l: manual.add(l.split('\t', 1)[0])
cnt = collections.Counter()
for dp, _, fs in os.walk(root):
    for f in fs:
        if f.lower().endswith(('.c', '.h')):
            for s in ex.lits(os.path.join(dp, f)): cnt[s] += 1
rows = [(c, s) for s, c in cnt.most_common() if s not in manual and len(s.strip()) > 3]
for i, (c, s) in enumerate(rows[:top]): print(f'{i}\t{c}\t{s}\u00b6')
print('files-weighted share of the top', top, ':', sum(c for c, _ in rows[:top]), 'of', sum(c for c, _ in rows))
