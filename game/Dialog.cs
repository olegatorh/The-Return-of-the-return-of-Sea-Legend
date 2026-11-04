using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine's "dialog" class. The script owns the content: it writes the NPC's line to Dialog.Text and the player's answers to
/// Dialog.Links.l1, l2, ... (each with a .go node name). The engine shows them; clicking an answer stores Dialog.CurrentNode = go
/// and raises the "DialogEvent" event, which makes the script run ProcessDialogEvent for the new node.
/// </summary>
public partial class DialogView : Control
{
    readonly Interp it;
    public readonly Entity Ent;
    readonly Label nameLabel = new();
    readonly RichTextLabel text = new();
    readonly VBoxContainer links = new();
    string signature = "";

    public DialogView(Interp it, Entity ent)
    {
        this.it = it;
        Ent = ent;
        Name = "Dialog";
        Size = new Vector2(640, 480);
        Position = new Vector2(40, 30);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 50;

        var panel = new Panel { Position = new Vector2(20, 262), Size = new Vector2(600, 206), MouseFilter = MouseFilterEnum.Stop };
        var style = new StyleBoxFlat { BgColor = new Color(0.03f, 0.06f, 0.16f, 0.88f), BorderColor = new Color(0.85f, 0.68f, 0.3f), CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6 };
        style.SetBorderWidthAll(2);
        panel.AddThemeStyleboxOverride("panel", style);
        AddChild(panel);

        nameLabel.Position = new Vector2(34, 266);
        nameLabel.AddThemeColorOverride("font_color", new Color(1f, 0.82f, 0.35f));
        nameLabel.AddThemeFontSizeOverride("font_size", 17);
        AddChild(nameLabel);

        text.Position = new Vector2(34, 290);
        text.Size = new Vector2(572, 84);
        text.BbcodeEnabled = false;
        text.ScrollActive = true;
        text.AddThemeFontSizeOverride("normal_font_size", 15);
        text.AddThemeColorOverride("default_color", Colors.White);
        AddChild(text);

        links.Position = new Vector2(34, 378);
        links.Size = new Vector2(572, 86);
        links.AddThemeConstantOverride("separation", 0);
        AddChild(links);
    }

    static string Display(string s) => s.Replace("\\n", "\n");

    string Speaker()
    {
        var chr = it.Globals.TryGetValue("CharacterRef", out var s) ? s.Val.Attr : null;
        if (chr == null) return "";
        string n = chr.Get("name")?.Value ?? "", l = chr.Get("lastname")?.Value ?? "";
        return (n + " " + l).Trim();
    }

    public override void _Process(double delta)
    {
        // the script rewrites Dialog.Text / Dialog.Links when it handles a node: rebuild the view whenever they change
        string t = Ent.Root.Get("Text")?.Value ?? "";
        var ls = Ent.Root.Get("Links");
        var items = ls == null ? new List<(string text, string go)>() : ls.Children.Select(c => (text: c.Value ?? "", go: c.Get("go")?.Value ?? "")).ToList();
        string sig = t + "|" + string.Join("|", items.Select(i => i.text + ">" + i.go));
        if (sig == signature) return;
        signature = sig;

        nameLabel.Text = Speaker();
        text.Text = Display(t);
        foreach (var c in links.GetChildren()) c.QueueFree();
        for (int i = 0; i < items.Count; i++)
        {
            int idx = i;
            var b = new Button
            {
                Text = $"{i + 1}. {Display(items[i].text).Replace("\n", " ")}",
                Flat = true,
                Alignment = HorizontalAlignment.Left,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(566, 20),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                FocusMode = FocusModeEnum.None,
            };
            var empty = new StyleBoxEmpty();
            foreach (var s in new[] { "normal", "hover", "pressed", "focus" }) b.AddThemeStyleboxOverride(s, empty);
            b.AddThemeColorOverride("font_color", new Color(0.72f, 0.84f, 1f));
            b.AddThemeColorOverride("font_hover_color", new Color(1f, 0.85f, 0.4f));
            b.AddThemeFontSizeOverride("font_size", 14);
            b.Pressed += () => Choose(idx);
            links.AddChild(b);
        }
    }

    public int LinkCount => Ent.Root.Get("Links")?.Count ?? 0;

    public void Choose(int index)
    {
        var ls = Ent.Root.Get("Links");
        var link = ls?.ChildAt(index);
        if (link == null) return;
        string go = link.Get("go")?.Value ?? "";
        GD.Print($"[dialog] answer {index + 1} -> node '{go}'");
        Ent.Root.GetOrCreate("CurrentNode").Value = go;
        it.FireEvent("DialogEvent");
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true } k && k.PhysicalKeycode >= Key.Key1 && k.PhysicalKeycode <= Key.Key9)
        {
            Choose((int)(k.PhysicalKeycode - Key.Key1));
            GetViewport().SetInputAsHandled();
        }
    }
}
