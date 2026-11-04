using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// People on deck: the engine class PEOPLE_ON_SHIP (MODULES\PEOPLES_ON_SHIP.dll). The script creates the entity in SeaLogin (SEA_AI\sea.c) and, on every
/// "ShpCreate" event, Ship_Walk_Create (SEA_AI\shipWalk.c) sends AI_MESSAGE_ADD_SHIP "lileee" with the walk graph of the ship type
/// (SEA_AI\walk\*_walk.c): vertex count, verts[3n] (ship model space), graph[] = edges packed as a*256+b (-1 = unused) and types[n].
/// The original dll holds the model names (Lowcharacters\Lo_Man_1..3, Lo_Man_Kamzol_1..3), the animation "Lo_Man" (walk, run, reload, action1..4,
/// crawl, turn_left/right), and reads ship.crew.quantity, ship.POS.mode (SHIP_SAIL / SHIP_WAR / SHIP_STORM), isNight, IsOnDeck and HidePeopleOnDeck.
/// Point types (by their placement in the graphs): 0 = deck, 1 = beside a cannon, 2 = at a mast, 3 = a hatch / work spot.
/// Here: a few sailors per ship walk the graph, stop for a while with one of the work animations, and in battle mode run to the cannon points and reload.
/// They are children of the ship node, so they ride its movement and rocking. Far from the camera they are hidden and not animated.
/// </summary>
public partial class SeaView
{
    const int MSG_SHIP_DELETE = 50301, MSG_PEOPLES_ON_SHIP_MASTFALL = 77500;
    const int MaxSailorsPerShip = 6;
    const float SailorsViewDist = 220f;
    static readonly string[] SailorModels = ["Lo_Man_1", "Lo_Man_2", "Lo_Man_3", "Lo_Man_1", "Lo_Man_2", "Lo_Man_Kamzol_1", "Lo_Man_Kamzol_2", "Lo_Man_Kamzol_3"];
    static readonly string[] SailorIdle = ["action1", "action2", "action3", "action4", "stand"];

    sealed class DeckWalk
    {
        public Vector3[] P = [];          // engine space of the ship model (x right, y up, z forward)
        public int[] Type = [];
        public List<int>[] Next = [];
    }

    sealed class Sailor
    {
        public CharacterRig Rig = null!;
        public int At, To, Prev = -1;
        public float T, Wait, Yaw;
        public bool Moving, Working;
        public List<int> Route = new();     // battle: the way to the cannon point
    }

    sealed class Deck
    {
        public ShipRt Ship = null!;
        public DeckWalk Walk = null!;
        public Node3D Root = null!;
        public readonly List<Sailor> Crew = new();
        public bool Shown = true;
    }

    readonly List<Deck> decks = new();
    Entity? peopleEnt;
    readonly Random sailorRnd = new(7);

    V OnPeopleOnShip(Entity e, V[] d)
    {
        peopleEnt = e;
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        switch (msg)
        {
            case AI_ADD_SHIP:                                                      // "lileee": ship attribute (Characters[i].Ship), count, &verts, &graph, &types
                if (d.Length > 5 && d[1].Attr is { } shipAttr && ships.FirstOrDefault(s => ReferenceEquals(s.Chr.Get("Ship"), shipAttr)) is { } rt)
                {
                    var walk = ReadWalk(Conv.ToInt(d[2]), d[3].Slot?.Arr, d[4].Slot?.Arr, d[5].Slot?.Arr);
                    if (walk != null) AddDeck(rt, walk);
                }
                return V.Int(1);
            case MSG_SHIP_DELETE:                                                  // "la": the ship sank
                if (d.Length > 1 && d[1].Attr is { } chr)
                    foreach (var dk in decks.Where(x => ReferenceEquals(x.Ship.Chr, chr)).ToList()) { dk.Root.QueueFree(); decks.Remove(dk); }
                return V.Int(1);
            case MSG_PEOPLES_ON_SHIP_MASTFALL: return V.Int(1);
        }
        return V.Int(1);
    }

    static DeckWalk? ReadWalk(int count, Slot[]? verts, Slot[]? graph, Slot[]? types)
    {
        if (count <= 1 || verts == null || graph == null || verts.Length < count * 3) return null;
        var w = new DeckWalk { P = new Vector3[count], Type = new int[count], Next = new List<int>[count] };
        for (int i = 0; i < count; i++)
        {
            w.P[i] = new Vector3(Conv.ToFloat(verts[i * 3].Val), Conv.ToFloat(verts[i * 3 + 1].Val), Conv.ToFloat(verts[i * 3 + 2].Val));
            w.Type[i] = types != null && i < types.Length ? Conv.ToInt(types[i].Val) : 0;
            w.Next[i] = new List<int>();
        }
        foreach (var g in graph)
        {
            // a*256 + b with -1 for "none": the low byte 255 is -1 (and borrows one from the high part)
            int v = Conv.ToInt(g.Val), lo = v & 0xFF, hi;
            if (lo == 0xFF) { lo = -1; hi = (v + 1) >> 8; } else hi = (v - lo) >> 8;
            if (hi < 0 || lo < 0 || hi >= count || lo >= count || hi == lo) continue;
            if (!w.Next[hi].Contains(lo)) w.Next[hi].Add(lo);
            if (!w.Next[lo].Contains(hi)) w.Next[lo].Add(hi);
        }
        return w.Next.Any(n => n.Count > 0) ? w : null;
    }

    void AddDeck(ShipRt ship, DeckWalk walk)
    {
        foreach (var old in decks.Where(x => x.Ship == ship).ToList()) { old.Root.QueueFree(); decks.Remove(old); }
        var deck = new Deck { Ship = ship, Walk = walk, Root = new Node3D { Name = "Crew" } };
        ship.Node.AddChild(deck.Root);
        decks.Add(deck);
        // how many: the crew size of the ship (Ship.Crew.Quantity) - one sailor per five men, a handful at most; fewer at night
        float crew = F(ship.Chr, "Ship.Crew.Quantity", 20f);
        var usable = Enumerable.Range(0, walk.P.Length).Where(i => walk.Next[i].Count > 0).ToList();
        int n = crew <= 0f ? 0 : Math.Clamp((int)MathF.Ceiling(crew / 5f), 1, Math.Min(MaxSailorsPerShip, Math.Max(1, usable.Count / 3)));
        if (peopleEnt != null && F(peopleEnt.Root, "isNight") != 0f) n = (n + 1) / 2;
        var free = usable.OrderBy(_ => sailorRnd.Next()).ToList();
        for (int k = 0; k < n && k < free.Count; k++)
        {
            var rig = CharacterRig.Create(SailorModels[sailorRnd.Next(SailorModels.Length)], "Lo_Man", null, "Lowcharacters");
            if (rig == null) break;
            var s = new Sailor { Rig = rig, At = free[k], To = free[k], Wait = (float)sailorRnd.NextDouble() * 3f, Yaw = (float)(sailorRnd.NextDouble() * Math.Tau) };
            deck.Root.AddChild(rig);
            rig.Play("stand", true);
            PlaceSailor(deck, s);
            deck.Crew.Add(s);
        }
        GD.Print($"[sea] deck crew of {ship.Chr.Get("id")?.Value}: {deck.Crew.Count} sailors (crew {crew:0}) on a walk graph of {walk.P.Length} points");
    }

    void ClearSailors() { decks.Clear(); }        // the nodes go with the ship nodes

    void TickSailors(float dt)
    {
        if (decks.Count == 0) return;
        var cam = Cam.GlobalPosition;
        bool onDeck = peopleEnt != null && F(peopleEnt.Root, "IsOnDeck") != 0f && F(peopleEnt.Root, "HidePeopleOnDeck") != 0f;
        foreach (var deck in decks.ToList())
        {
            if (!ships.Contains(deck.Ship) || !GodotObject.IsInstanceValid(deck.Root)) { decks.Remove(deck); continue; }
            bool show = deck.Ship.Node.GlobalPosition.DistanceTo(cam) < SailorsViewDist && !(onDeck && deck.Ship.IsPlayer);
            if (show != deck.Shown)
            {
                deck.Shown = show; deck.Root.Visible = show;
                foreach (var s in deck.Crew) s.Rig.ProcessMode = show ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
            }
            if (!show) continue;
            bool war = (int)F(deck.Ship.Chr, "Ship.POS.Mode") == 1;
            foreach (var s in deck.Crew) StepSailor(deck, s, war, dt);
        }
    }

    void StepSailor(Deck deck, Sailor s, bool war, float dt)
    {
        var w = deck.Walk;
        if (s.Moving)
        {
            float len = Math.Max(0.05f, w.P[s.At].DistanceTo(w.P[s.To]));
            float speed = war ? 3.2f : s.Rig.ActionSpeed("walk", 1.3f);
            s.T += speed * dt / len;
            if (s.T >= 1f) { s.Prev = s.At; s.At = s.To; s.T = 0f; s.Moving = false; Arrive(deck, s, war); }
            PlaceSailor(deck, s);
            return;
        }
        if (s.Working)
        {
            // at a cannon in battle the reload loops; when the battle is over (or a work animation ended) walk on
            if (war && w.Type[s.At] == 1) { if (s.Rig.Finished) s.Rig.Play("reload", false, true); return; }
            s.Wait -= dt;
            bool done = s.Rig.ActionName == "stand" ? s.Wait <= 0f : s.Rig.Finished || s.Wait <= 0f;
            if (!done && !war) return;
            s.Working = false; s.Wait = 0f;
        }
        s.Wait -= dt;
        if (s.Wait > 0f && !war) return;
        // battle: the way to the nearest free cannon point; peace: a random neighbour, not straight back if there is another way
        if (war)
        {
            if (s.Route.Count == 0 && w.Type[s.At] != 1) s.Route = RouteToCannon(deck, s);
            if (s.Route.Count > 0) { Go(s, s.Route[0], true); s.Route.RemoveAt(0); return; }
            if (w.Type[s.At] == 1) { s.Working = true; s.Rig.Play("reload", false, true); return; }
        }
        var next = w.Next[s.At].Where(x => x != s.Prev).ToList();
        if (next.Count == 0) next = w.Next[s.At];
        if (next.Count == 0) { s.Wait = 5f; return; }
        Go(s, next[sailorRnd.Next(next.Count)], war);
    }

    void Go(Sailor s, int to, bool run)
    {
        s.To = to; s.T = 0f; s.Moving = true;
        s.Rig.Play(run ? "run" : "walk", true);
    }

    /// <summary>At a point: in peace sometimes stop and do something (pull a rope, look around...), longer at the cannon, mast and hatch points.</summary>
    void Arrive(Deck deck, Sailor s, bool war)
    {
        if (war) return;
        int type = deck.Walk.Type[s.At];
        double chance = type == 0 ? 0.25 : 0.6;
        if (sailorRnd.NextDouble() < chance)
        {
            string act = SailorIdle[sailorRnd.Next(SailorIdle.Length)];
            s.Working = true;
            s.Rig.Play(act, act == "stand", true);
            s.Wait = act == "stand" ? 2f + (float)sailorRnd.NextDouble() * 4f : 8f;
        }
        else s.Rig.Play("stand", true);
    }

    /// <summary>Breadth-first way over the walk graph to the nearest cannon point that no other sailor of the deck has taken.</summary>
    static List<int> RouteToCannon(Deck deck, Sailor me)
    {
        var w = deck.Walk;
        var taken = new HashSet<int>(deck.Crew.Where(c => c != me).Select(c => c.Route.Count > 0 ? c.Route[^1] : c.Moving ? c.To : c.At));
        var from = new int[w.P.Length];
        Array.Fill(from, -2);
        var q = new Queue<int>();
        q.Enqueue(me.At); from[me.At] = -1;
        while (q.Count > 0)
        {
            int v = q.Dequeue();
            if (w.Type[v] == 1 && !taken.Contains(v) && v != me.At)
            {
                var path = new List<int>();
                for (int x = v; x != me.At; x = from[x]) path.Add(x);
                path.Reverse();
                return path;
            }
            foreach (var n in w.Next[v]) if (from[n] == -2) { from[n] = v; q.Enqueue(n); }
        }
        return new List<int>();
    }

    static void PlaceSailor(Deck deck, Sailor s)
    {
        var w = deck.Walk;
        var a = w.P[s.At];
        var p = s.Moving ? a.Lerp(w.P[s.To], s.T) : a;
        if (s.Moving)
        {
            var dir = w.P[s.To] - a;
            float want = MathF.Atan2(dir.X, dir.Z);
            s.Yaw = Mathf.LerpAngle(s.Yaw, want, 0.25f);
        }
        s.Rig.Position = new Vector3(p.X, p.Y, -p.Z);                     // engine -> Godot: z flips; the node is a child of the ship
        s.Rig.Rotation = new Vector3(0f, -s.Yaw, 0f);
    }
}
