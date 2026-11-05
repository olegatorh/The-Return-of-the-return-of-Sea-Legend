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

    public int NearestTri(float x, float z, float refY) => TriAt(x, z, refY);

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

    // ------------------------------------------------------------------ node walking (location/src/ptc_data.cpp)

    float[]? nrmX, nrmY, nrmZ;

    void BuildNormals()
    {
        nrmX = new float[Tris.Length]; nrmY = new float[Tris.Length]; nrmZ = new float[Tris.Length];
        for (int i = 0; i < Tris.Length; i++)
        {
            var t = Tris[i];
            float ux = Vx[t.B] - Vx[t.A], uy = Vy[t.B] - Vy[t.A], uz = Vz[t.B] - Vz[t.A];
            float wx = Vx[t.C] - Vx[t.A], wy = Vy[t.C] - Vy[t.A], wz = Vz[t.C] - Vz[t.A];
            float nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            float l = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l < 1e-12f) { nrmY[i] = 1f; continue; }
            if (ny < 0) l = -l;                                   // the patch is a floor: the normal looks up
            nrmX[i] = nx / l; nrmY[i] = ny / l; nrmZ[i] = nz / l;
        }
    }

    /// <summary>Unit normal of a triangle (pointing up) - the engine's <c>GetNodeNormal</c>.</summary>
    public (float x, float y, float z) NodeNormal(int node)
    {
        if (node < 0 || node >= Tris.Length) return (0, 0, 0);
        if (nrmX == null) BuildNormals();
        return (nrmX![node], nrmY![node], nrmZ![node]);
    }

    /// <summary>Surface height of a triangle's plane at (x, z) - <c>PtcData::FindHeight</c>.</summary>
    public float FindHeight(int node, float x, float z)
    {
        var (nx, ny, nz) = NodeNormal(node);
        if (MathF.Abs(ny) < 1e-6f) return Vy[Tris[node].A];
        float d = nx * Vx[Tris[node].A] + ny * Vy[Tris[node].A] + nz * Vz[Tris[node].A];
        return (d - nx * x - nz * z) / ny + 0.0001f;
    }

    /// <summary>True if (x, z) is inside the triangle in plan (the engine's edge test: not outside any edge).</summary>
    bool InsideTri(int node, float x, float z)
    {
        var t = Tris[node];
        for (int j = 0; j < 3; j++)
        {
            int s = j == 0 ? t.A : j == 1 ? t.B : t.C, e = j == 0 ? t.B : j == 1 ? t.C : t.A;
            float nx = -(Vz[e] - Vz[s]), nz = Vx[e] - Vx[s];
            if (x * nx + z * nz > Vx[s] * nx + Vz[s] * nz) return false;
        }
        return true;
    }

    /// <summary>The triangle under (x, z) whose surface is closest to y (the engine's <c>FindNode</c>); -1 outside the patch. <paramref name="h"/> = surface height there.</summary>
    public int FindNode(float x, float y, float z, out float h)
    {
        int node = -1; float best = float.MaxValue; h = 0f;
        for (int i = 0; i < Tris.Length; i++)
        {
            if (!InsideTri(i, x, z)) continue;
            float hh = FindHeight(i, x, z), d = MathF.Abs(y - hh);
            if (d < best) { best = d; h = hh; node = i; }
        }
        return node;
    }

    float[][]? vcache;

    /// <summary>PtcData::Trace: nearest crossing of the segment src -> dst with the patch (fraction along it, 2 = none); both sides count.</summary>
    public float Trace(float sx, float sy, float sz, float dx, float dy, float dz)
    {
        if (vcache == null) { vcache = new float[Vx.Length][]; for (int i = 0; i < vcache.Length; i++) vcache[i] = new[] { Vx[i], Vy[i], Vz[i] }; }
        float best = 2f;
        float lx = Math.Min(sx, dx), hx = Math.Max(sx, dx), lz = Math.Min(sz, dz), hz = Math.Max(sz, dz);
        foreach (var t in Tris)
        {
            if (Math.Max(Vx[t.A], Math.Max(Vx[t.B], Vx[t.C])) < lx || Math.Min(Vx[t.A], Math.Min(Vx[t.B], Vx[t.C])) > hx) continue;
            if (Math.Max(Vz[t.A], Math.Max(Vz[t.B], Vz[t.C])) < lz || Math.Min(Vz[t.A], Math.Min(Vz[t.B], Vz[t.C])) > hz) continue;
            float k = TriTrace.Triangle(vcache[t.A], vcache[t.B], vcache[t.C], sx, sy, sz, dx, dy, dz);
            if (k < best) best = k;
        }
        return best;
    }

    /// <summary>Set by <see cref="Move"/>: the move ran into the patch edge / stands on the floor / the edge's outward direction.</summary>
    public bool IsSlide, IsBearing;
    public float SlideDirX, SlideDirZ;

    /// <summary>
    /// <c>PtcData::Move</c>: moves <paramref name="pos"/> to <paramref name="to"/> across the triangle graph starting in <paramref name="curNode"/>; the
    /// move that leaves the patch slides along the border edge (the position stays inside), height is lifted onto the floor (never below it).
    /// Returns the node the position ended in (-1 = no node given).
    /// </summary>
    public int Move(int curNode, (float x, float y, float z) to, ref (float x, float y, float z) pos, int depth = 0)
    {
        IsSlide = false; IsBearing = false;
        if (curNode < 0 || curNode >= Tris.Length) return -1;
        float dx = to.x - pos.x, dz = to.z - pos.z;
        var ps = pos;
        int firstNode = curNode;
        float dst = dx * dx + dz * dz;
        if (dst == 0f || depth > 32)
        {
            if (to.y != pos.y)
            {
                float hy = FindHeight(curNode, pos.x, pos.z);
                pos.y = to.y;
                if (pos.y < hy) pos.y = hy;
            }
            return curNode;
        }
        dst = MathF.Sqrt(dst);
        float dirX = dx / dst, dirZ = dz / dst;
        float ndX = dirZ, ndZ = -dirX;                            // vertical plane through the displacement segment
        float d = ndX * pos.x + ndZ * pos.z;
        int fromNode = -2;
        for (int loop = 0; loop < 256; loop++)
        {
            var t = Tris[curNode];
            int j;
            for (j = 0; j < 3; j++)
            {
                int s = j == 0 ? t.A : j == 1 ? t.B : t.C, e = j == 0 ? t.B : j == 1 ? t.C : t.A;
                float nx = -(Vz[e] - Vz[s]), nz = Vx[e] - Vx[s];
                float nl = MathF.Sqrt(nx * nx + nz * nz);
                if (nl == 0f) continue;
                nx /= nl; nz /= nl;
                if (to.x * nx + to.z * nz > Vx[s] * nx + Vz[s] * nz) break;
            }
            if (j == 3)
            {
                pos = to;
                float h = FindHeight(curNode, pos.x, pos.z);
                if (pos.y < h) { pos.y = h; IsBearing = true; }
                return curNode;
            }
            int curEdge = -1;
            float pX = 0, pZ = 0;
            for (j = 0; j < 3; j++)
            {
                int nbj = j == 0 ? t.N0 : j == 1 ? t.N1 : t.N2;
                if (nbj == fromNode) continue;
                int s = j == 0 ? t.A : j == 1 ? t.B : t.C, e = j == 0 ? t.B : j == 1 ? t.C : t.A;
                float d1 = ndX * Vx[s] + ndZ * Vz[s] - d, d2 = ndX * Vx[e] + ndZ * Vz[e] - d;
                if (d1 * d2 > 0f) continue;
                if (d1 - d2 == 0f) continue;
                double k = d1 / (double)(d1 - d2);
                if (k < 0) k = 0;
                if (k > 1) k = 1;
                float qx = (float)(Vx[s] + (Vx[e] - Vx[s]) * k), qz = (float)(Vz[s] + (Vz[e] - Vz[s]) * k);
                if ((d1 = dirX * (qx - ps.x) + dirZ * (qz - ps.z)) < -0.00001f) continue;
                if ((d2 = dirX * (to.x - qx) + dirZ * (to.z - qz)) < -0.00001f) continue;
                curEdge = j; pX = qx; pZ = qz;
                if (nbj >= 0) break;
                if (d1 != 0f && d2 != 0f) break;
            }
            if (curEdge == -1)
            {
                IsSlide = true;
                return firstNode;
            }
            int nb = curEdge == 0 ? t.N0 : curEdge == 1 ? t.N1 : t.N2;
            if (nb >= 0 && nb < Tris.Length)
            {
                fromNode = curNode;
                curNode = nb;
                continue;
            }
            // a border edge: slide along it
            {
                int s = curEdge == 0 ? t.A : curEdge == 1 ? t.B : t.C, e = curEdge == 0 ? t.B : curEdge == 1 ? t.C : t.A;
                float nx = -(Vz[e] - Vz[s]), nz = Vx[e] - Vx[s];
                float dd = nx * nx + nz * nz;
                if (dd == 0f) { pos = (pX, pos.y, pZ); return curNode; }
                dd = 1f / MathF.Sqrt(dd);
                nx *= dd; nz *= dd;
                float sldX = to.x - pX, sldZ = to.z - pZ;
                dd = nx * sldX + nz * sldZ;
                if (dd > 0f) dd += 0.001f;
                if (dd < 0f) dd -= 0.001f;
                var np = (x: to.x - nx * dd, y: to.y, z: to.z - nz * dd);
                if (np.x == to.x && np.z == to.z) { IsSlide = true; return firstNode; }
                np = (pos.x + (np.x - pos.x) * 0.99f, pos.y + (np.y - pos.y) * 0.99f, pos.z + (np.z - pos.z) * 0.99f);
                curNode = Move(firstNode, np, ref pos, depth + 1);
                IsSlide = true;
                SlideDirX = dd >= 0f ? nx : -nx; SlideDirZ = dd >= 0f ? nz : -nz;
                return curNode;
            }
        }
        return curNode;
    }

    /// <summary>The force pushing away from the border edges of a node (<c>PtcData::FindForce</c>), used by the NPCs as the slide force.</summary>
    public (float x, float z) EdgeForce(int node)
    {
        if (node < 0 || node >= Tris.Length) return (0, 0);
        var t = Tris[node];
        float fx = 0, fz = 0;
        for (int i = 0; i < 3; i++)
        {
            int nb = i == 0 ? t.N0 : i == 1 ? t.N1 : t.N2;
            if (nb >= 0) continue;
            int s = i == 0 ? t.A : i == 1 ? t.B : t.C, e = i == 0 ? t.B : i == 1 ? t.C : t.A;
            float nx = Vz[e] - Vz[s], nz = -(Vx[e] - Vx[s]);
            float nl = MathF.Sqrt(nx * nx + nz * nz);
            if (nl == 0f) continue;
            fx += nx / nl; fz += nz / nl;
        }
        return (fx, fz);
    }
}
