using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

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
