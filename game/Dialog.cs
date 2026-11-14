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
    DialogFrame frame = null!;
    DialogHead head = null!;
    Control blocker = null!;
    const float TextX = 35f, TextW = 570f, LineH = 26f;
    const int MaxLines = 8;
    int relayout;

    public DialogView(Interp it, Entity ent)
    {
        this.it = it;
        Ent = ent;
        Name = "Dialog";
        Size = new Vector2(640, 480);
        Position = new Vector2(40, 30);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 50;

        // the window of the original (legacy_dialog.cpp): the carved frame of dialog\dialog.tga with the head window at the left, the name bar on top and the text / answer
        // lines growing upwards from the bottom bar; the frame is drawn from the sprite table of the original, the head is a live 3D view
        frame = new DialogFrame { MouseFilter = MouseFilterEnum.Ignore, Position = Vector2.Zero, Size = new Vector2(640, 480), ShowBehindParent = false };
        AddChild(frame);
        head = new DialogHead { Position = new Vector2(31, 28) };
        AddChild(head);
        AddChild(new HeadOverlay(frame));
        blocker = new Control { MouseFilter = MouseFilterEnum.Stop };           // the window takes the mouse clicks (nothing behind it is clicked)
        AddChild(blocker);

        nameLabel.Position = new Vector2(168, 26);
        nameLabel.AddThemeColorOverride("font_color", new Color(1f, 0.86f, 0.45f));
        nameLabel.AddThemeColorOverride("font_outline_color", new Color(0.05f, 0.03f, 0.0f));
        nameLabel.AddThemeConstantOverride("outline_size", 3);
        nameLabel.AddThemeFontSizeOverride("font_size", 19);
        AddChild(nameLabel);

        text.Position = new Vector2(TextX, 300);
        text.Size = new Vector2(TextW, 84);
        text.BbcodeEnabled = false;
        text.ScrollActive = true;
        text.AddThemeFontSizeOverride("normal_font_size", 15);
        text.AddThemeColorOverride("default_color", Colors.White);
        AddChild(text);

        links.Position = new Vector2(TextX, 378);
        links.Size = new Vector2(TextW, 86);
        links.AddThemeConstantOverride("separation", 0);
        AddChild(links);

        // NPC greeting: dialog.c (DialogPlayGreeting) writes Dialog.greeting = CharacterRef.greeting 10 frames after the start; the original
        // DIALOG class plays that alias (INI/ALIASES/Greetings_alias.ini) as speech when the attribute is written. The Dialog object is
        // global, so a value left from the previous dialog must not play: only writes made while this dialog lives count.
        HookHead(ent);
        ent.Root.GetOrCreate("greeting").OnWrite = a =>
        {
            if (!IsInstanceValid(this) || string.IsNullOrEmpty(a.Value)) return;
            if (lastGreeting > 0) SoundView.Instance?.StopVoice(lastGreeting);          // greetings never overlap, also across two dialogs in quick succession
            greetingVoice = lastGreeting = SoundView.Instance?.PlaySpeech(a.Value!) ?? 0;
            head.SetSpeaking(greetingVoice > 0);
            GD.Print($"[dialog] greeting '{a.Value}'");
        };
    }

    int greetingVoice;
    static int lastGreeting;

    void HookHead(Entity ent)
    {
        // Dialog.headModel is written by the script right after the entity is created (dialog.c StartDialogMain), Dialog.mood by quests that change the face
        var hm = ent.Root.GetOrCreate("headModel");
        hm.OnWrite = a => { if (IsInstanceValid(this)) head.SetHead(a.Value); };
        head.SetHead(hm.Value);
        var md = ent.Root.GetOrCreate("mood");
        md.OnWrite = a => { if (IsInstanceValid(this)) head.SetMood(a.Value); };
        head.SetMood(md.Value);
    }

    public override void _ExitTree()
    {
        if (Ent.Root.Get("greeting") is { } g) g.OnWrite = null;
        if (Ent.Root.Get("headModel") is { } hm) hm.OnWrite = null;
        if (Ent.Root.Get("mood") is { } md) md.OnWrite = null;
        if (greetingVoice > 0 && greetingVoice == lastGreeting) { SoundView.Instance?.StopVoice(greetingVoice); lastGreeting = 0; }          // the voice ends with the dialog window
    }

    static string Display(string s) => s.Replace("\\n", "\n");

    /// <summary>Test aid (env SEADOGS_LANGLOG=1): every shown dialog line / answer is logged and tagged RU (Russian-only letters) or EN (Latin words, no Cyrillic) when it looks untranslated.</summary>
    public static readonly bool LangLog = System.Environment.GetEnvironmentVariable("SEADOGS_LANGLOG") == "1";
    public static string Suspect(string s)
    {
        string body = s.Contains(": ") ? s[(s.IndexOf(": ", StringComparison.Ordinal) + 2)..] : s;
        bool cyr = false; int latinWords = 0;
        foreach (var w in System.Text.RegularExpressions.Regex.Matches(body, @"[A-Za-z]{3,}")) latinWords++;
        foreach (char ch in body) if (ch >= 'Ѐ' && ch <= 'ӿ') { cyr = true; break; }
        if (body.IndexOfAny(new[] { 'ы', 'э', 'ъ', 'ё', 'Ы', 'Э', 'Ъ', 'Ё' }) >= 0) return "RU";
        if (!cyr && latinWords >= 2) return "EN";
        return "";
    }
    public static void LogShown(string kind, string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        string sus = Suspect(s);
        GD.Print($"[lang] {kind}{(sus != "" ? " " + sus : "")}: {s.Replace("\n", " ")}");
    }

    string Speaker()
    {
        var chr = it.Globals.TryGetValue("CharacterRef", out var s) ? s.Val.Attr : null;
        if (chr == null) return "";
        string n = chr.Get("name")?.Value ?? "", l = chr.Get("lastname")?.Value ?? "";
        return (n + " " + l).Trim();
    }

    public override void _Process(double delta)
    {
        Position = new Vector2(40, 30) + UiLayout.Extra(this);
        if (greetingVoice > 0 && !(SoundView.Instance?.VoiceAlive(greetingVoice) ?? false)) { greetingVoice = 0; head.SetSpeaking(false); }          // the voice ended: the head idles again
        Process2(delta);
    }

    /// <summary>
    /// legacy_dialog.cpp: the text starts at y = 445 - lines * 26, the answers end at y = 450, and the blue body of the window is as many 26 px strips as the text and the
    /// answers need ((text + answers) / 26, one more with answers) growing upwards from the bottom bar; at most eight.
    /// </summary>
    void Relayout()
    {
        var font = text.GetThemeDefaultFont();
        foreach (var c in links.GetChildren()) if (c is Button b) b.CustomMinimumSize = new Vector2(TextW, 20);
        float linksH = 0f;
        foreach (var c in links.GetChildren()) if (c is Control ctl) linksH += Math.Max(20f, ctl.GetCombinedMinimumSize().Y);
        text.Size = new Vector2(TextW, text.Size.Y);
        float textMax = Math.Max(LineH, MaxLines * LineH - linksH - (linksH > 0 ? LineH : 0f) - 4f);
        float textH = Math.Min(Math.Max(font.GetHeight(15), text.GetContentHeight()), textMax);
        int tl = (int)Math.Floor((textH + linksH) / LineH) + (linksH > 0 ? 1 : 0);
        tl = Math.Clamp(tl, 0, MaxLines);
        frame.Set(tl, linksH > 0, linksH);
        float top = 445f - tl * LineH;
        text.Position = new Vector2(TextX, top); text.Size = new Vector2(TextW, textH);
        links.Position = new Vector2(TextX, 450f - linksH); links.Size = new Vector2(TextW, linksH);
        float bTop = 425f - tl * LineH;
        blocker.Position = new Vector2(-39f, bTop); blocker.Size = new Vector2(717f, 518f - bTop);
    }

    void Process2(double delta)
    {
        if (relayout > 0) { relayout--; Relayout(); }
        // the script rewrites Dialog.Text / Dialog.Links when it handles a node: rebuild the view whenever they change
        string t = Ent.Root.Get("Text")?.Value ?? "";
        var ls = Ent.Root.Get("Links");
        var items = ls == null ? new List<(string text, string go)>() : ls.Children.Select(c => (text: c.Value ?? "", go: c.Get("go")?.Value ?? "")).ToList();
        string sig = t + "|" + string.Join("|", items.Select(i => i.text + ">" + i.go));
        if (sig == signature) return;
        signature = sig;

        for (int i = 0; i < items.Count; i++)
            if (string.IsNullOrWhiteSpace(items[i].text)) GD.Print($"[dialog] EMPTY answer {i + 1} (go='{items[i].go}') in node '{Ent.Root.Get("CurrentNode")?.Value}' of {Speaker()}");
        if (LangLog)
        {
            LogShown("line", Speaker() + ": " + t);
            foreach (var it2 in items) LogShown("answer", it2.text);
        }
        nameLabel.Text = Speaker().ToUpperInvariant();
        text.Text = Display(t);
        foreach (var c in links.GetChildren()) c.QueueFree();
        sel = 0;
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
        PaintSelection();
        relayout = 3;
    }

    int sel;

    /// <summary>The answer chosen with the arrow keys is drawn in the highlight colour.</summary>
    void PaintSelection()
    {
        int i = 0;
        foreach (var c in links.GetChildren())
            if (c is Button b) b.AddThemeColorOverride("font_color", i++ == sel ? new Color(1f, 0.85f, 0.4f) : new Color(0.72f, 0.84f, 1f));
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
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        int n = LinkCount;
        if (k.PhysicalKeycode >= Key.Key1 && k.PhysicalKeycode <= Key.Key9)
        {
            Choose((int)(k.PhysicalKeycode - Key.Key1));
            GetViewport().SetInputAsHandled();
        }
        else if (n > 0 && k.PhysicalKeycode is Key.Up or Key.W) { sel = (sel + n - 1) % n; PaintSelection(); GetViewport().SetInputAsHandled(); }
        else if (n > 0 && k.PhysicalKeycode is Key.Down or Key.S) { sel = (sel + 1) % n; PaintSelection(); GetViewport().SetInputAsHandled(); }
        else if (n > 0 && k.PhysicalKeycode is Key.Enter or Key.KpEnter or Key.Space)            // the original DlgAction (Space) picks the highlighted answer
        {
            Choose(Math.Clamp(sel, 0, n - 1));
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>The window's carved frame, drawn from the sprite table of legacy_dialog.cpp (SPRITE_DATA; positions in the 640 x 480 dialog, rectangles in pixels of dialog.tga 1024 x 256).</summary>
    public sealed partial class DialogFrame : Control
    {
        static readonly Rect2 Left = R(-39, -39, 169, 216), LeftUv = U(0, 0, 208, 255), Top = R(169, -39, 678, 79), TopUv = U(208, 0, 757, 118), Bottom = R(-39, 451, 678, 518), BottomUv = U(209, 189, 1023, 255);
        static readonly Rect2 Line = R(-39, 425, 678, 451), LineUv = U(209, 155, 1023, 186), Cap = R(-39, 425, 678, 451), CapUv = U(209, 119, 1023, 156), Divider = R(35, 445, 605, 462), DividerUv = U(209, 94, 602, 116);
        static Rect2 R(float l, float t, float r, float b) => new(l, t, r - l, b - t);
        static Rect2 U(float l, float t, float r, float b) => new(l, t, r - l, b - t);
        Texture2D? tex;
        int lines; bool divider; float linksH;
        public Rect2 HeadTopDst = R(29, 25, 147, 37), HeadTopUv = U(904, 91, 1023, 103), HeadBotDst = R(29, 173, 146, 185), HeadBotUv = U(904, 105, 1023, 116);

        public void Set(int tl, bool withDivider, float links)
        {
            if (tl == lines && withDivider == divider && Math.Abs(links - linksH) < 0.5f) return;
            lines = tl; divider = withDivider; linksH = links; QueueRedraw();
        }

        public Texture2D? Tex => tex ??= GameAssets.Texture("dialog\\dialog.tga");

        public override void _Draw()
        {
            var t = Tex;
            if (t == null) { DrawRect(new Rect2(0, 425 - lines * LineH, 640, 56 + lines * LineH), new Color(0.03f, 0.06f, 0.16f, 0.9f)); return; }
            DrawTextureRectRegion(t, Left, LeftUv); DrawTextureRectRegion(t, Top, TopUv); DrawTextureRectRegion(t, Bottom, BottomUv);
            DrawTextureRectRegion(t, HeadTopDst, HeadTopUv); DrawTextureRectRegion(t, HeadBotDst, HeadBotUv);
            for (int i = 0; i < lines; i++) DrawTextureRectRegion(t, new Rect2(Line.Position.X, Line.Position.Y - LineH * i, Line.Size.X, Line.Size.Y), LineUv);
            DrawTextureRectRegion(t, new Rect2(Cap.Position.X, Cap.Position.Y - LineH * lines, Cap.Size.X, Cap.Size.Y), CapUv);
            if (divider) DrawTextureRectRegion(t, new Rect2(Divider.Position.X, Divider.Position.Y - linksH, Divider.Size.X, Divider.Size.Y), DividerUv);
            CloseSides();
        }

        // The sprites of the original run past both screen edges (the carved leaves are simply cut by the screen). On a wider window the 4:3 frame would end in a raw cut, so each cut edge
        // gets a bevelled gold border (outer line, gold, highlight, wood shade) and the strips beside the frame are dimmed a little.
        static readonly Color[] Bevel = { new(0.08f, 0.05f, 0.02f), new(0.78f, 0.62f, 0.27f), new(0.92f, 0.80f, 0.45f), new(0.50f, 0.34f, 0.14f), new(0.36f, 0.24f, 0.10f), new(0.22f, 0.14f, 0.06f), new(0.08f, 0.05f, 0.02f) };
        static readonly float[] BevelW = { 1.2f, 2.2f, 1.2f, 2.2f, 2.2f, 1.6f, 1.0f };      // total 11.6 units, from the outer edge inwards

        void CloseSides()
        {
            float bTop = 425f - lines * LineH;
            // dim the strips outside the frame (only visible on windows wider than 4:3)
            var dim = new Color(0f, 0f, 0f, 0.38f);
            DrawRect(new Rect2(-4000f, -2000f, 4000f - 39f, 5000f), dim);
            DrawRect(new Rect2(678f, -2000f, 4000f, 5000f), dim);
            // left edge: the head block and the bottom panel; right edge: the top plank and the bottom panel
            Edge(-39f, -39f, 216f, true); Edge(-39f, bTop, 518f, true);
            Edge(678f, -39f, 79f, false); Edge(678f, bTop, 518f, false);
        }

        void Edge(float x, float y0, float y1, bool left)
        {
            float o = 0f;
            for (int i = 0; i < Bevel.Length; i++)
            {
                float w = BevelW[i], px = left ? x + o : x - o - w;
                DrawRect(new Rect2(px, y0, w, y1 - y0), Bevel[i]);
                o += w;
            }
            // a soft shadow cast outwards by the border
            for (int k = 0; k < 5; k++)
                DrawRect(new Rect2(left ? x - (k + 1) * 1.4f : x + k * 1.4f, y0, 1.4f, y1 - y0), new Color(0f, 0f, 0f, 0.30f * (1f - k / 5f)));
        }
    }

    /// <summary>The bevel of the head window (the first two sprites of the table) drawn over the 3D head, as the original draws it last.</summary>
    sealed partial class HeadOverlay : Control
    {
        readonly DialogFrame f;
        public HeadOverlay(DialogFrame frame) { f = frame; MouseFilter = MouseFilterEnum.Ignore; Size = new Vector2(640, 480); }
        public override void _Draw()
        {
            var t = f.Tex;
            if (t == null) return;
            DrawTextureRectRegion(t, f.HeadTopDst, f.HeadTopUv); DrawTextureRectRegion(t, f.HeadBotDst, f.HeadBotUv);
        }
    }
}
