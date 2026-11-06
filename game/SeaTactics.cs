using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The tactical part of the ship AI (sea_ai: AIShip::GetAttackPoint, AIShipTaskController::DoAttackRotate / FindRunAwayPoint, AIShipMoveController, AIShipTouchController).
/// Which task a ship has is decided by the scripts (Shp_ChkSituation, GrpChkTsk: attack, follow, defend, run away when the hull is low...); here is how the engine
/// carries it out:
///  * attack: the goal is the nearest of 16 points of a ring of Ship.SeaAI.Init.AttackDistance (150 m) around the target that is not over land, renewed every 2 s
///    (AIShipMoveController::Move); in range of a charged bort the ship turns that bort to the target at half sail (DoAttackRotate);
///  * run away: away from every enemy ship (weight 1 - d / 1000) and enemy fort (d &lt; 2000 m) with the wind added (x10), as FindRunAwayPoint;
///  * ships ahead are steered round (AIShipTouchController), a ship that does not move although it sails (a shoal, a ship in the way) turns for a few seconds.
/// </summary>
public partial class SeaView
{
    static float Wrap(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    /// <summary>AIShip::GetAttackPoint: of 16 points of the attack ring round the target the one nearest to us that is deep enough; kept for 2 s.</summary>
    (float x, float z) AttackGoal(ShipRt s, ShipRt tgt, float dt)
    {
        s.GoalT -= dt;
        if (s.GoalT > 0f && s.GoalFor == tgt.Index) return (s.GoalX, s.GoalZ);
        s.GoalT = 2f; s.GoalFor = tgt.Index;                                              // AIShipMoveController::Move: fMoveTime = 2
        float ring = F(tgt.Chr.Find("Ship.SeaAI.Init", false), "AttackDistance", 150f);
        float best = float.MaxValue, bx = s.X, bz = s.Z;
        for (int i = 0; i < 16; i++)
        {
            float a = Random.Shared.NextSingle() * 0.1f + MathF.Tau * i / 15f;
            float px = tgt.X + MathF.Sin(a) * ring, pz = tgt.Z + MathF.Cos(a) * ring;
            if (island != null && AgroundAt(s, px, pz, a)) continue;
            float d = (px - s.X) * (px - s.X) + (pz - s.Z) * (pz - s.Z);
            if (d < best) { best = d; bx = px; bz = pz; }
        }
        s.GoalX = bx; s.GoalZ = bz;
        return (bx, bz);
    }

    /// <summary>
    /// DoAttackRotate: in range of a charged bort (with a margin of 20-70 m) the ship turns the bort that needs the least turning to face the target
    /// (the original turns by +-1.2 and adds half speed); out of range or while every bort reloads, the ship keeps going to the ring point.
    /// </summary>
    float AttackHeading(ShipRt s, ShipRt tgt, float want, ref bool slow)
    {
        var sh = s.Chr.Find("Ship", false);
        float d = Dist(s, tgt), v0 = Math.Max(10f, F(sh, "Cannons.SpeedV0", 50f)), ang = F(sh, "Cannons.FireAngMax", 0.6f);
        float zapas = 20f + 10f * (s.Index % 6);
        float bearing = MathF.Atan2(tgt.X - s.X, tgt.Z - s.Z);
        float bestTurn = float.MaxValue, bestAy = want;
        foreach (var bort in s.Cannons.Select(c => c.bort).Distinct())
        {
            if (!BortCharged(s, bort)) continue;
            float h = GunsOf(s).Where(g => g.Bort == bort).Average(g => g.Pos.Y);
            float range = MaxFireDistance(h, v0, ang);
            if (d > range - zapas) continue;
            float off = bort switch { "cannonr" => MathF.PI / 2, "cannonl" => -MathF.PI / 2, "cannonf" => 0f, _ => MathF.PI };
            float wantAy = bearing - off, turn = MathF.Abs(Wrap(wantAy - s.Ay));
            if (turn < bestTurn) { bestTurn = turn; bestAy = wantAy; }
        }
        if (bestTurn == float.MaxValue) return want;
        slow = true;
        return bestAy;
    }

    /// <summary>
    /// FindRunAwayPoint: the mean direction to the enemy ships (1 - d/1000) and forts (1 - d/2000, within 2000 m); the course is opposite to it with the wind
    /// (weight 10) added; without a threat the ship keeps its heading. Returns a far point on that course.
    /// </summary>
    (float x, float z) RunAwayGoal(ShipRt s)
    {
        float vx = 0f, vz = 0f; int n = 0;
        foreach (var o in ships)
        {
            if (o == s || o.Dying || Relation(s.Index, o.Index) != 2) continue;
            float dx = o.X - s.X, dz = o.Z - s.Z, d = MathF.Sqrt(dx * dx + dz * dz);
            if (d < 0.01f) continue;
            float w = Math.Clamp(1f - d / 1000f, 0f, 1f);
            vx += dx / d * w; vz += dz / d * w; n++;
        }
        foreach (var f in forts)
        {
            if (Relation(s.Index, f.Index) != 2) continue;
            float dx = f.X - s.X, dz = f.Z - s.Z, d = MathF.Sqrt(dx * dx + dz * dz);
            if (d > 2000f || d < 0.01f) continue;
            float w = Math.Clamp(1f - d / 2000f, 0f, 1f);
            vx += dx / d * w; vz += dz / d * w; n++;
        }
        if (n == 0) return (s.X + MathF.Sin(s.Ay) * 5000f, s.Z + MathF.Cos(s.Ay) * 5000f);
        vx /= n; vz /= n;
        float windAng = 0f;
        if (it.Globals.TryGetValue("Weather", out var w0) && w0.Val.Attr is { } wa) windAng = F(wa, "Wind.Angle");
        float ex = -vx + 10f * MathF.Sin(windAng), ez = -vz + 10f * MathF.Cos(windAng);       // engine axes: the wind blows towards (sin a, cos a)
        float len = MathF.Sqrt(ex * ex + ez * ez);
        if (len < 0.001f) return (s.X + MathF.Sin(s.Ay) * 5000f, s.Z + MathF.Cos(s.Ay) * 5000f);
        return (s.X + ex / len * 10000f, s.Z + ez / len * 10000f);
    }

    /// <summary>AIShipTouchController: another ship close ahead turns us away from it (the task target of ramming / boarding / formation excepted).</summary>
    float AvoidShips(ShipRt s, float want, ShipRt? tgt, int task)
    {
        float look = Math.Clamp(40f + s.Speed * KnotsToMs * 5f, 40f, 110f);
        float push = 0f;
        foreach (var o in ships)
        {
            if (o == s || o.Dying) continue;
            if (o == tgt && task != 1) continue;
            float dx = o.X - s.X, dz = o.Z - s.Z, d = MathF.Sqrt(dx * dx + dz * dz) - (s.Length + o.Length) * 0.25f;
            if (d > look) continue;
            float rel = Wrap(MathF.Atan2(dx, dz) - want);
            if (MathF.Abs(rel) > 0.9f) continue;
            push += (rel >= 0f ? -1f : 1f) * (1.1f * (1f - Math.Max(0f, d) / look));
        }
        return want + Math.Clamp(push, -1.4f, 1.4f);
    }

    /// <summary>A ship that sails (sail set) but has hardly moved for 8 s swings out for 5 s (a shoal, a ship in the way, being pinned by the wind).</summary>
    float AntiStuck(ShipRt s, float want, float dt)
    {
        s.StuckT += dt;
        if (s.StuckT >= 8f)
        {
            float moved = MathF.Sqrt((s.X - s.StuckX) * (s.X - s.StuckX) + (s.Z - s.StuckZ) * (s.Z - s.StuckZ));
            if (s.Sail > 0f && moved < 4f && s.StuckUntil <= 0f && s.StuckX != 0f)
            {
                s.StuckUntil = 5f; s.StuckDir = Random.Shared.Next(2) == 0 ? -1f : 1f;
                GD.Print($"[sea] {s.Chr.Get("id")?.Value} is stuck at ({s.X:0},{s.Z:0}): swings {(s.StuckDir > 0 ? "right" : "left")} for 5 s");
            }
            s.StuckT = 0f; s.StuckX = s.X; s.StuckZ = s.Z;
            if (s.StuckX == 0f) s.StuckX = 0.001f;
        }
        if (s.StuckUntil > 0f) { s.StuckUntil -= dt; return s.Ay + s.StuckDir * 1.4f; }
        return want;
    }
}
