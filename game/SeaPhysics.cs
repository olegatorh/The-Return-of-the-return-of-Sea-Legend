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
        float amp = Math.Clamp(0.012f + wind * 0.0016f, 0.01f, 0.05f);
        float ph = s.Index * 1.7f;
        float size = Math.Clamp(30f / Math.Max(10f, s.Length), 0.5f, 1.3f);        // small ships rock more
        float roll = amp * size * (MathF.Sin(waveClock * 0.9f + ph) + 0.5f * MathF.Sin(waveClock * 1.7f + ph * 2f));
        float pitch = amp * 0.6f * size * MathF.Sin(waveClock * 0.7f + ph + 1f);
        // heel: outwards in a turn, and away from the wind under sail (the wind pushes the sails, strongest abeam)
        float windAng = 0f;
        if (it.Globals.TryGetValue("Weather", out var w2) && w2.Val.Attr is { } wb) windAng = F(wb, "Wind.Angle");
        float side = MathF.Sin(windAng - s.Ay);                                   // > 0: the wind blows towards the starboard side
        float heel = -s.TurnRate * Math.Clamp(s.Speed / 8f, 0f, 1.5f) * 0.6f + s.Sail * Math.Clamp(wind / 18f, 0f, 1f) * 0.11f * side * Math.Clamp(s.Speed / 5f, 0.3f, 1f);
        s.Node.Rotation = new Vector3(pitch, -s.Ay, roll + heel);
        s.Node.Position = new Vector3(s.X, 0.15f * MathF.Sin(waveClock * 1.1f + ph) * size, -s.Z);
        UpdateWake(s);
    }

    // ---- wake: a translucent white wedge behind a moving ship, its width and strength follow the speed
    readonly Dictionary<ShipRt, MeshInstance3D> wakes = new();
    static StandardMaterial3D? wakeMat;

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
            wakeMat ??= new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = new Color(1f, 1f, 1f, 0.28f), NoDepthTest = false, DisableFog = false,
            };
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
                a.X += n.X * -best * 0.5f; a.Z += n.Y * -best * 0.5f; b.X -= n.X * -best * 0.5f; b.Z -= n.Y * -best * 0.5f;
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
