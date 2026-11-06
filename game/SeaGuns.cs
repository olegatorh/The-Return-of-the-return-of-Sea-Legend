using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Per-cannon state of a ship, as AICannon / AIShipCannonController (sea_ai/src/ai_cannon.cpp, ai_ship_cannon_controller.cpp):
///  * every gun loads its own ball: Load = CANNON_LOAD (CannLoad: takes a ball and powder from the hold), Unload = CANNON_UNLOAD (CannUnload: gives them back),
///    after a shot the gun reloads at once (the ball is taken) and is ready after its own CANNON_GET_RECHARGE_TIME (CannGRTime);
///  * a broadside fires only when the whole bort has finished reloading (AISHIP_BORT::isCharged: ChargePercent >= 1), every intact gun then shoots after its own
///    CANNON_GET_FIRE_TIME delay; without balls for the bort the guns stay empty (SetRechargeEnable), the charge change (AI_MESSAGE_CANNON_RELOAD) unloads
///    every gun and loads it again with the new charge, AI_MESSAGE_UNLOAD gives all balls back when the sea is left;
///  * damaged guns (Ship.Cannons.Borts.&lt;bort&gt;.damages.c&lt;N&gt; &gt;= 1, 2 = disabled) do not load or fire; the bort attributes ChargeRatio / DamageRatio / MaxFireDistance
///    are written for the script and the HUD.
/// </summary>
public partial class SeaView
{
    sealed class Gun
    {
        public string Bort = ""; public Vector3 Pos; public int Idx;
        public float Damage;                       // fDamaged: >= 1 destroyed, >= 2 disabled
        public bool Loaded;                        // !bEmpty
        public bool Recharging;                    // bRecharged: loading, Time counts down
        public bool Fired;                         // waiting for its fire delay
        public bool CanRecharge = true;            // bCanRecharge: enough balls for the bort
        public float Time, Total = 1f;
        public bool Damaged => Damage >= 1f;
        public bool Ready => Loaded && !Recharging && !Fired && !Damaged;
        public float Percent => Recharging ? 1f - Time / Math.Max(0.001f, Total) : 1f;      // GetRechargePercent
    }

    static readonly string[] BortNames = { "cannonf", "cannonr", "cannonb", "cannonl" };
    float gunAttrT;

    /// <summary>The guns of a ship (built on first use, when the hold exists): loaded at once like AIShipCannonController::Init (QuickRecharge).</summary>
    List<Gun> GunsOf(ShipRt s)
    {
        if (s.Guns != null && s.Guns.Count == s.Cannons.Count) return s.Guns;
        s.Guns = new List<Gun>();
        var sh = s.Chr.Find("Ship", false);
        foreach (var grp in s.Cannons.GroupBy(c => c.bort))
        {
            int j = 0;
            foreach (var c in grp)
            {
                var g = new Gun { Bort = grp.Key, Pos = c.pos, Idx = j };
                g.Damage = F(sh, $"Cannons.Borts.{grp.Key}.damages.c{j}", 0f);
                s.Guns.Add(g); j++;
            }
        }
        foreach (var grp in s.Guns.GroupBy(g => g.Bort))
        {
            bool enough = EnoughBalls(s, grp.Key);
            foreach (var g in grp.Where(x => !x.Damaged)) { g.CanRecharge = enough; Recharge(s, g); g.Time = enough ? 0f : g.Total; }
        }
        return s.Guns;
    }

    int IntactGuns(ShipRt s, string bort) => GunsOf(s).Count(g => g.Bort == bort && !g.Damaged);

    /// <summary>isHaveEnoughBallsForBort: SHIP_GET_CURRENT_BALLS_NUM against the intact guns of the bort.</summary>
    bool EnoughBalls(ShipRt s, string bort) => s.Guns != null && s.Guns.Count(g => g.Bort == bort && !g.Damaged) <= Conv.ToInt(it.FireEvent("Shp_GetCurBallsNum", new List<V> { V.A(s.Chr) }));

    void Unload(ShipRt s, Gun g)
    {
        if (!g.Damaged && g.Loaded) it.FireEvent("CannUnload", new List<V> { V.A(s.Chr) });
        g.Loaded = false; g.Recharging = false; g.Fired = false;
    }

    /// <summary>AICannon::Recharge: unload what is in the gun, load again if allowed, then wait the script's recharge time.</summary>
    void Recharge(ShipRt s, Gun g)
    {
        if (g.Fired || g.Damaged) return;
        if (g.Loaded) Unload(s, g);
        if (g.CanRecharge) g.Loaded = Conv.ToBool(it.FireEvent("CannLoad", new List<V> { V.A(s.Chr) }));
        g.Recharging = true;
        g.Total = g.Time = Math.Max(0.5f, Conv.ToFloat(it.FireEvent("CannGRTime", new List<V> { V.A(s.Chr) })));
        if (s.IsPlayer && g.Idx == 0) GD.Print($"[sea] gun {g.Bort}#0 loading: {(g.Loaded ? "ball taken" : "empty")}, ready in {g.Total:0.0} s");
    }

    /// <summary>AI_MESSAGE_CANNON_RELOAD: the charge type changed: every intact gun is unloaded and loaded with the new charge.</summary>
    void ReloadAll(ShipRt s)
    {
        var guns = GunsOf(s);
        foreach (var g in guns) if (!g.Damaged) Unload(s, g);
        foreach (var grp in guns.GroupBy(g => g.Bort))
        {
            bool enough = EnoughBalls(s, grp.Key);
            foreach (var g in grp.Where(x => !x.Damaged)) { g.CanRecharge = enough; Recharge(s, g); }
        }
        GD.Print($"[sea] {s.Chr.Get("id")?.Value}: charge changed, {guns.Count(g => !g.Damaged)} guns unloaded and loading again ({guns.Where(g => g.Loaded).Count()} loaded)");
    }

    /// <summary>AI_MESSAGE_UNLOAD: every loaded gun gives its ball and powder back to the hold before the sea is left.</summary>
    void UnloadAll()
    {
        foreach (var s in ships)
        {
            if (s.Guns == null) continue;
            foreach (var g in s.Guns) if (!g.Damaged) Unload(s, g);
        }
    }

    /// <summary>The recharge ratio of a bort (AISHIP_BORT::fChargePercent): the mean over the intact guns; 0 when there are none.</summary>
    float BortCharge(ShipRt s, string bort)
    {
        var intact = GunsOf(s).Where(g => g.Bort == bort && !g.Damaged).ToList();
        return intact.Count == 0 ? 0f : intact.Average(g => g.Percent);
    }

    bool BortCharged(ShipRt s, string bort) => BortCharge(s, bort) >= 1f && GunsOf(s).Any(g => g.Bort == bort && g.Ready);

    /// <summary>Where a gun of the bort looks: the broadside direction (the locator axes are not kept).</summary>
    void TickGuns(float dt)
    {
        gunAttrT -= dt;
        bool attrs = gunAttrT <= 0f;
        if (attrs) gunAttrT = 0.25f;
        foreach (var s in ships)
        {
            if (s.Dying || s.Cannons.Count == 0) continue;
            var guns = GunsOf(s);
            foreach (var bg in guns.GroupBy(g => g.Bort))
            {
                int ready0 = bg.Count(g => g.Ready);
                foreach (var g in bg)
                {
                    if (g.Damaged || g.Fired || !g.Recharging) continue;
                    if (g.CanRecharge && g.Loaded) g.Time -= dt;                  // an empty gun that may not load waits for the next reload
                    if (g.Time <= 0f) { g.Recharging = false; g.Time = 0f; }
                }
                int intact = bg.Count(g => !g.Damaged);
                if (ready0 != bg.Count(g => g.Ready) && bg.Count(g => g.Ready) == intact && intact > 0 && s.IsPlayer)
                    it.FireEvent("Shp_BortReloaded", new List<V> { V.A(s.Chr), V.Str(bg.Key) });     // SHIP_BORT_RELOADED "as"
                if (!attrs) continue;
                var sh = s.Chr.Find("Ship", false);
                if (sh == null) continue;
                foreach (var g in bg) g.Damage = F(sh, $"Cannons.Borts.{bg.Key}.damages.c{g.Idx}", g.Damage);
                int total = bg.Count(), disabled = bg.Count(g => g.Damage >= 2f);
                SetF(sh, $"Cannons.Borts.{bg.Key}.ChargeRatio", BortCharge(s, bg.Key));
                SetF(sh, $"Cannons.Borts.{bg.Key}.DamageRatio", 1f - (float)(bg.Count(g => !g.Damaged) + disabled) / total);
                SetF(sh, $"Cannons.Borts.{bg.Key}.MaxFireDistance", MaxFireDistance(bg.Average(g => g.Pos.Y), F(sh, "Cannons.SpeedV0", 50f), F(sh, "Cannons.FireAngMax", 0.6f)));
            }
        }
    }

    /// <summary>The bort that the HUD shows on side <paramref name="i"/> (bow, starboard, stern, port); null when the ship has no such guns.</summary>
    string? HudBort(ShipRt p, int i)
    {
        if (p.Cannons.Any(c => c.bort == BortNames[i])) return BortNames[i];
        return i == 2 ? p.Cannons.Select(c => c.bort).FirstOrDefault(b => b != "cannonf" && b != "cannonr" && b != "cannonl") : null;
    }
}
