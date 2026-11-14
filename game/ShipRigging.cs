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
        RiggingFx.End Deck(float x, float z) => new(root, new Vector3(x, deckY, z));
        void Add(RiggingFx.End a, RiggingFx.End b)
        {
            var pa = RiggingFx.ToRoot(a.Node, root) * a.Local; var pb = RiggingFx.ToRoot(b.Node, root) * b.Local;
            float len = pa.DistanceTo(pb);
            if (System.Environment.GetEnvironmentVariable("SEADOGS_RIGLOG") != null) GD.Print($"[rig]   extra {pa} -> {pb} len {len:0.0} {(len > 1.5f && len < 90f ? "" : "DROPPED")}");
            if (len > 1.5f && len < 90f) list.Add(new RiggingFx.Rope(a, b));
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

        // stays: the head of the mast behind to the foot region of the mast ahead (two lines per pair), the fore mast to the bowsprit
        for (int i = 0; i + 1 < masts.Count; i++)
        {
            var fore = masts[i].Node; var aft = masts[i + 1].Node;
            Add(OnNode(aft, Along(aft, 0.93f).local), OnNode(fore, Along(fore, 0.55f).local));
            Add(OnNode(aft, Along(aft, 0.68f).local), OnNode(fore, Along(fore, 0.32f).local));
        }
        if (spr != null)
        {
            // fore stay: the head of the fore mast down to the bow, a second line lower
            var f0 = masts[0].Node;
            Add(OnNode(f0, Along(f0, 0.96f).local), new RiggingFx.End(root, new Vector3(0f, deckY + 1.2f, bowZ)));
            Add(OnNode(f0, Along(f0, 0.70f).local), new RiggingFx.End(root, new Vector3(0f, deckY + 0.8f, bowZ - bow * 3f)));
        }
        // backstays: the head of the aft-most mast (and the one before it) to the rail at the stern, both sides
        if (bow != 0f)
        {
            var last = masts[^1].Node;
            foreach (float s in new[] { -1f, 1f })
            {
                Add(OnNode(last, Along(last, 0.9f).local), Deck(s * hw, sternZ));
                Add(OnNode(last, Along(last, 0.6f).local), Deck(s * hw, sternZ + bow * 2.5f));
            }
        }

        // yard braces: each end of a turning yard leads aft-down to the rail
        foreach (var m in masts)
            foreach (var y in m.Yards)
            {
                var b = ModelFactory.Bounds(y.Node);
                if (b.Size.LengthSquared() < 1f) continue;
                int ax = b.Size.X >= b.Size.Y && b.Size.X >= b.Size.Z ? 0 : b.Size.Y >= b.Size.Z ? 1 : 2;
                var c = b.GetCenter();
                float half = b.Size[ax] * 0.46f;
                foreach (float s in new[] { -1f, 1f })
                {
                    var local = c; local[ax] += s * half;
                    var p = W(y.Node, local);
                    float side = p.X >= 0f ? 1f : -1f;
                    Add(OnNode(y.Node, local), Deck(side * hw, p.Z - bow * 3f));
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
                Add(OnNode(sl.Host, sl.C[k]), Deck(side * Math.Min(hw, Math.Max(Math.Abs(p.X), hw * 0.7f)), p.Z - bow * (sl.Tri ? 1.5f : 2.5f)));
            }
        }
        if (System.Environment.GetEnvironmentVariable("SEADOGS_RIGLOG") != null) GD.Print($"[rig] extra ropes: {list.Count} (masts {masts.Count}, bowsprit {spr != null}, bow {bow})");
        return list;
    }
}
