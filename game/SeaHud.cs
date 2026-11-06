using System;
using System.Collections.Generic;
using Godot;

namespace SeaDogs;

/// <summary>One ship line of the squadron panel / the target panel.</summary>
public sealed class HudShip
{
    public string Name = "", Type = "", NationGroup = "", NationName = "";
    public float Hull = 1f, Sails = 1f, Crew = 1f;
    public int CrewCount;
    public float Distance;
    public bool Own;
}

/// <summary>What the spyglass shows of a ship (MSG_ISG_UPDATE); -1 / "" = the spyglass type does not show it.</summary>
public sealed class SpyGlassInfo
{
    public string Name = "", Type = "";
    public int Hull = -1, Sail = -1, Crew = -1, Cannons = -1, Charge = -1, Nation = -1;
    public float Speed = -1f;
}

/// <summary>A marker above a ship in the 3D view (friend / neutral / enemy).</summary>
public sealed class HudMarker
{
    public Vector2 Screen;
    public int Relation;          // 0 friend, 1 neutral, 2 enemy
    public float Distance;
    public string Name = "";
    public bool Target;
}

/// <summary>Everything the sea HUD shows, collected by SeaView from the script's attributes.</summary>
public sealed class HudData
{
    public bool On;
    public float Heading, WindTo, WindSpeed, Speed, MaxSpeed, SailState;
    public float[] Ready = { 1f, 1f, 1f, 1f };     // bow, starboard, stern, port: 1 = loaded, < 1 = reloading
    public float[] Damage = new float[4];          // DamageRatio of the four sides (destroyed guns, grey arc)
    public bool[] HasGuns = { false, false, false, false };
    public int Charge = -1;
    public int[] Ammo = new int[4];
    public string Action = "";
    public string ActionKey = "";
    public int ActionIcon = -1;
    public List<HudShip> Squadron = new();
    public HudShip? Target;
    public List<HudMarker> Markers = new();
    public string Info = "";
    public HudMenu? Menu;
    public int NavCharge = -1;                      // GetCurrentCharge: picture of the navigator charge icon (small_cannons.tga rows)
    public List<NavShip> NavShips = new();
    public bool BiVisible = true, NotEnoughBalls;     // battle_interface BI_SET_VISIBLE / BI_MSG_NOT_ENOUGH_BALLS_FLAG (BattleInterface.cs)
}

/// <summary>
/// Sea HUD built from the textures of the original battle interface (RESOURCE\Textures\battle_interface): navigator ring (wind and ship speed, gun readiness
/// of the four sides, compass and wind pointer), squadron panel, target panel, command bar and markers above the ships. The original draws these in the C++
/// class "battle_interface"; its layout is not in any file, so the arrangement here is my own.
/// </summary>
public partial class SeaHud : CanvasLayer
{
    const string Dir = "battle_interface\\";
    readonly Control root = new() { Name = "SeaHudRoot", MouseFilter = Control.MouseFilterEnum.Ignore };
    readonly NavigatorView nav = new() { Name = "Navigator" };
    readonly Label actionText = new();
    readonly VBoxContainer squad = new() { Name = "Squadron" };
    readonly Control targetPanel = new() { Name = "Target" };
    readonly Label targetName = new(), targetInfo = new();
    readonly ColorRect targetHull = new(), targetHullBack = new();
    readonly TextureRect targetFlag = new();
    readonly Control commands = new() { Name = "Commands" };
    readonly Control menuBox = new() { Name = "CommandMenu", MouseFilter = Control.MouseFilterEnum.Ignore };
    readonly Control markers = new() { Name = "Markers", MouseFilter = Control.MouseFilterEnum.Ignore };
    readonly List<Control> commandIcons = new();
    readonly Label[] ammoText = new Label[4];
    readonly TextureRect[] chargeIcon = new TextureRect[4];
    readonly Label debug = new() { Name = "SeaHudText" };

    public SeaHud()
    {
        Layer = 5;
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        BuildNavigator();
        BuildSquadron();
        BuildTarget();
        BuildCommands();
        root.AddChild(markers);
        scopeRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scopeRect.Visible = false;
        // the eyepiece textures (telescope.tga, eye_*Tub.tga) are white inside and dark at the rim: the original multiplies them over the picture
        scopeRect.Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Mul };
        root.AddChild(scopeRect);
        root.AddChild(scopeBarL);
        root.AddChild(scopeBarR);
        scopeBarL.Visible = scopeBarR.Visible = false;
        spyPanel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        spyPanel.Hud = this;
        root.AddChild(spyPanel);
        pointer.Visible = false;
        root.AddChild(pointer);
        menuBox.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        // BattleInterface.CommandShowParam: icons 64 x 64, 4 apart, left 16, top = bottom - 80, the note 28 above (800 x 600 pixels at UiLayout.Hud scale)
        menuBox.OffsetLeft = 16 * UiLayout.Hud; menuBox.OffsetTop = -(80 + 28) * UiLayout.Hud; menuBox.OffsetRight = (16 + 560) * UiLayout.Hud; menuBox.OffsetBottom = -16 * UiLayout.Hud;
        menuBox.Scale = new Vector2(UiLayout.Hud, UiLayout.Hud);
        root.AddChild(menuBox);
        debug.Visible = false;
        root.AddChild(debug);
        Visible = false;
    }

    // ------------------------------------------------------------------ building
    static Label MakeLabel(int size, Color? col = null, bool outline = true)
    {
        var l = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", col ?? new Color(1f, 0.95f, 0.8f));
        if (outline) { l.AddThemeColorOverride("font_outline_color", Colors.Black); l.AddThemeConstantOverride("outline_size", 4); }
        return l;
    }

    static TextureRect Tex(string file, Vector2 pos, Vector2 size, Rect2? region = null)
    {
        Texture2D? t = GameAssets.Texture(Dir + file);
        if (t != null && region is { } r) t = new AtlasTexture { Atlas = t, Region = r };
        var tr = new TextureRect { Texture = t, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
        tr.Position = pos; tr.Size = size;           // the size must be set after the expand mode, otherwise the texture's own size is the minimum
        return tr;
    }

    void BuildNavigator()
    {
        root.AddChild(nav);
    }

    void BuildSquadron()
    {
        squad.Position = new Vector2(10, 10);
        squad.AddThemeConstantOverride("separation", 4);
        squad.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(squad);
    }

    void BuildTarget()
    {
        targetPanel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        targetPanel.OffsetLeft = -150; targetPanel.OffsetRight = 150; targetPanel.OffsetTop = 10; targetPanel.OffsetBottom = 70;
        targetPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
        var back = new Panel { Size = new Vector2(300, 60), MouseFilter = Control.MouseFilterEnum.Ignore };
        back.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.02f, 0.03f, 0.12f, 0.6f), BorderColor = new Color(0.85f, 0.7f, 0.3f, 0.9f), BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4 });
        targetPanel.AddChild(back);
        targetFlag.Position = new Vector2(8, 8); targetFlag.Size = new Vector2(32, 32); targetFlag.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize; targetFlag.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        targetPanel.AddChild(targetFlag);
        targetName.Position = new Vector2(48, 4); targetName.Size = new Vector2(244, 24);
        foreach (var l in new[] { targetName, targetInfo }) { l.AddThemeFontSizeOverride("font_size", 15); l.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.8f)); l.AddThemeColorOverride("font_outline_color", Colors.Black); l.AddThemeConstantOverride("outline_size", 3); l.MouseFilter = Control.MouseFilterEnum.Ignore; }
        targetInfo.Position = new Vector2(48, 32); targetInfo.Size = new Vector2(244, 20);
        targetInfo.AddThemeFontSizeOverride("font_size", 13);
        targetHullBack.Position = new Vector2(48, 28); targetHullBack.Size = new Vector2(244, 4); targetHullBack.Color = new Color(0, 0, 0, 0.6f);
        targetHull.Position = new Vector2(48, 28); targetHull.Size = new Vector2(244, 4); targetHull.Color = new Color(0.85f, 0.15f, 0.12f);
        targetPanel.AddChild(targetName); targetPanel.AddChild(targetHullBack); targetPanel.AddChild(targetHull); targetPanel.AddChild(targetInfo);
        targetPanel.Visible = false;
        root.AddChild(targetPanel);
    }

    readonly (string key, string icon, Rect2 rect, string label)[] slots =
    {
        ("W/S", "", new Rect2(0, 192, 64, 64), "Вітрила"),
    };

    void BuildCommands()
    {
        commands.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        commands.OffsetLeft = -330; commands.OffsetRight = -10; commands.OffsetTop = -118; commands.OffsetBottom = -10;
        commands.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(commands);
        // the four kinds of ammunition: 1 balls, 2 grape, 3 knippels, 4 bombs (cicons_charge: one column per state, four rows)
        for (int i = 0; i < 4; i++)
        {
            var ic = Tex("cicons_charge.tga", new Vector2(i * 78, 50), new Vector2(60, 60), new Rect2(0, i * 64, 64, 64));
            commands.AddChild(ic); chargeIcon[i] = ic;
            var l = MakeLabel(14); l.Position = new Vector2(i * 78 + 2, 94); l.Size = new Vector2(60, 18); l.HorizontalAlignment = HorizontalAlignment.Right; commands.AddChild(l); ammoText[i] = l;
            var key = MakeLabel(13, new Color(1f, 0.85f, 0.4f)); key.Text = (i + 1).ToString(); key.Position = new Vector2(i * 78 + 2, 48); commands.AddChild(key);
        }
        actionText.AddThemeFontSizeOverride("font_size", 22);
        actionText.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.4f));
        actionText.AddThemeColorOverride("font_outline_color", Colors.Black);
        actionText.AddThemeConstantOverride("outline_size", 6);
        actionText.MouseFilter = Control.MouseFilterEnum.Ignore;
        actionText.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        actionText.OffsetLeft = -300; actionText.OffsetRight = 300; actionText.OffsetTop = -150; actionText.OffsetBottom = -100;
        actionText.HorizontalAlignment = HorizontalAlignment.Center;
        root.AddChild(actionText);
    }

    // ------------------------------------------------------------------ per frame
    readonly List<(Control row, ColorRect hull, ColorRect sails, ColorRect crew, Label name, TextureRect icon, TextureRect flag)> rows = new();

    Control Row(out ColorRect hull, out ColorRect sails, out ColorRect crew, out Label name, out TextureRect icon, out TextureRect flag)
    {
        var row = new Control { CustomMinimumSize = new Vector2(250, 54), MouseFilter = Control.MouseFilterEnum.Ignore };
        var back = new Panel { Size = new Vector2(250, 54), MouseFilter = Control.MouseFilterEnum.Ignore };
        back.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.02f, 0.03f, 0.12f, 0.55f), BorderColor = new Color(0.85f, 0.7f, 0.3f, 0.7f), BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1 });
        row.AddChild(back);
        icon = new TextureRect { Position = new Vector2(3, 3), Size = new Vector2(48, 48), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(icon);
        flag = new TextureRect { Position = new Vector2(34, 30), Size = new Vector2(20, 20), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(flag);
        name = MakeLabel(14); name.Position = new Vector2(58, 2); name.Size = new Vector2(188, 20); row.AddChild(name);
        ColorRect Bar(float y, Color c)
        {
            row.AddChild(new ColorRect { Position = new Vector2(60, y), Size = new Vector2(180, 6), Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Ignore });
            var b = new ColorRect { Position = new Vector2(60, y), Size = new Vector2(180, 6), Color = c, MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddChild(b); return b;
        }
        hull = Bar(26, new Color(0.85f, 0.15f, 0.12f));
        sails = Bar(34, new Color(0.9f, 0.9f, 0.8f));
        crew = Bar(42, new Color(0.95f, 0.75f, 0.2f));
        return row;
    }

    static Texture2D? NationFlag(HudShip s) => s.NationGroup == "" ? null : Atlas.Get(s.NationGroup, s.NationName);
    static Texture2D? ShipIcon(HudShip s) => Atlas.Get("SHIPS16", s.Type) ?? Atlas.Get("Ships16", s.Type);

    bool suspended;
    bool lastOn;
    public bool Suspended { get => suspended; set { suspended = value; Visible = lastOn && !suspended; } }

    // ---- telescope eyepiece
    readonly TextureRect scopeRect = new() { MouseFilter = Control.MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
    readonly ColorRect scopeBarL = new() { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore }, scopeBarR = new() { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
    string scopeTexName = "";
    float scopeAmount;
    public string ScopeTexture { set { if (value != scopeTexName) { scopeTexName = value; scopeRect.Texture = value.Length > 0 ? GameAssets.Texture(value) : null; } } }
    public float ScopeAmount
    {
        set
        {
            scopeAmount = value; scopeRect.Visible = value > 0.05f;
            scopeBarL.Visible = scopeBarR.Visible = scopeRect.Visible;
            scopeBarL.Modulate = scopeBarR.Modulate = scopeRect.Modulate;
            var sz = root.GetViewportRect().Size;                       // the round eyepiece keeps its proportions; black bars fill the sides of a wide window
            float bar = Math.Max(0f, (sz.X - sz.Y) / 2f);
            scopeBarL.Position = new Vector2(0, 0); scopeBarL.Size = new Vector2(bar, sz.Y);
            scopeBarR.Position = new Vector2(sz.X - bar, 0); scopeBarR.Size = new Vector2(bar, sz.Y);
        }
    }

    // ---- spyglass information panel (class "ispyglass", BATTLE_INTERFACE\ispyglass.c)
    readonly SpyPanel spyPanel = new() { Name = "SpyGlass", MouseFilter = Control.MouseFilterEnum.Ignore };
    public SpyGlassInfo? Spy;
    public bool SpyVisible;
    float spyAlpha;

    /// <summary>
    /// The layout of FillISpyGlassParameters (a 640 x 480 screen, bottomOffset 24): a strip 50 high above the bottom edge with the nation flag at the left,
    /// the ship type beside it and the name above the strip; icons 40 x 40 for hull (x 163), sails (255), crew (351), speed (436), cannons (520) with their
    /// numbers 42 to the right, and the charge at the right edge. The original fades it in / out in FadeTime = 0.5 s.
    /// </summary>
    sealed partial class SpyPanel : Control
    {
        public SeaHud Hud = null!;
        static readonly Rect2[] Nations = { new(0.1640625f, 0, 0.1640625f, 0.65625f), new(0.328125f, 0, 0.1640625f, 0.65625f), new(0.4921875f, 0, 0.1640625f, 0.65625f), new(0.65625f, 0, 0.1640625f, 0.65625f), new(0.8203125f, 0, 0.1640625f, 0.65625f) };
        static readonly Rect2 IcHull = new(0, 0, 0.3125f, 0.3125f), IcSail = new(0.3125f, 0, 0.3125f, 0.3125f), IcCrew = new(0.625f, 0, 0.3125f, 0.3125f),
            IcSpeed = new(0, 0.3125f, 0.3125f, 0.3125f), IcCannons = new(0.3125f, 0.3125f, 0.3125f, 0.3125f);
        static readonly Rect2[] IcCharge = { new(0.625f, 0.625f, 0.3125f, 0.3125f), new(0.625f, 0.3125f, 0.3125f, 0.3125f), new(0.3125f, 0.625f, 0.3125f, 0.3125f), new(0, 0.625f, 0.3125f, 0.3125f) };

        public override void _Draw()
        {
            var info = Hud.Spy;
            float a = Hud.spyAlpha;
            if (info == null || a <= 0.01f) return;
            var sz = Size;
            float sx = sz.X / 640f, s = sz.Y / 480f;
            var mod = new Color(1, 1, 1, a);
            var font = ThemeDB.FallbackFont;
            void Img(string file, Rect2 uv, float x, float y, float w, float h)
            {
                var t = GameAssets.Texture(Dir + file);
                if (t == null) return;
                var ts = t.GetSize();
                DrawTextureRectRegion(t, new Rect2(x, y, w, h), new Rect2(uv.Position * ts, uv.Size * ts), mod);
            }
            void Text(string txt, float x, float y, int size, Color c)
            {
                if (txt.Length == 0) return;
                var pos = new Vector2(x, y + size);
                DrawStringOutline(font, pos, txt, HorizontalAlignment.Left, -1, size, 5, new Color(0, 0, 0, a));
                DrawString(font, pos, txt, HorizontalAlignment.Left, -1, size, new Color(c.R, c.G, c.B, a));
            }
            Img("panel.tga", new Rect2(0, 0, 1, 1), 0, sz.Y - 74 * s, sz.X, 50 * s);
            float icon = 40 * s, iy = sz.Y - 69 * s, ty = sz.Y - 58 * s;
            int fsz = Math.Max(12, (int)(15 * s)), nsz = Math.Max(12, (int)(16 * s));
            var gold = new Color(1f, 0.92f, 0.65f);
            if (info.Nation is >= 0 and < 5) Img("all_nations.tga", Nations[info.Nation], 10 * sx, iy, icon, icon);
            Text(info.Type, 52 * sx, sz.Y - 60 * s, fsz, gold);
            Text(info.Name, 52 * sx, sz.Y - 104 * s, nsz, gold);
            void Field(int left, Rect2 uv, string val)
            {
                if (val.Length == 0) return;
                Img("spyglasses_icons.tga", uv, left * sx - 42 * s, iy, icon, icon);
                Text(val, left * sx, ty, fsz, Colors.White);
            }
            Field(205, IcHull, info.Hull >= 0 ? info.Hull + "%" : "");
            Field(297, IcSail, info.Sail >= 0 ? info.Sail + "%" : "");
            Field(393, IcCrew, info.Crew >= 0 ? info.Crew.ToString() : "");
            Field(478, IcSpeed, info.Speed >= 0f ? info.Speed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "");
            Field(562, IcCannons, info.Cannons >= 0 ? info.Cannons.ToString() : "");
            if (info.Charge is >= 0 and < 4) Img("spyglasses_icons.tga", IcCharge[info.Charge], sz.X - 42 * s, iy, icon, icon);
        }
    }

    // ---- ship pointer (class "shippointer"): the arrow over the ship chosen in the command menu
    readonly TextureRect pointer = new() { MouseFilter = Control.MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };

    /// <param name="screen">the middle of the lower edge</param><param name="size">pixels</param>
    public void SetPointer(Vector2? screen, bool friend, Vector2 size)
    {
        pointer.Visible = screen != null;
        if (screen is not { } at) return;
        pointer.Texture = GameAssets.Texture(Dir + (friend ? "Frendly.tga" : "Enemy.tga"));
        pointer.StretchMode = TextureRect.StretchModeEnum.Scale;
        pointer.Size = size;
        pointer.Position = at - new Vector2(size.X / 2f, size.Y);
    }

    public void Update(HudData d)
    {
        lastOn = d.On;
        float spyPrev = spyAlpha;
        spyAlpha = Math.Clamp(spyAlpha + (SpyVisible && scopeAmount > 0.5f ? 1f : -1f) * (float)GetProcessDeltaTime() / 0.5f, 0f, 1f);
        if (spyAlpha > 0.01f || spyPrev > 0.01f) spyPanel.QueueRedraw();      // a Control draws only when asked: the panel follows the glass data and the fade
        // navigator
        nav.Set(d);
        // squadron
        while (rows.Count < d.Squadron.Count)
        {
            var row = Row(out var h, out var s, out var c, out var n, out var ic, out var fl);
            squad.AddChild(row); rows.Add((row, h, s, c, n, ic, fl));
        }
        for (int i = 0; i < rows.Count; i++)
        {
            bool on = i < d.Squadron.Count;
            rows[i].row.Visible = on;
            if (!on) continue;
            var sh = d.Squadron[i];
            rows[i].name.Text = sh.Name;
            rows[i].hull.Size = new Vector2(180 * Math.Clamp(sh.Hull, 0f, 1f), 6);
            rows[i].sails.Size = new Vector2(180 * Math.Clamp(sh.Sails, 0f, 1f), 6);
            rows[i].crew.Size = new Vector2(180 * Math.Clamp(sh.Crew, 0f, 1f), 6);
            rows[i].icon.Texture = ShipIcon(sh);
            rows[i].flag.Texture = NationFlag(sh);
        }
        // target
        // as in the original: a foreign ship is described (name, hull, crew, distance) only through the telescope; by eye there is no target panel
        targetPanel.Visible = d.Target != null && scopeAmount > 0.5f;
        if (d.Target is { } t)
        {
            // a foreign ship's name and characteristics (hull, crew) are read only through the telescope (the ispyglass panel); by eye it is just a flag and a distance
            bool spy = scopeAmount > 0.5f;
            targetName.Text = spy ? t.Name : "";
            targetHull.Visible = spy;
            targetHull.Size = new Vector2(244 * Math.Clamp(t.Hull, 0f, 1f), 4);
            targetInfo.Text = spy ? $"{t.Distance:0} м   екіпаж {t.CrewCount}" : $"{t.Distance:0} м";
            targetFlag.Texture = NationFlag(t);
        }
        // ammunition bar
        for (int i = 0; i < 4; i++)
        {
            ammoText[i].Text = d.Ammo[i].ToString();
            bool sel = i == d.Charge;
            chargeIcon[i].Texture = ChargeTex(i, sel);
            chargeIcon[i].Modulate = d.Ammo[i] <= 0 ? new Color(1, 1, 1, 0.35f) : Colors.White;
        }
        actionText.Text = "";            // the hint with the key comes from the script (ILogAndActions)
        BuildMenuUi(d.Menu);
        commands.Visible = (d.Menu == null || !d.Menu.Open) && spyAlpha <= 0.01f;     // the spyglass strip takes the bottom of the screen
        // BI_SET_VISIBLE (the script's BI_CommandVisible, e.g. under the spyglass): the battle interface parts disappear, the eyepiece / spyglass strip stay
        nav.Visible = squad.Visible = markers.Visible = menuBox.Visible = d.BiVisible;
        if (!d.BiVisible) { targetPanel.Visible = false; commands.Visible = false; }
        // BI_MSG_NOT_ENOUGH_BALLS_FLAG: the navigator's charge picture blinks red when a broadside cannot be loaded (BattleNavigator::SetEnoughBalls)
        if (d.NotEnoughBalls && d.Charge >= 0 && d.Charge < 4)
        {
            float blink = (float)(Time.GetTicksMsec() % 1000) / 1000f;
            chargeIcon[d.Charge].Modulate = blink < 0.5f ? new Color(1f, 0.3f, 0.3f) : new Color(1f, 1f, 1f, 0.5f);
        }
        // markers over ships
        foreach (var c in markers.GetChildren()) c.QueueFree();
        // by eye the ships carry no markers (the original shows only the arrow of the ship chosen in the command menu, see 'pointer');
        // under the telescope each ship gets its relation triangle with the name and distance
        if (scopeAmount > 0.5f) foreach (var m in d.Markers)
        {
            var col = m.Relation == 2 ? new Color(1f, 0.25f, 0.2f) : m.Relation == 0 ? new Color(0.4f, 1f, 0.45f) : new Color(1f, 0.9f, 0.4f);
            var tri = new Polygon2D { Polygon = new[] { new Vector2(-8, -12), new Vector2(8, -12), new Vector2(0, 0) }, Color = col, Position = m.Screen };
            markers.AddChild(tri);
            var l = MakeLabel(m.Target ? 15 : 12, col); l.Text = scopeAmount > 0.5f && m.Name.Length > 0 ? $"{m.Name}  {m.Distance:0}" : $"{m.Distance:0}"; l.Position = m.Screen + new Vector2(-70, -34); l.Size = new Vector2(140, 20); l.HorizontalAlignment = HorizontalAlignment.Center;
            markers.AddChild(l);
        }
    }

    static Texture2D? ChargeTex(int i, bool selected)
    {
        var tex = GameAssets.Texture(Dir + "cicons_charge.tga");
        if (tex == null) return null;
        return new AtlasTexture { Atlas = tex, Region = new Rect2(selected ? 64 : 0, i * 64, 64, 64) };
    }

    // ------------------------------------------------------------------ command menu
    string menuSig = "";
    Action<int>? pick;
    public void SetPickHandler(Action<int> a) => pick = a;

    void BuildMenuUi(HudMenu? m)
    {
        string sig = m == null || !m.Open ? "" : m.Title + "|" + m.Selected + "|" + string.Join(",", m.Items.ConvertAll(i => i.Label + i.Enabled));
        if (sig == menuSig) return;
        menuSig = sig;
        foreach (var c in menuBox.GetChildren()) c.QueueFree();
        if (m == null || !m.Open) return;
        const int Show = 8; const float W = 68;
        int first = Math.Clamp(m.Selected - Show / 2, 0, Math.Max(0, m.Items.Count - Show));
        var title = MakeLabel(16); title.Text = m.Title; title.Position = new Vector2(0, -26); title.Size = new Vector2(560, 22); menuBox.AddChild(title);
        for (int i = first; i < Math.Min(m.Items.Count, first + Show); i++)
        {
            var it = m.Items[i];
            bool sel = i == m.Selected;
            var b = new TextureButton { TextureNormal = sel ? it.IconSelected ?? it.Icon : it.Icon, IgnoreTextureSize = true, StretchMode = TextureButton.StretchModeEnum.Scale,
                Position = new Vector2((i - first) * W, 28), Size = new Vector2(64, 64), FocusMode = Control.FocusModeEnum.None, Modulate = it.Enabled ? Colors.White : new Color(1, 1, 1, 0.35f) };
            int idx = i; b.Pressed += () => pick?.Invoke(idx);
            menuBox.AddChild(b);
            var num = MakeLabel(13, new Color(1f, 0.85f, 0.4f)); num.Text = (i + 1).ToString(); num.Position = new Vector2((i - first) * W + 3, 28); menuBox.AddChild(num);
            if (sel)
            {
                var note = MakeLabel(15); note.Text = it.Label; note.Position = new Vector2(0, 0); note.Size = new Vector2(560, 20); menuBox.AddChild(note);
                menuBox.AddChild(new ColorRect { Position = new Vector2((i - first) * W - 2, 26), Size = new Vector2(68, 2), Color = new Color(1f, 0.85f, 0.4f), MouseFilter = Control.MouseFilterEnum.Ignore });
            }
        }
        if (first > 0) { var l = MakeLabel(20); l.Text = "◄"; l.Position = new Vector2(-18, 46); menuBox.AddChild(l); }
        if (first + Show < m.Items.Count) { var l = MakeLabel(20); l.Text = "►"; l.Position = new Vector2(Show * W - 4, 46); menuBox.AddChild(l); }
    }
}
