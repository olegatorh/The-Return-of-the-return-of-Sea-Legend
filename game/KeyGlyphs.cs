using System;
using System.Collections.Generic;
using Godot;

namespace SeaDogs;

/// <summary>
/// The engine's KEYBOARD_SYMBOL bitmap font (RESOURCE\INI\fonts.ini, texture fonts\pc_keyboard.tga): every character of the "img" attribute of a key code
/// (CONTROLS\init_pc.c: "q" = left mouse button, "w" = right button, "A" = the A key ...) is a picture of that key cap.
/// </summary>
public static class KeyGlyphs
{
    static Dictionary<char, Rect2>? rects;

    public static Texture2D? Texture => GameAssets.Texture("fonts\\pc_keyboard.tga");

    public static Rect2? Rect(char c)
    {
        if (rects == null)
        {
            rects = new Dictionary<char, Rect2>();
            var sec = IniFile.Load(GameAssets.Resource("INI\\fonts.ini")).Get("KEYBOARD_SYMBOL");
            if (sec != null)
                foreach (var (k, v) in sec.Lines)
                {
                    if (!k.StartsWith("char_", StringComparison.OrdinalIgnoreCase) || k.Length < 6) continue;
                    string id = k.Substring(5);
                    char ch = id.Length == 2 && id[1] == '_' ? id[0] : id[0];       // "char_a_" is the lower case "a" (ini keys are case-insensitive)
                    var p = v.Split(',');
                    if (p.Length < 4) continue;
                    float[] f = new float[4];
                    for (int i = 0; i < 4; i++) float.TryParse(p[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f[i]);
                    rects[ch] = new Rect2(f[0], f[1], f[2], f[3]);
                }
        }
        return rects.TryGetValue(c, out var r) ? r : null;
    }
}

/// <summary>A FORMATEDTEXT node whose font is KEYBOARD_SYMBOL: one row of key caps per text line, right aligned (the key column of the control pages).</summary>
public partial class KeyGlyphLabel : Label
{
    readonly float lineSpace;
    readonly float rowOffset;

    public KeyGlyphLabel(float lineSpace, float rowOffset)
    {
        this.lineSpace = lineSpace <= 0 ? 24 : lineSpace;
        this.rowOffset = rowOffset;
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeColorOverride("font_color", new Color(1, 1, 1, 0));
        AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0));
    }

    string shown = "";

    public override void _Process(double delta)
    {
        if (Text != shown) { shown = Text; QueueRedraw(); }
    }

    public override void _Draw()
    {
        var tex = KeyGlyphs.Texture;
        if (tex == null) return;
        var rows = Text.Split('\n');
        float size = lineSpace - 2;
        for (int i = 0; i < rows.Length; i++)
        {
            string row = rows[i];
            float x = Size.X;
            for (int c = row.Length - 1; c >= 0; c--)
            {
                if (KeyGlyphs.Rect(row[c]) is not { } src) continue;
                x -= size;
                DrawTextureRectRegion(tex, new Rect2(x, rowOffset + i * lineSpace, size, size), src);
            }
        }
    }
}
