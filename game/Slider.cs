using System;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// SLIDER: a horizontal value bar (volume, brightness, mouse sensitivity). The value lives in the script's attribute tree at
/// GameInterface.nodes.&lt;node name&gt;.value (0..1); the script reads it every frame and may set it.
/// </summary>
public partial class SliderView : Control
{
    readonly Entity gi;
    readonly string node;
    readonly Texture2D? baseTex, lineTex, pointerTex;
    readonly float pointerW, pointerH, edge;
    bool dragging;

    public SliderView(Entity gameInterface, string nodeName, float x1, float y1, float x2, float y2, string baseTexture, string selectTexture, string pointerTexture, float pw, float ph, float baseLeft)
    {
        gi = gameInterface; node = nodeName;
        Position = new Vector2(x1, y1);
        Size = new Vector2(x2 - x1, y2 - y1);
        baseTex = GameAssets.Texture(baseTexture);
        lineTex = GameAssets.Texture(selectTexture);
        pointerTex = GameAssets.Texture(pointerTexture);
        pointerW = pw > 0 ? pw : 32; pointerH = ph > 0 ? ph : 32; edge = baseLeft;
        MouseFilter = MouseFilterEnum.Stop;
        Name = nodeName;
    }

    Attr ValueAttr => gi.Root.GetOrCreate("nodes").GetOrCreate(node.ToLowerInvariant()).GetOrCreate("value");

    public float Value
    {
        get => Math.Clamp(Conv.ParseFloatPrefix(ValueAttr.Value), 0f, 1f);
        set => ValueAttr.Value = Conv.FloatToGcvt(Math.Clamp(value, 0f, 1f));
    }

    public void Step(float delta) => Value = Value + delta;

    float Track => Math.Max(1f, Size.X - 2 * Math.Min(edge, Size.X / 4));

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) { dragging = mb.Pressed; if (mb.Pressed) SetFromX(mb.Position.X); }
        else if (e is InputEventMouseMotion mm && dragging) SetFromX(mm.Position.X);
    }

    void SetFromX(float x)
    {
        float left = Math.Min(edge, Size.X / 4);
        Value = (x - left) / Track;
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        float h = Math.Min(Size.Y, 16);
        float y = (Size.Y - h) / 2;
        if (baseTex != null) DrawTextureRect(baseTex, new Rect2(0, y, Size.X, h), false);
        float left = Math.Min(edge, Size.X / 4);
        float v = Value;
        if (lineTex != null) DrawTextureRect(lineTex, new Rect2(left, y, Track * v, h), false);
        if (pointerTex != null) DrawTextureRect(pointerTex, new Rect2(left + Track * v - pointerW / 2, (Size.Y - pointerH) / 2, pointerW, pointerH), false);
    }
}
