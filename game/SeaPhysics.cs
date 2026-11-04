using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>Extra ship physics of the sea: wave rocking and heeling, ship-to-ship collisions (the script converts the impact to damage) and the wake.</summary>
public partial class SeaView
{
    float waveClock;

    /// <summary>Roll and pitch from the swell (stronger with the wind), heel in turns and under sail.</summary>
    void ApplyRocking(ShipRt s)
    {
        if (s.Dying) return;
        waveClock = clock;
        float wind = 0f;
        if (it.Globals.TryGetValue("Weather", out var w) && w.Val.Attr is { } wa) wind = F(wa, "Wind.Speed");
        // the ship rides the wave field of the sea (SeaWaves, the same function that displaces the water): lifted by the surface under it, pitched and rolled by its slope
        float sn = MathF.Sin(s.Ay), cs = MathF.Cos(s.Ay);
        float px = s.X, pz = -s.Z;                                          // Godot world position
        var fwd = new Vector2(sn, -cs);                                      // bow direction in Godot x,z
        var right = new Vector2(cs, sn);
        float lh = s.Length * 0.4f, bh = Math.Max(3f, s.Length * 0.12f);
        float hC = SeaWaves.Height(px, pz);
        float hBow = SeaWaves.Height(px + fwd.X * lh, pz + fwd.Y * lh), hStern = SeaWaves.Height(px - fwd.X * lh, pz - fwd.Y * lh);
        float hR = SeaWaves.Height(px + right.X * bh, pz + right.Y * bh), hL = SeaWaves.Height(px - right.X * bh, pz - right.Y * bh);
        float size = Math.Clamp(30f / Math.Max(10f, s.Length), 0.5f, 1.3f);        // small ships follow the surface more closely
        float pitch = MathF.Atan2(hBow - hStern, 2f * lh) * (0.6f + 0.3f * size);
        float roll = MathF.Atan2(hR - hL, 2f * bh) * (0.6f + 0.3f * size);
        float heave = hC * 0.9f;
        // heel: outwards in a turn, and away from the wind under sail (the wind pushes the sails, strongest abeam)
        float windAng = 0f;
        if (it.Globals.TryGetValue("Weather", out var w2) && w2.Val.Attr is { } wb) windAng = F(wb, "Wind.Angle");
        float side = MathF.Sin(windAng - s.Ay);                                   // > 0: the wind blows towards the starboard side
        float heel = -s.TurnRate * Math.Clamp(s.Speed / 8f, 0f, 1.5f) * 0.6f + s.Sail * Math.Clamp(wind / 18f, 0f, 1f) * 0.11f * side * Math.Clamp(s.Speed / 5f, 0.3f, 1f);
        s.Node.Rotation = new Vector3(pitch, -s.Ay, roll + heel);
        s.Node.Position = new Vector3(s.X, heave, -s.Z);
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
                float rel = MathF.Abs(a.Speed - b.Speed) + 0.5f * MathF.Abs(a.Speed) * 0.2f;
                a.Speed *= 0.6f; b.Speed *= 0.6f;
                var key = (a.Index, b.Index);
                if (collCool.TryGetValue(key, out var until) && clock < until) continue;
                collCool[key] = clock + 1f;
                float x = (pa.X + pb.X) * 0.5f, z = (pa.Y + pb.Y) * 0.5f;
                GD.Print($"[sea] collision {a.Chr.Get("id")?.Value} <-> {b.Chr.Get("id")?.Value}, power {rel:0.0}");
                it.FireEvent("Shp_Ship2ShipColl", new List<V> { V.Int(a.Index), V.Int(b.Index), V.Float(rel), V.Float(0.4f), V.Int(0), V.Float(x), V.Float(0f), V.Float(z) });
                it.FireEvent("Shp_Ship2ShipColl", new List<V> { V.Int(b.Index), V.Int(a.Index), V.Float(rel), V.Float(0.4f), V.Int(0), V.Float(x), V.Float(0f), V.Float(z) });
            }
    }
}
