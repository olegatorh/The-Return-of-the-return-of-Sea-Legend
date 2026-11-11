using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine's "CharactersGroups": who is friend, neutral or enemy of whom, alarm levels and target selection.
/// Protocol (script side: Loc_ai/LAi_groups.c): messages start with a string command: RegistryGroup, SetGroupLook/Hear/Say/Priority, SetRelation,
/// SetAlarmReaction, SetAlarm, SetAlarmDown, MoveChr, GetTrg, VldTrg, IsEnemy, Attack, AddTarget, UpdChrTrg, ClearAllTargets, LoadDataRelations, SaveData ...
/// The state is static: the script deletes and re-creates the class on every location change but expects the grudges to be remembered.
/// The relation/alarm/memory rules live in <see cref="GroupRelations"/> (StormScript, unit tested); this class adds the characters of the location and their positions.
/// </summary>
public static class GroupsState
{
    static readonly GroupRelations R = new();

    /// <summary>
    /// CharactersGroups::Execute: the alarm fades; then the script is told how things stand for the player every frame (event CharacterGroup_UpdateAlarm "fl": the highest
    /// alarm of a hostile group, and whether such a group has somebody with the sword out in an active relation) - the script turns it into the battle music, the alarm
    /// bar of the land interface and the hero's sword timeout.
    /// </summary>
    public static void Tick(Interp it, float dt)
    {
        R.DecayAlarms(dt);
        var members = CharacterView.All.Values.Where(c => c.Placed).ToList();
        if (members.Count > 0)
        {
            float playerAlarm = 0f; bool playerActive = false;
            foreach (var g in members.Select(c => GroupOf(c.Ent.Root)).Where(g => g.Length > 0 && !string.Equals(g, "player", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (R.Relation("player", g) != "enemy") continue;
                playerAlarm = Math.Max(playerAlarm, Math.Max(R.Alarm("player", g), R.Alarm(g, "player")));
                if ((R.IsActive("player", g) || R.IsActive(g, "player")) && members.Any(c => c.FightMode && string.Equals(GroupOf(c.Ent.Root), g, StringComparison.OrdinalIgnoreCase))) playerActive = true;
            }
            it.FireEvent("CharacterGroup_UpdateAlarm", new List<V> { V.Float(playerAlarm), V.Int(playerActive ? 1 : 0) });
        }
        R.ExpireTargets(dt);
    }

    static string GroupOf(Attr chr) => chr.Find("chr_ai.group", false)?.Value ?? "";
    static int IndexOf(Attr chr) => (int)Conv.ParseFloatPrefix(chr.Get("index")?.Value);

    static bool Enemy(Attr a, Attr b) => R.Relation(GroupOf(a), GroupOf(b)) == "enemy";

    /// <summary>Relation of the two characters' groups is "enemy" (or <paramref name="b"/> attacked <paramref name="a"/> a moment ago): the fight AI picks its victims among these.</summary>
    public static bool AreEnemies(Attr a, Attr b) => Enemy(a, b) || IsRemembered(a, b);

    static CharacterView? View(Attr a) => CharacterView.All.TryGetValue(a, out var v) ? v : null;

    static CharacterView? Find(int index) => CharacterView.All.Values.FirstOrDefault(c => IndexOf(c.Ent.Root) == index);

    /// <summary>The nearest valid enemy of <paramref name="chr"/>: a remembered attacker first, otherwise anybody hostile inside the group's look radius.</summary>
    static int Target(Attr chr)
    {
        var me = View(chr);
        if (me == null || !me.Placed) return -1;
        foreach (int a in R.Remembered(IndexOf(chr)))
            if (Find(a) is { } c && c.Placed && c.IsAlive) return a;
        var grp = R.G(GroupOf(chr));
        int best = Seen(me, grp.Look);
        if (best >= 0) return best;
        // nobody in sight: the engine's group "say" radius - a mate within it who sees an enemy tells the others (boarding crews across the deck find each other)
        float bd = float.MaxValue;
        foreach (var m in CharacterView.All.Values)
        {
            if (ReferenceEquals(m, me) || !m.Placed || !m.IsAlive || !string.Equals(GroupOf(m.Ent.Root), GroupOf(chr), StringComparison.OrdinalIgnoreCase)) continue;
            var q = m.ScriptPos - me.ScriptPos;
            if (q.X * q.X + q.Z * q.Z > grp.Say * grp.Say) continue;
            int t = Seen(m, grp.Look);
            if (t < 0 || Find(t) is not { } tv) continue;
            var p = tv.ScriptPos - me.ScriptPos;
            float d2 = p.X * p.X + p.Z * p.Z;
            if (d2 < bd) { bd = d2; best = t; }
        }
        return best;
    }

    /// <summary>The nearest living enemy of <paramref name="me"/> within <paramref name="look"/> metres (plan distance), or -1.</summary>
    static int Seen(CharacterView me, float look)
    {
        int best = -1; float bd = look * look;
        foreach (var c in CharacterView.All.Values)
        {
            if (ReferenceEquals(c, me) || !c.Placed || !c.IsAlive || !Enemy(me.Ent.Root, c.Ent.Root)) continue;
            var p = c.ScriptPos - me.ScriptPos;
            float d2 = p.X * p.X + p.Z * p.Z;
            if (d2 < bd) { bd = d2; best = IndexOf(c.Ent.Root); }
        }
        return best;
    }

    public static V OnMessage(Interp it, V[] d)
    {
        string cmd = d.Length > 0 ? Conv.ToStr(d[0]) : "";
        string S(int i) => i < d.Length ? Conv.ToStr(d[i]) : "";
        float F(int i) => i < d.Length ? Conv.ToFloat(d[i]) : 0f;
        switch (cmd)
        {
            case "RegistryGroup": R.G(S(1)); return V.Int(1);
            case "SetGroupLook": R.G(S(1)).Look = F(2); return V.Int(1);
            case "SetGroupHear": R.G(S(1)).Hear = F(2); return V.Int(1);
            case "SetGroupSay": R.G(S(1)).Say = F(2); return V.Int(1);
            case "SetGroupPriority": R.G(S(1)).Priority = (int)F(2); return V.Int(1);
            case "SetRelation": R.SetRelation(S(1), S(2), S(3)); return V.Int(1);
            case "SetAlarmReaction": R.SetReaction(S(1), S(2), S(3), S(4)); return V.Int(1);
            case "SetAlarm": R.SetAlarm(S(1), S(2), F(3)); return V.Int(1);
            case "SetAlarmDown": R.SetAlarmDown(S(1), S(2), F(3)); return V.Int(1);
            case "GetTrg":                                                  // chr, &index
            {
                if (d.Length < 3 || d[1].Attr is not { } chr) return V.Int(0);
                int t = Target(chr);
                if (System.Environment.GetEnvironmentVariable("SEADOGS_GRPLOG") != null) GD.Print($"[grp] GetTrg {GroupOf(chr)} #{IndexOf(chr)} -> {t}");
                if (t < 0) return V.Int(0);
                it.WriteRef(d[2], V.Int(t));
                return V.Int(1);
            }
            case "VldTrg":                                                  // chr, trg
                return V.Int(d.Length > 2 && d[1].Attr is { } c1 && d[2].Attr is { } t1 && View(t1) is { IsAlive: true, Placed: true } && (Enemy(c1, t1) || IsRemembered(c1, t1)) ? 1 : 0);
            case "IsEnemy":
                return V.Int(d.Length > 2 && d[1].Attr is { } c2 && d[2].Attr is { } t2 && (Enemy(c2, t2) || IsRemembered(c2, t2)) ? 1 : 0);
            case "Attack":                                                  // group of the attacker, group of the victim: raise the alarm
                R.Attack(S(1), S(2));
                return V.Int(1);
            case "AddTarget":                                               // victim, attacker, seconds to remember
                if (d.Length > 3 && d[1].Attr is { } v && d[2].Attr is { } at) R.AddTarget(IndexOf(v), IndexOf(at), F(3));
                return V.Int(1);
            case "ClearAllTargets": R.ClearTargets(); return V.Int(1);
            case "UpdChrTrg": case "ResetWaveTime": case "LoadDataRelations": case "SaveData": case "RestoreStates": case "MoveChr":
                return V.Int(1);
        }
        return V.Int(0);
    }

    static bool IsRemembered(Attr chr, Attr trg) => R.IsRemembered(IndexOf(chr), IndexOf(trg));
}
