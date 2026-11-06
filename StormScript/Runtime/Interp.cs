using System.Text;

namespace StormScript.Runtime;

/// <summary>
/// Tree-walking interpreter for Storm Script. Semantics follow the original VM (see docs/vm_semantics.md):
/// case-insensitive names, attributes are strings, "string + number" concatenates, missing attributes read as "".
/// </summary>
public sealed partial class Interp
{
    public sealed class FuncDef(FuncDecl decl, string segment)
    {
        public FuncDecl Decl = decl;
        public string Segment = segment;
    }

    sealed class Frame
    {
        public readonly Dictionary<string, Slot> L = new(StringComparer.OrdinalIgnoreCase);
        public string Name = "";
    }

    enum Flow { Normal, Break, Continue, Return }

    public readonly Dictionary<string, FuncDef> Funcs = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, Slot> Globals = new(StringComparer.OrdinalIgnoreCase);
    public Action<string> Log = _ => { };
    /// <summary>Called for an unknown variable name; may supply a value (e.g. a #define written with different case).</summary>
    public Func<string, V?>? UnknownVariable;
    public Random Rng = new(12345);
    public bool Exit;
    public long StepLimit = 400_000_000;

    // diagnostics ("what does the new engine still have to provide")
    public readonly Dictionary<string, int> UnresolvedFuncs = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, int> UnresolvedVars = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, int> RuntimeErrors = new();
    public readonly Dictionary<string, int> EngineCalls = new(StringComparer.OrdinalIgnoreCase);
    public int DuplicateFuncs;

    long steps;
    int depth;
    string curFunc = "";
    V retVal;
    readonly Frame globalFrame = new() { Name = "<global>" };

    // ------------------------------------------------------------------ registration
    public void Register(ScriptUnit unit, string segment)
    {
        foreach (var it in unit.Items)
            if (it is FuncDecl fd && fd.Body != null)
            {
                if (Funcs.ContainsKey(fd.Name) && Funcs[fd.Name].Segment == segment) DuplicateFuncs++;
                Funcs[fd.Name] = new FuncDef(fd, segment);
            }
        foreach (var it in unit.Items)
        {
            switch (it)
            {
                case VarDecl vd: GuardedDeclare(vd); break;
                case DeclGroup dg: foreach (var d in dg.Decls) GuardedDeclare(d); break;
            }
        }
    }

    void GuardedDeclare(VarDecl d)
    {
        try { Declare(d, globalFrame, Globals); }
        catch (ScriptError e) { Error($"global {d.Name}: {e.Message}", d); }
    }

    /// <summary>Functions of unloaded segments: an event handler left behind by a screen script (SetEventHandler without a matching DelEventHandler) is skipped silently, as the original compiler does for functions whose segment is gone.</summary>
    public readonly HashSet<string> UnloadedFuncs = new(StringComparer.OrdinalIgnoreCase);

    public int RemoveSegment(string segment)
    {
        var del = Funcs.Where(kv => kv.Value.Segment == segment).Select(kv => kv.Key).ToList();
        foreach (var k in del) { Funcs.Remove(k); UnloadedFuncs.Add(k); }
        return del.Count;
    }

    // ------------------------------------------------------------------ errors
    public void Error(string msg, Node? at = null)
    {
        string where = at != null ? $" [{Path.GetFileName(at.File)}:{at.Line}]" : "";
        string key = msg + where;
        RuntimeErrors[key] = RuntimeErrors.GetValueOrDefault(key) + 1;
        if (RuntimeErrors[key] == 1) Log("ERROR " + key);
    }

    ScriptError Err(string msg, Node? at = null)
        => new(at != null ? $"{msg} [{Path.GetFileName(at.File)}:{at.Line}]" : msg);

    // ------------------------------------------------------------------ calling
    /// <summary>Call a script function by name from the host. Errors are logged, not thrown.</summary>
    public V CallTop(string name, params V[] args)
    {
        try { return CallByName(name, args.ToList(), null); }
        catch (ScriptError e) { Error($"in {name}: {e.Message}"); return V.Void; }
    }

    /// <summary>Engine functions implemented by the host (the game window), registered by name.</summary>
    public readonly Dictionary<string, Func<V[], V>> HostFunctions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The control service: CreateControl / MapControl / LockControl ... (see <see cref="ControlTable"/>).</summary>
    public readonly ControlTable Controls = new();

    V CallByName(string name, List<V> args, Node? at)
    {
        if (Funcs.TryGetValue(name, out var fd)) return Invoke(fd, args, at);
        if (Builtins.Table.TryGetValue(name, out var bf)) return bf(this, args.ToArray());
        if (HostFunctions.TryGetValue(name, out var hf0)) return hf0(args.ToArray());
        if (UnloadedFuncs.Contains(name)) return V.Int(0);
        return Unresolved(name, args);
    }

    V Unresolved(string name, List<V> args)
    {
        UnresolvedFuncs[name] = UnresolvedFuncs.GetValueOrDefault(name) + 1;
        if (UnresolvedFuncs[name] == 1) Log($"UNRESOLVED function {name}({args.Count} args)");
        return V.Int(0);
    }

    static readonly HashSet<string> traceCalls = new((Environment.GetEnvironmentVariable("SEADOGS_TRACE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    V Invoke(FuncDef fd, List<V> args, Node? at, List<(int index, Slot caller)>? outs = null)
    {
        if (traceCalls.Count > 0 && traceCalls.Contains(fd.Decl.Name)) Log($"CALL {fd.Decl.Name}({string.Join(", ", args.Select(a => a.ToString()))}) at t={Now}");
        if (++depth > 400) { depth--; throw Err("call stack too deep in " + fd.Decl.Name, at); }
        var fr = new Frame { Name = fd.Decl.Name };
        string prevFunc = curFunc;
        curFunc = fd.Decl.Name;
        var ps = fd.Decl.Params;
        for (int i = 0; i < ps.Count; i++)
        {
            var s = new Slot(ps[i].Name, ps[i].Type);
            V v = i < args.Count ? args[i] : V.Void;
            if (ps[i].IsArray) s.Val = v;
            else if (ps[i].Type == "object") { s.Type = "ref"; s.Val = v.T == VT.Attr || v.T == VT.Slot ? v : V.A(null); }
            else s.Val = Conv.Coerce(v, ps[i].Type);
            fr.L[ps[i].Name] = s;
        }
        foreach (var d in HoistedLocals(fd.Decl))        // locals are function-wide: usable before their declaration line
            if (!fr.L.ContainsKey(d.Name)) fr.L[d.Name] = new Slot(d.Name, d.Type) { Val = DefaultValue(d.Type, d.Name) };
        retVal = V.Void;
        try
        {
            ExecBlock(fd.Decl.Body!.Body, fr);
        }
        finally { depth--; curFunc = prevFunc; }
        // "&variable" passed for a ref parameter: assigning that parameter (itemARef = curItem) re-points the caller's variable
        if (outs != null)
            foreach (var (i, callerSlot) in outs)
                if (i < ps.Count && fr.L.TryGetValue(ps[i].Name, out var ps2) && ps2.Val.T == VT.Attr && ps2.Val.Attr != null && callerSlot.Val.Attr != ps2.Val.Attr)
                    callerSlot.Val = ps2.Val;
        var r = retVal;
        retVal = V.Void;
        string rt = fd.Decl.RetType;
        if (rt == "void") return V.Void;
        if (r.T == VT.Void) return DefaultValue(rt, fd.Decl.Name);
        return Conv.IsAttrType(rt) ? (r.T is VT.Attr or VT.Slot ? r : V.A(null)) : Conv.Coerce(r, rt);       // "ref f() { return &globalInt; }" hands out the variable
    }

    readonly Dictionary<FuncDecl, List<VarDecl>> hoisted = new();

    List<VarDecl> HoistedLocals(FuncDecl fd)
    {
        if (hoisted.TryGetValue(fd, out var l)) return l;
        l = new List<VarDecl>();
        Walker.Walk(fd.Body, n => { if (n is VarDecl v && !v.IsArray) l.Add(v); });
        hoisted[fd] = l;
        return l;
    }

    // ------------------------------------------------------------------ declarations
    static V DefaultValue(string type, string name) => type switch
    {
        "int" or "bool" or "dword" => V.Int(0),
        "float" => V.Float(0),
        "string" => V.Str(""),
        "object" => V.A(new Attr(name)),
        _ => V.A(null),
    };

    void Declare(VarDecl d, Frame f, Dictionary<string, Slot> into)
    {
        Slot slot;
        if (into.TryGetValue(d.Name, out var existing) && existing.Type == d.Type && (existing.Arr != null) == d.IsArray)
            slot = existing;      // redeclared global (extern-style) or hoisted local: keep the live variable
        else
            slot = new Slot(d.Name, d.Type);

        if (d.IsArray)
        {
            int n = d.ArraySize != null ? Conv.ToInt(Eval(d.ArraySize, f)) : (d.Init is ArrayInit ai ? ai.Items.Count : 0);
            if (n < 0 || n > 5_000_000) throw Err($"bad array size {n} for {d.Name}", d);
            if (slot.Arr == null || slot.Arr.Length != n)
            {
                slot.Arr = new Slot[n];
                for (int i = 0; i < n; i++)
                    slot.Arr[i] = new Slot($"{d.Name}[{i}]", d.Type) { Val = DefaultValue(d.Type, $"{d.Name}[{i}]") };
            }
            if (d.Init is ArrayInit init)
            {
                // dialog lines: DLG_TEXT of "<name>_dialog.h" is replaced by the translation layer, line by line
                string? dlgFile = Language != null && d.Name.Equals("DLG_TEXT", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(d.File) : null;
                for (int i = 0; i < init.Items.Count && i < n; i++)
                {
                    var v = Eval(init.Items[i], f);
                    if (dlgFile != null && v.T == VT.String && Language!.DialogText(dlgFile, i) is { } tr) v = V.Str(tr);
                    SetSlot(slot.Arr[i], v, d);
                }
            }
        }
        else
        {
            if (slot.Val.T == VT.Void) slot.Val = DefaultValue(d.Type, d.Name);
            if (d.Init != null) SetSlot(slot, Eval(d.Init, f), d);
        }
        into[d.Name] = slot;
    }

    // ------------------------------------------------------------------ variables
    Slot? FindSlot(string name, Frame f)
    {
        if (f.L.TryGetValue(name, out var s)) return s;
        if (Globals.TryGetValue(name, out s)) return s;
        return null;
    }

    void SetSlot(Slot s, V v, Node? at)
    {
        if (s.Arr != null) throw Err($"cannot assign to array {s.Name}", at);
        switch (s.Type)
        {
            case "object":
                if (s.Val.Attr is { } root && v.Attr is { } src) { root.Clear(); root.CopyFrom(src); }
                break;
            case "ref":
            case "aref":
                if (s.Val.Slot is { Arr: null } target && ((v.T != VT.Attr && v.T != VT.Slot) || (v.T == VT.Attr && !Conv.IsAttrType(target.Type))))
                    SetSlot(target, Conv.Decay(v), at);       // an attribute read assigned to a ref that points at an int/float/string variable: its value                       // "void f(ref x) { x = 5; }" writes through to the caller's variable
                else
                    s.Val = v.T == VT.Attr || v.T == VT.Slot ? v : V.A(null);
                break;
            default:
                s.Val = Conv.Coerce(Conv.Decay(v), s.Type);
                break;
        }
    }

    Slot ElementSlot(IndexExpr ix, Frame f)
    {
        Slot? arr = ix.Target switch
        {
            IdentExpr id => FindSlot(id.Name, f),
            _ => Eval(ix.Target, f).Slot,
        };
        if (arr == null) throw Err("unknown array " + (ix.Target as IdentExpr)?.Name, ix);
        arr = ArrayOf(arr);
        if (arr.Arr == null) throw Err($"{arr.Name} is not an array", ix);
        int i = Conv.ToInt(Eval(ix.Index, f));
        if (i < 0 || i >= arr.Arr!.Length) throw Err($"invalid index {i} [size:{arr.Arr.Length}] of {arr.Name}", ix);
        return arr.Arr[i];
    }

    /// <summary>Resolve an expression to an attribute node (the "object" it denotes).</summary>
    Attr? AttrOf(Expr e, Frame f, bool create)
    {
        switch (e)
        {
            case IdentExpr id:
            {
                var s = FindSlot(id.Name, f);
                if (s == null) { NoVar(id); return null; }
                return s.Val.Attr;
            }
            case MemberExpr m:
            {
                var b = AttrOf(m.Target, f, create);
                if (b == null) return null;
                if (m.Name != null) return create ? b.GetOrCreate(m.Name) : b.Get(m.Name);
                // dynamic name: "obj.(s)" where s may be a dotted path such as "models.always.l1"
                string dyn = Conv.ToStr(Eval(m.Dynamic!, f));
                return b.Find(dyn, create);
            }
            case IndexExpr ix:
                return ElementSlot(ix, f).Val.Attr;
            default:
                return Eval(e, f).Attr;
        }
    }

    void NoVar(IdentExpr id)
    {
        UnresolvedVars[id.Name] = UnresolvedVars.GetValueOrDefault(id.Name) + 1;
        if (UnresolvedVars[id.Name] == 1) Log($"UNRESOLVED variable {id.Name} in {curFunc}() [{Path.GetFileName(id.File)}:{id.Line}]");
    }

    // ------------------------------------------------------------------ statements
    Flow ExecBlock(List<Stmt> body, Frame f)
    {
        foreach (var s in body)
        {
            var fl = Exec(s, f);
            if (fl != Flow.Normal) return fl;
        }
        return Flow.Normal;
    }

    Flow Exec(Stmt s, Frame f)
    {
        if (++steps > StepLimit) throw Err("step limit exceeded (infinite loop?)", s);
        switch (s)
        {
            case ExprStmt es: Eval(es.E, f); return Flow.Normal;
            case VarDecl vd: Declare(vd, f, f.L); return Flow.Normal;
            case DeclGroup dg: foreach (var d in dg.Decls) Declare(d, f, f.L); return Flow.Normal;
            case BlockStmt b: return ExecBlock(b.Body, f);
            case IfStmt i:
                if (Conv.ToBool(Eval(i.Cond, f))) return Exec(i.Then, f);
                return i.Else != null ? Exec(i.Else, f) : Flow.Normal;
            case WhileStmt w:
                while (Conv.ToBool(Eval(w.Cond, f)))
                {
                    var fl = Exec(w.Body, f);
                    if (fl == Flow.Break) break;
                    if (fl == Flow.Return) return fl;
                    if (++steps > StepLimit) throw Err("step limit exceeded (infinite loop?)", w);
                }
                return Flow.Normal;
            case DoWhileStmt dw:
                do
                {
                    var fl = Exec(dw.Body, f);
                    if (fl == Flow.Break) break;
                    if (fl == Flow.Return) return fl;
                    if (++steps > StepLimit) throw Err("step limit exceeded (infinite loop?)", dw);
                } while (Conv.ToBool(Eval(dw.Cond, f)));
                return Flow.Normal;
            case ForStmt fs:
                if (fs.Init != null) Exec(fs.Init, f);
                while (fs.Cond == null || Conv.ToBool(Eval(fs.Cond, f)))
                {
                    var fl = Exec(fs.Body, f);
                    if (fl == Flow.Break) break;
                    if (fl == Flow.Return) return fl;
                    if (fs.Step != null) Eval(fs.Step, f);
                    if (++steps > StepLimit) throw Err("step limit exceeded (infinite loop?)", fs);
                }
                return Flow.Normal;
            case SwitchStmt sw: return ExecSwitch(sw, f);
            case BreakStmt: return Flow.Break;
            case ContinueStmt: return Flow.Continue;
            case ReturnStmt r:
                retVal = r.Value != null ? Eval(r.Value, f) : V.Void;
                return Flow.Return;
            case FuncDecl: return Flow.Normal;   // local prototype
            case EmptyStmt: return Flow.Normal;
        }
        throw Err("cannot execute " + s.GetType().Name, s);
    }

    Flow ExecSwitch(SwitchStmt sw, Frame f)
    {
        var subj = Conv.Decay(Eval(sw.Subject, f));
        int start = -1, def = -1;
        for (int i = 0; i < sw.Cases.Count; i++)
        {
            var c = sw.Cases[i];
            if (c.Value == null) { def = i; continue; }
            if (Conv.ToBool(BinOp("==", Eval(c.Value, f), subj))) { start = i; break; }   // STACK_COMPARE: the case value is the LEFT operand (a string case is converted to an int subject)
        }
        if (start < 0) start = def;
        if (start < 0) return Flow.Normal;
        for (int i = start; i < sw.Cases.Count; i++)
        {
            var fl = ExecBlock(sw.Cases[i].Body, f);
            if (fl == Flow.Break) return Flow.Normal;
            if (fl != Flow.Normal) return fl;
        }
        return Flow.Normal;
    }

    // ------------------------------------------------------------------ expressions
    V Eval(Expr e, Frame f)
    {
        switch (e)
        {
            case NumberLit n:
                if (n.IsFloat) return V.Float(float.Parse(n.Text, System.Globalization.CultureInfo.InvariantCulture));
                if (n.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return V.Int(unchecked((int)Convert.ToUInt32(n.Text.Substring(2), 16)));
                return V.Int(unchecked((int)long.Parse(n.Text)));
            case StringLit s:
                if (Language != null && !s.TranslationChecked)
                {
                    s.TranslationChecked = true;
                    if (HasCyrillic(s.Value)) s.Translated = Language.LiteralText(Path.GetFileNameWithoutExtension(s.File), s.Value);
                }
                return V.Str(s.Translated ?? s.Value);
            case IdentExpr id:
            {
                var s = FindSlot(id.Name, f);
                if (s != null)
                {
                    if (s.Arr != null) return V.Ref(s);
                    if (s.Val.Slot is { Arr: null } tgt && Conv.IsAttrType(s.Type)) return tgt.Val;   // deref a ref to a scalar
                    return s.Val;
                }
                if (id.Name.Equals("true", StringComparison.OrdinalIgnoreCase)) return V.Int(1);
                if (id.Name.Equals("false", StringComparison.OrdinalIgnoreCase)) return V.Int(0);
                if (UnknownVariable?.Invoke(id.Name) is { } fallback) return fallback;
                NoVar(id);
                return V.Void;
            }
            case MemberExpr m:
            {
                var a = AttrOf(m, f, false);
                return a == null ? V.Str("") : V.A(a);
            }
            case IndexExpr ix:
            {
                var s = ElementSlot(ix, f);
                return s.Val;
            }
            case CallExpr c: return EvalCall(c, f);
            case UnaryExpr u: return EvalUnary(u, f);
            case BinaryExpr b: return EvalBinary(b, f);
            case AssignExpr a: return EvalAssign(a, f);
            case ArrayInit: throw Err("array initialiser outside declaration", e);
        }
        throw Err("cannot evaluate " + e.GetType().Name, e);
    }

    static bool HasCyrillic(string s) { foreach (char c in s) if (c >= 'А' && c <= 'я' || c == 'Ё' || c == 'ё') return true; return false; }

    V EvalUnary(UnaryExpr u, Frame f)
    {
        switch (u.Op)
        {
            case "&": return AddressOf(u.Operand, f);
            case "-":
            {
                var v = Conv.Decay(Eval(u.Operand, f));
                return v.T == VT.Float ? V.Float(-v.F) : V.Int(unchecked(-Conv.ToInt(v)));
            }
            case "+": return Conv.Decay(Eval(u.Operand, f));
            case "!": return V.Int(Conv.ToBool(Eval(u.Operand, f)) ? 0 : 1);
            case "~": return V.Int(~Conv.ToInt(Eval(u.Operand, f)));
            case "++":
            case "--":
            {
                var old = Conv.Decay(Eval(u.Operand, f));
                int d = u.Op == "++" ? 1 : -1;
                V nv = old.T == VT.Float ? V.Float(old.F + d) : V.Int(Conv.ToInt(old) + d);
                AssignTo(u.Operand, nv, f);
                return u.Postfix ? old : nv;
            }
        }
        throw Err("unknown unary " + u.Op, u);
    }

    V AddressOf(Expr e, Frame f)
    {
        switch (e)
        {
            case IdentExpr id:
            {
                var s = FindSlot(id.Name, f);
                if (s == null) { NoVar(id); return V.Void; }
                if (s.Arr == null && Conv.IsAttrType(s.Type)) return s.Val;
                return V.Ref(s);
            }
            case MemberExpr m: return V.A(AttrOf(m, f, true));
            case IndexExpr ix:
            {
                var s = ElementSlot(ix, f);
                return Conv.IsAttrType(s.Type) ? s.Val : V.Ref(s);
            }
        }
        return Eval(e, f);
    }

    V EvalBinary(BinaryExpr b, Frame f)
    {
        if (b.Op == "&&") return V.Int(Conv.ToBool(Eval(b.L, f)) && Conv.ToBool(Eval(b.R, f)) ? 1 : 0);
        if (b.Op == "||") return V.Int(Conv.ToBool(Eval(b.L, f)) || Conv.ToBool(Eval(b.R, f)) ? 1 : 0);
        return BinOp(b.Op, Eval(b.L, f), Eval(b.R, f), b);
    }

    static string CatStr(V v) => v.T switch
    {
        VT.Int => v.I.ToString(System.Globalization.CultureInfo.InvariantCulture),
        VT.Float => Conv.FloatToConcat(v.F),
        VT.String => v.S ?? "",
        _ => "",
    };

    public V BinOp(string op, V a, V b, Node? at = null)
    {
        a = Conv.Decay(a); b = Conv.Decay(b);
        bool anyStr = a.T == VT.String || b.T == VT.String;
        bool anyFloat = a.T == VT.Float || b.T == VT.Float;
        switch (op)
        {
            case "+":
                if (anyStr) return V.Str(CatStr(a) + CatStr(b));
                if (anyFloat) return V.Float(Conv.ToFloat(a) + Conv.ToFloat(b));
                return V.Int(unchecked(Conv.ToInt(a) + Conv.ToInt(b)));
            case "-":
                if (anyFloat) return V.Float(Conv.ToFloat(a) - Conv.ToFloat(b));
                return V.Int(unchecked(Conv.ToInt(a) - Conv.ToInt(b)));
            case "*":
                if (anyFloat) return V.Float(Conv.ToFloat(a) * Conv.ToFloat(b));
                return V.Int(unchecked(Conv.ToInt(a) * Conv.ToInt(b)));
            case "/":
                if (anyFloat)
                {
                    float d = Conv.ToFloat(b);
                    if (d == 0) { Error("Divide by zero", at); return V.Float(0); }
                    return V.Float(Conv.ToFloat(a) / d);
                }
                else
                {
                    int d = Conv.ToInt(b);
                    if (d == 0) { Error("Divide by zero", at); return V.Int(0); }
                    return V.Int(Conv.ToInt(a) / d);
                }
            case "%":
            {
                int d = Conv.ToInt(b);
                if (d == 0) { Error("Divide by zero (modulo)", at); return V.Int(0); }
                return V.Int(Conv.ToInt(a) % d);
            }
            case "&": return V.Int(Conv.ToInt(a) & Conv.ToInt(b));
            case "|": return V.Int(Conv.ToInt(a) | Conv.ToInt(b));
            case "^": return V.Int(Conv.ToInt(a) ^ Conv.ToInt(b));
            case "<<": return V.Int(Conv.ToInt(a) << Conv.ToInt(b));
            case ">>": return V.Int(Conv.ToInt(a) >> Conv.ToInt(b));
            case "==": case "!=": case "<": case ">": case "<=": case ">=":
                return V.Int(Compare(op, a, b) ? 1 : 0);
        }
        throw Err("unknown operator " + op, at);
    }

    static bool Compare(string op, V a, V b)
    {
        if (a.T == VT.Void) a = V.Int(0);
        if (b.T == VT.Void) b = V.Int(0);
        if (a.T == VT.String && b.T == VT.String)
        {
            switch (op)
            {
                case "==": return string.Equals(a.S, b.S, StringComparison.OrdinalIgnoreCase);
                case "!=": return !string.Equals(a.S, b.S, StringComparison.OrdinalIgnoreCase);
                default:   // the original VM orders strings by length only
                    return NumCmp(op, (a.S ?? "").Length, (b.S ?? "").Length);
            }
        }
        if (a.T == VT.String)   // string vs number: the left side is converted
        {
            if (b.T == VT.Float) return NumCmp(op, Conv.ToFloat(a), b.F);
            return NumCmp(op, Conv.ToInt(a), Conv.ToInt(b));
        }
        if (b.T == VT.String) return op == "!=";   // number vs string: the original yields false
        if (a.T == VT.Float || b.T == VT.Float) return NumCmp(op, Conv.ToFloat(a), Conv.ToFloat(b));
        return NumCmp(op, Conv.ToInt(a), Conv.ToInt(b));
    }

    static bool NumCmp(string op, double x, double y) => op switch
    {
        "==" => x == y, "!=" => x != y, "<" => x < y, ">" => x > y, "<=" => x <= y, ">=" => x >= y,
        _ => false,
    };

    V EvalAssign(AssignExpr a, Frame f)
    {
        V val = Eval(a.Value, f);
        if (a.Op != "=")
        {
            var cur = Eval(a.Target, f);
            val = BinOp(a.Op.Substring(0, a.Op.Length - 1), cur, val, a);
        }
        return AssignTo(a.Target, val, f);
    }

    V AssignTo(Expr target, V val, Frame f)
    {
        switch (target)
        {
            case IdentExpr id:
            {
                var s = FindSlot(id.Name, f);
                if (s == null)
                {
                    // undeclared: behave like an implicit global so the run can continue
                    NoVar(id);
                    s = new Slot(id.Name, val.T == VT.Attr ? "ref" : val.T == VT.Float ? "float" : val.T == VT.String ? "string" : "int");
                    s.Val = V.Void;
                    Globals[id.Name] = s;
                }
                SetSlot(s, val, target);
                return s.Val;
            }
            case MemberExpr m:
            {
                var node = AttrOf(m, f, true);
                if (node == null) throw Err("assignment to attribute of uninitialised reference", m);
                node.Value = Conv.ToStr(val);
                return val;
            }
            case IndexExpr ix:
            {
                var s = ElementSlot(ix, f);
                SetSlot(s, val, target);
                return s.Val;
            }
        }
        throw Err("invalid assignment target", target);
    }

    // ------------------------------------------------------------------ calls
    V EvalCall(CallExpr c, Frame f)
    {
        string name;
        if (c.IsDynamic)
        {
            name = Conv.ToStr(Eval(c.Callee, f));
        }
        else if (c.Callee is IdentExpr id) name = id.Name;
        else throw Err("call of non-function expression", c);

        // builtins that need unevaluated / lvalue arguments
        if (!c.IsDynamic && !Funcs.ContainsKey(name))
        {
            var special = EvalSpecial(name.ToLowerInvariant(), c, f);
            if (special.handled) return special.result;
        }

        var args = new List<V>(c.Args.Count);
        foreach (var a in c.Args) args.Add(Eval(a, f));
        if (Funcs.TryGetValue(name, out var fd))
        {
            List<(int, Slot)>? outs = null;
            // a "ref" variable that points at a caller's scalar (void f(ref x) { g(x); }) is handed on as the reference, not as its value
            for (int i = 0; i < c.Args.Count && i < fd.Decl.Params.Count; i++)
                if (c.Args[i] is IdentExpr rid && Conv.IsAttrType(fd.Decl.Params[i].Type) && !fd.Decl.Params[i].IsArray
                    && FindSlot(rid.Name, f) is { Arr: null } rs && rs.Val.T == VT.Slot && rs.Val.Slot is { Arr: null })
                    args[i] = rs.Val;
            for (int i = 0; i < c.Args.Count && i < fd.Decl.Params.Count; i++)
                if (c.Args[i] is UnaryExpr { Op: "&", Operand: IdentExpr oid } && Conv.IsAttrType(fd.Decl.Params[i].Type) && !fd.Decl.Params[i].IsArray
                    && FindSlot(oid.Name, f) is { Arr: null } cs && Conv.IsAttrType(cs.Type))
                    (outs ??= new()).Add((i, cs));
            return Invoke(fd, args, c, outs);
        }
        if (Builtins.Table.TryGetValue(name, out var bf))
        {
            EngineCalls[name] = EngineCalls.GetValueOrDefault(name) + 1;
            return bf(this, args.ToArray());
        }
        if (HostFunctions.TryGetValue(name, out var hf)) return hf(args.ToArray());
        return Unresolved(name, args);
    }

    (bool handled, V result) EvalSpecial(string lname, CallExpr c, Frame f)
    {
        switch (lname)
        {
            case "makeref":
            {
                if (c.Args.Count != 2) return (true, V.Void);
                var dst = SlotOf(c.Args[0], f);
                // makeref(r, someArray): the reference denotes the array itself
                if (c.Args[1] is IdentExpr aid && FindSlot(aid.Name, f) is { } asl && ArrayOf(asl).Arr != null)
                {
                    if (dst != null) dst.Val = V.Ref(ArrayOf(asl));
                    return (true, V.Void);
                }
                var src = Eval(c.Args[1], f);
                if (dst != null) dst.Val = src.T == VT.Attr ? src : V.A(null);
                return (true, V.Void);
            }
            case "findclass":
            case "findclassnext":
            {
                // FindClass(&arModel, "modelr") / FindClassNext(&arModel) on an aref: the aref is re-pointed at the entity's attribute root (the engine's semantics);
                // objects are bound by the builtins instead
                if (c.Args.Count < 1 || c.Args[0] is not UnaryExpr { Op: "&", Operand: IdentExpr aid } || FindSlot(aid.Name, f) is not { Arr: null } s
                    || !string.Equals(s.Type, "aref", StringComparison.OrdinalIgnoreCase)) return (false, V.Void);
                Entity? found;
                if (lname == "findclass")
                {
                    string cls = Conv.ToStr(Eval(c.Args[1], f));
                    found = Entities.FirstOrDefault(x => !x.Deleted && string.Equals(x.Class, cls, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    var cur = s.Val.Attr?.Entity;
                    int at = cur == null ? -1 : Entities.IndexOf(cur);
                    found = at < 0 ? null : Entities.Skip(at + 1).FirstOrDefault(x => !x.Deleted && string.Equals(x.Class, cur!.Class, StringComparison.OrdinalIgnoreCase));
                }
                if (found == null) return (true, V.Int(0));
                s.Val = V.A(found.Root);
                return (true, V.Int(1));
            }
            case "makearef":
            {
                if (c.Args.Count != 2) return (true, V.Void);
                var dst = SlotOf(c.Args[0], f);
                var node = AttrOf(c.Args[1], f, true);
                if (dst != null) dst.Val = V.A(node);
                return (true, V.Void);
            }
            case "clearref":
            {
                var dst = SlotOf(c.Args[0], f);
                if (dst != null) dst.Val = V.A(null);
                return (true, V.Void);
            }
            case "getarraysize":
            {
                var s = SlotOf(c.Args[0], f);
                if (s != null) s = ArrayOf(s);
                return (true, V.Int(s?.Arr?.Length ?? 0));
            }
            case "setarraysize":
            {
                var s = SlotOf(c.Args[0], f);
                if (s != null) s = ArrayOf(s);
                int n = Conv.ToInt(Eval(c.Args[1], f));
                if (s?.Arr != null && n > 0)
                {
                    var old = s.Arr;
                    var arr = new Slot[n];
                    for (int i = 0; i < n; i++)
                        arr[i] = i < old.Length ? old[i] : new Slot($"{s.Name}[{i}]", s.Type) { Val = DefaultValue(s.Type, $"{s.Name}[{i}]") };
                    s.Arr = arr;
                }
                return (true, V.Void);
            }
        }
        return (false, V.Void);
    }

    /// <summary>A ref parameter may hold the slot of an array: follow it.</summary>
    static Slot ArrayOf(Slot s) => s.Arr == null && s.Val.Slot is { Arr: not null } inner ? inner : s;

    Slot? SlotOf(Expr e, Frame f) => e switch
    {
        IdentExpr id => FindSlot(id.Name, f) ?? NoVarNull(id),
        IndexExpr ix => ElementSlot(ix, f),
        _ => null,
    };

    Slot? NoVarNull(IdentExpr id) { NoVar(id); return null; }
}
