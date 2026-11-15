import re, sys
t = open(sys.argv[1], encoding='utf-8-sig').read().splitlines()
cur = {}; rows = []
for l in t:
    m = re.match(r'\[script\] dump: (\w+) = (\S+)', l)
    if m:
        cur[m.group(1)] = m.group(2)
        if m.group(1) == 'boarding_location_type': rows.append(dict(cur)); cur = {}
    elif l.startswith('[location] model') and '_l:' in l: rows.append('--- ' + l[17:30])
last = None
for r in rows:
    if isinstance(r, str): print(r); continue
    s = ' '.join('%s=%s' % (k.replace('boarding_', ''), v) for k, v in r.items() if k != 'boarding_location_type')
    if s != last: print(s)
    last = s
