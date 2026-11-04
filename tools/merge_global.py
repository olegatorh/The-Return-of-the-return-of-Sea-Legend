"""Build lang/<language>/strings/_global.tsv (the translation memory) from all per-file tsv files.
A text translated in one script file then also applies to every other file. Only COMPLETE sentences go in: fragments such as
"I wanted" + a gender suffix depend on the code that glues them together and stay in their own file.
The first file (alphabetically) wins on conflicts, conflicts are reported. Usage: merge_global.py <lang dir>/strings"""
import os
import re
import sys

d = sys.argv[1]
START = re.compile('[A-ZА-ЯЁ"\'(0-9—-]')
END = '.!?…)"\''


def complete(ru):
    return len(ru) >= 4 and ru == ru.rstrip() and ru[-1] in END and START.match(ru) is not None


mem, conflicts, skipped = {}, 0, 0
for f in sorted(os.listdir(d)):
    if not f.endswith('.tsv') or f.startswith('_'):
        continue
    for line in open(os.path.join(d, f), encoding='utf-8').read().split('\n'):
        if '\t' not in line:
            continue
        ru, uk = line.split('\t', 1)
        if not complete(ru):
            skipped += 1
            continue
        if ru in mem and mem[ru] != uk:
            conflicts += 1
        mem.setdefault(ru, uk)
with open(os.path.join(d, '_global.tsv'), 'w', encoding='utf-8') as out:
    for ru in sorted(mem):
        out.write(ru + '\t' + mem[ru] + '\n')
print(len(mem), 'sentences in the translation memory,', skipped, 'fragments left file-local,', conflicts, 'conflicting duplicates')
