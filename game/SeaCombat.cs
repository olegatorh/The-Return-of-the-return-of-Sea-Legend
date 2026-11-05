using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Cannons and balls of the sea mode. The script owns the rules (ammunition: CannLoad, reload time: CannGRTime, cannon data: Characters[i].Ship.Cannons.*, damage in Shp_HullHit);
/// the engine side here fires the broadsides, flies the balls (real ballistics, g = 9.81, speed Ship.Cannons.SpeedV0 as computed by the script) and reports hits.
/// Engine coordinates: x, y (up), z; Godot position = (x, y, -z).
/// </summary>
public partial class SeaView
{
    sealed class Ball
    {
        public Vector3 P, V;
        public int Owner, Type;
        public float Life;
        public readonly HashSet<int> SailHit = new();
        public MeshInstance3D Node = null!;
    }

    readonly List<Ball> balls = new();
    readonly List<(float at, ShipRt ship, Vector3 local, string bort, float dir, float elev)> queued = new();
    float clock;
    static SphereMesh? ballMesh;
    static StandardMaterial3D? ballMat;
    const float G = 9.81f;

    void ClearCombat()
    {
        foreach (var b in balls) b.Node.QueueFree();
        balls.Clear(); queued.Clear();
    }

    /// <summary>Player: fire the broadside that faces the camera direction (left mouse button).</summary>
    public void FirePlayer()
    {
        if (ships.FirstOrDefault(s => s.IsPlayer) is not { } p) return;
        var fwd = -Cam.GlobalTransform.Basis.Z;                                    // Godot -> engine: z flips
        float aimAy = MathF.Atan2(fwd.X, -fwd.Z);
        float rel = aimAy - p.Ay;
        while (rel > MathF.PI) rel -= MathF.Tau;
        while (rel < -MathF.PI) rel += MathF.Tau;
        string bort = rel >= 0f ? "cannonr" : "cannonl";
        // aim distance: the nearest ship roughly in that direction, otherwise 70 % of the range
        float range = F(p.Chr.Find("Ship", false), "Cannons.SpeedV0", 50f);
        float dist = range * range / G * 0.7f;
        float best = float.MaxValue;
        foreach (var o in ships)
        {
            if (ReferenceEquals(o, p)) continue;
            float d = Dist(p, o), a = MathF.Atan2(o.X - p.X, o.Z - p.Z) - p.Ay + (bort == "cannonr" ? -MathF.PI / 2 : MathF.PI / 2);
            while (a > MathF.PI) a -= MathF.Tau;
            while (a < -MathF.PI) a += MathF.Tau;
            if (MathF.Abs(a) < 0.6f && d < best) { best = d; dist = d; }
        }
        FireBort(p, bort, dist);
    }

    /// <summary>
    /// Test hook --fire: the broadside that faces the nearest other ship, aimed at its distance. The mouse rule above picks the side by the camera, which is a
    /// coin flip while the camera sits straight behind the ship (that made the fight scenario fire at the empty side now and then).
    /// </summary>
    public void FireAtNearest()
    {
        if (ships.FirstOrDefault(s => s.IsPlayer) is not { } p) return;
        var o = ships.Where(s => !ReferenceEquals(s, p) && !s.Dying).OrderBy(s => Dist(p, s)).FirstOrDefault();
        if (o == null) { FirePlayer(); return; }
        float rel = MathF.Atan2(o.X - p.X, o.Z - p.Z) - p.Ay;
        while (rel > MathF.PI) rel -= MathF.Tau;
        while (rel < -MathF.PI) rel += MathF.Tau;
        FireBort(p, rel >= 0f ? "cannonr" : "cannonl", Dist(p, o));
    }

    static float Dist(ShipRt a, ShipRt b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>One broadside (or the bow / stern guns): every cannon of the bort that can be loaded fires with a small random delay.</summary>
    void FireBort(ShipRt s, string bort, float aimDist)
    {
        if (s.ReadyAt.TryGetValue(bort, out var t) && clock < t) return;
        var sh = s.Chr.Find("Ship", false);
        if (sh == null) return;
        var cannons = s.Cannons.Where(c => c.bort == bort).ToList();
        if (cannons.Count == 0) return;
        float recharge = Math.Max(2f, Conv.ToFloat(it.FireEvent("CannGRTime", new List<V> { V.A(s.Chr) })));
        float v0 = Math.Max(10f, F(sh, "Cannons.SpeedV0", 50f)), angMax = F(sh, "Cannons.FireAngMax", 0.6f), angMin = F(sh, "Cannons.FireAngMin", -0.35f);
        float elev = Math.Clamp(0.5f * MathF.Asin(Math.Clamp(G * aimDist / (v0 * v0), 0f, 1f)), angMin, angMax);
        float dir = bort switch
        {
            "cannonr" => s.Ay + MathF.PI / 2, "cannonl" => s.Ay - MathF.PI / 2, "cannonf" => s.Ay, _ => s.Ay + MathF.PI,
        };
        int fired = 0;
        var rnd = new Random();
        foreach (var c in cannons)
        {
            if (!Conv.ToBool(it.FireEvent("CannLoad", new List<V> { V.A(s.Chr) }))) continue;      // no ball / powder
            queued.Add((clock + (float)rnd.NextDouble() * 0.6f, s, c.pos, bort, dir, elev));
            fired++;
        }
        if (fired == 0) { if (s.IsPlayer) it.FireEvent("Shp_NotEnoughBalls"); return; }
        s.ReadyAt[bort] = clock + recharge;
        GD.Print($"[sea] {s.Chr.Get("id")?.Value} fires {fired} cannons of {bort}, aim {aimDist:0} m, elevation {elev:0.00}, recharge {recharge:0.0} s");
        var bp = BallStart(s, cannons[0].pos);
        it.FireEvent("Ship_Bort_Fire", new List<V> { V.Int(s.Index), V.A(s.Chr), V.Str(bort), V.Float(bp.X), V.Float(bp.Y), V.Float(bp.Z), V.Float(0), V.Float(0), V.Float(0) });
        if (s.IsPlayer) FireAction(s, dir, v0 * v0 / G);                                 // ships in the sector learn that they were fired at (SeaAI.cs)
    }

    Vector3 BallStart(ShipRt s, Vector3 local)
    {
        float sn = MathF.Sin(s.Ay), cs = MathF.Cos(s.Ay);
        return new Vector3(s.X + local.X * cs + local.Z * sn, local.Y, s.Z - local.X * sn + local.Z * cs);
    }

    void Launch(ShipRt s, Vector3 local, float dir, float elev)
    {
        var sh = s.Chr.Find("Ship", false);
        float v0 = Math.Max(10f, F(sh, "Cannons.SpeedV0", 50f));
        var p = BallStart(s, local);
        (v0, dir, elev) = CannonFire(s.Chr, p, v0, dir, elev);
        var v = new Vector3(MathF.Sin(dir) * MathF.Cos(elev), MathF.Sin(elev), MathF.Cos(dir) * MathF.Cos(elev)) * v0;
        ballMesh ??= new SphereMesh { Radius = 0.8f, Height = 1.6f, RadialSegments = 8, Rings = 4 };
        ballMat ??= new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.12f, 0.12f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var n = new MeshInstance3D { Mesh = ballMesh, MaterialOverride = ballMat };
        AddChild(n);
        int type = (int)F(sh, "Cannons.Charge.Type");
        balls.Add(new Ball { P = p, V = v, Owner = s.Index, Type = type, Node = n });
    }

    /// <summary>
    /// CANNON_FIRE ("CannFire", AICannon.c Cannon_FireCannon -> Ball_AddBall): the script adds its dispersion by the gunner's accuracy (AIBalls.Dir / SpdV0 / Ang),
    /// creates the cannon smoke ("cancloud", every other shot) and plays the shot sound; the engine then flies the ball with those values.
    /// </summary>
    (float v0, float dir, float elev) CannonFire(Attr chr, Vector3 p, float v0, float dir, float elev)
    {
        it.FireEvent("CannFire", new List<V> { V.A(chr), V.Float(p.X), V.Float(p.Y), V.Float(p.Z), V.Float(v0), V.Float(dir), V.Float(elev), V.Float(dir) });
        if (it.Globals.TryGetValue("AIBalls", out var g) && g.Val.Attr is { } a)
        {
            float Get(string k, float def) => a.Get(k)?.Value is { Length: > 0 } s ? Conv.ParseFloatPrefix(s) : def;
            v0 = Math.Max(10f, Get("SpdV0", v0)); dir = Get("Dir", dir); elev = Get("Ang", elev);
        }
        return (v0, dir, elev);
    }

    void TickCombat(float dt)
    {
        clock += dt;
        // delayed cannons of a broadside
        for (int i = queued.Count - 1; i >= 0; i--)
            if (clock >= queued[i].at) { var q = queued[i]; queued.RemoveAt(i); if (ships.Contains(q.ship)) Launch(q.ship, q.local, q.dir, q.elev); }
        // AI ships shoot at their target when it is abeam and in range
        foreach (var s in ships)
        {
            if (s.IsPlayer || s.Task != 1 || s.TaskTarget == null) continue;
            var tgt = ships.FirstOrDefault(o => ReferenceEquals(o.Chr, s.TaskTarget));
            if (tgt == null) continue;
            float range = F(s.Chr.Find("Ship", false), "Cannons.SpeedV0", 50f); range = range * range / G;
            float d = Dist(s, tgt);
            if (d > range * 0.95f) continue;
            float rel = MathF.Atan2(tgt.X - s.X, tgt.Z - s.Z) - s.Ay;
            while (rel > MathF.PI) rel -= MathF.Tau;
            while (rel < -MathF.PI) rel += MathF.Tau;
            if (MathF.Abs(MathF.Abs(rel) - MathF.PI / 2) < 0.5f) FireBort(s, rel > 0 ? "cannonr" : "cannonl", d);
        }
        // flight
        for (int i = balls.Count - 1; i >= 0; i--)
        {
            var b = balls[i];
            b.Life += dt;
            var from = b.P;
            b.V.Y -= G * dt;
            b.P += b.V * dt;
            b.Node.Position = new Vector3(b.P.X, b.P.Y, -b.P.Z);
            bool done = false;
            foreach (var o in ships) if (o.Index != b.Owner && !o.Dying && InsideRigging(o, b.P)) RiggingPass(o, b, from);      // masts and sails it touches (SeaSails.cs)
            // ships
            foreach (var o in ships)
            {
                if (o.Index == b.Owner || o.Dying || !InsideShip(o, b.P)) continue;
                SetBallType(b);
                GD.Print($"[sea] hit on {o.Chr.Get("id")?.Value} (HP {F(o.Chr.Find("Ship", false), "HP"):0})");
                var (fpi, fpd) = NearestFirePlace(o, b.P);                                    // the script sets the ship on fire when the place is near (< 4 m)
                it.FireEvent("Shp_HullHit", new List<V> { V.A(o.Chr), V.Int(b.Owner), V.Int(o.Index), V.Float(b.P.X), V.Float(b.P.Y), V.Float(b.P.Z), V.Int(fpi), V.Float(fpd) });
                done = true; break;
            }
            if (!done && island != null && b.P.Y < LandHeight(b.P.X, b.P.Z) && b.P.Y > -5f)
            {
                SetBallType(b);
                if (NearFort(b.P.X, b.P.Z)) it.FireEvent("Ball_FortHit", new List<V> { V.Int(b.Owner), V.Float(b.P.X), V.Float(b.P.Y), V.Float(b.P.Z) });
                else it.FireEvent("Ball_IslandHit", new List<V> { V.Int(b.Owner), V.Float(b.P.X), V.Float(b.P.Y), V.Float(b.P.Z) });
                done = true;
            }
            if (!done && b.P.Y < 0f)
            {
                SetBallType(b);
                GD.Print($"[sea] ball splash at ({b.P.X:0},{b.P.Z:0}) after {b.Life:0.0} s");
                it.FireEvent("Ball_WaterHit", new List<V> { V.Int(b.Owner), V.Float(b.P.X), V.Float(0f), V.Float(b.P.Z) });
                done = true;
            }
            if (done || b.Life > 20f) { b.Node.QueueFree(); balls.RemoveAt(i); }
        }
    }

    void SetBallType(Ball b)
    {
        if (it.Globals.TryGetValue("AIBalls", out var g) && g.Val.Attr is { } a) a.GetOrCreate("CurrentBallType").Value = b.Type.ToString();
    }

    /// <summary>The volume of masts and sails above the deck (a coarse test before the exact mast / sail checks).</summary>
    static bool InsideRigging(ShipRt s, Vector3 p)
    {
        float dx = p.X - s.X, dz = p.Z - s.Z;
        float sn = MathF.Sin(s.Ay), cs = MathF.Cos(s.Ay);
        float fwd = dx * sn + dz * cs, side = dx * cs - dz * sn;
        return MathF.Abs(fwd) < s.Length * 0.6f && MathF.Abs(side) < Math.Max(6f, s.Length * 0.3f) && p.Y > 2f && p.Y < s.Height * 1.9f;
    }

    /// <summary>A box around the hull: length x 0.2 length wide x ship height.</summary>
    static bool InsideShip(ShipRt s, Vector3 p)
    {
        float dx = p.X - s.X, dz = p.Z - s.Z;
        float sn = MathF.Sin(s.Ay), cs = MathF.Cos(s.Ay);
        float fwd = dx * sn + dz * cs, side = dx * cs - dz * sn;
        return MathF.Abs(fwd) < s.Length * 0.46f && MathF.Abs(side) < Math.Max(3f, s.Length * 0.11f) && p.Y > -2f && p.Y < Math.Max(6f, s.Height * 0.5f);
    }
}
