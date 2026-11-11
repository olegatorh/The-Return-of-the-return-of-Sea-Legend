using System;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// "Rest until morning / evening": the script (WaitDate -> WaitDatePostEventControl) silently moves the clock behind a fade. The player gets a line in the message log
/// saying that the next day has come (with the date) or how many hours have passed.
/// </summary>
public sealed class TimeSkipNotice
{
    readonly Interp it;
    bool pending;
    int day, month, year;
    float time;

    public TimeSkipNotice(Interp it)
    {
        this.it = it;
        it.WatchedCalls.Add("WaitDatePostEventControl");
        it.CallHook += n => { if (n.Equals("WaitDatePostEventControl", StringComparison.OrdinalIgnoreCase)) { Snapshot(out day, out month, out year, out time); pending = true; } };
    }

    void Snapshot(out int d, out int m, out int y, out float t)
    {
        d = m = y = 0; t = 0f;
        if (!it.Globals.TryGetValue("Environment", out var g) || g.Val.Attr is not { } env) return;
        int I(string p) => (int)Conv.ParseFloatPrefix(env.Find(p, false)?.Value ?? "0");
        d = I("date.day"); m = I("date.month"); y = I("date.year"); t = Conv.ParseFloatPrefix(env.Find("time", false)?.Value ?? "0");
    }

    /// <summary>Called every frame: the frame after the script has moved the clock, writes the message.</summary>
    public void Tick(LogHud? log)
    {
        if (!pending) return;
        pending = false;
        Snapshot(out int d, out int m, out int y, out float t);
        string text;
        if (d != day || m != month || y != year) text = $"Настав наступний день: {d:00}.{m:00}.{y}, {(int)t:00}:{(int)((t - (int)t) * 60):00}";
        else
        {
            int hours = (int)MathF.Round(t - time);
            if (hours <= 0) return;
            text = $"Минуло годин: {hours}. Зараз {(int)t:00}:{(int)((t - (int)t) * 60):00}";
        }
        GD.Print("[time] " + text);
        log?.Say(text);
    }
}
