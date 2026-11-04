namespace StormScript;

public sealed record EventHandlerDecl(List<Token> Event, string Handler, string File, int Line);

/// <summary>
/// Handles #include, #define NAME tokens..., #ifdef/#ifndef/#else/#endif, #event_handler(evt,"fn"); and
/// #libriary "name". Produces a flat token stream with defines substituted (tokens remember the define in Via).
///
/// Two passes: the original compiler resolves #define globally, regardless of order (Encounters.c includes a file that
/// uses MAX_MAP_ENCOUNTERS before Encounters.c defines it). Pass 1 collects every define in the include tree, pass 2
/// substitutes. Conditionals (#ifndef guards) are evaluated against the defines seen so far in each pass.
/// </summary>
public sealed class Preprocessor
{
    readonly string root;
    readonly HashSet<string> included = new(StringComparer.OrdinalIgnoreCase);   // files already emitted (persists across Run calls)
    HashSet<string> live = new(StringComparer.OrdinalIgnoreCase);                // defines "seen so far" for #ifdef evaluation
    HashSet<string> seen1 = new(StringComparer.OrdinalIgnoreCase);
    bool pass1;

    // #define names are case sensitive: items.h defines MAP/GOLD/POTION1 while scripts use the attributes .map/.gold/.potion1
    public Dictionary<string, List<Token>> Defines { get; } = new();
    public List<EventHandlerDecl> EventHandlers { get; } = new();
    public List<string> Libraries { get; } = new();
    public List<string> MissingIncludes { get; } = new();
    public List<string> IncludedFiles { get; } = new();
    /// <summary>Files emitted by the most recent Run (the entry and everything it newly included).</summary>
    public List<string> LastRunFiles { get; private set; } = new();

    /// <summary>Allow files to be included again (used when a segment is unloaded and may be loaded again).</summary>
    public void Forget(IEnumerable<string> files) { foreach (var f in files) included.Remove(f); }

    public Preprocessor(string programRoot) => root = programRoot;

    /// <summary>Preprocess one entry file (and everything it includes) into a single token list ending with Eof.</summary>
    public List<Token> Run(string entryFile)
    {
        string entryFull = Path.GetFullPath(entryFile);
        included.Remove(entryFull);           // an entry file may be (re)loaded

        var saved = new HashSet<string>(live, StringComparer.OrdinalIgnoreCase);
        pass1 = true;
        seen1 = new HashSet<string>(included, StringComparer.OrdinalIgnoreCase);
        Process(entryFull, new List<Token>());

        pass1 = false;
        live = saved;
        LastRunFiles = new List<string>();
        var output = new List<Token>();
        Process(entryFull, output);
        output.Add(new Token { Kind = TokKind.Eof, Text = "<eof>", File = entryFull, Line = 0 });
        return output;
    }

    string? Resolve(string name, string fromFile)
    {
        string rel = name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string a = Path.Combine(root, rel);
        if (File.Exists(a)) return Path.GetFullPath(a);
        string b = Path.Combine(Path.GetDirectoryName(fromFile) ?? root, rel);
        if (File.Exists(b)) return Path.GetFullPath(b);
        return null;
    }

    void Process(string full, List<Token> output)
    {
        if (!(pass1 ? seen1 : included).Add(full)) return;
        if (!pass1) { IncludedFiles.Add(full); LastRunFiles.Add(full); }
        var toks = Lexer.Lex(Lexer.ReadFile(full), full);

        // conditional compilation stack: (parentActive, thisActive, anyBranchTaken)
        var cond = new Stack<(bool parent, bool active, bool taken)>();
        bool Active() => cond.Count == 0 || cond.Peek().active;

        int i = 0;
        while (toks[i].Kind != TokKind.Eof)
        {
            var t = toks[i];
            bool startsLine = i == 0 || toks[i - 1].Line != t.Line;
            if (t.Kind == TokKind.Punct && t.Text == "#" && startsLine && toks[i + 1].Kind == TokKind.Ident && toks[i + 1].Line == t.Line)
            {
                string dir = toks[i + 1].Text.ToLowerInvariant();
                int line = t.Line;
                int j = i + 2;
                var rest = new List<Token>();
                while (toks[j].Kind != TokKind.Eof && toks[j].Line == line) rest.Add(toks[j++]);
                i = j;

                switch (dir)
                {
                    case "ifdef":
                    case "ifndef":
                    {
                        bool defined = rest.Count > 0 && live.Contains(rest[0].Text);
                        bool v = dir == "ifdef" ? defined : !defined;
                        bool parent = Active();
                        cond.Push((parent, parent && v, parent && v));
                        break;
                    }
                    case "else":
                        if (cond.Count == 0) throw new LexException("#else without #if", full, line);
                        {
                            var (parent, _, taken) = cond.Pop();
                            cond.Push((parent, parent && !taken, true));
                        }
                        break;
                    case "endif":
                        if (cond.Count == 0) throw new LexException("#endif without #if", full, line);
                        cond.Pop();
                        break;
                    default:
                        if (!Active()) break;
                        Directive(dir, rest, full, line, output);
                        break;
                }
                continue;
            }

            i++;
            if (!Active() || pass1) continue;
            Emit(t, output, 0);
        }
        if (cond.Count != 0) throw new LexException("unterminated #if", full, toks[^1].Line);
    }

    void Directive(string dir, List<Token> rest, string file, int line, List<Token> output)
    {
        switch (dir)
        {
            case "include":
            {
                if (rest.Count < 1 || rest[0].Kind != TokKind.String) throw new LexException("bad #include", file, line);
                string? p = Resolve(rest[0].Text, file);
                if (p == null)
                {
                    if (!pass1) MissingIncludes.Add($"{Path.GetFileName(file)}({line}): {rest[0].Text}");
                    return;
                }
                Process(p, output);
                break;
            }
            case "define":
            {
                if (rest.Count < 1 || rest[0].Kind != TokKind.Ident) throw new LexException("bad #define", file, line);
                live.Add(rest[0].Text);
                if (pass1) Defines[rest[0].Text] = rest.Skip(1).ToList();
                break;
            }
            case "event_handler":
            {
                if (pass1) break;
                // ( <event tokens> , "Handler" ) ;
                var sub = new List<Token>();
                foreach (var tk in rest) Emit(tk, sub, 0);
                int lp = sub.FindIndex(x => x.Is("("));
                int comma = sub.FindLastIndex(x => x.Is(","));
                int rp = sub.FindLastIndex(x => x.Is(")"));
                if (lp != 0 || comma < 0 || rp < comma + 2 || sub[comma + 1].Kind != TokKind.String)
                    throw new LexException("bad #event_handler", file, line);
                EventHandlers.Add(new EventHandlerDecl(sub.GetRange(1, comma - 1), sub[comma + 1].Text, file, line));
                break;
            }
            case "libriary":
            case "library":
                if (!pass1 && rest.Count > 0) Libraries.Add(rest[0].Text);
                break;
            default:
                throw new LexException($"unknown directive #{dir}", file, line);
        }
    }

    void Emit(Token t, List<Token> output, int depth)
    {
        if (t.Kind == TokKind.Ident && Defines.TryGetValue(t.Text, out var val) && depth < 16)
        {
            foreach (var v in val)
            {
                var c = v.Clone();
                c.Line = t.Line;
                c.File = t.File;
                if (c.Kind == TokKind.Ident && Defines.ContainsKey(c.Text))
                    Emit(c, output, depth + 1);
                else
                {
                    c.Via ??= t.Text;
                    output.Add(c);
                }
            }
            return;
        }
        output.Add(t);
    }
}
