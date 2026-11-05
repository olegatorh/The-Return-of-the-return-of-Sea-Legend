using System;
using System.Linq;
using Godot;

namespace SeaDogs;

/// <summary>
/// Keeping ships in the water. The island is a trimesh (BuildIslandCollision); a ship's hull footprint is sampled at bow, stern, quarters and sides,
/// and a point counts as land when the island surface above it is higher than the draught. Rules:
///  * a move (or a turn) that would put any hull point on land is refused: the ship stops (speed, drift and turn rate die) as the original did on a shore hit;
///  * a ship that already stands on land (spawned there by the script, pushed by another ship, an old save) may only move in a way that takes it out,
///    and if it is fully inside an island it is lifted to the nearest free water at once.
/// </summary>
public partial class SeaView
{
    const float Draught = -2.5f;
    readonly System.Collections.Generic.Dictionary<ShipRt, (float x, float z)> hitAt = new();        // island surface higher than this under a hull point = the hull touches land

    /// <summary>How deep the hull is in land at (x, z, ay): the highest island surface under the sample points (or -1000 over open water), and how many points touch.</summary>
    (float top, int touching) LandUnder(ShipRt s, float x, float z, float ay, float pad = 0f)
    {
        float sn = MathF.Sin(ay), cs = MathF.Cos(ay);
        float hl = s.Length * 0.42f + pad, hb = Math.Max(1.5f, s.Width * 0.5f) + pad * 0.3f;
        float top = -1000f; int touching = 0;
        float reach = s.Length * 0.6f + 5f;           // far from the island's bounding box: open water, no rays needed
        if (x < islandBox.Position.X - reach || x > islandBox.End.X + reach || -z < islandBox.Position.Z - reach || -z > islandBox.End.Z + reach) return (top, 0);
        foreach (var (lx, lz) in Samples)
        {
            // local (lx right, lz forward) as fractions of the half width / half length -> world engine: forward = (sin, cos), right = (cos, -sin)
            float px = lx * hb, pz = lz * hl;
            float wx = x + px * cs + pz * sn, wz = z - px * sn + pz * cs;
            float h = LandHeight(wx, wz);
            if (h > Draught) touching++;
            if (h > top) top = h;
        }
        return (top, touching);
    }

    static readonly (float, float)[] Samples =
    {
        (0f, 1f), (0f, -1f), (0f, 0f),                         // bow, stern, middle
        (1f, 0f), (-1f, 0f), (0.8f, 0.5f), (-0.8f, 0.5f),     // sides
        (0.8f, -0.5f), (-0.8f, -0.5f), (0.4f, 0.85f), (-0.4f, 0.85f),
    };

    bool OnLand(ShipRt s, float x, float z, float ay) => LandUnder(s, x, z, ay).touching > 0;

    /// <summary>
    /// Applies the movement of one frame: (nx, nz) is where the physics wants the ship, newAy its new heading (oldAy the previous one).
    /// Returns true if the ship hit the shore this frame.
    /// </summary>
    bool MoveInWater(ShipRt s, float nx, float nz, float oldAy)
    {
        if (island == null || islandBody == null) { s.X = nx; s.Z = nz; return false; }
        var cur = LandUnder(s, s.X, s.Z, oldAy);
        if (cur.touching > 0 && cur.touching >= Samples.Length - 2)
        {
            // fully inside the island (spawned / pushed there): lift it to the nearest water
            var (fx, fz) = NearestWater(s, s.X, s.Z, s.Ay);
            GD.Print($"[sea] {s.Chr.Get("id")?.Value} was inside the island at ({s.X:0},{s.Z:0}), moved to the water at ({fx:0},{fz:0})");
            s.X = fx; s.Z = fz; s.Speed = 0f; s.Drift = 0f; s.TurnRate = 0f;
            return true;
        }
        // the turn first: a heading that swings the bow or stern into the rocks is refused
        var turned = LandUnder(s, s.X, s.Z, s.Ay);
        // (a ship stopped at the shore may still turn away: only a swing that drives the hull deep into the rocks is refused)
        if (turned.touching > cur.touching + 1 || turned.top > Math.Max(cur.top, Draught) + 1.5f) { s.Ay = oldAy; s.TurnRate = 0f; }
        // moving: the hull plus a few metres of sea room ahead and astern must stay clear, so a ship stopped at the shore still has room to turn away
        float room = Math.Clamp(s.Length * 0.15f, 3f, 8f);
        var next = LandUnder(s, nx, nz, s.Ay, room);
        var now = LandUnder(s, s.X, s.Z, s.Ay, room);
        // free water, or (already touching the sea room) a move that does not go deeper: sliding along the shore or backing out is allowed
        bool ok = next.touching == 0 || (now.touching > 0 && next.touching <= now.touching && next.top <= now.top + 0.01f && LandUnder(s, nx, nz, s.Ay).touching == 0);
        if (ok)
        {
            s.X = nx; s.Z = nz;
            // the shore hit counts once: the ship is 'free' again only after it got a few metres away from where it struck
            if (s.Aground && next.touching == 0 && (!hitAt.TryGetValue(s, out var h) || new Vector2(s.X - h.x, s.Z - h.z).Length() > 6f)) SetAground(s, false);
            return false;
        }
        bool first = !s.Aground;
        if (first) hitAt[s] = (nx, nz);
        SetAground(s, true);
        float hitSpeed = s.Speed;
        s.Speed = 0f; s.Drift = 0f;
        if (first)
        {
            GD.Print($"[sea] {s.Chr.Get("id")?.Value} touches the shore at ({nx:0},{nz:0})");
            // the script turns the impact into hull damage: (ours, other (-1), power, slide, touch point, x, y, z). TOUCH::IsIntersectShipsReal against the
            // island: power = the speed of the hull point that went into the land (knots), slide = 0
            it.FireEvent("Shp_ShipIslColl", new System.Collections.Generic.List<StormScript.Runtime.V> { StormScript.Runtime.V.Int(s.Index), StormScript.Runtime.V.Int(-1), StormScript.Runtime.V.Float(MathF.Abs(hitSpeed)), StormScript.Runtime.V.Float(0f), StormScript.Runtime.V.Int(0), StormScript.Runtime.V.Float(nx), StormScript.Runtime.V.Float(0f), StormScript.Runtime.V.Float(nz) });
        }
        return true;
    }

    /// <summary>SHIP::CheckShip2Strand: the change of the stranded state raises "Shp2Strand" (character index, new state); AIShip.c Ship_OnStrand then plays the looped "ship_aground"
    /// sound and posts Shp2StrandNext, which damages the hull by the speed (unless the SandbankManeuver perk) until the ship is free again.</summary>
    void SetAground(ShipRt s, bool on)
    {
        if (s.Aground == on) return;
        s.Aground = on;
        it.FireEvent("Shp2Strand", new System.Collections.Generic.List<StormScript.Runtime.V> { StormScript.Runtime.V.Int(s.Index), StormScript.Runtime.V.Int(on ? 1 : 0) });
    }

    /// <summary>The nearest place (spiral search, 5 m rings up to 1.5 km) where the whole hull floats.</summary>
    (float x, float z) NearestWater(ShipRt s, float x, float z, float ay)
    {
        for (float r = 5f; r <= 1500f; r += 5f)
        {
            int steps = Math.Max(8, (int)(r * 0.25f));
            for (int i = 0; i < steps; i++)
            {
                float a = i * MathF.Tau / steps;
                float px = x + MathF.Cos(a) * r, pz = z + MathF.Sin(a) * r;
                if (!OnLand(s, px, pz, ay)) return (px, pz);
            }
        }
        return (x, z);
    }

    /// <summary>
    /// "Sail to" of the command menu (AI_MESSAGE_SAIL_2_LOCATOR / _CHARACTER): behind the fader the engine moves the player's squadron next to the port's
    /// reload locator (which stands in the water of the mooring zone) or next to a ship. The place is pushed into free water, the bow turned away from the
    /// island, the ships stopped; companions line up behind.
    /// </summary>
    void SailToPoint(float x, float z, ShipRt? nearShip, float dist, float ang)
    {
        var p = ships.FirstOrDefault(s => s.IsPlayer);
        if (p == null) return;
        if (nearShip != null) { x += MathF.Sin(ang) * Math.Max(dist, nearShip.Length + p.Length); z += MathF.Cos(ang) * Math.Max(dist, nearShip.Length + p.Length); }
        // heading: away from the island's centre (seaward), so the ship can sail off after mooring is declined
        float ay = p.Ay;
        if (island != null && islandBox.Size.X > 0f)
        {
            var c = islandBox.GetCenter();          // godot space: engine z = -c.Z
            ay = MathF.Atan2(x - c.X, z + c.Z);
        }
        float px = x, pz = z;
        if (OnLand(p, px, pz, ay)) (px, pz) = NearestWater(p, px, pz, ay);
        p.X = px; p.Z = pz; p.Ay = ay; p.Speed = 0f; p.Drift = 0f; p.TurnRate = 0f; p.Task = -1; SetAground(p, false);
        Publish(p);
        int k = 0;
        foreach (var s in ships.Where(s => !s.IsPlayer && !s.Dying && Relation(p.Index, s.Index) == 0 && Dist(p, s) < 3000f))
        {
            k++;
            float bx = px - MathF.Sin(ay) * 70f * k, bz = pz - MathF.Cos(ay) * 70f * k;
            if (OnLand(s, bx, bz, ay)) (bx, bz) = NearestWater(s, bx, bz, ay);
            s.X = bx; s.Z = bz; s.Ay = ay; s.Speed = 0f; s.Drift = 0f; Publish(s);
        }
        GD.Print($"[sea] sail to ({x:0},{z:0}): player at ({px:0},{pz:0}) ay={ay:0.00}, {k} companions");
    }

    /// <summary>After ship-to-ship pushes: a ship shoved onto land goes back to where it was.</summary>
    void KeepOffLand(ShipRt s, float oldX, float oldZ)
    {
        if (island == null || islandBody == null) return;
        if (OnLand(s, s.X, s.Z, s.Ay) && !OnLand(s, oldX, oldZ, s.Ay)) { s.X = oldX; s.Z = oldZ; }
    }
}
