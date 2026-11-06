using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The Godot side of the engine's control service (see <see cref="ControlTable"/>): reads the keyboard and the mouse for it, raises the script events
/// "Control Activation" / "Control Deactivation" (CoreImpl::ProcessControls) and translates between Windows virtual keys (the script's key codes) and Godot keys.
/// The bindings themselves are made by the script (CONTROLS/init_pc.c, the options pages) through CreateControl / MapControl.
/// </summary>
public static class Ctl
{
    public static ControlTable T = new();
    static Interp? it;

    /// <summary>Controls that the game window already carries out itself (and raises the event for) from its own input routing: the generic raise skips them.</summary>
    static readonly HashSet<string> RoutedByMain = new(StringComparer.OrdinalIgnoreCase)
    {
        "Quick_Save", "Quick_Load", "Interface", "MainMenu", "MainMenuDouble", "LICommandsActivate", "BOAL_UsePotion", "BI_MapEnter",
        "BI_ChargeCannonballs", "BI_ChargeGrapes", "BI_ChargeKnippels", "BI_ChargeBombs", "Sea_CameraSwitch", "ChrAction", "Ship_Fire",
    };

    /// <summary>The F9 / F10 / F11 debug controls of the original keys collide with the port's own free camera, land control style switch and full screen: the port's win.</summary>
    static readonly HashSet<string> PortKeysWin = new(StringComparer.OrdinalIgnoreCase) { "BOAL_Control", "BOAL_Control3", "BOAL_SetCamera" };

    public static void Attach(Interp interp)
    {
        it = interp;
        T = interp.Controls;
        T.IsKeyDown = KeyDown;
    }

    // ------------------------------------------------------------------ virtual keys <-> Godot keys
    static Dictionary<int, Key>? fromVk;

    static Dictionary<int, Key> VkTable()
    {
        if (fromVk != null) return fromVk;
        fromVk = new Dictionary<int, Key>();
        foreach (var k in Enum.GetValues<Key>())
        {
            int vk = KeyChooserView.Vk(k);
            if (vk > 0 && k != Key.KpEnter) fromVk.TryAdd(vk, k);
        }
        return fromVk;
    }

    /// <summary>The Godot key of a keyboard virtual key (Key.None for mouse buttons, axes and unknown codes).</summary>
    public static Key FromVk(int vk) => vk > 6 && VkTable().TryGetValue(vk, out var k) ? k : Key.None;

    public static int VkOf(MouseButton b) => b switch { MouseButton.Left => 1, MouseButton.Right => 2, MouseButton.Middle => 4, MouseButton.Xbutton1 => 5, MouseButton.Xbutton2 => 6, _ => -1 };

    public static bool KeyDown(int vk)
    {
        switch (vk)
        {
            case 1: return Input.IsMouseButtonPressed(MouseButton.Left);
            case 2: return Input.IsMouseButtonPressed(MouseButton.Right);
            case 4: return Input.IsMouseButtonPressed(MouseButton.Middle);
            case 5: return Input.IsMouseButtonPressed(MouseButton.Xbutton1);
            case 6: return Input.IsMouseButtonPressed(MouseButton.Xbutton2);
            case 13: return Input.IsPhysicalKeyPressed(Key.Enter) || Input.IsPhysicalKeyPressed(Key.KpEnter);
        }
        var k = FromVk(vk);
        return k != Key.None && Input.IsPhysicalKeyPressed(k);
    }

    /// <summary>Does this mouse button carry the control right now (the script's binding, VK_LBUTTON ... )?</summary>
    public static bool Is(MouseButton b, string control) => T.Bound(control) is var sc && sc > 0 && sc == VkOf(b);

    /// <summary>The keyboard key a control is bound to (Key.None: unbound or bound to a mouse button), or <paramref name="fallback"/> while the script has not built the table yet.</summary>
    public static Key KeyOf(string control, Key fallback)
    {
        var c = T.Get(control);
        if (c == null) return fallback;
        return c.SystemCode >= 0 ? FromVk(c.SystemCode) : Key.None;
    }

    /// <summary>The control is down (key or mouse button held), whatever the script's key group locks say. False for unknown controls.</summary>
    public static bool Down(string control) => T.DownIgnoringLocks(control);

    /// <summary>The control exists and the script has not locked it (LockControl: the block lock of the hard levels, the recoil lock ...).</summary>
    public static bool Allowed(string control) => T.Get(control) is not { } c || !(c.Locked || T.LockAll);

    // ------------------------------------------------------------------ the frame
    /// <summary>The mouse movement of this frame in pixels (only while the mouse is captured by the game).</summary>
    public static void AddMouse(float dx, float dy) => T.AddMouse((int)MathF.Round(dx), (int)MathF.Round(dy));

    /// <summary>Start of a frame: update the table and raise the control events the game window does not raise itself.</summary>
    static long frameNo;
    static readonly bool Log = System.Environment.GetEnvironmentVariable("SEADOGS_CTLLOG") != null;

    public static void Frame(Func<bool> raise)
    {
        T.Update();
        if (it == null) return;
        if (Log && ++frameNo % 100 == 0)
            GD.Print($"[controls] group={it.Globals.GetValueOrDefault("curKeyGroupName")?.Val.ToString()} locked: " + string.Join(",", T.All.Where(c => c.Locked && c.SystemCode >= 0).Select(c => c.Name)));
        foreach (var (name, on) in T.Changes())
        {
            if (!raise() || RoutedByMain.Contains(name) || PortKeysWin.Contains(name)) continue;
            it.FireEvent(on ? "Control Activation" : "Control Deactivation", new List<V> { V.Str(name) });
        }
    }

    // ------------------------------------------------------------------ the port's own controls
    /// <summary>
    /// The port's additions to the original table (modern WASD walking, camera on the arrow keys, ...) are real script controls (CI_CreateAndSetControls) in the groups of
    /// the original pages, so that they show up on the key binding page and are saved with the other options. Re-created when the script rebuilt the table.
    /// </summary>
    public static void EnsurePortControls(long frame)
    {
        if (it == null || frame % 20 != 7 || !it.Funcs.ContainsKey("CI_CreateAndSetControls")) return;
        if (!it.Globals.TryGetValue("objControlsState", out var st) || st.Val.Attr is not { } root) return;
        if (root.Find("map.controls.ICancel", false) == null) return;          // the table is not built yet
        foreach (var a in Keys.All)
        {
            if (a.Ctl == null || !a.Ctl.StartsWith("Port_")) continue;
            if (root.Find("map.controls." + a.Ctl, false) != null) continue;
            int vk = KeyChooserView.Vk(a.Default);
            string[] groups = a.Group.Split(',');
            it.CallTop("CI_CreateAndSetControls", V.Str(groups[0]), V.Str(a.Ctl), V.Int(vk), V.Int(0), V.Int(1));
            for (int g = 1; g < groups.Length; g++) it.CallTop("MapControlToGroup", V.Str(a.Ctl), V.Str(groups[g]));
        }
    }
}
