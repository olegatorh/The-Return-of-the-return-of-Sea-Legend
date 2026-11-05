#!/usr/bin/env python3
"""Engine contract audit: what the original scripts expect from the engine vs what the port implements.

Static scan of
  * the script tree (port/addon/merged/PROGRAM): #define chains, functions, CreateEntity/FindClass, SendMessage,
    SetEventHandler/#event_handler, Event/PostEvent, GetEventData() order in the handlers, native calls;
  * the port (StormScript/Runtime/*.cs + game/*.cs): Builtins/HostFunctions, EntityHandlers[...] and the message codes /
    sub-command strings reachable from each handler (call-graph closure), FireEvent/PostEventInternal + argument types;
  * the open-source Storm Engine (port/reference/storm-engine, optional): CREATE_CLASS, ProcessMessage case labels,
    core.Event/PostEvent names + format strings, IntFuncTable / SetScriptFunction natives with their arity;
  * runtime evidence: game/smoke_*.log(.err), reports/*/log_tail.txt ([events], [nohandler], [unresolved], 'not found').

Writes docs/CONTRACTS.md and docs/contracts.json. Python 3 stdlib only. Always exits 0 (it is a report, not a gate).
Usage: python tools/contract_audit.py [--program DIR] [--game DIR] [--ref DIR] [--md FILE] [--json FILE] [--top N]
"""
import argparse
import ast
import bisect
import collections
import datetime
import glob
import json
import math
import os
import re
import sys

PORT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
IDENT = re.compile(r'[A-Za-z_][A-Za-z0-9_]*')

# ----------------------------------------------------------------------------------------------------------------------
# text helpers


def read_text(path):
    data = open(path, 'rb').read()
    for enc in ('utf-8', 'cp1251'):
        try:
            return data.decode(enc)
        except UnicodeDecodeError:
            pass
    return data.decode('latin-1')


class Src:
    """A source file: original text, a 'blanked' copy (comments -> spaces, string contents -> spaces, quotes kept)
    with identical offsets, the string literals with their offsets, and a line index."""

    def __init__(self, path, rel, text, lang):
        self.path, self.rel, self.text = path, rel, text
        self.lang = lang
        self.code, self.strings = blank(text, lang)
        self.nl = [i for i, c in enumerate(text) if c == '\n']
        self.str_at = {s: v for s, e, v in self.strings}

    def line(self, pos):
        return bisect.bisect_left(self.nl, pos) + 1

    def site(self, pos):
        return '%s:%d' % (self.rel, self.line(pos))

    def lit(self, start, end):
        """The string literal whose opening quote is the first non-blank char of code[start:end] (None if not a literal)."""
        seg = self.code[start:end]
        m = re.match(r'\s*@?\$?"', seg)
        if not m:
            return None
        q = start + m.end() - 1
        if self.code[end - 1:end] != '"' and not re.search(r'"\s*$', seg):
            return None
        return self.str_at.get(q)


def blank(text, lang):
    """Comments -> spaces, string/char contents -> spaces (newlines kept). Returns (code, [(qstart, qend, value)]).
    lang: 'storm' (no escapes, string ends at newline), 'c' (C/C++ escapes, char literals), 'cs' (C#: verbatim and
    interpolated strings; the code inside {holes} of $"..." stays code)."""
    out = list(text)
    strings = []
    n = len(text)
    i = 0

    def wipe(a, b):
        for k in range(a, b):
            if out[k] != '\n':
                out[k] = ' '

    def cs_string(i):
        """i at the opening quote (after @/$ prefixes). Returns index after the closing quote."""
        j = i - 1
        verbatim = interp = False
        while j >= 0 and text[j] in '@$':
            verbatim |= text[j] == '@'
            interp |= text[j] == '$'
            j -= 1
        k = i + 1
        buf = []
        while k < n:
            c = text[k]
            if c == '"':
                if verbatim and k + 1 < n and text[k + 1] == '"':
                    buf.append('"')
                    out[k] = out[k + 1] = ' '
                    k += 2
                    continue
                break
            if c == '\\' and not verbatim:
                buf.append(text[k:k + 2])
                out[k] = ' '
                if k + 1 < n and text[k + 1] != '\n':
                    out[k + 1] = ' '
                k += 2
                continue
            if interp and c == '{':
                if k + 1 < n and text[k + 1] == '{':
                    out[k] = out[k + 1] = ' '
                    k += 2
                    continue
                # code hole: scan to the matching brace, recursing into nested strings
                depth = 1
                k += 1
                while k < n and depth:
                    ch = text[k]
                    if ch == '"':
                        k = cs_string(k)
                        continue
                    if ch == '{':
                        depth += 1
                    elif ch == '}':
                        depth -= 1
                        if depth == 0:
                            break
                    k += 1
                buf.append('{}')
                k += 1
                continue
            if c == '\n' and not verbatim:
                break
            buf.append(c)
            if c != '\n':
                out[k] = ' '
            k += 1
        strings.append((i, k, ''.join(buf)))
        return k + 1

    while i < n:
        c = text[i]
        if c == '/' and i + 1 < n and text[i + 1] == '/':
            j = text.find('\n', i)
            j = n if j < 0 else j
            wipe(i, j)
            i = j
            continue
        if c == '/' and i + 1 < n and text[i + 1] == '*':
            j = text.find('*/', i + 2)
            j = n if j < 0 else j + 2
            wipe(i, j)
            i = j
            continue
        if c == '"':
            if lang == 'cs':
                i = cs_string(i)
                continue
            k = i + 1
            buf = []
            while k < n and text[k] != '"' and text[k] != '\n':
                if lang == 'c' and text[k] == '\\' and k + 1 < n:
                    buf.append(text[k:k + 2])
                    out[k] = ' '
                    if text[k + 1] != '\n':
                        out[k + 1] = ' '
                    k += 2
                    continue
                buf.append(text[k])
                out[k] = ' '
                k += 1
            strings.append((i, k, ''.join(buf)))
            i = k + 1
            continue
        if c == "'" and lang in ('c', 'cs'):
            k = i + 1
            while k < n and text[k] != "'" and text[k] != '\n':
                k += 2 if text[k] == '\\' else 1
            wipe(i + 1, min(k, n))
            i = k + 1
            continue
        i += 1
    return ''.join(out), strings


def match_close(code, i, open_c='(', close_c=')'):
    """code[i] == open_c; returns index of the matching close (or len(code))."""
    depth = 0
    pairs = {'(': ')', '[': ']', '{': '}'}
    n = len(code)
    k = i
    while k < n:
        c = code[k]
        if c in pairs:
            depth += 1
        elif c in ')]}':
            depth -= 1
            if depth == 0:
                return k
        k += 1
    return n


def split_args(code, open_pos):
    """Top-level comma split of the call whose '(' is at open_pos. Returns ([(start, end)], close_pos)."""
    close = match_close(code, open_pos)
    args = []
    depth = 0
    start = open_pos + 1
    for k in range(open_pos + 1, close):
        c = code[k]
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
        elif c == ',' and depth == 0:
            args.append((start, k))
            start = k + 1
    if code[start:close].strip() or args:
        args.append((start, close))
    return args, close


def load_tree(root, exts, lang, relbase=None):
    out = []
    for dp, dn, fn in os.walk(root):
        dn[:] = [d for d in dn if d not in ('bin', 'obj', '.godot', '.git')]
        for f in fn:
            if os.path.splitext(f)[1].lower() in exts:
                p = os.path.join(dp, f)
                rel = os.path.relpath(p, relbase or root).replace('\\', '/')
                out.append(Src(p, rel, read_text(p), lang))
    out.sort(key=lambda s: s.rel.lower())
    return out


# ----------------------------------------------------------------------------------------------------------------------
# #define tables (case-sensitive) + safe evaluation


class Defines:
    def __init__(self):
        self.raw = {}           # name -> [(value text, site)]
        self.cache = {}

    def add(self, name, value, site):
        self.raw.setdefault(name, []).append((value.strip(), site))

    def scan(self, src):
        for m in re.finditer(r'(?m)^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)(?![(\w])[ \t]*([^\n]*)', src.code):
            # value from the original text (string literals were blanked in .code)
            v0, v1 = m.start(2), m.end(2)
            self.add(m.group(1), src.text[v0:v1] if '"' in src.code[v0:v1] else src.code[v0:v1], src.site(m.start()))

    def value(self, name, depth=0):
        """Resolved value: int, float, str, or None. Uses the first definition (they are global in the original)."""
        if name in self.cache:
            return self.cache[name]
        if name not in self.raw or depth > 20:
            return None
        self.cache[name] = None          # cycle guard
        v = self.eval(self.raw[name][0][0], depth + 1)
        self.cache[name] = v
        return v

    def eval(self, expr, depth=0):
        expr = re.sub(r'//.*', '', expr).strip()
        if not expr:
            return None
        m = re.fullmatch(r'"([^"\n]*)"', expr)
        if m:
            return m.group(1)
        m = re.fullmatch(r'[A-Za-z_]\w*', expr)
        if m:
            return self.value(expr, depth + 1)
        # arithmetic: substitute identifiers, then evaluate with a restricted AST walker
        bad = []

        def sub(mm):
            v = self.value(mm.group(0), depth + 1)
            if isinstance(v, (int, float)):
                return repr(v)
            bad.append(mm.group(0))
            return '0'
        e2 = re.sub(r'(?<![\w.])[A-Za-z_]\w*', sub, expr)
        e2 = re.sub(r'(?<=\d)[uUlLfF]+\b', '', e2)
        if bad:
            return None
        try:
            return safe_eval(ast.parse(e2, mode='eval').body)
        except Exception:
            return None


def safe_eval(n):
    if isinstance(n, ast.Constant) and isinstance(n.value, (int, float)):
        return n.value
    if isinstance(n, ast.UnaryOp):
        v = safe_eval(n.operand)
        return {ast.USub: lambda x: -x, ast.UAdd: lambda x: x, ast.Invert: lambda x: ~x}[type(n.op)](v)
    if isinstance(n, ast.BinOp):
        a, b = safe_eval(n.left), safe_eval(n.right)
        ops = {ast.Add: lambda: a + b, ast.Sub: lambda: a - b, ast.Mult: lambda: a * b,
               ast.Div: lambda: a // b if isinstance(a, int) and isinstance(b, int) else a / b,
               ast.Mod: lambda: a % b, ast.LShift: lambda: a << b, ast.RShift: lambda: a >> b,
               ast.BitOr: lambda: a | b, ast.BitAnd: lambda: a & b, ast.BitXor: lambda: a ^ b}
        return ops[type(n.op)]()
    raise ValueError('unsupported')


# ----------------------------------------------------------------------------------------------------------------------
# script model

STORM_KEYWORDS = {'if', 'while', 'for', 'switch', 'return', 'case', 'else', 'do', 'sizeof', 'int', 'float', 'string',
                  'bool', 'ref', 'aref', 'object', 'void', 'native', 'extern'}
TYPE_CODE = {'int': 'l', 'bool': 'l', 'float': 'f', 'string': 's', 'aref': 'a', 'ref': 'a', 'object': 'a'}
DEBUG_FILES = re.compile(r'(?i)(^|/)(debuger|test|test1|test_seg1|test_seg2|memtest|s|ps|tr|lcorrect|teleport|locators_test|boal_debug\d*|InitInterface|StartInterface)\.c$')


class Func:
    def __init__(self, src, name, ret, params, hdr, body_open, body_close):
        self.src, self.name, self.ret, self.params = src, name, ret, params
        self.hdr, self.open, self.close = hdr, body_open, body_close


class Scripts:
    def __init__(self, root):
        self.root = root
        self.files = load_tree(root, {'.c', '.h'}, 'storm')
        self.defs = Defines()
        for s in self.files:
            self.defs.scan(s)
        self.funcs = collections.defaultdict(list)      # lower name -> [Func]
        self.natives = {}                               # lower name -> (decl text, nargs, site)
        self.globals = {}                               # lower global variable -> type
        for s in self.files:
            self.parse_funcs(s)

    def parse_funcs(self, s):
        code = s.code
        depth = 0
        last = 0                         # start of the current top-level statement
        i, n = 0, len(code)
        while i < n:
            c = code[i]
            if c == '{':
                if depth == 0:
                    hdr = code[last:i]
                    m = re.search(r'([A-Za-z_]\w*)\s+([A-Za-z_]\w*)\s*\(([^()]*)\)\s*$', hdr)
                    if m and m.group(1).lower() in TYPE_CODE.keys() | {'void'}:
                        close = match_close(code, i, '{', '}')
                        params = []
                        for p in m.group(3).split(','):
                            pm = re.match(r'\s*(\w+)\s*&?\s*(\w+)', p)
                            if pm:
                                params.append((pm.group(1).lower(), pm.group(2)))
                        f = Func(s, m.group(2), m.group(1).lower(), params, last + m.start(), i, close)
                        self.funcs[f.name.lower()].append(f)
                        i = close + 1
                        last = i
                        continue
                depth += 1
            elif c == '}':
                depth = max(0, depth - 1)
                if depth == 0:
                    last = i + 1
            elif c == ';' and depth == 0:
                stmt = code[last:i]
                m = re.search(r'(?:^|\s)native\s+(\w+)\s+([A-Za-z_]\w*)\s*\(([^()]*)\)\s*$', stmt)
                if m:
                    ps = [p for p in m.group(3).split(',') if p.strip()]
                    self.natives[m.group(2).lower()] = (re.sub(r'\s+', ' ', stmt.strip()), len(ps), s.site(last))
                gm = re.match(r'\s*(int|float|string|bool|ref|aref|object)\s+([A-Za-z_]\w*)\s*(?:\[|=|,|$)', stmt)
                if gm:
                    self.globals.setdefault(gm.group(2).lower(), gm.group(1))
                last = i + 1
            elif c == '\n' and depth == 0 and code[last:i].lstrip().startswith('#'):
                last = i + 1
            i += 1

    def resolve(self, s, a, b):
        """Value of an argument expression: str literal, define (str/int), int literal; else None."""
        lit = s.lit(a, b)
        if lit is not None:
            return lit
        e = s.code[a:b].strip()
        if re.fullmatch(r'-?\d+', e):
            return int(e)
        if re.fullmatch(r'[A-Za-z_]\w*', e):
            return self.defs.value(e)
        return self.defs.eval(e) if re.fullmatch(r'[\w\s+\-*/|&()<>]+', e) else None

    def calls(self, name_rx):
        """All call sites of functions whose name matches name_rx (case-insensitive): (src, name, open_pos, args)."""
        rx = re.compile(r'(?<![\w.])(' + name_rx + r')\s*\(', re.I)
        for s in self.files:
            for m in rx.finditer(s.code):
                op = m.end() - 1
                args, close = split_args(s.code, op)
                yield s, m.group(1), m.start(), args

    def func_at(self, s, pos):
        if not hasattr(self, '_by_file'):
            self._by_file = collections.defaultdict(list)
            for fl in self.funcs.values():
                for f in fl:
                    self._by_file[f.src.rel].append(f)
        for f in self._by_file.get(s.rel, []):
            if f.open <= pos <= f.close:
                return f
        return None

    def call_index(self):
        """lower function name -> [(src, pos, args)] for every call in the tree (built once)."""
        if not hasattr(self, '_calls'):
            self._calls = collections.defaultdict(list)
            for s, nm, pos, args in self.calls(r'[A-Za-z_]\w*'):
                self._calls[nm.lower()].append((s, pos, args))
        return self._calls

    def param_targets(self, s, pos, var, depth=0):
        """When `var` is a parameter of the function around pos: the normalized argument expressions the callers pass
        for it (followed through further parameters up to 3 levels)."""
        f = self.func_at(s, pos)
        if not f or depth > 3:
            return []
        idx = next((i for i, (t, n) in enumerate(f.params) if n.lower() == var), None)
        if idx is None:
            return []
        out = []
        for cs, cpos, args in self.call_index().get(f.name.lower(), []):
            if idx < len(args):
                t = norm_target(cs.code[args[idx][0]:args[idx][1]])
                if re.fullmatch(r'[\w.]+', t):
                    out.append(t)
                    out += self.param_targets(cs, cpos, t, depth + 1)
        return out


def norm_target(expr):
    """'&Locations[i]' -> 'locations', 'chr' -> 'chr', '&objs[n].x' -> 'objs.x'"""
    e = expr.strip().lstrip('&').strip()
    e = re.sub(r'\[[^\]]*\]', '', e)
    return re.sub(r'\s+', '', e).lower()


# ----------------------------------------------------------------------------------------------------------------------
# C# (port) model


CS_SKIP_CALLS = {'if', 'while', 'for', 'foreach', 'switch', 'return', 'new', 'catch', 'using', 'lock', 'nameof', 'typeof',
                 'sizeof', 'default', 'when', 'is', 'as', 'base', 'this', 'throw'}


class CsMethod:
    def __init__(self, src, cls, name, start, body):
        self.src, self.cls, self.name, self.start, self.body = src, cls, name, start, body


class Port:
    def __init__(self, port_dir):
        self.files = load_tree(os.path.join(port_dir, 'StormScript', 'Runtime'), {'.cs'}, 'cs', port_dir) + \
            load_tree(os.path.join(port_dir, 'StormScript'), {'.cs'}, 'cs', port_dir)
        seen = set()
        self.files = [f for f in self.files if not (f.rel in seen or seen.add(f.rel))]
        self.files += [f for f in load_tree(os.path.join(port_dir, 'game'), {'.cs'}, 'cs', port_dir) if '/' not in f.rel[5:]]
        self.methods = collections.defaultdict(list)    # name -> [CsMethod]
        self.class_files = collections.defaultdict(set)  # class -> {src}
        self.consts = {}
        for s in self.files:
            self.parse(s)
        self.all_strings = collections.defaultdict(list)   # lower literal -> [site]
        for s in self.files:
            for a, b, v in s.strings:
                self.all_strings[v.lower()].append(s.site(a))

    def parse(self, s):
        code = s.code
        classes = []
        for m in re.finditer(r'\b(?:class|struct|record)\s+([A-Za-z_]\w*)[^;{]*\{', code):
            ob = m.end() - 1
            classes.append((ob, match_close(code, ob, '{', '}'), m.group(1)))
            self.class_files[m.group(1)].add(s)
        for m in re.finditer(r'\bconst\s+int\s+([^;]*);', code):     # const int A = 1, B = 2;
            for mm in re.finditer(r'([A-Za-z_]\w*)\s*=\s*(-?\d+)', m.group(1)):
                self.consts[mm.group(1)] = int(mm.group(2))
        rx = re.compile(r'(?m)^[ \t]*(?:(?:public|private|protected|internal|static|override|virtual|async|partial|'
                        r'readonly|unsafe|new|sealed|extern)\s+)*(?:[\w<>\[\],.?()]+?\s+)(?!(?:if|while|for|foreach|switch|'
                        r'return|using|lock|catch|new)\b)([A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\(')
        for m in rx.finditer(code):
            op = m.end() - 1
            close = match_close(code, op)
            k = close + 1
            while k < len(code) and code[k] in ' \t\r\n':
                k += 1
            if code.startswith('=>', k):
                end = k
                depth = 0
                while end < len(code):
                    ch = code[end]
                    if ch in '([{':
                        depth += 1
                    elif ch in ')]}':
                        depth -= 1
                    elif ch == ';' and depth <= 0:
                        break
                    end += 1
                body = (k, end)
            elif k < len(code) and code[k] == '{':
                body = (k, match_close(code, k, '{', '}'))
            else:
                continue
            cls = None
            for ob, cb, nm in classes:
                if ob < m.start() < cb:
                    cls = nm                     # innermost wins (later classes start later)
            self.methods[m.group(1)].append(CsMethod(s, cls, m.group(1), m.start(), body))

    def var_type(self, s, var):
        for rx in (r'\b([A-Z]\w*)\??\s+' + re.escape(var) + r'\s*[;=,)]', re.escape(var) + r'\s*=\s*new\s+([A-Z]\w*)'):
            for m in re.finditer(rx, s.code):
                if m.group(1) in self.class_files:
                    return m.group(1)
        return None

    def closure(self, s, a, b, cls=None, max_depth=5):
        """All (src, start, end) code ranges reachable from s.code[a:b] through calls of port methods."""
        out = [(s, a, b)]
        seen = set()
        frontier = [(s, a, b, cls, 0)]
        while frontier:
            fs, fa, fb, fcls, d = frontier.pop()
            if d >= max_depth:
                continue
            seg = fs.code[fa:fb]
            for m in re.finditer(r'(?:([A-Za-z_]\w*)\s*[!?]?\s*\.\s*)?([A-Za-z_]\w*)\s*(?:<[^>()]*>)?\s*\(', seg):
                recv, name = m.group(1), m.group(2)
                if name in CS_SKIP_CALLS or name not in self.methods:
                    continue
                cands = self.methods[name]
                pick = []
                if recv:
                    t = recv if recv in self.class_files else self.var_type(fs, recv)
                    if t:
                        pick = [c for c in cands if c.cls == t]
                else:
                    pick = [c for c in cands if c.cls == fcls]
                if not pick:
                    pick = cands if len(cands) <= 4 else []
                for c in pick:
                    key = (c.src.rel, c.start)
                    if key in seen:
                        continue
                    seen.add(key)
                    out.append((c.src, c.body[0], c.body[1]))
                    frontier.append((c.src, c.body[0], c.body[1], c.cls, d + 1))
        return out

    def comment_ints(self, ranges):
        """Integers that occur only in comments of the code ranges (codes 'accepted without action' are documented so)."""
        out = set()
        for s, a, b in ranges:
            for m in re.finditer(r'(?<![\w.])(\d{3,})(?![\w.])', s.text[a:b]):
                if s.code[a + m.start():a + m.end()].strip() == '':
                    out.add(int(m.group(1)))
        return out

    def facts(self, ranges):
        """Integers, string literals and const ints visible in code ranges."""
        ints, strs = set(), set()
        for s, a, b in ranges:
            seg = s.code[a:b]
            for m in re.finditer(r'(?<![\w.])(-?\d+)(?![\w.])', seg):
                ints.add(int(m.group(1)))
            for m in re.finditer(r'\b([A-Z][A-Za-z0-9_]*)\b', seg):
                if m.group(1) in self.consts:
                    ints.add(self.consts[m.group(1)])
            for qa, qb, v in s.strings:
                if a <= qa < b:
                    strs.add(v.lower())
        return ints, strs

    def enclosing(self, s, pos):
        best = None
        for ms in self.methods.values():
            for m in ms:
                if m.src is s and m.body[0] <= pos <= m.body[1]:
                    if best is None or m.body[0] > best.body[0]:
                        best = m
        return best


def cs_value_type(expr):
    e = expr.strip()
    m = re.match(r'V\.(Int|Float|Str|A|Void)\b', e)
    if m:
        return {'Int': 'l', 'Float': 'f', 'Str': 's', 'A': 'a', 'Void': 'v'}[m.group(1)]
    return '?'


# ----------------------------------------------------------------------------------------------------------------------
# reference engine (optional)


class Reference:
    def __init__(self, root, script_defs):
        self.ok = os.path.isdir(root)
        self.classes = {}        # lower -> C++ name
        self.handled = {}        # lower class -> (ints, strs, site)
        self.events = collections.defaultdict(list)   # event name -> [(fmt or None, site)]
        self.natives = {}        # lower -> (nargs, site)
        self.strings = set()
        if not self.ok:
            return
        src = os.path.join(root, 'src')
        self.files = load_tree(src, {'.h', '.hpp', '.cpp'}, 'c', root)
        self.defs = Defines()
        for f in self.files:
            self.defs.scan(f)
        self.sdefs = script_defs
        self.strings = {v.lower() for f in self.files for a, b, v in f.strings}
        bases = {}
        methods = collections.defaultdict(list)     # (class, method) -> [(src, a, b)]
        for f in self.files:
            for m in re.finditer(r'\bCREATE_CLASS\(\s*(\w+)\s*\)', f.code):
                self.classes[m.group(1).lower()] = m.group(1)
            for m in re.finditer(r'\b(?:class|struct)\s+(\w+)\s*(?:final\s*)?:\s*(?:public|protected|private)?\s*(\w+)', f.code):
                bases[m.group(1)] = m.group(2)
            for m in re.finditer(r'\b(\w+)::(~?\w+)\s*\(', f.code):
                op = m.end() - 1
                close = match_close(f.code, op)
                k = close + 1
                mm = re.match(r'\s*(?:const\s*)?(?:noexcept\s*)?(?:override\s*)?\{', f.code[k:k + 80])
                if mm:
                    ob = k + mm.end() - 1
                    methods[(m.group(1), m.group(2))].append((f, ob, match_close(f.code, ob, '{', '}')))
            # inline class-body ProcessMessage definitions: class X { ... ProcessMessage(MESSAGE &message) { ... } }
        self.methods = methods
        for lc, cname in self.classes.items():
            ranges = []
            c = cname
            chain = []
            while c and c not in chain:
                chain.append(c)
                ranges += methods.get((c, 'ProcessMessage'), [])
                c = bases.get(c)
            # one level of same-class helper methods (MessageEx, ...)
            extra = []
            for f, a, b in ranges:
                for m in re.finditer(r'(?<![\w.>:])(\w+)\s*\(', f.code[a:b]):
                    for cc in chain:
                        for r in methods.get((cc, m.group(1)), []):
                            if r not in ranges and r not in extra:
                                extra.append(r)
            ints, strs = set(), set()
            for f, a, b in ranges + extra:
                seg = f.code[a:b]
                for m in re.finditer(r'\bcase\s+([^:]+?)\s*:(?!:)|==\s*([A-Za-z_]\w*|\d+)|([A-Za-z_]\w*|\d+)\s*(?===)', seg):
                    tok = (m.group(1) or m.group(2) or m.group(3)).strip()
                    v = int(tok) if tok.isdigit() else self.dv(tok)
                    if isinstance(v, int):
                        ints.add(v)
                for qa, qb, v in f.strings:
                    if a <= qa < b:
                        strs.add(v.lower())
            site = ranges[0][0].site(ranges[0][1]) if ranges else ''
            self.handled[lc] = (ints, strs, site, bool(ranges))
        # events raised by the engine
        for f in self.files:
            for m in re.finditer(r'\b(?:core|core_private|api)\s*(?:\.|->)\s*(Event|PostEvent)\s*\(', f.code):
                args, close = split_args(f.code, m.end() - 1)
                if not args:
                    continue
                name = f.lit(*args[0])
                if name is None:
                    t = f.code[args[0][0]:args[0][1]].strip()
                    v = self.dv(t) if re.fullmatch(r'\w+', t) else None
                    name = v if isinstance(v, str) else None
                if name is None:
                    continue
                fi = 1 if m.group(1) == 'Event' else 2
                fmt = f.lit(*args[fi]) if len(args) > fi else ''
                if fmt is None and len(args) > fi and f.code[args[fi][0]:args[fi][1]].strip() in ('nullptr', 'NULL', '0'):
                    fmt = ''
                self.events[name].append((fmt, f.site(m.start())))
        # natives
        for f in self.files:
            if f.rel.endswith('internal_functions.cpp'):
                m = re.search(r'IntFuncTable\s*\[\s*\]\s*=\s*\{', f.code)
                if m:
                    body = f.text[m.end():match_close(f.code, m.end() - 1, '{', '}')]
                    body = re.sub(r'//[^\n]*', '', body)
                    for mm in re.finditer(r'(\d+)\s*,\s*"(\w+)"', body):
                        self.natives[mm.group(2).lower()] = (int(mm.group(1)), f.site(m.start()))
            for m in re.finditer(r'nArguments\s*=\s*(\d+)\s*;\s*\w+\.pFuncName\s*=\s*"(\w+)"', f.text):
                self.natives[m.group(2).lower()] = (int(m.group(1)), f.site(m.start()))
            for m in re.finditer(r'pFuncName\s*=\s*"(\w+)"\s*;\s*\w+\.nArguments\s*=\s*(\d+)', f.text):
                self.natives[m.group(1).lower()] = (int(m.group(2)), f.site(m.start()))

    def dv(self, tok):
        v = self.defs.value(tok)
        return v if v is not None else self.sdefs.value(tok)


# ----------------------------------------------------------------------------------------------------------------------
# runtime evidence (logs)


class Logs:
    def __init__(self, port_dir):
        pats = [os.path.join(port_dir, 'game', 'smoke_*.log'), os.path.join(port_dir, 'game', 'smoke_*.log.err'),
                os.path.join(port_dir, 'reports', '*', 'log_tail.txt')]
        self.files = sorted(p for pat in pats for p in glob.glob(pat))
        self.events = collections.defaultdict(dict)      # event -> {log: count}
        self.nohandler = collections.defaultdict(dict)   # (class, code/sub) -> {log: count}
        self.nohandler_fmt = {}
        self.unresolved = collections.defaultdict(dict)  # lower func -> {log: count}
        self.unres_name = {}
        self.notfound = collections.defaultdict(dict)    # (kind, item) -> {log: count}
        self.event_lists = 0                             # logs that contain an [events] list
        for p in self.files:
            rel = os.path.relpath(p, port_dir).replace('\\', '/')
            seen_events = False
            for line in read_text(p).splitlines():
                m = re.match(r'\[events\] (\d+)x (.+?) handlers=', line)
                if m:
                    seen_events = True
                    d = self.events[m.group(2)]
                    d[rel] = max(d.get(rel, 0), int(m.group(1)))
                    continue
                m = re.match(r'\[nohandler\] (\d+)x (.+?) \| (\S*) \| ?(.*)$', line)
                if m:
                    key = (m.group(2).strip(), m.group(4).strip())
                    d = self.nohandler[key]
                    d[rel] = max(d.get(rel, 0), int(m.group(1)))
                    self.nohandler_fmt[key] = m.group(3)
                    continue
                m = re.match(r'\[unresolved\] (\d+)x (\w+)', line) or re.search(r'UNRESOLVED function (\w+)\(()', line)
                if m:
                    nm = m.group(2) if m.re.pattern.startswith(r'\[unresolved') else m.group(1)
                    cnt = int(m.group(1)) if m.re.pattern.startswith(r'\[unresolved') else 1
                    d = self.unresolved[nm.lower()]
                    d[rel] = max(d.get(rel, 0), cnt)
                    self.unres_name[nm.lower()] = nm
                    continue
                if 'TRACE' in line:
                    continue                 # script traces ('path not found') are game logic, not resources
                m = re.search(r'(?:\[(\w+)\] )?(?:(\w[\w ]*?) )?not found: (.+)$', line)
                if m:
                    kind = (m.group(1) or m.group(2) or 'file').strip()
                    key = (kind, m.group(3).strip())
                    self.notfound[key][rel] = self.notfound[key].get(rel, 0) + 1
                    continue
                m = re.search(r"\[(\w+)\] unknown (picture|system|scheme|light type) '?([^']*)'?(?: in group '([^']*)')?", line)
                if m:
                    item = m.group(3) + (' (group ' + m.group(4) + ')' if m.group(4) is not None else '')
                    key = (m.group(1) + ' ' + m.group(2), item)
                    self.notfound[key][rel] = self.notfound[key].get(rel, 0) + 1
            if seen_events:
                self.event_lists += 1


# ----------------------------------------------------------------------------------------------------------------------
# scoring (most player-visible first)

VIS = {}
for _n in ('battle_interface xinterface ship sea_ai sea location npcharacter player character worldmap dialog sound fader '
           'locationcamera battle_land_interface ilogandactions sail island weather sky').split():
    VIS[_n] = 10
for _n in ('particles blast aiballs aifort ballsplash rain lightning sunglow tornado flag rope vant mast telescope ispyglass '
           'shippointer people_on_ship sea_cameras ship_camera deck_camera animals blots model modelr charactersgroups '
           'activeperkshower aiseagoods seafoam wmvideoplay caviplayer widescreen infohandler helpchooser sinkeffect '
           'sharks sea_operator artifact').split():
    VIS[_n] = 6
for _n in ('env controls_container scriptscreen tmpteleport sealocatorshow lighter free_camera touch dummy islanded '
           'characteranimationkipper scrshoter obj_strservice findfilesintodirectory finddialognodes').split():
    VIS[_n] = 1


def vis_class(c):
    return VIS.get((c or '').lower(), 4)


def vis_event(name):
    rules = [(r'^(BI_|evntBI|blie|ievnt|Interface|evnt.*Interface|BLI_|evntBLI|evntSetUsing)', 9),
             (r'^(Shp|Ship|Ball|Cann|Fort|Sea|AI|Group|Sail|Mast|GetRigging|GetSail)', 8),
             (r'^(Location_|LAi|Character|chr|NPC_|Event_ChrSnd)', 8), (r'^(wdm|WorldMap|Worldmap|Map)', 8),
             (r'^(Fader|LoadScene|frame$|Control)', 7), (r'^(EWhr|Whr|Weather)', 6), (r'(?i)debug|cheat|test', 1)]
    for rx, v in rules:
        if re.search(rx, name):
            return v
    return 5


def debug_only(sites):
    return bool(sites) and all(DEBUG_FILES.search(s.split(':')[0]) for s in sites)


# ----------------------------------------------------------------------------------------------------------------------
# the audit


class Audit:
    def __init__(self, a):
        self.args = a
        self.S = Scripts(a.program)
        self.P = Port(PORT)
        self.R = Reference(a.ref, self.S.defs)
        self.L = Logs(PORT)
        self.items = []
        self.stats = collections.OrderedDict()

    def add(self, contract, item, sites, status, evidence, score, kind, **extra):
        sites = list(dict.fromkeys(sites))
        if debug_only(sites):
            score -= 30
            evidence = (evidence + '; ' if evidence else '') + 'лише налагоджувальні скрипти'
        self.items.append(dict(contract=contract, item=item, sites=sites, status=status, evidence=evidence,
                               score=round(score, 1), kind=kind, **extra))
        st = self.stats.setdefault(contract, collections.Counter())
        st[kind] += 1

    # ------------------------------------------------------------------ port registries
    def port_registry(self):
        P = self.P
        self.builtins = {}          # lower -> (kind, site, read_count or None(variadic))
        for s in P.files:
            if s.rel.endswith('Runtime/Builtins.cs'):        # Add("name", fn) / Nop("a", "b") / Zero(...); a later registration wins
                for m in re.finditer(r'\b(Add|Nop|Zero)\s*\(', s.code):
                    args, close = split_args(s.code, m.end() - 1)
                    if not args:
                        continue
                    if m.group(1) == 'Add':
                        nm = s.lit(*args[0])
                        if nm is None or len(args) < 2:
                            continue
                        body = s.code[args[1][0]:args[1][1]]
                        self.builtins[nm.lower()] = ('impl', s.site(m.start()), arity_read(body))
                    else:
                        if s.code[m.start() - 7:m.start()].strip().endswith('void'):
                            continue
                        for a_ in args:
                            nm = s.lit(*a_)
                            if nm:
                                self.builtins[nm.lower()] = ('stub: ' + ('no-op' if m.group(1) == 'Nop' else 'returns 0'),
                                                             s.site(m.start()), None)
        for s in P.files:
            for m in re.finditer(r'HostFunctions\s*\[\s*"(\w+)"\s*\]\s*=', s.text):      # game-side natives
                self.builtins[m.group(1).lower()] = ('impl (host)', s.site(m.start()), None)
            if s.rel.endswith('Runtime/Interp.cs'):                                     # makeref/findclass/... special forms
                md = re.search(r'\bEvalSpecial\s*\([^)]*\)\s*\{', s.code)      # tuple return type: not in P.methods
                if md:
                    a0, b0 = md.end() - 1, match_close(s.code, md.end() - 1, '{', '}')
                    for m in re.finditer(r'case\s+"(\w+)"\s*:', s.text[a0:b0]):
                        self.builtins.setdefault(m.group(1).lower(), ('impl (interp)', s.site(a0 + m.start()), None))
        # entity handlers
        self.handlers = {}          # lower class -> (site, ranges)
        for s in P.files:
            for m in re.finditer(r'EntityHandlers\s*\[\s*"(\w+)"\s*\]\s*=\s*', s.text):
                a = m.end()
                end = a
                depth = 0
                while end < len(s.code):
                    ch = s.code[end]
                    if ch in '([{':
                        depth += 1
                    elif ch in ')]}':
                        depth -= 1
                    elif ch == ';' and depth == 0:
                        break
                    end += 1
                expr = s.code[a:end].strip()
                encl = P.enclosing(s, m.start())
                if re.fullmatch(r'[A-Za-z_]\w*', expr):     # a variable holding the lambda
                    dm = None
                    for dm in re.finditer(r'\b' + expr + r'\s*=\s*(?=[(\w])', s.code):
                        if dm.start() < m.start():
                            break
                    if dm:
                        a2 = dm.end()
                        e2 = a2
                        depth = 0
                        while e2 < len(s.code):
                            ch = s.code[e2]
                            if ch in '([{':
                                depth += 1
                            elif ch in ')]}':
                                depth -= 1
                            elif ch == ';' and depth == 0:
                                break
                            e2 += 1
                        a, end = a2, e2
                ranges = P.closure(s, a, end, encl.cls if encl else None)
                self.handlers[m.group(1).lower()] = (s.site(m.start()), ranges, m.group(1))
        self.handler_facts = {k: P.facts(v[1]) for k, v in self.handlers.items()}
        self.handler_comments = {k: P.comment_ints(v[1]) for k, v in self.handlers.items()}

    # ------------------------------------------------------------------ entities
    def entities(self):
        S = self.S
        self.var_class = collections.defaultdict(set)      # norm target -> classes
        self.created = collections.defaultdict(list)       # class -> sites
        cls_case = {}
        for s, nm, pos, args in S.calls(r'CreateEntity|FindClass|CreateClass'):
            low = nm.lower()
            ci = 0 if low == 'createclass' else 1
            if len(args) <= ci:
                continue
            v = S.resolve(s, *args[ci])
            vals = [v] if isinstance(v, str) else []
            if not vals:   # string variable: literal assignments in the same function
                e = s.code[args[ci][0]:args[ci][1]].strip()
                f = S.func_at(s, pos)
                if f and re.fullmatch(r'\w+', e):
                    for mm in re.finditer(r'\b' + re.escape(e) + r'\s*=\s*"', s.code[f.open:f.close]):
                        lv = s.str_at.get(f.open + mm.end() - 1)
                        if lv:
                            vals.append(lv)
            for v in vals:
                if low != 'findclass':
                    self.created[v.lower()].append(s.site(pos))
                    cls_case.setdefault(v.lower(), v)
                if ci == 1:
                    self.var_class[norm_target(s.code[args[0][0]:args[0][1]])].add(v.lower())
                else:   # x = CreateClass("..")
                    mm = re.search(r'([\w\[\].&]+)\s*=\s*$', s.code[max(0, pos - 80):pos])
                    if mm:
                        self.var_class[norm_target(mm.group(1))].add(v.lower())
        self.cls_case = cls_case

    # ------------------------------------------------------------------ messages
    def messages(self):
        S, R = self.S, self.R
        sites = collections.defaultdict(list)     # (class, code, sub) -> [site]
        fmts = collections.defaultdict(set)
        dynamic = 0
        code_class = collections.defaultdict(collections.Counter)
        raw = []
        for s, nm, pos, args in S.calls(r'SendMessage'):
            if len(args) < 3:
                continue
            tgt = norm_target(s.code[args[0][0]:args[0][1]])
            fmt = s.lit(*args[1])
            code = S.resolve(s, *args[2])
            sub = None
            if isinstance(code, str):
                code, sub = None, code
            elif isinstance(code, int) and fmt and len(fmt) > 1 and fmt[1] == 's' and len(args) > 3:
                v = S.resolve(s, *args[3])
                sub = v if isinstance(v, str) else None
            elif code is None:
                dynamic += 1
                continue
            cname = s.code[args[2][0]:args[2][1]].strip()
            classes = set(self.var_class.get(tgt, set()))
            if not classes and re.fullmatch(r'\w+', tgt):
                for t in S.param_targets(s, pos, tgt):
                    classes |= self.var_class.get(t, set())
            raw.append((s.site(pos), tgt, classes, code, sub, fmt, cname))
            if classes and code is not None and not self.unfit(classes, code):
                for c in self.narrow(classes, code):
                    code_class[code][c] += 1
        # reference: code -> classes handling it
        ref_code = collections.defaultdict(set)
        for lc, (ints, strs, site, has) in R.handled.items():
            for i in ints:
                ref_code[i].add(lc)
        created = set(self.created)
        for site, tgt, classes, code, sub, fmt, cname in raw:
            how = 'var'
            if classes and code is not None and self.unfit(classes, code):
                classes = set()           # the variable's known classes never handle this code (Characters[i] = ship + character)
            if not classes and code is not None:
                if code_class.get(code):
                    classes = {code_class[code].most_common(1)[0][0]}
                    how = 'code'
                elif ref_code.get(code):
                    cand = ref_code[code] & created or ref_code[code]
                    classes = set(sorted(cand)[:3]) if len(cand) <= 3 else set()
                    how = 'ref'
            if not classes:
                classes = {'?'}
                how = '?'
            classes = self.narrow(classes, code)
            # a string argument after the code is a sub-command only for EX_MSG-style messages (the original compares it);
            # otherwise it is data (node name, texture, ...) and the message is keyed by its code alone
            if sub and code is not None and 'EX_MSG' not in cname.upper():
                ref = [self.R.handled.get(c) for c in classes]
                if not any(r and sub.lower() in r[1] for r in ref):
                    sub = None
            # character classes are one engine class family
            if classes & {'npcharacter', 'player', 'character'}:
                classes = {'npcharacter'} | (classes - {'npcharacter', 'player', 'character'})
            for c in classes:
                k = (c, code, sub.lower() if sub else None)
                sites[k].append(site)
                fmts[k].add(fmt or '?')
                self.msg_names.setdefault(k, (cname, sub))
                self.msg_how[k] = how
        self.msg_dynamic = dynamic
        return sites, fmts

    def unfit(self, classes, code):
        """True when every class of the set is known to the reference, none handles the code, but another class does."""
        if not self.R.ok or code is None:
            return False
        if not all(self.R.handled.get(c, (0, 0, 0, False))[3] for c in classes):
            return False
        if not any(code in r[0] for r in self.R.handled.values()):
            return False
        return not self.narrow(classes, code, strict=True)

    def narrow(self, classes, code, strict=False):
        """A variable bound to several classes (Characters[i] is both a character and a ship): keep the classes whose
        original ProcessMessage handles the code (character family counts as one). strict: may return an empty set."""
        if (len(classes) < 2 and not strict) or code is None or not self.R.ok:
            return set(classes)
        fam = {'npcharacter', 'player', 'character'}

        def handles(c):
            for cc in (fam if c in fam else {c}):
                r = self.R.handled.get(cc)
                if r and code in r[0]:
                    return True
            return False
        keep = {c for c in classes if handles(c)}
        return keep if (keep or strict) else set(classes)

    def audit_messages(self):
        self.msg_names, self.msg_how = {}, {}
        sites, fmts = self.messages()
        nohandler_used = set()
        for k in sorted(sites, key=lambda k: (k[0], str(k[1]), str(k[2]))):
            c, code, sub = k
            cname, sub0 = self.msg_names[k]
            label = '%s → %s' % (self.cls_case.get(c, c), cname if code is not None else '"%s"' % sub0)
            if code is not None:
                label += ' (%d)' % code if not re.fullmatch(r'-?\d+', cname) else ''
                if sub0:
                    label += ' "%s"' % sub0
            label += ' fmt "%s"' % '/'.join(sorted(fmts[k]))
            ev = []
            ref = self.R.handled.get(c)
            orig = None
            if ref and ref[3]:
                orig = (code in ref[0]) if code is not None else (sub in ref[1])
                if code is not None and sub and orig:
                    orig = sub in ref[1] or not ref[1]
                ev.append('оригінал: ' + ('обробляє' if orig else 'НЕ обробляє') + ' (' + ref[2] + ')')
            elif self.R.ok and c != '?':
                ev.append('оригінал: класу немає в reference' if c not in self.R.classes else 'оригінал: ProcessMessage не знайдено')
            # runtime
            rt = 0
            for (lc, lcode), d in self.L.nohandler.items():
                lcls = lc.lower()
                parts = lcode.split(None, 1)
                lnum = int(parts[0]) if parts and re.fullmatch(r'-?\d+', parts[0]) else None
                lsub = (parts[1] if lnum is not None and len(parts) > 1 else (lcode if lnum is None else '')).strip().lower()
                if lnum == code and (lsub or None) == sub and (lcls == c or (c == 'npcharacter' and lcls in ('player', 'character'))):
                    rt += sum(d.values())
                    nohandler_used.add((lc, lcode))
            if c == '?':
                status, kind, base = 'клас цілі не визначено статично', 'info', 0
            elif c not in self.handlers:
                status, kind, base = 'немає EntityHandlers["%s"]' % self.cls_case.get(c, c), 'missing', 50
            else:
                ints, strs = self.handler_facts[c]
                if code is not None and code not in ints and not any(i >= 1000 for i in ints):
                    status, kind, base = 'обробник не розрізняє коди (однаково реагує на будь-яке повідомлення)', 'ok', 0
                elif code is not None and code not in ints and code in self.handler_comments[c]:
                    status, kind, base = 'код %d приймається без дії (згаданий лише в коментарі обробника)' % code, 'mismatch', 22
                elif code is not None and code not in ints:
                    status, kind, base = 'код %d не знайдено в обробнику' % code, 'missing', 40
                elif sub and sub not in strs:
                    status, kind, base = 'підкоманду "%s" не знайдено в обробнику' % sub0, 'missing', 35
                else:
                    status, kind, base = 'обробляється', 'ok', 0
                ev.append('порт: ' + self.handlers[c][0])
            if kind == 'missing' and orig is False:
                kind, base = 'info', 5
                status += ' (оригінал теж ігнорує)'
            if rt:
                ev.append('[nohandler] %d× у логах' % rt)
            if self.msg_how.get(k) in ('code', 'ref'):
                ev.append('клас визначено за кодом' + (' (reference)' if self.msg_how[k] == 'ref' else ''))
            score = base + 3 * vis_class(c) + min(10, len(sites[k])) + (15 if rt else 0) if kind != 'ok' else 0
            self.add('Повідомлення', label, sites[k], status, '; '.join(ev), score, kind, cls=c, code=code, sub=sub)
        # runtime-only [nohandler] lines not matched to a static site
        for (lc, lcode), d in self.L.nohandler.items():
            if (lc, lcode) in nohandler_used:
                continue
            total = sum(d.values())
            what = '%s | %s | %s' % (lc, self.L.nohandler_fmt.get((lc, lcode), ''), lcode)
            kind = 'missing'
            if lc == '<no entity>':
                status = ('ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; '
                          'перевірити, чи порт створює її так само рано)')
                kind = 'info'
            elif lc.lower() in self.handlers:
                status = 'застарілий лог: обробник класу вже є (%s)' % self.handlers[lc.lower()][0]
                kind = 'info'
            else:
                status = 'немає обробника класу (лише runtime)'
            self.add('Повідомлення (runtime)', what, [], status, '[nohandler] %d× у %s' % (total, ', '.join(sorted(d)[:3])),
                     (20 + 3 * vis_class(lc) if kind == 'missing' else 0) + min(15, math.log10(total + 1) * 5), kind)

    def audit_entities(self):
        msg_classes = collections.Counter(it['cls'] for it in self.items if it['contract'] == 'Повідомлення')
        port_strings = self.P.all_strings
        for c in sorted(self.created):
            sites = self.created[c]
            nm = self.cls_case.get(c, c)
            ev = []
            if self.R.ok:
                ev.append('оригінал: ' + ('CREATE_CLASS(%s)' % self.R.classes[c] if c in self.R.classes else 'класу немає в reference'))
            cc = 'npcharacter' if c in ('player', 'character') else c
            if c in self.handlers:
                status, kind, base = 'EntityHandlers["%s"] (%s)' % (self.handlers[c][2], self.handlers[c][0]), 'ok', 0
            elif c in port_strings:
                status, kind, base = 'без обробника повідомлень; клас згадується в коді порту', 'ok' if not msg_classes.get(cc) else 'missing', 30
                ev.append('порт: ' + port_strings[c][0])
            else:
                status, kind, base = 'немає реалізації', 'missing', 40
            if kind == 'missing':
                n = msg_classes.get(cc, 0)
                status += '; скрипти шлють йому %d різних повідомлень' % n if n else '; повідомлень йому скрипти не шлють'
                base += 15 if n else -15
                if vis_class(c) <= 1:
                    kind = 'info'
                    status += ' (налагоджувальний/службовий клас)'
            self.add('Сутності', nm, sites, status, '; '.join(ev), base + 3 * vis_class(c) if kind != 'ok' else 0, kind, cls=c)

    # ------------------------------------------------------------------ natives
    def audit_natives(self):
        S, R = self.S, self.R
        calls = collections.defaultdict(list)
        externs = set()
        nargs = collections.defaultdict(collections.Counter)
        for s, nm, pos, args in S.calls(r'[A-Za-z_]\w*'):
            low = nm.lower()
            if low in STORM_KEYWORDS or low in S.funcs:
                continue
            # skip definitions/declarations: 'type name(' at statement start
            pre = s.code[max(0, pos - 30):pos]
            if re.search(r'(?:^|[;{}\s])(?:native\s+|extern\s+)?(int|float|string|bool|ref|aref|object|void)\s+$', pre):
                if re.search(r'\bextern\s+\w+\s+$', pre):
                    externs.add(low)
                continue
            if re.search(r'(#\s*|\bcall\s+)$', pre):      # #event_handler(...), 'call var(...)' (a function name in a variable)
                continue
            if nm in S.defs.raw:
                v = S.defs.value(nm)
                if isinstance(v, (int, float)) or v is None:
                    continue
            calls[low].append(s.site(pos))
            nargs[low][len(args)] += 1
        names = {}
        for s, nm, pos, args in S.calls(r'[A-Za-z_]\w*'):
            names.setdefault(nm.lower(), nm)
        for low in sorted(calls):
            sites = calls[low]
            nm = names.get(low, low)
            ev = []
            rt = self.L.unresolved.get(low)
            ref = R.natives.get(low)
            decl = S.natives.get(low)
            if ref:
                ev.append('оригінал: %d арг. (%s)' % ref)
            elif R.ok:
                ev.append('у reference немає')
            if decl:
                ev.append('native-оголошення: %s (%s)' % (decl[0], decl[2]))
            pb = self.builtins.get(low)
            used = sorted(nargs[low])
            if pb:
                kindp, site, read = pb
                ev.insert(0, 'порт: ' + site)
                mism = []
                declared = ref[0] if ref else (decl[1] if decl else None)
                if declared is not None and any(n != declared for n in used) and low not in ('event', 'postevent', 'sendmessage'):
                    mism.append('скрипт передає %s арг., оригінал оголошує %d' % ('/'.join(map(str, used)), declared))
                if read is not None and max(used) > read:
                    mism.append('порт читає лише %d з %d арг.' % (read, max(used)))
                if kindp.startswith('stub') and re.match(r'(layer|entityupdate|rdtsc_|reloadprogress|setreload)', low):
                    status, kind, base = kindp + ' (у Godot не потрібно: шари/профайлер/екран завантаження)', 'info', 0
                elif kindp.startswith('stub'):
                    status, kind, base = kindp, 'mismatch', 20
                elif mism:
                    status, kind, base = '; '.join(mism), 'mismatch', 12
                else:
                    status, kind, base = kindp, 'ok', 0
            else:
                status, kind, base = 'немає в порті (Builtins/HostFunctions)', 'missing', 45
                if low in externs and not ref and not decl:
                    status, kind, base = 'extern без визначення в жодному скрипті (мертвий код скриптів)', 'info', 0
                elif R.ok and not ref and not decl and not rt:
                    status, kind, base = 'не визначена ні в скриптах, ні в оригінальному рушії — ймовірно дефект скриптів', 'info', 0
            if rt:
                ev.append('[unresolved] %d× у логах' % sum(rt.values()))
                base += 20
            elif kind == 'missing' and self.L.event_lists:
                ev.append('у логах не траплялась')
            score = base + min(10, len(sites)) + (vis_event(nm) if kind != 'ok' else 0)
            self.add('Функції рушія', '%s() ×%d' % (nm, len(sites)), sites[:5], status, '; '.join(ev), score if kind != 'ok' else 0,
                     kind, args_used=used)
        # unresolved at runtime but not seen statically
        for low, d in self.L.unresolved.items():
            if low not in calls:
                self.add('Функції рушія', self.L.unres_name[low] + '()', [], 'unresolved під час прогону', '%d× у %s' %
                         (sum(d.values()), ', '.join(sorted(d)[:3])), 55, 'missing')

    # ------------------------------------------------------------------ events
    def handler_reads(self, fn):
        """GetEventData() reads of a script function: [(type code, conditional, line)]; None if unknown function."""
        fl = self.S.funcs.get(fn.lower())
        if not fl:
            return None
        f = fl[0]
        s = f.src
        code = s.code
        body = code[f.open:f.close]
        decl = {}
        for t, n in f.params:
            decl[n.lower()] = t
        for m in re.finditer(r'\b(int|float|string|bool|ref|aref|object)\s+([A-Za-z_]\w*)', body):
            decl.setdefault(m.group(2).lower(), m.group(1))
        for m in re.finditer(r'\b(int|float|string|bool|ref|aref|object)\s+([A-Za-z_][\w\s,\[\]]*);', body):   # float x, y, z;
            for n in m.group(2).split(','):
                n = re.sub(r'\[.*', '', n).strip()
                if re.fullmatch(r'[A-Za-z_]\w*', n):
                    decl.setdefault(n.lower(), m.group(1))
        out = []
        loop = False
        for m in re.finditer(r'GetEventData\s*\(\s*\)', body):
            p = m.start()
            st = max(body.rfind(';', 0, p), body.rfind('{', 0, p), body.rfind('}', 0, p)) + 1
            stmt = body[st:p]
            depth = body[:p].count('{') - body[:p].count('}')
            t = '?'
            mm = re.search(r'(?:\b(int|float|string|bool|ref|aref|object)\s+)?([A-Za-z_]\w*)\s*=\s*$', stmt)
            if mm and not re.search(r'[.\]]\s*' + re.escape(mm.group(2)) + r'\s*=\s*$', stmt):
                ty = mm.group(1) or decl.get(mm.group(2).lower()) or self.S.globals.get(mm.group(2).lower())
                t = TYPE_CODE.get(ty, '?') if ty else '?'
            elif re.search(r'\bGetCharacter\s*\(\s*$', stmt):
                t = 'l'
            if re.search(r'\b(for|while)\b', body[max(0, st - 120):p]) and depth > 1:
                loop = True
            out.append((t, depth > 1, s.line(f.open + p)))
        return out, loop, s.site(f.open)

    def audit_events(self):
        S, P, R, L = self.S, self.P, self.R, self.L
        handlers = collections.defaultdict(list)       # event -> [(fn, site)]
        for s, nm, pos, args in S.calls(r'SetEventHandler'):
            if len(args) >= 2:
                ev, fn = S.resolve(s, *args[0]), S.resolve(s, *args[1])
                if isinstance(ev, str) and isinstance(fn, str):
                    handlers[ev].append((fn, s.site(pos)))
        for s in S.files:
            for m in re.finditer(r'#\s*event_handler\s*\(', s.code):
                args, close = split_args(s.code, m.end() - 1)
                if len(args) >= 2:
                    ev, fn = S.resolve(s, *args[0]), S.resolve(s, *args[1])
                    if isinstance(ev, str) and isinstance(fn, str):
                        handlers[ev].append((fn, s.site(m.start())))
        raised = collections.defaultdict(list)          # script raisers
        for s, nm, pos, args in S.calls(r'Event|PostEvent'):
            if args:
                ev = S.resolve(s, *args[0])
                if isinstance(ev, str):
                    raised[ev.lower()].append(s.site(pos))
        # names used as data by scripts (passed to the engine or stored for a later Event(var))
        reg_pos = set()
        for s, nm, pos, args in S.calls(r'SetEventHandler|DelEventHandler|Event|PostEvent'):
            if args:
                reg_pos.add((s.rel, args[0][0]))
        data_use = collections.defaultdict(list)
        sendmsg_ranges = collections.defaultdict(list)
        for s, nm, pos, args in S.calls(r'SendMessage'):
            if args:
                sendmsg_ranges[s.rel].append((args[0][0], args[-1][1]))
        hnames = {e.lower() for e in handlers}
        for s in S.files:
            for qa, qb, v in s.strings:
                lv = v.lower()
                if lv in hnames and (s.rel, qa) not in reg_pos and not any((s.rel, qa - k) in reg_pos for k in range(1, 4)):
                    if re.search(r'#\s*event_handler\s*\(\s*$', s.code[max(0, qa - 40):qa]):
                        continue
                    insend = any(a <= qa <= b for a, b in sendmsg_ranges.get(s.rel, []))
                    data_use[lv].append(('engine' if insend else 'data', s.site(qa)))
        # port raisers
        port_raise = collections.defaultdict(list)     # lower event -> [(types, site)]
        for s in P.files:
            for m in re.finditer(r'\b(FireEvent|PostEventInternal)\s*\(', s.code):
                args, close = split_args(s.code, m.end() - 1)
                if not args:
                    continue
                names = [v for qa, qb, v in s.strings if args[0][0] <= qa < args[0][1]]
                if not names:
                    continue
                li = 1 if m.group(1) == 'FireEvent' else 2
                types = ''
                if len(args) > li:
                    seg = s.code[args[li][0]:args[li][1]]
                    ob = seg.find('{')
                    if ob >= 0:
                        items, _ = split_args(s.code, args[li][0] + ob)      # new List<V> { V.Int(..), V.A(..) }
                        types = ''.join(cs_value_type(s.code[a_:b_]) for a_, b_ in items)
                    elif re.search(r'new\s+List<V>\s*\(\s*\)', seg):
                        types = ''
                    else:
                        types = '*'          # built elsewhere
                for n_ in names:
                    port_raise[n_.lower()].append((types, s.site(m.start())))
        engine_events = {}
        for ev in handlers:
            le = ev.lower()
            ref_ev = R.events.get(ev) or next((v for k, v in R.events.items() if k.lower() == le), None)
            if le in raised and not ref_ev and le not in port_raise:
                continue                       # raised by the scripts themselves
            engine_events[ev] = ref_ev
        port_strings = P.all_strings
        ini_events = {}               # lower event -> first 'file:line' of an interface ini 'event:NAME'
        ini_root = os.path.join(self.args.game, 'RESOURCE', 'INI')
        for dp, dn, fn in os.walk(ini_root):
            for f in sorted(fn):
                if f.lower().endswith('.ini'):
                    for i, line in enumerate(read_text(os.path.join(dp, f)).splitlines(), 1):
                        for m in re.finditer(r'(?i)\bevent:(\w+)', line.split(';')[0]):
                            ini_events.setdefault(m.group(1).lower(), '%s:%d' % (
                                os.path.relpath(os.path.join(dp, f), self.args.game).replace('\\', '/'), i))
        n_logs = L.event_lists
        rt_events = {k.lower(): v for k, v in L.events.items()}
        for ev in sorted(engine_events, key=str.lower):
            le = ev.lower()
            ref_ev = engine_events[ev]
            hs = handlers[ev]
            sites = [h[1] for h in hs]
            ev_text = []
            if ref_ev:
                fm = sorted({f for f, _ in ref_ev if f is not None})
                ev_text.append('оригінал: core.Event "%s" (%s)' % ('|'.join(fm) if fm else '?', ref_ev[0][1]))
            elif R.ok:
                ev_text.append('у reference не підіймається')
            if le in raised:
                ev_text.append('скрипти теж підіймають (%s)' % raised[le][0])
            du = data_use.get(le, [])
            rt = rt_events.get(le)
            pr = port_raise.get(le)
            vis = vis_event(ev)
            if pr:
                status, kind, base = 'підіймається: ' + pr[0][1] + (' (+%d)' % (len(pr) - 1) if len(pr) > 1 else ''), 'ok', 0
            elif rt:
                status, kind, base = 'підіймається (лише runtime-докази)', 'ok', 0
            elif le in port_strings:
                status, kind, base = 'ім\'я є в коді порту (%s), виклик непрямий' % port_strings[le][0], 'ok', 0
            elif le in raised and ref_ev:
                status, kind, base = 'скрипти підіймають самі, але оригінал підіймає теж — порт ні', 'mismatch', 25
            elif le in raised:
                status, kind, base = 'скрипти підіймають самі', 'ok', 0
            elif le in ini_events:
                if 'event:' in port_strings:
                    status, kind, base = 'з ini інтерфейсу (%s, `event:`), XInterface підіймає генерично (%s)' % (
                        ini_events[le], port_strings['event:'][0]), 'ok', 0
                else:
                    status, kind, base = 'НЕ підіймається: ini інтерфейсу (%s) має `event:`, порт його не читає' % ini_events[le], 'missing', 40
            elif du and not ref_ev:
                eng = [d for d in du if d[0] == 'engine']
                if eng:
                    status, kind, base = 'не підіймається; ім\'я передається рушію через SendMessage (%s)' % eng[0][1], 'missing', 35
                else:
                    status, kind, base = 'не знайдено; ім\'я використовується скриптами як дані (%s) — можливо, скрипт-подія' % du[0][1], 'info', 5
            elif ref_ev or not R.ok:
                status, kind, base = 'НЕ підіймається портом', 'missing', 45
            else:
                status, kind, base = ('не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія '
                                      'або мертвий обробник, перевірити вручну'), 'info', 8
            if rt:
                ev_text.append('у логах: %d×' % sum(rt.values()))
            elif n_logs and kind == 'ok' and not (le in raised):
                ev_text.append('0× у [events] %d логів (топ-40)' % n_logs)
            self.add('Події', ev, sites, status, '; '.join(ev_text), base + 3 * vis if kind != 'ok' else 0, kind,
                     handlers=[h[0] for h in hs])
            # runtime: raised by the port statically but never seen in logs
            if pr and not rt and n_logs:
                self.add('Події: runtime', ev, sites, 'жодного разу в регресійних прогонах', 'порт: %s; [events] топ-40 у %d логах' %
                         (pr[0][1], n_logs), 10 + vis, 'info')
            # ---------- port arguments vs the original core.Event format (independent of what the handlers read)
            if pr and ref_ev:
                fmts = sorted({f_ for f_, _ in ref_ev if f_ is not None})
                for types, ps in pr:
                    if types == '*' or not fmts:
                        continue
                    if not any(fmt_compatible(types, f_) for f_ in fmts):
                        reads = [r for r in (self.handler_reads(fn) for fn, _ in hs) if r and r[0]]
                        happy = reads and all(not param_mismatch(''.join(t for t, c, l in r[0]), [c for t, c, l in r[0]], types, r[1])[0]
                                              for r in reads)
                        if happy:
                            self.add('Параметри подій: формат', ev, sites, 'порт передає "%s", reference "%s" — але обробники PotC-скриптів '
                                     'читають саме те, що дає порт (різниця версій рушія)' % (types, '|'.join(fmts)),
                                     'порт: %s; оригінал: %s' % (ps, ref_ev[0][1]), 2, 'info')
                            continue
                        self.add('Параметри подій: формат', ev, sites, 'порт передає "%s", оригінал "%s"' % (types, '|'.join(fmts)),
                                 'порт: %s; оригінал: %s' % (ps, ref_ev[0][1]), 8 + vis, 'mismatch')
                    else:
                        self.stats.setdefault('Параметри подій: формат', collections.Counter())['ok'] += 1
            # ---------- parameters
            for fn, hsite in hs:
                hr = self.handler_reads(fn)
                if hr is None:
                    continue
                reads, loop, fsite = hr
                if not reads:
                    continue
                want = ''.join(t for t, c, l in reads)
                sources = []
                if ref_ev:
                    for f_, rs in ref_ev:
                        if f_ is not None:
                            sources.append(('оригінал', f_, rs))
                for types, ps in (pr or []):
                    sources.append(('порт', types, ps))
                for who, have, ssite in sources:
                    if have == '*':
                        continue
                    probs, serious = param_mismatch(want, [c for t, c, l in reads], have, loop)
                    if not probs:
                        if who == 'порт':
                            st = self.stats.setdefault('Параметри подій', collections.Counter())
                            st['ok'] += 1
                        continue
                    if who == 'оригінал':
                        # only interesting when the port does not follow the original either
                        continue
                    orig = [f_ for w_, f_, _ in sources if w_ == 'оригінал']
                    ev2 = 'скрипт читає "%s" (%s); порт передає "%s" (%s)' % (want, fsite, have, ssite)
                    if orig:
                        ev2 += '; оригінал "%s"' % '|'.join(sorted(set(orig)))
                        if any(not param_mismatch(want, [c for t, c, l in reads], o, loop)[0] for o in orig):
                            serious = True
                    if have in orig:
                        self.add('Параметри подій', '%s → %s()' % (ev, fn), [hsite], '; '.join(probs) + ' (порт = формат reference)',
                                 ev2, 5 + vis, 'info')
                        continue
                    self.add('Параметри подій', '%s → %s()' % (ev, fn), [hsite], '; '.join(probs), ev2,
                             (40 if serious else 15) + 3 * vis, 'mismatch')
        self.handlers_by_event = handlers

    # ------------------------------------------------------------------ resources
    def audit_resources(self):
        L, S, P = self.L, self.S, self.P
        lit_index = collections.defaultdict(list)
        for s in S.files:
            for qa, qb, v in s.strings:
                lit_index[v.lower()].append(s.site(qa))
        for (kind, item), d in sorted(L.notfound.items()):
            base = re.split(r'[\\/]', item.strip("'"))[-1]
            stem = os.path.splitext(base)[0].lower()
            sites = lit_index.get(item.lower()) or lit_index.get(base.lower()) or lit_index.get(stem) or []
            total = sum(d.values())
            self.add('Ресурси', '%s: %s' % (kind, item), sites[:3], 'не завантажується портом',
                     '%d× у %s' % (total, ', '.join(sorted(d)[:3])), 25 + min(15, total), 'missing')
        # ini files never read by the port
        ini_root = os.path.join(self.args.game, 'RESOURCE', 'INI')
        if not os.path.isdir(ini_root):
            return
        pstr = ' '.join(P.all_strings.keys())
        sstr = set(lit_index.keys())
        for dp, dn, fn in os.walk(ini_root):
            for f in sorted(fn):
                if not f.lower().endswith('.ini'):
                    continue
                full = os.path.join(dp, f)
                rel = os.path.relpath(full, self.args.game).replace('\\', '/')
                parts = [p.lower() for p in os.path.relpath(full, ini_root).replace('\\', '/').split('/')]
                low = f.lower()
                by_name = low in pstr
                by_dir = len(parts) > 1 and any(p in pstr for p in parts[:-1])
                by_script = any(low in k or rel.lower() in k for k in sstr)
                rel_l = rel.lower()
                ref_reads = any(re.sub(r'[\\/]+', '/', k).endswith(rel_l) for k in self.R.strings) if self.R.ok else None
                if by_name:
                    secs = list(dict.fromkeys(re.findall(r'(?m)^\s*\[([^\]]+)\]', read_text(full))))
                    if len(parts) == 1 and not self.ini_generic(low):
                        # sections the original engine asks for by name, the port never names
                        missing = [x for x in secs if x.lower() not in P.all_strings and
                                   (not self.R.ok or x.lower() in self.R.strings)]
                        if missing:
                            self.add('Ресурси: ini', '%s: секції [%s]' % (rel, '], ['.join(missing[:8]) + (' …' if len(missing) > 8 else '')),
                                     [], 'секції, яких порт не читає (оригінал читає їх за назвою)',
                                     '%d з %d секцій' % (len(missing), len(secs)), 14, 'mismatch')
                    self.stats.setdefault('Ресурси: ini', collections.Counter())['ok'] += 1
                elif by_dir or by_script:
                    self.stats.setdefault('Ресурси: ini', collections.Counter())['ok'] += 1
                elif ref_reads is False:
                    self.add('Ресурси: ini', rel, [], 'порт не читає; оригінал (reference) теж не читає за цим шляхом',
                             'ні назви, ні теки в коді порту; скрипти не передають', 2, 'info')
                else:
                    self.add('Ресурси: ini', rel, [], 'порт не читає цей файл', 'оригінал читає (reference); ні назви, ні теки в коді порту', 24,
                             'missing')

    def ini_generic(self, low):
        """True when a port file that names the ini enumerates all its sections."""
        for s in self.P.files:
            if low in s.text.lower() and re.search(r'\.Sections\s*(?:\.(?:Values|Keys)|\))|foreach\s*\([^)]*\bSections\b', s.code):
                return True
        return False

    # ------------------------------------------------------------------ run
    def run(self):
        self.port_registry()
        self.entities()
        self.audit_messages()
        self.audit_entities()
        self.audit_natives()
        self.audit_events()
        self.audit_resources()


def arity_read(body):
    if re.search(r'\ba\s*\.\s*(Skip|Length|Count|Select|ToList|ToArray)\b|foreach\s*\(|params\b|\bArg\(a,\s*[a-z]', body):
        return None
    idx = [int(x) for x in re.findall(r'\b(?:I|S|F|Arg|B|R|Ref|A)\s*\(\s*a\s*,\s*(\d+)\s*\)|\ba\s*\[\s*(\d+)\s*\]', body) for x in x if x]
    if not idx and re.search(r'\(\s*\w+\s*,\s*_\s*\)', body):
        return 0
    if not idx and re.search(r'\ba\b', body):
        return None
    return (max(idx) + 1) if idx else 0


def fmt_compatible(have, fmt):
    """Port argument types vs an original format string: same count, types equal (entity id ~ aref/int, '?' = any)."""
    if len(have) != len(fmt):
        return False
    for h, f in zip(have, fmt):
        if h == '?' or f == '?' or h == f or (f == 'i' and h in 'la') or (h == 'i' and f in 'la'):
            continue
        return False
    return True


def param_mismatch(want, cond, have, loop):
    """want: script reads (type codes), cond: per read 'conditional?', have: provided types. -> ([problems], serious)"""
    probs = []
    serious = False
    if len(have) < len(want) and not loop:
        missing_unc = [i for i in range(len(have), len(want)) if not cond[i]]
        probs.append('скрипт читає %d значень, передається %d%s' % (len(want), len(have), '' if missing_unc else ' (зайві читання умовні)'))
        serious |= bool(missing_unc)
    for i, (w, h) in enumerate(zip(want, have)):
        if '?' in (w, h) or w == h:
            continue
        if h == 'i' and w in 'la':
            continue
        if {w, h} == {'l', 'f'}:
            probs.append('#%d: %s замість %s' % (i + 1, NAMES[h], NAMES[w]))
            continue
        probs.append('#%d: %s замість %s' % (i + 1, NAMES.get(h, h), NAMES.get(w, w)))
        serious = True
    return probs, serious


NAMES = {'l': 'int', 'f': 'float', 's': 'string', 'a': 'aref', 'i': 'entity-id', 'v': 'void', '?': '?'}


# ----------------------------------------------------------------------------------------------------------------------
# output


def md_escape(t):
    return str(t).replace('|', '\\|').replace('\n', ' ')


def write_outputs(au, md_path, json_path, top):
    items = sorted(au.items, key=lambda it: (-it['score'], it['contract'], it['item']))
    problems = [it for it in items if it['kind'] in ('missing', 'mismatch')]
    infos = [it for it in items if it['kind'] == 'info']
    now = datetime.datetime.now().strftime('%Y-%m-%d %H:%M')
    summary = collections.OrderedDict()
    for c, st in au.stats.items():
        summary[c] = {'implemented': st.get('ok', 0), 'missing': st.get('missing', 0), 'mismatched': st.get('mismatch', 0),
                      'info': st.get('info', 0)}
    data = {'generated': now, 'inputs': {'program': au.args.program, 'reference': au.R.ok, 'logs': [os.path.relpath(f, PORT).replace('\\', '/') for f in au.L.files]},
            'summary': summary, 'dynamic_sendmessage_sites': au.msg_dynamic,
            'items': items}
    with open(json_path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=1)

    L = []
    L.append('# Контракти рушія: скрипти ↔ порт (автоматичний аудит)')
    L.append('')
    L.append('_Згенеровано `python tools/contract_audit.py` %s. Не редагувати вручну — перезапустити (`run_tests.ps1 -Contracts`)._' % now)
    L.append('')
    L.append('Джерела: скрипти `addon/merged/PROGRAM` (%d файлів, %d #define, %d функцій), порт `StormScript/Runtime` + `game/*.cs` '
             '(%d файлів), reference `reference/storm-engine` (%s), логи: %d (`game/smoke_*.log`, `reports/*/log_tail.txt`).' %
             (len(au.S.files), len(au.S.defs.raw), sum(len(v) for v in au.S.funcs.values()), len(au.P.files),
              '%d класів, %d подій, %d native' % (len(au.R.classes), len(au.R.events), len(au.R.natives)) if au.R.ok else 'відсутній',
              len(au.L.files)))
    L.append('')
    L.append('## Відомі обмеження')
    L.append('* Обробник порту «обробляє» код, якщо число коду (або підкоманда-рядок) трапляється будь-де в коді, досяжному з лямбди '
             '`EntityHandlers[...]` (замикання викликів до 5 рівнів, неоднозначні методи — усі кандидати). Тому пропуски скоріше '
             'НЕДОоцінені: випадковий збіг числа ховає проблему, але хибних «немає» мало. Заглушки `case N: return 0` рахуються як обробка.')
    L.append('* Клас цілі `SendMessage` береться з `CreateEntity/FindClass` змінної; для `aref`-параметрів — за кодом (де ще цей код '
             'шлють відомій сутності) або за reference (`ProcessMessage`, що має цей `case`). %d викликів з нестатичним кодом пропущено.' % au.msg_dynamic)
    L.append('* Reference (`reference/storm-engine`) — відкритий рушій пізнішої гри (TEHO); коди/формати здебільшого збігаються з PotC, '
             'але не завжди. «Оригінал не обробляє» = у його `ProcessMessage` такого `case` немає.')
    L.append('* Події: «рушійна» = скрипти слухають, але самі не підіймають `Event/PostEvent` з константною назвою, або reference її '
             'підіймає. Назви, які скрипти зберігають як дані (для пізнішого `Event(змінна)`), позначені як info. '
             'Порт «підіймає», якщо назва є першим аргументом `FireEvent/PostEventInternal` (рядок) або хоч десь у коді порту як рядок.')
    L.append('* Параметри: послідовність `GetEventData()` у тексті обробника (умовні читання позначені), тип — з оголошення змінної; '
             '`?` = невідомо (атрибут, `sti(GetEventData())`). Аргументи порту — з ініціалізатора `new List<V> { V.Int(..), ... }`; '
             'списки, зібрані окремо, не перевіряються. int↔float — не критично (VM конвертує).')
    L.append('* Runtime: `[events]` у логах — лише топ-40 подій кожного прогону, тож «0× у логах» означає «не в топ-40». Логи можуть бути '
             'старші за код (запускати після `run_tests.ps1 -Smoke/-Sea ...`); `[nohandler]` класу, який уже має обробник, позначено як застарілий.')
    L.append('* «Параметри подій: формат» — аргументи порту проти формату `core.Event` у reference; якщо обробники PotC-скриптів читають '
             'саме те, що дає порт, розбіжність вважається різницею версій рушія (info).')
    L.append('* Функції: native = викликається, але не визначена в жодному скрипті. Арність порту — найбільший індекс `I/S/F/Arg(a, N)`; '
             'лямбди з `a.Skip/a.Length` вважаються змінної арності.')
    L.append('')
    L.append('## Підсумок')
    L.append('| Контракт | реалізовано | відсутнє | розбіжність | інфо |')
    L.append('|---|---|---|---|---|')
    for c, st in summary.items():
        L.append('| %s | %d | %d | %d | %d |' % (c, st['implemented'], st['missing'], st['mismatched'], st['info']))
    L.append('')
    L.append('## Топ-%d найпомітніших для гравця розбіжностей' % top)
    L.append('| # | Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |')
    L.append('|---|---|---|---|---|---|')
    for i, it in enumerate(problems[:top], 1):
        L.append('| %d | %s | %s | %s | %s | %s |' % (i, it['contract'], md_escape(it['item']), md_escape(', '.join(it['sites'][:2]) +
                 (' (+%d)' % (len(it['sites']) - 2) if len(it['sites']) > 2 else '')), md_escape(it['status']), md_escape(it['evidence'])))
    L.append('')
    order = list(summary.keys())
    for c in order:
        rows = [it for it in problems if it['contract'] == c]
        if not rows:
            continue
        L.append('## %s — розбіжності (%d)' % (c, len(rows)))
        L.append('| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |')
        L.append('|---|---|---|---|---|')
        for it in rows:
            L.append('| %s | %s | %s | %s | %s |' % (c, md_escape(it['item']), md_escape(', '.join(it['sites'][:3]) +
                     (' (+%d)' % (len(it['sites']) - 3) if len(it['sites']) > 3 else '')), md_escape(it['status']), md_escape(it['evidence'])))
        L.append('')
    if infos:
        L.append('## Інформаційне (оригінал теж ігнорує, ціль не визначено, події-дані, не спрацювали в прогонах)')
        L.append('| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |')
        L.append('|---|---|---|---|---|')
        for it in infos:
            L.append('| %s | %s | %s | %s | %s |' % (it['contract'], md_escape(it['item']), md_escape(', '.join(it['sites'][:2])),
                     md_escape(it['status']), md_escape(it['evidence'])))
        L.append('')
    with open(md_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(L))
    return summary, problems


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--program', default=os.path.join(PORT, 'addon', 'merged', 'PROGRAM'))
    ap.add_argument('--game', default=os.path.dirname(PORT))
    ap.add_argument('--ref', default=os.path.join(PORT, 'reference', 'storm-engine'))
    ap.add_argument('--md', default=os.path.join(PORT, 'docs', 'CONTRACTS.md'))
    ap.add_argument('--json', default=os.path.join(PORT, 'docs', 'contracts.json'))
    ap.add_argument('--top', type=int, default=40)
    a = ap.parse_args()
    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass
    if not os.path.isdir(a.program):
        print('contract_audit: script tree not found: %s (run tools/unpack_stormex2.py + tools/merge_addon.py)' % a.program)
        return 0
    au = Audit(a)
    au.run()
    summary, problems = write_outputs(au, a.md, a.json, a.top)
    print('contract audit -> %s, %s' % (a.md, a.json))
    for c, st in summary.items():
        print('  %-24s ok %4d  missing %4d  mismatch %4d  info %4d' % (c, st['implemented'], st['missing'], st['mismatched'], st['info']))
    for it in problems[:10]:
        print('  [%5.1f] %s: %s — %s' % (it['score'], it['contract'], it['item'], it['status']))
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as e:      # a report tool must never break the test run
        import traceback
        traceback.print_exc()
        print('contract_audit: failed: %s' % e)
        sys.exit(0)
