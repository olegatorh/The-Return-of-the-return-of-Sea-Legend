using System;
using System.Collections.Generic;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Chest traps. The script (ITEMS/itemLogic.c OpenBoxProcedure) rolls the trap on every use of a chest in a house
/// (GenQuest.God_hit_us, rand(100) >= 85 + fortune). The owner's rule: once a trap has hit the hero, the very next
/// attempt on the same chest opens it. The engine remembers the trapped chest and suppresses the roll for that one call.
/// </summary>
public static class LandTraps
{
    static string trappedBox = "";          // "location id:box locator" of the chest whose last use ended in a trap

    static Attr? Hero(Interp it)
        => it.Globals.TryGetValue("Characters", out var s) && s.Arr is { Length: > 0 } ? s.Val.Attr ?? s.Arr[0].Val.Attr : null;

    static string lastKey = "";
    static float lastHp;

    static float Hp(Attr? hero) => float.TryParse(hero?.Find("chr_ai.hp", false)?.Value ?? "0", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;

    /// <summary>
    /// Runs the BI_FastCommand "OpenBox" through <paramref name="fire"/>. The chest screen opens a little later and the trap hits 0.2 s later (a quest delay), so the
    /// outcome of an attempt is judged at the next use: the hero lost health since the last attempt on the same chest = it was a trap, this attempt skips the roll.
    /// </summary>
    public static void OpenBox(Interp it, Action fire)
    {
        var hero = Hero(it);
        var trap = hero?.Find("GenQuest.God_hit_us", false);
        string box = hero?.Get("boxname")?.Value ?? "";
        string loc = hero?.Find("location", false)?.Value ?? "";
        string key = loc + ":" + box;
        string saved = trap?.Value ?? "";
        bool armed = saved.Length > 0 && saved != "0";
        bool skip = armed && key == lastKey && Hp(hero) < lastHp - 0.01f;
        if (skip) { trap!.Value = "0"; GD.Print("[traps] " + key + ": the last attempt was a trap, this one opens the chest"); }
        lastKey = key;
        lastHp = Hp(hero);
        fire();
        if (skip) trap!.Value = saved;
        if (skip) lastKey = "";
    }
}
