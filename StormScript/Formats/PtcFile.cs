using System.Buffers.Binary;
using System.Collections.Generic;

namespace StormScript.Formats;

/// <summary>
/// Walkable-surface patch of a location (.ptc, id "PTC ", version "1.01"): triangles with neighbour links, vertices, normals,
/// a collision grid and materials. Direct3D space (y up). Only what is needed to stand and walk on the surface is read.
/// </summary>
public sealed class PtcFile
{
    public struct Tri
    {
        public ushort A, B, C;
        public short N0, N1, N2;        // neighbour triangles across the edges (A-B, B-C, C-A); negative = none
        public sbyte Material, Flags;
    }

    public bool IsSea;
    public float[] Min = new float[3], Max = new float[3];
    public float[] Vx = [], Vy = [], Vz = [];
    public Tri[] Tris = [];

    public static PtcFile Parse(byte[] d)
    {
        int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
        float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o));
        if (d.Length < 76 || I(0) != 0x20435450) throw new InvalidDataException("not a .ptc patch");   // "PTC "
        var p = new PtcFile { IsSea = I(16) == 0x20414553 };
        int nTri = I(24), nVert = I(28);
        for (int i = 0; i < 3; i++) { p.Min[i] = F(52 + 4 * i); p.Max[i] = F(64 + 4 * i); }
        int o = 76;
        p.Tris = new Tri[nTri];
        for (int i = 0; i < nTri; i++, o += 16)
        {
            p.Tris[i] = new Tri
            {
                A = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o)),
                B = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 2)),
                C = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 4)),
                N0 = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o + 8)),
                N1 = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o + 10)),
                N2 = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o + 12)),
                Material = (sbyte)d[o + 14], Flags = (sbyte)d[o + 15],
            };
        }
        p.Vx = new float[nVert]; p.Vy = new float[nVert]; p.Vz = new float[nVert];
        for (int i = 0; i < nVert; i++, o += 12) { p.Vx[i] = F(o); p.Vy[i] = F(o + 4); p.Vz[i] = F(o + 8); }
        // the file ends with the material table: int count + 16 names of 16 chars ("snd_grass", "add_snd_stone", "lambert4SG"...);
        // Tri.Material indexes it. The engine picks the step sound by it. Two broken files (fort2/fort3) have garbage there.
        if (d.Length >= o + 260)
        {
            int cnt = I(d.Length - 260);
            if (cnt > 0 && cnt <= 16)
            {
                p.Materials = new string[cnt];
                for (int i = 0; i < cnt; i++)
                {
                    int s = d.Length - 256 + 16 * i, e = s;
                    while (e < s + 16 && d[e] != 0) e++;
                    p.Materials[i] = System.Text.Encoding.Latin1.GetString(d, s, e - s);
                }
            }
        }
        return p;
    }

    public string[] Materials = [];

    /// <summary>Material name of the patch triangle under (x, z) nearest to height y ("" if unknown).</summary>
    public string MaterialAt(float x, float z, float y)
    {
        int t = TriAt(x, z, y);
        if (t < 0) return "";
        int m = Tris[t].Material;
        return m >= 0 && m < Materials.Length ? Materials[m] : "";
    }

    /// <summary>
    /// Nearest point of the patch to (x,y,z): a triangle containing (x,z) in plan wins by height difference, otherwise the closest
    /// point on any triangle edge. Returns false for an empty patch.
    /// </summary>
    public bool Snap(float x, float y, float z, out float ox, out float oy, out float oz)
    {
        ox = x; oy = y; oz = z;
        float best = float.MaxValue;
        bool found = false;
        foreach (var t in Tris)
        {
            float ax = Vx[t.A], ay = Vy[t.A], az = Vz[t.A];
            float bx = Vx[t.B], by = Vy[t.B], bz = Vz[t.B];
            float cx = Vx[t.C], cy = Vy[t.C], cz = Vz[t.C];
            float den = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (MathF.Abs(den) > 1e-9f)
            {
                float u = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / den;
                float v = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / den;
                float w = 1 - u - v;
                if (u >= 0 && v >= 0 && w >= 0)
                {
                    float py = u * ay + v * by + w * cy;
                    float score = MathF.Abs(py - y);
                    if (score < best) { best = score; ox = x; oy = py; oz = z; found = true; }
                    continue;
                }
            }
            // outside in plan: closest point on the three edges (in the xz plane), height interpolated along the edge
            for (int e = 0; e < 3; e++)
            {
                float sx = e == 0 ? ax : e == 1 ? bx : cx, sy = e == 0 ? ay : e == 1 ? by : cy, sz = e == 0 ? az : e == 1 ? bz : cz;
                float tx = e == 0 ? bx : e == 1 ? cx : ax, ty = e == 0 ? by : e == 1 ? cy : ay, tz = e == 0 ? bz : e == 1 ? cz : az;
                float dx = tx - sx, dz = tz - sz, len2 = dx * dx + dz * dz;
                float k = len2 < 1e-12f ? 0 : Math.Clamp(((x - sx) * dx + (z - sz) * dz) / len2, 0f, 1f);
                float qx = sx + k * dx, qz = sz + k * dz, qy = sy + k * (ty - sy);
                float dist = MathF.Sqrt((qx - x) * (qx - x) + (qz - z) * (qz - z)) + MathF.Abs(qy - y) * 0.5f + 100f;   // inside-triangles always win
                if (dist < best) { best = dist; ox = qx; oy = qy; oz = qz; found = true; }
            }
        }
        return found;
    }

    int TriAt(float x, float z, float refY)
    {
        int best = -1; float bd = float.MaxValue;
        for (int i = 0; i < Tris.Length; i++)
        {
            var t = Tris[i];
            float ax = Vx[t.A], az = Vz[t.A], bx = Vx[t.B], bz = Vz[t.B], cx = Vx[t.C], cz = Vz[t.C];
            float den = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (MathF.Abs(den) > 1e-9f)
            {
                float u = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / den, v = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / den, w = 1 - u - v;
                if (u >= -1e-4f && v >= -1e-4f && w >= -1e-4f)
                {
                    float dy = MathF.Abs(u * Vy[t.A] + v * Vy[t.B] + w * Vy[t.C] - refY);
                    if (dy < bd) { bd = dy; best = i; }
                    continue;
                }
            }
        }
        if (best >= 0) return best;
        for (int i = 0; i < Tris.Length; i++)                    // outside: the nearest triangle centre
        {
            var t = Tris[i];
            float mx = (Vx[t.A] + Vx[t.B] + Vx[t.C]) / 3f, mz = (Vz[t.A] + Vz[t.B] + Vz[t.C]) / 3f;
            float d = (mx - x) * (mx - x) + (mz - z) * (mz - z);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    /// <summary>A walking route over the patch (A* over the triangle neighbour links): waypoints in plan, ending at the target. Null if unreachable.</summary>
    public List<(float x, float z)>? FindPath(float sx, float sz, float sy, float tx, float tz, float ty)
    {
        if (Tris.Length == 0) return null;
        int s0 = TriAt(sx, sz, sy), t0 = TriAt(tx, tz, ty);
        if (s0 < 0 || t0 < 0) return null;
        float Cx(int i) => (Vx[Tris[i].A] + Vx[Tris[i].B] + Vx[Tris[i].C]) / 3f;
        float Cz(int i) => (Vz[Tris[i].A] + Vz[Tris[i].B] + Vz[Tris[i].C]) / 3f;
        float H(int i) => MathF.Sqrt((Cx(i) - tx) * (Cx(i) - tx) + (Cz(i) - tz) * (Cz(i) - tz));
        var g = new Dictionary<int, float> { [s0] = 0 };
        var from = new Dictionary<int, int>();
        var open = new PriorityQueue<int, float>();
        open.Enqueue(s0, H(s0));
        var done = new HashSet<int>();
        while (open.Count > 0)
        {
            int c = open.Dequeue();
            if (!done.Add(c)) continue;
            if (c == t0) break;
            foreach (int n in new[] { (int)Tris[c].N0, Tris[c].N1, Tris[c].N2 })
            {
                if (n < 0 || n >= Tris.Length || done.Contains(n)) continue;
                float ng = g[c] + MathF.Sqrt((Cx(c) - Cx(n)) * (Cx(c) - Cx(n)) + (Cz(c) - Cz(n)) * (Cz(c) - Cz(n)));
                if (!g.TryGetValue(n, out var old) || ng < old) { g[n] = ng; from[n] = c; open.Enqueue(n, ng + H(n)); }
            }
        }
        if (!done.Contains(t0)) return null;
        var path = new List<(float, float)>();
        for (int c = t0; c != s0; c = from[c]) path.Add((Cx(c), Cz(c)));
        path.Reverse();
        path.Add((tx, tz));
        return path;
    }

    /// <summary>
    /// True if (x,z) lies inside a triangle of the patch whose surface is within maxDy of refY; y receives the surface height
    /// (the closest one when several floors overlap). Used to keep a walking character on the walkable surface.
    /// </summary>
    public bool Contains(float x, float z, float refY, float maxDy, out float y)
    {
        y = refY;
        float best = maxDy + 1e-3f;
        bool found = false;
        foreach (var t in Tris)
        {
            float ax = Vx[t.A], az = Vz[t.A], bx = Vx[t.B], bz = Vz[t.B], cx = Vx[t.C], cz = Vz[t.C];
            float den = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (MathF.Abs(den) < 1e-9f) continue;
            float u = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / den;
            float v = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / den;
            float w = 1 - u - v;
            if (u < -1e-4f || v < -1e-4f || w < -1e-4f) continue;
            float py = u * Vy[t.A] + v * Vy[t.B] + w * Vy[t.C];
            float dy = MathF.Abs(py - refY);
            if (dy < best) { best = dy; y = py; found = true; }
        }
        return found;
    }
}
