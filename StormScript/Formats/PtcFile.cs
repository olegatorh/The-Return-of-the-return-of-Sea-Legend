using System.Buffers.Binary;

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
        return p;
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
