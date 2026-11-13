using System;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// EDITBOX node (ship.ini RENAMEBOX): a text field whose text lives in the script attribute GameInterface.&lt;node&gt;.strdata.
/// The original drew an on-screen alphabet grid (chrTexture, alphabet = "alphabet low register") picked with the arrows; the port types
/// with the real keyboard instead (any layout, so Ukrainian letters work). Enter raises "NodeOk", Escape "NodeCancel" (event data = node name),
/// the length is limited by GameInterface.&lt;node&gt;.maxlen or the ini's stringLength.
/// </summary>
public partial class EditBoxView : Control
{
    readonly Entity gi;
    readonly string node;
    readonly int iniMaxLen;
    readonly Label text;
    double blink;

    public EditBoxView(Entity gameInterface, string nodeName, float x1, float y1, float x2, float y2, int stringLength, int fontPx, Color box, Color bound)
    {
        gi = gameInterface;
        node = nodeName;
        iniMaxLen = stringLength > 0 ? stringLength : 20;
        Position = new Vector2(x1, y1);
        Size = new Vector2(x2 - x1, y2 - y1);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 15;        // above the script's strings (they are created after the nodes)
        Box = box; Bound = bound;
        // a one-line field at the top of the node's rectangle (the rest was the alphabet grid)
        text = new Label { Position = new Vector2(8, 8), Size = new Vector2(Size.X - 16, fontPx + 12), MouseFilter = MouseFilterEnum.Ignore, ClipText = true };
        text.AddThemeFontSizeOverride("font_size", fontPx);
        text.AddThemeColorOverride("font_color", Colors.White);
        text.AddThemeColorOverride("font_outline_color", Colors.Black);
        text.AddThemeConstantOverride("outline_size", 3);
        AddChild(text);
    }

    public Color Box, Bound;

    Attr? Data => gi.Root.Find(node + ".strdata", false);
    public string Text => Data?.Value ?? "";

    int MaxLen => gi.Root.Find(node + ".maxlen", false)?.Value is { Length: > 0 } m ? Math.Max(1, (int)Conv.ParseFloatPrefix(m)) : iniMaxLen;

    /// <summary>A key while the box has the input. Returns the event to raise ("NodeOk"/"NodeCancel") or null.</summary>
    public string? Key(InputEventKey k)
    {
        var a = gi.Root.Find(node + ".strdata", true)!;
        string s = a.Value ?? "";
        switch (k.Keycode)
        {
            case Godot.Key.Enter: case Godot.Key.KpEnter: return "NodeOk";
            case Godot.Key.Escape: return "NodeCancel";
            case Godot.Key.Backspace: if (s.Length > 0) a.Value = s.Substring(0, s.Length - 1); return null;
        }
        if (k.Unicode >= 32 && !k.CtrlPressed && !k.AltPressed && s.Length < MaxLen) a.Value = s + char.ConvertFromUtf32((int)k.Unicode);
        return null;
    }

    public override void _Process(double delta)
    {
        blink += delta;
        string t = Text + ((int)(blink * 2) % 2 == 0 ? "|" : "");
        if (text.Text != t) text.Text = t;
        QueueRedraw();
    }

    public override void _Draw()
    {
        // the whole node is the box (the original filled it with the alphabet grid); the text line at its top
        DrawRect(new Rect2(Vector2.Zero, Size), Box);
        DrawRect(new Rect2(Vector2.Zero, Size), Bound, false, 2f);
        DrawRect(new Rect2(4, 4, Size.X - 8, text.Size.Y + 8), Bound, false, 1.5f);
    }
}
