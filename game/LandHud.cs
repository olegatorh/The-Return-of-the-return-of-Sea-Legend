using System;
using System.Collections.Generic;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine class "battle_land_interface" (BATTLE_INTERFACE\LandInterface.c): the land HUD in the bottom left corner.
/// <list type="bullet">
/// <item>Portraits of the hero and the officers (objLandInterface.data.icons.id0..id3: picture = FaceId, health, charge, shootMax/shootCur, poison;
/// the script refreshes them every 550 ms) on a soft shadow plate, with the bars of officerstate.tga (red = health, blue = pistol reload), the pistol
/// charges (guncharge.tga) and the message icons of each character (Characters[].MessageIcons.msgN.pic: level up, logbook, item; MessageIcons.tga).
/// The plate blinks red while the town is alarmed (data.alarm) and green for a poisoned character (Parameters Alarm*/Poison* colours).</item>
/// <item>The command menu (Enter = LICommandsActivate): the script enables Commands.* in the event BI_SetPossibleCommands, BI_CommandEndChecking says
/// whether a command needs a second choice (BI_COMMODE_USER_ICONS: fast travel places, potions ...) and BI_LaunchCommand executes it.
/// While the menu is open objLandInterface.ComState != 0 (the script then switches to "BattleInterfaceControls").</item>
/// </list>
/// Positions follow BLI_SetShowParameters / BLI_SetCommandParameters (values for a 640x480 screen: icons 64x64 at left+16, top = bottom-80, 8 apart;
/// bars 64x16; command icons 4 apart, caption 28 above). The script's own values are computed from showWindow, which this engine never sets, so the
/// constants are used here, anchored to the real bottom left corner of the wide window.
/// </summary>
public partial class LandHud : CanvasLayer
{
    const string Dir = "battle_interface\\";
    const int ModeUser = 512;
    // Parameters (LandInterface.c BLI_SetShowParameters)
    const float Left = 16, Bottom = 80, IconW = 64, IconH = 64, DistCom = 8, BarW = 64, BarH = 16, DistBar = 4;
    const float ShadowW = 20, ShadowH = 28, ShadowTop = 18, GunSize = 16, GunSpace = 2;
    // CommandShowParam
    const int MaxShow = 8;
    const float IconDist = 4, ButtonW = 8, ButtonOffset = 4, NoteY = -28;
    // MessageIcons
    const float MsgW = 64, MsgH = 24, MsgDist = 2, MsgBottom = 120;

    readonly Interp it;
    readonly Control root = new() { Name = "LandHudRoot", MouseFilter = Control.MouseFilterEnum.Ignore };
    readonly Control portraits = new() { Name = "Portraits", MouseFilter = Control.MouseFilterEnum.Ignore };
    readonly Control menu = new() { Name = "Commands", MouseFilter = Control.MouseFilterEnum.Ignore };
    Entity? ent;
    bool started, show = true;
    double clock;

    /// <summary>Set by Main every frame: a dialog, an interface screen or the sea owns the screen.</summary>
    public bool Suppressed;

    public LandHud(Interp interp)
    {
        it = interp;
        Name = "LandHud";
        Layer = 4;
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        root.AddChild(portraits);
        root.AddChild(menu);
        Visible = false;
    }

    Attr? Root => ent?.Root;

    // ------------------------------------------------------------------ messages
    public V OnMessage(Entity e, string fmt, V[] d)
    {
        ent = e;
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        switch (msg)
        {
            case 46100: started = true; break;                                       // MSG_BATTLE_LAND_START: Commands / UserIcons / textures are set
            case 46101: started = false; CloseMenu(false); break;                    // MSG_BATTLE_LAND_END
            case 46102: show = d.Length > 1 && Conv.ToInt(d[1]) != 0; if (!show) CloseMenu(false); break;   // MSG_BATTLE_LAND_SET_SHOW
            case 46103: case 46104: break;                                           // message icons / portrait textures: read from the characters directly
            case 46013:                                                              // BI_IN_SET_COMMAND_MODE: 0 = nothing to offer, -1 = refresh
                if (d.Length > 1 && Conv.ToInt(d[1]) == 0) { if (level == Level.Commands) CloseMenu(true); }
                else if (level == Level.Commands) ShowCommands(true);
                break;
        }
        return V.Int(0);
    }

    public void OnDeleted(Entity e)
    {
        if (!ReferenceEquals(e, ent)) return;
        ent = null; started = false; CloseMenu(false);
        Visible = false;
    }

    // ------------------------------------------------------------------ textures
    static Texture2D? Region(string file, Rect2 r)
    {
        var t = GameAssets.Texture(file);
        return t == null ? null : new AtlasTexture { Atlas = t, Region = r };
    }

    static ImageTexture? shadowTex;

    /// <summary>portraits_shadow.tga is white with a black middle (the original multiplies it onto the screen): turned into a dark plate with soft edges.</summary>
    static Texture2D? Shadow()
    {
        if (shadowTex != null) return shadowTex;
        var src = GameAssets.Texture(Dir + "portraits_shadow.tga");
        if (src == null) return null;
        var img = src.GetImage();
        if (img.IsCompressed()) img.Decompress();
        img.Convert(Image.Format.Rgba8);
        var res = Image.CreateEmpty(img.GetWidth(), img.GetHeight(), false, Image.Format.Rgba8);
        for (int y = 0; y < img.GetHeight(); y++)
            for (int x = 0; x < img.GetWidth(); x++)
            {
                var c = img.GetPixel(x, y);
                res.SetPixel(x, y, new Color(1, 1, 1, Math.Clamp(1f - c.V, 0f, 1f)));
            }
        shadowTex = ImageTexture.CreateFromImage(res);
        return shadowTex;
    }

    // the command textures (objLandInterface.CommandTextures.list.t<N>); the table of LandInterface.c BLI_SetObjectData is the fallback
    static readonly (string file, int xs, int ys)[] DefTex =
    {
        ("battle_interface\\LandCommands.tga", 2, 4), ("battle_interface\\LandTarget4.tga", 8, 1), ("battle_interface\\LandTarget1.tga", 1, 1), ("battle_interface\\Cancel.tga", 1, 1),
        ("battle_interface\\UsedItems1.tga", 2, 2), ("battle_interface\\UsedItems2.tga", 1, 2), ("battle_interface\\Viper_SailsMain.tga", 2, 2), ("battle_interface\\Viper_SailsPirate.tga", 4, 4),
        ("battle_interface\\UsedItems3.tga", 2, 2),
    };

    (string file, int xs, int ys) TexInfo(int n)
    {
        var t = Root?.Find("CommandTextures.list.t" + n, false);
        if (t?.Get("name")?.Value is { Length: > 0 } nm) return (nm, Math.Max(1, (int)F(t, "xsize", 1)), Math.Max(1, (int)F(t, "ysize", 1)));
        return n >= 0 && n < DefTex.Length ? DefTex[n] : ("", 1, 1);
    }

    /// <summary>Picture <paramref name="pic"/> (row by row) of command texture <paramref name="tex"/>; a file twice as wide as its cells holds the highlighted copy in its right half.</summary>
    Texture2D? CmdIcon(int tex, int pic, bool highlighted)
    {
        var (file, xs, ys) = TexInfo(tex);
        var texture = file.Length > 0 ? GameAssets.Texture(file) : null;
        if (texture == null) return null;
        float w = texture.GetWidth(), h = texture.GetHeight(), cw = w / xs, ch = h / ys;
        bool doubled = cw / ch > 1.5f;
        if (doubled) cw /= 2;
        float x = (pic % xs) * cw + (doubled && highlighted ? w / 2 : 0), y = (pic / xs % ys) * ch;
        return new AtlasTexture { Atlas = texture, Region = new Rect2(x, y, cw, ch) };
    }

    static float F(Attr? a, string path, float def = 0f) => a?.Find(path, false)?.Value is { Length: > 0 } v ? Conv.ParseFloatPrefix(v) : def;

    static TextureRect Rect(Texture2D? t) => new()
    {
        Texture = t, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    static Label MakeLabel(int size)
    {
        var l = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.8f));
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 4);
        return l;
    }

    // ------------------------------------------------------------------ portraits
    sealed class Slot
    {
        public readonly Control Box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        public TextureRect Shadow = null!, Face = null!, BarBack = null!, Health = null!, Charge = null!;
        public readonly List<TextureRect> Guns = new(), Msgs = new();
        public string FaceId = "";
    }
    readonly List<Slot> slots = new();

    Slot NewSlot()
    {
        var s = new Slot();
        s.Shadow = Rect(Shadow()); s.Shadow.Position = new Vector2(-ShadowW / 2, -ShadowTop - BarH - DistBar); s.Shadow.Size = new Vector2(IconW + ShadowW, IconH + ShadowH + BarH);
        s.Face = Rect(null); s.Face.Position = Vector2.Zero; s.Face.Size = new Vector2(IconW, IconH);
        s.BarBack = Rect(Region(Dir + "officerstate.tga", new Rect2(0, 16, 64, 16))); s.BarBack.Position = new Vector2(0, -DistBar - BarH); s.BarBack.Size = new Vector2(BarW, BarH);
        s.Charge = Rect(null); s.Charge.Position = s.BarBack.Position;
        s.Health = Rect(null); s.Health.Position = s.BarBack.Position + new Vector2(0, BarH / 2);
        foreach (var c in new Control[] { s.Shadow, s.Face, s.BarBack, s.Charge, s.Health }) s.Box.AddChild(c);
        portraits.AddChild(s.Box);
        return s;
    }

    static readonly Color AlarmLight = new(128 / 255f, 128 / 255f, 128 / 255f), AlarmDark = new(148 / 255f, 38 / 255f, 38 / 255f);
    static readonly Color PoisonLight = new(128 / 255f, 128 / 255f, 128 / 255f), PoisonDark = new(38 / 255f, 148 / 255f, 38 / 255f);

    /// <summary>0..1..0 blinking with the given rise and fall times.</summary>
    float Blink(float up, float down)
    {
        double t = clock % (up + down);
        return t < up ? (float)(t / up) : 1f - (float)((t - up) / down);
    }

    void UpdatePortraits(Vector2 vis)
    {
        var icons = Root?.Find("data.icons", false);
        float alarm = F(Root, "data.alarm");
        int n = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = icons?.Get("id" + i);
            if (a == null || a.Get("picture") == null) continue;
            if (n >= slots.Count) slots.Add(NewSlot());
            var s = slots[n++];
            s.Box.Visible = true;
            s.Box.Position = new Vector2(Left + (n - 1) * (IconW + DistCom), vis.Y - Bottom);
            string face = a.Get("picture")?.Value ?? "";
            if (face != s.FaceId) { s.FaceId = face; s.Face.Texture = GameAssets.Texture("interfaces\\portraits\\64\\face_" + face + ".tga"); }
            float hp = Math.Clamp(F(a, "health"), 0f, 1f), ch = Math.Clamp(F(a, "charge"), 0f, 1f);
            SetBar(s.Health, new Rect2(0, 8, 64, 8), hp);
            SetBar(s.Charge, new Rect2(0, 0, 64, 8), ch);
            bool poison = F(a, "poison") != 0;
            // the shadow plate: black; blinking dark red while the player's group is alarmed, dark green for a poisoned character
            var col = Colors.Black;
            if (poison) col = PoisonLight.Lerp(PoisonDark, Blink(0.3f, 0.7f));
            else if (alarm > 0f) col = AlarmLight.Lerp(AlarmDark, Blink(0.3f, 0.7f) * Math.Clamp(alarm, 0f, 1f));
            s.Shadow.Modulate = col;
            s.Face.Modulate = hp <= 0f ? new Color(0.4f, 0.4f, 0.4f) : Colors.White;
            UpdateGuns(s, (int)F(a, "shootMax"), (int)F(a, "shootCur"));
            UpdateMessages(s, CharacterByFace(i));
        }
        for (int i = n; i < slots.Count; i++) slots[i].Box.Visible = false;
    }

    static void SetBar(TextureRect r, Rect2 full, float f)
    {
        if (f <= 0.001f) { r.Visible = false; return; }
        r.Visible = true;
        var t = GameAssets.Texture(Dir + "officerstate.tga");
        if (r.Texture is not AtlasTexture at) { at = new AtlasTexture { Atlas = t }; r.Texture = at; }
        at.Region = new Rect2(full.Position, new Vector2(full.Size.X * f, full.Size.Y));
        r.Size = new Vector2(BarW * f, BarH / 2);
    }

    /// <summary>The pistol charges: a column of shot balls along the right edge of the portrait (loaded = the bright right half of guncharge.tga, spent = the dark left half).</summary>
    void UpdateGuns(Slot s, int max, int cur)
    {
        max = Math.Clamp(max, 0, 3);
        while (s.Guns.Count < max)
        {
            var g = Rect(null); g.Size = new Vector2(GunSize, GunSize);
            s.Box.AddChild(g); s.Guns.Add(g);
        }
        for (int k = 0; k < s.Guns.Count; k++)
        {
            var g = s.Guns[k];
            g.Visible = k < max;
            if (!g.Visible) continue;
            g.Position = new Vector2(IconW - GunSize - 1, IconH - (k + 1) * (GunSize + GunSpace));
            g.Texture = Region(Dir + "guncharge.tga", new Rect2(k < cur ? 16 : 0, 0, 16, 16));
        }
    }

    Attr? CharacterByFace(int officer)
    {
        var main = it.CallTop("GetMainCharacter").Attr;
        if (main == null) return null;
        int idx = Conv.ToInt(it.CallTop("GetOfficersIndex", V.A(main), V.Int(officer)));
        return idx < 0 ? null : it.CallTop("GetCharacter", V.Int(idx)).Attr;
    }

    /// <summary>The message icons of a character (AddMsgToCharacter: level up, logbook entry, new item), stacked above its bars and blinking.</summary>
    void UpdateMessages(Slot s, Attr? chr)
    {
        var mi = chr?.Get("MessageIcons");
        var pics = new List<int>();
        for (int k = 0; mi != null && k < mi.Count && pics.Count < 4; k++)
            if (mi.ChildAt(k) is { } m && m.Get("pic") != null) pics.Add((int)F(m, "pic"));
        while (s.Msgs.Count < pics.Count)
        {
            var r = Rect(null); r.Size = new Vector2(MsgW, MsgH);
            s.Box.AddChild(r); s.Msgs.Add(r);
        }
        float b = Blink(0.5f, 1.0f), lum = 0.55f + 0.45f * b;
        for (int k = 0; k < s.Msgs.Count; k++)
        {
            var r = s.Msgs[k];
            r.Visible = k < pics.Count;
            if (!r.Visible) continue;
            int p = pics[k];
            r.Texture = Region(Dir + "MessageIcons.tga", new Rect2((p % 2) * 64, (p / 2 % 2) * 32, 64, 32));
            // IconBottom = bottom-120: the lowest icon ends 40 above the portraits
            r.Position = new Vector2(0, -(MsgBottom - Bottom) - (k + 1) * MsgH - k * MsgDist);
            r.Modulate = new Color(lum, lum, lum);
        }
    }

    // ------------------------------------------------------------------ command menu
    enum Level { None, Commands, User }
    Level level;
    sealed class Item { public string Id = "", Label = "", Name = ""; public int Tex, Pic; }
    readonly List<Item> items = new();
    int sel;
    string command = "";
    string menuSig = "";

    public bool MenuOpen => level != Level.None;

    bool CanShow => started && show && ent != null && !Suppressed;

    int MainIndex() => Conv.ToInt(it.CallTop("GetMainCharacterIndex"));

    void Sound(string what) => it.FireEvent("evntBattleCommandSound", new List<V> { V.Str(what) });

    void SetComState(int v) { if (Root != null) Root.GetOrCreate("ComState").Value = v.ToString(); }

    /// <summary>A control of the land HUD: LICommandsActivate (Enter) opens the menu; BICommandsConfirm / Cancel / Left / Right drive it.</summary>
    public bool Command(string name)
    {
        switch (name)
        {
            case "LICommandsActivate":
                if (level != Level.None) { Confirm(); return true; }
                if (!CanShow) return false;
                ShowCommands(false);
                if (level != Level.None) Sound("activate");
                return true;
            case "BICommandsConfirm": if (level == Level.None) return false; Confirm(); return true;
            case "BICommandsCancel": if (level == Level.None) return false; CloseMenu(true); return true;
            case "BICommandsLeft": case "BICommandsRight":
                if (level == Level.None || items.Count == 0) return false;
                sel = (sel + (name == "BICommandsLeft" ? items.Count - 1 : 1)) % items.Count;
                Sound(name == "BICommandsLeft" ? "left" : "right");
                return true;
        }
        return false;
    }

    /// <summary>Keys while the menu is open (the original "BattleInterfaceControls": Enter, Esc, arrows; digits pick directly).</summary>
    public bool KeyPress(Godot.Key k)
    {
        if (level == Level.None) return false;
        switch (k)
        {
            case Godot.Key.Left: case Godot.Key.A: return Command("BICommandsLeft");
            case Godot.Key.Right: case Godot.Key.D: return Command("BICommandsRight");
            case Godot.Key.Enter: case Godot.Key.KpEnter: case Godot.Key.Space: return Command("BICommandsConfirm");
            case Godot.Key.Escape: case Godot.Key.Backspace: return Command("BICommandsCancel");
        }
        if (k >= Godot.Key.Key1 && k <= Godot.Key.Key8 && (int)(k - Godot.Key.Key1) < items.Count) { sel = (int)(k - Godot.Key.Key1); Confirm(); }
        return true;           // the menu has the keyboard
    }

    void ShowCommands(bool refresh)
    {
        string keep = refresh && sel < items.Count ? items[sel].Id : "";
        it.FireEvent("BI_SetPossibleCommands", new List<V> { V.Int(MainIndex()) });
        items.Clear();
        var cmds = Root?.Find("Commands", false);
        for (int i = 0; cmds != null && i < cmds.Count; i++)
        {
            var c = cmds.ChildAt(i);
            if (c == null || c.Name.Equals("Cancel", StringComparison.OrdinalIgnoreCase) || c.Get("event") == null) continue;
            if ((int)F(c, "enable") == 0) continue;
            items.Add(new Item { Id = c.Get("event")!.Value ?? "", Label = c.Get("note")?.Value ?? c.Name, Tex = (int)F(c, "texNum", 0), Pic = (int)F(c, "picNum", 0) });
        }
        if (items.Count == 0) { CloseMenu(false); return; }
        level = Level.Commands; command = "";
        sel = Math.Max(0, items.FindIndex(x => x.Id == keep));
        SetComState(1);
    }

    void Confirm()
    {
        if (sel < 0 || sel >= items.Count) return;
        var item = items[sel];
        if (level == Level.Commands)
        {
            command = item.Id;
            int mode = Conv.ToInt(it.FireEvent("BI_CommandEndChecking", new List<V> { V.Str(command) }));
            if ((mode & ModeUser) != 0 && ShowUserIcons()) return;
            Launch(command, "");
        }
        else if (level == Level.User) Launch(command, item.Name);
    }

    bool ShowUserIcons()
    {
        var ui = Root?.Find("UserIcons", false);
        var list = new List<Item>();
        for (int i = 0; ui != null && i < ui.Count; i++)
        {
            var u = ui.ChildAt(i)!;
            if ((int)F(u, "enable") == 0) continue;
            string name = u.Get("name")?.Value ?? u.Name;
            string note = u.Get("note")?.Value ?? "";
            if (note.Length == 0 && name == "cancel") note = "Скасувати";
            list.Add(new Item { Id = u.Name, Name = name, Label = note, Tex = (int)F(u, "tex", 0), Pic = (int)F(u, "pic", 0) });
        }
        // the cancel icon goes last (the original shows it as the way back)
        list.Sort((a, b) => (a.Name == "cancel" ? 1 : 0).CompareTo(b.Name == "cancel" ? 1 : 0));
        if (list.Count == 0) return false;
        items.Clear(); items.AddRange(list);
        level = Level.User; sel = 0;
        return true;
    }

    void Launch(string ev, string evName)
    {
        int ch = MainIndex();
        GD.Print($"[land] command {ev} '{evName}'");
        CloseMenu(false);
        Sound("deactivate");
        it.FireEvent("BI_LaunchCommand", new List<V> { V.Int(ch), V.Str(ev), V.Int(-1), V.Str(evName) });
    }

    void CloseMenu(bool sound)
    {
        if (level == Level.None) return;
        level = Level.None; items.Clear(); command = "";
        SetComState(0);
        if (sound) Sound("deactivate");
    }

    void UpdateMenu(Vector2 vis)
    {
        string sig = level == Level.None ? "" : level + "|" + sel + "|" + vis + "|" + string.Join(",", items.ConvertAll(i => i.Id + i.Tex + "." + i.Pic + i.Label));
        if (sig == menuSig) return;
        menuSig = sig;
        foreach (var c in menu.GetChildren()) c.QueueFree();
        if (level == Level.None) return;
        int first = Math.Clamp(sel - MaxShow / 2, 0, Math.Max(0, items.Count - MaxShow));
        float x0 = Left, y0 = vis.Y - Bottom;
        if (first > 0) x0 += ButtonW + ButtonOffset;
        int shown = Math.Min(items.Count - first, MaxShow);
        for (int k = 0; k < shown; k++)
        {
            var item = items[first + k];
            bool on = first + k == sel;
            var r = Rect(CmdIcon(item.Tex, item.Pic, on));
            r.Position = new Vector2(x0 + k * (IconW + IconDist), y0); r.Size = new Vector2(IconW, IconH);
            if (!on) r.Modulate = new Color(0.85f, 0.85f, 0.85f);
            menu.AddChild(r);
            if (on)
            {
                var note = MakeLabel(15); note.Text = item.Label;
                note.Position = new Vector2(x0 + k * (IconW + IconDist), y0 + NoteY); note.Size = new Vector2(400, 22);
                menu.AddChild(note);
            }
        }
        // the scroll buttons of lr_buttons.tga (left / right half) when more icons than fit
        if (first > 0)
        {
            var b = Rect(Region(Dir + "lr_buttons.tga", new Rect2(0, 0, 16, 64)));
            b.Position = new Vector2(Left, y0); b.Size = new Vector2(ButtonW, IconH); menu.AddChild(b);
        }
        if (first + shown < items.Count)
        {
            var b = Rect(Region(Dir + "lr_buttons.tga", new Rect2(16, 0, 16, 64)));
            b.Position = new Vector2(x0 + shown * (IconW + IconDist) - IconDist + ButtonOffset, y0); b.Size = new Vector2(ButtonW, IconH); menu.AddChild(b);
        }
    }

    // ------------------------------------------------------------------ per frame
    public override void _Process(double delta)
    {
        clock += delta;
        bool on = CanShow;
        if (!on && level != Level.None) CloseMenu(false);
        Visible = on;
        if (!on) return;
        var vis = root.GetViewportRect().Size;
        portraits.Visible = level == Level.None;            // the command icons take the place of the portraits
        if (portraits.Visible) UpdatePortraits(vis);
        UpdateMenu(vis);
    }

    /// <summary>For the screenshot diagnostics.</summary>
    public string DebugState() => $"started={started} show={show} suppressed={Suppressed} level={level} items=[{string.Join(", ", items.ConvertAll(i => i.Id + (i.Name.Length > 0 ? "/" + i.Name : "") + ":" + i.Label))}] sel={sel} slots={slots.FindAll(s => s.Box.Visible).Count}";
}
