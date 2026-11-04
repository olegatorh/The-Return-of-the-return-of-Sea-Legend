"""Turn the machine translation output into lang/<language>/strings/_mt.tsv ("russian<TAB>ukrainian").
It is the lowest-priority fallback of LanguageService: any manual translation (per file, or in _global.tsv / _common.tsv) wins.
Several passes can exist; each pass has its own todo file (ids differ between them):
    (todo.tsv, mt_out.tsv)    first full run
    (todo.tsv, mt_retry.tsv)  second try of the rejected ones (n-best, no Russian letters)
    (todo_b.tsv, mt_b.tsv)    pieces that were translated by hand in one file but are needed everywhere
Usage: apply.py <work dir> <strings dir>"""
import os
import sys

work, sdir = sys.argv[1:3]
PASSES = [('todo.tsv', 'mt_out.tsv'), ('todo.tsv', 'mt_retry.tsv'), ('todo_b.tsv', 'mt_b.tsv')]
mem = {}
for todo, out in PASSES:
    tp, op = os.path.join(work, todo), os.path.join(work, out)
    if not (os.path.exists(tp) and os.path.exists(op)):
        continue
    src = {}
    for line in open(tp, encoding='utf-8').read().split('\n'):
        if '\t' in line:
            i, s = line.split('\t', 1)
            src[int(i)] = s
    for line in open(op, encoding='utf-8').read().split('\n'):
        if '\t' in line:
            i, t = line.split('\t', 1)
            ru = src[int(i)]
            if t and t != ru and '\t' not in ru and '\t' not in t:
                # the model ends single words and fragments with a full stop the source does not have
                core = ru.rstrip()
                if core and core[-1] not in '.!?…:;,)"\'' and t.rstrip().endswith('.'):
                    t = t.rstrip()[:-1] + t[len(t.rstrip()):]
                mem.setdefault(ru, t)
with open(os.path.join(sdir, '_mt.tsv'), 'w', encoding='utf-8') as f:
    for ru in sorted(mem):
        f.write(ru + '\t' + mem[ru] + '\n')
print(len(mem), 'machine translations written to', os.path.join(sdir, '_mt.tsv'))
