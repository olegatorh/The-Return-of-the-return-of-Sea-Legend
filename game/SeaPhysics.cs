using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>Extra ship physics of the sea: wave rocking and heeling, ship-to-ship collisions (the script converts the impact to damage) and the wake.</summary>
public partial class SeaView
{
    float rockDt = 1f / 60f;
    readonly float[,] rockH = new float[6, 6];

    /// <summary>
    /// SHIP::ShipRocking: the sea height under a 6 x 6 grid over the hull box (width x length) gives the height (their mean) and the pitch / roll (the mean
    /// slope between the front and back rows / the left and right columns, three pairs each); the ship follows them by min(dt / 0.025 x Ship.Rocking.y / .az, 1)
    /// per frame. The heel in a turn is SHIP::Execute's vAng.z += clamp(rotation speed, ±0.25).
    /// </summary>
    void ApplyRocking(ShipRt s)
    {
        if (s.Dying) return;
        var sh = s.Chr.Find("Ship", false);
        float fDelta = Math.Min(rockDt, 0.1f) / 0.025f, kY = F(sh, "Rocking.y", 1f), kAZ = F(sh, "Rocking.az", 1f);
        float W = Math.Max(1f, s.Width), L = Math.Max(1f, s.Length);
        float sn = MathF.Sin(s.Ay), cs = MathF.Cos(s.Ay), sum = 0f;
        for (int ix = 0; ix < 6; ix++)
            for (int iz = 0; iz < 6; iz++)
            {
                float x = ix * W * 0.2f - 0.5f * W, z = iz * L * 0.2f - 0.5f * L;          // engine local: x right, z forward
                float wx = s.X + x * cs + z * sn, wz = s.Z - x * sn + z * cs;
                float h = SeaWaves.Height(wx, -wz);                                         // Godot world position
                rockH[ix, iz] = h; sum += h;
            }
        float bowUp = 0f, rightUp = 0f;
        for (int k = 0; k < 3; k++)
        {
            float back = 0f, front = 0f, left = 0f, right = 0f;
            for (int j = 0; j < 6; j++) { back += rockH[j, k]; front += rockH[j, 5 - k]; left += rockH[k, j]; right += rockH[5 - k, j]; }
            bowUp += MathF.Atan((front - back) / 6f / (L - 0.4f * k * L));
            rightUp += MathF.Atan((right - left) / 6f / (W - 0.4f * k * W));
        }
        bowUp /= 3f; rightUp /= 3f;
        s.RockY += Math.Min(fDelta * kY, 1f) * (sum / 36f - s.RockY);
        float ka = Math.Min(kAZ * fDelta, 1f);
        s.RockPitch += ka * (bowUp - s.RockPitch);
        s.RockRoll += ka * (rightUp - s.RockRoll);
        float heel = -Math.Clamp(s.TurnRate, -0.25f, 0.25f);                                   // outwards in a turn
        s.Node.Rotation = new Vector3(s.RockPitch, -s.Ay, s.RockRoll + heel);
        s.Node.Position = new Vector3(s.X, s.RockY, -s.Z);
        UpdateWake(s);
    }

    // ---- wake: a translucent white wedge behind a moving ship, its width and strength follow the speed
    readonly Dictionary<ShipRt, MeshInstance3D> wakes = new();
    static ShaderMaterial? wakeMat;
    // soft foam: dense in the middle and near the hull, streaky, fading out sideways and towards the tail (the old flat white wedge looked like a sheet of paper)
    const string WakeShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
varying vec3 lp;
void vertex() { lp = VERTEX; }
void fragment() {
    float u = abs(UV.x - 0.5) * 2.0;                  // 0 centre .. 1 edge
    float v = UV.y;                                    // 0 bow .. 1 tail
    float streak = 0.55 + 0.45 * sin(lp.x * 1.9 + lp.z * 0.35) * sin(lp.x * 0.7 - lp.z * 0.9 + 1.3);
    float a = (1.0 - smoothstep(0.15, 1.0, u)) * (1.0 - smoothstep(0.05, 1.0, v));
    a *= streak * 0.55;
    ALBEDO = vec3(0.92, 0.96, 1.0);
    ALPHA = clamp(a, 0.0, 0.6);
}";

    void UpdateWake(ShipRt s)
    {
        if (!wakes.TryGetValue(s, out var m))
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            // local space: ship forward = -z; the wedge starts at the bow (z = -0.4 L) and spreads to 1.3 L behind the stern
            float L = s.Length;
            var a = new Vector3(0, 0.05f, -L * 0.35f); var b = new Vector3(-L * 0.14f, 0.05f, L * 0.2f); var c = new Vector3(-L * 0.28f, 0.05f, L * 1.3f);
            var d = new Vector3(L * 0.14f, 0.05f, L * 0.2f); var e = new Vector3(L * 0.28f, 0.05f, L * 1.3f);
            foreach (var (p, uv) in new[] { (a, new Vector2(0.5f, 0)), (b, new Vector2(0, 0.4f)), (d, new Vector2(1, 0.4f)), (b, new Vector2(0, 0.4f)), (c, new Vector2(0, 1)), (e, new Vector2(1, 1)),
                                            (b, new Vector2(0, 0.4f)), (e, new Vector2(1, 1)), (d, new Vector2(1, 0.4f)) })
            { st.SetUV(uv); st.AddVertex(p); }
            wakeMat ??= new ShaderMaterial { Shader = new Shader { Code = WakeShader } };
            m = new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = wakeMat, Name = "Wake", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            s.Node.AddChild(m);
            wakes[s] = m;
        }
        m.Visible = s.Speed > 0.8f && !s.Dying;
        m.Scale = new Vector3(1f, 1f, Math.Clamp(s.Speed / 8f, 0.25f, 1.4f));
    }

    // ---- ship-to-ship collisions
    readonly Dictionary<(int, int), float> collCool = new();

    /// <summary>Three circles along each hull; overlapping circles push the ships apart and report an impact (power = relative speed) to the script.</summary>
    void ResolveShipCollisions()
    {
        for (int i = 0; i < ships.Count; i++)
            for (int j = i + 1; j < ships.Count; j++)
            {
                var a = ships[i]; var b = ships[j];
                if (a.Dying || b.Dying) continue;
                if (MathF.Abs(a.X - b.X) > (a.Length + b.Length) * 0.6f || MathF.Abs(a.Z - b.Z) > (a.Length + b.Length) * 0.6f) continue;
                float ra = Math.Max(3f, a.Length * 0.12f), rb = Math.Max(3f, b.Length * 0.12f);
                float best = float.MaxValue; Vector2 pa = default, pb = default;
                foreach (var ka in new[] { -0.3f, 0f, 0.3f })
                    foreach (var kb in new[] { -0.3f, 0f, 0.3f })
                    {
                        var qa = new Vector2(a.X + MathF.Sin(a.Ay) * a.Length * ka, a.Z + MathF.Cos(a.Ay) * a.Length * ka);
                        var qb = new Vector2(b.X + MathF.Sin(b.Ay) * b.Length * kb, b.Z + MathF.Cos(b.Ay) * b.Length * kb);
                        float d = qa.DistanceTo(qb) - ra - rb;
                        if (d < best) { best = d; pa = qa; pb = qb; }
                    }
                if (best >= 0f) continue;
                var n = (pa - pb); n = n.LengthSquared() < 1e-4f ? new Vector2(1, 0) : n.Normalized();
                float ax0 = a.X, az0 = a.Z, bx0 = b.X, bz0 = b.Z;
                a.X += n.X * -best * 0.5f; a.Z += n.Y * -best * 0.5f; b.X -= n.X * -best * 0.5f; b.Z -= n.Y * -best * 0.5f;
                KeepOffLand(a, ax0, az0); KeepOffLand(b, bx0, bz0);       // a push must not shove a hull onto the island
                // TOUCH::IsIntersectShipsReal / Touch: the blow power is the closing speed (knots) along the contact normal, the slide the cosine between the
                // motion and the hull side; the hit ship takes kW = 0.5 + (wHitter - wHit) / (2 max w) of it, the hitter the rest (Ship type "Weight")
                float x = (pa.X + pb.X) * 0.5f, z = (pa.Y + pb.Y) * 0.5f;
                var va = new Vector2(MathF.Sin(a.Ay), MathF.Cos(a.Ay)) * a.Speed + new Vector2(MathF.Cos(a.Ay), -MathF.Sin(a.Ay)) * a.Drift;
                var vb = new Vector2(MathF.Sin(b.Ay), MathF.Cos(b.Ay)) * b.Speed + new Vector2(MathF.Cos(b.Ay), -MathF.Sin(b.Ay)) * b.Drift;
                var vr = va - vb;                                                                  // a relative to b; n points from b to a
                float closing = Math.Max(0f, -vr.Dot(n)), power = closing;
                float slide = vr.LengthSquared() > 1e-6f ? MathF.Sqrt(Math.Max(0f, 1f - MathF.Pow(vr.Normalized().Dot(n), 2))) : 0f;
                float wa = Math.Max(1f, TypeF(a.Chr.Find("Ship", false), "Weight", 1f)), wb = Math.Max(1f, TypeF(b.Chr.Find("Ship", false), "Weight", 1f));
                float kHit = 0.5f + (wa - wb) / (2f * Math.Max(wa, wb));
                float pB = kHit * power, pA = power - pB;
                // ProcessImpulse: the hit ship is pushed along the blow, the hitter recoils away from the other hull
                Impulse(b, x, z, -n.X, -n.Y, pB);
                Impulse(a, x, z, n.X, n.Y, pA);
                var key = (a.Index, b.Index);
                if (collCool.TryGetValue(key, out var until) && clock < until) continue;
                collCool[key] = clock + 1f;
                GD.Print($"[sea] collision {a.Chr.Get("id")?.Value} <-> {b.Chr.Get("id")?.Value}, power {pA:0.0}/{pB:0.0} slide {slide:0.00}");
                it.FireEvent("Shp_Ship2ShipColl", new List<V> { V.Int(b.Index), V.Int(a.Index), V.Float(pB), V.Float(slide), V.Int(0), V.Float(x), V.Float(0f), V.Float(z) });
                it.FireEvent("Shp_Ship2ShipColl", new List<V> { V.Int(a.Index), V.Int(b.Index), V.Float(pA), V.Float(slide), V.Int(0), V.Float(x), V.Float(0f), V.Float(z) });
            }
    }

    /// <summary>
    /// TOUCH::ProcessImpulse: a blow of <paramref name="power"/> knots at the world point (px, pz) in the world direction (dx, dz): forward speed gets
    /// dir.z x power x (1.1 - |x| / half width), sideways speed dir.x x power x (1.1 - |z| / half length), the yaw rate 0.02 x power x (1.1 - |dir . to-centre|).
    /// </summary>
    void Impulse(ShipRt s, float px, float pz, float dx, float dz, float power)
    {
        if (power <= 0f) return;
        float c = MathF.Cos(s.Ay), sn = MathF.Sin(s.Ay);
        float rx = px - s.X, rz = pz - s.Z;
        float lx = rx * c - rz * sn, lz = rx * sn + rz * c;                                 // contact point in the ship's frame
        float dlx = dx * c - dz * sn, dlz = dx * sn + dz * c;                               // blow direction in the ship's frame
        float kZ = Math.Clamp(1.1f - MathF.Abs(lx / (0.5f * Math.Max(1f, s.Width))), 0f, 1f);
        float kX = Math.Clamp(1.1f - MathF.Abs(lz / (0.5f * Math.Max(1f, s.Length))), 0f, 1f);
        s.Speed += dlz * power * kZ;
        s.Drift += dlx * power * kX;
        float len = MathF.Sqrt(lx * lx + lz * lz);
        float toC = len > 1e-3f ? (dlx * -lx + dlz * -lz) / len : 0f;
        float rot = Math.Clamp(1.1f - MathF.Abs(toC), 0f, 1f) * power * 0.02f;
        s.TurnRate += lx * lz >= 0f ? -rot : rot;
    }
}
