using System;
using Godot;

namespace SeaDogs;

/// <summary>
/// The engine's "fader": a full-screen picture. FADER_OUT fades it in (opaque at the end -> FaderEvent_StartFade),
/// FADER_IN fades it away (-> FaderEvent_EndFade). With autodelete the entity is removed at the end.
/// </summary>
public partial class FaderView : TextureRect
{
    enum Mode { None, Out, Hold, In }
    Mode mode = Mode.None;
    float t, dur;
    bool autoDelete;
    readonly Action<string> fire;
    readonly Action deleteEntity;

    public FaderView(Action<string> fire, Action deleteEntity)
    {
        this.fire = fire;
        this.deleteEntity = deleteEntity;
        Name = "Fader";
        ExpandMode = ExpandModeEnum.IgnoreSize;
        StretchMode = StretchModeEnum.Scale;
        Size = new Vector2(720, 540);
        ZIndex = 100;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void Start(bool fadeIn, float seconds, bool autodelete)
    {
        mode = fadeIn ? Mode.In : Mode.Out;
        dur = Math.Max(seconds, 0.001f);
        t = 0;
        autoDelete = autodelete;
        Modulate = new Color(1, 1, 1, fadeIn ? 1 : 0);
    }

    int holdFrames;

    public override void _Process(double delta)
    {
        if (mode == Mode.None) return;
        if (mode == Mode.Hold)
        {
            // the screen is fully covered: the engine gives the script a moment (StartFade work done), then reports EndFade;
            // reload.c does the heavy loading in that handler and starts the fade-in itself
            if (--holdFrames > 0) return;
            mode = Mode.None;
            fire("FaderEvent_EndFade");
            if (autoDelete) { deleteEntity(); QueueFree(); }      // a fade-out with "delete" lives until its EndFade has been reported (sea -> map)
            return;
        }
        t += (float)delta;
        float k = Math.Clamp(t / dur, 0f, 1f);
        Modulate = new Color(1, 1, 1, mode == Mode.In ? 1 - k : k);
        if (k < 1f) return;
        var finished = mode;
        mode = finished == Mode.Out ? Mode.Hold : Mode.None;
        holdFrames = 2;
        fire(finished == Mode.In ? "FaderEvent_EndFade" : "FaderEvent_StartFade");
        if (autoDelete && finished == Mode.In) { deleteEntity(); QueueFree(); }
    }
}
