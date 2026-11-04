using Godot;

namespace SeaDogs;

/// <summary>
/// SCROLLER: vertical scroll bar of the long lists (quest book, help ...). The script sets the roller position (0..1) with MSG_INTERFACE_SET_SCROLLER;
/// dragging with the mouse is not wired to the script yet, only drawn.
/// </summary>
public partial class ScrollerView : Control
{
    readonly TextureRect roller;
    readonly float rollerH;

    public ScrollerView(float x1, float y1, float x2, float y2, string baseTex, string rollerTex, float[] scrollUv, float[] rollUv, float rollerHeight)
    {
        Position = new Vector2(x1, y1); Size = new Vector2(x2 - x1, y2 - y1);
        MouseFilter = MouseFilterEnum.Ignore;
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

    public void SetPos(float p)
    {
        p = Mathf.Clamp(p, 0, 1);
        roller.Position = new Vector2(0, p * (Size.Y - rollerH));
    }
}
