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
    sealed class FortCannon { public Vector3 Pos; public float Damage, Next, Dir; public bool Dead; }

    sealed class Fort { public Attr Chr = null!; public int Index; public List<FortCannon> Cannons = new(); public Attr? Island; }

    readonly List<Fort> forts = new();

    void ClearForts() => forts.Clear();

    V AddFort(V[] d)
    {
        if (d.Length < 4 || d[1].Attr is not { } isl || d[2].Attr is not { } loc || d[3].Attr is not { } chr) return V.Int(0);
        string models = isl.Find("filespath.models", false)?.Value?.Replace('/', '\\').Trim('\\') ?? "", locFile = loc.Find("fort.locators", false)?.Value ?? "";
        string file = GameAssets.Resource("MODELS\\" + models + "\\" + locFile + ".gm");
        var f = new Fort { Chr = chr, Index = (int)F(chr, "index"), Island = isl };
        if (File.Exists(file))
        {
            try
            {
                foreach (var l in GmFile.Parse(File.ReadAllBytes(file)).Labels.Where(l => l.Name.StartsWith("cannon", StringComparison.OrdinalIgnoreCase) || l.Name.StartsWith("culverin", StringComparison.OrdinalIgnoreCase)))
                    f.Cannons.Add(new FortCannon { Pos = new Vector3(l.M[12], l.M[13], l.M[14]), Dir = MathF.Atan2(l.M[8], l.M[10]), Next = 2f + Random.Shared.NextSingle() * 8f });
            }
            catch (Exception ex) { GD.PrintErr("[sea] fort locators: " + ex.Message); }
        }
        forts.Add(f);
        GD.Print($"[sea] fort {chr.Get("id")?.Value}: {f.Cannons.Count} cannons ({locFile})");
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

    /// <summary>True when the point is close to a fort cannon (balls that land there hit the fort, not the bare island).</summary>
    bool NearFort(float x, float z) => forts.Any(f => f.Cannons.Any(c => MathF.Abs(c.Pos.X - x) < 40f && MathF.Abs(c.Pos.Z - z) < 40f));

    int Relation(int a, int b) => Conv.ToInt(it.CallTop("GetRelation", V.Int(a), V.Int(b)));

    void TickForts(float dt)
    {
        var player = ships.FirstOrDefault(s => s.IsPlayer);
        foreach (var f in forts)
        {
            if (Conv.ToInt(V.Str(f.Chr.Find("Fort.Mode", false)?.Value ?? "0")) != 0) continue;        // only a fort in normal mode shoots
            var sh = f.Chr.Find("Ship", false);
            if (sh == null) continue;
            float v0 = Math.Max(20f, F(sh, "Cannons.SpeedV0", 70f)), range = v0 * v0 / G;
            foreach (var c in f.Cannons)
            {
                if (c.Dead) continue;
                c.Next -= dt;
                if (c.Next > 0f) continue;
                // the nearest enemy ship in range
                ShipRt? best = null; float bd = range;
                foreach (var s in ships)
                {
                    if (s.Dying || s.Index == f.Index) continue;
                    float dd = MathF.Sqrt((s.X - c.Pos.X) * (s.X - c.Pos.X) + (s.Z - c.Pos.Z) * (s.Z - c.Pos.Z));
                    if (dd < bd && Relation(f.Index, s.Index) == 2) { bd = dd; best = s; }
                }
                c.Next = 0.5f;                                      // look again soon
                if (best == null) continue;
                if (!Conv.ToBool(it.FireEvent("CannLoad", new List<V> { V.A(f.Chr) }))) { c.Next = 5f; continue; }
                float dir = MathF.Atan2(best.X - c.Pos.X, best.Z - c.Pos.Z);
                float elev = 0.5f * MathF.Asin(Math.Clamp(G * bd / (v0 * v0), 0f, 1f));
                LaunchRaw(f.Chr, new Vector3(c.Pos.X, c.Pos.Y, c.Pos.Z), f.Index, (int)F(sh, "Cannons.Charge.Type"), v0, dir, elev + 0.01f);
                c.Next = Math.Max(4f, Conv.ToFloat(it.FireEvent("CannGRTime", new List<V> { V.A(f.Chr) })) * 0.5f) + Random.Shared.NextSingle() * 3f;
            }
        }
    }

    void LaunchRaw(Attr chr, Vector3 p, int owner, int type, float v0, float dir, float elev)
    {
        (v0, dir, elev) = CannonFire(chr, p, v0, dir, elev);                       // dispersion, smoke and sound by the script (SeaCombat.cs)
        var v = new Vector3(MathF.Sin(dir) * MathF.Cos(elev), MathF.Sin(elev), MathF.Cos(dir) * MathF.Cos(elev)) * v0;
        ballMesh ??= new SphereMesh { Radius = 0.8f, Height = 1.6f, RadialSegments = 8, Rings = 4 };
        ballMat ??= new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.12f, 0.12f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var n = new MeshInstance3D { Mesh = ballMesh, MaterialOverride = ballMat };
        AddChild(n);
        balls.Add(new Ball { P = p, V = v, Owner = owner, Type = type, Node = n });
    }
}
