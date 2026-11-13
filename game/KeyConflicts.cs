using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Conflict check of the key binding pages (Options -> Controls). The original (option_screen.c: DoMapToOtherKey / KeyAlreadyUsed) only refused a key that another control of the
/// SHOWN group already carried ("Button Already Used", fading string), looked at the shown group only and let the player bind the system keys (Esc, F1 ...) or the keys the port
/// reads itself. Here, before the key reaches the script (event evntKeyChoose): (1) system keys are refused, Esc cancels the choice; (2) a key carried by another control that shares
/// a key group with the changed one (the controls of one script group are active together: <c>curKeyGroupName</c> switches whole groups) is SWAPPED (the other control gets the
/// changed control's old key, through the script's own CI_CreateAndSetControls) with a notice; when the swap would itself make a clash, or the other control is fixed, the choice is
/// refused. Documented in docs/CONTROLS.md.
/// </summary>
public static class KeyConflicts
{
    public enum Result { Proceed, Handled }

    /// <summary>Keys no control may take (the keys of the global controls and of the port's own actions); Esc cancels the choice. B only on the world map page (map wait).</summary>
    static readonly Dictionary<int, string> Reserved = new()
    {
        [27] = "Esc", [112] = "F1", [113] = "F2", [114] = "F3", [115] = "F4", [116] = "F5", [117] = "F6", [118] = "F7", [119] = "F8", [120] = "F9", [121] = "F10", [122] = "F11", [123] = "F12",
        [19] = "Pause", [45] = "Insert", [82] = "R", [84] = "T", [107] = "Num +", [109] = "Num -",
    };

    static string? Pair(string n) => n switch { "ChrBlock" => "ChrBlock_Unlock", "ChrBlock_Unlock" => "ChrBlock", "TelescopeIn" => "TelescopeOut", "TelescopeOut" => "TelescopeIn", _ => null };
    static string Primary(string n) => n == "ChrBlock_Unlock" ? "ChrBlock" : n == "TelescopeOut" ? "TelescopeIn" : n;

    static Interp? lastIt;
    static int notice;
    static string noticeText = "";
    static int checkIn;

    static string Str(Interp it, string g) => it.Globals.TryGetValue(g, out var v) ? Conv.ToStr(v.Val) : "";

    /// <summary>Decides what happens with the key (index into objControlsState.key_codes) pressed in the key chooser. Proceed: fire evntKeyChoose as usual.</summary>
    public static Result Check(Interp it, KeyChooserView kc, int idx)
    {
        lastIt = it;
        if (!it.Globals.ContainsKey("g_sGroupName") || !it.Globals.ContainsKey("curCntrlIdx") || !it.Funcs.ContainsKey("GetControlNameFromIdx") || kc.Codes?.ChildAt(idx) is not { } keyAttr) return Result.Proceed;
        string page = Str(it, "g_sGroupName"), cur = Conv.ToStr(it.CallTop("GetControlNameFromIdx", V.Int(it.Globals["curCntrlIdx"].Val.I)));
        if (cur.Length == 0 || it.Globals["objControlsState"].Val.Attr is not { } st || st.Find("keygroups", false) is not { } kgs) return Result.Proceed;
        int vk = (int)Conv.ParseFloatPrefix(keyAttr.Value);
        string keyName = Pretty(keyAttr.Name);
        if (keyAttr.Find("stick", false) != null || vk > 255) return Result.Proceed;      // the script's own message

        if (vk == 27)
        {
            if (it.Funcs.ContainsKey("ReturnFromReassign")) it.CallTop("ReturnFromReassign");
            Say(it, "Esc: призначення скасовано", "cancel");
            return Result.Handled;
        }
        string? res = Reserved.GetValueOrDefault(vk);
        if (res == null && vk == 66 && page == "WorldMapControls") res = "B";
        if (res != null)
        {
            Say(it, $"Клавіша {keyName} зарезервована для системних функцій гри", "refuse reserved " + res);
            return Result.Handled;
        }

        // the groups the changed control belongs to, and the controls that share one with it and carry this key
        var groups = kgs.Children.Where(g => g.Find(cur, false) != null).ToList();
        var clash = new List<string>();
        foreach (var g in groups)
            foreach (var c in g.Children)
            {
                string n = c.Name;
                if (n == cur || n == Pair(cur) || Ctl.T.Bound(n) != vk) continue;
                string p = Primary(n);
                if (!clash.Contains(p)) clash.Add(p);
            }
        if (clash.Count == 0) return Result.Proceed;

        string other = clash[0], otherName = Title(it, other);
        int oldVk = Ctl.T.Bound(cur);
        if (clash.Count > 1 || oldVk <= 0 || oldVk > 255 || Reserved.ContainsKey(oldVk) || NotRemappable(kgs, other) || OldKeyClashes(kgs, other, cur, oldVk))
        {
            Say(it, $"Клавіша {keyName} вже використовується для «{otherName}». Оберіть іншу", $"refuse used {cur} -> {vk} by {string.Join(",", clash)}");
            return Result.Handled;
        }

        // swap: the other control (and its inverse twin) takes the old key; the script then gives the changed control the new one
        foreach (var n in new[] { other, Pair(other) })
        {
            if (n == null) continue;
            var ga = kgs.Children.FirstOrDefault(g => g.Find(n, false) != null);
            if (ga == null) continue;
            int state = n == other ? (int)Conv.ParseFloatPrefix(ga.Find(n + ".state", false)?.Value) : 4;
            it.CallTop("CI_CreateAndSetControls", V.Str(ga.Name), V.Str(n), V.Int(oldVk), V.Int(state), V.Int(1));
        }
        string oldName = Pretty(OldKeyName(kc, oldVk));
        Say(it, $"Клавіша {keyName} вже використовується для «{otherName}», призначення поміняно (тепер «{otherName}»: {oldName})", $"swap {cur} <-> {other} ({vk} <-> {oldVk})");
        return Result.Proceed;
    }

    static string OldKeyName(KeyChooserView kc, int vk) { int i = kc.IndexOf(vk); return i >= 0 ? kc.Codes!.ChildAt(i)!.Name : "?"; }

    static string Pretty(string n)
    {
        if (n.StartsWith("KEY_")) return n.Substring(4);
        if (!n.StartsWith("VK_")) return n;
        string s = n.Substring(3);
        return s switch { "LBUTTON" => "ліва кнопка миші", "RBUTTON" => "права кнопка миші", "MBUTTON" => "середня кнопка миші", _ => s.Length > 1 ? s[0] + s.Substring(1).ToLowerInvariant() : s };
    }

    static bool NotRemappable(Attr kgs, string control)
    {
        var a = kgs.Children.Select(g => g.Find(control, false)).FirstOrDefault(x => x != null);
        var r = a?.Find("remapping", false)?.Value;
        return r == "0" || r == "false";
    }

    /// <summary>The other control would take <paramref name="oldVk"/>, but a third control of one of its groups carries it already.</summary>
    static bool OldKeyClashes(Attr kgs, string other, string cur, int oldVk)
    {
        foreach (var g in kgs.Children.Where(g => g.Find(other, false) != null))
            foreach (var c in g.Children)
            {
                string n = c.Name;
                if (Primary(n) == other || Primary(n) == Primary(cur) || Ctl.T.Bound(n) != oldVk) continue;
                return true;
            }
        return false;
    }

    static string Title(Interp it, string control)
    {
        var lang = it.Language;
        if (lang == null) return control;
        int f = lang.Open("ControlsNames.txt");
        string s = lang.Convert(f, control);
        lang.Close(f);
        return string.IsNullOrWhiteSpace(s) ? control : s.Trim();
    }

    // ------------------------------------------------------------------ the notice (the script's own string mechanism: CreateString), fades out
    static void Say(Interp it, string text, string log)
    {
        GD.Print("[ctlconflict] " + log + " | " + text);
        noticeText = text; notice = 200; checkIn = 8;
        Draw(it, 255);
    }

    static void Draw(Interp it, int alpha)
    {
        if (!it.Funcs.ContainsKey("CreateString")) return;
        int col = unchecked((int)(((uint)alpha << 24) | 0xFFD060u));
        it.CallTop("CreateString", V.Int(1), V.Str("KeyConflict"), V.Str(noticeText), V.Str("interface_normal"), V.Int(col), V.Int(320), V.Int(258), V.Int(2), V.Float(1f));
    }

    /// <summary>Every frame: fade the notice out.</summary>
    public static void Tick()
    {
        if (checkIn > 0 && --checkIn == 0 && lastIt != null) { int d = Duplicates(lastIt, out var f); GD.Print($"[ctlconflict] duplicates={d} {f}"); }
        if (notice <= 0 || lastIt == null) return;
        notice--;
        if (notice == 0) { noticeText = ""; Draw(lastIt, 0); return; }
        if (notice < 51) Draw(lastIt, notice * 5);
    }

    /// <summary>Number of pairs of remappable controls that share a key group and a key (inverse twins excluded): 0 in a consistent table. Logged by the test scenario.</summary>
    public static int Duplicates(Interp it, out string first)
    {
        first = "";
        int dup = 0;
        if (!it.Globals.TryGetValue("objControlsState", out var s) || s.Val.Attr?.Find("keygroups", false) is not { } kgs) return 0;
        foreach (var g in kgs.Children)
        {
            var seen = new Dictionary<int, string>();
            foreach (var c in g.Children)
            {
                int b = Ctl.T.Bound(c.Name);
                var rm = c.Find("remapping", false)?.Value;
                if (b <= 0 || b > 255 || rm == "0" || rm == "false") continue;
                if (seen.TryGetValue(b, out var o) && Primary(o) != Primary(c.Name)) { dup++; if (first.Length == 0) first = $"{g.Name}: {o}/{c.Name} key {b}"; }
                else seen[b] = c.Name;
            }
        }
        return dup;
    }
}
