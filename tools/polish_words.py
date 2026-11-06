"""Whole-word fixes of Russian words the machine translation left in Ukrainian values (all strings/*.tsv, values only)."""
import glob, os, re, sys
HERE = os.path.dirname(os.path.abspath(__file__)); SD = os.path.join(os.path.dirname(HERE), 'lang', 'ukrainian', 'strings')
FIX = [(r'пиастров', 'піастрів'), (r'піастров\b', 'піастрів'), (r'пиастры', 'піастри'), (r'пиастрам', 'піастрам'), (r'пиастра', 'піастра'),
       (r'пиастр', 'піастр'), (r'Пиастров', 'Піастрів'), (r'Пиастр', 'Піастр'), (r'\bЗдравствуйте\b', 'Вітаю'), (r'\bздравствуйте\b', 'вітаю'),
       (r'\bстарика\b', 'старого'), (r'\bстарик\b', 'старий'), (r'\bСтарик\b', 'Старий'), (r'\bСпроси\b', 'Спитай'), (r'\bспроси\b', 'спитай'),
       (r'\bкрасавица\b', 'красуне'), (r'\bКрасавица\b', 'Красуне'), (r'\bпойдемте\b', 'ходімо'), (r'\bПойдемте\b', 'Ходімо'),
       (r'\bпойдите\b', 'ідіть'), (r'\bПойдите\b', 'Ідіть')]
tot = 0
for p in glob.glob(os.path.join(SD, '*.tsv')):
    if os.path.basename(p) == '_polish.tsv': continue
    lines = open(p, encoding='utf-8').read().split('\n'); ch = 0
    for i, l in enumerate(lines):
        t = l.find('\t')
        if t <= 0: continue
        k, v = l[:t], l[t + 1:]; nv = v
        for a, b in FIX: nv = re.sub(a, b, nv)
        if nv != v: lines[i] = k + '\t' + nv; ch += 1
    if ch: open(p, 'w', encoding='utf-8', newline='\n').write('\n'.join(lines)); tot += ch
print('values changed:', tot)
