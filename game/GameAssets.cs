using System.Collections.Generic;
using System.IO;
using Godot;
using StormScript.Formats;

namespace SeaDogs;

/// <summary>Reads the original game files directly (RESOURCE\Textures\*.tx, INI files) - nothing is pre-converted.</summary>
public static class GameAssets
{
    public static string GameDir = "";
    public static readonly bool Prof = System.Environment.GetEnvironmentVariable("SEADOGS_PROF") != null;
    public static double DecodeMs; public static int DecodeCount;
    static readonly Dictionary<string, ImageTexture?> cache = new(System.StringComparer.OrdinalIgnoreCase);

    public static string Resource(string rel) => Path.Combine(GameDir, "RESOURCE", rel.Replace('\\', Path.DirectorySeparatorChar));

    /// <summary>"interfaces\mainback\mb3.tga" -> texture decoded from RESOURCE\Textures\interfaces\mainback\mb3.tga.tx</summary>
    public static ImageTexture? Texture(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (cache.TryGetValue(name, out var t)) return t;
        ImageTexture? tex = null;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!prefetched.TryRemove(name, out var pre)) pre = DecodeImage(name);          // decoded ahead on worker threads (Prefetch) or here
            if (pre.img != null) { if (pre.alpha) alphaTex.Add(name); tex = ImageTexture.CreateFromImage(pre.img); }
            else GD.PrintErr("texture not found: " + name);
        }
        catch (System.Exception e) { GD.PrintErr($"texture {name}: {e.Message}"); }
        cache[name] = tex;
        DecodeMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; DecodeCount++;
        return tex;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Image? img, bool alpha)> prefetched = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>File read + .tx decode + mipmaps: no scene or shared state is touched, so it may run on any thread.</summary>
    static (Image? img, bool alpha) DecodeImage(string name)
    {
        string p = Resource("Textures\\" + name + ".tx");
        if (!File.Exists(p)) return (null, false);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var d = TxDecoder.Decode(File.ReadAllBytes(p));
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool alpha = false;
        for (int i = 3; i < d.Rgba.Length; i += 4) if (d.Rgba[i] < 250) { alpha = true; break; }
        var img = Image.CreateFromData(d.Width, d.Height, false, Image.Format.Rgba8, d.Rgba);
        if (d.Width >= 16 && d.Height >= 16 && !name.StartsWith("interfaces", System.StringComparison.OrdinalIgnoreCase)) img.GenerateMipmaps();       // no shimmering at a distance
        if (Prof) GD.Print($"[load] texture {name} {d.Width}x{d.Height}: decode {(t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms, image+mips {(System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms");
        return (img, alpha);
    }

    /// <summary>Decodes the textures that are not loaded yet on all cores; Texture() then only uploads them. Call from the main thread (a model's materials before it is built).</summary>
    public static void Prefetch(IEnumerable<string> names)
    {
        var todo = new List<string>();
        foreach (var n in names) if (!string.IsNullOrWhiteSpace(n) && !cache.ContainsKey(n) && !prefetched.ContainsKey(n) && !todo.Contains(n)) todo.Add(n);
        if (todo.Count < 2 || System.Environment.GetEnvironmentVariable("SEADOGS_NOPREFETCH") != null) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        System.Threading.Tasks.Parallel.ForEach(todo, n =>
        {
            try { prefetched[n] = DecodeImage(n); } catch (System.Exception e) { GD.PrintErr($"texture {n}: {e.Message}"); }
        });
        if (Prof) GD.Print($"[load] prefetched {todo.Count} textures in {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms");
    }

    static Dictionary<string, string>? texIndex;
    static readonly HashSet<string> alphaTex = new(System.StringComparer.OrdinalIgnoreCase);

    public static bool HasAlpha(string name) => alphaTex.Contains(name);

    /// <summary>Find a texture by bare file name ("deck.tga") in a preferred folder, the Textures root, or anywhere below it.</summary>
    public static string? FindTexture(string file, string? preferDir)
    {
        string root = Resource("Textures");
        if (preferDir != null)
        {
            string rel = preferDir.TrimEnd((char)92) + (char)92 + file;
            if (File.Exists(Path.Combine(root, rel + ".tx"))) return rel;
        }
        if (File.Exists(Path.Combine(root, file + ".tx"))) return file;
        if (texIndex == null)
        {
            texIndex = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(root, "*.tx", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, f);
                rel = rel.Substring(0, rel.Length - 3);
                texIndex.TryAdd(Path.GetFileName(rel), rel);
            }
        }
        return texIndex.TryGetValue(file, out var r) ? r : null;
    }

    /// <summary>All textures in a folder, e.g. "interfaces\mainback" -> list of "interfaces\mainback\x.tga".</summary>
    public static List<string> TexturesIn(string dir)
    {
        var r = new List<string>();
        string d = Resource("Textures\\" + dir);
        if (!Directory.Exists(d)) return r;
        foreach (var f in Directory.GetFiles(d, "*.tx"))
            r.Add(dir.TrimEnd('\\') + "\\" + Path.GetFileNameWithoutExtension(f));   // drops ".tx", keeps ".tga"
        return r;
    }
}
