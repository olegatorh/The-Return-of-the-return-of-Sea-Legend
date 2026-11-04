using System;
using Godot;

namespace SeaDogs;

/// <summary>
/// SCROLLBAR: "[<] ===== [>]" quantity control of the trade screens. It holds no value itself: the arrows (and the Left/Right keys while it is focused)
/// run the ini commands leftstep / rightstep (events decreaseTrade / increaseTrade); holding an arrow repeats them.
/// </summary>
public partial class ScrollBarView : Control
{
    readonly Action<string, string> run;
    readonly string node;
    string? holding;
    double held, next;

    public ScrollBarView(string name, float x1, float y1, float x2, float y2, string group, string left, string right, string center, Action<string, string> run)
    {
        node = name; this.run = run; Name = name;
        Position = new Vector2(x1, y1); Size = new Vector2(x2 - x1, y2 - y1);
        FocusMode = FocusModeEnum.All;
        var lt = Atlas.Get(group, left); var rt = Atlas.Get(group, right); var ct = Atlas.Get(group, center);
        float aw = Math.Min(Size.Y, 24);
        AddChild(new TextureRect { Texture = ct, Position = new Vector2(aw, 0), Size = new Vector2(Size.X - 2 * aw, Size.Y), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore });
        AddChild(Arrow(lt, new Vector2(0, 0), new Vector2(aw, Size.Y), "leftstep"));
        AddChild(Arrow(rt, new Vector2(Size.X - aw, 0), new Vector2(aw, Size.Y), "rightstep"));
    }

    TextureButton Arrow(Texture2D? t, Vector2 pos, Vector2 size, string cmd)
    {
        var b = new TextureButton { TextureNormal = t, Position = pos, Size = size, IgnoreTextureSize = true, StretchMode = TextureButton.StretchModeEnum.Scale, FocusMode = FocusModeEnum.None };
        b.ButtonDown += () => { holding = cmd; held = 0; next = 0.35; run(node, cmd); };
        b.ButtonUp += () => holding = null;
        return b;
    }

    public override void _Process(double delta)
    {
        if (holding == null) return;
        held += delta;
        if (held >= next) { run(node, holding); next += 0.08; }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true } k)
        {
            if (k.Keycode == Key.Left) { run(node, "leftstep"); AcceptEvent(); }
            else if (k.Keycode == Key.Right) { run(node, "rightstep"); AcceptEvent(); }
        }
        else if (e is InputEventMouseButton { Pressed: true }) GrabFocus();
    }
}
