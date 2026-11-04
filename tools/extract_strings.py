"""Extract the Cyrillic string literals of script files (for translation by exact text).
  extract_strings.py <PROGRAM dir> --stats [<dir under DIALOGS>]      count Cyrillic literals per dialog file
  extract_strings.py <PROGRAM dir> --dump <relative file> ...         print "russian<TAB>" lines (unique, in file order)
Translations: lang/<language>/strings/<file name without extension>.tsv with lines "russian<TAB>translation"."""
import os, re, sys
root = sys.argv[1]
LIT = re.compile(r'"([^"\n]*)"')
CYR = re.compile('[А-Яа-яЁё]')
def lits(path):
    t = open(path, 'rb').read().decode('cp1251', 'replace')
    t = re.sub(r'/\*.*?\*/', '', t, flags=re.S)
    seen, out = set(), []
    for line in t.split('\n'):
        code = line.split('//')[0] if line.count('"') % 2 == 0 and '//' in line and not re.search(r'"[^"]*//[^"]*"', line) else line
        for m in LIT.finditer(code):
            s = m.group(1)
            if CYR.search(s) and s not in seen:
                seen.add(s); out.append(s)
    return out
if '--stats' in sys.argv:
    tot = 0; rows = []
    for dp, _, fs in os.walk(os.path.join(root, 'DIALOGS')):
        for f in fs:
            if f.lower().endswith('.c'):
                n = len(lits(os.path.join(dp, f))); tot += n
                rows.append((n, os.path.relpath(os.path.join(dp, f), root)))
    rows.sort(reverse=True)
    print('dialog .c files:', len(rows), 'unique Cyrillic literals (per file):', tot)
    for n, p in rows[:12]: print(f'{n:5d}  {p}')
elif '--dump' in sys.argv:
    for rel in sys.argv[sys.argv.index('--dump') + 1:]:
        print('##', rel)
        for i, s in enumerate(lits(os.path.join(root, rel))): print(f'{i}\t{s}¶')   # the pilcrow marks the end, so trailing spaces are visible
