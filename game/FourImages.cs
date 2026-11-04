using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// FOURIMAGES: four fixed slots (position1..position4, a 2x2 layout) used for ships, companions and passengers. The script fills
/// GameInterface.&lt;node&gt;.pic1..pic4 (img1/tex1 = background picture, img2/tex2 = overlay picture, str1/str2 = captions, selected) and `current`.
/// </summary>
public partial class FourImagesView : Control
{
    readonly Entity gi;
    readonly string node;
    readonly Rect2[] slots;
    readonly Action<string, string> command;
    readonly string[] strStyle;       // style lines of the first and the second caption
    string signature = "";
    readonly List<Control> pieces = new();
    TextureRect? border;

    public FourImagesView(Entity gameInterface, string nodeName, Rect2[] slotRects, string oneString, string twoString, Action<string, string> cmd)
    {
        gi = gameInterface; node = nodeName; slots = slotRects; command = cmd;
        strStyle = new[] { oneString, twoString };
        MouseFilter = MouseFilterEnum.Ignore;
        Name = nodeName;
    }

    Attr? Root => gi.Root.Find(node, false);

    public int Current
    {
        get => (int)Conv.ParseFloatPrefix(Root?.Find("current", false)?.Value);
        set { var r = Root; if (r != null) r.GetOrCreate("current").Value = value.ToString(); }
    }

    public void Move(int dx, int dy)
    {
        int c = Current, col = c % 2 + dx, row = c / 2 + dy;
        if (col < 0 || col > 1 || row < 0 || row > 1) return;
        Current = row * 2 + col;
        command("select", node);
    }

    string Signature()
    {
        var r = Root;
        if (r == null) return "";
        return $"{r.Find("current", false)?.Value}|{r.Count}|" + string.Join(",", Enumerable.Range(1, 4).Select(i => r.Find($"pic{i}.img2", false)?.Value + r.Find($"pic{i}.str1", false)?.Value));
    }

    static float Num(string s) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;

    static (string font, Vector2 off) ParseStyle(string s)
    {
        string font = "interface_normal";
        var off = Vector2.Zero;
        int i = s.IndexOf("font:", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            int e = s.IndexOf(',', i);
            font = s.Substring(i + 5, (e < 0 ? s.Length : e) - i - 5).Trim();
        }
        i = s.IndexOf("off:(", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            int e = s.IndexOf(')', i);
            var p = s.Substring(i + 5, e - i - 5).Split(',');
            off = new Vector2(Num(p[0]), p.Length > 1 ? Num(p[1]) : 0);
        }
        return (font, off);
    }

    static int Px(string font) => font.Contains("small", StringComparison.OrdinalIgnoreCase) ? 15 : font.Contains("ultra", StringComparison.OrdinalIgnoreCase) ? 11 : 22;

    public void Refresh()
    {
        foreach (var p in pieces) p.QueueFree();
        pieces.Clear();
        border?.QueueFree();
        border = null;
        var r = Root;
        if (r == null) return;
        for (int i = 0; i < 4; i++)
        {
            var pic = r.Find("pic" + (i + 1), false);
            if (pic == null) continue;
            bool selectable = pic.Find("selected", false)?.Value != "0";
            var cell = new Control { Position = slots[i].Position, Size = slots[i].Size, MouseFilter = MouseFilterEnum.Pass, Modulate = selectable ? Colors.White : new Color(0.5f, 0.5f, 0.5f) };
            foreach (var k in new[] { "1", "2" })
            {
                string? img = pic.Find("img" + k, false)?.Value;
                if (string.IsNullOrEmpty(img)) continue;
                int tex = (int)Conv.ParseFloatPrefix(pic.Find("tex" + k, false)?.Value);
                string? group = r.Get("ImagesGroup")?.ChildAt(tex)?.Value;
                if (string.IsNullOrEmpty(group)) continue;
                var t = Atlas.Get(group, img);
                if (t != null) cell.AddChild(new TextureRect { Texture = t, Size = slots[i].Size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore });
            }
            for (int s = 0; s < 2; s++)
            {
                string? txt = pic.Find("str" + (s + 1), false)?.Value;
                if (string.IsNullOrEmpty(txt)) continue;
                var (font, off) = ParseStyle(strStyle[s]);
                var l = new Label { Text = txt.TrimStart('#'), Position = off, Size = new Vector2(slots[i].Size.X - off.X, 30), MouseFilter = MouseFilterEnum.Ignore };
                l.AddThemeFontSizeOverride("font_size", Px(font));
                l.AddThemeColorOverride("font_outline_color", Colors.Black);
                l.AddThemeConstantOverride("outline_size", 3);
                cell.AddChild(l);
            }
            int idx = i;
            cell.GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) { bool again = Current == idx; Current = idx; command("click", node); if (again) command("activate", node); }   // a second click on the chosen item activates it
            };
            AddChild(cell);
            pieces.Add(cell);
        }
        border = new TextureRect { Texture = Atlas.Get("ICONS", "select border"), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(border);
        signature = Signature();
    }

    public override void _Process(double delta)
    {
        if (signature != Signature()) Refresh();
        if (border != null)
        {
            int c = Math.Clamp(Current, 0, 3);
            border.Position = slots[c].Position;
            border.Size = slots[c].Size;
        }
    }
}
