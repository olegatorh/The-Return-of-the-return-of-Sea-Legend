using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace SeaDogs;

/// <summary>
/// The interface picture atlas (RESOURCE\INI\INTERFACES\pictures.ini): a group is one texture, a picture is a pixel rectangle in it.
/// Scripts and .ini files refer to pictures as (group, picture name), e.g. ICONS / "small left arrow".
/// </summary>
public static class Atlas
{
    sealed class Group
    {
        public string Texture = "";
        public Dictionary<string, Rect2> Pictures = new(StringComparer.OrdinalIgnoreCase);
    }

    static Dictionary<string, Group>? groups;
    static readonly Dictionary<(string, string), AtlasTexture?> cache = new();

    static void Load()
    {
        groups = new(StringComparer.OrdinalIgnoreCase);
        var path = GameAssets.Resource("INI\\INTERFACES\\pictures.ini");
        if (!System.IO.File.Exists(path)) return;
        var ini = IniFile.Load(path);
        foreach (var (name, sec) in ini.Sections)
        {
            var g = new Group { Texture = "interfaces\\" + (sec.Get("sTextureName") ?? "") };
            foreach (var line in sec.All("picture"))
            {
                var p = line.Split(',');
                if (p.Length < 5) continue;
                var n = p.Select(x => x.Trim()).ToArray();
                if (!float.TryParse(n[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x1) ||
                    !float.TryParse(n[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y1) ||
                    !float.TryParse(n[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var x2) ||
                    !float.TryParse(n[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var y2)) continue;
                g.Pictures[n[0]] = new Rect2(x1, y1, x2 - x1, y2 - y1);
            }
            groups[name] = g;
        }
        GD.Print($"[atlas] {groups.Count} picture groups");
    }

    /// <summary>The picture as a texture region, or null if the group/name is unknown.</summary>
    public static AtlasTexture? Get(string group, string picture)
    {
        if (groups == null) Load();
        if (cache.TryGetValue((group, picture), out var c)) return c;
        AtlasTexture? r = null;
        if (groups!.TryGetValue(group, out var g) && g.Pictures.TryGetValue(picture, out var rect))
        {
            var tex = GameAssets.Texture(g.Texture);
            if (tex != null) r = new AtlasTexture { Atlas = tex, Region = rect };
        }
        else GD.Print($"[atlas] unknown picture '{picture}' in group '{group}'");
        cache[(group, picture)] = r;
        return r;
    }
}
