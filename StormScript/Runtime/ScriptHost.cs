namespace StormScript.Runtime;

/// <summary>Loads script files (the main program and LoadSegment segments) into an interpreter.</summary>
public sealed class ScriptHost
{
    public readonly Interp It = new();
    public readonly Preprocessor Pp;
    readonly string root;
    readonly HashSet<string> loaded = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<string>> segmentFiles = new(StringComparer.OrdinalIgnoreCase);
    int handlersDone;
    public int FilesLoaded;
    public IReadOnlyCollection<string> LoadedSegments => loaded;

    public ScriptHost(string programRoot)
    {
        root = Path.GetFullPath(programRoot);
        Pp = new Preprocessor(root);
        It.SegmentLoader = LoadSegment;
        It.SegmentUnloader = n =>
        {
            if (!loaded.Remove(n)) return;
            It.RemoveSegment(n);
            if (segmentFiles.Remove(n, out var files)) Pp.Forget(files);
        };
        It.SegmentIsLoaded = n => loaded.Contains(n);
        // the shipped addon has "SCRIPT_ALIGN_Center" where the define is SCRIPT_ALIGN_CENTER: use the define as the author meant
        It.UnknownVariable = name =>
        {
            foreach (var (k, toks) in Pp.Defines)
                if (toks.Count == 1 && string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                    return toks[0].Kind switch
                    {
                        TokKind.Int => V.Int(Conv.ParseIntPrefix(toks[0].Text)),
                        TokKind.Float => V.Float(Conv.ParseFloatPrefix(toks[0].Text)),
                        TokKind.String => V.Str(toks[0].Text),
                        _ => null,
                    };
            return null;
        };
    }

    public bool LoadFile(string file, string segment)
    {
        string full = Path.IsPathRooted(file) ? file : Path.Combine(root, file.Replace('\\', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) { It.Log($"segment not found: {file}"); return false; }
        ScriptUnit unit;
        try
        {
            var toks = Pp.Run(full);
            unit = Parser.ParseUnit(toks);
        }
        catch (Exception e) when (e is LexException or ParseException)
        {
            It.Log("LOAD FAILED " + e.Message);
            return false;
        }
        foreach (var err in unit.Errors) It.Log("SYNTAX " + err);
        if (segment != "<main>") segmentFiles[segment] = new List<string>(Pp.LastRunFiles);
        It.Register(unit, segment);
        for (; handlersDone < Pp.EventHandlers.Count; handlersDone++)
        {
            var h = Pp.EventHandlers[handlersDone];
            if (h.Event.Count > 0) It.SetStaticHandler(h.Event[0].Text, h.Handler);
        }
        FilesLoaded++;
        return true;
    }

    public bool LoadSegment(string name)
    {
        if (loaded.Contains(name)) return true;
        if (!LoadFile(name, name)) return false;
        loaded.Add(name);
        return true;
    }
}
