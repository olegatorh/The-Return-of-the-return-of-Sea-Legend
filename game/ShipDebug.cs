using System.Collections.Generic;
using Godot;

namespace SeaDogs;

/// <summary>Debug: finds loose pieces of a ship mesh (connected components of triangles that touch no other part).</summary>
static class ShipDebug
{
    public static void Dump(string name, Node root)
    {
        foreach (var n in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)n;
            for (int su = 0; su < mi.Mesh.GetSurfaceCount(); su++)
            {
                var arr = mi.Mesh.SurfaceGetArrays(su);
                var vs = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
                var idx = arr[(int)Mesh.ArrayType.Index].AsInt32Array();
                if (idx.Length == 0) { idx = new int[vs.Length]; for (int i = 0; i < idx.Length; i++) idx[i] = i; }
                // union-find over vertices merged by position
                var key = new Dictionary<(int, int, int), int>(); var parent = new List<int>(); var vid = new int[vs.Length];
                for (int i = 0; i < vs.Length; i++)
                {
                    var k = ((int)(vs[i].X * 50), (int)(vs[i].Y * 50), (int)(vs[i].Z * 50));
                    if (!key.TryGetValue(k, out var id)) { id = parent.Count; parent.Add(id); key[k] = id; }
                    vid[i] = id;
                }
                int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
                for (int t = 0; t + 2 < idx.Length; t += 3) { int a = Find(vid[idx[t]]), b = Find(vid[idx[t + 1]]), c = Find(vid[idx[t + 2]]); parent[b] = a; parent[Find(c)] = a; }
                var comp = new Dictionary<int, (Aabb box, int n)>();
                for (int i = 0; i < vs.Length; i++)
                {
                    int r = Find(vid[i]);
                    comp[r] = comp.TryGetValue(r, out var cb) ? (cb.box.Expand(vs[i]), cb.n + 1) : (new Aabb(vs[i], Vector3.Zero), 1);
                }
                GD.Print($"[shipobj] {name} {mi.Name} surface {su}: {vs.Length} verts, {comp.Count} components");
                foreach (var (_, (box, cnt)) in comp)
                    if (cnt < 400) GD.Print(string.Format(System.Globalization.CultureInfo.InvariantCulture, "[shipobj]   piece {0} verts at ({1:0.0};{2:0.0};{3:0.0}) size ({4:0.0};{5:0.0};{6:0.0})", cnt, box.GetCenter().X, box.GetCenter().Y, box.GetCenter().Z, box.Size.X, box.Size.Y, box.Size.Z));
            }
        }
    }

    /// <summary>
    /// Removes loose little pieces (rope blocks / deadeyes of the original models, which the engine's Rope/Vant classes hung on rope ends that we do not draw)
    /// that float far from the mast's axis: they showed up as dark discs in the air next to the ship.
    /// </summary>
    public static void CullLoose(Node root, float maxDist, int maxVerts, bool outsideMain = false)
    {
        foreach (var n in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)n;
            if (mi.Mesh is not ArrayMesh am) continue;
            var res = new ArrayMesh(); bool changed = false;
            for (int su = 0; su < am.GetSurfaceCount(); su++)
            {
                var arr = am.SurfaceGetArrays(su);
                var vs = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
                var idx = arr[(int)Mesh.ArrayType.Index].AsInt32Array();
                bool indexed = idx.Length > 0;
                if (!indexed) { idx = new int[vs.Length]; for (int i = 0; i < idx.Length; i++) idx[i] = i; }
                var key = new Dictionary<(int, int, int), int>(); var parent = new List<int>(); var vid = new int[vs.Length];
                for (int i = 0; i < vs.Length; i++)
                {
                    var k = ((int)System.Math.Round(vs[i].X * 50), (int)System.Math.Round(vs[i].Y * 50), (int)System.Math.Round(vs[i].Z * 50));
                    if (!key.TryGetValue(k, out var id)) { id = parent.Count; parent.Add(id); key[k] = id; }
                    vid[i] = id;
                }
                int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
                for (int t = 0; t + 2 < idx.Length; t += 3) { int a = Find(vid[idx[t]]), b = Find(vid[idx[t + 1]]), c = Find(vid[idx[t + 2]]); parent[b] = a; parent[Find(c)] = a; }
                var cnt = new Dictionary<int, int>(); var sum = new Dictionary<int, Vector3>();
                for (int i = 0; i < vs.Length; i++) { int r = Find(vid[i]); cnt[r] = cnt.GetValueOrDefault(r) + 1; sum[r] = sum.GetValueOrDefault(r) + vs[i]; }
                var boxes = new Dictionary<int, Aabb>();
                for (int i = 0; i < vs.Length; i++) { int r = Find(vid[i]); boxes[r] = boxes.TryGetValue(r, out var bx) ? bx.Expand(vs[i]) : new Aabb(vs[i], Vector3.Zero); }
                int mainR = -1; foreach (var kv in boxes) if (mainR < 0 || kv.Value.Size.Length() > boxes[mainR].Size.Length()) mainR = kv.Key;      // the yard itself = the largest piece by extent
                Aabb mainBox = mainR >= 0 ? boxes[mainR] : default;
                bool Loose(int vtx)
                {
                    int r = Find(vid[vtx]); var c = sum[r] / cnt[r];
                    if (cnt[r] > maxVerts) return false;
                    if (outsideMain) return r != mainR && cnt[r] >= 30 && boxes[r].Size.Length() < 0.9f;       // the rope blocks (34 verts, ~0.4 m) that hang beside a yard on ropes we do not draw
                    return new Vector2(c.X, c.Z).Length() > maxDist;
                }
                var keep = new List<int>();
                for (int t = 0; t + 2 < idx.Length; t += 3) { if (Loose(idx[t])) { changed = true; continue; } keep.Add(idx[t]); keep.Add(idx[t + 1]); keep.Add(idx[t + 2]); }
                if (keep.Count == idx.Length) { res.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, am.SurfaceGetArrays(su)); res.SurfaceSetMaterial(res.GetSurfaceCount() - 1, am.SurfaceGetMaterial(su)); continue; }
                if (keep.Count == 0) continue;
                arr[(int)Mesh.ArrayType.Index] = keep.ToArray();
                res.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
                res.SurfaceSetMaterial(res.GetSurfaceCount() - 1, am.SurfaceGetMaterial(su));
            }
            if (changed) { mi.Mesh = res; GD.Print("[shipobj] culled loose pieces of " + mi.Name); }
        }
    }
}
