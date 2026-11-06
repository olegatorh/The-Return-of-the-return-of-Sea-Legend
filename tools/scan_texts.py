"""Scan the text-file layer (lang/ukrainian/texts) against RESOURCE/INI/TEXTS/RUSSIAN: keys still showing Russian.
  scan_texts.py [--list]   prints per-file counts of keys whose effective value is Russian (untranslated, or Russian-only letters)"""
import os, re, sys
HERE = os.path.dirname(os.path.abspath(__file__)); PORT = os.path.dirname(HERE); GAME = os.path.dirname(PORT)
BASE = os.path.join(GAME, 'RESOURCE', 'INI', 'TEXTS', 'RUSSIAN'); OVER = os.path.join(PORT, 'lang', 'ukrainian', 'texts')
sys.path.insert(0, HERE)
from scan_lang import russian_score
CYR = re.compile('[А-Яа-яЁё]')
LEX = None
def lexicon():
    global LEX
    if LEX is None:
        LEX = set()
        base = os.path.join(PORT, 'lang', 'ukrainian')
        for f in ['strings/_polish.tsv', 'strings/_common.tsv', 'strings/_common_extra.tsv', 'strings/_global.tsv']:
            for l in open(os.path.join(base, f), encoding='utf-8').read().split(chr(10)):
                t = l.split(chr(9), 1)
                if len(t) == 2: LEX.update(w.lower() for w in re.findall('[А-Яа-яІіЇїЄєҐґ]+', t[1]) if not re.search('[ыэъёЫЭЪЁ]', w))
    return LEX
def looks_ukrainian(v):
    """all Cyrillic words of an untranslated value are known Ukrainian words (cognates: Золото, Фрегат, Магазин...)"""
    ws = re.findall('[А-Яа-яЁёІіЇїЄєҐґ]+', v)
    return bool(ws) and all(w.lower() in lexicon() for w in ws) and not russian_score(v)
def parse(text, ini):
    d = {}
    if ini:
        for l in text.split('\n'):
            m = re.match(r'\s*string\s*=\s*([^,]+),\s*"(.*)"\s*$', l.rstrip('\r'))
            if m: d[m.group(1).strip()] = m.group(2)
        return d
    pos = 0
    while True:
        o = text.find('{', pos)
        if o < 0: break
        c = text.find('}', o + 1)
        if c < 0: break
        k = ' '.join(x.strip() for x in text[pos:o].split('\n') if x.strip() and not x.strip().startswith(';'))
        if k: d[k] = text[o + 1:c].strip()
        pos = c + 1
    return d
def quests(text):
    d = {}; q = key = ''; body = []
    def fl():
        if q and key: d.setdefault(q + '_' + key, '\n'.join(body).strip())
        body.clear()
    for l in text.split('\n'):
        l = l.rstrip('\r')
        if l.startswith('#QUEST'): fl(); q = l[6:].strip(); key = 'title'
        elif l.startswith('#TEXT'): fl(); key = l[5:].strip()
        else: body.append(l)
    fl(); return d
def scan():
    out = {}
    for f in sorted(os.listdir(BASE)):
        p = os.path.join(BASE, f)
        if not os.path.isfile(p): continue
        ini = f.lower().endswith('.ini')
        base = parse(open(p, 'rb').read().decode('cp1251', 'replace'), ini)
        if True:
            op = os.path.join(OVER, f)
            over = parse(open(op, encoding='utf-8').read(), ini) if os.path.exists(op) else {}
        if f == 'QuestBook.txt':
            qp = os.path.join(GAME, 'RESOURCE', 'QUESTSTEXT', 'quests_texts.txt')
            qb = quests(open(qp, 'rb').read().decode('cp1251', 'replace')) if os.path.exists(qp) else {}
            for k, v in qb.items(): base.setdefault(k, v)
        bad = []
        for k, v in base.items():
            if not CYR.search(v): continue
            t = over.get(k, v)
            if (t == v and not looks_ukrainian(v)) or russian_score(t) > 0: bad.append((k, v, t))
        out[f] = (len(base), bad)
    return out
if __name__ == '__main__':
    r = scan(); tot = 0
    for f, (n, bad) in r.items():
        print(f'{f:28s} keys {n:5d} russian {len(bad):4d}'); tot += len(bad)
        if '--list' in sys.argv:
            for k, v, t in bad: print('   ', k, '|', v[:90].replace('\n', ' '))
    print('total Russian keys:', tot)
