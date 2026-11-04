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
    readonly Control nav = new() { Name = "Navigator", MouseFilter = Control.MouseFilterEnum.Ignore };
    readonly TextureProgressBar windBar = new(), speedBar = new();
    readonly TextureRect[] gunArc = new TextureRect[4];
    readonly Control compassBox = new(), windBox = new();
    readonly Label speedText = new(), sailText = new(), actionText = new();
    readonly VBoxContainer squad = new() { Name = "Squadron" };
    readonly Control targetPanel = new() { Name = "Target" };
    readonly Label targetName = new(), targetInfo = new();
    readonly ColorRect targetHull = new(), targetHullBack = new();
    readonly TextureRect targetFlag = new();
    readonly Control commands = new() { Name = "Commands" };
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
        const float S = 230f;                                     // the navigator textures are 256x256
        nav.Size = new Vector2(S, S);
        nav.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        nav.Position = new Vector2(8, -S - 8);
        nav.OffsetLeft = 8; nav.OffsetTop = -S - 8; nav.OffsetRight = 8 + S; nav.OffsetBottom = -8;
        root.AddChild(nav);
        float k = S / 256f;
        nav.AddChild(Tex("indicators_dark_and_center_ship.tga", Vector2.Zero, new Vector2(S, S)));
        // gun readiness: the four arcs of the ring texture (bow, starboard, stern, port)
        var rects = new Rect2[] { new(80, 24, 100, 32), new(176, 40, 60, 176), new(80, 202, 100, 32), new(20, 40, 60, 176) };
        for (int i = 0; i < 4; i++)
        {
            gunArc[i] = Tex("indicators_cannons_reload.tga", rects[i].Position * k, rects[i].Size * k, rects[i]);
            nav.AddChild(gunArc[i]);
        }
        // wind (left half, blue) and ship speed (right half, orange) fill the outer ring from the bottom gap upwards
        var ring = GameAssets.Texture(Dir + "indicators_wind_and_ship.tga");
        foreach (var (bar, cw) in new[] { (windBar, false), (speedBar, true) })
        {
            bar.TextureProgress = ring;
            bar.FillMode = (int)TextureProgressBar.FillModeEnum.Clockwise;
            bar.NinePatchStretch = false;
            bar.MinValue = 0; bar.MaxValue = 1; bar.Step = 0.001;
            bar.Position = Vector2.Zero; bar.Size = new Vector2(S, S);
            bar.RadialInitialAngle = cw ? 180f : 180f;
            bar.RadialFillDegrees = 180f;
            bar.MouseFilter = Control.MouseFilterEnum.Ignore;
            nav.AddChild(bar);
        }
        windBar.FillMode = (int)TextureProgressBar.FillModeEnum.CounterClockwise;
        // the compass card turns with the heading
        compassBox.Position = new Vector2(S / 2, S / 2); compassBox.Size = Vector2.Zero;
        var card = Tex("compass.tga", new Vector2(-S * 0.36f, -S * 0.36f), new Vector2(S * 0.72f, S * 0.72f));
        card.PivotOffset = card.Size / 2;
        compassBox.AddChild(card);
        nav.AddChild(compassBox);
        // wind pointer around the card
        windBox.Position = new Vector2(S / 2, S / 2); windBox.Size = Vector2.Zero;
        var arrow = Tex("wind_pointer.tga", new Vector2(-9, -S * 0.40f), new Vector2(18, S * 0.30f));
        arrow.PivotOffset = new Vector2(9, S * 0.40f);
        windBox.AddChild(arrow);
        nav.AddChild(windBox);
        speedText.Position = new Vector2(0, S - 6); nav.AddChild(speedText);
        sailText.Position = new Vector2(0, S + 14);
        foreach (var l in new[] { speedText, sailText }) { l.AddThemeFontSizeOverride("font_size", 15); l.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.8f)); l.AddThemeColorOverride("font_outline_color", Colors.Black); l.AddThemeConstantOverride("outline_size", 4); l.MouseFilter = Control.MouseFilterEnum.Ignore; }
        speedText.Position = new Vector2(S / 2 - 50, S / 2 + 30); speedText.Size = new Vector2(100, 20); speedText.HorizontalAlignment = HorizontalAlignment.Center;
        sailText.Visible = false;
        nav.AddChild(speedText);
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

    public void Update(HudData d)
    {
        Visible = d.On;
        if (!d.On) return;
        // navigator
        float heading = d.Heading;
        ((Control)compassBox.GetChild(0)).Rotation = -heading;
        ((Control)compassBox.GetChild(0)).PivotOffset = ((Control)compassBox.GetChild(0)).Size / 2;
        windBox.Rotation = d.WindTo - heading;
        windBar.Value = Math.Clamp(d.WindSpeed / 30f, 0f, 1f);
        speedBar.Value = Math.Clamp(d.Speed / 20f, 0f, 1f);
        speedText.Text = $"{d.Speed:0.0}";
        for (int i = 0; i < 4; i++)
        {
            gunArc[i].Visible = d.HasGuns[i];
            float r = d.Ready[i];
            gunArc[i].Modulate = r >= 1f ? new Color(1f, 1f, 1f, 1f) : new Color(1f, 1f, 1f, 0.25f + 0.4f * r);
        }
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
        targetPanel.Visible = d.Target != null;
        if (d.Target is { } t)
        {
            targetName.Text = t.Name;
            targetHull.Size = new Vector2(244 * Math.Clamp(t.Hull, 0f, 1f), 4);
            targetInfo.Text = $"{t.Distance:0} м   екіпаж {t.CrewCount}";
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
        // markers over ships
        foreach (var c in markers.GetChildren()) c.QueueFree();
        foreach (var m in d.Markers)
        {
            var col = m.Relation == 2 ? new Color(1f, 0.25f, 0.2f) : m.Relation == 0 ? new Color(0.4f, 1f, 0.45f) : new Color(1f, 0.9f, 0.4f);
            var tri = new Polygon2D { Polygon = new[] { new Vector2(-8, -12), new Vector2(8, -12), new Vector2(0, 0) }, Color = col, Position = m.Screen };
            markers.AddChild(tri);
            var l = MakeLabel(m.Target ? 15 : 12, col); l.Text = $"{m.Name}  {m.Distance:0}"; l.Position = m.Screen + new Vector2(-70, -34); l.Size = new Vector2(140, 20); l.HorizontalAlignment = HorizontalAlignment.Center;
            markers.AddChild(l);
        }
    }

    static Texture2D? ChargeTex(int i, bool selected)
    {
        var tex = GameAssets.Texture(Dir + "cicons_charge.tga");
        if (tex == null) return null;
        return new AtlasTexture { Atlas = tex, Region = new Rect2(selected ? 64 : 0, i * 64, 64, 64) };
    }
}
