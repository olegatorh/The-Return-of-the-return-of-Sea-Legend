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

    [Fact]
    public void PatchMoveSlidesAlongBorderAndStaysInside()
    {
        // two triangles forming the square (0..10, 0..10) on the floor y = 1; the first has the neighbour link, the outer edges are borders
        var p = new PtcFile
        {
            Vx = [0, 10, 10, 0], Vy = [1, 1, 1, 1], Vz = [0, 0, 10, 10],
            Tris = [new PtcFile.Tri { A = 0, B = 3, C = 2, N0 = -1, N1 = -1, N2 = 1 }, new PtcFile.Tri { A = 0, B = 2, C = 1, N0 = 0, N1 = -1, N2 = -1 }],
        };
        int node = p.FindNode(2, 1, 3, out var h);
        Assert.True(node >= 0);
        Assert.InRange(h, 0.99f, 1.01f);
        // straight move inside, falling onto the floor
        var pos = (x: 2f, y: 5f, z: 3f);
        node = p.Move(node, (4, 4.9f, 6), ref pos);
        Assert.Equal((4f, 4.9f, 6f), pos);
        pos = (4, 0.5f, 6);
        p.Move(node, (4.1f, 0.4f, 6), ref pos);
        Assert.True(p.IsBearing); Assert.InRange(pos.y, 0.99f, 1.01f);
        // a diagonal move out of the square slides along the border and never leaves it
        pos = (9f, 1f, 5f);
        node = p.FindNode(9, 1, 5, out _);
        node = p.Move(node, (12f, 1f, 7f), ref pos);
        Assert.True(p.IsSlide);
        Assert.InRange(pos.x, 9f, 10.01f);
        Assert.InRange(pos.z, 5f, 7.1f);
        Assert.True(p.FindNode(pos.x, pos.y, pos.z, out _) >= 0 || pos.x <= 10.01f);
    }

    [Fact]
    public void RandomWalksOnRealPatchesNeverLeaveThem()
    {
        if (!Paths.HasGame) return;
        var rnd = new Random(7);
        int walked = 0;
        foreach (var f in Files("*.ptc").Take(40))
        {
            var p = PtcFile.Parse(File.ReadAllBytes(f));
            if (p.IsSea || p.Tris.Length < 4) continue;
            var (cx, cy, cz) = Centre(p, p.Tris[p.Tris.Length / 2]);
            int node = p.FindNode(cx, cy, cz, out _);
            if (node < 0) continue;
            var pos = (x: cx, y: cy, z: cz);
            float ang = 0;
            for (int i = 0; i < 400; i++)
            {
                if (i % 20 == 0) ang = (float)(rnd.NextDouble() * 6.28);
                node = p.Move(node, (pos.x + MathF.Sin(ang) * 0.1f, pos.y - 0.05f, pos.z + MathF.Cos(ang) * 0.1f), ref pos);
                Assert.InRange(node, 0, p.Tris.Length - 1);
                Assert.True(pos.y >= p.FindHeight(node, pos.x, pos.z) - 0.01f, f);
            }
            walked++;
        }
        Assert.True(walked > 0);
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

/// <summary>Segment-triangle test of the walk patch / jump patch (PtcData::Trace): the engine lets a character fall through a ledge only if this finds the floor below.</summary>
public sealed class TraceTests
{
    static TriSoup Floor(float y)
    {
        var s = new TriSoup();
        s.Add(new float[] { 0, y, 0, 10, y, 0, 0, y, 10, 10, y, 10 }, new[] { 0, 1, 2, 1, 3, 2 });
        return s;
    }

    [Fact]
    public void ASegmentCrossingTheFloorReturnsTheFractionAlongIt()
    {
        var floor = Floor(1f);
        Assert.Equal(0.25f, floor.Trace(4, 2, 3, 4, -2, 3), 3);
        Assert.Equal(0.75f, floor.Trace(4, -2, 3, 4, 2, 3), 3);          // both sides count
    }

    [Fact]
    public void AMissReturnsTwo()
    {
        var floor = Floor(1f);
        Assert.Equal(2f, floor.Trace(4, 3, 3, 4, 2, 3));                 // stops above the floor
        Assert.Equal(2f, floor.Trace(15, 2, 3, 15, -2, 3));              // beside it
    }
}
