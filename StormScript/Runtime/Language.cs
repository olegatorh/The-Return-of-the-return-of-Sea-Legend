using System.Text;

namespace StormScript.Runtime;

/// <summary>
/// The engine's text service: LanguageOpenFile / LanguageConvertString / XI_ConvertString.
/// Base texts come from RESOURCE\INI\TEXTS\RUSSIAN (fallback ENGLISH). A translation layer
/// (lang\&lt;language&gt;\texts\&lt;same file name&gt;, UTF-8) is merged on top key by key, so a partial translation works:
/// untranslated keys keep the base language. Dialog lines (DLG_TEXT) are translated by <see cref="DialogText"/>.
/// </summary>
public sealed class LanguageService
{
    public string Name { get; }                       // what LanguageGetLanguage() returns, e.g. "Ukrainian"
    readonly string textsRoot;
    readonly string overlayRoot;                      // lang\<language>
    readonly string[] baseLanguages;
    readonly List<Dictionary<string, string>?> open = new();
    readonly Dictionary<string, Dictionary<string, string>> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<int, string>> dialogs = new(StringComparer.OrdinalIgnoreCase);

    public LanguageService(string gameDir, string langDir, string language, params string[] baseLanguages)
    {
        Name = language;
        textsRoot = Path.Combine(gameDir, "RESOURCE", "INI", "TEXTS");
        overlayRoot = Path.Combine(langDir, language.ToLowerInvariant());
        this.baseLanguages = baseLanguages.Length > 0 ? baseLanguages : ["RUSSIAN", "ENGLISH"];
    }

    // ------------------------------------------------------------------ text files
    public Dictionary<string, string> Load(string file)
    {
        if (cache.TryGetValue(file, out var c)) return c;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in baseLanguages)
        {
            string p = Path.Combine(textsRoot, lang, file);
            if (File.Exists(p)) { Parse(Lexer.ReadFile(p), file, d); break; }
        }
        if (file.Equals("QuestBook.txt", StringComparison.OrdinalIgnoreCase))
        {
            // the quest book texts of the base game live in RESOURCE\QUESTSTEXT\quests_texts.txt ("#QUEST id", title line, "#TEXT n", text);
            // they sit below the overlay, so every translated key wins
            string qp = Path.Combine(textsRoot, "..", "..", "QUESTSTEXT", "quests_texts.txt");
            if (File.Exists(qp)) ParseQuestTexts(Lexer.ReadFile(qp), d);
        }
        string ov = Path.Combine(overlayRoot, "texts", file);
        if (File.Exists(ov)) Parse(File.ReadAllText(ov, new UTF8Encoding(false)), file, d);
        cache[file] = d;
        return d;
    }

    /// <summary>Quest file format: "#QUEST id" + title line, then any number of "#TEXT key" + body lines. Keys: id_title, id_key.</summary>
    internal static void ParseQuestTexts(string text, Dictionary<string, string> into)
    {
        string quest = "", key = "";
        var body = new List<string>();
        void Flush()
        {
            if (quest.Length > 0 && key.Length > 0 && !into.ContainsKey(quest + "_" + key))
                into[quest + "_" + key] = string.Join("\n", body).Trim();
            body.Clear();
        }
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("#QUEST", StringComparison.Ordinal))
            {
                Flush(); quest = line.Substring(6).Trim(); key = "title";
            }
            else if (line.StartsWith("#TEXT", StringComparison.Ordinal))
            {
                Flush(); key = line.Substring(5).Trim();
            }
            else body.Add(line);
        }
        Flush();
    }

    static void Parse(string text, string file, Dictionary<string, string> into)
    {
        if (file.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                int eq = line.IndexOf('=');
                if (eq < 0 || line.TrimStart().StartsWith(';')) continue;
                if (!line.Substring(0, eq).Trim().Equals("string", StringComparison.OrdinalIgnoreCase)) continue;
                string rest = line.Substring(eq + 1);
                int comma = rest.IndexOf(',');
                int q1 = rest.IndexOf('"'), q2 = rest.LastIndexOf('"');
                if (comma < 0 || q1 < 0 || q2 <= q1) continue;
                into[rest.Substring(0, comma).Trim()] = rest.Substring(q1 + 1, q2 - q1 - 1);
            }
            return;
        }
        // "key{value}" blocks (a value may span lines)
        int pos = 0;
        while (pos < text.Length)
        {
            int open = text.IndexOf('{', pos);
            if (open < 0) break;
            int close = text.IndexOf('}', open + 1);
            if (close < 0) break;
            string key = text.Substring(pos, open - pos);
            // drop comment lines that precede the key
            var lines = key.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !l.TrimStart().StartsWith(';') && l.Trim().Length > 0);
            key = string.Join(" ", lines).Trim();
            if (key.Length > 0)
            {
                // str_service.cpp GetNextUsersString: blanks and line breaks on both sides of the data are cut (a value of blanks only keeps one character: the "newline" key is one line break)
                string raw = text.Substring(open + 1, close - open - 1), val = raw.Trim(' ', '\t', '\r', '\n');
                into[key] = val.Length == 0 && raw.Length > 0 ? "\n" : val;
            }
            pos = close + 1;
        }
    }

    public int Open(string file)
    {
        open.Add(Load(file));
        return open.Count - 1;
    }

    public string Convert(int id, string key)
    {
        if (id >= 0 && id < open.Count && open[id] != null)
            return open[id]!.TryGetValue(key, out var v) ? v : "";      // LanguageConvertString: a key the file lacks gives "" (the control page skips such rows, e.g. ChrBlock_Unlock)
        return Global(key, key);
    }

    public void Close(int id) { if (id >= 0 && id < open.Count) open[id] = null; }

    /// <summary>Strings of the global file (common.ini): used by XI_ConvertString and interface node captions.</summary>
    public string Global(string key, string? fallback = null)
        => Load("common.ini").TryGetValue(key, out var v) ? v : fallback ?? key;

    // ------------------------------------------------------------------ literals
    readonly Dictionary<string, Dictionary<string, string>> literals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Translation of a Cyrillic string literal of a script file, looked up by its exact text in
    /// lang/&lt;language&gt;/strings/&lt;script file name&gt;.tsv ("russian&lt;TAB&gt;translation"). Null if there is none.
    /// </summary>
    public string? LiteralText(string scriptFile, string russian)
    {
        if (PolishLiterals().TryGetValue(russian, out var pol)) return pol;   // hand-polished sentences (_polish.tsv) win over everything
        if (!literals.TryGetValue(scriptFile, out var d))
        {
            d = new Dictionary<string, string>();
            // "<name>.tsv" for the dialog script and "<name>.h.tsv" for its header (both literals use the same base name)
            foreach (var suffix in new[] { ".tsv", ".h.tsv" })
            {
                string p = Path.Combine(overlayRoot, "strings", scriptFile + suffix);
                if (!File.Exists(p)) continue;
                foreach (var line in File.ReadAllLines(p, new UTF8Encoding(false)))
                {
                    int tab = line.IndexOf('	');
                    if (tab > 0) d[line.Substring(0, tab)] = line.Substring(tab + 1);   // an empty translation is valid (drops a gender ending)
                }
            }
            literals[scriptFile] = d;
        }
        if (d.TryGetValue(russian, out var t)) return t;
        if (GlobalLiterals().TryGetValue(russian, out t)) return t;       // translation memory: the same text anywhere
        return MachineLiterals().TryGetValue(russian, out t) ? t : null;  // machine-translated draft, lowest priority
    }

    Dictionary<string, string>? polishLiterals;

    Dictionary<string, string> PolishLiterals()
    {
        if (polishLiterals != null) return polishLiterals;
        polishLiterals = new Dictionary<string, string>();
        string p = Path.Combine(overlayRoot, "strings", "_polish.tsv");
        if (File.Exists(p))
            foreach (var line in File.ReadAllLines(p, new UTF8Encoding(false)))
            {
                int tab = line.IndexOf('	');
                if (tab <= 0) continue;
                string key = line.Substring(0, tab), val = line.Substring(tab + 1);
                // a script literal that ends with a space is a fragment before a concatenated number / name ("... " + price): the hand-written value must keep that space
                if (key.EndsWith(' ') && val.Length > 0 && !val.EndsWith(' ')) val += " ";
                polishLiterals[key] = val;
            }
        return polishLiterals;
    }

    Dictionary<string, string>? machineLiterals;

    Dictionary<string, string> MachineLiterals()
    {
        if (machineLiterals != null) return machineLiterals;
        machineLiterals = new Dictionary<string, string>();
        string p = Path.Combine(overlayRoot, "strings", "_mt.tsv");
        if (File.Exists(p))
            foreach (var line in File.ReadAllLines(p, new UTF8Encoding(false)))
            {
                int tab = line.IndexOf('	');
                if (tab > 0) machineLiterals[line.Substring(0, tab)] = line.Substring(tab + 1);
            }
        return machineLiterals;
    }

    Dictionary<string, string>? globalLiterals;

    Dictionary<string, string> GlobalLiterals()
    {
        if (globalLiterals != null) return globalLiterals;
        globalLiterals = new Dictionary<string, string>();
        // _global.tsv: complete sentences collected from the per-file translations; _common.tsv: hand-translated frequent pieces
        foreach (var name in new[] { "_global.tsv", "_common.tsv", "_common_extra.tsv" })
        {
            string p = Path.Combine(overlayRoot, "strings", name);
            if (!File.Exists(p)) continue;
            foreach (var line in File.ReadAllLines(p, new UTF8Encoding(false)))
            {
                int tab = line.IndexOf('	');
                if (tab > 0) globalLiterals[line.Substring(0, tab)] = line.Substring(tab + 1);
            }
        }
        return globalLiterals;
    }

    // ------------------------------------------------------------------ dialogs
    /// <summary>Translated line <paramref name="index"/> of DLG_TEXT of a dialog header, or null if not translated.</summary>
    public string? DialogText(string dialogFile, int index)
    {
        if (!dialogs.TryGetValue(dialogFile, out var d))
        {
            d = new Dictionary<int, string>();
            string p = Path.Combine(overlayRoot, "dialogs", dialogFile + ".tsv");
            if (File.Exists(p))
                foreach (var line in File.ReadAllLines(p, new UTF8Encoding(false)))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0 && int.TryParse(line.AsSpan(0, tab), out int i)) d[i] = line.Substring(tab + 1);
                }
            dialogs[dialogFile] = d;
        }
        return d.TryGetValue(index, out var t) ? t : null;
    }
}
