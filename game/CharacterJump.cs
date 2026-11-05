using System;
using Godot;
using StormScript.Formats;

namespace SeaDogs;

/// <summary>
/// Jumping off ledges and swimming (location/src/character.cpp: TestJump / BuildJump / the sea part of Move). The engine has no fall damage: the hero walks
/// to a patch border where the location's jump patch lies, flies along a ballistic track to the lower floor and lands (sounds "recoil", "clothes", "jump");
/// where the sea is deeper than 1.1 m the characters swim (animation "swim", they cannot run in water above the ankles).
/// </summary>
public partial class CharacterView
{
    /// <summary>The location's jump patch (Location::JmpPatch), the segment test of the location's models (Direct3D coordinates) and the sea height (Godot world height) under a point.</summary>
    public static Func<TriSoup?>? JumpPatchOf;
    public static Func<Vector3, Vector3, bool>? Obstacle;
    public static Func<float, float, float?>? SeaLevelOf;
    public static int StatJumps, StatJumpsExplicit, StatSwimStarts;
    static readonly bool JumpLog = System.Environment.GetEnvironmentVariable("SEADOGS_JUMPLOG") != null;

    const int MaxJumpPoints = 50;
    const float JumpMaxTime = 5f, JumpStep = JumpMaxTime / MaxJumpPoints, JumpSpeed = 3.3f;
    const float SeaNoRun = 0.2f, SeaMove = 0.6f, SeaSwim = 0.8f;      // character.cpp CHARACTER_SEA_*: metres of water / fractions of the height

    /// <summary>Player::Move sets it while the hero has no scripted task (isEnableJump).</summary>
    public bool EnableJump;
    bool isJump, jumpSnd;
    int jumpPoints;
    float jumpFallTime, curJumpFall;
    readonly Vector3[] jumpTrack = new Vector3[MaxJumpPoints];
    Vector3 osculation;
    bool isSwim, runDisable;
    float seaY, swimChange;

    public bool IsJumping => isJump;

    /// <summary>Test hook (--at=x,y,z,ay): stand at a point of the walk patch (engine coordinates).</summary>
    public void TeleportTo(float px, float py, float pz, float heading) { x = px; y = py; z = pz; ay = heading; SnapToPatch(); Apply(); }
    public bool IsSwimming => isSwim;

    float TraceWalk(Vector3 s, Vector3 d) => getPatch() is { } p ? p.Trace(s.X, s.Y, s.Z, d.X, d.Y, d.Z) : 2f;

    /// <summary>Debug (SEADOGS_JUMPLOG): the border edges of the walk patch over the jump patch that would send the hero flying, with where he lands.</summary>
    public static void ScanLedges(PtcFile patch, TriSoup jp)
    {
        int edges = 0, ledges = 0, quick = 0;
        foreach (var t in patch.Tris)
            for (int j = 0; j < 3; j++)
            {
                int nb = j == 0 ? t.N0 : j == 1 ? t.N1 : t.N2;
                if (nb >= 0) continue;
                int sIdx = j == 0 ? t.A : j == 1 ? t.B : t.C, eIdx = j == 0 ? t.B : j == 1 ? t.C : t.A;
                edges++;
                float mx = (patch.Vx[sIdx] + patch.Vx[eIdx]) * 0.5f, my = (patch.Vy[sIdx] + patch.Vy[eIdx]) * 0.5f, mz = (patch.Vz[sIdx] + patch.Vz[eIdx]) * 0.5f;
                if (jp.Trace(mx, my + 1f, mz, mx, my - 1f, mz) >= 1f) continue;
                float nx = -(patch.Vz[eIdx] - patch.Vz[sIdx]), nz = patch.Vx[eIdx] - patch.Vx[sIdx], nl = MathF.Sqrt(nx * nx + nz * nz);
                if (nl < 1e-6f) continue;
                nx /= nl; nz /= nl;                                      // one of the two sides: the open one has no triangle across the edge, test both
                foreach (float sg in new[] { 1f, -1f })
                {
                    var pos = new Vector3(mx + sg * nx * 0.001f, my - 0.001f, mz + sg * nz * 0.001f);
                    var v = new Vector3(JumpSpeed * sg * nx, 0f, JumpSpeed * sg * nz);
                    for (int i = 0; i < MaxJumpPoints; i++)
                    {
                        var dlt = v * JumpStep; v.Y -= 9.8f * JumpStep;
                        float k = patch.Trace(pos.X - dlt.X * 0.001f, pos.Y - dlt.Y * 0.001f, pos.Z - dlt.Z * 0.001f, pos.X + dlt.X * 1.001f, pos.Y + dlt.Y * 1.001f, pos.Z + dlt.Z * 1.001f);
                        if (k < 1f)
                        {
                            if ((i + k) * JumpStep < 0.2f) { quick++; break; }       // an adjoining floor next to the edge (planks beside the beach): a step, not a fall
                            ledges++;
                            if (ledges <= 6) GD.Print($"[jump] ledge at ({mx:0.0},{my:0.0},{mz:0.0}) heading {MathF.Atan2(sg * nx, sg * nz):0.00}: lands {pos.X + dlt.X * k:0.0},{pos.Y + dlt.Y * k:0.0},{pos.Z + dlt.Z * k:0.0} after {(i + k) * JumpStep:0.00} s");
                            break;
                        }
                        pos += dlt;
                    }
                }
            }
        GD.Print($"[jump] scan: {edges} border edges, {ledges} ledges with a fall, {quick} steps onto an adjoining floor");
    }

    /// <summary>Character::TestJump: standing over the jump patch, does the fall along the heading end on the walk patch?</summary>
    bool TestJump(Vector3 pos)
    {
        var jp = JumpPatchOf?.Invoke();
        if (jp == null) return false;
        if (jp.Trace(pos.X, pos.Y + 1f, pos.Z, pos.X, pos.Y - 1f, pos.Z) >= 1f) return false;
        var v = new Vector3(JumpSpeed * MathF.Sin(ay), vy, JumpSpeed * MathF.Cos(ay));
        for (int i = 0; i < MaxJumpPoints; i++)
        {
            jumpTrack[i] = pos;
            var dlt = v * JumpStep;
            v.Y -= 9.8f * JumpStep;
            var src = pos - dlt * 0.001f;
            var dst = pos + dlt * 1.001f;
            float k = TraceWalk(src, dst);
            if (k < 1f)
            {
                osculation = src + (dst - src) * k + new Vector3(0f, 0.01f, 0f);
                jumpFallTime = (i + k) * JumpStep; curJumpFall = 0f; jumpPoints = i + 1;
                return true;
            }
            pos += dlt;
        }
        return false;
    }

    /// <summary>Character::BuildJump: a jump from the spot (up and forward at 3.3 m/s) that must not hit the location and lands on the walk patch.</summary>
    bool BuildJump(Vector3 pos, float ang)
    {
        var jp = JumpPatchOf?.Invoke();
        if (jp == null || getPatch() == null) return false;
        float ft = TraceWalk(new Vector3(pos.X, pos.Y + 1f, pos.Z), new Vector3(pos.X, pos.Y - 1f, pos.Z));
        if (ft > 1f) return false;
        pos.Y = pos.Y + 1f + (-2f) * (ft - 0.1f);
        var v = new Vector3(JumpSpeed * MathF.Sin(ang), JumpSpeed, JumpSpeed * MathF.Cos(ang));
        for (int i = 0; i < MaxJumpPoints; i++)
        {
            jumpTrack[i] = pos;
            var dlt = v * JumpStep;
            v.Y -= 9.8f * JumpStep;
            var src = pos - dlt * 0.001f;
            var dst = pos + dlt * 1.001f;
            float k = TraceWalk(src, dst);
            if (k < 1f)
            {
                osculation = src + (dst - src) * k + new Vector3(0f, 0.01f, 0f);
                jumpFallTime = (i + k) * JumpStep; curJumpFall = 0f; jumpPoints = i + 1;
                // landing on the jump patch itself: bend the track to end exactly on it
                float fT = jp.Trace(osculation.X, osculation.Y + 10f, osculation.Z, osculation.X, osculation.Y - 20f, osculation.Z);
                if (fT < 1f)
                {
                    var vd = new Vector3(0f, osculation.Y + 10f + (-30f) * fT - osculation.Y, 0f);
                    if (vd.Length() > 0.1f) { vd /= jumpPoints; for (int j = 1; j < jumpPoints; j++) jumpTrack[j] += vd; }
                }
                return true;
            }
            if (Obstacle?.Invoke(src, dst) == true) return false;
            pos += dlt;
        }
        return false;
    }

    void BeginJump(bool explicitJump)
    {
        isJump = true; jumpSnd = true; vy = 0f; IsSliding = false;
        if (explicitJump) StatJumpsExplicit++; else StatJumps++;
        Snd("recoil"); Snd("clothes");
        if (explicitJump && rig != null && rig.Play("jump", false, true)) playing = "jump";
        if (JumpLog) GD.Print($"[jump] {Id} jumps from ({x:0.00},{y:0.00},{z:0.00}) heading {ay:0.00} to ({osculation.X:0.00},{osculation.Y:0.00},{osculation.Z:0.00}) in {jumpFallTime:0.00} s ({jumpPoints} track points{(explicitJump ? ", on request" : ", off a ledge")})");
    }

    /// <summary>Character::StartJump (the control "ChrJump"): a jump forward, if the location has a jump patch here.</summary>
    public bool Jump()
    {
        if (isJump || isSwim || !Placed || !IsAlive || fightMode) return false;
        if (!BuildJump(new Vector3(x, y, z), ay)) return false;
        BeginJump(true);
        return true;
    }

    /// <summary>Called every frame the character does not walk on the patch: the track, the landing.</summary>
    void TickJump(float dt)
    {
        curJumpFall += dt;
        if (curJumpFall < jumpFallTime)
        {
            int s = Math.Min((int)(curJumpFall / JumpStep), jumpPoints - 1);
            int e = Math.Min(s + 1, jumpPoints - 1);
            float kb = Math.Clamp(curJumpFall / JumpStep - s, 0f, 1f);
            var p = jumpTrack[s] + (jumpTrack[e] - jumpTrack[s]) * kb;
            x = p.X; y = p.Y; z = p.Z;
        }
        else
        {
            if (jumpSnd) { Snd("jump"); jumpSnd = false; }
            x = osculation.X; y = osculation.Y; z = osculation.Z;
            isJump = false; vy = 0f; node = -1; hasNay = false;
            if (JumpLog) GD.Print($"[jump] {Id} landed at ({x:0.00},{y:0.00},{z:0.00})");
        }
        Apply();
    }

    /// <summary>The sea part of Character::Move: deeper than 0.6 of the height the character is carried, deeper than 0.8 it swims (its feet hang below the surface).</summary>
    void UpdateSwim(float dt)
    {
        var level = SeaLevelOf;
        if (level == null || isJump) { isSwim = false; runDisable = false; return; }
        if (swimChange > 0f) { swimChange -= dt; return; }
        bool old = isSwim;
        isSwim = false; runDisable = false;
        if (level(x, z) is not { } sy) return;
        seaY = sy;
        runDisable = seaY - y > SeaNoRun;
        if (seaY > y + SeaMove * CharHeight)
        {
            float dlt = old ? -0.03f : 0.03f;
            if (seaY > y + SeaSwim * CharHeight + dlt) { seaY -= SeaSwim * CharHeight; isSwim = true; }
        }
        if (isSwim != old)
        {
            swimChange = 0.8f;
            if (isSwim) { StatSwimStarts++; Snd("jump_water", 0f); }
            if (JumpLog) GD.Print($"[swim] {Id} {(isSwim ? "starts swimming" : "walks out of the water")} at ({x:0.00},{y:0.00},{z:0.00}), sea {sy:0.00}");
        }
    }
}
