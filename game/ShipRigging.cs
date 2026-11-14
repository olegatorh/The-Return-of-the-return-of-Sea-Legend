using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SeaDogs;

/// <summary>
/// Extra rigging made by the port: the original ship models carry only ~30 rope label pairs (ropeb/ropee), which leaves the masts and yards looking bare. From the geometry of the masts,
/// yards and sails this adds stays (mast to mast, to the bowsprit), backstays to the stern, yard braces down to the rail and the sheets of the lower sail corners. Every rope end hangs on
/// a model node (mast, yard, hull root), so turning yards and falling masts carry or drop them like the original ropes (<see cref="RiggingFx.Rope"/>).
/// Switch off with SEADOGS_NOEXTRAROPES.
/// </summary>
public partial class SeaView
{
    static readonly bool NoExtraRopes = System.Environment.GetEnvironmentVariable("SEADOGS_NOEXTRAROPES") != null;

    static List<RiggingFx.Rope> ExtraRigging(ShipModel sm)
    {
        var list = new List<RiggingFx.Rope>();
        if (NoExtraRopes || sm.Masts.Count == 0) return list;
        var root = sm.Root;
        float hw = Math.Max(1f, sm.Width * 0.42f);
        float deckY = sm.Masts.Where(m => !m.Bowsprit).Select(m => m.Node.Position.Y).DefaultIfEmpty(1f).Min() + 1.0f;

        // a point of a model node at a fraction of its own length (the node's local Y axis runs along a mast / the bowsprit)
        (Vector3 local, Aabb box) Along(Node3D n, float f)
        {
            var b = ModelFactory.Bounds(n);
            var c = b.GetCenter();
            return (new Vector3(c.X, b.Position.Y + b.Size.Y * f, c.Z), b);
        }
        RiggingFx.End OnNode(Node3D n, Vector3 local) => new(n, local);
        Vector3 W(Node3D n, Vector3 local) => RiggingFx.ToRoot(n, root) * local;
        bool log = System.Environment.GetEnvironmentVariable("SEADOGS_RIGLOG") != null;
        // every end sits on a real vertex of its node's mesh (a mast / yard / bowsprit / the hull): an end that finds none within SnapMax is dropped with its rope
        var hullNode = root.GetChildren().OfType<Node3D>().FirstOrDefault();
        var hullV = hullNode != null ? MeshVerts(hullNode) : new List<Vector3>();
        var vcache = new Dictionary<Node3D, List<Vector3>>();
        List<Vector3> VertsOf(Node3D n) { if (!vcache.TryGetValue(n, out var v)) vcache[n] = v = MeshVerts(n); return v; }
        const float SnapMax = 1.0f;
        // nearest vertex of the node's own mesh to a point in the node's space
        RiggingFx.End? Snap(Node3D n, Vector3 local)
        {
            float best = float.MaxValue; Vector3 bp = default;
            foreach (var v in VertsOf(n)) { float d = v.DistanceSquaredTo(local); if (d < best) { best = d; bp = v; } }
            return best <= SnapMax * SnapMax ? new RiggingFx.End(n, bp) : null;
        }
        // a point on the shaft of a spar at a fraction of its length: inside its mesh (the ropes are tied round the spar), only where the mesh really has vertices around that height
        RiggingFx.End? OnAxis(Node3D n, float f)
        {
            var vs = VertsOf(n);
            if (vs.Count == 0) return null;
            float y0 = vs.Min(v => v.Y), y1 = vs.Max(v => v.Y), y = y0 + (y1 - y0) * f;
            var near = vs.Where(v => Math.Abs(v.Y - y) < Math.Max(2f, (y1 - y0) * 0.15f)).ToList();
            if (near.Count == 0) return null;
            return new RiggingFx.End(n, new Vector3(near.Average(v => v.X), y, near.Average(v => v.Z)));
        }
        // the extreme vertex of the node along its local axis (the head of a mast / the tip of the bowsprit / the end of a yard)
        RiggingFx.End? Extreme(Node3D n, int axis, float sign)
        {
            var vs = VertsOf(n);
            if (vs.Count == 0) return null;
            var bp = vs[0];
            foreach (var v in vs) if (v[axis] * sign > bp[axis] * sign) bp = v;
            return new RiggingFx.End(n, bp);
        }
        // a point on the rail of the hull near (x, z): an upper-edge vertex of the hull on the outer side; null when there is none close enough
        RiggingFx.End? Rail(float x, float z, float reach = 2.5f)
        {
            float best = float.MaxValue; Vector3 bp = default;
            foreach (var v in hullV)
            {
                if (v.Y < deckY - 0.8f || v.Y > deckY + 2.5f) continue;
                if (Math.Abs(x) > 0.1f && Math.Sign(v.X) != Math.Sign(x)) continue;
                if (Math.Abs(v.X) < hw * 0.6f) continue;
                float d = new Vector2(v.X - x, v.Z - z).LengthSquared();
                if (d < best) { best = d; bp = v; }
            }
            return best <= reach * reach ? new RiggingFx.End(root, bp) : null;
        }
        // stays may be long, everything else (braces, sheets, backstays) is shorter than most of the hull
        void Add(RiggingFx.End? a, RiggingFx.End? b, bool stay = false)
        {
            if (a == null || b == null) { if (log) GD.Print("[rig]   extra DROPPED (no vertex for an end)"); return; }
            var pa = RiggingFx.ToRoot(a.Value.Node, root) * a.Value.Local; var pb = RiggingFx.ToRoot(b.Value.Node, root) * b.Value.Local;
            float len = pa.DistanceTo(pb);
            bool ok = len > 1.5f && len < (stay ? sm.Length * 1.1f : sm.Length * 0.7f) && pa.Y > 0.3f && pb.Y > 0.3f;
            if (log) GD.Print($"[rig]   extra {pa} -> {pb} len {len:0.0} {(ok ? "" : "DROPPED")}");
            if (ok) list.Add(new RiggingFx.Rope(a.Value, b.Value));
        }

        if (System.Environment.GetEnvironmentVariable("SEADOGS_RIGLOG") != null)
            foreach (var m in sm.Masts) GD.Print($"[rig]   mast {m.Name} top {m.Top:0.0} root {RiggingFx.ToRoot(m.Node, root).Origin} axisY {RiggingFx.ToRoot(m.Node, root).Basis.Y.Normalized()} bowsprit {m.Bowsprit} yards {m.Yards.Count}");
        // the real masts are the tall ones; the short stubs at the bow (and a model flagged as the bowsprit) are the bowsprit
        float tallest = sm.Masts.Max(m => m.Top);
        var masts = sm.Masts.Where(m => !m.Bowsprit && m.Top >= tallest * 0.4f).ToList();
        if (masts.Count == 0) return list;
        float centerZ = masts.Average(m => W(m.Node, Vector3.Zero).Z);
        var stubs = sm.Masts.Where(m => !masts.Contains(m)).OrderByDescending(m => Math.Abs(W(m.Node, Vector3.Zero).Z - centerZ)).ToList();
        // the bow lies on the side where most of the stubs stand (bowsprit root, ensign pole ...); without stubs the end ropes (bow / stern) are skipped
        float bow = stubs.Count > 0 ? Math.Sign(stubs.Sum(m => W(m.Node, Vector3.Zero).Z - centerZ)) : 0f, sternZ = 0f, bowZ = 0f;
        var spr = bow != 0f ? stubs.OrderByDescending(m => (W(m.Node, Vector3.Zero).Z - centerZ) * bow).First() : null;
        masts = masts.OrderByDescending(m => W(m.Node, Vector3.Zero).Z * (bow == 0f ? 1f : bow)).ToList();      // fore-most first
        if (bow != 0f) { sternZ = centerZ - bow * sm.Length * 0.42f; bowZ = centerZ + bow * sm.Length * 0.47f; }

        // stays: the head of the mast behind to the region of the mast ahead (two lines per pair), the fore mast to the tip of the bowsprit
        for (int i = 0; i + 1 < masts.Count; i++)
        {
            var fore = masts[i].Node; var aft = masts[i + 1].Node;
            Add(Extreme(aft, 1, 1f), OnAxis(fore, 0.55f), true);
            Add(OnAxis(aft, 0.68f), OnAxis(fore, 0.32f), true);
        }
        if (spr != null)
        {
            // fore stay: the head of the fore mast down to the bowsprit (its tip, and a lower line to its middle)
            var f0 = masts[0].Node;
            Add(Extreme(f0, 1, 1f), Extreme(spr.Node, 1, 1f), true);
            Add(OnAxis(f0, 0.70f), OnAxis(spr.Node, 0.55f), true);
        }
        // backstays: the head of the aft-most mast (and a lower point of it) to the rail at the stern, both sides
        if (bow != 0f)
        {
            var last = masts[^1].Node;
            foreach (float s in new[] { -1f, 1f })
            {
                Add(Extreme(last, 1, 1f), Rail(s * hw, sternZ, 8f));
                Add(OnAxis(last, 0.6f), Rail(s * hw, sternZ + bow * 2.5f, 8f));
            }
        }

        // yard braces: each end of a turning yard (the extreme vertex along its long axis) leads aft-down to the rail
        foreach (var m in masts)
            foreach (var y in m.Yards)
            {
                var b = ModelFactory.Bounds(y.Node);
                if (b.Size.LengthSquared() < 1f) continue;
                int ax = b.Size.X >= b.Size.Y && b.Size.X >= b.Size.Z ? 0 : b.Size.Y >= b.Size.Z ? 1 : 2;
                foreach (float s in new[] { -1f, 1f })
                {
                    var end = Extreme(y.Node, ax, s);
                    if (end == null) continue;
                    var p = W(y.Node, end.Value.Local);
                    float side = p.X >= 0f ? 1f : -1f;
                    Add(end, Rail(side * hw, p.Z - bow * 3f, 5f));
                }
            }

        // sheets: the lower corners of the sails to the rail (a triangle: its clew)
        if (System.Environment.GetEnvironmentVariable("SEADOGS_NOSHEETS") == null)
        foreach (var sl in sm.Sails)
        {
            if (sl.Host == null || sl.C == null) continue;
            var corners = sl.Tri ? new[] { 2 } : new[] { 2, 3 };
            foreach (int k in corners)
            {
                var p = W(sl.Host, sl.C[k]);
                float side = p.X >= 0f ? 1f : -1f;
                Add(OnNode(sl.Host, sl.C[k]), Rail(side * Math.Min(hw, Math.Max(Math.Abs(p.X), hw * 0.7f)), p.Z - bow * (sl.Tri ? 1.5f : 2.5f), 5f));
            }
        }
        if (System.Environment.GetEnvironmentVariable("SEADOGS_RIGLOG") != null) GD.Print($"[rig] extra ropes: {list.Count} (masts {masts.Count}, bowsprit {spr != null}, bow {bow})");
        return list;
    }

    /// <summary>SEADOGS_RIGLOG: for every rope end of the model labels the distance to the nearest vertex of its own node's mesh (ends in empty air = a part of the model that is not drawn).</summary>
    static void RopeDiag(ShipModel sm)
    {
        if (System.Environment.GetEnvironmentVariable("SEADOGS_RIGLOG") == null) return;
        foreach (var m in sm.Masts)
            GD.Print($"[ropediag] {sm.Root.Name}: {m.Name} base ({m.Base.X:0.0},{m.Base.Y:0.0},{m.Base.Z:0.0}) top {m.Top:0.0} bowsprit={m.Bowsprit} yards {m.Yards.Count} sails {m.Sails.Count} basisY=({m.Node.Basis.Y.X:0.00},{m.Node.Basis.Y.Y:0.00},{m.Node.Basis.Y.Z:0.00})");
        var cache = new Dictionary<Node3D, List<Vector3>>();
        float Near(RiggingFx.End e)
        {
            if (!cache.TryGetValue(e.Node, out var vs)) cache[e.Node] = vs = MeshVerts(e.Node);
            float best = float.MaxValue;
            foreach (var v in vs) best = Math.Min(best, v.DistanceTo(e.Local));
            return best;
        }
        int far = 0;
        foreach (var r in sm.Ropes)
        {
            float da = Near(r.A), db = Near(r.B);
            if (da > 0.5f || db > 0.5f)
            {
                far++;
                GD.Print($"[ropediag] {sm.Root.Name}: {r.A.Node.Name} {da:0.0} m -> {r.B.Node.Name} {db:0.0} m, len {RiggingFx.ToRoot(r.A.Node, sm.Root).Origin.DistanceTo(RiggingFx.ToRoot(r.B.Node, sm.Root).Origin):0.0}");
            }
        }
        GD.Print($"[ropediag] {sm.Root.Name}: {far} of {sm.Ropes.Count} ropes have an end farther than 0.5 m from the mesh of its node");
    }

    /// <summary>The vertices of the node's own meshes (its direct MeshInstance3D children, not the yards hung on a mast) in the node's space.</summary>
    static List<Vector3> MeshVerts(Node3D n)
    {
        var r = new List<Vector3>();
        foreach (var mi in n.GetChildren().OfType<MeshInstance3D>())
        {
            if (mi.Mesh == null) continue;
            for (int sIdx = 0; sIdx < mi.Mesh.GetSurfaceCount(); sIdx++)
            {
                var arr = mi.Mesh.SurfaceGetArrays(sIdx);
                if (arr.Count == 0 || arr[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.Nil) continue;
                foreach (var v in arr[(int)Mesh.ArrayType.Vertex].AsVector3Array()) r.Add(mi.Transform * v);
            }
        }
        return r;
    }
}
