using System;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The wave field of the sea: a sum of N travelling sine waves taken from Sea.Harmonics (the script's weather sets them) capped by Sea.MaxSeaHeight.
/// The same function displaces the sea mesh in the shader and lifts, pitches and rolls the ships, so a ship rides the water it floats on.
/// Coordinates are Godot world coordinates (x, z = -engine z).
/// </summary>
public static class SeaWaves
{
    public const int N = 6;
    public static float Time;
    public static readonly float[] K = { 0.14f, 0.12f, 0.18f, 0.5f, 0.7f, 0.9f }, W = new float[N], A = { 0.1f, 0.1f, 0.05f, 0, 0, 0 };
    public static readonly Vector2[] Dir = { new(0.72f, -0.69f), new(0.72f, 0.69f), new(0.3f, 0.95f), new(1f, 0f), new(0f, 1f), new(0.7f, 0.7f) };
    public static float MaxAmp, TotalAmp;      // the cap given by Sea.MaxSeaHeight and the resulting sum of the amplitudes (the crest height above the mean level)

    static SeaWaves() { for (int i = 0; i < N; i++) W[i] = MathF.Sqrt(9.81f * K[i]); }

    /// <summary>
    /// Reads the harmonics of the script's sea entity (called each frame, cheap). The weather script (Weather\Init\*.c) writes Sea.Harmonics.h1..h10 =
    /// "phase, frequency, amplitude, speed, direction in degrees"; the NOISIEST N harmonics (largest amplitude) are used: the wave length is 450 / frequency m,
    /// the amplitude is half the script's value, and the sum of the amplitudes is capped by half of Sea.MaxSeaHeight (3 m at sea, 1.2 m on a deck, like the original
    /// sea.cpp that scales the amplitudes down to MaxSeaHeight). A storm (h2, h3 = 2.1..2.2) therefore gives 1.5 m crests, a calm day 0.1..0.5 m.
    /// </summary>
    public static void Configure(Attr? sea)
    {
        if (sea == null) return;
        float cap = Conv.ParseFloatPrefix(sea.Find("MaxSeaHeight", false)?.Value);
        if (cap <= 0f) cap = 3f;
        var list = new System.Collections.Generic.List<(float amp, float len, float ang)>();
        for (int i = 0; i < 16; i++)
        {
            var hv = sea.Find("Harmonics.h" + i, false)?.Value;
            if (hv == null) continue;
            var p = hv.Split(',');
            if (p.Length < 5) continue;
            float f(int j) => float.TryParse(p[j].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
            float fr = Math.Max(f(1), 1f);
            list.Add((f(2) * 0.5f, Math.Clamp(450f / fr, 6f, 120f), f(4) * MathF.PI / 180f));
        }
        if (list.Count == 0) return;
        list.Sort((x, y) => y.amp.CompareTo(x.amp));
        float total = 0f;
        for (int i = 0; i < N && i < list.Count; i++) total += list[i].amp;
        MaxAmp = cap * 0.5f;
        float scale = total > MaxAmp ? MaxAmp / total : 1f;
        float old = TotalAmp; TotalAmp = total * scale;
        if (MathF.Abs(old - TotalAmp) > 0.01f) GD.Print($"[waves] {list.Count} harmonics, cap {cap}, crest amplitude {TotalAmp:0.00} m, wave lengths {string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(list, N), l => l.len.ToString("0")))}");
        for (int i = 0; i < N; i++)
        {
            if (i >= list.Count) { A[i] = 0f; continue; }
            var (amp, len, ang) = list[i];
            K[i] = MathF.Tau / len; W[i] = MathF.Sqrt(9.81f * K[i]);
            A[i] = amp * scale;
            Dir[i] = new Vector2(MathF.Sin(ang), -MathF.Cos(ang));      // engine (sin, cos) with z flipped: Godot z = -engine z
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
