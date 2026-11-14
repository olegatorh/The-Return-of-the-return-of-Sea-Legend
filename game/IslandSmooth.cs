using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SeaDogs;

/// <summary>
/// Visual-only smoothing of the island models seen from the sea. The original island meshes are coarse (a few thousand vertices over ~3 km): big flat triangles give a
/// faceted, "crystal" look and a flat vertex-light. Here every island object is welded by position, long edges are split adaptively (midpoint subdivision, new points
/// pulled toward the neighbours' tangent planes: Phong tessellation), the vertex lighting is interpolated and a slope shading from SMOOTH normals (angle limit 60 deg)
/// is applied on top of it. Collision / ground height keep the original rough geometry (Sea.BuildIslandCollision runs before this), so ships behave as before.
/// SEADOGS_ROUGHISLAND=1 keeps the original geometry.
/// </summary>
static class IslandSmooth
{
    public static readonly bool Rough = System.Environment.GetEnvironmentVariable("SEADOGS_ROUGHISLAND") != null;

    const float MinEdge = 14f;          // edges shorter than this are never split
    const float MaxVertsFactor = 14f;   // vertex budget of one object = original vertices x this
    const int MaxVertsPerObject = 110000;
    const float SmoothCos = 0.5f;       // cos 60 deg: faces at a steeper angle keep a crease
    const float Phong = 0.6f;
    const float DetailMetres = 14f;
    const float Relief = 0.9f;
    static readonly Vector3 ReliefSun = new Vector3(-0.45f, 0.75f, 0.35f).Normalized();

    sealed class MeshData
    {
        public List<Vector3> P = new(); public List<Vector2> UV = new(); public List<Color> C = new(); public List<int> W = new();
        public List<Vector3> WN = new();          // per welded point: area-weighted normal (Phong tessellation)
        public List<int> Idx = new();
    }

    /// <summary>Smooth every suitable object of the island (call after the collision has been built from the rough meshes).</summary>
    public static void Apply(Node3D island, bool hasColors)
    {
        if (Rough || !hasColors) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int before = 0, after = 0, done = 0;
        foreach (var mi in island.GetChildren().OfType<MeshInstance3D>())
        {
            string n = mi.Name.ToString();
            if (n.StartsWith("Palm", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Prichal", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Pier", StringComparison.OrdinalIgnoreCase)) continue;
            if (mi.Mesh is not ArrayMesh am || am.GetSurfaceCount() != 1) continue;
            if (am.SurfaceGetMaterial(0) is StandardMaterial3D { Transparency: not BaseMaterial3D.TransparencyEnum.Disabled }) continue;       // palms and other cut-outs
            var arr = am.SurfaceGetArrays(0);
            if (arr[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.Nil) continue;
            var verts = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (verts.Length < 30) continue;
            try
            {
                var nm = Smooth(arr, am.SurfaceGetMaterial(0));
                if (nm == null) continue;
                before += verts.Length; after += nm.GetSurfaceCount() > 0 ? nm.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length : 0;
                mi.Mesh = nm; done++;
            }
            catch (Exception e) { GD.PrintErr($"[island] smooth {n}: {e.Message}"); }
        }
        GD.Print($"[island] smoothed {done} objects: {before} -> {after} vertices in {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms");
    }

    static ArrayMesh? Smooth(Godot.Collections.Array arr, Material? mat)
    {
        var md = new MeshData();
        var v = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var uv = arr[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var col = arr[(int)Mesh.ArrayType.Color].AsColorArray();
        var idx = arr[(int)Mesh.ArrayType.Index].AsInt32Array();
        if (idx.Length < 3 || uv.Length != v.Length || col.Length != v.Length) return null;
        var weld = new Dictionary<Vector3I, int>();
        for (int i = 0; i < v.Length; i++)
        {
            var key = new Vector3I((int)MathF.Round(v[i].X * 1000f), (int)MathF.Round(v[i].Y * 1000f), (int)MathF.Round(v[i].Z * 1000f));
            if (!weld.TryGetValue(key, out int w)) { w = weld.Count; weld[key] = w; }
            md.P.Add(v[i]); md.UV.Add(uv[i]); md.C.Add(col[i]); md.W.Add(w);
        }
        md.Idx.AddRange(idx);
        RebuildWeldedNormals(md, weld.Count);

        // adaptive splitting of the longest edges until the vertex budget or the minimum edge length is reached
        int budget = Math.Min((int)(v.Length * MaxVertsFactor), MaxVertsPerObject);
        for (int pass = 0; pass < 16; pass++)
        {
            float maxEdge = 0f;
            for (int t = 0; t < md.Idx.Count; t += 3)
                for (int k = 0; k < 3; k++) maxEdge = Math.Max(maxEdge, md.P[md.Idx[t + k]].DistanceTo(md.P[md.Idx[t + (k + 1) % 3]]));
            if (maxEdge <= MinEdge) break;
            float thresh = Math.Max(maxEdge * 0.6f, MinEdge);
            int pCount = md.P.Count, wCount = md.WN.Count; var idxBackup = md.Idx.ToArray();
            SplitPass(md, thresh);
            if (md.P.Count > budget)
            {
                md.P.RemoveRange(pCount, md.P.Count - pCount); md.UV.RemoveRange(pCount, md.UV.Count - pCount); md.C.RemoveRange(pCount, md.C.Count - pCount);
                md.W.RemoveRange(pCount, md.W.Count - pCount); md.WN.RemoveRange(wCount, md.WN.Count - wCount); md.Idx = idxBackup.ToList();
                break;
            }
        }

        // smooth normals (per vertex, faces within the angle limit of the vertex's own average) -> slope shading on the interpolated vertex light
        int n = md.P.Count;
        var fn = new Vector3[md.Idx.Count / 3];
        var wFaces = new List<int>[md.WN.Count];
        var vFaces = new List<int>[n];
        for (int t = 0; t < fn.Length; t++)
        {
            int a = md.Idx[t * 3], b = md.Idx[t * 3 + 1], c = md.Idx[t * 3 + 2];
            fn[t] = (md.P[b] - md.P[a]).Cross(md.P[c] - md.P[a]);
            foreach (int x in new[] { a, b, c }) { (wFaces[md.W[x]] ??= new()).Add(t); (vFaces[x] ??= new()).Add(t); }
        }
        var norms = new Vector3[n]; var uv2 = new Vector2[n]; var cols = new Color[n]; var pos = md.P.ToArray();
        for (int i = 0; i < n; i++)
        {
            Vector3 own = Vector3.Zero;
            if (vFaces[i] != null) foreach (int t in vFaces[i]) own += fn[t];
            own = own.LengthSquared() > 1e-12f ? own.Normalized() : Vector3.Up;
            Vector3 sum = Vector3.Zero;
            foreach (int t in wFaces[md.W[i]])
            {
                if (fn[t].LengthSquared() < 1e-12f) continue;
                if (fn[t].Normalized().Dot(own) > SmoothCos) sum += fn[t];
            }
            var nn = sum.LengthSquared() > 1e-12f ? sum.Normalized() : own;
            norms[i] = nn;
            uv2[i] = new Vector2(pos[i].X, pos[i].Z) / DetailMetres;
            float k = 1f - Relief * 0.5f + Relief * Math.Max(0f, nn.Dot(ReliefSun));
            var c = md.C[i];
            cols[i] = new Color(Math.Min(c.R * k, 1f), Math.Min(c.G * k, 1f), Math.Min(c.B * k, 1f));
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = pos;
        arrays[(int)Mesh.ArrayType.Normal] = norms;
        arrays[(int)Mesh.ArrayType.TexUV] = md.UV.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Color] = cols;
        arrays[(int)Mesh.ArrayType.Index] = md.Idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        if (mat != null) mesh.SurfaceSetMaterial(0, mat);
        return mesh;
    }

    static void RebuildWeldedNormals(MeshData md, int count)
    {
        md.WN.Clear();
        for (int i = 0; i < count; i++) md.WN.Add(Vector3.Zero);
        for (int t = 0; t < md.Idx.Count; t += 3)
        {
            int a = md.Idx[t], b = md.Idx[t + 1], c = md.Idx[t + 2];
            var f = (md.P[b] - md.P[a]).Cross(md.P[c] - md.P[a]);
            md.WN[md.W[a]] += f; md.WN[md.W[b]] += f; md.WN[md.W[c]] += f;
        }
        for (int i = 0; i < count; i++) md.WN[i] = md.WN[i].LengthSquared() > 1e-12f ? md.WN[i].Normalized() : Vector3.Up;
    }

    /// <summary>Splits every edge longer than <paramref name="thresh"/> at its midpoint (pulled onto the Phong surface). The decision and the position depend only on the edge's
    /// two points, so triangles on both sides of an edge (and across UV seams) agree and no cracks appear.</summary>
    static void SplitPass(MeshData md, float thresh)
    {
        var mids = new Dictionary<(int, int), int>();       // actual vertex pair -> new vertex
        var midW = new Dictionary<(int, int), int>();       // welded pair -> new welded point
        int Mid(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (mids.TryGetValue(key, out int m)) return m;
            int wa = md.W[a], wb = md.W[b];
            // canonical order by welded id so both seam sides compute bit-identical positions
            int ia = wa <= wb ? a : b, ib = wa <= wb ? b : a;
            Vector3 pa = md.P[ia], pb = md.P[ib], na = md.WN[md.W[ia]], nb = md.WN[md.W[ib]];
            Vector3 mid = (pa + pb) * 0.5f;
            Vector3 proj = ((mid - na * na.Dot(mid - pa)) + (mid - nb * nb.Dot(mid - pb))) * 0.5f;
            Vector3 p = mid + (proj - mid) * Phong;
            var wkey = wa <= wb ? (wa, wb) : (wb, wa);
            if (!midW.TryGetValue(wkey, out int w))
            {
                w = md.WN.Count; midW[wkey] = w;
                var wn = md.WN[wkey.Item1] + md.WN[wkey.Item2];
                md.WN.Add(wn.LengthSquared() > 1e-12f ? wn.Normalized() : Vector3.Up);
            }
            m = md.P.Count;
            md.P.Add(p); md.UV.Add((md.UV[a] + md.UV[b]) * 0.5f); md.W.Add(w);
            var ca = md.C[a]; var cb = md.C[b];
            md.C.Add(new Color((ca.R + cb.R) * 0.5f, (ca.G + cb.G) * 0.5f, (ca.B + cb.B) * 0.5f));
            mids[key] = m;
            return m;
        }
        var src = md.Idx; var dst = new List<int>(src.Count * 2);
        int[] tri = new int[3]; bool[] s = new bool[3];
        for (int t = 0; t < src.Count; t += 3)
        {
            for (int k = 0; k < 3; k++) tri[k] = src[t + k];
            int cnt = 0;
            for (int k = 0; k < 3; k++) { s[k] = md.P[tri[k]].DistanceTo(md.P[tri[(k + 1) % 3]]) > thresh; if (s[k]) cnt++; }
            if (cnt == 0) { dst.Add(tri[0]); dst.Add(tri[1]); dst.Add(tri[2]); continue; }
            void T(int x, int y, int z) { dst.Add(x); dst.Add(y); dst.Add(z); }
            if (cnt == 3)
            {
                int m01 = Mid(tri[0], tri[1]), m12 = Mid(tri[1], tri[2]), m20 = Mid(tri[2], tri[0]);
                T(tri[0], m01, m20); T(m01, tri[1], m12); T(m20, m12, tri[2]); T(m01, m12, m20);
            }
            else if (cnt == 1)
            {
                int k = s[0] ? 0 : s[1] ? 1 : 2;
                int a = tri[k], b = tri[(k + 1) % 3], c = tri[(k + 2) % 3], m = Mid(a, b);
                T(a, m, c); T(m, b, c);
            }
            else
            {
                int k = !s[0] ? 1 : !s[1] ? 2 : 0;          // edge k (a->b) and the next edge (b->c) are split, edge c->a is not
                int a = tri[k], b = tri[(k + 1) % 3], c = tri[(k + 2) % 3];
                int mab = Mid(a, b), mbc = Mid(b, c);
                T(mab, b, mbc); T(a, mab, mbc); T(a, mbc, c);
            }
        }
        md.Idx = dst;
    }
}
