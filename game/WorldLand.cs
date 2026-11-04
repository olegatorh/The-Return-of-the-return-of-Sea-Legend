using System;
using StormScript.Formats;

namespace SeaDogs;

/// <summary>
/// A land mask of the world map: the triangles of Islands.gm (above the water line) rasterised on a grid, so ships can be kept off the islands.
/// Coordinates are the engine's map coordinates (x, z).
/// </summary>
public sealed class WorldLand
{
    const float Cell = 3f;
    byte[] grid = [];
    int w, h;
    float minX, minZ;

    public static WorldLand? Build(GmFile g)
    {
        var tris = new System.Collections.Generic.List<(float x0, float z0, float x1, float z1, float x2, float z2)>();
        float lx = float.MaxValue, lz = float.MaxValue, hx = float.MinValue, hz = float.MinValue;
        foreach (var o in g.Objects)
        {
            var m = g.ExtractObject(o);
            if (m == null) continue;
            for (int i = 0; i + 2 < m.Index.Length; i += 3)
            {
                int a = m.Index[i], b = m.Index[i + 1], c = m.Index[i + 2];
                float ya = m.Pos[a * 3 + 1], yb = m.Pos[b * 3 + 1], yc = m.Pos[c * 3 + 1];
                if (Math.Max(ya, Math.Max(yb, yc)) < 0.15f) continue;                 // below the water line: not land
                float x0 = m.Pos[a * 3], z0 = m.Pos[a * 3 + 2], x1 = m.Pos[b * 3], z1 = m.Pos[b * 3 + 2], x2 = m.Pos[c * 3], z2 = m.Pos[c * 3 + 2];
                tris.Add((x0, z0, x1, z1, x2, z2));
                lx = Math.Min(lx, Math.Min(x0, Math.Min(x1, x2))); hx = Math.Max(hx, Math.Max(x0, Math.Max(x1, x2)));
                lz = Math.Min(lz, Math.Min(z0, Math.Min(z1, z2))); hz = Math.Max(hz, Math.Max(z0, Math.Max(z1, z2)));
            }
        }
        if (tris.Count == 0) return null;
        var land = new WorldLand { minX = lx - 10f, minZ = lz - 10f };
        land.w = (int)((hx - lx + 20f) / Cell) + 1; land.h = (int)((hz - lz + 20f) / Cell) + 1;
        land.grid = new byte[land.w * land.h];
        foreach (var (x0, z0, x1, z1, x2, z2) in tris)
        {
            int cx0 = (int)((Math.Min(x0, Math.Min(x1, x2)) - land.minX) / Cell), cx1 = (int)((Math.Max(x0, Math.Max(x1, x2)) - land.minX) / Cell);
            int cz0 = (int)((Math.Min(z0, Math.Min(z1, z2)) - land.minZ) / Cell), cz1 = (int)((Math.Max(z0, Math.Max(z1, z2)) - land.minZ) / Cell);
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    float px = land.minX + (cx + 0.5f) * Cell, pz = land.minZ + (cz + 0.5f) * Cell;
                    if (Inside(px, pz, x0, z0, x1, z1, x2, z2, Cell * 0.6f)) land.grid[cz * land.w + cx] = 1;
                }
        }
        int n = 0; foreach (var b in land.grid) if (b != 0) n++;
        Godot.GD.Print($"[map] land mask {land.w}x{land.h} cells, {n} land cells ({n * Cell * Cell / 1000f:0} k units2)");
        return land;
    }

    static bool Inside(float px, float pz, float x0, float z0, float x1, float z1, float x2, float z2, float margin)
    {
        // the cell centre is inside the triangle (edge function signs agree), with a margin of about half a cell so thin slivers still count
        float d0 = (x1 - x0) * (pz - z0) - (z1 - z0) * (px - x0);
        float d1 = (x2 - x1) * (pz - z1) - (z2 - z1) * (px - x1);
        float d2 = (x0 - x2) * (pz - z2) - (z0 - z2) * (px - x2);
        float t = margin * Math.Max(MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)), Math.Max(MathF.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1)), MathF.Sqrt((x0 - x2) * (x0 - x2) + (z0 - z2) * (z0 - z2))));
        return (d0 >= -t && d1 >= -t && d2 >= -t) || (d0 <= t && d1 <= t && d2 <= t);
    }

    public bool IsLand(float x, float z)
    {
        int cx = (int)((x - minX) / Cell), cz = (int)((z - minZ) / Cell);
        return cx >= 0 && cz >= 0 && cx < w && cz < h && grid[cz * w + cx] != 0;
    }

    /// <summary>True if land lies within <paramref name="r"/> of the point along the heading ahead.</summary>
    public bool LandAhead(float x, float z, float ay, float r) => IsLand(x + MathF.Sin(ay) * r, z + MathF.Cos(ay) * r) || IsLand(x + MathF.Sin(ay) * r * 0.5f, z + MathF.Cos(ay) * r * 0.5f);
}
