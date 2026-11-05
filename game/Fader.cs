using System;
using Godot;

namespace SeaDogs;

/// <summary>
/// The engine's "fader": a full-screen picture. FADER_OUT fades it in (opaque at the end -> FaderEvent_StartFade),
/// FADER_IN fades it away (-> FaderEvent_StartFadeIn when it begins, FaderEvent_EndFadeIn at its end; event data (fadeIn flag, fader)). With autodelete the entity is
/// removed at the end. FADER_STARTFRAME freezes the current game frame under the picture while it fades (fader.cpp haveFrame).
/// </summary>
public partial class FaderView : TextureRect
{
    enum Mode { None, Out, Hold, In }
    Mode mode = Mode.None;
    float t, dur;
    bool autoDelete;
    readonly Action<string, int> fire;
    readonly Action deleteEntity;

    public FaderView(Action<string, int> fire, Action deleteEntity)
    {
        this.fire = fire;
        this.deleteEntity = deleteEntity;
        Name = "Fader";
        ExpandMode = ExpandModeEnum.IgnoreSize;
        StretchMode = StretchModeEnum.Scale;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        ZIndex = 100;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    TextureRect? snap;
    bool startInPending;

    /// <summary>FADER_STARTFRAME: the screenshot of the current frame is shown full opaque behind the fading picture.</summary>
    public void CaptureFrame(Image? img)
    {
        snap?.QueueFree(); snap = null;
        if (img == null || GetParent() == null) return;
        snap = new TextureRect { Name = "FaderFrame", Texture = ImageTexture.CreateFromImage(img), ExpandMode = ExpandModeEnum.IgnoreSize, StretchMode = StretchModeEnum.Scale, ZIndex = 99, MouseFilter = MouseFilterEnum.Ignore };
        snap.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        GetParent().AddChild(snap);
    }

    public override void _ExitTree() { snap?.QueueFree(); snap = null; }

    public void Start(bool fadeIn, float seconds, bool autodelete)
    {
        if (fadeIn) { snap?.QueueFree(); snap = null; startInPending = true; }         // FADER_IN forgets the frame (haveFrame = false)
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
        if (startInPending) { startInPending = false; fire("FaderEvent_StartFadeIn", 1); if (mode == Mode.None) return; }
        if (mode == Mode.Hold)
        {
            // the screen is fully covered: the engine gives the script a moment (StartFade work done), then reports EndFade;
            // reload.c does the heavy loading in that handler and starts the fade-in itself
            if (--holdFrames > 0) return;
            mode = Mode.None;
            if (System.Environment.GetEnvironmentVariable("SEADOGS_FADERLOG") != null) GD.Print("[fader] EndFade (after hold)");
            fire("FaderEvent_EndFade", 0);
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
        if (System.Environment.GetEnvironmentVariable("SEADOGS_FADERLOG") != null) GD.Print("[fader] finished " + finished);
        if (finished == Mode.In) fire("FaderEvent_EndFadeIn", 1); else fire("FaderEvent_StartFade", 0);
        if (autoDelete && finished == Mode.In) { deleteEntity(); QueueFree(); }
    }
}
