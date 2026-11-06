using System;
using System.Collections.Generic;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>A ship on the navigator minimap: offset from the player's ship in the world (x east, z north), heading, relation 0 friend / 1 neutral / 2 enemy.</summary>
public sealed class NavShip
{
    public float Dx, Dz, Ay;
    public int Relation;
    public bool Dead;
}

/// <summary>
/// The sea navigator (the engine class BATTLE_NAVIGATOR, battle_interface/src/sea/battle_navigator.cpp, with the parameters of BattleInterface.navigation set by
/// BattleInterface.c): in the top right corner of the screen a square of navigatorWidth x navigatorHeight (192 px at 800 x 600) holding, from the back:
/// the minimap disc (mapRadius 54 px, sea colour; ships as small triangles in the colour of their relation, 400 m x scale per radius, turned with the heading),
/// the "dark and centre ship" ring, the compass card turned by -heading, the gun-charge arcs of the four sides (angle ranges leftChargeBegAngle...), the wind and
/// ship speed arcs (windSpeedBegAngle / shipSpeedBegAngle), the wind pointer (windWidth x windHeight, turned by wind angle - heading), the wind and ship speed
/// numbers (speedOutYOffset above the centre, +-80 px aside) and the current charge picture at chargePos (from the square's top left corner in PotC).
/// All numbers are the script's (800 x 600 pixels) times <see cref="UiLayout.Hud"/>.
/// </summary>
public partial class NavigatorView : Control
{
    const string Dir = "battle_interface\\";
    // BattleInterface.c: navigation.*
    const float Size0 = 192f, MapRadius = 54f, WorldRad = 400f, MinScale = 0.5f, MaxScale = 4f, ShipShowRadius = 8f, WindW = 30f, WindH = 120f;
    const float SpeedOutY = -88f, ShipSpeedX = 80f, WindSpeedX = -80f, FontScale = 0.75f, MaxWind = 30f, MaxShipSpeed = 20f;
    static readonly float D2R = MathF.PI / 180f;
    const float LeftBeg = 215, LeftEnd = 325, RightBeg = 145, RightEnd = 35, FwdBeg = 335, FwdEnd = 385, BackBeg = 205, BackEnd = 155;
    const float ShipSpeedBeg = 175, ShipSpeedEnd = 55, WindSpeedBeg = 180, WindSpeedEnd = 410;
    static readonly Color Ready = new(0f, 155 / 255f, 0f), Charging = new(1f, 0f, 0f), Damaged = new(64 / 255f, 64 / 255f, 64 / 255f);
    static readonly Color SeaColor = new(10 / 255f, 30 / 255f, 80 / 255f), Enemy = new(1f, 0f, 0f), Friend = new(0f, 1f, 0f), Neutral = new(0.5f, 0.5f, 0.5f), Dead = new(0f, 0f, 1f);

    readonly Label windText = MakeText(), shipText = MakeText();
    float heading, windAngle, windSpeed, shipSpeed, scale = 1f;
    float[] charge = { 1f, 1f, 1f, 1f }, damage = new float[4];
    bool[] hasGuns = { true, true, true, true };
    int chargePic = -1;
    bool notEnough;
    List<NavShip> ships = new();

    static float U => UiLayout.Hud;
    public static float Side => Size0 * U;

    static Label MakeText()
    {
        var l = new Label { MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", Math.Max(10, (int)MathF.Round(28 * FontScale * UiLayout.Hud)));
        l.AddThemeColorOverride("font_color", Colors.White);
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 4);
        return l;
    }

    public NavigatorView()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        float s = Side;
        SetAnchorsPreset(LayoutPreset.TopRight);                                  // rightPos / topPos = the edges of the show window
        OffsetLeft = -s; OffsetRight = 0; OffsetTop = 0; OffsetBottom = s;
        AddChild(windText); AddChild(shipText);
        float cx = s / 2, cy = s / 2;
        windText.Size = shipText.Size = new Vector2(60 * U, 30 * U);
        windText.Position = new Vector2(cx + WindSpeedX * U - windText.Size.X / 2, cy + SpeedOutY * U);
        shipText.Position = new Vector2(cx + ShipSpeedX * U - shipText.Size.X / 2, cy + SpeedOutY * U);
    }

    public void Set(HudData d)
    {
        heading = d.Heading; windAngle = d.WindTo; windSpeed = d.WindSpeed; shipSpeed = d.Speed;
        for (int i = 0; i < 4; i++) { charge[i] = d.Ready[i]; hasGuns[i] = d.HasGuns[i]; }
        chargePic = d.NavCharge; notEnough = d.NotEnoughBalls;
        ships = d.NavShips;
        windText.Text = $"{windSpeed:0.0}"; shipText.Text = $"{shipSpeed:0.0}";
        QueueRedraw();
    }

    // ---- BATTLE_NAVIGATOR::CalculateCrossX/Y, SetRectangleSegVertexPos/Tex
    static float CrossX(int side, float w, float h, float a) => side switch { 0 => h / 2 * MathF.Tan(a), 1 => w / 2, 2 => -h / 2 * MathF.Tan(a), _ => -w / 2 };
    static float CrossY(int side, float w, float h, float a) => side switch { 0 => -h / 2, 1 => w / 2 * MathF.Tan(a - MathF.PI / 2), 2 => h / 2, _ => -w / 2 * MathF.Tan(a - MathF.PI / 2) };

    static float Norm(float a)
    {
        while (a < 0) a += 2 * MathF.PI;
        while (a > 2 * MathF.PI) a -= 2 * MathF.PI;
        return a;
    }

    /// <summary>A textured pie slice of the square (centre, width) from <paramref name="beg"/> to <paramref name="end"/> radians, clockwise from "up".</summary>
    void Sector(Texture2D? tex, Color col, Vector2 centre, float w, float beg, float end)
    {
        if (tex == null || MathF.Abs(beg - end) < 1e-4f) return;
        beg = Norm(beg); end = Norm(end);
        float alpha = MathF.Atan2(w / 2, w / 2);
        int Seg(float a) => a < alpha ? 0 : a < MathF.PI - alpha ? 1 : a < MathF.PI + alpha ? 2 : a < 2 * MathF.PI - alpha ? 3 : 0;
        int begSeg = Seg(beg), endSeg = Seg(end);
        var pts = new List<Vector2> { centre };
        var uvs = new List<Vector2> { new(0.5f, 0.5f) };
        void Add(float px, float py) { pts.Add(centre + new Vector2(px, py)); uvs.Add(new Vector2(0.5f + px / w, 0.5f + py / w)); }
        int cur = begSeg;
        for (int guard = 0; guard < 8; guard++)
        {
            if (cur == begSeg) Add(CrossX(cur, w, w, beg), CrossY(cur, w, w, beg));
            if (cur == endSeg) { Add(CrossX(cur, w, w, end), CrossY(cur, w, w, end)); break; }
            switch (cur)
            {
                case 0: Add(w / 2, -w / 2); break;
                case 1: Add(w / 2, w / 2); break;
                case 2: Add(-w / 2, w / 2); break;
                default: Add(-w / 2, -w / 2); break;
            }
            cur = (cur + 1) % 4;
        }
        for (int i = 1; i + 1 < pts.Count; i++)
            DrawPrimitive(new[] { pts[0], pts[i], pts[i + 1] }, new[] { col, col, col }, new[] { uvs[0], uvs[i], uvs[i + 1] }, tex);
    }

    void Rotated(Texture2D? tex, Vector2 centre, float w, float h, float angle)
    {
        if (tex == null) return;
        DrawSetTransform(centre, angle, Vector2.One);
        DrawTextureRect(tex, new Rect2(-w / 2, -h / 2, w, h), false);
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    static float Lerp(float a, float b, float k) => a + (b - a) * k;

    public override void _Draw()
    {
        float s = Side;
        var c = new Vector2(s / 2, s / 2);
        // the minimap: sea disc (36 segments) and the ships
        var pts = new Vector2[36];      // no closing duplicate of the first point and no zero radius: both make canvas_item_add_polygon log "triangulation failed" every frame
        for (int i = 0; i < 36; i++) { float a = i * 2 * MathF.PI / 36; pts[i] = c + new Vector2(MathF.Sin(a), MathF.Cos(a)) * MapRadius * U; }
        if (MapRadius * U > 0.5f) DrawColoredPolygon(pts, SeaColor);
        float mapR = MapRadius * U, k = mapR / (WorldRad * scale);
        foreach (var sh in ships)
        {
            float fx = sh.Dx * k, fy = sh.Dz * k;
            if (fx * fx + fy * fy > mapR * mapR) continue;
            float tmp = fx * MathF.Cos(heading) - fy * MathF.Sin(heading);
            fy = c.Y - (fy * MathF.Cos(heading) + fx * MathF.Sin(heading));
            fx = tmp + c.X;
            var col = sh.Dead ? Dead : sh.Relation == 2 ? Enemy : sh.Relation == 0 ? Friend : Neutral;
            float ang = sh.Ay - heading, r = ShipShowRadius * U;
            var t0 = new Vector2(fx + r * MathF.Sin(ang), fy - r * MathF.Cos(ang));
            var t1 = new Vector2(fx + r * MathF.Sin(ang - MathF.PI * .9f), fy - r * MathF.Cos(ang - MathF.PI * .8f));
            var t2 = new Vector2(fx + r * MathF.Sin(ang + MathF.PI * .9f), fy - r * MathF.Cos(ang + MathF.PI * .8f));
            // a degenerate triangle (zero radius before the first layout, NaN position) makes canvas_item_add_polygon log "triangulation failed" every frame
            float area = (t1.X - t0.X) * (t2.Y - t0.Y) - (t2.X - t0.X) * (t1.Y - t0.Y);
            if (!float.IsFinite(area) || MathF.Abs(area) < 0.01f) continue;
            DrawColoredPolygon(new[] { t0, t1, t2 }, col);
        }
        // the ring with the ship in the middle, then the compass card
        var empty = GameAssets.Texture(Dir + "indicators_dark_and_center_ship.tga");
        if (empty != null) DrawTextureRect(empty, new Rect2(0, 0, s, s), false);
        Rotated(GameAssets.Texture(Dir + "compass.tga"), c, s, s, -heading);
        // gun charge of the four sides: loaded arcs are green, the arc grows in red while loading, destroyed guns are grey
        var cannons = GameAssets.Texture(Dir + "indicators_cannons_reload.tga");
        void Side4(int i, float beg, float end, bool reversed)
        {
            if (!hasGuns[i]) return;
            float curDmg = Lerp(end * D2R, beg * D2R, damage[i]);
            float curChg = Lerp(beg * D2R, curDmg, Math.Clamp(charge[i], 0f, 1f));
            bool done = MathF.Abs(curChg - curDmg) < 1e-4f;
            var col = done ? Ready : Charging;
            if (!reversed) Sector(cannons, col, c, s, beg * D2R, curChg); else Sector(cannons, col, c, s, curChg, beg * D2R);
            if (MathF.Abs(curDmg - end * D2R) > 1e-4f)
            {
                if (!reversed) Sector(cannons, Damaged, c, s, curDmg, end * D2R); else Sector(cannons, Damaged, c, s, end * D2R, curDmg);
            }
        }
        Side4(3, LeftBeg, LeftEnd, false);
        Side4(1, RightBeg, RightEnd, true);
        Side4(0, FwdBeg, FwdEnd, false);
        Side4(2, BackBeg, BackEnd, true);
        // wind speed and ship speed arcs
        var speedTex = GameAssets.Texture(Dir + "indicators_wind_and_ship.tga");
        float wnd = windSpeed > MaxWind ? WindSpeedEnd : Lerp(WindSpeedBeg, WindSpeedEnd, windSpeed / MaxWind);
        float shp = shipSpeed > MaxShipSpeed ? ShipSpeedEnd : Lerp(ShipSpeedBeg, ShipSpeedEnd, Math.Max(0f, shipSpeed) / MaxShipSpeed);
        Sector(speedTex, Colors.White, c, s, WindSpeedBeg * D2R, wnd * D2R);
        Sector(speedTex, Colors.White, c, s, shp * D2R, ShipSpeedBeg * D2R);
        // wind pointer
        Rotated(GameAssets.Texture(Dir + "wind_pointer.tga"), c, WindW * U, WindH * U, windAngle - heading);
        // the current charge picture (small_cannons.tga: 1 column, 4 rows) at chargePos from the top left corner
        if (chargePic >= 0)
        {
            var tex = GameAssets.Texture(Dir + "small_cannons.tga");
            if (tex != null)
            {
                float blink = notEnough ? ((Time.GetTicksMsec() % 1000) < 500 ? 1f : 0.2f) : 1f;
                var at = new Vector2(160 * U, 174 * U);
                var sz = new Vector2(32 * U, 32 * U);
                var ts = tex.GetSize();
                DrawTextureRectRegion(tex, new Rect2(at - sz / 2, sz), new Rect2(0, ts.Y / 4f * Math.Clamp(chargePic, 0, 3), ts.X, ts.Y / 4f), new Color(1, notEnough ? blink : 1, notEnough ? blink : 1, 1));
            }
        }
    }
}
