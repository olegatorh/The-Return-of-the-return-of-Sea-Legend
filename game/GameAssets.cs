using System.Collections.Generic;
using System.IO;
using Godot;
using StormScript.Formats;

namespace SeaDogs;

/// <summary>Reads the original game files directly (RESOURCE\Textures\*.tx, INI files) - nothing is pre-converted.</summary>
public static class GameAssets
{
    public static string GameDir = "";
    static readonly Dictionary<string, ImageTexture?> cache = new(System.StringComparer.OrdinalIgnoreCase);

    public static string Resource(string rel) => Path.Combine(GameDir, "RESOURCE", rel.Replace('\\', Path.DirectorySeparatorChar));

    /// <summary>"interfaces\mainback\mb3.tga" -> texture decoded from RESOURCE\Textures\interfaces\mainback\mb3.tga.tx</summary>
    public static ImageTexture? Texture(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (cache.TryGetValue(name, out var t)) return t;
        ImageTexture? tex = null;
        string p = Resource("Textures\\" + name + ".tx");
        try
        {
            if (File.Exists(p))
            {
                var d = TxDecoder.Decode(File.ReadAllBytes(p));
                for (int i = 3; i < d.Rgba.Length; i += 4) if (d.Rgba[i] < 250) { alphaTex.Add(name); break; }
                var img = Image.CreateFromData(d.Width, d.Height, false, Image.Format.Rgba8, d.Rgba);
                if (d.Width >= 16 && d.Height >= 16 && !name.StartsWith("interfaces", System.StringComparison.OrdinalIgnoreCase)) img.GenerateMipmaps();       // no shimmering at a distance
                tex = ImageTexture.CreateFromImage(img);
            }
            else GD.PrintErr("texture not found: " + name);
        }
        catch (System.Exception e) { GD.PrintErr($"texture {name}: {e.Message}"); }
        cache[name] = tex;
        return tex;
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
