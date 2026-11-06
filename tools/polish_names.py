"""Conservative spelling fix for name pools (Characters_names): names the machine translation left untouched get Ukrainian endings
(Моника -> Моніка, Надин -> Надін, Сабина -> Сабіна, Мария -> Марія), э/ы/ё/ъ are replaced. Result goes to _polish.tsv."""
import sys, os, re
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import scan_lang as S
RULES = [(r'ия$', 'ія'), (r'ин$', 'ін'), (r'ина$', 'іна'), (r'ика$', 'іка'), (r'ие$', 'іє'), (r'ий$', 'ій'),
         (r'э', 'е'), (r'Э', 'Е'), (r'ы', 'и'), (r'ё', 'йо'), (r'ъ', '')]
def fix(n):
    for a, b in RULES: n = re.sub(a, b, n)
    return n
pol = S.tsv(S.os.path.join(S.SDIR, '_polish.tsv'))
n = 0
for r in S.scan():
    if r['file'] != 'Characters_names' or not r['uk']: continue
    a = r['ru'].split(','); b = r['uk'].split(',')
    if len(a) != len(b): continue
    out = []
    for x, y in zip(a, b):
        if x.strip() == y.strip(): y = y.replace(y.strip(), fix(y.strip()))
        out.append(y)
    new = ','.join(out)
    if new != r['uk'] and r['ru'] not in pol:
        pol[r['ru']] = new; n += 1
with open(os.path.join(S.SDIR, '_polish.tsv'), 'w', encoding='utf-8', newline='\n') as f:
    for k, v in pol.items(): f.write(f"{k}\t{v}\n")
print('name pools fixed:', n)
