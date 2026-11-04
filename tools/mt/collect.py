"""Collect every unique Cyrillic string literal of the merged addon scripts that has no manual translation yet.
Writes work/todo.tsv ("id<TAB>text") and prints what kinds of special content the strings contain.
Usage: collect.py <PROGRAM dir> <lang strings dir> <work dir>"""
import collections
import os
import re
import sys

root, strings_dir, work = sys.argv[1:4]
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
sys.argv = [sys.argv[0], root]
import extract_strings as ex   # noqa: E402

done = set()
# only layers that apply to EVERY script file count as done (per-file manual fragments do not apply elsewhere)
for f in ('_global.tsv', '_common.tsv', '_mt.tsv'):
    p = os.path.join(strings_dir, f)
    if os.path.exists(p):
        for line in open(p, encoding='utf-8').read().split('\n'):
            if '\t' in line:
                done.add(line.split('\t', 1)[0])

where = collections.defaultdict(set)
for dp, _, fs in os.walk(root):
    for f in fs:
        if f.lower().endswith(('.c', '.h')):
            p = os.path.join(dp, f)
            for s in ex.lits(p):
                where[s].add(os.path.relpath(p, root).replace('\\', '/').split('/')[0])
todo = sorted(s for s in where if s not in done)
os.makedirs(work, exist_ok=True)
with open(os.path.join(work, 'todo.tsv'), 'w', encoding='utf-8') as out:
    for i, s in enumerate(todo):
        out.write(f'{i}\t{s}\n')
print('unique Cyrillic strings:', len(where), '| already translated manually:', len(where) - len(todo), '| to translate:', len(todo))
kinds = collections.Counter()
for s in todo:
    if '\\n' in s: kinds['contains \\n'] += 1
    if re.search(r'%\d|%%|%[sdf]', s): kinds['% placeholder'] += 1
    if re.search(r'#[^#\s]+#', s): kinds['#tag#'] += 1
    if re.search(r'\\[^n]', s): kinds['other backslash'] += 1
    if re.fullmatch(r'[а-яё]{1,3}', s): kinds['tiny lowercase fragment'] += 1
    if s != s.strip(): kinds['leading/trailing space'] += 1
    if not re.search(r'[.!?…]\s*$', s.strip()) and len(s) > 25: kinds['long without sentence end (fragment?)'] += 1
    if re.search('[A-Za-z]{3,}', s): kinds['has Latin words'] += 1
print(dict(kinds))
tiny = sorted(s for s in todo if re.fullmatch(r'[а-яё]{1,4}', s))
print('tiny lowercase strings:', tiny)
areas = collections.Counter()
for s in todo: areas[sorted(where[s])[0]] += 1
print('by area:', dict(areas.most_common(8)))
