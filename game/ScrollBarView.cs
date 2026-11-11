using System;
using Godot;

namespace SeaDogs;

/// <summary>
/// SCROLLBAR: "[<] ===== [>]" quantity control of the trade screens. It holds no value itself: the arrows (and the Left/Right keys while it is focused)
/// run the ini commands leftstep / rightstep (events decreaseTrade / increaseTrade); holding an arrow repeats them. As LRCHANGER (goods / crew transfer) a mouse click
/// on an arrow raises the event IEvnt_MouseClick (node, 0 = left / 1 = right) instead (xi_lr_changer.cpp), the keys still run the commands.
/// </summary>
public partial class ScrollBarView : Control
{
    readonly Action<string, string> run;
    readonly string node;
    readonly Action<string, int>? mouseClick;
    string? holding;
    double held, next;

    public ScrollBarView(string name, float x1, float y1, float x2, float y2, string group, string left, string right, string center, Action<string, string> run, Action<string, int>? mouseClick = null, float sideWidth = -1)
    {
        node = name; this.run = run; this.mouseClick = mouseClick; Name = name;
        Position = new Vector2(x1, y1); Size = new Vector2(x2 - x1, y2 - y1);
        FocusMode = FocusModeEnum.All;
        var lt = Atlas.Get(group, left); var rt = Atlas.Get(group, right); var ct = center == "" ? null : Atlas.Get(group, center);
        float aw = sideWidth >= 0 ? Math.Min(sideWidth, Size.X / 2) : Math.Min(Size.Y, 24);        // xi_scrollbar.cpp: sideWidth, or (width - barWidth) / 2, or the height
        AddChild(new TextureRect { Texture = ct, Position = new Vector2(aw, 0), Size = new Vector2(Size.X - 2 * aw, Size.Y), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore });
        AddChild(Arrow(lt, new Vector2(0, 0), new Vector2(aw, Size.Y), "leftstep"));
        AddChild(Arrow(rt, new Vector2(Size.X - aw, 0), new Vector2(aw, Size.Y), "rightstep"));
    }

    TextureButton Arrow(Texture2D? t, Vector2 pos, Vector2 size, string cmd)
    {
        var b = new TextureButton { TextureNormal = t, Position = pos, Size = size, IgnoreTextureSize = true, StretchMode = TextureButton.StretchModeEnum.Scale, FocusMode = FocusModeEnum.None };
        b.ButtonDown += () =>
        {
            Fire(cmd);
            // xi_scrollbar.cpp: holding the mouse button only repeats the bar's own ChangeValue (no ini command, no script event), so a SCROLLBAR (the quantity of the store screens) changes once per click;
            // the key repeat (Left / Right held: every frame after RepeatDelay) is what runs the script's decreaseTrade / increaseTrade fast. The goods / crew changer repeats its click.
            if (mouseClick != null) { holding = cmd; held = 0; next = 0.35; }
        };
        b.ButtonUp += () => holding = null;
        return b;
    }

    void Fire(string cmd)
    {
        if (mouseClick != null) mouseClick(node, cmd == "leftstep" ? 0 : 1); else run(node, cmd);
    }

    public override void _Process(double delta)
    {
        if (holding == null) return;
        held += delta;
        if (held >= next) { Fire(holding); next += 0.08; }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true } k)
        {
            if (k.Keycode == Key.Left) { run(node, k.ShiftPressed ? "speedleft" : "leftstep"); AcceptEvent(); }       // Shift = the "speed" action (itemstrade: +-10)
            else if (k.Keycode == Key.Right) { run(node, k.ShiftPressed ? "speedright" : "rightstep"); AcceptEvent(); }
        }
        else if (e is InputEventMouseButton { Pressed: true }) GrabFocus();
    }
}
