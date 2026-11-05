using StormScript.Formats;
using StormScript.Runtime;
using Xunit;

namespace StormScript.Tests;

/// <summary>Locates the port folder and the original game (one level above it). Tests that need game data return early when it is absent.</summary>
static class Paths
{
    public static readonly string Port = FindPort();
    public static string Game => Path.GetFullPath(Path.Combine(Port, ".."));
    public static string Merged => Path.Combine(Port, "addon", "merged", "PROGRAM");
    public static bool HasMerged => Directory.Exists(Merged);
    public static bool HasGame => Directory.Exists(Path.Combine(Game, "RESOURCE", "MODELS"));

    static string FindPort()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CLAUDE.md")) && Directory.Exists(Path.Combine(d.FullName, "StormScript"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
}

/// <summary>Whole-game regression tests: they load the real (merged) script tree, so a broken interpreter or parser is noticed without starting Godot.</summary>
public sealed class ScriptCorpusTests
{
    [Fact]
    public void EveryScriptFileParsesWithinTheKnownNumberOfRecoveredSyntaxErrors()
    {
        if (!Paths.HasMerged) return;
        var files = Directory.GetFiles(Paths.Merged, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".c", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".h", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(files.Count > 1000);
        var unparseable = new List<string>();
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(files, f =>
        {
            try
            {
                var unit = Parser.ParseUnit(new Preprocessor(Paths.Merged).Run(f));
                foreach (var e in unit.Errors) errors.Add(e);
            }
            catch (Exception e) when (e is LexException or ParseException) { lock (unparseable) unparseable.Add(f + ": " + e.Message); }
        });
        Assert.Empty(unparseable);
        // 12 known defects of the original sources (see docs/source_defects.md); a rise means the parser regressed
        Assert.True(errors.Distinct().Count() <= 12, "recovered syntax errors: " + string.Join("\n", errors.Distinct()));
    }

    [Fact]
    public void GameBootsHeadlessWithoutRuntimeErrorsOrMissingEngineFunctions()
    {
        if (!Paths.HasMerged) return;
        var log = new List<string>();
        var host = new ScriptHost(Paths.Merged);
        host.It.Log = s => { lock (log) log.Add(s); };
        EngineStubs.Install(host.It);
        Assert.True(host.LoadFile("seadogs.c", "<main>"), string.Join("\n", log.Take(20)));
        Assert.True(host.It.Funcs.Count >= 2500, "functions: " + host.It.Funcs.Count);
        Assert.True(host.It.Globals.Count >= 790, "globals: " + host.It.Globals.Count);

        host.It.CallTop("Main");
        for (int i = 0; i < 50 && !host.It.Exit; i++) host.It.Advance(100);

        Assert.True(host.It.RuntimeErrors.Count == 0, "runtime errors: " + string.Join(", ", host.It.RuntimeErrors.Keys));
        Assert.True(host.It.UnresolvedFuncs.Count == 0, "unresolved functions: " + string.Join(", ", host.It.UnresolvedFuncs.Keys));
        Assert.True(host.It.UnresolvedVars.Count == 0, "unresolved variables: " + string.Join(", ", host.It.UnresolvedVars.Keys));
    }
}

/// <summary>Binary format readers against the real resources.</summary>
public sealed class FormatTests
{
    static IEnumerable<string> Files(string pattern) =>
        Directory.GetFiles(Path.Combine(Paths.Game, "RESOURCE", "MODELS"), pattern, SearchOption.AllDirectories);

    [Fact]
    public void PatchFilesParseAndReferenceOnlyExistingVertices()
    {
        if (!Paths.HasGame) return;
        int n = 0;
        foreach (var f in Files("*.ptc").Take(60))
        {
            var p = PtcFile.Parse(File.ReadAllBytes(f));
            foreach (var t in p.Tris)
                Assert.True(t.A < p.Vx.Length && t.B < p.Vx.Length && t.C < p.Vx.Length, f);
            n++;
        }
        Assert.True(n > 0);
    }

    [Fact]
    public void PathFindingConnectsNeighbouringTriangles()
    {
        if (!Paths.HasGame) return;
        int tested = 0;
        foreach (var f in Files("*.ptc").Take(60))
        {
            var p = PtcFile.Parse(File.ReadAllBytes(f));
            if (p.IsSea || p.Tris.Length < 4) continue;
            for (int i = 0; i < p.Tris.Length && tested < 20; i++)
            {
                var t = p.Tris[i];
                if (t.N0 < 0 || t.N0 >= p.Tris.Length) continue;
                var (ax, ay, az) = Centre(p, t);
                var (bx, by, bz) = Centre(p, p.Tris[t.N0]);
                var path = p.FindPath(ax, az, ay, bx, bz, by);
                Assert.NotNull(path);
                Assert.NotEmpty(path!);
                tested++;
                break;
            }
        }
        Assert.True(tested > 0);
    }

    [Fact]
    public void PatchMaterialsNameStepSounds()
    {
        if (!Paths.HasGame) return;
        int withSnd = 0, files = 0;
        foreach (var f in Files("*.ptc"))
        {
            var p = PtcFile.Parse(File.ReadAllBytes(f));
            files++;
            foreach (var t in p.Tris) if (p.Materials.Length > 0) Assert.True(t.Material < p.Materials.Length, f);
            if (p.Materials.Any(m => m.Contains("snd_", StringComparison.OrdinalIgnoreCase))) withSnd++;
        }
        Assert.True(withSnd > files / 2, $"{withSnd}/{files} patches name snd_ materials");
    }

    static (float, float, float) Centre(PtcFile p, PtcFile.Tri t) =>
        ((p.Vx[t.A] + p.Vx[t.B] + p.Vx[t.C]) / 3, (p.Vy[t.A] + p.Vy[t.B] + p.Vy[t.C]) / 3, (p.Vz[t.A] + p.Vz[t.B] + p.Vz[t.C]) / 3);

    [Fact]
    public void GeometryFilesParse()
    {
        if (!Paths.HasGame) return;
        int n = 0;
        foreach (var f in Files("*.gm").Take(40))
        {
            try { Assert.NotNull(GmFile.Parse(File.ReadAllBytes(f))); n++; }
            catch (InvalidDataException) { }          // other .gm versions are not supported (and not used by the game)
        }
        Assert.True(n > 0);
    }
}

/// <summary>The translation layer merges key by key over the base language.</summary>
public sealed class LanguageTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "ss_lang_" + Guid.NewGuid().ToString("N"));

    public LanguageTests()
    {
        var baseDir = Path.Combine(dir, "game", "RESOURCE", "INI", "TEXTS", "RUSSIAN");
        var overlay = Path.Combine(dir, "lang", "ukrainian", "texts");
        Directory.CreateDirectory(baseDir); Directory.CreateDirectory(overlay);
        File.WriteAllText(Path.Combine(baseDir, "t.txt"), "hello{hi-base}\nbye{Bye-base}\n", System.Text.Encoding.ASCII);   // base files are cp1251, keep ASCII here
        File.WriteAllText(Path.Combine(overlay, "t.txt"), "hello{Привіт}\n", new System.Text.UTF8Encoding(false));
    }
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void OverlayWinsKeyByKeyAndUntranslatedKeysFallBackToTheBase()
    {
        var l = new LanguageService(Path.Combine(dir, "game"), Path.Combine(dir, "lang"), "Ukrainian");
        var d = l.Load("t.txt");
        Assert.Equal("Привіт", d["hello"]);     // translated
        Assert.Equal("Bye-base", d["bye"]);         // not translated: base language kept
        Assert.Equal("Ukrainian", l.Name);
    }
}
