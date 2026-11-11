using System;
using System.Collections.Generic;
using System.Linq;

namespace StormScript.Runtime;

/// <summary>
/// The Godot-free core of the engine's "CharactersGroups": group parameters, relations, alarm levels and the memory of who attacked whom.
/// Group names are case-insensitive. Characters are identified by their script index. Positions and character views stay in the game project (game/Groups.cs).
/// </summary>
public sealed class GroupRelations
{
    public sealed class Group { public float Look = 15f, Hear = 2.5f, Say = 20f; public int Priority = 10; }

    // characters_groups.cpp: a relation is "active" once its alarm reaches AlarmMax and stays so until it falls to AlarmMin
    public const float AlarmMin = 0.3f, AlarmMax = 0.7f;
    const float DefaultAlarmDown = 0.02f;

    readonly Dictionary<string, Group> groups = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string, string), string> relation = new();
    readonly Dictionary<(string, string), (string act, string rel)> reaction = new();
    readonly Dictionary<(string, string), float> alarm = new();
    readonly Dictionary<(string, string), float> alarmDown = new();
    readonly HashSet<(string, string)> active = new();
    readonly Dictionary<int, List<(int attacker, float time)>> targets = new();

    static (string, string) K(string a, string b) => (a.ToLowerInvariant(), b.ToLowerInvariant());

    /// <summary>The group with this name, registered on first use.</summary>
    public Group G(string name)
    {
        name = name.ToLowerInvariant();
        if (!groups.TryGetValue(name, out var g)) groups[name] = g = new Group();
        return g;
    }

    public void SetRelation(string a, string b, string rel) => relation[K(a, b)] = rel;
    public void SetReaction(string a, string b, string act, string rel) => reaction[K(a, b)] = (act, rel);
    public void SetAlarmDown(string a, string b, float speed) => alarmDown[K(a, b)] = speed;

    public void SetAlarm(string a, string b, float value)
    {
        var k = K(a, b);
        alarm[k] = Math.Clamp(value, 0f, 1f);
        UpdActive(k);
    }

    public float Alarm(string a, string b) => alarm.GetValueOrDefault(K(a, b));
    public bool IsActive(string a, string b) => active.Contains(K(a, b));

    void UpdActive((string, string) k)
    {
        float a = alarm.GetValueOrDefault(k);
        if (a <= AlarmMin) active.Remove(k);
        if (a >= AlarmMax) active.Add(k);
    }

    /// <summary>Current relation of two groups ("friend" / "neitral" / "enemy"): while an alarm is up the "act" reaction replaces it.</summary>
    public string Relation(string ga, string gb)
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

    /// <summary>The attacker's group raises the alarm against the victim's group, and against the victim's friends too (MsgAttack).</summary>
    public void Attack(string a, string b)
    {
        Raise(a, b);
        foreach (var other in groups.Keys.ToList())
        {
            if (string.Equals(other, a, StringComparison.OrdinalIgnoreCase) || string.Equals(other, b, StringComparison.OrdinalIgnoreCase) || Relation(b, other) != "friend") continue;
            Raise(a, other);
        }
    }

    void Raise(string a, string b)
    {
        alarm[K(a, b)] = 1f; alarm[K(b, a)] = 1f;
        active.Add(K(a, b)); active.Add(K(b, a));
    }

    /// <summary>The alarms fade (CharactersGroups::Execute).</summary>
    public void DecayAlarms(float dt)
    {
        foreach (var k in alarm.Keys.ToList())
        {
            float down = alarmDown.TryGetValue(k, out var d) ? d : DefaultAlarmDown;
            if (alarm[k] > 0f) alarm[k] = Math.Max(0f, alarm[k] - down * dt);
            UpdActive(k);
        }
    }

    /// <summary>Remember that <paramref name="attacker"/> hit <paramref name="victim"/> for <paramref name="seconds"/>.</summary>
    public void AddTarget(int victim, int attacker, float seconds)
    {
        if (!targets.TryGetValue(victim, out var l)) targets[victim] = l = new();
        l.RemoveAll(x => x.attacker == attacker);
        l.Add((attacker, seconds));
    }

    public bool IsRemembered(int chr, int attacker)
        => targets.TryGetValue(chr, out var l) && l.Any(x => x.attacker == attacker);

    /// <summary>Remembered attackers of a character, the one with the least time left first.</summary>
    public IEnumerable<int> Remembered(int chr)
        => targets.TryGetValue(chr, out var l) ? l.OrderBy(t => t.time).Select(t => t.attacker).ToList() : Enumerable.Empty<int>();

    public void ExpireTargets(float dt)
    {
        foreach (var l in targets.Values)
            for (int i = l.Count - 1; i >= 0; i--)
            {
                var (a, t) = l[i];
                if (t - dt <= 0f) l.RemoveAt(i); else l[i] = (a, t - dt);
            }
    }

    public void ClearTargets() => targets.Clear();
}
