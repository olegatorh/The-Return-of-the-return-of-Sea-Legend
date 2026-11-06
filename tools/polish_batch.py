"""Hand-translation workflow.
  polish_batch.py make <kind> <out> [start count]   dump "id<TAB>ru<TAB>old uk" of unique Russian sentences with that scan kind (missing|ru)
  polish_batch.py apply <batch dump> <answers>      answers = lines "id<TAB>ukrainian"; appended to lang/ukrainian/strings/_polish.tsv
"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import scan_lang as S
POL = os.path.join(S.SDIR, '_polish.tsv')
def uniq(kind):
    seen = {}; 
    for r in S.scan():
        if r['kind'] == kind and r['words'] >= 3 and r['ru'] not in seen: seen[r['ru']] = r
    return list(seen.values())
if sys.argv[1] == 'make':
    rows = uniq(sys.argv[2])
    a = int(sys.argv[4]) if len(sys.argv) > 4 else 0
    n = int(sys.argv[5]) if len(sys.argv) > 5 else len(rows)
    with open(sys.argv[3], 'w', encoding='utf-8') as f:
        for i, r in enumerate(rows[a:a + n], a):
            f.write(f"{i}\t{r['ru']}\t{r['uk'] or ''}\t[{r['file']}]\n")
    print(len(rows), 'total unique')
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
