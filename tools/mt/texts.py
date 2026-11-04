"""Machine-translate (ru -> uk) the game's text files (RESOURCE/INI/TEXTS/RUSSIAN/*.txt: "key {text}" entries) into
lang/ukrainian/texts/<file> (UTF-8, same format). Draft quality (NLLB-200 600M), to be polished by hand.

  texts.py <model dir> <russian texts dir> <out dir> <file> [<file> ...]

Entries that already exist in <out dir>/<file> are kept untouched (hand translations win) and are not re-translated.
#tags# and %-placeholders are masked with neutral tokens and checked after translation; an entry whose tokens did not survive stays Russian.
"""
import os
import re
import sys

import ctranslate2
from transformers import AutoTokenizer

model_dir, src_dir, out_dir = sys.argv[1:4]
files = sys.argv[4:]

tok = AutoTokenizer.from_pretrained(model_dir, src_lang='rus_Cyrl')
tr = ctranslate2.Translator(model_dir, device='cpu', compute_type='int8', inter_threads=2, intra_threads=max(1, (os.cpu_count() or 4) // 2))
CYR = re.compile('[А-Яа-яЁё]')
SENT = re.compile('(?<=[.!?…])\\s+(?=[A-ZА-ЯЁ"\'(—-])')
MASK = re.compile(r'#[^#\s]+#|%\d*[sdf]|%%')


def parse(path):
    """-> list of (key, value) in file order (key '' entries are skipped)."""
    text = open(path, encoding='cp1251', errors='replace').read().replace('\r', '')
    entries = []
    i, n = 0, len(text)
    prev_key = ''
    while i < n:
        j = text.find('{', i)
        if j < 0:
            break
        head = text[i:j]
        key = head.strip()
        if '\n' in key:                       # text between entries: the last non-empty line is the key
            key = [x for x in key.split('\n') if x.strip()][-1].strip()
        k = text.find('}', j)
        if k < 0:
            break
        value = text[j + 1:k]
        entries.append((key, value))
        i = k + 1
    return entries


def load_existing(path):
    if not os.path.exists(path):
        return {}
    text = open(path, encoding='utf-8').read().replace('\r', '')
    res = {}
    i, n = 0, len(text)
    while i < n:
        j = text.find('{', i)
        if j < 0:
            break
        key = text[i:j].strip()
        if '\n' in key:
            key = [x for x in key.split('\n') if x.strip()][-1].strip()
        k = text.find('}', j)
        if k < 0:
            break
        res[key] = text[j + 1:k]
        i = k + 1
    return res


def run(segments):
    src = [tok.convert_ids_to_tokens(tok.encode(s)) for s in segments]
    res = tr.translate_batch(src, target_prefix=[['ukr_Cyrl']] * len(src), beam_size=5, num_hypotheses=4, max_batch_size=1024,
                             length_penalty=1.2, batch_type='tokens', max_decoding_length=256)
    out = []
    for r in res:
        cands = [tok.decode(tok.convert_tokens_to_ids(h[1:]), skip_special_tokens=True).strip() for h in r.hypotheses]
        out.append(next((c for c in cands if not re.search('[ыэъёЫЭЪЁ]', c)), cands[0]))
    return out


def translate_value(value):
    """Translate one entry value keeping its layout; returns None if it should stay as is."""
    if not CYR.search(value):
        return None
    tags = []

    def mask(m):
        tags.append(m.group(0))
        return f'XQ{len(tags)}Q'

    masked = MASK.sub(mask, value)
    lines = masked.split('\n')
    segs = []
    for ln in lines:
        lead = re.match(r'^\s*', ln).group(0)
        body = ln.strip()
        if not body or not CYR.search(body):
            segs.append((lead, [], ''))
            continue
        trail = ln[len(lead) + len(body):]
        segs.append((lead, [x for x in SENT.split(body) if x.strip()] or [body], trail))
    flat = [s for _, parts, _ in segs for s in parts]
    outs = iter(run(flat) if flat else [])
    result = []
    for (lead, parts, trail), ln in zip(segs, lines):
        if not parts:
            result.append(ln)
            continue
        tr_parts = []
        for p in parts:
            o = next(outs)
            # keep sentence-final punctuation consistent with the source
            if not re.search(r'[.!?…:;»")]$', p) and re.search(r'[.]$', o):
                o = o[:-1]
            tr_parts.append(o)
        result.append(lead + ' '.join(tr_parts) + trail)
    out = '\n'.join(result)
    for idx, t in enumerate(tags, 1):
        if out.count(f'XQ{idx}Q') != 1:
            return None
        out = out.replace(f'XQ{idx}Q', t)
    if re.search('[ыэъёЫЭЪЁ]', out):
        return None
    return out


for name in files:
    entries = parse(os.path.join(src_dir, name))
    out_path = os.path.join(out_dir, name)
    existing = load_existing(out_path)
    if name == 'HeroDescribe.txt':        # only the hero descriptions: type/nation values are used by the scripts as they are
        entries = [(k, v) for k, v in entries if re.fullmatch(r'hero_\d+', k)]
    todo = [(k, v) for k, v in entries if k and k not in existing and CYR.search(v)]
    print(f'{name}: {len(entries)} entries, {len(existing)} translated already, {len(todo)} to do', flush=True)
    res = {}
    CH = 60
    for c in range(0, len(todo), CH):
        for k, v in todo[c:c + CH]:
            t = translate_value(v)
            if t is not None:
                res[k] = t
        print(f'  {min(c + CH, len(todo))}/{len(todo)}', flush=True)
    # write: keep existing entries first, then the draft ones, in source order
    lines = []
    for k, v in entries:
        val = existing.get(k, res.get(k))
        if val is None:
            continue
        if '\n' in val:
            lines.append(f'{k}\n{{{val}}}\n')
        else:
            lines.append(f'{k} {{{val}}}')
    os.makedirs(out_dir, exist_ok=True)
    open(out_path, 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
    print(f'  wrote {out_path}: {len(lines)} entries ({len(res)} machine-translated)', flush=True)
