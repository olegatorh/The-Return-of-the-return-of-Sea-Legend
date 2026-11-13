using System;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>What the aim circle shows this frame.</summary>
public struct AimState
{
    public bool Show;          // a broadside can fire in the direction of the camera (AIShipCannonController::isCanFire)
    public int Relation;       // -1 nothing under the aim point (white), else 0 friend (green), 1 neutral (grey), 2 enemy (red)
}

/// <summary>
/// The aim circle of the sea battle: AIShipCameraController (sea_ai/src/ai_ship_camera_controller.cpp) draws the texture CROSSHAIR\crosshair.tga in the middle of the
/// screen ONLY while the camera is not "outside" (script SEA_AI\AICameras.c: Crosshair.OutsideCamera = false for the deck camera only; the ship camera, the free
/// camera and the telescope hide it) and only if some broadside can fire along the camera direction (isCanFire: the horizontal direction inside the bort's
/// FireZone, the elevation within FireAngMin..FireAngMax). The aim point is the ship position + the range of a ball fired at the camera elevation
/// (AICannon::CalcMaxFireDistance at the bort's height) along the 3D view direction, taken on the water. A ship is "under" it when that point lies within
/// 0.8 * ship length of a point 0.8 * length behind the ship centre (seen from us); the colour is then the relation (Crosshair.Colors: enemy red, friend green,
/// neutral grey, otherwise white) and the size pulses 0.9..1.1 (fDelta runs 0..2 at 5 per second).
/// </summary>
public partial class SeaView
{
    float aimDelta;
    int aimLogRel = -9; bool aimLogShow;
    /// <summary>The latest aim state (log '[aim]' on every change).</summary>
    public AimState Aim;

    /// <summary>isCanFire + GetFirePos for the view direction <paramref name="d"/> (engine axes); false when no side can fire that way.</summary>
    bool AimFirePos(ShipRt p, Vector3 d, out Vector3 firePos)
    {
        firePos = default;
        var sh = p.Chr.Find("Ship", false);
        int ti = (int)F(sh, "Type", -1);
        Attr? type = it.Globals.TryGetValue("ShipsTypes", out var st) && st.Arr != null && ti >= 0 && ti < st.Arr.Length ? st.Arr[ti].Val.Attr : null;
        float hl = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        if (hl < 1e-4f) return false;
        float pitch = MathF.Atan2(d.Y, hl);
        for (int i = 0; i < BortNames.Length; i++)
        {
            string bn = BortNames[i];
            var gs = GunsOf(p).Where(g => g.Bort == bn).ToList();
            if (gs.Count == 0 || gs.All(g => g.Damaged)) continue;                      // no guns / AISHIP_BORT::isBortDamaged
            float zone = F(type, $"Cannons.Borts.{bn}.FireZone"), dir = p.Ay + F(type, $"Cannons.Borts.{bn}.FireDir");
            if ((d.X * MathF.Sin(dir) + d.Z * MathF.Cos(dir)) / hl <= MathF.Cos(zone / 2f)) continue;          // fCosFireZone
            float aMin = F(sh, $"Cannons.Borts.{bn}.FireAngMin", F(sh, "Cannons.FireAngMin", -0.35f)), aMax = F(sh, $"Cannons.Borts.{bn}.FireAngMax", F(sh, "Cannons.FireAngMax", 0.6f));
            float ang = MathF.Atan(MathF.Abs(d.Y / hl));                                 // the angle between (bort direction, camera y) and the horizontal
            if (d.Y < 0f && ang > MathF.Abs(aMin)) continue;
            if (d.Y > 0f && ang > MathF.Abs(aMax)) continue;
            float h0 = p.Cannons.Where(c => c.bort == bn).Select(c => c.pos.Y).DefaultIfEmpty(3f).Average();
            float maxDist = MaxFireDistance(h0, Math.Max(10f, F(sh, "Cannons.SpeedV0", 50f)), pitch);
            if (System.Environment.GetEnvironmentVariable("SEADOGS_AIMLOG") != null && Engine.GetProcessFrames() % 60 == 0) GD.Print($"[aim] bort {bn} pitch {pitch:0.00} h0 {h0:0.0} v0 {F(sh, "Cannons.SpeedV0", 50f):0} range {maxDist:0} m horizontal {maxDist * MathF.Cos(pitch):0} m");
            firePos = new Vector3(p.X + d.X * maxDist, 0f, p.Z + d.Z * maxDist);          // vOurPos + fMaxDist * !vFireDir, y = 0
            return true;
        }
        return false;
    }

    /// <summary>AIShipCameraController::Execute / Realize for the player: the state of the circle this frame.</summary>
    AimState ComputeAim(ShipRt p, float dt)
    {
        var st = new AimState { Relation = -1 };
        // outside camera: the ship / free camera and the telescope have no circle (Crosshair.OutsideCamera)
        if (!deckCam || freeCam || scopeT > 0f || CommandsOpen || !HasPlayerShip) return st;
        var f = -Cam.GlobalTransform.Basis.Z;
        var d = new Vector3(f.X, f.Y, -f.Z);                                              // Godot -> engine: z flips
        if (!AimFirePos(p, d, out var fp)) return st;
        st.Show = true;
        float best = 1e8f;
        foreach (var o in ships)
        {
            if (ReferenceEquals(o, p) || o.Dying) continue;
            var u = new Vector2(o.X - p.X, o.Z - p.Z);
            if (u.LengthSquared() > 1e-6f) u = u.Normalized();
            var fake = new Vector2(o.X + u.X * o.Length * 0.8f, o.Z + u.Y * o.Length * 0.8f);       // the "fake" position of the ship behind it
            float dist = (fake - new Vector2(fp.X, fp.Z)).Length();
            if (dist > 0.8f * o.Length || dist > best) continue;
            best = dist; st.Relation = Relation(p.Index, o.Index);
        }
        foreach (var fo in forts)
        {
            if (fo.Index == p.Index) continue;
            foreach (var c in fo.Cannons)
            {
                if (c.Dead) continue;
                var toC = new Vector2(c.Pos.X - p.X, c.Pos.Z - p.Z); float cd = toC.Length();
                float fireDist = MathF.Sqrt((fp.X - p.X) * (fp.X - p.X) + (fp.Z - p.Z) * (fp.Z - p.Z));
                if (cd > fireDist || cd > best) continue;
                if ((d.X * toC.X + d.Z * toC.Y) / Math.Max(1e-4f, MathF.Sqrt(d.X * d.X + d.Z * d.Z) * cd) < 0f) continue;
                var at = new Vector2(p.X + d.X / MathF.Sqrt(d.X * d.X + d.Z * d.Z) * cd, p.Z + d.Z / MathF.Sqrt(d.X * d.X + d.Z * d.Z) * cd);   // GetFirePos(vZ, distance) on the ground
                if ((at - new Vector2(c.Pos.X, c.Pos.Z)).LengthSquared() > 64f) continue;
                best = cd; st.Relation = Relation(p.Index, fo.Index);
            }
        }
        if (st.Relation >= 0) { aimDelta += dt * 5f; while (aimDelta > 2f) aimDelta -= 2f; }
        return st;
    }

    /// <summary>The size factor of the circle: 0.9 + 0.2 * (delta > 1 ? delta : 2 - delta).</summary>
    public float AimPulse => 0.9f + 0.2f * (aimDelta > 1f ? aimDelta : 2f - aimDelta);

    void UpdateAim(ShipRt? p, HudData d, float dt)
    {
        var s = p == null ? new AimState { Relation = -1 } : ComputeAim(p, dt);
        Aim = s; d.Aim = s; d.AimPulse = AimPulse;
        if (s.Show != aimLogShow || s.Relation != aimLogRel)
        {
            aimLogShow = s.Show; aimLogRel = s.Relation;
            GD.Print($"[aim] {(!s.Show ? "hidden" : s.Relation switch { 2 => "red (enemy under the aim point, a shot reaches)", 0 => "green (friend)", 1 => "grey (neutral)", _ => "white (nothing under the aim point)" })}");
        }
    }
}
