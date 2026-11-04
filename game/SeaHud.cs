using System;
using Godot;

namespace SeaDogs;

/// <summary>
/// Simplified sea HUD (the original "battle_interface" class draws a richer one): ship name, hull / sails / crew, speed, sail state, heading and a compass
/// with the wind arrow. Fed by SeaView from the script's attributes.
/// </summary>
public partial class SeaHud : CanvasLayer
{
    readonly Label text = new() { Name = "SeaHudText" };
    readonly Compass compass = new() { Name = "Compass" };
    readonly Label action = new() { Name = "SeaAction" };

    public SeaHud()
    {
        Layer = 5;
        var panel = new PanelContainer { Name = "SeaPanel", Position = new Vector2(12, 12) };
        var style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.45f), ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6 };
        panel.AddThemeStyleboxOverride("panel", style);
        text.AddThemeFontSizeOverride("font_size", 15);
        text.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.8f));
        panel.AddChild(text);
        AddChild(panel);
        compass.Position = new Vector2(12, 150);
        AddChild(compass);
        action.AddThemeFontSizeOverride("font_size", 24);
        action.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.4f));
        action.AddThemeColorOverride("font_outline_color", Colors.Black);
        action.AddThemeConstantOverride("outline_size", 6);
        action.AnchorLeft = 0.5f; action.AnchorRight = 0.5f; action.AnchorTop = 1f; action.AnchorBottom = 1f;
        action.OffsetLeft = -300; action.OffsetRight = 300; action.OffsetTop = -90; action.OffsetBottom = -50;
        action.HorizontalAlignment = HorizontalAlignment.Center;
        AddChild(action);
        Visible = false;
    }

    public void Set(bool on, string info, float headingRad, float windToRad, float windSpeed, string act)
    {
        Visible = on;
        if (!on) return;
        text.Text = info;
        compass.Heading = headingRad; compass.Wind = windToRad; compass.WindSpeed = windSpeed;
        compass.QueueRedraw();
        action.Text = act;
    }

    partial class Compass : Control
    {
        public float Heading, Wind, WindSpeed;
        public Compass() { CustomMinimumSize = new Vector2(110, 110); Size = new Vector2(110, 110); }

        public override void _Draw()
        {
            var c = Size / 2f;
            DrawCircle(c, 52, new Color(0, 0, 0, 0.45f));
            DrawArc(c, 52, 0, MathF.Tau, 48, new Color(1f, 0.95f, 0.8f, 0.8f), 1.5f);
            // the card rotates with the ship: the ship always points up, "N" moves
            Vector2 Dir(float a) => new(MathF.Sin(a - Heading), -MathF.Cos(a - Heading));
            var n = c + Dir(0) * 42;
            DrawString(ThemeDB.FallbackFont, n - new Vector2(5, -5), "N", HorizontalAlignment.Left, -1, 14, new Color(1, 0.4f, 0.3f));
            DrawLine(c, c + new Vector2(0, -30), new Color(0.9f, 0.9f, 0.9f), 3f);                  // the ship
            DrawColoredPolygon(new[] { c + new Vector2(0, -38), c + new Vector2(-6, -26), c + new Vector2(6, -26) }, new Color(0.9f, 0.9f, 0.9f));
            // wind: arrow towards where the wind blows, length by strength
            float len = Math.Clamp(10f + WindSpeed * 1.8f, 12f, 46f);
            var wd = Dir(Wind);
            var tip = c + wd * len;
            DrawLine(c, tip, new Color(0.4f, 0.8f, 1f), 3f);
            var side = new Vector2(-wd.Y, wd.X);
            DrawColoredPolygon(new[] { tip + wd * 7, tip - wd * 4 + side * 6, tip - wd * 4 - side * 6 }, new Color(0.4f, 0.8f, 1f));
        }
    }
}
