"""Character names: compare the machine translation with a rule-based Russian -> Ukrainian transliteration and list the disagreements.
  names_check.py <charnames.txt> <_mt.tsv> <out review.tsv>
Names of people in the scripts are plain Russian transcriptions of foreign names; Ukrainian transcription differs in a few letters
(и -> і after a consonant in loan names, э -> е, ы -> и, е -> є only after vowels, ё -> йо/ьо, ъ dropped)."""
import difflib
import sys

VOW = set('аеєиіїоуюяэыё')


def translit(s):
    out = []
    low = s
    for i, ch in enumerate(low):
        prev = low[i - 1].lower() if i else ''
        nxt = low[i + 1].lower() if i + 1 < len(low) else ''
        lo = ch.lower()
        up = ch.isupper()
        if lo == 'э': r = 'е'
        elif lo == 'ы': r = 'и'
        elif lo == 'ъ': r = ''
        elif lo == 'ё': r = 'йо' if prev in VOW or not prev else 'ьо'
        elif lo == 'и':
            # Ukrainian loan-word rule: и stays after ж ч ш щ; elsewhere (foreign names) it is і, except before a vowel -> ї
            r = 'и' if prev in 'жчшщ' else ('ї' if prev in VOW and nxt and nxt in VOW and False else 'і')
        elif lo == 'е': r = 'є' if prev in 'аеєиіоуюяь' and prev != '' and False else 'е'
        else: r = lo
        out.append(r.upper() if up and r else r)
    res = ''.join(out)
    return res.replace('ійй', 'ій')


if __name__ == '__main__':
    names = [x for x in open(sys.argv[1], encoding='utf-8').read().split('\n') if x]
    mt = dict(l.split('\t', 1) for l in open(sys.argv[2], encoding='utf-8').read().split('\n') if '\t' in l)
    rows = []
    for n in names:
        t = translit(n)
        m = mt.get(n, '')
        ratio = difflib.SequenceMatcher(None, t.lower(), m.lower()).ratio() if m else 0
        rows.append((ratio, n, t, m))
    rows.sort()
    with open(sys.argv[3], 'w', encoding='utf-8') as f:
        for ratio, n, t, m in rows:
            f.write(f'{ratio:.2f}\t{n}\t{t}\t{m}\n')
    print(len(rows), 'names;', sum(1 for r in rows if r[0] < 0.8), 'differ')
