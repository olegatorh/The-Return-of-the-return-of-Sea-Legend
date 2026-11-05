using System;
using System.Collections.Generic;
using Godot;

namespace SeaDogs;

/// <summary>
/// The two windows the world map draws itself (not xinterface screens): the event window (worldMap.eventWindow.text/yes/no + update, script wdmMessage;
/// text "Header# body") and the wait menu (worldMap.waitMenu.update, script wdmWaitMenu). Built from RESOURCE\Textures\WorldMap\Interfaces
/// (eventbkg / wmenu backgrounds, button / wmenubutton with a lit and a dark half). Keys: arrows change the selection, Enter/Space choose, Escape = the last item.
/// </summary>
public partial class WorldMapUi : CanvasLayer
{
    readonly Control root = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    readonly List<(TextureRect bg, Label text)> items = new();
    int sel;
    Action<int>? onSelect, onChange;
    AtlasTexture? lit, dark;
    public bool IsOpen => root.Visible;

    public WorldMapUi()
    {
        Layer = 8;
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.Visible = false;
        AddChild(root);
    }

    public void Close()
    {
        root.Visible = false;
        foreach (var c in root.GetChildren()) c.QueueFree();
        items.Clear(); onSelect = onChange = null;
    }

    static Label MakeLabel(string s, int size, Color c, HorizontalAlignment al = HorizontalAlignment.Center)
    {
        var l = new Label { Text = s, HorizontalAlignment = al, VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", c);
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 4);
        return l;
    }

    void SetButtons(string tex)
    {
        var t = GameAssets.Texture(tex);
        if (t == null) { lit = dark = null; return; }
        // one half is lit (the selected button), the other dark; which one is on top differs between the files, so the brighter half is taken
        var top = new AtlasTexture { Atlas = t, Region = new Rect2(0, 0, t.GetWidth(), t.GetHeight() / 2f) };
        var bottom = new AtlasTexture { Atlas = t, Region = new Rect2(0, t.GetHeight() / 2f, t.GetWidth(), t.GetHeight() / 2f) };
        bool topLit = true;
        if (t.GetImage() is { } img)
        {
            img = (Image)img.Duplicate();
            if (img.IsCompressed()) img.Decompress();
            double a = 0, b = 0;
            int w = img.GetWidth(), h = img.GetHeight();
            for (int y = 0; y < h; y += 2)
                for (int x = 0; x < w; x += 2) { float v = img.GetPixel(x, y).V; if (y < h / 2) a += v; else b += v; }
            topLit = a >= b;
        }
        lit = topLit ? top : bottom; dark = topLit ? bottom : top;
    }

    void AddItem(string text, Vector2 pos, Vector2 size)
    {
        var bg = new TextureRect { Position = pos, Size = size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Stop };
        var lab = MakeLabel(text, 18, new Color(1f, 0.93f, 0.75f));
        lab.Position = Vector2.Zero; lab.Size = size;
        bg.AddChild(lab);
        int idx = items.Count;
        bg.GuiInput += ev =>
        {
            if (ev is InputEventMouseMotion && sel != idx) Select(idx);
            if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) { Select(idx); Choose(); }
        };
        root.AddChild(bg);
        items.Add((bg, lab));
    }

    void Select(int i)
    {
        if (items.Count == 0) return;
        sel = (i % items.Count + items.Count) % items.Count;
        for (int k = 0; k < items.Count; k++)
        {
            items[k].bg.Texture = k == sel ? lit : dark;
            items[k].text.Modulate = k == sel ? Colors.White : new Color(0.75f, 0.75f, 0.8f);
        }
        onChange?.Invoke(sel);
    }

    void Choose()
    {
        int s = sel; var cb = onSelect;
        Close();
        cb?.Invoke(s);
    }

    Vector2 Screen => root.GetViewportRect().Size;

    /// <summary>The event window: a header, a text and two buttons (yes = 0, no = 1).</summary>
    public void OpenEvent(string text, string yes, string no, Action<int> select, Action<int>? change = null)
    {
        Close();
        onSelect = select; onChange = change;
        var scr = Screen;
        var size = new Vector2(560, 290);
        var pos = (scr - size) / 2f;
        var bk = new TextureRect { Texture = GameAssets.Texture("WorldMap\\Interfaces\\eventbkg.tga"), Position = pos, Size = size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
        root.AddChild(bk);
        string head = text, body = "";
        int sharp = text.IndexOf('#');
        if (sharp >= 0) { head = text[..sharp].Trim(); body = text[(sharp + 1)..].Trim(); }
        var h = MakeLabel(head, 24, new Color(1f, 0.85f, 0.45f));
        h.Position = pos + new Vector2(50, 26); h.Size = new Vector2(size.X - 100, 40);
        root.AddChild(h);
        var b = MakeLabel(body, 18, new Color(0.95f, 0.95f, 1f));
        b.Position = pos + new Vector2(50, 72); b.Size = new Vector2(size.X - 100, 110);
        root.AddChild(b);
        SetButtons("WorldMap\\Interfaces\\button.tga");
        var bs = new Vector2(190, 46);
        AddItem(yes.Length > 0 ? yes : "Так", pos + new Vector2(size.X / 2f - bs.X - 14, size.Y - 92), bs);
        AddItem(no.Length > 0 ? no : "Ні", pos + new Vector2(size.X / 2f + 14, size.Y - 92), bs);
        root.Visible = true;
        Select(0);
    }

    /// <summary>The wait menu: a title and a column of items.</summary>
    public void OpenMenu(string title, string[] entries, Action<int> select, Action<int>? change = null)
    {
        Close();
        onSelect = select; onChange = change;
        var scr = Screen;
        float rowH = 40f, gap = 6f;
        // the frame of wmenu.tga fills only x 32..480, y 0..182 of the 512x256 picture; its title band is y 10..40
        float itemsH = entries.Length * (rowH + gap);
        float frameH = itemsH / (1f - 60f / 182f);              // the band and the margins take 60 of the 182 rows
        var size = new Vector2(460, frameH);
        var pos = (scr - size) / 2f;
        float k = frameH / 182f;
        var bkSize = new Vector2(size.X * 512f / 448f, 256f * k);
        var bk = new TextureRect { Texture = GameAssets.Texture("WorldMap\\Interfaces\\wmenu.tga"), Position = pos - new Vector2(bkSize.X * 32f / 512f, 0), Size = bkSize, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
        root.AddChild(bk);
        var h = MakeLabel(title, 22, new Color(1f, 0.85f, 0.45f));
        h.Position = pos + new Vector2(80, 10 * k); h.Size = new Vector2(size.X - 160, 30 * k);
        root.AddChild(h);
        SetButtons("WorldMap\\Interfaces\\wmenubutton.tga");
        for (int i = 0; i < entries.Length; i++) AddItem(entries[i], pos + new Vector2(40, 48 * k + i * (rowH + gap)), new Vector2(size.X - 80, rowH));
        root.Visible = true;
        Select(0);
    }

    /// <summary>A key while a window is open. Returns true (the key is always consumed).</summary>
    public bool Key(Key k)
    {
        if (!IsOpen) return false;
        switch (k)
        {
            case Godot.Key.Up: case Godot.Key.Left: case Godot.Key.W: case Godot.Key.A: Select(sel - 1); break;
            case Godot.Key.Down: case Godot.Key.Right: case Godot.Key.S: case Godot.Key.D: case Godot.Key.Tab: Select(sel + 1); break;
            case Godot.Key.Enter: case Godot.Key.KpEnter: case Godot.Key.Space: Choose(); break;
            case Godot.Key.Escape: Select(items.Count - 1); Choose(); break;
        }
        return true;
    }
}
