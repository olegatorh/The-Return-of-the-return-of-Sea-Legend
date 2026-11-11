using System;
using System.Globalization;
using System.Linq;

namespace StormScript.Runtime;

/// <summary>Godot-free helpers of the character logic (game/Characters.cs delegates here so they can be unit tested).</summary>
public static class CharacterMath
{
    /// <summary>Turn the heading <paramref name="cur"/> toward <paramref name="want"/> (radians) by at most <paramref name="maxStep"/>, the short way round.</summary>
    public static float TurnToward(float cur, float want, float maxStep)
    {
        float d = want - cur;
        while (d > MathF.PI) d -= 2 * MathF.PI;
        while (d < -MathF.PI) d += 2 * MathF.PI;
        return cur + Math.Clamp(d, -maxStep, maxStep);
    }

    /// <summary>Shorten the plan vector (ax, az) to length 1 when it is longer.</summary>
    public static void LimitLength(ref float ax, ref float az)
    {
        float l = ax * ax + az * az;
        if (l > 1f) { l = 1f / MathF.Sqrt(l); ax *= l; az *= l; }
    }

    static readonly string[] StepKinds = { "grass", "ground", "stone", "sand", "wood", "echo", "carpet", "stairway", "iron", "church", "water" };

    /// <summary>Step sound alias for a patch material: "snd_grass" / "add_snd_stone" / truncated "GrexitT_snd_gras" -> step_grass; others -> step_ground.</summary>
    public static string StepAlias(string material)
    {
        string m = material.ToLowerInvariant();
        int i = m.IndexOf("snd_");
        string k = i >= 0 ? m.Substring(i + 4) : m;
        if (k.StartsWith("srairway")) k = "stairway";                 // a typo in some patches
        if (k.Length >= 2)
            foreach (var s in StepKinds)
                if (s.StartsWith(k) || k.StartsWith(s)) return "step_" + s;
        return "step_ground";
    }

    /// <summary>Position and heading as the text kept in a save: "x,y,z,ay", round-trip precision, invariant culture.</summary>
    public static string FormatPosition(float x, float y, float z, float ay)
        => string.Join(",", new[] { x, y, z, ay }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

    /// <summary>Parse <see cref="FormatPosition"/> text; false when it has fewer than four parts.</summary>
    public static bool TryParsePosition(string text, out float x, out float y, out float z, out float ay)
    {
        x = y = z = ay = 0f;
        var p = text.Split(',');
        if (p.Length < 4) return false;
        float F(string t) => float.Parse(t, CultureInfo.InvariantCulture);
        x = F(p[0]); y = F(p[1]); z = F(p[2]); ay = F(p[3]);
        return true;
    }
}
