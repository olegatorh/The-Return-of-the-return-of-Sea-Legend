namespace StormScript;

public sealed class ParseException(string message, string file, int line) : Exception($"{System.IO.Path.GetFileName(file)}({line}): {message}")
{
    public string File { get; } = file;
    public int Line { get; } = line;
}

public sealed class Parser
{
    static readonly HashSet<string> TypeNames = new(StringComparer.OrdinalIgnoreCase) { "int", "float", "string", "bool", "ref", "aref", "object", "void", "dword" };

    readonly List<Token> t;
    int p;

    public Parser(List<Token> tokens) { t = tokens; }

    public static ScriptUnit ParseUnit(List<Token> tokens)
    {
        var parser = new Parser(tokens);
        var unit = new ScriptUnit { EntryFile = tokens.Count > 0 ? tokens[0].File : "" };
        while (parser.Cur.Kind != TokKind.Eof)
        {
            int before = parser.p;
            try { parser.TopLevel(unit.Items); }
            catch (ParseException e)
            {
                parser.Errors.Add(e.Message);
                parser.Recover();
                if (parser.p == before) parser.p++;
                else if (parser.IsPunct("}")) parser.p++;   // stray closer at top level
            }
        }
        unit.Warnings.AddRange(parser.Warnings);
        unit.Errors.AddRange(parser.Errors);
        return unit;
    }

    // ---------------------------------------------------------------- helpers
    Token Cur => t[p];
    Token Peek(int n = 1) => t[Math.Min(p + n, t.Count - 1)];
    bool IsPunct(string s) => Cur.Kind == TokKind.Punct && Cur.Text == s;
    bool IsKw(string s) => Cur.Kind == TokKind.Ident && string.Equals(Cur.Text, s, StringComparison.OrdinalIgnoreCase);

    ParseException Err(string msg) => new(msg + $" (found '{Cur.Text}')", Cur.File, Cur.Line);

    Token Expect(string punct)
    {
        if (!IsPunct(punct)) throw Err($"expected '{punct}'");
        return t[p++];
    }

    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();

    /// <summary>Skip to the end of the broken statement: past the next ';' at nesting depth 0, or up to an unmatched '}'.</summary>
    void Recover()
    {
        int depth = 0;
        while (Cur.Kind != TokKind.Eof)
        {
            if (Cur.Kind == TokKind.Punct)
            {
                switch (Cur.Text)
                {
                    case "(": case "[": case "{": depth++; break;
                    case ")": case "]": if (depth > 0) depth--; break;
                    case "}":
                        if (depth == 0) return;
                        depth--;
                        if (depth == 0) { p++; return; }   // a whole {...} group closed: statement over
                        break;
                    case ";":
                        if (depth == 0) { p++; return; }
                        break;
                }
            }
            p++;
        }
    }

    /// <summary>Parse a statement; on a syntax error record it, resynchronise and return null.</summary>
    Stmt? SafeStatement()
    {
        int before = p;
        try { return Statement(); }
        catch (ParseException e)
        {
            Errors.Add(e.Message);
            Recover();
            if (p == before) p++;
            return null;
        }
    }

    /// <summary>
    /// The original compiler tolerates a missing ';' at the end of a line (it is present in shipped scripts).
    /// We do the same, recording a warning.
    /// </summary>
    void ExpectSemi()
    {
        // shipped scripts contain stray ')' and ', ;' that the original compiler ignores
        while ((IsPunct(")") || (IsPunct(",") && Peek().Is(";"))) && p > 0)
        {
            Warnings.Add($"{System.IO.Path.GetFileName(Cur.File)}({Cur.Line}): stray '{Cur.Text}' ignored");
            p++;
        }
        if (Accept(";")) return;
        if (p > 0 && (Cur.Line != t[p - 1].Line || IsPunct("}") || Cur.Kind == TokKind.Eof))
        {
            Warnings.Add($"{System.IO.Path.GetFileName(t[p - 1].File)}({t[p - 1].Line}): missing ';'");
            return;
        }
        throw Err("expected ';'");
    }

    Expr ArrayInitializer()
    {
        var start = Expect("{");
        var items = new List<Expr>();
        while (!IsPunct("}"))
        {
            items.Add(IsPunct("{") ? ArrayInitializer() : ParseAssign());
            if (!Accept(",")) break;
        }
        Expect("}");
        return At(new ArrayInit(items), start);
    }

    bool Accept(string punct)
    {
        if (IsPunct(punct)) { p++; return true; }
        return false;
    }

    string ExpectIdent()
    {
        if (Cur.Kind != TokKind.Ident) throw Err("expected identifier");
        return t[p++].Text;
    }

    static T At<T>(T n, Token tok) where T : Node { n.Line = tok.Line; n.File = tok.File; return n; }

    bool IsTypeStart() => Cur.Kind == TokKind.Ident && TypeNames.Contains(Cur.Text);

    // ---------------------------------------------------------------- top level
    void TopLevel(List<Stmt> items)
    {
        var start = Cur;
        if (Accept(";")) return;
        string kind = "";
        if (IsKw("native") || IsKw("extern") || IsKw("const")) { kind = Cur.Text.ToLowerInvariant(); p++; }
        if (!IsTypeStart()) throw Err("expected declaration");
        string type = t[p++].Text.ToLowerInvariant();
        if (Peek().Is("(") && Cur.Kind == TokKind.Ident)
        {
            items.Add(FuncRest(kind, type, start));
            return;
        }
        items.Add(DeclRest(type, start));
    }

    FuncDecl FuncRest(string kind, string retType, Token start)
    {
        string name = ExpectIdent();
        Expect("(");
        var ps = new List<Param>();
        if (!IsPunct(")"))
        {
            do
            {
                if (!IsTypeStart()) throw Err("expected parameter type");
                string pt = t[p++].Text.ToLowerInvariant();
                string pn = ExpectIdent();
                bool arr = false;
                if (Accept("[")) { if (!IsPunct("]")) ParseExpr(); Expect("]"); arr = true; }
                ps.Add(new Param(pt, pn, arr));
            } while (Accept(","));
        }
        Expect(")");
        BlockStmt? body = null;
        if (IsPunct("{")) body = Block();
        else ExpectSemi();
        return At(new FuncDecl(kind, retType, name, ps, body), start);
    }

    /// <summary>Parses "name [= init] {, name [= init]} ;" after the type has been consumed.</summary>
    Stmt DeclRest(string type, Token start)
    {
        var list = new List<VarDecl>();
        do
        {
            var nameTok = Cur;
            string name = ExpectIdent();
            Expr? size = null;
            bool arr = false;
            if (Accept("[")) { arr = true; if (!IsPunct("]")) size = ParseExpr(); Expect("]"); }
            Expr? init = null;
            if (Accept("=")) init = IsPunct("{") ? ArrayInitializer() : ParseAssign();
            list.Add(At(new VarDecl(type, name, size, arr, init), nameTok));
        } while (Accept(","));
        ExpectSemi();
        return list.Count == 1 ? list[0] : At(new DeclGroup(list), start);
    }

    // ---------------------------------------------------------------- statements
    BlockStmt Block()
    {
        var start = Expect("{");
        var body = new List<Stmt>();
        while (!IsPunct("}"))
        {
            if (Cur.Kind == TokKind.Eof) throw Err("unexpected end of file in block");
            var s = SafeStatement();
            if (s != null) body.Add(s);
        }
        p++;
        return At(new BlockStmt(body), start);
    }

    Stmt Statement()
    {
        while (IsPunct(")"))   // stray ')' after an if-condition, ignored by the original compiler
        {
            Warnings.Add($"{System.IO.Path.GetFileName(Cur.File)}({Cur.Line}): stray ')' ignored");
            p++;
        }
        var start = Cur;
        if (IsPunct("{")) return Block();
        if (Accept(";")) return At(new EmptyStmt(), start);

        if (Cur.Kind == TokKind.Ident)
        {
            switch (Cur.Text.ToLowerInvariant())
            {
                case "if":
                {
                    p++;
                    Expect("(");
                    var c = ParseExpr();
                    Expect(")");
                    var th = Statement();
                    Stmt? el = null;
                    if (IsKw("else")) { p++; el = Statement(); }
                    return At(new IfStmt(c, th, el), start);
                }
                case "while":
                {
                    p++;
                    Expect("(");
                    var c = ParseExpr();
                    Expect(")");
                    return At(new WhileStmt(c, Statement()), start);
                }
                case "do":
                {
                    p++;
                    var body = Statement();
                    if (!IsKw("while")) throw Err("expected 'while'");
                    p++;
                    Expect("(");
                    var c = ParseExpr();
                    Expect(")");
                    ExpectSemi();
                    return At(new DoWhileStmt(body, c), start);
                }
                case "for":
                {
                    p++;
                    Expect("(");
                    Stmt? init = null;
                    if (!IsPunct(";"))
                    {
                        if (IsTypeStart()) { string ty = t[p++].Text.ToLowerInvariant(); init = DeclRest(ty, Cur); }
                        else { var e = ParseExpr(); Expect(";"); init = At(new ExprStmt(e), start); }
                    }
                    else p++;
                    Expr? cond = IsPunct(";") ? null : ParseExpr();
                    Expect(";");
                    Expr? step = IsPunct(")") ? null : ParseExpr();
                    Expect(")");
                    return At(new ForStmt(init, cond, step, Statement()), start);
                }
                case "switch":
                    return Switch();
                case "break": p++; ExpectSemi(); return At(new BreakStmt(), start);
                case "continue": p++; ExpectSemi(); return At(new ContinueStmt(), start);
                case "return":
                {
                    p++;
                    Expr? v = IsPunct(";") || IsPunct("}") ? null : ParseExpr();
                    ExpectSemi();
                    return At(new ReturnStmt(v), start);
                }
            }

            if (IsTypeStart() && Peek().Kind == TokKind.Ident)
            {
                string ty = t[p++].Text.ToLowerInvariant();
                if (Peek().Is("("))   // local prototype: "void wdmInitWorldMap();"
                    return FuncRest("", ty, start);
                return DeclRest(ty, start);
            }
            if ((IsKw("native") || IsKw("extern")) && Peek().Kind == TokKind.Ident)
            {
                string kind = t[p++].Text.ToLowerInvariant();
                string ty = t[p++].Text.ToLowerInvariant();
                return FuncRest(kind, ty, start);
            }
        }

        var ex = ParseExpr();
        ExpectSemi();
        return At(new ExprStmt(ex), start);
    }

    Stmt Switch()
    {
        var start = Cur;
        p++;
        Expect("(");
        var subject = ParseExpr();
        Expect(")");
        Expect("{");
        var cases = new List<SwitchCase>();
        while (!IsPunct("}"))
        {
            var ct = Cur;
            Expr? val;
            if (IsKw("case")) { p++; val = ParseExpr(); }
            else if (IsKw("default")) { p++; val = null; }
            else throw Err("expected 'case' or 'default'");
            Expect(":");
            var body = new List<Stmt>();
            while (!IsPunct("}") && !IsKw("case") && !IsKw("default"))
            {
                if (Cur.Kind == TokKind.Eof) throw Err("unexpected end of file in switch");
                var s = SafeStatement();
                if (s != null) body.Add(s);
            }
            cases.Add(At(new SwitchCase(val, body), ct));
        }
        p++;
        return At(new SwitchStmt(subject, cases), start);
    }

    // ---------------------------------------------------------------- expressions
    Expr ParseExpr() => ParseAssign();

    static readonly HashSet<string> AssignOps = new() { "=", "+=", "-=", "*=", "/=", "%=" };

    Expr ParseAssign()
    {
        var left = Binary(1);
        if (Cur.Kind == TokKind.Punct && AssignOps.Contains(Cur.Text))
        {
            var op = t[p++];
            var right = ParseAssign();
            return At(new AssignExpr(op.Text, left, right), op);
        }
        return left;
    }

    static int Prec(string op) => op switch
    {
        "||" => 1,
        "&&" => 2,
        "|" => 3,
        "^" => 4,
        "&" => 5,
        "==" or "!=" => 6,
        "<" or ">" or "<=" or ">=" => 7,
        "<<" or ">>" => 8,
        "+" or "-" => 9,
        "*" or "/" or "%" => 10,
        _ => 0,
    };

    Expr Binary(int minPrec)
    {
        var left = Unary();
        while (Cur.Kind == TokKind.Punct)
        {
            int pr = Prec(Cur.Text);
            if (pr == 0 || pr < minPrec) break;
            var op = t[p++];
            var right = Binary(pr + 1);
            left = At(new BinaryExpr(op.Text, left, right), op);
        }
        return left;
    }

    Expr Unary()
    {
        var tk = Cur;
        if (tk.Kind == TokKind.Punct && (tk.Text is "-" or "+" or "!" or "~" or "&" or "++" or "--"))
        {
            p++;
            return At(new UnaryExpr(tk.Text, Unary(), false), tk);
        }
        if (tk.Kind == TokKind.Ident && tk.Text.Equals("call", StringComparison.OrdinalIgnoreCase) && Peek().Kind == TokKind.Ident)
        {
            p++;
            var callee = Postfix(Primary(), noCall: true);
            Expect("(");
            var args = Args();
            return Postfix(At(new CallExpr(callee, args, true), tk));
        }
        return Postfix(Primary());
    }

    List<Expr> Args()
    {
        var args = new List<Expr>();
        if (!IsPunct(")"))
        {
            do args.Add(ParseAssign()); while (Accept(","));
        }
        Expect(")");
        return args;
    }

    Expr Postfix(Expr e, bool noCall = false)
    {
        while (true)
        {
            var tk = Cur;
            if (tk.Kind != TokKind.Punct) return e;
            if (tk.Text == "(" && !noCall)
            {
                p++;
                e = At(new CallExpr(e, Args(), false), tk);
            }
            else if (tk.Text == ".")
            {
                p++;
                if (Accept("("))
                {
                    var d = ParseExpr();
                    Expect(")");
                    e = At(new MemberExpr(e, null, d), tk);
                }
                else if (Cur.Kind == TokKind.Ident || Cur.Kind == TokKind.Int)
                    e = At(new MemberExpr(e, t[p++].Text, null), tk);
                else throw Err("expected attribute name after '.'");
            }
            else if (tk.Text == "[")
            {
                p++;
                var idx = ParseExpr();
                Expect("]");
                e = At(new IndexExpr(e, idx), tk);
            }
            else if (tk.Text is "++" or "--")
            {
                p++;
                e = At(new UnaryExpr(tk.Text, e, true), tk);
            }
            else return e;
        }
    }

    Expr Primary()
    {
        var tk = Cur;
        switch (tk.Kind)
        {
            case TokKind.Int: p++; return At(new NumberLit(tk.Text, false, tk.Via), tk);
            case TokKind.Float: p++; return At(new NumberLit(tk.Text, true, tk.Via), tk);
            case TokKind.String: p++; return At(new StringLit(tk.Text, tk.Via), tk);
            case TokKind.Ident: p++; return At(new IdentExpr(tk.Text), tk);
            case TokKind.Punct when tk.Text == "(":
            {
                p++;
                var e = ParseExpr();
                Expect(")");
                return e;
            }
        }
        throw Err("expected expression");
    }
}
