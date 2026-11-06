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
/// </summary>
public static class GroupsState
{
    sealed class Group { public float Look = 15f, Hear = 2.5f, Say = 20f; public int Priority = 10; }

    static readonly Dictionary<string, Group> groups = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<(string, string), string> relation = new();
    static readonly Dictionary<(string, string), (string act, string rel)> reaction = new();
    static readonly Dictionary<(string, string), float> alarm = new();
    static readonly Dictionary<(string, string), float> alarmDown = new();
    static readonly Dictionary<int, List<(int attacker, float time)>> targets = new();

    static Group G(string n) { n = n.ToLowerInvariant(); if (!groups.TryGetValue(n, out var g)) groups[n] = g = new Group(); return g; }
    static (string, string) K(string a, string b) => (a.ToLowerInvariant(), b.ToLowerInvariant());

    // characters_groups.cpp: a relation is "active" once its alarm reaches CGS_ALARMMAX (0.7) and stays so until it falls to CGS_ALARMMIN (0.3)
    const float AlarmMin = 0.3f, AlarmMax = 0.7f;
    static readonly HashSet<(string, string)> active = new();

    static void UpdActive((string, string) k)
    {
        float a = alarm.GetValueOrDefault(k);
        if (a <= AlarmMin) active.Remove(k);
        if (a >= AlarmMax) active.Add(k);
    }

    /// <summary>
    /// CharactersGroups::Execute: the alarm fades; then the script is told how things stand for the player every frame (event CharacterGroup_UpdateAlarm "fl": the highest
    /// alarm of a hostile group, and whether such a group has somebody with the sword out in an active relation) - the script turns it into the battle music, the alarm
    /// bar of the land interface and the hero's sword timeout.
    /// </summary>
    public static void Tick(Interp it, float dt)
    {
        foreach (var k in alarm.Keys.ToList())
        {
            float down = alarmDown.TryGetValue(k, out var d) ? d : 0.02f;
            if (alarm[k] > 0f) alarm[k] = Math.Max(0f, alarm[k] - down * dt);
            UpdActive(k);
        }
        var members = CharacterView.All.Values.Where(c => c.Placed).ToList();
        if (members.Count > 0)
        {
            float playerAlarm = 0f; bool playerActive = false;
            foreach (var g in members.Select(c => GroupOf(c.Ent.Root)).Where(g => g.Length > 0 && !string.Equals(g, "player", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Relation("player", g) != "enemy") continue;
                var k1 = K("player", g); var k2 = K(g, "player");
                playerAlarm = Math.Max(playerAlarm, Math.Max(alarm.GetValueOrDefault(k1), alarm.GetValueOrDefault(k2)));
                if ((active.Contains(k1) || active.Contains(k2)) && members.Any(c => c.FightMode && string.Equals(GroupOf(c.Ent.Root), g, StringComparison.OrdinalIgnoreCase))) playerActive = true;
            }
            it.FireEvent("CharacterGroup_UpdateAlarm", new List<V> { V.Float(playerAlarm), V.Int(playerActive ? 1 : 0) });
        }
        foreach (var l in targets.Values)
            for (int i = l.Count - 1; i >= 0; i--)
            {
                var (a, t) = l[i];
                if (t - dt <= 0f) l.RemoveAt(i); else l[i] = (a, t - dt);
            }
    }

    static string GroupOf(Attr chr) => chr.Find("chr_ai.group", false)?.Value ?? "";
    static int IndexOf(Attr chr) => (int)Conv.ParseFloatPrefix(chr.Get("index")?.Value);

    /// <summary>Current relation of two groups ("friend" / "neitral" / "enemy"): the alarm switches to the "act" reaction.</summary>
    static string Relation(string ga, string gb)
    {
        if (string.Equals(ga, gb, StringComparison.OrdinalIgnoreCase)) return "friend";
        var k1 = K(ga, gb); var k2 = K(gb, ga);
        bool alarmed = alarm.GetValueOrDefault(k1) > 0.01f || alarm.GetValueOrDefault(k2) > 0.01f;
        if (alarmed)
        {
            if (reaction.TryGetValue(k1, out var r1)) return r1.act;
            if (reaction.TryGetValue(k2, out var r2)) return r2.act;
        }
        if (relation.TryGetValue(k1, out var a)) return a;
        if (relation.TryGetValue(k2, out var b)) return b;
        return "neitral";
    }

    static bool Enemy(Attr a, Attr b) => Relation(GroupOf(a), GroupOf(b)) == "enemy";

    /// <summary>Relation of the two characters' groups is "enemy" (or <paramref name="b"/> attacked <paramref name="a"/> a moment ago): the fight AI picks its victims among these.</summary>
    public static bool AreEnemies(Attr a, Attr b) => Enemy(a, b) || IsRemembered(a, b);

    static CharacterView? View(Attr a) => CharacterView.All.TryGetValue(a, out var v) ? v : null;

    static CharacterView? Find(int index) => CharacterView.All.Values.FirstOrDefault(c => IndexOf(c.Ent.Root) == index);

    /// <summary>The nearest valid enemy of <paramref name="chr"/>: a remembered attacker first, otherwise anybody hostile inside the group's look radius.</summary>
    static int Target(Attr chr)
    {
        var me = View(chr);
        if (me == null || !me.Placed) return -1;
        if (targets.TryGetValue(IndexOf(chr), out var list))
            foreach (var (a, _) in list.OrderBy(t => t.time))
                if (Find(a) is { } c && c.Placed && c.IsAlive) return a;
        var grp = G(GroupOf(chr));
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
            case "RegistryGroup": G(S(1)); return V.Int(1);
            case "SetGroupLook": G(S(1)).Look = F(2); return V.Int(1);
            case "SetGroupHear": G(S(1)).Hear = F(2); return V.Int(1);
            case "SetGroupSay": G(S(1)).Say = F(2); return V.Int(1);
            case "SetGroupPriority": G(S(1)).Priority = (int)F(2); return V.Int(1);
            case "SetRelation": relation[K(S(1), S(2))] = S(3); return V.Int(1);
            case "SetAlarmReaction": reaction[K(S(1), S(2))] = (S(3), S(4)); return V.Int(1);
            case "SetAlarm": { var k = K(S(1), S(2)); alarm[k] = Math.Clamp(F(3), 0f, 1f); UpdActive(k); return V.Int(1); }
            case "SetAlarmDown": alarmDown[K(S(1), S(2))] = F(3); return V.Int(1);
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
            {
                string a = S(1), b = S(2);
                alarm[K(a, b)] = 1f; alarm[K(b, a)] = 1f; active.Add(K(a, b)); active.Add(K(b, a));
                // MsgAttack: the victim's friends become the attacker's enemies as well
                foreach (var other in groups.Keys.ToList())
                {
                    if (string.Equals(other, a, StringComparison.OrdinalIgnoreCase) || string.Equals(other, b, StringComparison.OrdinalIgnoreCase) || Relation(b, other) != "friend") continue;
                    alarm[K(a, other)] = 1f; alarm[K(other, a)] = 1f; active.Add(K(a, other)); active.Add(K(other, a));
                }
                return V.Int(1);
            }
            case "AddTarget":                                               // victim, attacker, seconds to remember
                if (d.Length > 3 && d[1].Attr is { } v && d[2].Attr is { } at)
                {
                    int vi = IndexOf(v);
                    if (!targets.TryGetValue(vi, out var l)) targets[vi] = l = new();
                    l.RemoveAll(x => x.attacker == IndexOf(at));
                    l.Add((IndexOf(at), F(3)));
                }
                return V.Int(1);
            case "ClearAllTargets": targets.Clear(); return V.Int(1);
            case "UpdChrTrg": case "ResetWaveTime": case "LoadDataRelations": case "SaveData": case "RestoreStates": case "MoveChr":
                return V.Int(1);
        }
        return V.Int(0);
    }

    static bool IsRemembered(Attr chr, Attr trg)
        => targets.TryGetValue(IndexOf(chr), out var l) && l.Any(x => x.attacker == IndexOf(trg));
}
