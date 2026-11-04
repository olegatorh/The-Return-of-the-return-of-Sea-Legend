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
/// the engine moves them, removes them when far, raises WorldMap_ShipEncounter when the player's ship touches one and exposes them through worldMap.encounter.*
/// (writing encounter.cur selects the ship whose x/z/ay/type/select/time/attack are then readable).
/// </summary>
public partial class WorldMapView
{
    enum EncKind { Merchant, Follow, War }

    sealed class Enc
    {
        public int Type;                 // slot in the script's MapEncounters
        public EncKind Kind;
        public float X, Z, Ay, Speed, Life = -1f;
        public Enc? Partner;
        public bool Attacker, Selected, Reported;
        public float DestX, DestZ;
        public Node3D Node = null!;
    }

    static readonly Random Rng = int.TryParse(System.Environment.GetEnvironmentVariable("SEADOGS_SEED"), out var seed) ? new Random(seed) : new Random();   // SEADOGS_SEED: reproducible encounters for tests
    readonly List<Enc> encs = new();
    bool encHooked;

    V OnMessage(V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        string S(int i) => i < d.Length ? Conv.ToStr(d[i]) : "";
        float Fl(int i) => i < d.Length ? Conv.ToFloat(d[i]) : 0f;
        int I(int i) => i < d.Length ? Conv.ToInt(d[i]) : 0;
        if (!built) return V.Int(0);
        switch (msg)
        {
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
        e.GetOrCreate("time").Value = Conv.FloatToConcat(c.Life < 0 ? 1000f : c.Life);
        e.GetOrCreate("attack").Value = c.Partner != null && c.Attacker ? encs.IndexOf(c.Partner).ToString() : "-1";
    }

    float PlayerMapSpeed() => SeaSpeedRate() * 0.7f;

    Enc Make(EncKind kind, int type, string model, float x, float z, float speed, float life)
    {
        var node = new Node3D { Name = "Enc" + type };
        string f = GameAssets.Resource("MODELS\\WorldMap\\" + (model.Length > 0 ? model : "ship") + ".gm");
        if (!File.Exists(f)) f = GameAssets.Resource("MODELS\\WorldMap\\ship.gm");
        if (File.Exists(f)) node.AddChild(ModelFactory.Build(GmFile.Parse(File.ReadAllBytes(f)), "EncShip", "WorldMap\\geometry", null));
        scene!.AddChild(node);
        var e = new Enc { Type = type, Kind = kind, X = x, Z = z, Speed = speed, Life = life, Node = node };
        encs.Add(e);
        Root!.GetOrCreate("encounter").GetOrCreate("num").Value = encs.Count.ToString();
        return e;
    }

    bool Spawn(EncKind kind, int type, string model, string island, float kSpeed, float life)
    {
        var rnd = Rng;
        float brnMin = F(Root, "enemyshipBrnDistMin", 65f), brnMax = F(Root, "enemyshipBrnDistMax", 140f);
        float a = (float)rnd.NextDouble() * MathF.Tau, r = brnMin + (float)rnd.NextDouble() * (brnMax - brnMin);
        float x = shipX + MathF.Sin(a) * r, z = shipZ + MathF.Cos(a) * r;
        var e = Make(kind, type, model, x, z, PlayerMapSpeed() * kSpeed, life);
        if (kind == EncKind.Merchant)
        {
            // sail to the named island, or to a random one
            var isls = Root!.Get("islands")!.Children.Where(i => F(i, "radius") > 0f).ToList();
            var tgt = isls.FirstOrDefault(i => i.Name.Equals(island, StringComparison.OrdinalIgnoreCase)) ?? isls[rnd.Next(isls.Count)];
            e.DestX = F(tgt, "position.rx"); e.DestZ = F(tgt, "position.rz");
            e.Ay = MathF.Atan2(e.DestX - e.X, e.DestZ - e.Z);
        }
        else e.Ay = MathF.Atan2(shipX - e.X, shipZ - e.Z);
        return true;
    }

    bool SpawnWar(int t1, string m1, int t2, string m2, float life)
    {
        var rnd = Rng;
        float brnMin = F(Root, "enemyshipBrnDistMin", 65f), brnMax = F(Root, "enemyshipBrnDistMax", 140f);
        float a = (float)rnd.NextDouble() * MathF.Tau, r = brnMin + (float)rnd.NextDouble() * (brnMax - brnMin);
        float x = shipX + MathF.Sin(a) * r, z = shipZ + MathF.Cos(a) * r;
        var e1 = Make(EncKind.War, t1, m1, x, z, 0f, life); var e2 = Make(EncKind.War, t2, m2, x + 7f, z + 5f, 0f, life);
        e1.Partner = e2; e2.Partner = e1; e1.Attacker = true;
        e1.Ay = MathF.Atan2(e2.X - e1.X, e2.Z - e1.Z); e2.Ay = e1.Ay + MathF.PI * 0.5f;
        return true;
    }

    void ClearEncounters(bool notify)
    {
        foreach (var e in encs) { e.Node.QueueFree(); if (notify) it.FireEvent("WorldMap_DeleteShipEncounter", new List<V> { V.Int(e.Type) }); }
        encs.Clear();
        if (Root != null) Root.GetOrCreate("encounter").GetOrCreate("num").Value = "0";
    }

    void RemoveEnc(Enc e)
    {
        e.Node.QueueFree(); encs.Remove(e);
        Root!.GetOrCreate("encounter").GetOrCreate("num").Value = encs.Count.ToString();
        it.FireEvent("WorldMap_DeleteShipEncounter", new List<V> { V.Int(e.Type) });
    }

    void TickEncounters(float dt)
    {
        if (!encHooked) HookEncounterAttrs();
        float kill = F(Root, "enemyshipDistKill", 150f);
        foreach (var e in encs.ToList())
        {
            if (e.Life >= 0f) { e.Life -= dt; if (e.Life <= 0f) { RemoveEnc(e); continue; } }
            switch (e.Kind)
            {
                case EncKind.Merchant:
                    e.Ay = MathF.Atan2(e.DestX - e.X, e.DestZ - e.Z);
                    if (MathF.Abs(e.DestX - e.X) + MathF.Abs(e.DestZ - e.Z) < 20f) { RemoveEnc(e); continue; }
                    break;
                case EncKind.Follow:
                {
                    float want = MathF.Atan2(shipX - e.X, shipZ - e.Z), diff = want - e.Ay;
                    while (diff > MathF.PI) diff -= MathF.Tau;
                    while (diff < -MathF.PI) diff += MathF.Tau;
                    e.Ay += Math.Clamp(diff, -0.6f * dt, 0.6f * dt);
                    break;
                }
            }
            e.X += MathF.Sin(e.Ay) * e.Speed * dt; e.Z += MathF.Cos(e.Ay) * e.Speed * dt;
            e.Node.Position = new Vector3(e.X, 0.5f, -e.Z); e.Node.Rotation = new Vector3(0, -e.Ay, 0);
            float d = MathF.Sqrt((e.X - shipX) * (e.X - shipX) + (e.Z - shipZ) * (e.Z - shipZ));
            if (d > kill * 1.2f) { RemoveEnc(e); continue; }
            if (d < 9f && !e.Reported)
            {
                // the player's ship touched it: the ship (and its opponent) take part in the battle
                e.Reported = true; e.Selected = true; if (e.Partner != null) e.Partner.Selected = true;
                GD.Print($"[map] encounter with ship type {e.Type} ({e.Kind})");
                it.FireEvent("WorldMap_ShipEncounter", new List<V> { V.Float(shipX), V.Float(shipZ), V.Float(shipAy), V.Int(encs.IndexOf(e)) });
                return;      // the script may have deleted the map
            }
        }
    }

    /// <summary>Test hook: ask the script for a merchant ship next to the player.</summary>
    public void TestEncounter(bool follow)
    {
        if (!built) return;
        GD.Print("[map] test encounter: asking the script for a " + (follow ? "pursuer" : "merchant"));
        it.CallTop(follow ? "wdmCreateFollowShip" : "wdmCreateMerchantShip", follow ? new[] { V.Int(0), V.Float(0f) } : new[] { V.Int(0), V.Str(""), V.Float(0f) });
    }
}
