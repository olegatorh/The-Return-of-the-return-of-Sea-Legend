"""Hand-translation workflow.
  polish_batch.py make <out> [start count]   dump "id<TAB>ru<TAB>old uk<TAB>[file]" of unique sentences that still look bad:
        scan kind 'missing', 'ru' (Russian words left) or a high share of words equal to the Russian source (untranslated pieces)
  polish_batch.py apply <batch dump> <answers>   answers = lines "id<TAB>ukrainian"; merged into lang/ukrainian/strings/_polish.tsv
Name pools (comma lists) are skipped."""
import sys, os, re
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import scan_lang as S
POL = os.path.join(S.SDIR, '_polish.tsv')
def overlap(r):
    a = set(w.lower() for w in re.findall(r'[А-Яа-яЁё]{4,}', r['ru']))
    b = [w.lower() for w in re.findall(r'[А-Яа-яЁёІіЇїЄє]{4,}', r['uk'])]
    return 0 if len(b) < 3 else sum(1 for w in b if w in a) / len(b)
def bad(r):
    if r['src'] == 'polish': return False
    return r['words'] >= 3 and not (r['ru'].count(',') >= 4 and len(r['ru'].split()) <= r['ru'].count(',') * 1.5 + 3) and not r['ru'].lower().startswith('voice') and \
        (r['kind'] in ('missing', 'ru', 'same') or (r['uk'] and overlap(r) >= 0.34))
def uniq():
    seen = {}
    for r in S.scan():
        if bad(r) and r['ru'] not in seen: seen[r['ru']] = r
    return list(seen.values())
if sys.argv[1] == 'make':
    rows = uniq()
    a = int(sys.argv[3]) if len(sys.argv) > 3 else 0
    n = int(sys.argv[4]) if len(sys.argv) > 4 else len(rows)
    with open(sys.argv[2], 'w', encoding='utf-8') as f:
        for i, r in enumerate(rows[a:a + n], a):
            f.write(f"{i}\t{r['ru']}\t{r['uk'] or ''}\t[{r['file']}]\n")
    print(len(rows), 'total unique')
elif sys.argv[1] == 'count':
    print(len(uniq()))
else:
    src = {}
    for l in open(sys.argv[2], encoding='utf-8').read().split('\n'):
        p = l.split('\t')
        if len(p) >= 2: src[p[0]] = p[1]
    ans = {}
    for l in open(sys.argv[3], encoding='utf-8').read().split('\n'):
        p = l.split('\t', 1)
        if len(p) == 2 and p[0] in src and p[1].strip(): ans[src[p[0]]] = p[1]
    pol = S.tsv(POL); pol.update(ans)
    with open(POL, 'w', encoding='utf-8', newline='\n') as f:
        for k, v in pol.items(): f.write(f"{k}\t{v}\n")
    print('applied', len(ans), 'total', len(pol))
