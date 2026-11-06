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
    public bool Own, Dead;
    public Texture2D? GeraldBack, GeraldFlag;          // BI_GetData BIDT_GERALD_DATA: the relation shield and the nation flag (small_nations.tga)
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
    public float[] FireZone = new float[4], FireDir = new float[4], FireRange = new float[4];     // fire-zone fans: angle, direction (rad, relative to the ship), range in metres
    public Texture2D? IslandMap;                    // the island seen from above (navigator minimap), engine coordinates of its centre and side
    public Vector2 IslandCentre, ShipPos;
    public float IslandSize;
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
    readonly Control menuBox = new() { Name = "CommandMenu", MouseFilter = Control.MouseFilterEnum.Ignore };
    readonly BarView bar = new() { Name = "CommandBar" };
    readonly Control markers = new() { Name = "Markers", MouseFilter = Control.MouseFilterEnum.Ignore };
    readonly Label debug = new() { Name = "SeaHudText" };

    public SeaHud()
    {
        Layer = 5;
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        BuildNavigator();
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
        bar.Hud = this; bar.Size = new Vector2(560, 108);
        menuBox.AddChild(bar);
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

    // ------------------------------------------------------------------ per frame
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
        // the command bar: the ships of the squadron (the first level of the command list, always shown) or the active menu
        bar.Set(d.Menu);
        bar.Visible = spyAlpha <= 0.01f;                     // the spyglass strip takes the bottom of the screen
        // BI_SET_VISIBLE (the script's BI_CommandVisible, e.g. under the spyglass): the battle interface parts disappear, the eyepiece / spyglass strip stay
        nav.Visible = markers.Visible = menuBox.Visible = d.BiVisible;
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

    // ------------------------------------------------------------------ command menu
    Action<int>? pick;
    public void SetPickHandler(Action<int> a) => pick = a;

    /// <summary>
    /// The icon row at the bottom left (BattleInterface.CommandShowParam): 64 x 64 icons 4 apart, left 16, top = bottom - 80, up to 8 shown with the left / right
    /// buttons (lr_buttons.tga, 8 x 64) at the ends when the list goes on, the note of the selected icon 28 above, the state strip of a ship (indicators.tga:
    /// sails white, crew blue, hull red above the dark empty part; 64 x 16) right under its icon, its nation shield (BI_GetData: back + flag, 32 x 32) over the corner,
    /// quantities (charge) printed in the lower part of the icon. All coordinates are pixels of the 800 x 600 screen (the parent is scaled by UiLayout.Hud).
    /// </summary>
    sealed partial class BarView : Control
    {
        public SeaHud Hud = null!;
        HudMenu? menu;
        const float Pitch = 68f, Icon = 64f, Top = 28f;
        const int Show = 8;

        public BarView() { MouseFilter = MouseFilterEnum.Stop; }

        public void Set(HudMenu? m) { menu = m; QueueRedraw(); }

        List<HudMenuItem> Items => menu is { Open: true } ? menu.Items : menu?.Passive ?? new();
        bool Active => menu is { Open: true };
        int First => Active ? Math.Clamp(menu!.Selected - Show / 2, 0, Math.Max(0, menu.Items.Count - Show)) : 0;

        public override void _GuiInput(InputEvent e)
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb || menu == null) return;
            int col = (int)(mb.Position.X / Pitch);
            if (mb.Position.Y < Top || mb.Position.Y > Top + Icon || mb.Position.X - col * Pitch > Icon) return;
            int idx = First + col;
            if (idx >= 0 && idx < Items.Count) { Hud.pick?.Invoke(idx); AcceptEvent(); }
        }

        void Img(Texture2D? t, Rect2 dst, Color? mod = null)
        {
            if (t != null) DrawTextureRect(t, dst, false, mod ?? Colors.White);
        }

        public override void _Draw()
        {
            var items = Items;
            if (items.Count == 0) return;
            var font = ThemeDB.FallbackFont;
            int first = First, last = Math.Min(items.Count, first + Show);
            int sel = Active ? menu!.Selected : -1;
            if (Active && menu!.Title.Length > 0)
            {
                DrawStringOutline(font, new Vector2(0, -10), menu.Title, HorizontalAlignment.Left, -1, 14, 5, Colors.Black);
                DrawString(font, new Vector2(0, -10), menu.Title, HorizontalAlignment.Left, -1, 14, new Color(0.75f, 0.78f, 0.9f));
            }
            var ind = GameAssets.Texture(Dir + "indicators.tga");
            for (int i = first; i < last; i++)
            {
                var it = items[i];
                float x = (i - first) * Pitch;
                bool on = i == sel;
                var tint = it.Enabled ? Colors.White : new Color(1, 1, 1, 0.35f);
                Img(on ? it.IconSelected ?? it.Icon : it.Icon, new Rect2(x, Top, Icon, Icon), tint);
                if (it.Ship is { } sh)
                {
                    // the state strip (the three bars of indicators.tga: the filled top half up to the ratio, the dark bottom half beyond it)
                    if (ind != null)
                    {
                        float[] ratio = { sh.Sails, sh.Crew, sh.Hull };
                        float[] y0 = { 0, 5, 10 }, y1 = { 5, 10, 16 };
                        for (int b = 0; b < 3; b++)
                        {
                            float r = Math.Clamp(ratio[b], 0f, 1f), w = r * Icon, h = y1[b] - y0[b];
                            if (w > 0.5f) DrawTextureRectRegion(ind, new Rect2(x, Top + Icon + y0[b], w, h), new Rect2(0, y0[b], w, h));
                            if (w < Icon - 0.5f) DrawTextureRectRegion(ind, new Rect2(x + w, Top + Icon + y0[b], Icon - w, h), new Rect2(w, 16 + y0[b], Icon - w, h));
                        }
                    }
                    Img(sh.GeraldBack, new Rect2(x + Icon - 34, Top + 2, 32, 32));
                    Img(sh.GeraldFlag, new Rect2(x + Icon - 34 + 4, Top + 2 + 4, 24, 24));
                }
                // the key that picks the icon
                if (Active && i < 9)
                {
                    DrawStringOutline(font, new Vector2(x + 4, Top + 14), (i + 1).ToString(), HorizontalAlignment.Left, -1, 13, 4, Colors.Black);
                    DrawString(font, new Vector2(x + 4, Top + 14), (i + 1).ToString(), HorizontalAlignment.Left, -1, 13, new Color(1f, 0.85f, 0.4f));
                }
                if (it.Count.Length > 0)
                {
                    // CommandShowParam printXOffset 32 / printYOffset -26 (bold_numbers): centred, 26 above the icon's lower edge
                    float cw = font.GetStringSize(it.Count, HorizontalAlignment.Left, -1, 20).X;
                    var at = new Vector2(x + 32 - cw / 2f, Top + Icon - 26 + 18);
                    DrawStringOutline(font, at, it.Count, HorizontalAlignment.Left, -1, 20, 6, Colors.Black);
                    DrawString(font, at, it.Count, HorizontalAlignment.Left, -1, 20, it.Enabled ? new Color(1f, 0.95f, 0.8f) : new Color(1f, 0.5f, 0.45f));
                }
                if (on)
                {
                    // the note: centred over the selected icon, 28 above its top (noteYOffset)
                    float nw = font.GetStringSize(it.Label, HorizontalAlignment.Left, -1, 16).X;
                    var at = new Vector2(Math.Max(0f, x + 32 - nw / 2f), Top - 12);          // the note stays on the screen at the left end
                    DrawStringOutline(font, at, it.Label, HorizontalAlignment.Left, -1, 16, 6, Colors.Black);
                    DrawString(font, at, it.Label, HorizontalAlignment.Left, -1, 16, new Color(1f, 0.95f, 0.7f));
                }
            }
            if (Active)
            {
                var lr = GameAssets.Texture(Dir + "lr_buttons.tga");
                if (lr != null)
                {
                    // buttonWidth 8, buttonHeight 64, buttonOffset 4: the left half of the picture before the first icon, the right half after the last one
                    if (first > 0) DrawTextureRectRegion(lr, new Rect2(-12, Top, 8, Icon), new Rect2(0, 0, 16, 64));
                    if (last < items.Count) DrawTextureRectRegion(lr, new Rect2((last - first) * Pitch, Top, 8, Icon), new Rect2(16, 0, 16, 64));
                }
            }
        }
    }
}
