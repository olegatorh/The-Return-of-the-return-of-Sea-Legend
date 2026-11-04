using Godot;

namespace SeaDogs;

/// <summary>
/// The 2D interface of the original is laid out for a 4:3 canvas (720x540 with the 640x480 core at 40,30). The project stretches that canvas with
/// the "expand" aspect, so on a wide window the visible area is larger than 720x540: screens with a fixed layout are centred, backgrounds and
/// HUD parts anchor to the real edges.
/// </summary>
public static class UiLayout
{
    public const float BaseW = 720f, BaseH = 540f;

    /// <summary>Offset that centres the 720x540 base canvas inside the visible area.</summary>
    public static Vector2 Extra(Node n)
    {
        var vp = n.GetViewport();
        if (vp == null) return Vector2.Zero;
        var s = vp.GetVisibleRect().Size;
        return new Vector2(Mathf.Max(0f, (s.X - BaseW) / 2f), Mathf.Max(0f, (s.Y - BaseH) / 2f));
    }
}
