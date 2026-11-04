using System;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The wave field of the sea: a sum of three travelling sine waves taken from Sea.Harmonics.h0..h2 (the script's weather sets them) scaled by Sea.MaxSeaHeight.
/// The same function displaces the sea mesh in the shader and lifts, pitches and rolls the ships, so a ship rides the water it floats on.
/// Coordinates are Godot world coordinates (x, z = -engine z).
/// </summary>
public static class SeaWaves
{
    public const int N = 3;
    public static float Time;
    public static readonly float[] K = { 0.12f, 0.10f, 0.18f }, W = new float[N], A = { 0.2f, 0.25f, 0.12f };
    public static readonly Vector2[] Dir = { new(0.72f, -0.69f), new(0.72f, 0.69f), new(0.3f, 0.95f) };

    static SeaWaves() { for (int i = 0; i < N; i++) W[i] = MathF.Sqrt(9.81f * K[i]); }

    /// <summary>Reads the harmonics of the script's sea entity (called each frame, cheap).</summary>
    public static void Configure(Attr? sea)
    {
        if (sea == null) return;
        float h = Conv.ParseFloatPrefix(sea.Find("MaxSeaHeight", false)?.Value);
        if (h <= 0f) h = 1.5f;
        for (int i = 0; i < N; i++)
        {
            var hv = sea.Find("Harmonics.h" + i, false)?.Value;
            if (hv == null) continue;
            var p = hv.Split(',');
            if (p.Length < 5) continue;
            float f(int j) => float.TryParse(p[j].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
            float len = Math.Clamp(f(1) * 5f, 20f, 120f);
            K[i] = MathF.Tau / len; W[i] = MathF.Sqrt(9.81f * K[i]);
            A[i] = h * 0.12f * (0.6f + 0.5f * Math.Clamp(f(0), 0f, 3f));
            var d = new Vector2(f(3), f(4));
            if (d.LengthSquared() > 1e-4f) Dir[i] = d.Normalized();
        }
    }

    public static float Height(float x, float z)
    {
        float h = 0f;
        for (int i = 0; i < N; i++) h += A[i] * MathF.Sin(K[i] * (Dir[i].X * x + Dir[i].Y * z) - W[i] * Time);
        return h;
    }

    /// <summary>d height / dx, d height / dz.</summary>
    public static Vector2 Slope(float x, float z)
    {
        var s = Vector2.Zero;
        for (int i = 0; i < N; i++)
        {
            float c = A[i] * K[i] * MathF.Cos(K[i] * (Dir[i].X * x + Dir[i].Y * z) - W[i] * Time);
            s += new Vector2(c * Dir[i].X, c * Dir[i].Y);
        }
        return s;
    }
}
