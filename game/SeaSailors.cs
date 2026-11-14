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
/// Point types (checked against the walk scripts and sailors.cpp): 0 = deck, 1 = a side cannon, 2 = bow gun, 3 = stern gun, 4..6 = the rigging (feet at the rail, tops): the men climb there with the clip "crawl" (down: "crawl_down").
/// Here: a few sailors per ship walk the graph to free deck points, now and then climb to a top, and in battle mode run to the cannon points and reload. A fallen mast makes the men aloft jump overboard.
/// They are children of the ship node, so they ride its movement and rocking. Far from the camera they are hidden and not animated.
/// </summary>
public partial class SeaView
{
    const int MSG_SHIP_DELETE = 50301, MSG_PEOPLES_ON_SHIP_MASTFALL = 77500;
    const int MaxSailorsPerShip = 6;
    const float SailorsViewDist = 220f;
    static readonly string[] SailorModels = ["Lo_Man_1", "Lo_Man_2", "Lo_Man_3", "Lo_Man_1", "Lo_Man_2", "Lo_Man_Kamzol_1", "Lo_Man_Kamzol_2", "Lo_Man_Kamzol_3"];
    // full-detail bodies (512 px textures) instead of the original low-poly "Lo_Man" men (256 px): they play the man.ani clips under the Lo_Man names the walk logic uses
    static readonly bool LowSailors = System.Environment.GetEnvironmentVariable("SEADOGS_LOWSAILORS") != null;
    static readonly string[] HiSailorModels = ["Sailor1", "Sailor2", "Sailor3", "Sailor4", "Sailor5", "Sailor6", "Sailor1", "Sailor3"];
    static readonly Dictionary<string, string> LoToMan = new()
    {
        ["stand"] = "idle_1", ["action1"] = "idle_2", ["action2"] = "idle_3", ["action3"] = "idle_4", ["action4"] = "idle_5", ["reload"] = "idle_7",
        ["crawl"] = "stairs up", ["crawl_down"] = "stairs down", ["turn_left"] = "turn left", ["turn_right"] = "turn right",
    };
    static readonly string[] SailorIdle = ["action1", "action2", "action3", "action4", "stand"];


    sealed class DeckWalk
    {
        public Vector3[] P = [];          // engine space of the ship model (x right, y up, z forward)
        public int[] Type = [];
        public bool[] Off = [];           // points taken out of the graph (a fallen mast): SailorsPoints disabled
        public List<int>[] Next = [];
    }

    // point types of the PotC graphs (SEA_AI\walk\*_walk.c, the dll's PointType): 0 = deck, 1 = a side cannon, 2 = the bow gun, 3 = the stern gun, 4..6 = the rigging
    // (the shroud feet at the rail, then the tops): the C++ IsMast() is "type >= first mast type", IsCannon() the cannon types
    static bool IsMastPt(int type) => type >= 4;
    static bool IsCannonPt(int type) => type is >= 1 and <= 3;

    sealed class Sailor
    {
        public CharacterRig Rig = null!;
        public int At, To, Prev = -1;
        public float T, Wait, Yaw;
        public bool Moving, Working, Climbing, Falling;
        public Vector3 FallPos, FallVel;
        public List<int> Route = new();     // the way to the target point (a cannon in battle, a mast point or a deck point in peace)
    }

    sealed class Deck
    {
        public ShipRt Ship = null!;
        public DeckWalk Walk = null!;
        public Node3D Root = null!;
        public readonly List<Sailor> Crew = new();
        public bool Shown = true;
    }

    // MOVE_SPEED / RUN_SPEED / CLIMB_SPEED of sailors.cpp are per 100 ms: 1.5, 3 and 0.65 m/s; at war and in a storm the men run and climb twice as fast
    const float SailorRunSpeed = 3f, SailorClimbSpeed = 0.65f;
    /// <summary>The share of walks that go aloft (the C++: 1 in 30); env SEADOGS_MASTCHANCE raises it for tests.</summary>
    static readonly double MastChance = double.TryParse(System.Environment.GetEnvironmentVariable("SEADOGS_MASTCHANCE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mc) ? mc : 1.0 / 30.0;

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
            case MSG_PEOPLES_ON_SHIP_MASTFALL:                                     // "lal": character, mast number (procMastFall in BattleInterface.c)
                if (d.Length > 2 && d[1].Attr is { } mchr && decks.FirstOrDefault(x => ReferenceEquals(x.Ship.Chr, mchr)) is { } mdeck) MastFallOnDeck(mdeck, Conv.ToInt(d[2]));
                return V.Int(1);
        }
        return V.Int(1);
    }

    static DeckWalk? ReadWalk(int count, Slot[]? verts, Slot[]? graph, Slot[]? types)
    {
        if (count <= 1 || verts == null || graph == null || verts.Length < count * 3) return null;
        var w = new DeckWalk { P = new Vector3[count], Type = new int[count], Off = new bool[count], Next = new List<int>[count] };
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
        var usable = Enumerable.Range(0, walk.P.Length).Where(i => walk.Next[i].Count > 0 && !IsMastPt(walk.Type[i])).ToList();
        int n = crew <= 0f ? 0 : Math.Clamp((int)MathF.Ceiling(crew / 5f), 1, Math.Min(MaxSailorsPerShip, Math.Max(1, usable.Count / 3)));
        if (peopleEnt != null && F(peopleEnt.Root, "isNight") != 0f) n = (n + 1) / 2;
        var free = usable.OrderBy(_ => sailorRnd.Next()).ToList();
        for (int k = 0; k < n && k < free.Count; k++)
        {
            var rig = LowSailors ? CharacterRig.Create(SailorModels[sailorRnd.Next(SailorModels.Length)], "Lo_Man", null, "Lowcharacters") : CharacterRig.Create(HiSailorModels[sailorRnd.Next(HiSailorModels.Length)], "man", null);
            if (rig != null && !LowSailors) rig.Alias = LoToMan;
            if (rig == null) break;
            var s = new Sailor { Rig = rig, At = free[k], To = free[k], Wait = (float)sailorRnd.NextDouble() * 3f, Yaw = (float)(sailorRnd.NextDouble() * Math.Tau) };
            deck.Root.AddChild(rig);
            rig.Play("stand", true);
            PlaceSailor(deck, s);
            deck.Crew.Add(s);
        }
        GD.Print($"[sea] deck crew of {ship.Chr.Get("id")?.Value}: {deck.Crew.Count} sailors (crew {crew:0}) on a walk graph of {walk.P.Length} points");
    }

    /// <summary>
    /// A mast went over the side (ShipWalk::SetMastBroken): its rigging points leave the graph, and the men on or heading for them jump overboard (MAN_JUMP).
    /// The PotC graphs mark the rigging by layers (feet at the rail, tops), not by mast, so the points within a few metres of the fallen mast's axis go.
    /// </summary>
    void MastFallOnDeck(Deck deck, int mastNum)
    {
        var m = deck.Ship.Masts.FirstOrDefault(x => x.Num == mastNum);
        if (m == null) return;
        var w = deck.Walk;
        var gone = new HashSet<int>();
        for (int i = 0; i < w.P.Length; i++)
            if (IsMastPt(w.Type[i]) && !w.Off[i] && new Vector2(w.P[i].X - m.Base.X, w.P[i].Z - m.Base.Z).Length() < 6f) gone.Add(i);
        if (gone.Count == 0) return;
        foreach (int i in gone) { w.Off[i] = true; foreach (int n in w.Next[i]) w.Next[n].Remove(i); w.Next[i].Clear(); }
        int jumped = 0;
        foreach (var s in deck.Crew)
        {
            if (s.Falling) continue;
            bool onMast = gone.Contains(s.At) || (s.Moving && gone.Contains(s.To)) || s.Route.Any(gone.Contains);
            if (!onMast) continue;
            var here = s.Moving ? w.P[s.At].Lerp(w.P[s.To], s.T) : w.P[s.At];
            // JUMP_SPEED_X_MASTFALL 3 per 350 ms, away from where the man looks (ang.y + PI): over the rail
            float side = here.X >= 0f ? 1f : -1f;
            s.Falling = true; s.Moving = false; s.Working = false; s.Route.Clear();
            s.FallPos = here; s.FallVel = new Vector3(side * 5f, 1f, 0f);
            s.Rig.Play("stand", true);
            jumped++;
        }
        GD.Print($"[sea] mast {mastNum} fell on the deck of {deck.Ship.Chr.Get("id")?.Value}: {gone.Count} rigging points closed, {jumped} men jumped overboard");
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
            int mode = (int)F(deck.Ship.Chr, "Ship.POS.Mode");          // SHIP_SAIL 0, SHIP_WAR 1, SHIP_STORM 2
            foreach (var s in deck.Crew.ToList()) StepSailor(deck, s, mode, dt);
        }
    }

    void StepSailor(Deck deck, Sailor s, int mode, float dt)
    {
        var w = deck.Walk;
        bool war = mode == 1, hurry = mode != 0;
        if (s.Falling)
        {
            s.FallVel.Y -= 30f * dt;
            s.FallPos += s.FallVel * dt;
            s.Rig.Position = new Vector3(s.FallPos.X, s.FallPos.Y, -s.FallPos.Z);
            if (s.FallPos.Y < -3f) { s.Rig.QueueFree(); deck.Crew.Remove(s); }      // into the sea: the C++ lets him swim a while, then he drowns
            return;
        }
        if (s.Moving)
        {
            float len = Math.Max(0.05f, w.P[s.At].DistanceTo(w.P[s.To]));
            float speed = s.Climbing ? SailorClimbSpeed * (hurry ? 2f : 1f) : hurry ? SailorRunSpeed : s.Rig.ActionSpeed("walk", 1.3f);
            s.T += speed * dt / len;
            if (s.T >= 1f) { s.Prev = s.At; s.At = s.To; s.T = 0f; s.Moving = false; s.Climbing = false; Arrive(deck, s, war); }
            PlaceSailor(deck, s);
            return;
        }
        if (s.Working)
        {
            // at a cannon in battle the reload loops; when the battle is over (or a work animation ended) walk on
            if (war && IsCannonPt(w.Type[s.At])) { if (s.Rig.Finished) s.Rig.Play("reload", false, true); return; }
            s.Wait -= dt;
            bool done = s.Rig.ActionName == "stand" ? s.Wait <= 0f : s.Rig.Finished || s.Wait <= 0f;
            if (!done && !war) return;
            s.Working = false; s.Wait = 0f;
        }
        s.Wait -= dt;
        if (s.Wait > 0f && !war) return;
        if (s.Route.Count == 0) s.Route = PickRoute(deck, s, war);
        if (s.Route.Count > 0) { Go(deck, s, s.Route[0], hurry); s.Route.RemoveAt(0); return; }
        if (war && IsCannonPt(w.Type[s.At])) { s.Working = true; s.Rig.Play("reload", false, true); return; }
        s.Wait = 3f;
    }

    /// <summary>
    /// ShipMan::FindRandomPoint: in battle (or when the guns need loading) the nearest free cannon; in peace now and then (1 in 30) a free rigging point to climb to,
    /// otherwise a free deck point. The way there is a breadth-first search over the graph.
    /// </summary>
    List<int> PickRoute(Deck deck, Sailor me, bool war)
    {
        var w = deck.Walk;
        var taken = new HashSet<int>(deck.Crew.Where(c => c != me && !c.Falling).Select(c => c.Route.Count > 0 ? c.Route[^1] : c.Moving ? c.To : c.At));
        if (war) { if (IsCannonPt(w.Type[me.At])) return new List<int>(); return RouteTo(w, me.At, v => IsCannonPt(w.Type[v]) && !taken.Contains(v), null); }
        bool mast = sailorRnd.NextDouble() < MastChance && !IsMastPt(w.Type[me.At]);
        float deckY = Enumerable.Range(0, w.P.Length).Where(i => w.Type[i] == 0).Select(i => w.P[i].Y).DefaultIfEmpty(0f).Min();
        var cands = Enumerable.Range(0, w.P.Length).Where(i => !w.Off[i] && w.Next[i].Count > 0 && i != me.At && !taken.Contains(i) && (mast ? IsMastPt(w.Type[i]) && w.P[i].Y > deckY + 4f : w.Type[i] == 0)).ToList();
        if (cands.Count == 0) return new List<int>();
        int target = cands[sailorRnd.Next(cands.Count)];
        return RouteTo(w, me.At, v => v == target, null);
    }

    /// <summary>Breadth-first way from a point to the nearest one the predicate accepts; the point itself is not part of the result.</summary>
    static List<int> RouteTo(DeckWalk w, int from, Func<int, bool> goal, Func<int, bool>? skip)
    {
        var prev = new int[w.P.Length];
        Array.Fill(prev, -2);
        var q = new Queue<int>();
        q.Enqueue(from); prev[from] = -1;
        while (q.Count > 0)
        {
            int v = q.Dequeue();
            if (v != from && goal(v))
            {
                var path = new List<int>();
                for (int x = v; x != from; x = prev[x]) path.Add(x);
                path.Reverse();
                return path;
            }
            foreach (var n in w.Next[v]) if (prev[n] == -2 && !w.Off[n] && (skip == null || !skip(n))) { prev[n] = v; q.Enqueue(n); }
        }
        return new List<int>();
    }

    /// <summary>NewAction: onto a rigging point = "crawl" up, off one = "crawl_down" (the same clip backwards); else walk, run in battle and storm.</summary>
    void Go(Deck deck, Sailor s, int to, bool run)
    {
        var w = deck.Walk;
        s.To = to; s.T = 0f; s.Moving = true;
        s.Climbing = IsMastPt(w.Type[to]) || IsMastPt(w.Type[s.At]);
        if (IsMastPt(w.Type[to]) && !IsMastPt(w.Type[s.At])) GD.Print($"[sea] sailor of {deck.Ship.Chr.Get("id")?.Value} climbs up from point {s.At} (type {w.Type[s.At]}) to {to} (type {w.Type[to]}, height {w.P[to].Y:0.0})");
        s.Rig.Play(IsMastPt(w.Type[to]) ? "crawl" : IsMastPt(w.Type[s.At]) ? "crawl_down" : run ? "run" : "walk", true);
    }

    /// <summary>At the end of a way: in peace stop and do something (pull a rope, look around...), longer aloft; the route goes on while there is one.</summary>
    void Arrive(Deck deck, Sailor s, bool war)
    {
        if (war || s.Route.Count > 0) return;
        int type = deck.Walk.Type[s.At];
        double chance = IsMastPt(type) ? 1.0 : type == 0 ? 0.3 : 0.6;
        if (sailorRnd.NextDouble() < chance)
        {
            string act = SailorIdle[sailorRnd.Next(SailorIdle.Length)];
            s.Working = true;
            s.Rig.Play(act, act == "stand", true);
            s.Wait = act == "stand" ? 2f + (float)sailorRnd.NextDouble() * 4f : IsMastPt(type) ? 4f : 8f;
            if (IsMastPt(type) && act != "stand") s.Wait = 5f + (float)sailorRnd.NextDouble() * 5f;
        }
        else s.Rig.Play("stand", true);
    }

    static void PlaceSailor(Deck deck, Sailor s)
    {
        var w = deck.Walk;
        var a = w.P[s.At];
        var p = s.Moving ? a.Lerp(w.P[s.To], s.T) : a;
        if (s.Moving)
        {
            var dir = w.P[s.To] - a;
            if (new Vector2(dir.X, dir.Z).LengthSquared() > 1e-4f)                // straight up a ladder: keep the heading
            {
                float want = MathF.Atan2(dir.X, dir.Z);
                s.Yaw = Mathf.LerpAngle(s.Yaw, want, 0.25f);
            }
        }
        s.Rig.Position = new Vector3(p.X, p.Y, -p.Z);                     // engine -> Godot: z flips; the node is a child of the ship
        s.Rig.Rotation = new Vector3(0f, -s.Yaw, 0f);
    }
}
