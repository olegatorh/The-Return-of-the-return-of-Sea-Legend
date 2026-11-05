using System;
using Godot;

namespace SeaDogs;

/// <summary>
/// SCROLLER: vertical scroll bar of the long lists (quest book, help ...). The script sets the roller position (0..1) with MSG_INTERFACE_SET_SCROLLER;
/// dragging the roller / clicking the track raises the event ScrollPosChange (position, node name), the mouse wheel over it ScrollTopChange (-1 / +1)
/// (xi_scroller.cpp); the script moves the lists it owns.
/// </summary>
public partial class ScrollerView : Control
{
    readonly TextureRect roller;
    readonly float rollerH;
    readonly string node;
    readonly Action<string, float>? changed;
    readonly Action<int>? stepped;
    bool dragging;

    public ScrollerView(float x1, float y1, float x2, float y2, string baseTex, string rollerTex, float[] scrollUv, float[] rollUv, float rollerHeight,
        string name = "", Action<string, float>? changed = null, Action<int>? stepped = null)
    {
        Position = new Vector2(x1, y1); Size = new Vector2(x2 - x1, y2 - y1);
        node = name; this.changed = changed; this.stepped = stepped; Name = name.Length > 0 ? name : "Scroller";
        MouseFilter = changed != null ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        rollerH = rollerHeight;
        AddChild(new TextureRect { Texture = Region(baseTex, scrollUv), Size = Size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore });
        roller = new TextureRect { Texture = Region(rollerTex, rollUv), Size = new Vector2(Size.X, rollerH), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(roller);
        SetPos(0);
    }

    static Texture2D? Region(string file, float[] uv)
    {
        var tex = GameAssets.Texture(file);
        if (tex == null) return null;
        float w = tex.GetWidth(), h = tex.GetHeight();
        return new AtlasTexture { Atlas = tex, Region = new Rect2(uv[0] * w, uv[1] * h, (uv[2] - uv[0]) * w, (uv[3] - uv[1]) * h) };
    }

    public override void _GuiInput(InputEvent e)
    {
        if (changed == null) return;
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) { dragging = mb.Pressed; if (mb.Pressed) Drag(mb.Position.Y); AcceptEvent(); }
        else if (e is InputEventMouseMotion mm && dragging) { Drag(mm.Position.Y); AcceptEvent(); }
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } w) { stepped?.Invoke(w.ButtonIndex == MouseButton.WheelUp ? -1 : 1); AcceptEvent(); }
    }

    void Drag(float y)
    {
        float span = Size.Y - rollerH;
        if (span <= 0) return;
        float p = Mathf.Clamp((y - rollerH * 0.5f) / span, 0f, 1f);
        SetPos(p);
        changed?.Invoke(node, p);
    }

    public void SetPos(float p)
    {
        p = Mathf.Clamp(p, 0, 1);
        roller.Position = new Vector2(0, p * (Size.Y - rollerH));
    }
}
