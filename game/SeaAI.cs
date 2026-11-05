using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The decision side of the sea AI (sea_ai.dll: AIHelper / AIGroup / AIShip). The scripts decide everything (SEA_AI/AIShip.c Ship_CheckSituation, Ship_FireAction,
/// AITasks/AIAttack.c); the engine keeps the relation table and the groups and raises the events that drive those scripts:
///  * relations are read from the script (GetRelation) into a table that is refreshed only by AI_MESSAGE_UPDATE_RELATIONS (and when ships are added or
///    change captains), as in AIHelper::CalculateRelations. The HUD markers, the command menu, the AI targets and the forts all read this table, so a ship
///    turns red exactly when the script says UpdateRelations (e.g. the quest "NationUpdate" after Ship_NationAgressive);
///  * SHIP_FIRE_ACTION "ShpFireAction" (attacked character, its group commander, relation) for every ship in the sector of a broadside the player fires:
///    the script makes a neutral / friendly ship and its nation hostile and gives its group the attack task (or turns a companion into an enemy);
///  * SHIP_CHECK_SITUATION "Shp_ChkSituation" (character, ship object) for every AI ship about once a second, with SeaAI.Update.Situation.MinEnemyDistance:
///    the script re-targets, switches the charge, runs away when damaged, becomes a fire ship, boards other ships...;
///  * GROUP_CHECKTASKEVENT "GrpChkTsk" (group id) every few seconds: the attack task of a group is checked (new victims, retreat, the target group is dead).
/// </summary>
public partial class SeaView
{
    readonly Dictionary<(int, int), int> relCache = new();
    readonly Dictionary<string, string> groupType = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> pursuit = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> pursuitPlaced = new(StringComparer.OrdinalIgnoreCase);
    float groupCheckT;
    const float CheckSituationPeriod = 1.0f, GroupCheckPeriod = 3.0f;
    static readonly string[] RelName = { "friend", "neutral", "enemy" };

    /// <summary>The relation between two characters (0 friend, 1 neutral, 2 enemy) as the engine knows it since the last UpdateRelations.</summary>
    int Relation(int a, int b)
    {
        if (a == b) return 0;
        if (relCache.TryGetValue((a, b), out var r)) return r;
        r = Conv.ToInt(it.CallTop("GetRelation", V.Int(a), V.Int(b)));
        relCache[(a, b)] = r;
        return r;
    }

    static string TaskName(int t) => t switch
    {
        0 => "drift", 1 => "attack", 2 => "follow", 3 => "defend", 4 => "defend group", 5 => "board", 6 => "run away", 7 => "move", 8 => "fire ship / board", _ => "none",
    };

    static string Rel(int r) => r >= 0 && r < RelName.Length ? RelName[r] : r.ToString();

    /// <summary>AI_MESSAGE_UPDATE_RELATIONS: re-read the relations; changes towards the player's ship are logged (they recolour the HUD markers).</summary>
    void UpdateRelations()
    {
        var p = ships.FirstOrDefault(s => s.IsPlayer);
        var old = p == null ? new Dictionary<int, int>() : ships.Where(o => o != p && relCache.ContainsKey((p.Index, o.Index))).ToDictionary(o => o.Index, o => relCache[(p.Index, o.Index)]);
        relCache.Clear();
        if (p == null) return;
        foreach (var o in ships)
        {
            if (o == p || o.Dying) continue;
            int now = Relation(p.Index, o.Index);
            if (old.TryGetValue(o.Index, out var was) && was != now)
                GD.Print($"[sea] relation {p.Chr.Get("id")?.Value} -> {o.Chr.Get("id")?.Value} (group '{o.Group}'): {Rel(was)} -> {Rel(now)}");
        }
    }

    /// <summary>
    /// AI_MESSAGE_GROUP_SET_LOCATION_OTHER_GROUP: a group that pursues another one appears next to it (100-300 m away, bow towards it) instead of at its
    /// own locator. Resolved when the pursued group has a position (its ships or its group position), once per sea.
    /// </summary>
    void PlaceNearGroup(string group)
    {
        if (!pursuit.TryGetValue(group, out var other) || pursuitPlaced.Contains(group)) return;
        float x, z;
        var lead = ships.Where(s => !s.Dying && s.Group.Equals(other, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.IsPlayer ? 0 : 1).FirstOrDefault();
        if (lead != null) { x = lead.X; z = lead.Z; }
        else if (groupPos.TryGetValue(other, out var gp)) { x = gp.x; z = gp.z; }
        else return;
        float a = Random.Shared.NextSingle() * MathF.Tau, d = 150f + Random.Shared.NextSingle() * 200f;
        float nx = x + MathF.Sin(a) * d, nz = z + MathF.Cos(a) * d;
        groupPos[group] = (nx, nz, MathF.Atan2(x - nx, z - nz));
        pursuitPlaced.Add(group);
        GD.Print($"[sea] group '{group}' pursues '{other}': placed at ({nx:0},{nz:0}), {d:0} m from it");
    }

    /// <summary>AI_MESSAGE_CANNON_RELOAD: the charge type changed: every bort has to load again.</summary>
    void ReloadAll(ShipRt s)
    {
        float recharge = Math.Max(2f, Conv.ToFloat(it.FireEvent("CannGRTime", new List<V> { V.A(s.Chr) })));
        foreach (var b in s.Cannons.Select(c => c.bort).Distinct()) s.ReadyAt[b] = clock + recharge;
    }

    /// <summary>
    /// SHIP_FIRE_ACTION: the player fired a broadside in direction <paramref name="dir"/>; every other ship in that sector and within range is reported
    /// with its group commander and its relation to the player. The script decides the consequences (Ship_FireAction).
    /// </summary>
    void FireAction(ShipRt p, float dir, float range)
    {
        var hit = new List<ShipRt>();
        foreach (var o in ships)
        {
            if (o == p || o.Dying) continue;
            float d = Dist(p, o);
            if (d > range * 1.05f) continue;
            float a = MathF.Atan2(o.X - p.X, o.Z - p.Z) - dir;
            while (a > MathF.PI) a -= MathF.Tau;
            while (a < -MathF.PI) a += MathF.Tau;
            if (MathF.Abs(a) < 0.7f) hit.Add(o);
        }
        foreach (var o in hit)                                                            // the script may change tasks / groups: collected first
        {
            int rel = Relation(p.Index, o.Index);
            var cmd = groupCommander.TryGetValue(o.Group, out var c) && ShipOf(c) is { Dying: false } ? c : o.Chr;
            GD.Print($"[sea] fire action: {p.Chr.Get("id")?.Value} fires at {o.Chr.Get("id")?.Value} (group '{o.Group}', {Rel(rel)})");
            it.FireEvent("ShpFireAction", new List<V> { V.A(o.Chr), V.A(cmd), V.Int(rel) });
        }
    }

    /// <summary>The leader of a group for AITASK_DEFEND_GROUP: its commander, else any living ship of it.</summary>
    ShipRt? GroupLeader(string? group)
    {
        if (string.IsNullOrEmpty(group)) return null;
        if (groupCommander.TryGetValue(group, out var c) && ShipOf(c) is { Dying: false } cs) return cs;
        return ships.Where(s => !s.Dying && s.Group.Equals(group, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.IsPlayer ? 0 : 1).FirstOrDefault();
    }

    /// <summary>The periodic script events of the sea AI (see the class comment).</summary>
    void TickSeaAI(float dt)
    {
        foreach (var s in ships.ToList())
        {
            if (s.IsPlayer || s.Dying || !ships.Contains(s)) continue;
            s.CheckT -= dt;
            if (s.CheckT > 0f) continue;
            s.CheckT = CheckSituationPeriod;
            float minEnemy = 100000f;
            foreach (var o in ships)
                if (o != s && !o.Dying && Relation(s.Index, o.Index) == 2) minEnemy = Math.Min(minEnemy, Dist(s, o));
            SetF(s.Chr.Find("SeaAI.Update.Situation", true)!, "MinEnemyDistance", minEnemy);
            it.FireEvent("Shp_ChkSituation", new List<V> { V.A(s.Chr), V.A(s.Chr) });
        }
        groupCheckT -= dt;
        if (groupCheckT > 0f) return;
        groupCheckT = GroupCheckPeriod;
        var groups = ships.Where(s => !s.Dying && !s.IsPlayer && s.Group.Length > 0).Select(s => s.Group).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var g in groups) it.FireEvent("GrpChkTsk", new List<V> { V.Str(g) });
    }
}
