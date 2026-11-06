using System;
using System.Linq;
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

    /// <summary>Locators of Islands.gm: group "merchant" (random goals of the merchant ships, WdmIslands::GetRandomMerchantPoint) and "quests" (named goals / starts).</summary>
    public readonly System.Collections.Generic.List<(float x, float z)> Merchants = new();
    public readonly System.Collections.Generic.List<(string name, float x, float z)> Quests = new();
    public float WorldMinX, WorldMinZ, WorldMaxX, WorldMaxZ;

    public static WorldLand? Build(GmFile g)
    {
        var tris = new System.Collections.Generic.List<(float x0, float z0, float x1, float z1, float x2, float z2)>();
        float lx = float.MaxValue, lz = float.MaxValue, hx = float.MinValue, hz = float.MinValue;
        int oi = -1;
        foreach (var o in g.Objects)
        {
            oi++;
            if (System.Environment.GetEnvironmentVariable("SEADOGS_MAPOBJ") is { } only && only != oi.ToString()) continue;
            var m = g.ExtractObject(o);
            if (m == null) continue;
            if (System.Environment.GetEnvironmentVariable("SEADOGS_MAPLOG") != null) { float mn = float.MaxValue, mx = float.MinValue; for (int k = 1; k < m.Pos.Length; k += 3) { mn = Math.Min(mn, m.Pos[k]); mx = Math.Max(mx, m.Pos[k]); } Godot.GD.Print($"[map] Islands.gm object {oi}: {m.Pos.Length / 3} vertices, y {mn:0.0}..{mx:0.0}, material {o.Material}"); }
            for (int i = 0; i + 2 < m.Index.Length; i += 3)
            {
                int a = m.Index[i], b = m.Index[i + 1], c = m.Index[i + 2];
                float ya = m.Pos[a * 3 + 1], yb = m.Pos[b * 3 + 1], yc = m.Pos[c * 3 + 1];
                if (Math.Max(ya, Math.Max(yb, yc)) < (float.TryParse(System.Environment.GetEnvironmentVariable("SEADOGS_MAPY"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ymin) ? ymin : 0.15f)) continue;                 // below the water line: not land
                float x0 = m.Pos[a * 3], z0 = m.Pos[a * 3 + 2], x1 = m.Pos[b * 3], z1 = m.Pos[b * 3 + 2], x2 = m.Pos[c * 3], z2 = m.Pos[c * 3 + 2];
                tris.Add((x0, z0, x1, z1, x2, z2));
                lx = Math.Min(lx, Math.Min(x0, Math.Min(x1, x2))); hx = Math.Max(hx, Math.Max(x0, Math.Max(x1, x2)));
                lz = Math.Min(lz, Math.Min(z0, Math.Min(z1, z2))); hz = Math.Max(hz, Math.Max(z0, Math.Max(z1, z2)));
            }
        }
        if (tris.Count == 0) return null;
        var land = new WorldLand { minX = lx - 10f, minZ = lz - 10f, WorldMinX = lx, WorldMinZ = lz, WorldMaxX = hx, WorldMaxZ = hz };
        foreach (var lb in g.Labels)
        {
            if (lb.Group.Equals("merchant", StringComparison.OrdinalIgnoreCase)) land.Merchants.Add((lb.X, lb.Z));
            else if (lb.Group.Equals("quests", StringComparison.OrdinalIgnoreCase) && lb.Name.Length > 0) land.Quests.Add((lb.Name, lb.X, lb.Z));
        }
        if (System.Environment.GetEnvironmentVariable("SEADOGS_MAPLOG") != null)
        {
            Godot.GD.Print($"[map] Islands.gm labels: {g.Labels.Count} ({string.Join(", ", g.Labels.GroupBy(l => l.Group).Select(x => x.Key + "=" + x.Count()))}); merchant goals {land.Merchants.Count}, quest locators {land.Quests.Count}; extent x {lx:0}..{hx:0}, z {lz:0}..{hz:0}");
            foreach (var m in land.Merchants.Take(40)) Godot.GD.Print($"[map]   merchant goal ({m.x:0},{m.z:0})");
        }
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
        if (System.Environment.GetEnvironmentVariable("SEADOGS_MAPMASK") is { } mp)
        {
            var img = Godot.Image.CreateEmpty(land.w, land.h, false, Godot.Image.Format.L8);
            for (int z = 0; z < land.h; z++) for (int x = 0; x < land.w; x++) img.SetPixel(x, land.h - 1 - z, land.grid[z * land.w + x] != 0 ? Godot.Colors.White : Godot.Colors.Black);
            img.SavePng(mp);
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

    /// <summary>The nearest water point to (x, z) (the point itself when it is water): used to put a ship that spawned on a coast cell back into the sea.</summary>
    public (float x, float z) NearestSea(float x, float z)
    {
        if (!IsLand(x, z)) return (x, z);
        for (float r = Cell; r <= 120f; r += Cell)
            for (int k = 0; k < 24; k++)
            {
                float a = k * MathF.Tau / 24f, px = x + MathF.Cos(a) * r, pz = z + MathF.Sin(a) * r;
                if (!IsLand(px, pz) && !IsLand(px + MathF.Cos(a) * Cell, pz + MathF.Sin(a) * Cell)) return (px, pz);     // one more cell of margin
            }
        return (x, z);
    }

    /// <summary>True if land lies within <paramref name="r"/> of the point along the heading ahead.</summary>
    public bool LandAhead(float x, float z, float ay, float r) => IsLand(x + MathF.Sin(ay) * r, z + MathF.Cos(ay) * r) || IsLand(x + MathF.Sin(ay) * r * 0.5f, z + MathF.Cos(ay) * r * 0.5f);

    // ------------------------------------------------------------------ navigation for the other ships (the original asks a walk patch of the islands: WdmIslands::FindDirection / FindReaction)
    const int NavK = 3;                     // a navigation cell = NavK x NavK land cells (9 units)
    bool[]? navBlocked; int nw, nh;

    void BuildNav()
    {
        if (navBlocked != null) return;
        nw = w / NavK + 1; nh = h / NavK + 1;
        var raw = new bool[nw * nh];
        for (int z = 0; z < h; z++) for (int x = 0; x < w; x++) if (grid[z * w + x] != 0) raw[(z / NavK) * nw + x / NavK] = true;
        navBlocked = new bool[nw * nh];
        for (int z = 0; z < nh; z++) for (int x = 0; x < nw; x++)           // one cell of margin around the coast
        {
            bool b = false;
            for (int dz = -1; dz <= 1 && !b; dz++) for (int dx = -1; dx <= 1 && !b; dx++)
            {
                int xx = x + dx, zz = z + dz;
                if (xx >= 0 && zz >= 0 && xx < nw && zz < nh && raw[zz * nw + xx]) b = true;
            }
            navBlocked[z * nw + x] = b;
        }
    }

    float NavX(int cx) => minX + (cx + 0.5f) * Cell * NavK;
    float NavZ(int cz) => minZ + (cz + 0.5f) * Cell * NavK;
    bool NavFree(float x, float z)
    {
        BuildNav();
        int cx = (int)((x - minX) / (Cell * NavK)), cz = (int)((z - minZ) / (Cell * NavK));
        return cx < 0 || cz < 0 || cx >= nw || cz >= nh || !navBlocked![cz * nw + cx];
    }

    /// <summary>True if the straight line is free of the coast (with the navigation margin).</summary>
    public bool LineFree(float x0, float z0, float x1, float z1)
    {
        float len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
        int n = Math.Max(1, (int)(len / (Cell * 0.9f)));
        for (int i = 1; i <= n; i++)
        {
            float t = (float)i / n;
            if (!NavFree(x0 + (x1 - x0) * t, z0 + (z1 - z0) * t)) return false;
        }
        return true;
    }

    /// <summary>The way round the islands from one point to another: waypoints (the goal last), or null when the goal cannot be reached. A* on the coarse grid, then string pulled.</summary>
    public System.Collections.Generic.List<(float x, float z)>? FindPath(float x0, float z0, float x1, float z1)
    {
        BuildNav();
        if (LineFree(x0, z0, x1, z1)) return new() { (x1, z1) };
        int Cx(float x) => Math.Clamp((int)((x - minX) / (Cell * NavK)), 0, nw - 1);
        int Cz(float z) => Math.Clamp((int)((z - minZ) / (Cell * NavK)), 0, nh - 1);
        int Free(int c)                                  // the nearest free cell to c (breadth by rings)
        {
            if (!navBlocked![c]) return c;
            int cx = c % nw, cz = c / nw;
            for (int r = 1; r < 14; r++)
                for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    int xx = cx + dx, zz = cz + dz;
                    if (xx >= 0 && zz >= 0 && xx < nw && zz < nh && !navBlocked[zz * nw + xx]) return zz * nw + xx;
                }
            return -1;
        }
        int s = Free(Cz(z0) * nw + Cx(x0)), g = Free(Cz(z1) * nw + Cx(x1));
        if (s < 0 || g < 0) return null;
        var cost = new float[nw * nh]; Array.Fill(cost, float.MaxValue);
        var from = new int[nw * nh]; Array.Fill(from, -1);
        var open = new System.Collections.Generic.PriorityQueue<int, float>();
        cost[s] = 0; open.Enqueue(s, 0);
        int gx = g % nw, gz = g / nw;
        while (open.Count > 0)
        {
            int c = open.Dequeue();
            if (c == g) break;
            int cx = c % nw, cz = c / nw;
            for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                int xx = cx + dx, zz = cz + dz;
                if (xx < 0 || zz < 0 || xx >= nw || zz >= nh) continue;
                int n = zz * nw + xx;
                if (navBlocked![n]) continue;
                if (dx != 0 && dz != 0 && (navBlocked[cz * nw + xx] || navBlocked[zz * nw + cx])) continue;     // no cutting corners
                float nc = cost[c] + (dx != 0 && dz != 0 ? 1.4142f : 1f);
                if (nc >= cost[n]) continue;
                cost[n] = nc; from[n] = c;
                float h2 = MathF.Sqrt((xx - gx) * (xx - gx) + (zz - gz) * (zz - gz));
                open.Enqueue(n, nc + h2);
            }
        }
        if (from[g] < 0 && g != s) return null;
        var cells = new System.Collections.Generic.List<(float x, float z)>();
        for (int c = g; c >= 0; c = from[c]) { cells.Add((NavX(c % nw), NavZ(c / nw))); if (c == s) break; }
        cells.Reverse();
        cells.Add((x1, z1));
        // string pulling: from each point jump to the farthest waypoint that is in sight
        var res = new System.Collections.Generic.List<(float x, float z)>();
        float px = x0, pz = z0; int i = 0;
        while (i < cells.Count)
        {
            int far = i;
            for (int k = cells.Count - 1; k > i; k--) if (LineFree(px, pz, cells[k].x, cells[k].z)) { far = k; break; }
            res.Add(cells[far]); px = cells[far].x; pz = cells[far].z; i = far + 1;
        }
        return res;
    }

    /// <summary>The push away from the coast (WdmIslands::FindReaction): up to 1, from land within 20 units.</summary>
    public (float x, float z) Repulsion(float x, float z, float radius = 20f)
    {
        float rx = 0, rz = 0;
        for (int k = 0; k < 16; k++)
        {
            float a = k * MathF.Tau / 16f, dx = MathF.Sin(a), dz = MathF.Cos(a);
            for (float r = Cell; r <= radius; r += Cell)
                if (IsLand(x + dx * r, z + dz * r)) { float f = 1f - r / radius; rx -= dx * f; rz -= dz * f; break; }
        }
        float l = MathF.Sqrt(rx * rx + rz * rz);
        if (l > 1f) { rx /= l; rz /= l; }
        return (rx * 0.5f, rz * 0.5f);
    }
}
