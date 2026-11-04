"""List/extract the DLG_TEXT lines of dialog headers (merged addon tree) so they can be translated.
  extract_dialogs.py <PROGRAM dir> --stats                      count lines per dialog
  extract_dialogs.py <PROGRAM dir> --dump <name> [<name>...]    print "index<TAB>text" for the named dialogs (without .h)
Translations go to lang/<language>/dialogs/<name>.tsv with the same "index<TAB>text" format; indexes not listed stay Russian."""
import os, re, sys
root = sys.argv[1]
def dialogs():
    for dp, _, fs in os.walk(os.path.join(root, 'DIALOGS')):
        for f in sorted(fs):
            if f.lower().endswith('.h'):
                t = open(os.path.join(dp, f), 'rb').read().decode('cp1251', 'replace')
                m = re.search(r'DLG_TEXT\s*\[[^\]]*\]\s*=\s*\{(.*?)\}\s*;', t, re.S | re.I)
                if m:
                    yield f[:-2], re.findall(r'"([^"]*)"', m.group(1))
if '--stats' in sys.argv:
    rows = sorted(((len(i), n) for n, i in dialogs()), reverse=True)
    print('dialogs:', len(rows), 'lines:', sum(r[0] for r in rows))
    for c, n in rows[:25]: print(f'{c:5d}  {n}')
elif '--dump' in sys.argv:
    want = {a.lower() for a in sys.argv[sys.argv.index('--dump') + 1:]}
    for n, items in dialogs():
        if n.lower() in want:
            print('##', n)
            for i, s in enumerate(items): print(f'{i}\t{s}')
