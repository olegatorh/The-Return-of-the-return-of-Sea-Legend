"""Static scan of the Ukrainian layer against the script string literals.
  scan_lang.py [--list KIND] [--json out]
Replicates LanguageService.LiteralText priority: _polish.tsv > per-file > _global/_common/_common_extra > _mt.
Kinds: missing (no translation), same (translation == source), ru (Russian-only letters / Russian words left), all.
Prints counts; with --list prints the entries."""
import os, re, sys, json
HERE = os.path.dirname(os.path.abspath(__file__))
PORT = os.path.dirname(HERE)
PROG = os.path.join(PORT, 'addon', 'merged', 'PROGRAM')
SDIR = os.path.join(PORT, 'lang', 'ukrainian', 'strings')
CYR = re.compile('[А-Яа-яЁё]')
LIT = re.compile(r'"([^"\n]*)"')
RU_LETTERS = re.compile('[ыэъЫЭЪёЁ]')
# Russian words that must not survive in Ukrainian text (whole-word, lower-case match)
RU_WORDS = set('''и но что это как он она они мы вы ты   еще или если когда только тоже очень
спроси красавица пойдите пойди     хорошо   деньги капитан корабль город человек всё
меня тебя его её их мне мной тобой свой мой твой   
здесь где куда откуда почему зачем потому чтобы чтоб был была были есть будет будешь
    можно нужно надо должен может могу можешь хочешь пожалуйста спасибо   
ладно слушай слушайте смотри дело дела дней ночь время года лет год нет
вы       тебе ну пойдемте идите идти иди возьми возьмите   сейчас теперь потом тогда
сегодня   вчера сюда туда оттуда всегда никогда иногда много   сколько какой какая какие такой такая
который которая которые которое этот эта эти этого этой этому для без при про над под через между'''.split())
# words that coincide in both languages: not counted
SHARED = set('''и на не при про над под через между для без ну вы вам вас тебе ми ви ти'''.split())

def read(p):
    return open(p, encoding='utf-8').read().split('\n')

def tsv(p):
    d = {}
    if os.path.exists(p):
        for l in read(p):
            t = l.find('\t')
            if t > 0: d[l[:t]] = l[t + 1:]
    return d

def literals(path):
    t = open(path, 'rb').read().decode('cp1251', 'replace')
    t = re.sub(r'/\*.*?\*/', '', t, flags=re.S)
    out = []
    for line in t.split('\n'):
        code = line.split('//')[0] if line.count('"') % 2 == 0 and '//' in line and not re.search(r'"[^"]*//[^"]*"', line) else line
        for m in LIT.finditer(code):
            if CYR.search(m.group(1)): out.append(m.group(1))
    return out

def load_layers():
    g = {}
    for n in ('_mt.tsv',): g.update(tsv(os.path.join(SDIR, n)))
    glob = {}
    for n in ('_global.tsv', '_common.tsv', '_common_extra.tsv'): glob.update(tsv(os.path.join(SDIR, n)))
    pol = tsv(os.path.join(SDIR, '_polish.tsv'))
    return g, glob, pol

def effective(file, ru, layers):
    mt, glob, pol = layers
    if ru in pol: return pol[ru], 'polish'
    per = {}
    for s in ('.tsv', '.h.tsv'): per.update(tsv(os.path.join(SDIR, file + s)))
    if ru in per: return per[ru], 'file'
    if ru in glob: return glob[ru], 'global'
    if ru in mt: return mt[ru], 'mt'
    return None, 'none'

def russian_score(t):
    """number of Russian-only letters + known Russian words in a translated string"""
    n = len(RU_LETTERS.findall(t))
    for w in re.findall(r'[А-Яа-яЁё]+', t):
        wl = w.lower()
        if wl in RU_WORDS and wl not in SHARED: n += 1
    return n

def scan():
    layers = load_layers()
    cache = {}
    res = []
    seen = set()
    for dp, _, fs in os.walk(PROG):
        for f in fs:
            if not f.lower().endswith(('.c', '.h')): continue
            base = os.path.splitext(f)[0]
            if f.lower().endswith('.h'): base = os.path.splitext(f)[0]
            for ru in literals(os.path.join(dp, f)):
                k = (base, ru)
                if k in seen: continue
                seen.add(k)
                tr, src = effective(base, ru, layers)
                # short fragments (gender endings, single words) are not sentences
                words = len(re.findall(r'[А-Яа-яЁёA-Za-z]+', ru))
                kind = 'ok'
                if ru.lower().startswith('voice'): continue   # voice file names are not text
                if tr is None: kind = 'missing'
                elif tr == ru: kind = 'same'
                elif russian_score(tr) > 0 and src != 'polish': kind = 'ru'
                res.append(dict(file=base, ru=ru, uk=tr, src=src, kind=kind, words=words))
    return res


# ---- heuristic report of suspicious machine-translated fragments (scan_lang.py --suspicious [N]) ----
SUS_WORDS = set(w.lower() for w in """что это который которая которые которое чтобы чтоб спроси спросить красавица пойдіть пойдите пойди
всего всё хорошо сейчас только очень тоже теперь здесь сюда туда почему зачем потому нужно надо можно деньги пожалуйста спасибо
згоднаен доберему теснити заодно вобще вообще наверное конечно кстати ладно слушай слушайте смотри смотрите 
потом тогда сегодня вчера всегда никогда иногда много сколько какой какая какие такой такая этот эта эти этого
привет здравствуйте извините простите проходи проходите садись садитесь""".split())
UKL = "А-Яа-яІіЇїЄєҐґ'"
SUS_RE = [
    (re.compile('[ыэъёЫЭЪЁ]'), 'ru-letters'),
    (re.compile(r"\b(?:щоб|можна|треба|потрібно|хочу|хочеш|хоче|можу|можеш|може|повинен|повинна|мусиш|мушу|став|почав|почала|встиг|вирішив|збираюсь|збираєшся|йти|вміє|вмію|вмієш|бажаєш|бажаю)\s+(?:не\s+)?[" + UKL + r"]+(?:ать|ять|еть|ить|ыть)\b"), 'ru-infinitive'),
    (re.compile(r"\b(?:Всего|Спасибо|Пожалуйста|Здравствуй|Извини|Дорогой|Милый|Дорогая)\b"), 'ru-phrase'),
    (re.compile('[А-Яа-яІіЇїЄє][A-Za-z]|[A-Za-z][А-Яа-яІіЇїЄє]'), 'mixed-script'),
    (re.compile(r"\b[" + UKL + r"]{4,}(?:аен|ыен|оен|еен)\b"), 'junk-ending'),
    (re.compile(r"\b([" + UKL + r"]{3,}) \1\b", re.I), 'repeat'),
    (re.compile(r"\b(?:я|ти|він|вона|ми|ви|вони)\s+(?:є|був|була|були)\s+(?:не\s+)?(?:ты|мы|вы)\b"), 'ru-pron'),
    (re.compile(r"\b(?:Всього|Вcього)\s+добре\b|\bдоберу\b"), 'known-slip'),
]
def suspicious(uk):
    """list of flag names for a Ukrainian string (empty = nothing suspicious)"""
    f = []
    for w in re.findall("[" + UKL + "ЁёЫыЭэЪъ]+", uk):
        if w.lower() in SUS_WORDS and w.lower() not in SHARED: f.append('ru-word:' + w.lower()); break
    uk = uk.replace('\\n', ' ')
    for rx, name in SUS_RE:
        if rx.search(uk): f.append(name)
    return f

def suspicious_report(res, lst=0, srcs=('mt',)):
    from collections import Counter
    tot = Counter(); n = 0; bad = []
    for r in res:
        if r['src'] not in srcs or not r['uk']: continue
        n += 1
        fl = suspicious(r['uk'])
        if fl:
            bad.append((r, fl))
            for x in set(k.split(':')[0] for k in fl): tot[x] += 1
    print(f'machine-translated literals in use: {n}, suspicious: {len(bad)}', dict(tot))
    for r, fl in bad[:lst]:
        print(f"{r['file']}\t{','.join(fl)}\t{r['ru']}\t{r['uk']}")
    return len(bad)

if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    res = scan()
    if '--suspicious' in sys.argv:
        i = sys.argv.index('--suspicious')
        suspicious_report(res, int(sys.argv[i + 1]) if len(sys.argv) > i + 1 and sys.argv[i + 1].isdigit() else 0)
        sys.exit(0)
    from collections import Counter
    c = Counter(r['kind'] for r in res)
    sent = Counter(r['kind'] for r in res if r['words'] >= 3)
    print('literals:', len(res), dict(c))
    print('sentences (>=3 words):', dict(sent))
    if '--list' in sys.argv:
        kind = sys.argv[sys.argv.index('--list') + 1]
        minw = 3
        for r in res:
            if (kind == 'all' or r['kind'] == kind) and r['words'] >= minw:
                print(f"{r['file']}\t{r['ru']}\t{r['uk']}")
    if '--json' in sys.argv:
        json.dump(res, open(sys.argv[sys.argv.index('--json') + 1], 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
