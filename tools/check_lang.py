"""Sanity checks for a translation file: homoglyph mixing (Latin letters inside Cyrillic words), leftover Russian-only letters
(ы э ъ ё), and key coverage against the Russian original. Usage: check_lang.py <translated common.ini> <russian common.ini>"""
import re, sys
def rows(path, enc):
    out = {}
    for l in open(path, 'rb').read().decode(enc).split('\n'):
        m = re.match(r'\s*string\s*=\s*([^,]+),\s*"(.*)"\s*$', l.rstrip('\r'))
        if m: out[m.group(1).strip()] = m.group(2)
    return out
uk, ru = rows(sys.argv[1], 'utf-8'), rows(sys.argv[2], 'cp1251')
bad = 0
for k, v in uk.items():
    for w in re.findall(r'\w+', v):
        if re.search('[А-Яа-яІіЇїЄєҐґ]', w) and re.search('[A-Za-z]', w):
            print('MIXED ALPHABET:', k, '->', w); bad += 1
    if re.search('[ыэъЫЭЪёЁ]', v) and k not in ('lng Russian',):
        print('RUSSIAN-ONLY LETTER:', k, '->', v); bad += 1
cyr = [k for k, v in ru.items() if re.search('[А-Яа-яЁё]', v)]
missing = [k for k in cyr if k not in uk]
print(f'translated keys: {len(uk)}; Russian lines with Cyrillic: {len(cyr)}; not translated: {len(missing)}; problems: {bad}')
print('not translated sample:', missing[:10])
