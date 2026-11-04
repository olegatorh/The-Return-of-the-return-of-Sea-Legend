using System.Text;
using StormScript.Runtime;

static class RunCommand
{
    // ssc run <PROGRAM dir> [--entry seadogs.c] [--report file] [--step kind:arg]...
    //   steps run in order:  call:Func   set:var=value   sim:seconds   dump:path.in.object
    // default steps: call:Main, sim:5
    public static int Run(string root, string[] opts)
    {
        string entry = "seadogs.c", report = "", lang = "", langDir = "", game = "";
        var steps = new List<string>();
        for (int i = 0; i < opts.Length; i++)
        {
            switch (opts[i])
            {
                case "--entry": entry = opts[++i]; break;
                case "--report": report = opts[++i]; break;
                case "--lang": lang = opts[++i]; break;
                case "--game": game = opts[++i]; break;
                case "--lang-dir": langDir = opts[++i]; break;
                case "--step": steps.Add(opts[++i]); break;
            }
        }
        if (steps.Count == 0) { steps.Add("call:Main"); steps.Add("sim:5"); }

        var log = new StringBuilder();
        var host = new ScriptHost(root);
        host.It.Log = s => { lock (log) log.AppendLine(s.TrimEnd()); };
        EngineStubs.Install(host.It);
        if (lang.Length > 0)
            host.It.Language = new LanguageService(game.Length > 0 ? game : Path.GetFullPath(Path.Combine(root, "..")), langDir, lang);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"loading {entry} ...");
        if (!host.LoadFile(entry, "<main>")) { Console.WriteLine(log); return 2; }
        Console.WriteLine($"loaded: {host.It.Funcs.Count} functions, {host.It.Globals.Count} globals, {host.It.Handlers.Count} static event names ({sw.ElapsedMilliseconds} ms)");

        foreach (var step in steps)
        {
            int c = step.IndexOf(':');
            string kind = c < 0 ? step : step.Substring(0, c), arg = c < 0 ? "" : step.Substring(c + 1);
            switch (kind)
            {
                case "call":
                    log.AppendLine($"--- call {arg}()");
                    host.It.CallTop(arg);
                    break;
                case "set":
                {
                    int eq = arg.IndexOf('=');
                    host.It.SetGlobal(arg.Substring(0, eq), arg.Substring(eq + 1));
                    break;
                }
                case "sim":
                    log.AppendLine($"--- simulate {arg}s");
                    for (int t = 0; t < int.Parse(arg) * 10 && !host.It.Exit; t++)
                    {
                        host.It.Advance(100);
                        host.It.FireEvent("frame");
                    }
                    break;
                case "chars":   // list the characters standing in a location with their dialog file
                    log.AppendLine($"--- characters in {arg}");
                    if (host.It.Globals.TryGetValue("Characters", out var chs) && chs.Arr != null)
                        foreach (var chSlot in chs.Arr)
                        {
                            var a = chSlot.Val.Attr;
                            if (a?.Get("location")?.Value is { } loc && string.Equals(loc, arg, StringComparison.OrdinalIgnoreCase))
                                log.AppendLine($"  [{a.Get("index")?.Value}] {a.Get("id")?.Value} | type={a.Get("chr_ai")?.Get("type")?.Value} tmpl={a.Get("chr_ai")?.Get("tmpl")?.Value} | {a.Get("name")?.Value} {a.Get("lastname")?.Value} | dialog: {a.Get("Dialog")?.Get("Filename")?.Value} | model: {a.Get("model")?.Value}");
                        }
                    break;
                case "dump":
                    log.AppendLine($"--- dump {arg}");
                    host.It.DumpPath(arg);
                    break;
            }
        }
        Console.WriteLine($"done in {sw.ElapsedMilliseconds} ms, simulated {host.It.Now} ms, exit requested: {host.It.Exit}");

        var rep = new StringBuilder();
        void Section<T>(string title, IEnumerable<KeyValuePair<string, T>> items, int max = 60)
        {
            var list = items.ToList();
            rep.AppendLine($"== {title}: {list.Count}");
            foreach (var kv in list.Take(max)) rep.AppendLine($"  {kv.Key}  {kv.Value}");
        }
        Section("unresolved functions (engine API still to implement)", host.It.UnresolvedFuncs.OrderByDescending(k => k.Value));
        Section("unresolved variables", host.It.UnresolvedVars.OrderByDescending(k => k.Value));
        Section("runtime errors", host.It.RuntimeErrors.OrderByDescending(k => k.Value), 40);
        Section("engine messages received by stub entities (class | format | msg id)", host.It.MessageStats.OrderByDescending(k => k.Value), 40);
        Section("entities created", host.It.Entities.GroupBy(e => e.Class).ToDictionary(g => g.Key, g => g.Count()).OrderByDescending(k => k.Value));
        Section("global object arrays", host.It.DescribeObjectArrays().Where(k => !k.Value.StartsWith("0/")).OrderBy(k => k.Key), 40);
        Section("events fired", host.It.EventsFired.OrderByDescending(k => k.Value), 25);
        Console.WriteLine(rep);
        string logText = log.ToString();
        if (report.Length > 0)
            File.WriteAllText(report, rep + Environment.NewLine + "== log" + Environment.NewLine + logText, new UTF8Encoding(false));
        else
            Console.WriteLine("== log (first 70 lines)" + Environment.NewLine + string.Join(Environment.NewLine, logText.Split('\n').Where(l => l.Trim().Length > 0).Take(70)));
        return 0;
    }
}
