using System;
using System.Collections.Generic;
using Godot;

namespace SeaDogs;

/// <summary>
/// CONTEXTHELP (xi_cont_help.cpp): the hint line of a screen (the bottom strip of the ship / items / goods screens). It shows the string of the node that has the focus
/// (ini "helpstr = NODE,key"), the default one (ini "defhelp") for any other node, and for "delay" ms the temporary string the script put there with
/// MSG_INTERFACE_SETHELPSTRING (ShowHelpString: "this item is not for trade" ...). A key starting with '#' is the text itself.
/// The text shrinks to the width of the rectangle.
/// </summary>
public partial class ContextHelpView : Control
{
    readonly List<(string node, string key)> list;
    readonly string defKey;
    readonly Func<string> currentNode;
    readonly Func<string, string> tr;
    readonly Label lab = new() { MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center, ClipText = true };
    readonly double delayMs;
    readonly int basePx;
    string? temp;
    double tempLeft;

    public ContextHelpView(float x1, float y1, float x2, float y2, Color fill, Color border, bool hasBorder, Color font, int px, float offset, double delay,
        List<(string, string)> helpList, string defaultKey, Func<string> currentNode, Func<string, string> translate)
    {
        Position = new Vector2(x1, y1); Size = new Vector2(x2 - x1, y2 - y1);
        MouseFilter = MouseFilterEnum.Ignore;
        list = helpList; defKey = defaultKey; this.currentNode = currentNode; tr = translate; delayMs = delay; basePx = px;
        AddChild(new ColorRect { Color = fill, Size = Size, MouseFilter = MouseFilterEnum.Ignore });
        if (hasBorder)
            AddChild(new Panel { Size = Size, MouseFilter = MouseFilterEnum.Ignore, Name = "Border" }.With(p => p.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = border, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1 })));
        lab.Position = new Vector2(2, offset); lab.Size = new Vector2(Size.X - 4, Size.Y);
        lab.AddThemeColorOverride("font_color", font);
        lab.AddThemeColorOverride("font_outline_color", Colors.Black);
        lab.AddThemeConstantOverride("outline_size", 2);
        AddChild(lab);
    }

    /// <summary>The script's temporary hint (ShowHelpString).</summary>
    public void SetTemp(string key) { temp = key; tempLeft = delayMs; }
    public void ClearTemp() { temp = null; tempLeft = 0; }
    public string? Temp => tempLeft > 0 ? temp : null;
    public string Shown => lab.Text;

    string Text(string key) => key.StartsWith('#') ? key.Substring(1) : tr(key);

    public override void _Process(double delta)
    {
        if (tempLeft > 0) tempLeft = Math.Max(0, tempLeft - delta * 1000.0);
        string key = defKey;
        if (tempLeft > 0 && temp != null) key = temp;
        else
        {
            string cur = currentNode();
            foreach (var (n, k) in list) if (n.Equals(cur, StringComparison.OrdinalIgnoreCase)) { key = k; break; }
        }
        string text = key.Length == 0 ? "" : Text(key);
        if (lab.Text != text)
        {
            lab.Text = text;
            int px = basePx;
            var f = lab.GetThemeDefaultFont();
            while (px > 9 && f.GetStringSize(text, HorizontalAlignment.Left, -1, px).X > Size.X - 4) px--;       // the scale shrinks to the width of the strip
            lab.AddThemeFontSizeOverride("font_size", px);
        }
    }
}

static class ControlExt
{
    public static T With<T>(this T c, Action<T> a) where T : Control { a(c); return c; }
}
