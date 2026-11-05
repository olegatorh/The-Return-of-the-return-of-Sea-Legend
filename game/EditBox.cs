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

/// <summary>
/// KEYCHOOSER node: invisible; while it has the input (LOCK_NODE / current node) every key press is reported to the script as
/// event "evntKeyChoose"(index of the key in the list the script gave with MSG_TO_NODE (objControlsState.key_codes), stick flag).
/// Used by the key binding pages and by the addon's text input (INTERFACE/key_hook.c: the ship rename on the ship screen).
/// </summary>
public partial class KeyChooserView : Control
{
    public Attr? Codes;

    public KeyChooserView() { MouseFilter = MouseFilterEnum.Ignore; }

    /// <summary>Windows virtual key code of a Godot key (the script's key_codes are VK_ numbers).</summary>
    public static int Vk(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return (int)k;              // 65..90
        if (k >= Key.Key0 && k <= Key.Key9) return (int)k;        // 48..57
        if (k >= Key.F1 && k <= Key.F12) return 112 + (int)(k - Key.F1);
        if (k >= Key.Kp0 && k <= Key.Kp9) return 96 + (int)(k - Key.Kp0);
        return k switch
        {
            Key.Backspace => 8, Key.Tab => 9, Key.Enter or Key.KpEnter => 13, Key.Shift => 16, Key.Ctrl => 17, Key.Alt => 18, Key.Pause => 19,
            Key.Capslock => 20, Key.Escape => 27, Key.Space => 32, Key.Pageup => 33, Key.Pagedown => 34, Key.End => 35, Key.Home => 36,
            Key.Left => 37, Key.Up => 38, Key.Right => 39, Key.Down => 40, Key.Insert => 45, Key.Delete => 46,
            Key.KpMultiply => 106, Key.KpAdd => 107, Key.KpSubtract => 109, Key.KpPeriod => 110, Key.KpDivide => 111,
            Key.Semicolon => 186, Key.Equal => 187, Key.Comma => 188, Key.Minus => 189, Key.Period => 190, Key.Slash => 191, Key.Quoteleft => 192,
            Key.Bracketleft => 219, Key.Backslash => 220, Key.Bracketright => 221, Key.Apostrophe => 222,
            _ => -1,
        };
    }

    /// <summary>Index of the key among the script's key codes (-1 = not in the list).</summary>
    public int IndexOf(int vk)
    {
        if (Codes == null || vk < 0) return -1;
        for (int i = 0; i < Codes.Count; i++)
            if ((int)Conv.ParseFloatPrefix(Codes.ChildAt(i)!.Value) == vk) return i;
        return -1;
    }
}
