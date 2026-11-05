using Godot;

namespace SeaDogs;

/// <summary>
/// The engine's "WideScreen" class (location/wide_screen.cpp): the black cinema bars of the quest movies (QUESTS/quests_movie.c). Each bar grows to 10 % of the
/// screen height in one second after CreateEntity; any message to the entity (SendMessage(&amp;obj, "")) lets the bars retract and then deletes the entity.
/// </summary>
public partial class WideScreenView : Control
{
    readonly ColorRect top = new() { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
    readonly ColorRect bottom = new() { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
    float state, dlt = 1f;
    readonly System.Action onDone;

    public WideScreenView(System.Action onDone)
    {
        this.onDone = onDone;
        Name = "WideScreen";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        ZIndex = 90;
        AddChild(top); AddChild(bottom);
        Layout();
    }

    /// <summary>WideScreen::ProcessMessage: dlt = -1, the bars go away.</summary>
    public void Retract() => dlt = -1f;

    void Layout()
    {
        float h = Size.Y * state * 0.1f;
        top.Position = Vector2.Zero; top.Size = new Vector2(Size.X, h);
        bottom.Position = new Vector2(0, Size.Y - h); bottom.Size = new Vector2(Size.X, h);
    }

    public override void _Process(double delta)
    {
        state += dlt * (float)delta;
        if (state < 0f) { onDone(); return; }
        if (state > 1f) { state = 1f; dlt = 0f; }
        Layout();
    }
}
