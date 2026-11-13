using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Evening street lamps and torches. The original script lights the "models.night.lights" locator groups only when the weather record of the moment has
/// Lights = true (night records only) and only once, when the location loads. Here the town lights follow the clock: in an open-air location the lamp and torch
/// locators of the night list are lit from the late afternoon on, fade in over ~1.5 h and fade out at dawn, whatever was loaded. The lights are ordinary
/// <see cref="LocationLights"/> lights (corona + light on the characters) whose <c>Gain</c> is driven here.
/// </summary>
public static class LocationLamps
{
    public static float OnFrom = 17.0f, FullAt = 18.5f, FullUntil = 5.0f, OffAt = 6.5f;
    static readonly bool log = System.Environment.GetEnvironmentVariable("SEADOGS_LAMPLOG") != null;

    sealed class State { public readonly HashSet<string> Groups = new(StringComparer.OrdinalIgnoreCase); public readonly List<LocationLights.PointLight> Lights = new(); public bool Scanned; public float Cool; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LocationView, State> states = new();

    /// <summary>0 by day, 1 at night, a linear ramp between (hours of the game clock).</summary>
    public static float Level(float h)
    {
        if (h >= FullAt || h <= FullUntil) return 1f;
        if (h >= OnFrom) return (h - OnFrom) / (FullAt - OnFrom);
        if (h < OffAt) return (OffAt - h) / (OffAt - FullUntil);
        return 0f;
    }

    static bool Lamp(string type) => type.Equals("lamp", StringComparison.OrdinalIgnoreCase) || type.StartsWith("torchlight", StringComparison.OrdinalIgnoreCase);

    public static float Hour(Interp it)
        => it.Globals.TryGetValue("Environment", out var e) && e.Val.Attr is { } ea
           && float.TryParse(ea.Find("time", false)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : 12f;

    public static void Tick(LocationView loc, Interp it, float dt)
    {
        if (loc.Indoor) return;
        var st = states.GetOrCreateValue(loc);
        float lvl = Level(Hour(it));
        if (!st.Scanned)
        {
            if (lvl <= 0f) return;                       // nothing to light yet: scan when the evening comes (the locators exist from the load on)
            st.Scanned = true;
            Scan(loc, st);
        }
        foreach (var l in st.Lights) l.Gain = lvl;
    }

    static void Scan(LocationView loc, State st)
    {
        var night = loc.Ent.Root.Find("models.night.lights", false);
        if (night == null) return;
        for (int i = 0; i < night.Count; i++)
        {
            if (night.ChildAt(i) is not { } g) continue;
            string type = g.Value ?? "";
            if (!Lamp(type) || !st.Groups.Add(g.Name)) continue;
            bool ownLoaded = LocationLights.Lights.Exists(l => l.Owner == loc && l.Type.Name.Equals(type, StringComparison.OrdinalIgnoreCase));
            if (ownLoaded) continue;                     // the script lit them itself (night record): they stay as loaded
            foreach (var lab in loc.LocatorsOfGroup(g.Name))
            {
                if (!LocationLights.Add(loc, type, lab.M[12], lab.M[13], lab.M[14])) break;
                var pl = LocationLights.Lights[^1]; pl.Gain = 0f; st.Lights.Add(pl);
            }
            if (log) GD.Print($"[lamps] {loc.Ent.Root.Get("id")?.Value}: group {g.Name} -> {type} x{st.Lights.Count}");
        }
    }
}
