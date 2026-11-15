using System;
using Godot;
using StormScript.Formats;

namespace SeaDogs;

/// <summary>
/// Comfort for the hero's fight on land (no new controls): a soft aim assist - standing still with the sword out, he eases round to the nearest enemy
/// in front of him (enemies only, a narrow cone, the ones that attack him first) - and a short input buffer for the next blow / the recoil step.
/// settings.ini [game]: AutoAim = 1|0, InputBuffer = 1|0.
/// </summary>
public partial class CharacterView
{
    public const float InputBufferSeconds = 0.25f;
    public static bool AutoAimOn = true, InputBufferOn = true;
    float recoilBuffered;
    ulong manualTurnAt;
    CharacterView? lockTarget;
    float lockLogT;

    /// <summary>settings.ini [game] AutoAim / InputBuffer (1 = on, default; 0 = off).</summary>
    public static void LoadComfort(IniFile.Section? gs)
    {
        bool On(string key, bool def) => gs?.Get(key)?.Trim() is { Length: > 0 } v ? !(v == "0" || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase)) : def;
        AutoAimOn = On("AutoAim", true);
        InputBufferOn = On("InputBuffer", true);
    }

    /// <summary>The player turned the hero by hand (mouse / A, D): the aim assist keeps off for a moment so it never fights him.</summary>
    void NoteManualTurn() { manualTurnAt = Time.GetTicksMsec(); lockTarget = null; }

    /// <summary>Who the hero eases round to: enemies in the front cone within 3.5 m, preferring the ones that strike at him, then those in a fight, then the most aligned.</summary>
    CharacterView? SoftLockTarget()
    {
        CharacterView? best = null;
        float bestScore = float.MinValue;
        float sx = MathF.Sin(ay), cz = MathF.Cos(ay);
        foreach (var c in All.Values)
        {
            if (ReferenceEquals(c, this) || !c.Placed || !c.IsAlive || !GroupsState.AreEnemies(Ent.Root, c.Ent.Root)) continue;
            float dx = c.x - x, dz = c.z - z, d2 = dx * dx + dz * dz;
            if (d2 > 3.5f * 3.5f || d2 < 1e-6f) continue;
            float d = MathF.Sqrt(d2), cs = (dx * sx + dz * cz) / d;
            if (cs < 0.7f) continue;                                              // a narrow cone (about 45 degrees each way): the player is looking at this one
            float score = cs - d / 14f + (c.atkTarget == this && c.atkHitIn >= 0f ? 0.5f : 0f) + (c.FightMode ? 0.15f : 0f) + (ReferenceEquals(c, lockTarget) ? 0.12f : 0f);     // the last term: no flicker between two similar targets
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    /// <summary>The hero idles with the sword drawn: turn slowly to the enemy in the cone.</summary>
    static readonly string[] LockTurn = (System.Environment.GetEnvironmentVariable("SEADOGS_LOCKTURN") ?? "").Split(':');       // test hook: SEADOGS_LOCKTURN=frame:degrees turns the idle hero off his heading at that frame

    void SoftLockOn(float dt)
    {
        if (IsPlayer && LockTurn.Length == 2 && int.TryParse(LockTurn[0], out var tf) && (int)Engine.GetFramesDrawn() == tf && float.TryParse(LockTurn[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var td)) { ay += td * MathF.PI / 180f; nay = ay; hasNay = false; }
        if (!AutoAimOn || !IsPlayer || !fightMode || !IsAlive || Stunned || Busy || Blocking) { lockTarget = null; return; }
        if (Time.GetTicksMsec() - manualTurnAt < 600) return;                     // he has just turned by hand
        var e = SoftLockTarget();
        if (e == null) { lockTarget = null; return; }
        float want = Angle(e.x - x, e.z - z);
        float err = TurnToward(ay, want, 10f) - ay;
        while (err > MathF.PI) err -= MathF.Tau;
        while (err < -MathF.PI) err += MathF.Tau;
        if (MathF.Abs(err) > 0.8f) { lockTarget = null; return; }                // too far off the heading
        if (MeleeLog && (!ReferenceEquals(lockTarget, e) || (lockLogT -= dt) <= 0f))
        {
            lockLogT = 0.2f;
            GD.Print(FormattableString.Invariant($"[lockon] f{Engine.GetFramesDrawn()} target {e.Id} err {err * 57.2958f:0.0} deg"));
        }
        lockTarget = e;
        float step = 3f * dt;
        if (MathF.Abs(err) <= step) { ay = want; if (MeleeLog && lockLogT > 0.19f) GD.Print("[lockon] f" + Engine.GetFramesDrawn() + " aligned"); }
        else ay += MathF.Sign(err) * step;
        nay = ay; hasNay = false;
    }

    /// <summary>A buffered blow is struck at whoever stands in reach when it is released (the target of the click may have moved or died meanwhile).</summary>
    CharacterView? BufferedTarget(CharacterView? clicked)
    {
        if (!IsPlayer) return clicked;
        var now = NearestInFront(2.64f);
        return now ?? (clicked != null && clicked.IsAlive && (clicked.x - x) * (clicked.x - x) + (clicked.z - z) * (clicked.z - z) < 2.64f * 2.64f ? clicked : null);
    }

    /// <summary>A step back asked for while the previous action ends within the buffer time is carried out right after it.</summary>
    bool BufferRecoil()
    {
        if (!InputBufferOn || !IsPlayer || !IsAlive || Stunned || atkCooldown > InputBufferSeconds) return false;
        recoilBuffered = InputBufferSeconds;
        return true;
    }

    void TickRecoilBuffer(float dt)
    {
        if (recoilBuffered <= 0f) return;
        recoilBuffered -= dt;
        if (atkCooldown <= 0f && atkHitIn < 0f && !Busy) { recoilBuffered = 0f; Recoil(); }
    }
}
