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
        // the original fires at the range of the camera elevation (AIShipCannonController::Fire(camPos, dir)); the port aims at the nearest ship in
        // that direction, capped at the range of the highest elevation (CalcMaxFireDistance)
        var sh = p.Chr.Find("Ship", false);
        float h0 = p.Cannons.Count > 0 ? p.Cannons.Average(c => c.pos.Y) : 3f;
        dist = Math.Min(dist, MaxFireDistance(h0, range, F(sh, "Cannons.FireAngMax", 0.6f)));
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

    /// <summary>One broadside aimed at a point <paramref name="aimDist"/> metres away at water level in the direction of the bort.</summary>
    void FireBort(ShipRt s, string bort, float aimDist)
    {
        float dir = BortDir(s, bort);
        FireBort(s, bort, new Vector3(s.X + MathF.Sin(dir) * aimDist, 0f, s.Z + MathF.Cos(dir) * aimDist));
    }

    static float BortDir(ShipRt s, string bort) => bort switch
    {
        "cannonr" => s.Ay + MathF.PI / 2, "cannonl" => s.Ay - MathF.PI / 2, "cannonf" => s.Ay, _ => s.Ay + MathF.PI,
    };

    /// <summary>
    /// AICannon::CalcHeightFireAngle: the elevation that puts a ball fired at <paramref name="v0"/> from <paramref name="from"/> into <paramref name="to"/>
    /// (the flat trajectory; 0 when the point is out of reach, as in the original).
    /// </summary>
    static float HeightFireAngle(float v0, Vector3 from, Vector3 to)
    {
        double dx = to.X - from.X, dz = to.Z - from.Z, dist = Math.Sqrt(dx * dx + dz * dz), h = to.Y - from.Y, g = G;
        double a = g * g / 4.0, b = h * g - v0 * v0, c = h * h + dist * dist, d = b * b - 4 * a * c;
        if (d < 0) return 0f;
        double T = Math.Sqrt((-b - Math.Sqrt(d)) / (2.0 * a));
        if (T <= 0) return 0f;
        double alpha = Math.Acos(Math.Clamp(dist / (v0 * T), -1.0, 1.0));
        double sy0 = v0 * T * Math.Sin(alpha) - g * T * T / 2.0, sy1 = v0 * T * Math.Sin(-alpha) - g * T * T / 2.0;
        return (float)(Math.Abs(sy0 - h) < Math.Abs(sy1 - h) ? alpha : -alpha);
    }

    /// <summary>AICannon::CalcMaxFireDistance: the range of a ball fired from <paramref name="height"/> metres at the elevation <paramref name="ang"/>.</summary>
    static float MaxFireDistance(float height, float v0, float ang)
    {
        float a = -G / 2f, b = v0 * MathF.Sin(ang), d = b * b - 4f * a * height;
        float t1 = (-b - MathF.Sqrt(Math.Max(0f, d))) / (2f * a);
        return v0 * t1 * MathF.Cos(ang);
    }

    /// <summary>
    /// One broadside (or the bow / stern guns) at the point <paramref name="target"/> (engine coordinates), as AIShipCannonController::Fire2Position: the
    /// script widens the point by the gunner's accuracy (SHIP_GET_BORT_FIRE_DELTA), SHIP_BORT_FIRE reports the point and the direction, then every cannon
    /// that can be loaded (CANNON_LOAD) fires after its own script delay (CANNON_GET_FIRE_TIME, AICannon::Fire) at the elevation that reaches the point
    /// (AICannon::RealFire / CalcHeightFireAngle). Reload is per bort here (the original reloads each cannon; the ammunition counts are the same).
    /// </summary>
    void FireBort(ShipRt s, string bort, Vector3 target)
    {
        if (s.ReadyAt.TryGetValue(bort, out var t) && clock < t) return;
        var sh = s.Chr.Find("Ship", false);
        if (sh == null) return;
        var cannons = s.Cannons.Where(c => c.bort == bort).ToList();
        if (cannons.Count == 0) return;
        float v0 = Math.Max(10f, F(sh, "Cannons.SpeedV0", 50f));
        float rad = Conv.ToFloat(it.FireEvent("ShpGetBortFireDelta", new List<V> { V.A(s.Chr), V.Float(target.X), V.Float(target.Y), V.Float(target.Z) }));
        float ra = Random.Shared.NextSingle() * MathF.Tau;
        target += new Vector3(rad * MathF.Sin(ra), 0f, rad * MathF.Cos(ra));
        var fdir = new Vector3(target.X - s.X, target.Y, target.Z - s.Z).Normalized();
        int fired = 0;
        var shots = new List<(float at, Vector3 local, float dir, float elev)>();
        foreach (var c in cannons)
        {
            if (!Conv.ToBool(it.FireEvent("CannLoad", new List<V> { V.A(s.Chr) }))) continue;      // no ball / powder
            var cp = BallStart(s, c.pos);
            float cdir = MathF.Atan2(target.X - cp.X, target.Z - cp.Z);
            float delay = Math.Max(0f, Conv.ToFloat(it.FireEvent("CannGFTime", new List<V> { V.A(s.Chr) })));
            shots.Add((clock + delay, c.pos, cdir, HeightFireAngle(v0, cp, target)));
            fired++;
        }
        if (fired == 0) return;                                                            // the HUD learns it from SHIP_NOT_ENOUGH_BALLS (TickCombat)
        float recharge = Math.Max(2f, Conv.ToFloat(it.FireEvent("CannGRTime", new List<V> { V.A(s.Chr) })));
        s.ReadyAt[bort] = clock + recharge;
        float dist = MathF.Sqrt((target.X - s.X) * (target.X - s.X) + (target.Z - s.Z) * (target.Z - s.Z));
        GD.Print($"[sea] {s.Chr.Get("id")?.Value} fires {fired} cannons of {bort}, aim {dist:0} m (spread {rad:0} m), recharge {recharge:0.0} s");
        it.FireEvent("Ship_Bort_Fire", new List<V> { V.Int(s.Index), V.A(s.Chr), V.Str(bort), V.Float(target.X), V.Float(target.Y), V.Float(target.Z), V.Float(fdir.X), V.Float(fdir.Y), V.Float(fdir.Z) });
        foreach (var q in shots) queued.Add((q.at, s, q.local, bort, q.dir, q.elev));
        if (s.IsPlayer) FireAction(s, BortDir(s, bort), v0 * v0 / G);                    // ships in the sector learn that they were fired at (SeaAI.cs)
    }

    /// <summary>
    /// AIShipCannonController::Execute for the main character: every frame SHIP_NOT_ENOUGH_BALLS "l" = some bort has more intact cannons than the
    /// current charge in the hold (SHIP_GET_CURRENT_BALLS_NUM); the script keeps it in bNotEnoughBalls for the HUD and the bort fire experience.
    /// </summary>
    void TickNotEnoughBalls()
    {
        if (ships.FirstOrDefault(s => s.IsPlayer && !s.Dying) is not { } p || p.Cannons.Count == 0) return;
        int balls = Conv.ToInt(it.FireEvent("Shp_GetCurBallsNum", new List<V> { V.A(p.Chr) }));
        bool notEnough = p.Cannons.GroupBy(c => c.bort).Any(g => g.Count() > balls);
        it.FireEvent("Shp_NotEnoughBalls", new List<V> { V.Int(notEnough ? 1 : 0) });
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
            var ssh = s.Chr.Find("Ship", false);
            float range = MaxFireDistance(s.Cannons.Count > 0 ? s.Cannons.Average(c => c.pos.Y) : 3f, F(ssh, "Cannons.SpeedV0", 50f), F(ssh, "Cannons.FireAngMax", 0.6f));
            float d = Dist(s, tgt);
            if (d > range * 0.95f) continue;
            float rel = MathF.Atan2(tgt.X - s.X, tgt.Z - s.Z) - s.Ay;
            while (rel > MathF.PI) rel -= MathF.Tau;
            while (rel < -MathF.PI) rel += MathF.Tau;
            if (MathF.Abs(MathF.Abs(rel) - MathF.PI / 2) >= 0.5f) continue;
            string bort = rel > 0 ? "cannonr" : "cannonl";
            if (s.ReadyAt.TryGetValue(bort, out var rt) && clock < rt) continue;
            // AIShipCannonController::Fire(AIShip*): aim height from the script (CANNON_GET_FIRE_HEIGHT), the target led by its speed over the flight
            // and the fire delay (CANNON_GET_FIRE_TIME)
            float h = Conv.ToFloat(it.FireEvent("CannGFHeight", new List<V> { V.A(s.Chr), V.A(tgt.Chr) }));
            float ft = Conv.ToFloat(it.FireEvent("CannGFTime", new List<V> { V.A(s.Chr) }));
            float v0 = Math.Max(10f, F(s.Chr.Find("Ship", false), "Cannons.SpeedV0", 50f));
            float lead = tgt.Speed * (d / v0 + ft);
            FireBort(s, bort, new Vector3(tgt.X + MathF.Sin(tgt.Ay) * lead, h, tgt.Z + MathF.Cos(tgt.Ay) * lead));
        }
        TickNotEnoughBalls();
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
