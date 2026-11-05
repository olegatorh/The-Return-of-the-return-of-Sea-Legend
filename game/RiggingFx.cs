using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Formats;

namespace SeaDogs;

/// <summary>
/// The engine's "Rope" and "Flag" classes for ship models (RESOURCE\INI\rigging.ini holds their parameters).
/// Ropes: the model files carry labels ropeb&lt;N&gt; (begin) and ropee&lt;N&gt; (end) in the hull, masts, yards and blocks; the two labels of one number are joined by a rope
/// that hangs a little (segment length fSEG_LENGTH, width fWIDTH, sag fWAVE). Flags: four labels f1..f4 of the group "flag*" are the corners of a flag that flutters in the wind.
/// </summary>
static class RiggingFx
{
    /// <summary>A model file's labels, with the node the file's geometry hangs on (label positions are in that node's space).</summary>
    public readonly record struct Part(Node3D Node, GmFile Gm);

    static string RiggingIni = "";

    /// <summary>A rope end: the node it hangs on and the point in that node's space.</summary>
    public readonly record struct End(Node3D Node, Vector3 Local);
    /// <summary>A rope between two ends (ropeb/ropee), or a shroud ladder (vant l, r on the hull, u on the mast) when <see cref="U"/> is set.</summary>
    public sealed record Rope(End A, End B, End? U = null)
    {
        public IEnumerable<Node3D> Nodes => U is { } u ? new[] { A.Node, B.Node, u.Node } : new[] { A.Node, B.Node };
    }

    /// <summary>All ropes and shrouds of a ship, with their ends attached to the model parts (so that turning yards and falling masts can carry them).</summary>
    public static List<Rope> CollectRopes(IEnumerable<Part> parts)
    {
        var begins = new Dictionary<string, End>(); var ends = new Dictionary<string, End>(); var vants = new Dictionary<string, End?[]>();
        foreach (var part in parts)
        {
            foreach (var l in part.Gm.Labels)
            {
                string n = l.Name.ToLowerInvariant();
                var at = new End(part.Node, new Vector3(l.M[12], l.M[13], -l.M[14]));
                if (n.StartsWith("vant") && n.Length > 5 && "lru".IndexOf(n[^1]) >= 0 && int.TryParse(n.AsSpan(4, n.Length - 5), out _))      // shrouds: vant<N>l / r on the hull, u on the mast
                {
                    string vk = n.Substring(4, n.Length - 5);
                    if (!vants.TryGetValue(vk, out var tri)) vants[vk] = tri = new End?[3];
                    tri["lru".IndexOf(n[^1])] = at;
                    continue;
                }
                bool isB = n.StartsWith("ropeb"), isE = n.StartsWith("ropee");
                if (!isB && !isE) continue;
                (isB ? begins : ends)[n.Substring(5)] = at;
            }
        }
        var list = new List<Rope>();
        foreach (var (num, a) in begins) if (ends.TryGetValue(num, out var b)) list.Add(new Rope(a, b));
        foreach (var tri in vants.Values) if (tri[0] is { } l && tri[1] is { } r && tri[2] is { } u) list.Add(new Rope(l, r, u));
        return list;
    }

    /// <summary>One mesh of the given ropes in the ship root's space (the ends are read from the current transforms of their nodes).</summary>
    public static MeshInstance3D? BuildRopes(Node3D shipRoot, IEnumerable<Rope> list, string name = "Ropes")
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int ropes = 0;
        foreach (var rope in list)
        {
            var a = ToRoot(rope.A.Node, shipRoot) * rope.A.Local; var b = ToRoot(rope.B.Node, shipRoot) * rope.B.Local;
            if (rope.U is not { } ue) { AddRope(st, a, b); ropes++; continue; }
            // a shroud ladder: two ropes from the hull points up to the mast point, joined by rungs
            Vector3 l = a, r = b, u = ToRoot(ue.Node, shipRoot) * ue.Local;
            AddRope(st, l, u); AddRope(st, r, u);
            int rungs = Math.Clamp((int)((u.Y - Math.Min(l.Y, r.Y)) / 0.9f), 2, 14);
            for (int i = 1; i < rungs; i++)
            {
                float t = i / (float)rungs;
                AddRope(st, l.Lerp(u, t), r.Lerp(u, t));
            }
            ropes++;
        }
        if (ropes == 0) return null;
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(0.30f, 0.22f, 0.13f),
        };
        var mi = new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = mat, Name = name, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        mi.SetMeta("ropes", ropes);
        return mi;
    }

    /// <summary>Transform of a node relative to the ship root (the nodes are not in the tree yet while the model is built).</summary>
    public static Transform3D ToRoot(Node3D n, Node3D root)
    {
        var t = Transform3D.Identity;
        for (Node? c = n; c != null && c != root; c = c.GetParent()) if (c is Node3D n3) t = n3.Transform * t;
        return t;
    }

    /// <summary>A rope as two crossed ribbons along a gently sagging curve.</summary>
    static void AddRope(SurfaceTool st, Vector3 a, Vector3 b)
    {
        const float width = 0.028f, segLen = 2.0f;       // half width; rigging.ini fWIDTH = 0.025
        float len = a.DistanceTo(b);
        if (len < 0.3f) return;
        int seg = Math.Max(2, (int)MathF.Ceiling(len / segLen));
        float sag = Math.Min(0.04f * len, 0.5f);
        var dir = (b - a) / len;
        var side = dir.Cross(Vector3.Up);
        if (side.LengthSquared() < 1e-4f) side = Vector3.Right;
        side = side.Normalized();
        var up = side.Cross(dir).Normalized();
        Vector3 P(int i)
        {
            float t = i / (float)seg;
            return a.Lerp(b, t) + Vector3.Down * (MathF.Sin(MathF.PI * t) * sag);
        }
        for (int i = 0; i < seg; i++)
        {
            Vector3 p0 = P(i), p1 = P(i + 1);
            foreach (var w in new[] { side * width, up * width })
            {
                st.AddVertex(p0 - w); st.AddVertex(p0 + w); st.AddVertex(p1 + w);
                st.AddVertex(p0 - w); st.AddVertex(p1 + w); st.AddVertex(p1 - w);
            }
        }
    }

    // ------------------------------------------------------------------ flags
    const string FlagShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled;
uniform sampler2D tex : source_color, filter_linear_mipmap;
uniform vec4 tile;          // x0, y0, w, h of the nation's flag in the atlas
uniform float t;
uniform float amp;
uniform vec3 nrm;
void vertex() {
    float u = UV.x;                                    // 0 at the pole, 1 at the free end
    VERTEX += nrm * (sin(u * 7.0 - t * 5.0) * amp * u + sin(u * 13.0 - t * 8.0 + 1.7) * amp * 0.25 * u);
}
void fragment() {
    vec4 c = texture(tex, tile.xy + UV * tile.zw);
    if (c.a < 0.5) discard;
    ALBEDO = c.rgb;
}";
    static Shader? flagShader;
    static readonly List<ShaderMaterial> flagMats = new();

    public static void UpdateFlags(float time) { foreach (var m in flagMats) m.SetShaderParameter("t", time); }

    /// <summary>Flags at the mast tops: the labels f1..f4 of every "flag*" group are the corners of one flag. Each flag hangs on its model part (a falling mast takes it along).</summary>
    public static IEnumerable<(Node3D host, Node3D flag)> BuildFlags(IEnumerable<Part> parts, int nation)
    {
        var tex = GameAssets.Texture("ships\\flagall.tga") ?? GameAssets.Texture("flagall.tga");
        flagShader ??= new Shader { Code = FlagShader };
        // rigging.ini: tile index per nation (England 1, France 2, Portugal 3, Spain 4, pirate 5, Holland 0)
        int tileIdx = nation switch { 0 => 1, 1 => 2, 2 => 4, 3 => 5, 4 => 0, 5 => 3, _ => 5 };
        const int tiles = 8;
        foreach (var part in parts)
        {
            foreach (var grp in part.Gm.Labels.Where(l => l.Group.StartsWith("flag", StringComparison.OrdinalIgnoreCase)).GroupBy(l => l.Group))
            {
                var c = new Vector3[4]; int have = 0;
                foreach (var l in grp)
                {
                    if (l.Name.Length == 2 && l.Name[0] == 'f' && l.Name[1] >= '1' && l.Name[1] <= '4') { c[l.Name[1] - '1'] = new Vector3(l.M[12], l.M[13], -l.M[14]); have++; }
                }
                if (have != 4) continue;
                yield return (part.Node, MakeFlag(c, tex, tileIdx, tiles));
            }
        }
    }

    static Node3D MakeFlag(Vector3[] c, Texture2D? tex, int tileIdx, int tiles)
    {
        // corners in label order f1..f4 = pole top, free end top, free end bottom, pole bottom (like the sail corners); find the pole edge as the shorter pair if it is not so
        Vector3 top0 = c[0], top1 = c[1], bot1 = c[2], bot0 = c[3];
        const int N = 10;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector3 P(int i, int j) { float u = i / (float)N, v = j / 2f; return top0.Lerp(top1, u).Lerp(bot0.Lerp(bot1, u), v); }
        for (int i = 0; i < N; i++)
            for (int j = 0; j < 2; j++)
            {
                (Vector3, Vector2) A = (P(i, j), new(i / (float)N, j / 2f)), B = (P(i + 1, j), new((i + 1) / (float)N, j / 2f)),
                    C = (P(i + 1, j + 1), new((i + 1) / (float)N, (j + 1) / 2f)), D = (P(i, j + 1), new(i / (float)N, (j + 1) / 2f));
                foreach (var v in new[] { A, B, C, A, C, D }) { st.SetUV(v.Item2); st.AddVertex(v.Item1); }
            }
        var mat = new ShaderMaterial { Shader = flagShader };
        mat.SetShaderParameter("tex", tex);
        // the atlas stacks the flags in one direction: use the longer side of the texture
        float w = tex?.GetWidth() ?? 1, h = tex?.GetHeight() ?? 1;
        if (System.Environment.GetEnvironmentVariable("SEADOGS_SHIPLOG") != null) { GD.Print($"[flag] atlas {w}x{h}, tile {tileIdx}"); tex?.GetImage().SavePng("review/flagall.png"); }
        mat.SetShaderParameter("tile", w >= h ? new Vector4(tileIdx / (float)tiles, 0, 1f / tiles, 1) : new Vector4(0, tileIdx / (float)tiles, 1, 1f / tiles));
        mat.SetShaderParameter("amp", 0.18f * (top0.DistanceTo(top1)));
        mat.SetShaderParameter("t", 0f);
        mat.SetShaderParameter("nrm", (top1 - top0).Cross(bot0 - top0).Normalized());
        flagMats.Add(mat);
        return new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = mat, Name = "Flag", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }
}
