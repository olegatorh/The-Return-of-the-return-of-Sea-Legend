using System;
using System.Collections.Generic;

namespace StormScript.Formats;

/// <summary>
/// Segment against triangle (PtcData::Trace of the engine): the fraction 0..1 along src -> dst where the segment crosses the plane inside the triangle (both
/// sides count), or 2 when it misses. Vertices are in the engine's Direct3D space.
/// </summary>
public static class TriTrace
{
    public static float Triangle(float[] a, float[] b, float[] c, float sx, float sy, float sz, float dx, float dy, float dz)
    {
        float e1x = b[0] - a[0], e1y = b[1] - a[1], e1z = b[2] - a[2];
        float e2x = c[0] - a[0], e2y = c[1] - a[1], e2z = c[2] - a[2];
        float nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
        float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (nl < 1e-12f) return 2f;
        nx /= nl; ny /= nl; nz /= nl;
        float dst = nx * a[0] + ny * a[1] + nz * a[2];
        float d1 = nx * sx + ny * sy + nz * sz - dst, d2 = nx * dx + ny * dy + nz * dz - dst;
        if (d1 * d2 > 0f) return 2f;
        float k = d1 - d2 != 0f ? d1 / (d1 - d2) : 0f;
        k = Math.Clamp(k, 0f, 1f);
        float px = sx + (dx - sx) * k, py = sy + (dy - sy) * k, pz = sz + (dz - sz) * k;
        // the point must lie on the inner side of the three edges
        if (Edge(a, b, nx, ny, nz, px, py, pz) || Edge(b, c, nx, ny, nz, px, py, pz) || Edge(c, a, nx, ny, nz, px, py, pz)) return 2f;
        return k;
    }

    static bool Edge(float[] v0, float[] v1, float nx, float ny, float nz, float px, float py, float pz)
    {
        float ex = (v1[1] - v0[1]) * nz - (v1[2] - v0[2]) * ny, ey = (v1[2] - v0[2]) * nx - (v1[0] - v0[0]) * nz, ez = (v1[0] - v0[0]) * ny - (v1[1] - v0[1]) * nx;
        return ex * (px - v0[0]) + ey * (py - v0[1]) + ez * (pz - v0[2]) > 0f;
    }
}

/// <summary>A set of triangles used only for tracing (the location's "jump patch" model: where a ledge may be jumped off).</summary>
public sealed class TriSoup
{
    readonly List<float[]> verts = new();
    readonly List<(int a, int b, int c)> tris = new();
    float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

    public int Count => tris.Count;

    public void Add(float[] pos, int[] index)
    {
        int first = verts.Count;
        for (int i = 0; i + 2 < pos.Length; i += 3)
        {
            verts.Add(new[] { pos[i], pos[i + 1], pos[i + 2] });
            minX = Math.Min(minX, pos[i]); maxX = Math.Max(maxX, pos[i]);
            minY = Math.Min(minY, pos[i + 1]); maxY = Math.Max(maxY, pos[i + 1]);
            minZ = Math.Min(minZ, pos[i + 2]); maxZ = Math.Max(maxZ, pos[i + 2]);
        }
        for (int i = 0; i + 2 < index.Length; i += 3) tris.Add((first + index[i], first + index[i + 1], first + index[i + 2]));
    }

    public string BoundsText => $"x {minX:0.0}..{maxX:0.0} y {minY:0.0}..{maxY:0.0} z {minZ:0.0}..{maxZ:0.0}";

    /// <summary>Nearest crossing of the segment (fraction along it), 2 = none.</summary>
    public float Trace(float sx, float sy, float sz, float dx, float dy, float dz)
    {
        if (tris.Count == 0 || Math.Max(sx, dx) < minX || Math.Min(sx, dx) > maxX || Math.Max(sy, dy) < minY || Math.Min(sy, dy) > maxY || Math.Max(sz, dz) < minZ || Math.Min(sz, dz) > maxZ) return 2f;
        float best = 2f;
        foreach (var (a, b, c) in tris)
        {
            float k = TriTrace.Triangle(verts[a], verts[b], verts[c], sx, sy, sz, dx, dy, dz);
            if (k < best) best = k;
        }
        return best;
    }
}
