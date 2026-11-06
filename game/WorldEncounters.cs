using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Formats;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Ship encounters of the world map. The script decides when and what (wdmCreateMerchantShip / FollowShip / WarringShips -> messages 311xx to the worldmap class);
/// the engine moves them like worldmap/src/wdm_enemy_ship.cpp, wdm_merchant_ship.cpp, wdm_follow_ship.cpp, wdm_warring_ship.cpp:
///  * a merchant sails to a port of a (named or random) island, round the coast, and fades out when it arrives; it never attacks, the player has to come within 16 units and press Enter;
///  * a follow ship (pirates, hunters, war squadrons: the script's "war" encounters) steers for the player, attacks when closer than 16 units (the action radius) and can be skipped by the script;
///  * a warring pair stands still, firing at each other (smoke), and is met on Enter like a merchant;
///  * all ships are born ahead of the player at 65..140 units (GeneratePosition), grow visible for 3 s, fade with the distance to the player (view distances), live 60..180 s unless the
///    script says otherwise, die farther than enemyshipDistKill from the player, push away from each other and from the coast.
/// The engine raises WorldMap_ShipEncounter and exposes the ships through worldMap.encounter.* (writing encounter.cur selects the ship whose x/z/ay/type/select/time/attack are then readable).
/// </summary>
public partial class WorldMapView
{
    enum EncKind { Merchant, Follow, War }

    sealed class Puff { public Sprite3D Node = null!; public Vector3 V; public float Time, KTime, DAng; }

    sealed class Enc
    {
        public int Type;                 // slot in the script's MapEncounters
        public EncKind Kind;
        public string Model = "";
        public float X, Z, Ay, Speed, Life = 120f;       // Speed = the script's kSpeed (WdmShip::kMaxSpeed); Life = seconds to live
        public float Vel, TurnSpd;                       // the current speed and turn rate (map units/s, rad/s)
        public Enc? Partner;
        public bool Attacker, Selected, Reported, Enemy, CanSkip, Dead;
        public float DestX, DestZ, DestRad = 12f;   // a path starts "old" so the first steer plans it
        public string From = "", To = "", Why = "";
        public List<(float x, float z)>? Path; public int PathIdx; public float PathAge = 99f, StuckTime;
        public float BrnAlpha, DeleteAlpha = 1f, Alpha;
        public float Age, LogTimer, LastDist;
        public float PuffTimer = 5f; public readonly List<Puff> Puffs = new();
        public Node3D Node = null!;
        public float StartX, StartZ;
    }

    static readonly Random Rng = int.TryParse(System.Environment.GetEnvironmentVariable("SEADOGS_SEED"), out var seed) ? new Random(seed) : new Random();   // SEADOGS_SEED: reproducible encounters for tests
    static readonly bool NoKill = System.Environment.GetEnvironmentVariable("SEADOGS_MAPNOKILL") != null;          // test: ships neither die far from the player nor of age (long-run route checks)
    static readonly bool MapLog = System.Environment.GetEnvironmentVariable("SEADOGS_MAPLOG") != null;
    readonly List<Enc> encs = new();
    bool encHooked;
    Enc? nearShip;                        // WdmObjects::enemyShip: the ship Enter will meet
    Texture2D? warSmokeTex;

    // counters for the logs / tests
    int encCreated, encArrived, encExpired, encFar, encMet;

    V OnMessage(V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        string S(int i) => i < d.Length ? Conv.ToStr(d[i]) : "";
        float Fl(int i) => i < d.Length ? Conv.ToFloat(d[i]) : 0f;
        int I(int i) => i < d.Length ? Conv.ToInt(d[i]) : 0;
        if (!built) return V.Int(0);
        switch (msg)
        {
            case 31000: return V.Int(CreateStorm(-1f) ? 1 : 0);
            case 31001: return V.Int(CreateStorm(Math.Max(1f, Fl(1))) ? 1 : 0);
            case 31100: case 31101: return V.Int(Spawn(EncKind.Merchant, I(1), S(2), S(3), Fl(4), msg == 31101 ? Fl(5) : -1f) ? 1 : 0);
            case 31110: case 31111: return V.Int(Spawn(EncKind.Follow, I(1), S(2), "", Fl(3), msg == 31111 ? Fl(4) : -1f) ? 1 : 0);
            case 31120: case 31121: return V.Int(SpawnWar(I(1), S(2), I(3), S(4), msg == 31121 ? Fl(5) : -1f) ? 1 : 0);
            case 31200: ClearEncounters(false); return V.Int(1);
        }
        return V.Int(0);
    }

    void HookEncounterAttrs()
    {
        encHooked = true;
        Root!.GetOrCreate("encounter").GetOrCreate("cur").OnWrite = _ => PublishCurrent();
    }

    /// <summary>The script wrote encounter.cur: fill the read-only fields of that ship.</summary>
    void PublishCurrent()
    {
        var root = Root; if (root == null) return;
        var e = root.GetOrCreate("encounter");
        int cur = (int)Conv.ParseFloatPrefix(e.Get("cur")?.Value);
        e.GetOrCreate("num").Value = encs.Count.ToString();
        if (cur < 0 || cur >= encs.Count) return;
        var c = encs[cur];
        e.GetOrCreate("x").Value = Conv.FloatToConcat(c.X); e.GetOrCreate("z").Value = Conv.FloatToConcat(c.Z); e.GetOrCreate("ay").Value = Conv.FloatToConcat(c.Ay);
        e.GetOrCreate("type").Value = c.Type.ToString(); e.GetOrCreate("select").Value = c.Selected ? "1" : "0";
        e.GetOrCreate("time").Value = Conv.FloatToConcat(c.Life);
        e.GetOrCreate("attack").Value = c.Partner != null && c.Attacker ? encs.IndexOf(c.Partner).ToString() : "-1";
    }

    /// <summary>WdmShip::SetMaxSpeed: 0..5 (the script's own factors are never below 0.1 here).</summary>
    static float KSpeed(float k) => Math.Clamp(k, 0.1f, 5f);

    string NearestIsland(float x, float z)
    {
        string best = ""; float bd = float.MaxValue;
        if (Root?.Get("islands") is { } isls)
            foreach (var i in isls.Children)
            {
                if (F(i, "radius") <= 0f) continue;
                float dx = F(i, "position.rx") - x, dz = F(i, "position.rz") - z, d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = i.Name; }
            }
        return best;
    }

    Enc Make(EncKind kind, int type, string model, float x, float z, float ay, float speed, float life)
    {
        var node = new Node3D { Name = "Enc" + type };
        string f = GameAssets.Resource("MODELS\\WorldMap\\" + (model.Length > 0 ? model : "ship") + ".gm");
        if (!File.Exists(f)) f = GameAssets.Resource("MODELS\\WorldMap\\ship.gm");
        if (File.Exists(f)) node.AddChild(ModelFactory.Build(GmFile.Parse(File.ReadAllBytes(f)), "EncShip", "WorldMap\\geometry", null));
        node.Visible = false;               // born invisible: faded in over 3 s
        scene!.AddChild(node);
        var e = new Enc
        {
            Type = type, Kind = kind, Model = model, X = x, Z = z, Ay = ay, Speed = speed, Node = node, StartX = x, StartZ = z,
            Enemy = kind == EncKind.Follow,
            Life = life >= 0f ? life : 60f + (float)Rng.NextDouble() * 120f,        // WDM_ENEMYSHIP_MINTIME..MAXTIME
        };
        encs.Add(e); encCreated++;
        Root!.GetOrCreate("encounter").GetOrCreate("num").Value = encs.Count.ToString();
        return e;
    }

    /// <summary>WdmShip::CheckPosition: inside the world, away from the other ships and off the coast.</summary>
    bool CheckPosition(float x, float z, float r)
    {
        if (land != null)
        {
            if (x - r < land.WorldMinX || x + r > land.WorldMaxX || z - r < land.WorldMinZ || z + r > land.WorldMaxZ) return false;
            if (land.IsLand(x, z) || land.IsLand(x + r, z) || land.IsLand(x - r, z) || land.IsLand(x, z + r) || land.IsLand(x, z - r)) return false;
        }
        foreach (var o in encs) if (!o.Dead && (o.X - x) * (o.X - x) + (o.Z - z) * (o.Z - z) < 6.3f * 6.3f) return false;
        return true;
    }

    /// <summary>WdmEnemyShip::GeneratePosition: 32 angles within brnDltAng round the player's heading x 8 rings between enemyshipBrnDistMin and Max; the first free one.</summary>
    bool GeneratePosition(float objRadius, float brnDltAng, out float x, out float z)
    {
        float brnMin = F(Root, "enemyshipBrnDistMin", 40f), brnMax = F(Root, "enemyshipBrnDistMax", 70f);
        var field = new byte[32];
        x = z = 0f;
        while (true)
        {
            int ang = Rng.Next(32);
            if (field[ang] != 0xff)
            {
                float angle = shipAy + brnDltAng * (0.5f - ang / 31f);
                int rad = Rng.Next(8);
                while ((field[ang] & (1 << rad)) != 0) rad = Rng.Next(8);
                float radius = brnMin + (brnMax - brnMin) * rad / 7f;
                x = shipX + radius * MathF.Sin(angle); z = shipZ + radius * MathF.Cos(angle);
                if (CheckPosition(x, z, objRadius)) return true;
                field[ang] |= (byte)(1 << rad);
            }
            if (field.All(b => b == 0xff)) return false;
        }
    }

    /// <summary>The ports a merchant can sail to: the town / fort markers of the islands (the original has "merchant" locators in Islands.gm; this game's file has none).</summary>
    List<(string island, float x, float z)> MerchantGoals(string island)
    {
        var res = new List<(string, float, float)>();
        if (Root?.Get("islands") is not { } isls) return res;
        foreach (var isl in isls.Children)
        {
            if (F(isl, "radius") <= 0f) continue;
            if (island.Length > 0 && !isl.Name.Equals(island, StringComparison.OrdinalIgnoreCase)) continue;
            int n = 0;
            if (isl.Get("locations") is { } locs)
                foreach (var loc in locs.Children)
                {
                    if (loc.Get("visible")?.Value == "false") continue;
                    string mn = (loc.Get("modelName")?.Value ?? "").ToLowerInvariant();
                    if (mn != "town" && mn != "fort" && mn != "anchor" && mn.Length > 0 && !mn.Contains("town")) continue;
                    res.Add((isl.Name, F(loc, "position.x"), F(loc, "position.z"))); n++;
                }
            if (n == 0) res.Add((isl.Name, F(isl, "position.rx"), F(isl, "position.rz")));
        }
        return res;
    }

    bool Spawn(EncKind kind, int type, string model, string island, float kSpeed, float life)
    {
        if (kind == EncKind.Merchant)
        {
            if (!GeneratePosition(1.2f * 6.3f, 2f, out float x, out float z)) return false;
            var goals = MerchantGoals(island);
            if (goals.Count == 0) return false;
            (string island, float x, float z) g = goals[0];
            List<(float x, float z)>? path = null;
            for (int tries = 0; tries < 10; tries++)           // a port the sea leads to
            {
                g = goals[Rng.Next(goals.Count)];
                if (land == null) break;
                var (sx, sz) = land.NearestSea(g.x, g.z); g = (g.island, sx, sz);
                if ((g.x - x) * (g.x - x) + (g.z - z) * (g.z - z) < 150f * 150f) continue;             // not the port next door
                path = land.FindPath(x, z, g.x, g.z);
                if (path != null) break;
            }
            if (land != null && path == null) return false;
            var e = Make(kind, type, model, x, z, MathF.Atan2(g.x - x, g.z - z), KSpeed(kSpeed), life);
            e.DestX = g.x; e.DestZ = g.z; e.Path = path; e.PathAge = 0f; e.From = NearestIsland(x, z); e.To = g.island;
            if (MapLog) GD.Print($"[map] enc#{encs.IndexOf(e)} +merchant type {type} model '{model}' speed {e.Speed:0.00} at ({x:0},{z:0}) near {e.From}, bound for {e.To} ({g.x:0},{g.z:0}), life {e.Life:0} s");
        }
        else
        {
            if (!GeneratePosition(1.2f * 6.3f, 6.28f, out float x, out float z)) return false;
            var e = Make(kind, type, model, x, z, MathF.Atan2(shipX - x, shipZ - z), KSpeed(kSpeed), life);
            e.CanSkip = Conv.ToInt(it.FireEvent("WorldMap_IsSkipEnable")) != 0;
            e.From = NearestIsland(x, z);
            if (MapLog) GD.Print($"[map] enc#{encs.IndexOf(e)} +follow type {type} model '{model}' speed {e.Speed:0.00} at ({x:0},{z:0}) near {e.From}, life {e.Life:0} s, canSkip {e.CanSkip}");
        }
        return true;
    }

    bool SpawnWar(int t1, string m1, int t2, string m2, float life)
    {
        // WorldMap::CreateWarringShips: the two stand side by side (broadsides to each other) moveRadius from the centre
        float moveRadius = (6.3f + 6.3f) * (0.4f + Rng.Next(4) * (0.1f / 3f)), fullRadius = 0.6f * (moveRadius + 2f * 6.3f);
        if (!GeneratePosition(fullRadius, 1.5f, out float x, out float z)) return false;
        float angl = (float)Rng.NextDouble() * MathF.Tau, dx = moveRadius * MathF.Cos(angl), dz = -moveRadius * MathF.Sin(angl);
        float l = life >= 0f ? Math.Max(life, 1f) : 60f + (float)Rng.NextDouble() * 120f;
        var e1 = Make(EncKind.War, t1, m1, x + dx, z + dz, angl + MathF.PI * Rng.Next(2), 1f, l);
        var e2 = Make(EncKind.War, t2, m2, x - dx, z - dz, angl + MathF.PI * Rng.Next(2), 1f, l);
        e1.Partner = e2; e2.Partner = e1; e1.Attacker = true; e1.From = e2.From = NearestIsland(x, z);
        if (MapLog) GD.Print($"[map] enc#{encs.IndexOf(e1)}+{encs.IndexOf(e2)} +warring pair types {t1}/{t2} models '{m1}'/'{m2}' at ({x:0},{z:0}) near {e1.From}, life {l:0} s");
        return true;
    }

    void ClearEncounters(bool notify)
    {
        foreach (var e in encs) { e.Node.QueueFree(); if (notify) it.FireEvent("WorldMap_DeleteShipEncounter", new List<V> { V.Int(e.Type) }); }
        encs.Clear(); nearShip = null;
        if (Root != null) Root.GetOrCreate("encounter").GetOrCreate("num").Value = "0";
    }

    void RemoveEnc(Enc e, string why)
    {
        if (e.Dead) return;
        e.Dead = true;
        if (MapLog) GD.Print($"[map] enc#{encs.IndexOf(e)} -{e.Kind} type {e.Type}: {why} at ({e.X:0},{e.Z:0}) after {e.Age:0.0} s, travelled {MathF.Sqrt((e.X - e.StartX) * (e.X - e.StartX) + (e.Z - e.StartZ) * (e.Z - e.StartZ)):0} units from {e.From} {(e.To.Length > 0 ? "to " + e.To : "")}");
        if (nearShip == e) nearShip = null;
        e.Node.QueueFree(); encs.Remove(e);
        Root!.GetOrCreate("encounter").GetOrCreate("num").Value = encs.Count.ToString();
        it.FireEvent("WorldMap_DeleteShipEncounter", new List<V> { V.Int(e.Type) });
    }

    // ------------------------------------------------------------------ movement
    const float ShipTSpeed = 1f, ShipSpeedK = 7.5f;

    /// <summary>The way to a point: straight when the sea is free, else round the islands along a path that is renewed when it is old (WdmIslands::FindDirection).</summary>
    (float x, float z) SteerTo(Enc e, float tx, float tz, float renew)
    {
        if (land != null)
        {
            if (e.PathAge > renew)
            {
                e.Path = land.FindPath(e.X, e.Z, tx, tz); e.PathIdx = 0; e.PathAge = 0f;
                if (e.Path == null && MapLog) GD.Print($"[map] enc#{encs.IndexOf(e)} no sea way to ({tx:0},{tz:0}): straight on");
            }
            if (e.Path != null)
            {
                while (e.PathIdx < e.Path.Count - 1 && (e.Path[e.PathIdx].x - e.X) * (e.Path[e.PathIdx].x - e.X) + (e.Path[e.PathIdx].z - e.Z) * (e.Path[e.PathIdx].z - e.Z) < 14f * 14f) e.PathIdx++;
                var w = e.Path[Math.Min(e.PathIdx, e.Path.Count - 1)];
                tx = w.x; tz = w.z;
            }
        }
        float dx = tx - e.X, dz = tz - e.Z, l = MathF.Sqrt(dx * dx + dz * dz);
        return l > 1e-4f ? (dx / l, dz / l) : (0f, 0f);
    }

    /// <summary>WdmEnemyShip::Move: the wanted direction + 1.5 x the push of the islands + 1.1 x the push of the other ships turns the ship (and brakes it when it is behind);
    /// WdmShip::ShipUpdate: limits, the wind, the move along the old heading, the turn, the braking.</summary>
    void MoveEnc(Enc e, float dt)
    {
        if (e.Kind == EncKind.War) return;                                  // WdmWarringShip::Move is empty: the pair stands still
        float mx, mz;
        if (e.Kind == EncKind.Merchant) (mx, mz) = SteerTo(e, e.DestX, e.DestZ, 6f);
        else (mx, mz) = SteerTo(e, shipX, shipZ, 1.5f);
        float ix = 0f, iz = 0f;
        if (land != null) { var (rx, rz) = land.Repulsion(e.X, e.Z); ix = rx * 1.5f; iz = rz * 1.5f; }
        float sx = 0f, sz = 0f;
        foreach (var o in encs)
        {
            if (o == e || o.Dead) continue;
            float fx = e.X - o.X, fz = e.Z - o.Z, fl = fx * fx + fz * fz - 25f * 25f;
            if (fl > 25f * 25f) continue;
            if (fl < 0.1f) fl = 0.1f;
            fl = 0.2f / MathF.Sqrt(fl);
            fx *= fl; fz *= fl; sx += fx; sz += fz;
            fx *= MathF.Cos(o.Ay); fz *= MathF.Sin(o.Ay);          // the deviation: pass on the right side (Vz of the other ship)
            sx += -fz * 0.01f; sz += fx * 0.01f;
        }
        if (e.Kind == EncKind.Follow)                                      // isLookOnPlayer is false for the follow ship: the player does not push it away
        { }
        float sl = sx * sx + sz * sz;
        if (sl > 1f) { sl = 1f / MathF.Sqrt(sl); sx *= sl; sz *= sl; }
        float dx = mx + 1.5f * ix + 1.1f * sx, dz = mz + 1.5f * iz + 1.1f * sz, dl = dx * dx + dz * dz;
        float vx = MathF.Sin(e.Ay), vz = MathF.Cos(e.Ay);
        float sn = vz * dx - vx * dz;
        if (dl > 0f) sn /= MathF.Sqrt(dl);
        float cs = vx * dx + vz * dz;
        if (cs < 0f) sn = sn < 0f ? -1f : 1f;
        e.TurnSpd += 2f * ShipTSpeed * sn * dt;
        if (cs >= 0f) e.Vel += e.Speed * 2.5f * ShipSpeedK * dt;
        else if (e.Vel > 0.1f) e.Vel = Math.Max(0.1f, e.Vel - e.Speed * 2.5f * ShipSpeedK * dt);
        float slowing = e.DeleteAlpha < 1f ? Math.Max(e.DeleteAlpha, 0f) : 1f;     // a dying ship slows down
        e.Vel *= slowing; e.TurnSpd *= slowing;
        // ShipUpdate
        e.Vel = Math.Clamp(e.Vel, -0.4f * 20f * e.Speed, 20f * e.Speed); e.TurnSpd = Math.Clamp(e.TurnSpd, -0.65f, 0.65f);
        float move = e.Vel * WindSpeedFactor(e.X, e.Z, e.Ay) * dt;
        float nx = e.X + MathF.Sin(e.Ay) * move, nz = e.Z + MathF.Cos(e.Ay) * move;
        e.Ay += e.TurnSpd * dt;
        float k = Math.Min(1.25f * dt, 1f);
        e.TurnSpd -= e.TurnSpd * k; e.Vel -= e.Vel * k;
        if (land != null && land.IsLand(nx, nz)) { e.Vel = 0f; e.StuckTime += dt; }   // a move that would end on land is refused
        else { e.X = nx; e.Z = nz; e.StuckTime = Math.Max(0f, e.StuckTime - dt); }
        if (land != null) { e.X = Math.Clamp(e.X, land.WorldMinX, land.WorldMaxX); e.Z = Math.Clamp(e.Z, land.WorldMinZ, land.WorldMaxZ); }
        if (e.StuckTime > 2f) { e.Path = null; e.StuckTime = 0f; e.PathAge = 99f; }           // wedged against the coast: plan the way again
    }

    void SetAlpha(Enc e, float a)
    {
        if (MathF.Abs(a - e.Alpha) < 0.02f && !(a <= 0f != (e.Alpha <= 0f))) return;
        e.Alpha = a;
        e.Node.Visible = a > 0.01f;
        if (!e.Node.Visible) return;
        float t = 1f - a;
        void Walk(Node n) { if (n is GeometryInstance3D g) g.Transparency = t; foreach (var c in n.GetChildren()) Walk(c); }
        Walk(e.Node);
    }

    void TickEncounters(float dt)
    {
        if (!encHooked) HookEncounterAttrs();
        float viewMin = F(Root, "enemyshipViewDistMin", 20f), viewMax = F(Root, "enemyshipViewDistMax", 50f), kill = NoKill ? 1e9f : F(Root, "enemyshipDistKill", 100f);
        foreach (var e in encs.ToList())
        {
            if (e.Dead) continue;
            e.Age += dt; e.PathAge += dt;
            for (float rest = Math.Min(dt, 0.5f); rest > 0f; rest -= 0.1f) MoveEnc(e, Math.Min(rest, 0.1f));
            e.Node.Position = new Vector3(e.X, 0.5f, -e.Z); e.Node.Rotation = new Vector3(0, -e.Ay, 0);
            float d = MathF.Sqrt((e.X - shipX) * (e.X - shipX) + (e.Z - shipZ) * (e.Z - shipZ));
            e.LastDist = d;
            // visibility: with the distance to the player, 3 s to appear, the fade of a dying ship
            e.BrnAlpha = Math.Min(1f, e.BrnAlpha + dt / 3f);
            SetAlpha(e, Math.Clamp(1f - (d - viewMin) / (viewMax - viewMin), 0f, 1f) * e.BrnAlpha * Math.Max(e.DeleteAlpha, 0f));
            if (e.Kind == EncKind.War) UpdateSmoke(e, dt);
            // the end of the life
            if (e.DeleteAlpha >= 1f)
            {
                string why = "";
                if (d > kill) { why = "too far from the player"; encFar++; }
                e.Life -= dt;
                if (why.Length == 0 && e.Life < 0f) { why = "lifetime over"; encExpired++; }
                if (why.Length == 0 && e.Kind == EncKind.Merchant && (e.DestX - e.X) * (e.DestX - e.X) + (e.DestZ - e.Z) * (e.DestZ - e.Z) <= e.DestRad * e.DestRad) { why = "arrived at " + e.To; encArrived++; }
                if (why.Length > 0)
                {
                    e.DeleteAlpha = 0.999999f; e.Why = why;
                    if (e.Partner != null && e.Partner.DeleteAlpha >= 1f) e.Partner.DeleteAlpha = 0.999999f;
                    if (MapLog) GD.Print($"[map] enc#{encs.IndexOf(e)} {e.Kind} type {e.Type}: {why}, fading out");
                }
            }
            else
            {
                e.DeleteAlpha -= 0.5f * dt;                                 // WDM_ENEMYSHIP_DELSPD
                if (e.DeleteAlpha < 0f) { RemoveEnc(e, e.Why); continue; }
            }
            if (MapLog && (e.LogTimer += dt) >= 10f)
            {
                e.LogTimer = 0f;
                string goal = e.Kind == EncKind.Merchant ? $" goal {e.To} ({e.DestX:0},{e.DestZ:0}) dist {MathF.Sqrt((e.DestX - e.X) * (e.DestX - e.X) + (e.DestZ - e.Z) * (e.DestZ - e.Z)):0} waypoints {(e.Path?.Count ?? 0)}" : "";
                GD.Print($"[maplog] enc#{encs.IndexOf(e)} {e.Kind} type {e.Type} age {e.Age:0} pos ({e.X:0},{e.Z:0}) ay {e.Ay:0.00} vel {e.Vel:0.0} life {e.Life:0} alpha {e.Alpha:0.00} to player {d:0}{goal}");
            }
        }
        TestPlayerAgainstShips();
    }

    /// <summary>WdmPlayerShip::Update: the follow ships attack within the action radius (16 units); other ships only become "near" (Enter meets them).</summary>
    void TestPlayerAgainstShips()
    {
        const float ActionRadius = 16f;
        nearShip = null;
        foreach (var e in encs.ToList())
        {
            if (e.Dead) continue;
            float r = (e.X - shipX) * (e.X - shipX) + (e.Z - shipZ) * (e.Z - shipZ);
            if (e.Enemy)
            {
                if (r < ActionRadius * ActionRadius * 6f)
                {
                    if (r < ActionRadius * ActionRadius)
                    {
                        if (e.Reported) continue;
                        e.Reported = true;
                        // the script may allow skipping the encounter (WorldMap_IsSkipEnable; the addon always says no): then the map asks first
                        if (e.CanSkip)
                        {
                            GD.Print($"[map] encounter with ship type {e.Type}: skip offered");
                            ui.OpenEvent("Вітрила на обрії!# Можна ухилитися від зустрічі.", "Зустріти", "Ухилитися", res => { if (res == 0 && encs.Contains(e)) ShipEncounter(e); });
                            return;
                        }
                        ShipEncounter(e);
                        return;      // the script may have deleted the map
                    }
                    nearShip ??= e;
                }
                else e.Reported = false;
            }
            else if (r < ActionRadius * ActionRadius && (nearShip == null || !nearShip.Enemy)) nearShip = e;
        }
    }

    void ShipEncounter(Enc e)
    {
        // the player's ship touched it: the ship (and its opponent) take part in the battle
        e.Selected = true; if (e.Partner != null) e.Partner.Selected = true;
        encMet++;
        GD.Print($"[map] encounter with ship type {e.Type} ({e.Kind}, model '{e.Model}', {e.LastDist:0} units away)");
        it.FireEvent("WorldMap_ShipEncounter", new List<V> { V.Float(shipX), V.Float(shipZ), V.Float(shipAy), V.Int(encs.IndexOf(e)) });
    }

    /// <summary>WdmPlayerShip::ExitFromMap: Enter meets the ship that is near (within 16 units); false when there is none (the map is left to the sea / island).</summary>
    bool MeetNearShip()
    {
        if (nearShip == null || nearShip.Dead || !encs.Contains(nearShip)) return false;
        ShipEncounter(nearShip);
        return true;
    }

    // ------------------------------------------------------------------ the smoke of the gunfight (wdm_warring_ship.cpp)
    void UpdateSmoke(Enc e, float dt)
    {
        warSmokeTex ??= GameAssets.Texture("WorldMap\\csmoke.tga");
        e.PuffTimer += dt;
        if (e.Puffs.Count == 0 && Rng.NextDouble() * 20.0 < e.PuffTimer - 5f)
        {
            e.PuffTimer = 0f;
            for (int i = 0; i < 12; i++)
            {
                float side = i < 6 ? 1f : -1f;
                var sp = new Sprite3D { Texture = warSmokeTex, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.02f, Shaded = false, Name = "Smoke" };
                var heading = new Vector3(MathF.Sin(e.Ay), 0f, -MathF.Cos(e.Ay));
                var right = new Vector3(MathF.Cos(e.Ay), 0f, MathF.Sin(e.Ay));
                var p = new Puff
                {
                    Node = sp, V = right * side * 30f * (0.6f + (float)Rng.NextDouble() * 0.4f), KTime = 1f / (2f + (float)Rng.NextDouble() * 2f), DAng = 0.3f * (1f - (float)Rng.NextDouble() * 2f),
                };
                scene!.AddChild(sp);
                sp.Position = new Vector3(e.X, 2f, -e.Z) + heading * 2.5f * (1f - (float)Rng.NextDouble() * 2f);
                e.Puffs.Add(p);
            }
        }
        for (int i = e.Puffs.Count - 1; i >= 0; i--)
        {
            var p = e.Puffs[i];
            p.Time += p.KTime * dt;
            if (p.Time >= 1f) { p.Node.QueueFree(); e.Puffs.RemoveAt(i); continue; }
            float k = Math.Max(0f, 1f - 12f * p.Time);
            p.Node.Position += p.V * k * k * dt;
            p.Node.PixelSize = 0.02f * 3f * (0.5f + p.Time * p.Time * 0.9f) / 3f;
            float a = MathF.Sqrt(p.Time); a = 1f - 4f * (a - 0.5f) * (a - 0.5f);
            p.Node.Modulate = new Color(1, 1, 1, Math.Clamp(a * 0.3f * e.Alpha, 0f, 1f));
        }
    }

    /// <summary>Test hook --mapmeet@N: put the player's ship next to the first encounter ship (false while there is none yet).</summary>
    public bool TestMeet()
    {
        if (!built || encs.Count == 0) return false;
        var e = encs[0];
        if (System.Environment.GetEnvironmentVariable("SEADOGS_MEETFOLLOW") == "1")       // quest tests: skip the merchants and meet the pursuer (the quest war encounters are made as pursuers)
        {
            var f = encs.FirstOrDefault(x => x.Type == 1);
            if (f == null) return false;
            e = f;
        }
        shipX = e.X + 4f; shipZ = e.Z + 4f;
        GD.Print($"[map] test: ship moved next to encounter type {e.Type} at ({e.X:0},{e.Z:0})");
        PublishShip();
        return true;
    }

    /// <summary>Test hook: ask the script for a merchant ship next to the player.</summary>
    public void TestEncounter(bool follow)
    {
        if (!built) return;
        GD.Print("[map] test encounter: asking the script for a " + (follow ? "pursuer" : "merchant"));
        it.CallTop(follow ? "wdmCreateFollowShip" : "wdmCreateMerchantShip", follow ? new[] { V.Int(0), V.Float(0f) } : new[] { V.Int(0), V.Str(""), V.Float(0f) });
    }
}
