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
    /// <summary>settings.ini [game] WaveScale: the script's wave heights are used at half size; this factor (default 1.35) makes the swell a little bigger for looks, the cap of Sea.MaxSeaHeight still applies.</summary>
    public static float WaveScale = 1.35f;
    /// <summary>Land locations (ports seen from the dock): the swell is a little bigger for looks and two short wind-chop waves (normal-only, below the mesh resolution) fill the last slots.</summary>
    public static bool Land;
    public static float MaxAmp, TotalAmp;      // the cap given by Sea.MaxSeaHeight and the resulting sum of the amplitudes (the crest height above the mean level)

    // The script's harmonics (what Configure reads) are only the TARGET: the waves in use glide to it (Tau seconds) so a weather change never pops the swell, and every wave keeps its own
    // phase accumulator (Phase += W dt) so a changing wavelength / speed does not jump the water either. All consumers (shaders, ship rocking) use Phase, not W * Time.
    static readonly float[] tK = new float[N], tA = new float[N]; static readonly Vector2[] tDir = new Vector2[N];
    public static readonly float[] Phase = new float[N];
    static float tTotal, lastTarget = -1f; static bool inited, lastLand;
    /// <summary>Time constant (s) of the glide towards the script's harmonics; WeatherFx raises it for the length of a weather transition.</summary>
    public static float Tau = 2f;
    /// <summary>The next Configure takes the script's values at once (sea created, land / sea switch).</summary>
    public static void Snap() { inited = false; }

    static SeaWaves() { for (int i = 0; i < N; i++) { tK[i] = K[i]; tA[i] = A[i]; tDir[i] = Dir[i]; W[i] = MathF.Sqrt(9.81f * K[i]); } }

    /// <summary>
    /// Reads the harmonics of the script's sea entity (called each frame, cheap). The weather script (Weather\Init\*.c) writes Sea.Harmonics.h1..h10 =
    /// "phase, frequency, amplitude, speed, direction in degrees"; the NOISIEST N harmonics (largest amplitude) are used: the wave length is 450 / frequency m,
    /// the amplitude is half the script's value, and the sum of the amplitudes is capped by half of Sea.MaxSeaHeight (3 m at sea, 1.2 m on a deck, like the original
    /// sea.cpp that scales the amplitudes down to MaxSeaHeight). A storm (h2, h3 = 2.1..2.2) therefore gives 1.5 m crests, a calm day 0.1..0.5 m.
    /// </summary>
    public static void Configure(Attr? sea, float dt)
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
            list.Add((f(2) * 0.5f * WaveScale, Math.Clamp(450f / fr, 6f, 120f), f(4) * MathF.PI / 180f));
        }
        if (list.Count == 0) return;
        list.Sort((x, y) => y.amp.CompareTo(x.amp));
        float total = 0f;
        for (int i = 0; i < N && i < list.Count; i++) total += list[i].amp;
        MaxAmp = cap * 0.5f;
        float scale = total > MaxAmp ? MaxAmp / total : 1f;
        tTotal = total * scale;
        for (int i = 0; i < N; i++)
        {
            if (i >= list.Count) { tA[i] = 0f; continue; }
            var (amp, len, ang) = list[i];
            tK[i] = MathF.Tau / len;
            tA[i] = amp * scale * (Land ? 1.4f : 1f);
            tDir[i] = new Vector2(MathF.Sin(ang), -MathF.Cos(ang));      // engine (sin, cos) with z flipped: Godot z = -engine z
        }
        if (Land)
        {
            float swell = 0f; for (int i = 0; i < N - 2; i++) swell += tA[i];
            float chop = 0.025f + 0.07f * Math.Min(swell, 1.2f);
            for (int j = 0; j < 2; j++)
            {
                int i = N - 2 + j; float len = j == 0 ? 7.5f : 12f;
                var d0 = tDir[0]; float a = (j == 0 ? 0.45f : -0.5f);
                tDir[i] = new Vector2(d0.X * MathF.Cos(a) - d0.Y * MathF.Sin(a), d0.X * MathF.Sin(a) + d0.Y * MathF.Cos(a));
                tK[i] = MathF.Tau / len; tA[i] = chop * (j == 0 ? 0.7f : 1f);
            }
        }
        tTotal = TotalAmpOf(tA);
        if (MathF.Abs(tTotal - lastTarget) > 0.01f) { lastTarget = tTotal; GD.Print($"[waves] {list.Count} harmonics, cap {cap}, crest amplitude target {tTotal:0.00} m, wave lengths {string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(list, N), l => l.len.ToString("0")))} tau {Tau:0}"); }
        if (Land != lastLand) { lastLand = Land; inited = false; }
        if (!inited)
        {
            inited = true; Array.Copy(tK, K, N); Array.Copy(tA, A, N); Array.Copy(tDir, Dir, N); TotalAmp = tTotal;
            for (int i = 0; i < N; i++) { W[i] = MathF.Sqrt(9.81f * K[i]); Phase[i] = W[i] * Time; }
            return;
        }
        float g = 1f - MathF.Exp(-Math.Max(dt, 0f) / Math.Max(Tau, 0.05f));
        for (int i = 0; i < N; i++)
        {
            K[i] += (tK[i] - K[i]) * g; A[i] += (tA[i] - A[i]) * g;
            var d = Dir[i].Lerp(tDir[i], g); Dir[i] = d.LengthSquared() < 1e-4f ? tDir[i] : d.Normalized();
            W[i] = MathF.Sqrt(9.81f * K[i]);
            Phase[i] = (Phase[i] + W[i] * Math.Max(dt, 0f)) % (MathF.Tau * 4096f);
        }
        TotalAmp = TotalAmpOf(A);
    }
    static float TotalAmpOf(float[] a) { float s = 0f; for (int i = 0; i < N; i++) s += a[i]; return s; }

    public static float Height(float x, float z)
    {
        float h = 0f;
        for (int i = 0; i < N; i++) h += A[i] * MathF.Sin(K[i] * (Dir[i].X * x + Dir[i].Y * z) - Phase[i]);
        return h;
    }

    /// <summary>d height / dx, d height / dz.</summary>
    public static Vector2 Slope(float x, float z)
    {
        var s = Vector2.Zero;
        for (int i = 0; i < N; i++)
        {
            float c = A[i] * K[i] * MathF.Cos(K[i] * (Dir[i].X * x + Dir[i].Y * z) - Phase[i]);
            s += new Vector2(c * Dir[i].X, c * Dir[i].Y);
        }
        return s;
    }
}
