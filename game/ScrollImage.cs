using System;
using System.Collections.Generic;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// SCROLLIMAGE: a horizontal strip of pictures (items, ships, goods, characters...). The script fills attributes of
/// GameInterface.&lt;node&gt;: ListSize, current, ImagesGroup.t0..tN (atlas group names), pic1.. each with img1/tex1 (picture + group index),
/// str1 (caption such as "#5"), img2/tex2 (overlay picture) and arbitrary data (itemID, ...). The node keeps `current` in the same attribute tree.
/// </summary>
public partial class ScrollImageView : Control
{
    readonly Entity gi;
    readonly string node;
    readonly Action<string, string> command;     // (command, node) -> event/commands of the interface
    readonly float itemW, itemH;
    float shown;                                 // smooth scroll position (item index, fractional)
    string signature = "";
    readonly List<Control> items = new();
    TextureRect? selectBorder;
    /// <summary>ini "centerXoffset": x of the chosen picture's centre in interface units (null = the middle of the strip).</summary>
    public float? CenterX;
    /// <summary>ini "UseOneString": the caption's centre offset from the picture's centre (x) and its top from the picture's top (y); null = bottom right.</summary>
    public Vector2? TextOffset;

    public ScrollImageView(Entity gameInterface, string nodeName, float x1, float y1, float x2, float y2, float imageW, float imageH, Action<string, string> command)
    {
        gi = gameInterface; node = nodeName; this.command = command;
        itemW = imageW; itemH = imageH;
        Position = new Vector2(x1, y1);
        Size = new Vector2(x2 - x1, y2 - y1);
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Pass;
        Name = nodeName;
    }

    Attr? Root => gi.Root.Find(node, false);

    public int Current
    {
        get => (int)Conv.ParseFloatPrefix(Root?.Find("current", false)?.Value);
        set { var r = Root; if (r != null) r.GetOrCreate("current").Value = value.ToString(); }
    }

    public int ListSize => (int)Conv.ParseFloatPrefix(Root?.Find("ListSize", false)?.Value);

    public void Move(int delta)
    {
        int n = ListSize;
        if (n <= 0) return;
        Current = Math.Clamp(Current + delta, 0, n - 1);
        command("select", node);
    }

    string Signature()
    {
        var r = Root;
        return r == null ? "" : $"{r.Find("ListSize", false)?.Value}|{r.Find("current", false)?.Value}|{r.Count}";
    }

    public void Refresh()
    {
        foreach (var c in items) c.QueueFree();
        items.Clear();
        selectBorder?.QueueFree(); selectBorder = null;
        var r = Root;
        if (r == null) return;
        int n = ListSize;
        for (int i = 0; i < n; i++)
        {
            var pic = r.Find("pic" + (i + 1), false);
            if (pic == null) continue;
            var cell = new Control { Size = new Vector2(itemW, itemH), MouseFilter = MouseFilterEnum.Pass };
            AddLayer(cell, r, pic, "1");
            AddLayer(cell, r, pic, "2");
            string? str = pic.Find("str1", false)?.Value;
            if (!string.IsNullOrEmpty(str))
            {
                var l = TextOffset is { } to
                    ? new Label { Text = str.TrimStart('#'), Position = new Vector2(itemW / 2 + to.X - itemW, to.Y - 4), Size = new Vector2(itemW * 2, 24), HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 1 }
                    : new Label { Text = str.TrimStart('#'), Position = new Vector2(itemW * 0.45f, itemH * 0.72f), Size = new Vector2(itemW * 0.55f, 24), HorizontalAlignment = HorizontalAlignment.Right, MouseFilter = MouseFilterEnum.Ignore };
                l.AddThemeFontSizeOverride("font_size", 16);
                l.AddThemeColorOverride("font_outline_color", Colors.Black);
                l.AddThemeConstantOverride("outline_size", 4);
                cell.AddChild(l);
            }
            int idx = i;
            cell.GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                {
                    bool again = Current == idx;
                    Current = idx;
                    command("click", node);
                    if (again) command("activate", node);       // a second click on the chosen item activates it
                }
            };
            AddChild(cell);
            items.Add(cell);
        }
        selectBorder = new TextureRect
        {
            Texture = Atlas.Get("ICONS", "select border"), Size = new Vector2(itemW, itemH),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(selectBorder);
        signature = Signature();
    }

    void AddLayer(Control cell, Attr root, Attr pic, string k)
    {
        string? img = pic.Find("img" + k, false)?.Value;
        if (string.IsNullOrEmpty(img)) return;
        int tex = (int)Conv.ParseFloatPrefix(pic.Find("tex" + k, false)?.Value);
        string? group = root.Get("ImagesGroup")?.ChildAt(tex)?.Value;
        if (string.IsNullOrEmpty(group)) return;
        Texture2D? t = Atlas.Get(group, img);
        // saves: a stored screenshot replaces the "corrupt save" picture
        string? file = pic.Find("descr", false)?.Value;
        if (k == "1" && img == "corruptsave" && !string.IsNullOrEmpty(file)) t = SaveFiles.ReadThumbnail(file) ?? t;
        if (t == null) return;
        bool shot = t is ImageTexture;                   // a save thumbnail (4:3) fills the square cell without distortion
        var tr = new TextureRect { Texture = t, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = shot ? TextureRect.StretchModeEnum.KeepAspectCovered : TextureRect.StretchModeEnum.Scale, ClipContents = shot, MouseFilter = MouseFilterEnum.Ignore };
        tr.Size = new Vector2(itemW, itemH);
        cell.AddChild(tr);
    }

    public override void _Process(double delta)
    {
        if (signature != Signature()) Refresh();
        int cur = Current;
        shown += (cur - shown) * Math.Min(1f, (float)delta * 12f);
        float cx = (CenterX is { } c ? c - Position.X : Size.X / 2) - itemW / 2;
        for (int i = 0; i < items.Count; i++)
        {
            float dx = (i - shown) * (itemW + 6);
            items[i].Position = new Vector2(cx + dx, (Size.Y - itemH) / 2);
            float dim = Math.Clamp(1f - Math.Abs(i - shown) * 0.18f, 0.35f, 1f);
            items[i].Modulate = new Color(dim, dim, dim);
        }
        if (selectBorder != null)
        {
            selectBorder.Position = new Vector2(cx, (Size.Y - itemH) / 2);
            selectBorder.Visible = items.Count > 0;
        }
    }
}
