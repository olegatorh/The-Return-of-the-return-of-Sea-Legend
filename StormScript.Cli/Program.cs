using System.Text;
using StormScript;

// usage:
//   ssc check <PROGRAM dir>            parse every .c/.h file, report failures
//   ssc api   <PROGRAM dir> <out.txt>  list functions called but never defined/declared in script (= engine API)
if (args.Length < 2) { Console.WriteLine("usage: ssc check|api <PROGRAM dir> [out]"); return 1; }

string root = Path.GetFullPath(args[1]);

if (args[0] == "run") return RunCommand.Run(root, args.Skip(2).ToArray());

var files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
    .Where(f => f.EndsWith(".c", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".h", StringComparison.OrdinalIgnoreCase))
    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

var results = new System.Collections.Concurrent.ConcurrentDictionary<string, (ScriptUnit? unit, Preprocessor? pp, string? error)>();
Parallel.ForEach(files, f =>
{
    var pp = new Preprocessor(root);
    try
    {
        var toks = pp.Run(f);
        var unit = Parser.ParseUnit(toks);
        results[f] = (unit, pp, null);
    }
    catch (Exception e) when (e is LexException or ParseException)
    {
        results[f] = (null, pp, e.Message);
    }
});

var failed = results.Where(r => r.Value.error != null).OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToList();
var partial = results.Where(r => r.Value.unit != null && r.Value.unit.Errors.Count > 0).OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToList();
Console.WriteLine($"files: {files.Count}, clean: {files.Count - failed.Count - partial.Count}, with recovered syntax errors: {partial.Count}, unparseable: {failed.Count}");
foreach (var (f, r) in failed.Take(40)) Console.WriteLine("  FAIL " + r.error);
var errs = partial.SelectMany(r => r.Value.unit!.Errors).Distinct().ToList();
Console.WriteLine($"distinct syntax errors recovered from: {errs.Count}");
foreach (var e in errs.Take(40)) Console.WriteLine("  ERR  " + e);

var warns = results.Values.Where(r => r.unit != null).SelectMany(r => r.unit!.Warnings).Distinct().ToList();
Console.WriteLine($"lenient parse warnings (missing ';' tolerated like the original compiler): {warns.Count}");
foreach (var w in warns.Take(5)) Console.WriteLine("  " + w);

var missing = results.Values.Where(r => r.pp != null).SelectMany(r => r.pp!.MissingIncludes).Distinct().ToList();
if (missing.Count > 0)
{
    Console.WriteLine($"unresolved #include: {missing.Count}");
    foreach (var m in missing.Take(10)) Console.WriteLine("  " + m);
}

if (args[0] == "api" && args.Length >= 3)
{
    var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // native/extern/prototypes
    var calls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var firstUse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var units = results.Values.Where(r => r.unit != null).Select(r => r.unit!).ToList();
    foreach (var u in units)
        foreach (var it in u.Items)
            if (it is FuncDecl fd)
                (fd.Body != null ? defined : declared).Add(fd.Name);
    foreach (var u in units)
        foreach (var it in u.Items)
            Walker.Walk(it, n =>
            {
                if (n is FuncDecl lf && lf.Body == null) declared.Add(lf.Name);
                if (n is FuncDecl lf2 && lf2.Body != null) defined.Add(lf2.Name);
                if (n is CallExpr { IsDynamic: false, Callee: IdentExpr id })
                {
                    calls[id.Name] = calls.GetValueOrDefault(id.Name) + 1;
                    firstUse.TryAdd(id.Name, $"{Path.GetFileName(n.File)}:{n.Line}");
                }
            });
    var sb = new StringBuilder();
    var engine = calls.Where(c => !defined.Contains(c.Key)).OrderByDescending(c => c.Value).ToList();
    sb.AppendLine($"# script-defined functions: {defined.Count}; native/extern/prototype decls: {declared.Count}");
    sb.AppendLine($"# called but not defined in any script file: {engine.Count}");
    sb.AppendLine("# name  calls  declared-in-script  first-use");
    foreach (var (name, cnt) in engine)
        sb.AppendLine($"{name}\t{cnt}\t{(declared.Contains(name) ? "decl" : "-")}\t{firstUse[name]}");
    File.WriteAllText(args[2], sb.ToString(), new UTF8Encoding(false));
    Console.WriteLine($"engine API candidates: {engine.Count} -> {args[2]}");
}
if (args[0] == "surface" && args.Length >= 3)
{
    // the real engine surface: entity classes (CreateEntity), message ids (SendMessage), events
    var classes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var msgs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var fmts = new Dictionary<string, int>();
    var events = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    int dynMsg = 0, totalMsg = 0;
    foreach (var u in results.Values.Where(r => r.unit != null).Select(r => r.unit!))
        foreach (var it in u.Items)
            Walker.Walk(it, n =>
            {
                if (n is not CallExpr { IsDynamic: false, Callee: IdentExpr id }) return;
                string nm = id.Name.ToLowerInvariant();
                var a = ((CallExpr)n).Args;
                if (nm == "createentity" && a.Count >= 2 && a[1] is StringLit cls)
                    classes[cls.Value] = classes.GetValueOrDefault(cls.Value) + 1;
                else if (nm == "sendmessage" && a.Count >= 3)
                {
                    totalMsg++;
                    if (a[1] is StringLit f) fmts[f.Value] = fmts.GetValueOrDefault(f.Value) + 1;
                    string? key = a[2] switch { NumberLit { Const: not null } nl => nl.Const, NumberLit nl2 => nl2.Text, _ => null };
                    if (key == null) dynMsg++; else msgs[key] = msgs.GetValueOrDefault(key) + 1;
                }
                else if (nm is "seteventhandler" or "postevent" or "event" or "deleventhandler" && a.Count >= 1)
                {
                    string? key = a[0] switch { StringLit s => s.Const ?? s.Value, _ => null };
                    if (key != null) events[key] = events.GetValueOrDefault(key) + 1;
                }
            });
    var sb2 = new StringBuilder();
    sb2.AppendLine($"# entity classes created by scripts: {classes.Count}");
    foreach (var kv in classes.OrderByDescending(k => k.Value)) sb2.AppendLine($"class	{kv.Key}	{kv.Value}");
    sb2.AppendLine($"# SendMessage call sites: {totalMsg}; distinct message ids: {msgs.Count}; with non-constant id: {dynMsg}");
    foreach (var kv in msgs.OrderByDescending(k => k.Value)) sb2.AppendLine($"msg	{kv.Key}	{kv.Value}");
    sb2.AppendLine($"# distinct SendMessage format strings: {fmts.Count}");
    foreach (var kv in fmts.OrderByDescending(k => k.Value)) sb2.AppendLine($"fmt	{kv.Key}	{kv.Value}");
    sb2.AppendLine($"# distinct event names used in script: {events.Count}");
    foreach (var kv in events.OrderByDescending(k => k.Value)) sb2.AppendLine($"event	{kv.Key}	{kv.Value}");
    File.WriteAllText(args[2], sb2.ToString(), new UTF8Encoding(false));
    Console.WriteLine($"classes {classes.Count}, messages {msgs.Count} ({totalMsg} sites, {dynMsg} dynamic), formats {fmts.Count}, events {events.Count}");
}
return failed.Count == 0 ? 0 : 2;
