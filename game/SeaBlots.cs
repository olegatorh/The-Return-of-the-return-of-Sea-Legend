using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine class "blots" (libs/blot/src/blots.cpp; AIFort.c creates one per fort): scorch marks of cannon balls on the fort's walls. MSG_BLOTS_SETMODEL 30600 "lia"
/// (fort model entity, the fort's character): the marks are kept in the character's attributes ship.blots.bNNN (rnd, x, y, z, vx, vy, vz, time; up to 256) and rebuilt from
/// them. MSG_BLOTS_HIT 30601 "lffffff" (point, direction) comes from AIFort::Cannon_Trace when a ball flies through the fort model: this port traces every ball against the
/// fort model itself (SeaCombat.cs) and raises Ball_FortHit with the real hit point. A mark is a 1.2 m square (BLOTS_RADIUS 0.6) lying on the wall with one of the four
/// pictures of blot.tga (rnd bits 1 / 2 pick the quarter, 4 / 8 mirror it); nearer than 3 radii to another mark none is added. A mark lives 120 s of the time the camera
/// is within 150 m, fading from the half of it and with the distance (BLOTS_DIST).
/// </summary>
public partial class SeaView
{
    const float BlotsRadius = 0.6f, BlotsTime = 120f, BlotsDist = 150f;
    const int BlotsMax = 256;

    sealed class BlotRt { public int Index, Rnd; public Vector3 LPos, LDir; public float Time; public MeshInstance3D Node = null!; public ShaderMaterial Mat = null!; }

    sealed class BlotsRt
    {
        public Entity Model = null!; public Attr Chr = null!;
        public Node3D? Node;
        public Vector3[]? Faces; public Aabb Box;
        public readonly BlotRt?[] Items = new BlotRt?[BlotsMax];
        public bool Loaded;
        public float SaveT;
    }

    readonly List<BlotsRt> blotSets = new();
    static Shader? blotShader;
    const string BlotShaderCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
uniform sampler2D tex : source_color, filter_linear_mipmap;
uniform float fade = 1.0;
void fragment() {
    vec4 c = texture(tex, UV);
    ALBEDO = vec3(0.02, 0.02, 0.02);
    ALPHA = clamp(c.a * fade, 0.0, 1.0);
}";

    void RegisterBlots(Interp i)
    {
        i.EntityHandlers["blots"] = (e, fmt, d) =>
        {
            int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
            if (msg == 30600 && d.Length > 2 && d[1].Attr?.Entity is { } model && d[2].Attr is { } chr)
            {
                blotSets.RemoveAll(b => ReferenceEquals(b.Chr, chr));
                var set = new BlotsRt { Model = model, Chr = chr };
                chr.Find("ship.blots", true)!.Value = BlotsMax.ToString();
                blotSets.Add(set);
                return V.Int(1);
            }
            return V.Int(0);
        };
    }

    void ClearBlots()
    {
        foreach (var b in blotSets) foreach (var t in b.Items) if (t != null && GodotObject.IsInstanceValid(t.Node)) t.Node.QueueFree();
        blotSets.Clear();
    }

    /// <summary>The fort model of a blots set, with its triangles in world space (built once).</summary>
    bool BlotsReady(BlotsRt b)
    {
        if (b.Node == null || !GodotObject.IsInstanceValid(b.Node))
        {
            if (!LandFlags.TryGet(b.Model, out var node) || !node.IsInsideTree()) return false;
            b.Node = node; b.Faces = null;
        }
        if (b.Faces == null)
        {
            var list = new List<Vector3>();
            foreach (var mi in b.Node.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            {
                if (mi.Mesh == null || !mi.Visible) continue;
                var gx = mi.GlobalTransform;
                foreach (var f in mi.Mesh.GetFaces()) list.Add(gx * f);
            }
            b.Faces = list.ToArray();
            if (list.Count > 0) { var bx = new Aabb(list[0], Vector3.Zero); foreach (var f in list) bx = bx.Expand(f); b.Box = bx.Grow(3f); }
            GD.Print($"[sea] blots: fort model {b.Node.Name} with {list.Count / 3} triangles");
        }
        if (!b.Loaded)
        {
            b.Loaded = true;
            var root = b.Chr.Find("ship.blots", false);
            if (root != null)
                for (int i = 0; i < BlotsMax; i++)
                {
                    var a = root.Get("b" + i.ToString("000"));
                    if (a == null || a.Get("rnd") == null || a.Get("x") == null || a.Get("vz") == null || a.Get("time") == null) continue;
                    var lp = new Vector3(F(a, "x"), F(a, "y"), F(a, "z")); var ld = new Vector3(F(a, "vx"), F(a, "vy"), F(a, "vz"));
                    MakeBlot(b, i, (int)F(a, "rnd"), lp, ld, F(a, "time"), false);
                }
        }
        return true;
    }

    /// <summary>Traces a ball segment (Godot space) against every fort model: the hit point and the direction of the flight, or false.</summary>
    bool BlotsTrace(Vector3 a, Vector3 b, out Vector3 hit, out Vector3 dir, out BlotsRt? set, out Vector3 normal)
    {
        hit = default; dir = (b - a).Normalized(); set = null; normal = Vector3.Up;
        float best = 2f;
        foreach (var bs in blotSets)
        {
            if (!BlotsReady(bs) || bs.Faces == null || bs.Faces.Length == 0) continue;
            if (!bs.Box.HasPoint(a) && !bs.Box.HasPoint(b)) continue;
            var d = b - a; float len = d.Length();
            if (len < 1e-4f) continue;
            var f = bs.Faces;
            for (int i = 0; i + 2 < f.Length; i += 3)
            {
                // Moller-Trumbore on the segment, both sides
                var e1 = f[i + 1] - f[i]; var e2 = f[i + 2] - f[i];
                var p = d.Cross(e2); float det = e1.Dot(p);
                if (MathF.Abs(det) < 1e-9f) continue;
                float inv = 1f / det; var tv = a - f[i];
                float u = tv.Dot(p) * inv; if (u < 0f || u > 1f) continue;
                var q = tv.Cross(e1); float v = d.Dot(q) * inv; if (v < 0f || u + v > 1f) continue;
                float t = e2.Dot(q) * inv;
                if (t < 0f || t > 1f || t >= best) continue;
                best = t; set = bs; hit = a + d * t;
                var n = e1.Cross(e2).Normalized();
                normal = n.Dot(d) > 0f ? -n : n;                        // facing the shooter
            }
        }
        return set != null;
    }

    /// <summary>Blots::Hit + AddBlot: a new mark at the world point <paramref name="p"/> of the fort wall with the given surface normal.</summary>
    void BlotAdd(BlotsRt bs, Vector3 p, Vector3 dir, Vector3 normal)
    {
        if (bs.Node == null) return;
        var inv = bs.Node.GlobalTransform.AffineInverse();
        var lpos = inv * p; var ldir = inv.Basis * dir;
        int free = -1;
        for (int i = 0; i < BlotsMax; i++)
        {
            var t = bs.Items[i];
            if (t != null) { if ((lpos - t.LPos).LengthSquared() < 9f * BlotsRadius * BlotsRadius) return; }
            else if (free < 0) free = i;
        }
        if (free < 0) return;
        MakeBlot(bs, free, fxRnd.Next(), lpos, ldir, 0f, true, normal);
        SaveBlot(bs, free);
    }

    void MakeBlot(BlotsRt bs, int index, int rnd, Vector3 lpos, Vector3 ldir, float time, bool fresh, Vector3? worldNormal = null)
    {
        if (bs.Node == null) return;
        // the wall normal: given at a fresh hit, else the stored direction of the flight reversed (the original projects along -dir)
        var n = worldNormal ?? -(bs.Node.GlobalTransform.Basis * ldir).Normalized();
        if (n.LengthSquared() < 1e-6f) n = Vector3.Up;
        n = n.Normalized();
        var wp = bs.Node.GlobalTransform * lpos + n * 0.05f;
        var up = MathF.Abs(n.Y) > 0.95f ? Vector3.Forward : Vector3.Up;
        var ux = up.Cross(n).Normalized(); var uy = n.Cross(ux).Normalized();
        float r = BlotsRadius;
        float baseU = (rnd & 1) != 0 ? 0.5f : 0f, baseV = (rnd & 2) != 0 ? 0.5f : 0f;
        Vector2 UV(float u, float v) { if ((rnd & 4) != 0) u = 1f - u; if ((rnd & 8) != 0) v = 1f - v; return new Vector2(baseU + u * 0.5f, baseV + v * 0.5f); }
        var verts = new[] { wp - ux * r - uy * r, wp + ux * r - uy * r, wp + ux * r + uy * r, wp - ux * r + uy * r };
        var uvs = new[] { UV(0, 1), UV(1, 1), UV(1, 0), UV(0, 0) };
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts; arrays[(int)Mesh.ArrayType.TexUV] = uvs; arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        blotShader ??= new Shader { Code = BlotShaderCode };
        var mat = new ShaderMaterial { Shader = blotShader };
        mat.SetShaderParameter("tex", GameAssets.Texture("blot.tga"));
        var node = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Name = "Blot" + index, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(node);                                                          // world space: the fort stands still
        bs.Items[index] = new BlotRt { Index = index, Rnd = rnd, LPos = lpos, LDir = ldir, Time = time, Node = node, Mat = mat };
        if (fresh) GD.Print($"[sea] blot {index} on fort {bs.Chr.Get("id")?.Value} at ({wp.X:0.0},{wp.Y:0.0},{-wp.Z:0.0})");
    }

    void SaveBlot(BlotsRt bs, int i)
    {
        var root = bs.Chr.Find("ship.blots", true)!;
        string name = "b" + i.ToString("000");
        var t = bs.Items[i];
        if (t == null) { root.Remove(name); return; }
        var a = root.GetOrCreate(name);
        void Set(string k, float v) => a.GetOrCreate(k).Value = Conv.FloatToConcat(v);
        a.GetOrCreate("rnd").Value = t.Rnd.ToString();
        Set("x", t.LPos.X); Set("y", t.LPos.Y); Set("z", t.LPos.Z); Set("vx", t.LDir.X); Set("vy", t.LDir.Y); Set("vz", t.LDir.Z); Set("time", t.Time);
    }

    void TickBlots(float dt)
    {
        if (blotSets.Count == 0) return;
        var cp = Cam.GlobalPosition;
        foreach (var bs in blotSets)
        {
            if (!BlotsReady(bs)) continue;
            bs.SaveT -= dt;
            bool save = bs.SaveT <= 0f;
            if (save) bs.SaveT = 1f;
            // the script (DeleteBlots, repair) removes marks by deleting their attributes
            var root = bs.Chr.Find("ship.blots", false);
            for (int i = 0; i < BlotsMax; i++)
            {
                var t = bs.Items[i];
                if (t == null) continue;
                if (root == null || root.Get("b" + i.ToString("000")) == null) { t.Node.QueueFree(); bs.Items[i] = null; continue; }
                float dist = (t.Node.Mesh.GetAabb().GetCenter() - cp).Length();
                if (dist >= BlotsDist) { t.Node.Visible = false; continue; }
                t.Node.Visible = true;
                t.Time += dt;
                if (t.Time >= BlotsTime) { t.Node.QueueFree(); bs.Items[i] = null; root.Remove("b" + i.ToString("000")); continue; }
                float k = Math.Clamp((t.Time / BlotsTime - 0.5f) / 0.5f, 0f, 1f);
                float dk = Math.Clamp((dist / BlotsDist - 0.5f) / 0.5f, 0f, 1f);
                t.Mat.SetShaderParameter("fade", (1f - k) * (1f - dk));
                if (save) SaveBlot(bs, i);
            }
        }
    }
}
