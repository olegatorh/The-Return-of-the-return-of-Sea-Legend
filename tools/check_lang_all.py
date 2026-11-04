"""Scan every translated file under lang/<language> for homoglyph mixing (Latin letters inside Cyrillic words),
Russian-only letters (ы э ъ ё) and stray accent marks. Usage: check_lang_all.py <lang dir>"""
import os, re, sys
bad = 0
files = 0
for dp, _, fs in os.walk(sys.argv[1]):
    for f in fs:
        p = os.path.join(dp, f); files += 1
        for n, line in enumerate(open(p, encoding='utf-8').read().split(chr(10)), 1):
            line = line.replace(chr(92) + 'n', ' ')      # the literal two-character "\n" line-break marker
            if os.sep + 'strings' + os.sep in p and chr(9) in line:
                line = line.split(chr(9), 1)[1]          # "russian<TAB>translation": check only the translation
            # ignore the alphabet lines and script placeholders like %1, #ddmg_min#
            for w in re.findall(r'[^\W\d_]+', line):
                if re.search('[А-Яа-яІіЇїЄєҐґ]', w) and re.search('[A-Za-z]', w):
                    print(f'{p}:{n}: mixed alphabet in word {w!r}'); bad += 1
            if re.search('[ыэъЫЭЪёЁ]', line) and 'alphabet' not in line and 'lng Russian' not in line:
                print(f'{p}:{n}: Russian-only letter: {line[:80]!r}'); bad += 1
            if '\u0301' in line or '´' in line:
                print(f'{p}:{n}: stray accent mark'); bad += 1
print(f'{files} files checked, {bad} problems')
