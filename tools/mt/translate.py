"""Machine-translate (ru -> uk) the strings in work/todo.tsv with NLLB-200 (CTranslate2, int8, CPU).

  translate.py <model dir> <work dir> [--limit N] [--sample N] [--out name]

Reads work/todo.tsv ("id<TAB>text"), appends "id<TAB>translation" to work/<out>.tsv (resumable) and writes the
rejected ones with a reason to work/<out>.rejected.tsv. The fragments the game glues together get special care:
  * leading/trailing punctuation and spaces of the source are re-applied to the output
  * a fragment that starts in lower case keeps a lower-case start
  * '\\n' markers split the text; strings with %-placeholders, #tags# or other backslashes are skipped (stay Russian)
  * tiny lower-case pieces (gender suffixes such as "ась") are skipped
"""
import os
import random
import re
import sys
import time

import ctranslate2
from transformers import AutoTokenizer

model_dir, work = sys.argv[1], sys.argv[2]
opt = {a: sys.argv[i + 1] for i, a in enumerate(sys.argv) if a.startswith('--') and i + 1 < len(sys.argv)}
out_name = opt.get('--out', 'mt_out')

todo = []
for line in open(os.path.join(work, opt.get('--todo', 'todo.tsv')), encoding='utf-8').read().split('\n'):
    if '\t' in line:
        i, s = line.split('\t', 1)
        todo.append((int(i), s))
if '--sample' in opt:
    random.Random(7).shuffle(todo)
    todo = todo[:int(opt['--sample'])]
elif '--limit' in opt:
    todo = todo[:int(opt['--limit'])]

out_path = os.path.join(work, out_name + '.tsv')
done = set()
if os.path.exists(out_path):
    for line in open(out_path, encoding='utf-8').read().split('\n'):
        if '\t' in line:
            done.add(int(line.split('\t', 1)[0]))
rej_path = os.path.join(work, out_name + '.rejected.tsv')

CYR = re.compile('[А-Яа-яЁё]')
LEAD = re.compile(r'^[\s.,!?…:;\-—"\'(]*')
TRAIL = re.compile(r'[\s.,!?…:;\-—"\')]*$')


def skip_reason(s):
    if re.search(r'%\d|%%|%[sdf]', s): return 'placeholder'
    if re.search(r'#[^#\s]+#', s): return 'tag'
    if re.search(r'\\[^n]', s): return 'backslash'
    if re.fullmatch(r'[а-яё]{1,4}', s): return 'tiny fragment'
    if not CYR.search(s): return 'no cyrillic'
    return None


tok = AutoTokenizer.from_pretrained(model_dir, src_lang='rus_Cyrl')
tr = ctranslate2.Translator(model_dir, device='cpu', compute_type='int8', inter_threads=2, intra_threads=max(1, (os.cpu_count() or 4) // 2))


def run(segments, beam=5):
    """segments: list of core texts (no leading/trailing punctuation). Returns translations."""
    src = [tok.convert_ids_to_tokens(tok.encode(s)) for s in segments]
    nbest = int(opt.get('--nbest', 1))
    res = tr.translate_batch(src, target_prefix=[['ukr_Cyrl']] * len(src), beam_size=max(beam, nbest), num_hypotheses=nbest, max_batch_size=2048, length_penalty=1.2,
                             batch_type='tokens', max_decoding_length=256)
    out = []
    for r in res:
        cands = [tok.decode(tok.convert_tokens_to_ids(h[1:]), skip_special_tokens=True).strip() for h in r.hypotheses]
        # prefer the best hypothesis that has no Russian-only letters
        out.append(next((c for c in cands if not re.search('[ыэъёЫЭЪЁ]', c)), cands[0]))
    return out


SENT = re.compile('(?<=[.!?…])\\s+(?=[A-ZА-ЯЁ"\'(—-])')


def sentences(part):
    """Long inputs make NLLB drop sentences, so every sentence is translated on its own."""
    return [x for x in SENT.split(part) if x.strip()] or [part]


def split(s):
    """-> (lead, [core segments separated by \\n], trail)"""
    lead = LEAD.match(s).group(0)
    rest = s[len(lead):]
    trail = TRAIL.search(rest).group(0) if rest else ''
    core = rest[:len(rest) - len(trail)] if trail else rest
    return lead, core, trail


def fix_ends(src_core, mt, lead, trail):
    if not mt:
        return None
    # continuation fragments keep a lower-case start
    first = next((c for c in src_core if c.isalpha()), '')
    if first.islower() and mt[0].isupper() and not (len(mt) > 1 and mt[1].isupper()):
        mt = mt[0].lower() + mt[1:]
    mt = re.sub(r'[.,!?…:;\s]+$', '', mt) if trail.strip() else mt
    return lead + mt + trail


items = [(i, s) for i, s in todo if i not in done]
work_items, rejected = [], []
for i, s in items:
    r = skip_reason(s)
    if r:
        rejected.append((i, r, s))
    else:
        work_items.append((i, s))
print(f'{len(items)} strings pending, {len(work_items)} translatable, {len(rejected)} skipped by rule; cpu={os.cpu_count()}', flush=True)

# translate by length so batches are homogeneous
prepared = []
for i, s in work_items:
    lead, core, trail = split(s)
    parts = [p for p in re.split(r'\\n', core)]
    prepared.append((i, s, lead, parts, trail))
t0 = time.time()
CH = 400
order = sorted(range(len(prepared)), key=lambda k: sum(len(p) for p in prepared[k][3]))
n_ok = 0
with open(out_path, 'a', encoding='utf-8') as fout, open(rej_path, 'a', encoding='utf-8') as frej:
    for i, r, s in rejected:
        frej.write(f'{i}\t{r}\t{s}\n')
    for c in range(0, len(order), CH):
        chunk = [prepared[k] for k in order[c:c + CH]]
        flat = [x for _, _, _, parts, _ in chunk for p in parts if p.strip() for x in sentences(p)]
        res = iter(run(flat) if flat else [])
        for i, s, lead, parts, trail in chunk:
            outs = []
            for p in parts:
                outs.append(' '.join(next(res) for _ in sentences(p)) if p.strip() else p)
            text = '\\n'.join(fix_ends(p, o, '', '') or o for p, o in zip(parts, outs))
            final = fix_ends(parts[0] if parts else '', text, lead, trail)
            core_len = max(1, sum(len(p) for p in parts))
            reason = None
            if not final: reason = 'empty'
            elif re.search('[ыэъёЫЭЪЁ]', final): reason = 'russian letters'
            elif len(text) / core_len > 3.2 or len(text) / core_len < 0.3: reason = 'length ratio'
            elif re.search('[A-Za-z]{4,}', text) and not re.search('[A-Za-z]{4,}', s): reason = 'latin garbage'
            if reason:
                frej.write(f'{i}\t{reason}\t{s}\n')
            else:
                fout.write(f'{i}\t{final}\n'); n_ok += 1
        fout.flush(); frej.flush()
        el = time.time() - t0
        print(f'  {min(c + CH, len(order))}/{len(order)} done, {el:.0f}s, {n_ok} accepted', flush=True)
print('finished:', n_ok, 'accepted of', len(work_items), 'translatable;', f'{time.time() - t0:.0f}s')
