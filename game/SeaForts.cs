using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Formats;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Forts (the engine's AIFort). The script creates a fort character (Ship.Type = SHIP_FORT) per island fort and sends AI_MESSAGE_ADD_FORT; the cannon positions are the
/// "cannon_N"/"culverin_N" labels of the fort's locator model. The fort shoots at enemy ships in range (relation by the script), balls that land near a cannon damage it
/// (script event FortCDamage computes the damage) and the fort's strength follows the destroyed cannons.
/// </summary>
public partial class SeaView
{
    sealed class FortCannon { public string Kind = "1"; public Vector3 Pos; public float Damage, Next, Dir, FireIn = -1f; public bool Dead; public Vector3 Aim; }

    sealed class Fort { public Attr Chr = null!; public int Index; public List<FortCannon> Cannons = new(); public Attr? Island; public float X, Z; }

    readonly List<Fort> forts = new();

    void ClearForts() => forts.Clear();

    V AddFort(V[] d)
    {
        if (d.Length < 4 || d[1].Attr is not { } isl || d[2].Attr is not { } loc || d[3].Attr is not { } chr) return V.Int(0);
        string models = isl.Find("filespath.models", false)?.Value?.Replace('/', '\\').Trim('\\') ?? "", locFile = loc.Find("fort.locators", false)?.Value ?? "";
        string file = GameAssets.Resource("MODELS\\" + models + "\\" + locFile + ".gm");
        var f = new Fort { Chr = chr, Index = (int)F(chr, "index"), Island = isl, X = F(loc, "x", 0f), Z = F(loc, "z", 0f) };   // AI_FORT: the position is the x / z of the island's fort label
        if (File.Exists(file))
        {
            try
            {
                foreach (var l in GmFile.Parse(File.ReadAllBytes(file)).Labels.Where(l => l.Name.StartsWith("cannon", StringComparison.OrdinalIgnoreCase) || l.Name.StartsWith("culverin", StringComparison.OrdinalIgnoreCase)))
                    f.Cannons.Add(new FortCannon { Kind = l.Name.StartsWith("culverin", StringComparison.OrdinalIgnoreCase) ? "2" : "1", Pos = new Vector3(l.M[12], l.M[13], l.M[14]), Dir = MathF.Atan2(l.M[8], l.M[10]), Next = 2f + Random.Shared.NextSingle() * 8f });
            }
            catch (Exception ex) { GD.PrintErr("[sea] fort locators: " + ex.Message); }
        }
        forts.Add(f);
        GD.Print($"[sea] fort {chr.Get("id")?.Value}: {f.Cannons.Count} cannons ({locFile}) at ({f.X:0},{f.Z:0}) radius {F(loc, "Radius", 0f):0}");
        it.FireEvent("FortCreate", new List<V> { V.A(chr), V.Int(f.Cannons.Count) });
        return V.Int(1);
    }

    /// <summary>AI_MESSAGE_FORT_HIT: a ball of a character landed at (x, y, z); damage the nearest cannon of a fort.</summary>
    V FortHit(V[] d)
    {
        if (d.Length < 5) return V.Int(0);
        int ball = Conv.ToInt(d[1]); float x = Conv.ToFloat(d[2]), y = Conv.ToFloat(d[3]), z = Conv.ToFloat(d[4]);
        Fort? bf = null; FortCannon? bc = null; float bd = 60f;
        foreach (var f in forts)
            foreach (var c in f.Cannons)
            {
                if (c.Dead) continue;
                float dd = MathF.Sqrt((c.Pos.X - x) * (c.Pos.X - x) + (c.Pos.Z - z) * (c.Pos.Z - z));
                if (dd < bd) { bd = dd; bf = f; bc = c; }
            }
        if (bf == null || bc == null) return V.Int(0);
        int dead = bf.Cannons.Count(c => c.Dead);
        var r = it.FireEvent("FortCDamage", new List<V> { V.Int(ball), V.Int(bf.Index), V.A(bf.Chr), V.Int(bf.Cannons.Count), V.Int(dead), V.Float(x), V.Float(y), V.Float(z), V.Float(bd), V.Float(bc.Damage) });
        bc.Damage = Conv.ToFloat(r);
        if (bc.Damage >= 100f) { bc.Dead = true; GD.Print($"[sea] a cannon of fort {bf.Chr.Get("id")?.Value} is destroyed ({bf.Cannons.Count(c => c.Dead)}/{bf.Cannons.Count})"); }
        return V.Int(1);
    }

    /// <summary>Test hook --fortkill: the player's balls land on every cannon of the fort nearest to the ship (AI_MESSAGE_FORT_HIT -> script FortCDamage) until the script switches the fort off its normal mode.</summary>
    public void FortKill()
    {
        if (ships.FirstOrDefault(s => s.IsPlayer) is not { } p || forts.Count == 0) return;
        var f = forts.OrderBy(x => (x.X - p.X) * (x.X - p.X) + (x.Z - p.Z) * (x.Z - p.Z)).First();
        int hits = 0;
        foreach (var c in f.Cannons)
            for (int k = 0; k < 400 && !c.Dead; k++)
            {
                if (Conv.ToInt(V.Str(f.Chr.Find("Fort.Mode", false)?.Value ?? "0")) != 0) break;
                FortHit(new[] { V.Int(0), V.Int(p.Index), V.Float(c.Pos.X), V.Float(c.Pos.Y), V.Float(c.Pos.Z) }); hits++;
            }
        GD.Print($"[sea] fortkill: fort {f.Chr.Get("id")?.Value} after {hits} balls: {f.Cannons.Count(c => c.Dead)}/{f.Cannons.Count} cannons destroyed, Fort.Mode = {f.Chr.Find("Fort.Mode", false)?.Value}");
    }

    /// <summary>True when the point is close to a fort cannon (balls that land there hit the fort, not the bare island).</summary>
    bool NearFort(float x, float z) => forts.Any(f => f.Cannons.Any(c => MathF.Abs(c.Pos.X - x) < 40f && MathF.Abs(c.Pos.Z - z) < 40f));

    void TickForts(float dt)
    {
        var player = ships.FirstOrDefault(s => s.IsPlayer);
        foreach (var f in forts)
        {
            if (Conv.ToInt(V.Str(f.Chr.Find("Fort.Mode", false)?.Value ?? "0")) != 0) continue;        // only a fort in normal mode shoots
            var sh = f.Chr.Find("Ship", false);
            if (sh == null) continue;
            foreach (var c in f.Cannons)
            {
                if (c.Dead) continue;
                float v0 = FortCannonV0(f, sh, c);
                // AICannon::Fire: the shot leaves after the script's fire delay (CANNON_GET_FIRE_TIME), at the elevation that reaches the aim point
                if (c.FireIn >= 0f)
                {
                    c.FireIn -= dt;
                    if (c.FireIn < 0f)
                    {
                        float dir = MathF.Atan2(c.Aim.X - c.Pos.X, c.Aim.Z - c.Pos.Z);
                        LaunchRaw(f.Chr, c.Pos, f.Index, (int)F(sh, "Cannons.Charge.Type"), v0, dir, HeightFireAngle(v0, c.Pos, c.Aim));
                        c.Next = Math.Max(1f, Conv.ToFloat(it.FireEvent("CannGRTime", new List<V> { V.A(f.Chr) })));      // CANNON_GET_RECHARGE_TIME
                    }
                    continue;
                }
                c.Next -= dt;
                if (c.Next > 0f) continue;
                // AIFort::Execute: once a second every loaded cannon looks for the nearest enemy within CalcMaxFireDistance(cannon height, v0, 0.35)
                float range = MaxFireDistance(c.Pos.Y, v0, 0.35f);
                // the nearest enemy ship in range
                ShipRt? best = null; float bd = range;
                foreach (var s in ships)
                {
                    if (s.Dying || s.Index == f.Index) continue;
                    float dd = MathF.Sqrt((s.X - c.Pos.X) * (s.X - c.Pos.X) + (s.Z - c.Pos.Z) * (s.Z - c.Pos.Z));
                    if (dd < bd && Relation(f.Index, s.Index) == 2) { bd = dd; best = s; }
                }
                c.Next = 1f;                                        // the fort's one second fire timer
                if (best == null) continue;
                if (!Conv.ToBool(it.FireEvent("CannLoad", new List<V> { V.A(f.Chr) }))) { c.Next = 5f; continue; }
                c.Aim = new Vector3(best.X, 0f, best.Z);
                c.FireIn = Math.Max(0f, Conv.ToFloat(it.FireEvent("CannGFTime", new List<V> { V.A(f.Chr) })));
            }
        }
    }

    /// <summary>AIFort::Execute: "cannon" labels use Fort.Cannons.Type.1, "culverin" labels Type.2; when the type differs from Ship.Cannons.Type it is written there and the script
    /// recomputes the shot data (event CannReCaclParam -> Cannon_RecalculateParameters: SpeedV0 of the cannon x the ball, FireAngMin/Max). Returns the muzzle speed.</summary>
    float FortCannonV0(Fort f, Attr sh, FortCannon c)
    {
        var want = f.Chr.Find("Fort.Cannons.Type." + c.Kind, false)?.Value;
        if (want != null && sh.Find("Cannons.Type", false)?.Value != want)
        {
            sh.Find("Cannons.Type", true)!.Value = want;
            it.FireEvent("CannReCaclParam", new List<V> { V.Int(f.Index) });
            GD.Print($"[sea] fort {f.Chr.Get("id")?.Value}: cannon type {want} -> SpeedV0 {F(sh, "Cannons.SpeedV0", 0f):0.0}, FireAngMax {F(sh, "Cannons.FireAngMax", 0f):0.00}");
        }
        return Math.Max(20f, F(sh, "Cannons.SpeedV0", 70f));
    }

    void LaunchRaw(Attr chr, Vector3 p, int owner, int type, float v0, float dir, float elev)
    {
        (v0, dir, elev) = CannonFire(chr, p, v0, dir, elev);                       // dispersion, smoke and sound by the script (SeaCombat.cs)
        var v = new Vector3(MathF.Sin(dir) * MathF.Cos(elev), MathF.Sin(elev), MathF.Cos(dir) * MathF.Cos(elev)) * v0;
        ballMesh ??= new SphereMesh { Radius = 0.25f, Height = 0.5f, RadialSegments = 8, Rings = 4 };
        ballMat ??= new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.12f, 0.12f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var n = new MeshInstance3D { Mesh = ballMesh, MaterialOverride = ballMat };
        AddChild(n);
        balls.Add(new Ball { P = p, V = v, Owner = owner, Type = type, Node = n });
    }
}
