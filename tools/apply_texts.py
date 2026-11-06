"""Append hand translations to the text-file layer: apply_texts.py <answers>   lines "file<TAB>key<TAB>text"
(literal \n in the text = line break for .txt files; .ini keeps it as is). Later entries override earlier ones in the loader."""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__)); OVER = os.path.join(os.path.dirname(HERE), 'lang', 'ukrainian', 'texts')
by = {}
for l in open(sys.argv[1], encoding='utf-8').read().split('\n'):
    p = l.split('\t')
    if len(p) >= 3: by.setdefault(p[0], []).append((p[1], '\t'.join(p[2:])))
for f, items in by.items():
    path = os.path.join(OVER, f)
    t = open(path, encoding='utf-8').read() if os.path.exists(path) else ''
    if t and not t.endswith('\n'): t += '\n'
    for k, v in items:
        if f.endswith('.ini'): t += f'string = {k},"{v}"\n'
        else: t += f'{k}\n{{\n{v.replace(chr(92) + "n", chr(10))}\n}}\n\n'
    open(path, 'w', encoding='utf-8', newline='\n').write(t)
    print(f, len(items))
