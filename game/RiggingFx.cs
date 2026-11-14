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

    // ------------------------------------------------------------------ rigging.ini
    static IniFile.Section? ropesIni, vantsIni, flagsIni;
    static float Par(ref IniFile.Section? sec, string section, string key, float def)
    {
        if (sec == null)
        {
            try { sec = IniFile.Load(GameAssets.Resource("INI\\rigging.ini")).Get(section); } catch (Exception) { }
            if (sec == null) return def;
        }
        return float.TryParse(sec.Get(key)?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
    }
    static float RopePar(string key, float def) => Par(ref ropesIni, "ROPES", key, def);
    static float VantPar(string key, float def) => Par(ref vantsIni, "VANTS", key, def);
    static float FlagPar(string key, float def) => Par(ref flagsIni, "FLAGS", key, def);

    // ------------------------------------------------------------------ ropes and vants
    // rope.cpp: ROPE::SetVertexes/SetTextureGrid/AddLabel. A rope is a 5-edge tube of rigging.ini [ROPES] fWIDTH, cut into len / fSEG_LENGTH + 1 segments with end cones of
    // fEND_LENGTH; its middle sags by fWAVE x (fMinDeepMul + fVarDeepMul sin(angDeep)) (absolute metres, -20 % of that for <= 2 segments), angDeep turns by
    // fDeepChangeSpeed and angRot (sideways swing fRotateAng) by fRotateSpeed per 1/20 s: done in the vertex shader (UV2 = parabola factor, amplitude).
    const string RopeShader = @"
shader_type spatial;
render_mode cull_disabled;
uniform sampler2D tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float t;
uniform vec4 spd = vec4(3.0, 1.6, 1.0, 0.7);     // deep change/s, rotate/s, min deep mul, var deep mul
uniform float rot_ang = 0.5;
uniform float rope_r = 0.0125;
uniform float min_px = 2.0;
varying float swollen;
void vertex() {
    float amp = UV2.x * UV2.y;
    float d = amp * (spd.z + spd.w * sin(spd.x * t));
    VERTEX.y -= d;
    VERTEX.x -= d * sin(rot_ang * sin(spd.y * t));
    // a rope is 2.5 cm thick: from some metres away it is thinner than a pixel and vanishes. Keep at least ~1.5 px of width by swelling the tube along its ring normals.
    vec3 vp = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float world_px = -vp.z * 2.0 / (VIEWPORT_SIZE.y * PROJECTION_MATRIX[1][1]);
    float extra = max(0.0, world_px * min_px * 0.5 - rope_r);
    VERTEX += NORMAL * extra;
    swollen = extra > 0.0 ? 1.0 : 0.0;
}
void fragment() {
    vec4 c = texture(tex, UV);
    // a rope swollen to a minimum width is a solid line: the texture's transparent strips would otherwise leave nothing of a 2-pixel tube
    ALBEDO = (swollen > 0.5 && c.a < 0.12) ? vec3(0.62, 0.52, 0.36) : c.rgb;
    ALPHA = swollen > 0.5 ? 1.0 : c.a;
    ALPHA_SCISSOR_THRESHOLD = 0.12;
    ROUGHNESS = 1.0;
}";
    // vant.cpp: the triangle, the cross beam, the ratlines quad and 5 tubes of the vant texture (no motion except with the ship)
    const string VantShader = @"
shader_type spatial;
render_mode cull_disabled;
uniform sampler2D tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
void fragment() {
    vec4 c = texture(tex, UV);
    ALBEDO = c.rgb;
    ALPHA = c.a;
    ALPHA_SCISSOR_THRESHOLD = 0.12;
    ROUGHNESS = 1.0;
}";
    static ShaderMaterial? ropeMat, vantMat;
    static void EnsureMaterials()
    {
        if (ropeMat != null) return;
        ropeMat = new ShaderMaterial { Shader = new Shader { Code = RopeShader } };
        ropeMat.SetShaderParameter("tex", GameAssets.Texture(RopeTex()) ?? GameAssets.Texture("ships\\" + RopeTex()));
        ropeMat.SetShaderParameter("spd", new Vector4(RopePar("fDeepChangeSpeed", 0.15f) * 20f, RopePar("fRotateSpeed", 0.08f) * 20f, RopePar("fMinDeepMul", 1f), RopePar("fVarDeepMul", 0.7f)));
        ropeMat.SetShaderParameter("rot_ang", RopePar("fRotateAng", 0.5f));
        ropeMat.SetShaderParameter("rope_r", RopePar("fWIDTH", 0.025f) * 0.5f);
        vantMat = new ShaderMaterial { Shader = new Shader { Code = VantShader } };
        vantMat.SetShaderParameter("tex", GameAssets.Texture("vant.tga") ?? GameAssets.Texture("ships\\vant.tga"));
        if (System.Environment.GetEnvironmentVariable("SEADOGS_RIGLOG") != null)
            foreach (var n in new[] { "sail_rope.tga", "vant.tga", "flagall.tga", "rope.tga" })
                try { var im = (GameAssets.Texture("ships\\" + n) ?? GameAssets.Texture(n))?.GetImage(); if (im != null) { im.SavePng("review/tex_" + n + ".png"); GD.Print($"[rig] {n} {im.GetWidth()}x{im.GetHeight()}"); } } catch (Exception) { }
    }
    static string RopeTex() => "sail_rope.tga";

    /// <summary>One mesh (surface 0: ropes, surface 1: vants) of the given ropes in the ship root's space (the ends are read from the current transforms of their nodes).</summary>
    public static MeshInstance3D? BuildRopes(Node3D shipRoot, IEnumerable<Rope> list, string name = "Ropes")
    {
        EnsureMaterials();
        var rst = new SurfaceTool(); rst.Begin(Mesh.PrimitiveType.Triangles);
        var vst = new SurfaceTool(); vst.Begin(Mesh.PrimitiveType.Triangles);
        int ropes = 0, vants = 0; ropeBase = 0;
        foreach (var rope in list)
        {
            var a = ToRoot(rope.A.Node, shipRoot) * rope.A.Local; var b = ToRoot(rope.B.Node, shipRoot) * rope.B.Local;
            if (rope.U is not { } ue) { if (AddRope(rst, a, b)) ropes++; continue; }
            AddVant(vst, ToRoot(ue.Node, shipRoot) * ue.Local, a, b);
            vants++;
        }
        if (ropes == 0 && vants == 0) return null;
        var mesh = new ArrayMesh();
        if (ropes > 0) { rst.Commit(mesh); mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, ropeMat); }
        if (vants > 0) { vst.GenerateNormals(); vst.Commit(mesh); mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, vantMat); }
        var mi = new MeshInstance3D { Mesh = mesh, Name = name, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        mi.SetMeta("ropes", ropes + vants);
        return mi;
    }

    /// <summary>Per-frame time of the rope shader (flags use their own).</summary>
    public static void UpdateRopes(float time) { ropeMat?.SetShaderParameter("t", time); }

    /// <summary>Transform of a node relative to the ship root (the nodes are not in the tree yet while the model is built).</summary>
    public static Transform3D ToRoot(Node3D n, Node3D root)
    {
        var t = Transform3D.Identity;
        for (Node? c = n; c != null && c != root; c = c.GetParent()) if (c is Node3D n3) t = n3.Transform * t;
        return t;
    }

    const int Edge = 5;           // ROPE_EDGE / VANT_EDGE

    /// <summary>A rope as the original's tube (rope.cpp). Returns false for lengths the engine drops (&lt; 0.1 or &gt; 200).</summary>
    static int ropeBase;
    static bool AddRope(SurfaceTool st, Vector3 a, Vector3 b)
    {
        float segLen = RopePar("fSEG_LENGTH", 2f), width = RopePar("fWIDTH", 0.025f), endLen = RopePar("fEND_LENGTH", 0.05f), wave = RopePar("fWAVE", 0.1f), texLen = RopePar("fTexHeight", 0.5f);
        float len = a.DistanceTo(b);
        if (len < 0.1f || len > 200f) return false;
        int n = (int)(len / segLen) + 1;
        float amp = n <= 2 ? wave * 0.2f : wave;
        var dir = (b - a) / len;
        var e1 = dir.Cross(Math.Abs(dir.Y) > 0.99f ? Vector3.Right : Vector3.Up).Normalized();
        var e2 = dir.Cross(e1).Normalized();
        var ring = new Vector3[Edge];
        for (int i = 0; i < Edge; i++) { float ang = i / (float)Edge * MathF.Tau; ring[i] = (e1 * MathF.Cos(ang) + e2 * MathF.Sin(ang)) * (width * 0.5f); }
        Vector3 cv = dir * endLen;
        Vector3 start = a + cv, dtV = (b - cv - start) / n;
        float tv0 = endLen / texLen, dtv = (len / texLen - tv0) / n;
        int vi = 0, b0 = ropeBase;                     // indices are absolute in the surface: every rope starts after the vertices of the ropes before it
        void V(Vector3 p, Vector3 nrm, float u, float v, float f) { st.SetNormal(nrm); st.SetUV(new Vector2(u, v)); st.SetUV2(new Vector2(f, amp)); st.AddVertex(p); vi++; }
        V(a, -dir, 0f, 0f, 0f);
        for (int k = 0; k <= n; k++)
        {
            float f = 4f * k * (n - k) / (n * (float)n);
            Vector3 c = start + dtV * k;
            for (int i = 0; i < Edge; i++)
            {
                float u = i <= Edge / 2 ? i / (float)(Edge / 2) : 1f - (i - Edge / 2) / (float)((Edge + 1) / 2);
                V(c + ring[i], ring[i].Normalized(), u, tv0 + dtv * k, f);
            }
        }
        V(b, dir, 1f, len, 0f);
        int last = vi - 1;
        for (int i = 0; i < Edge; i++) { st.AddIndex(b0 + 0); st.AddIndex(b0 + 1 + (i + 1) % Edge); st.AddIndex(b0 + 1 + i); }
        for (int k = 0; k < n; k++)
            for (int i = 0; i < Edge; i++)
            {
                int i0 = 1 + k * Edge + i, i1 = 1 + k * Edge + (i + 1) % Edge, j0 = i0 + Edge, j1 = i1 + Edge;
                st.AddIndex(b0 + i0); st.AddIndex(b0 + i1); st.AddIndex(b0 + j0);
                st.AddIndex(b0 + i1); st.AddIndex(b0 + j1); st.AddIndex(b0 + j0);
            }
        for (int i = 0; i < Edge; i++) { st.AddIndex(b0 + 1 + n * Edge + i); st.AddIndex(b0 + 1 + n * Edge + (i + 1) % Edge); st.AddIndex(b0 + last); }
        ropeBase += vi;
        return true;
    }

    /// <summary>A shroud ladder as vant.cpp builds it: the triangle at the mast (apex u, fTreangWidth x fTreangHeight) with the beam, the ratlines quad down to the hull points l / r
    /// and fRopeQuant tubes (fWidth) between the triangle and the hull, all taken from the texture regions of [VANTS].</summary>
    static void AddVant(SurfaceTool st, Vector3 u, Vector3 l, Vector3 r)
    {
        float width = VantPar("fWidth", 0.1f), upW = VantPar("fTreangWidth", 1f), upH = VantPar("fTreangHeight", 1f), balkH = VantPar("fBalkHeight", 0.1f), balkW = VantPar("fBalkWidth", 1.2f);
        int quant = Math.Max(2, (int)VantPar("fRopeQuant", 5));
        float ropeXl = VantPar("fHRopeXbeg", 0.5f), ropeXr = VantPar("fHRopeXend", 1f), tXl = VantPar("fTreangXbeg", 0f), tXr = VantPar("fTreangXend", 0.5f), tYu = VantPar("fTreangYbeg", 0f), tYd = VantPar("fTreangYend", 1f);
        float bYu = VantPar("fBalkYbeg", 0.6f), bYd = VantPar("fBalkYend", 1f), vXl = VantPar("fVRopeXbeg", 0f), vXr = VantPar("fVRopeXend", 0.1f), vH = VantPar("fVRopeHeight", 1f), hH = VantPar("fHRopeHeight", 1f);
        Vector3 horz = (r - l).Normalized(), vert = ((r + l) * 0.5f - u).Normalized();
        float fh = ((r + l) * 0.5f - u).Length();
        var pos = new Vector3[Edge];
        for (int i = 0; i < Edge; i++) { float ang = i / (float)Edge * MathF.Tau; pos[i] = new Vector3(MathF.Cos(ang), 0f, MathF.Sin(ang)) * (width * 0.5f); }
        void T(Vector3 p0, Vector2 t0, Vector3 p1, Vector2 t1, Vector3 p2, Vector2 t2)
        {
            st.SetUV(t0); st.AddVertex(p0); st.SetUV(t1); st.AddVertex(p1); st.SetUV(t2); st.AddVertex(p2);
        }
        // the centre triangle and the ratlines square
        Vector3 tl = u - horz * (upW * 0.5f) + vert * upH * (1f - balkH), tr = u + horz * (upW * 0.5f) + vert * upH * (1f - balkH);
        T(u, new((tXl + tXr) * 0.5f, tYu), tr, new(tXr, tYd), tl, new(tXl, tYd));
        float rows = MathF.Floor(fh / hH + 0.5f);
        Vector2 uvTl = new(ropeXl, 0f), uvTr = new(ropeXr, 0f), uvL = new(ropeXl, rows), uvR = new(ropeXr, rows);
        T(tl, uvTl, tr, uvTr, l, uvL);
        T(tr, uvTr, r, uvR, l, uvL);
        // the beam: a prism of 6 points
        Vector3 bt = u - horz * (upW * 0.5f) + vert * upH;
        Vector3[] bp = new Vector3[6]; Vector2[] bu = new Vector2[6];
        bp[0] = bt - vert * upH * balkH; bp[1] = bt + pos[0] * balkW; bp[2] = bt + pos[Edge / 2] * balkW;
        bt += horz * upW;
        bp[3] = bt - vert * upH * balkH; bp[4] = bt + pos[0] * balkW; bp[5] = bt + pos[Edge / 2] * balkW;
        bu[0] = bu[1] = bu[2] = new(tXl, 0f); bu[3] = bu[4] = bu[5] = new(tXr, 0f);
        bu[0].Y = bu[3].Y = bYu; bu[1].Y = bu[2].Y = bu[4].Y = bu[5].Y = bYd;
        int[] bi = { 0, 1, 2, 3, 4, 5, 0, 3, 4, 0, 4, 1, 0, 2, 3, 3, 2, 5, 2, 4, 5, 1, 4, 2 };
        for (int k = 0; k < bi.Length; k += 3) T(bp[bi[k]], bu[bi[k]], bp[bi[k + 1]], bu[bi[k + 1]], bp[bi[k + 2]], bu[bi[k + 2]]);
        // the tubes from the triangle's top line down to the hull points
        Vector3 sp = u - horz * (0.5f * upW) + vert * upH, dp = horz * (upW / (quant - 1));
        Vector3 hp = l, dh = (r - l) / (quant - 1);
        float dtmp = (vXr - vXl) / Edge, down = fh / vH;
        for (int q = 0; q < quant; q++)
        {
            for (int j = 0; j < Edge; j++)
            {
                int j1 = j + 1;
                Vector3 a0 = sp + pos[j], a1 = sp + pos[j1 % Edge], d0 = hp + pos[j], d1 = hp + pos[j1 % Edge];
                float x0 = vXl + dtmp * j, x1 = vXl + dtmp * j1;
                T(a0, new(x0, 0f), d1, new(x1, down), d0, new(x0, down));
                T(a0, new(x0, 0f), a1, new(x1, 0f), d1, new(x1, down));
            }
            sp += dp; hp += dh;
        }
    }

    // ------------------------------------------------------------------ flags
    // flag.cpp: FLAG::DoMove. The flag streams from the middle of its pole edge in the direction the wind blows (fSegLen per segment, sinking fDownVal per segment); every
    // segment i waves by fWindAm x (sin/cos of alfa_i - alfa_0) with alfa_i = -Alfa + i x (fAlfaStep + fAlfaMax / n) and its half height turns by
    // hIncr x (sin beta_i - sin beta_0) x fRotAm; Alfa/Beta grow by (alfa_depend + rand x alpha_rand) per 1/20 s. The engine's coordinates are (x, y, z), ours (x, y, -z).
    // UV2 = (segment index, +1 top / -1 bottom edge); everything is computed in the host part's space, the wind comes in world space.
    const string FlagShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled;
uniform sampler2D tex : source_color, filter_linear_mipmap_anisotropic;
uniform vec4 tile;          // x0, y0, w, h of the nation's flag in the atlas
uniform float t;
uniform vec3 windW;         // world direction the wind blows to
uniform vec3 spos;          // middle of the pole edge (host space, ours)
uniform vec3 h0;            // half height vector at the pole
uniform float n;            // segments
uniform float dhy;          // ddhv.y
uniform vec4 par;           // seg len, down val, wind am, rot am
uniform vec4 ang;           // alfa step, alfa max, beta step, beta max
uniform bool dbg = false;
uniform vec2 phase;         // start Alfa, Beta (rad) and their speeds are below
uniform vec2 rate = vec2(3.0, 4.0);
void vertex() {
    float i = UV2.x;
    float side = UV2.y;
    vec3 wl = inverse(mat3(MODEL_MATRIX)) * windW;
    float xm = wl.x;            // engine wind x / z (z is flipped back from ours)
    float zm = -wl.z;
    float l = max(length(vec2(xm, zm)), 0.0001);
    xm /= l; zm /= l;
    float a0 = -(phase.x + rate.x * t);
    float b0 = -(phase.y + rate.y * t);
    float ai = a0 + i * (ang.x + ang.y / n);
    float bi = b0 + i * (ang.z + ang.w / n);
    float tmp = par.z * (sin(ai) - sin(a0));
    vec3 dop = vec3(tmp * zm, par.z * (cos(ai) - cos(a0)), tmp * xm);          // engine coordinates
    vec3 c = vec3(spos.x, spos.y, -spos.z) + i * vec3(par.x * xm, -par.y, par.x * zm) + dop;
    vec3 dh = vec3(h0.x, h0.y, -h0.z);
    if (i > 0.5) {
        float h = h0.y - i * dhy;
        float r = h * (sin(bi) - sin(b0)) * par.w;
        if (abs(r) > abs(h)) dh = vec3(h * zm, 0.0, h * xm);
        else dh = vec3(r * zm, sqrt(abs(h * h - r * r)), r * xm);
    }
    VERTEX = vec3(c.x + side * dh.x, c.y + side * dh.y, -(c.z + side * dh.z));
}
void fragment() {
    vec4 c = texture(tex, tile.xy + UV * tile.zw);
    if (c.a < 0.5 && !dbg) discard;
    ALBEDO = dbg ? vec3(1.0, 0.0, 0.0) : c.rgb;
}";
    static Shader? flagShader;
    static readonly List<ShaderMaterial> flagMats = new();

    /// <summary>Set by SeaView: the flag atlas tile of a nation as the script answers GetRiggingData "GetFlagTexNum".</summary>
    public static Func<int, int>? FlagTexNum;

    /// <summary>Where the wind blows to (world, Godot axes) - set by the sea each frame.</summary>
    public static Vector3 WindWorld = new(0, 0, -1);
    static readonly RandomNumberGenerator flagRng = new();
    public static void UpdateFlags(float time)
    {
        foreach (var m in flagMats) { m.SetShaderParameter("t", time); m.SetShaderParameter("windW", WindWorld); }
        UpdateRopes(time);
    }

    /// <summary>Flags at the mast tops: the labels f1..f4 of every "flag*" group are the corners of one flag. Each flag hangs on its model part (a falling mast takes it along).</summary>
    public static IEnumerable<(Node3D host, Node3D flag)> BuildFlags(IEnumerable<Part> parts, int nation)
    {
        var tex = GameAssets.Texture("flagall.tga") ?? GameAssets.Texture("ships\\flagall.tga");
        flagShader ??= new Shader { Code = FlagShader };
        // the tile of the nation comes from the script: flag.cpp raises "GetRiggingData" ("GetFlagTexNum", triangle, nation) -> BattleInterface.c
        // procGetRiggingData (England 1, France 2, Spain 4, pirate 5, Holland 0, anything else 0); the table is the fallback without a script
        int tileIdx = FlagTexNum?.Invoke(nation) ?? nation switch { 0 => 1, 1 => 2, 2 => 4, 3 => 5, _ => 0 };
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

    /// <summary>rigging.ini [FLAGS] / flag.cpp: the pole edge is f1-f4, the free edge f2-f3; the flag is cut into len / fSegLen (at least 4) segments.</summary>
    static Node3D MakeFlag(Vector3[] c, Texture2D? tex, int tileIdx, int tiles)
    {
        Vector3 p0 = c[0], p2 = c[1], p3 = c[2], p1 = c[3];       // f1, f2, f3, f4
        Vector3 bm = (p0 + p1) * 0.5f, em = (p2 + p3) * 0.5f;
        float segLen = FlagPar("fSegLen", 0.2f);
        int n = Math.Max((int)FlagPar("MinSegQuantity", 4f), (int)(bm.DistanceTo(em) / segLen));
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i <= n; i++)
            for (int s = 0; s < 2; s++)
            {
                st.SetUV(new Vector2(i / (float)n, s)); st.SetUV2(new Vector2(i, s == 0 ? 1f : -1f)); st.AddVertex(bm);
            }
        for (int i = 0; i < n * 2; i++) { st.AddIndex(i); st.AddIndex(i + 1); st.AddIndex(i + 2); }
        var mat = new ShaderMaterial { Shader = flagShader };
        mat.SetShaderParameter("tex", tex);
        float w = tex?.GetWidth() ?? 1, h = tex?.GetHeight() ?? 1;
        if (System.Environment.GetEnvironmentVariable("SEADOGS_RIGLOG") != null) GD.Print($"[flag] atlas {w}x{h}, tile {tileIdx}, {n} segments, length {bm.DistanceTo(em):0.00} f1 {p0} f2 {p2} f3 {p3} f4 {p1}");
        mat.SetShaderParameter("tile", w >= h ? new Vector4(tileIdx / (float)tiles, 0, 1f / tiles, 1) : new Vector4(0, tileIdx / (float)tiles, 1, 1f / tiles));
        mat.SetShaderParameter("dbg", System.Environment.GetEnvironmentVariable("SEADOGS_FLAGDBG") != null);
        mat.SetShaderParameter("t", 0f);
        mat.SetShaderParameter("windW", WindWorld);
        mat.SetShaderParameter("spos", bm);
        mat.SetShaderParameter("h0", p0 - bm);
        mat.SetShaderParameter("n", (float)n);
        mat.SetShaderParameter("dhy", (p2.Y - p0.Y - em.Y + bm.Y) / (n + 1));
        mat.SetShaderParameter("par", new Vector4(segLen, FlagPar("fDownVal", 0.05f), FlagPar("fWindAm", 0.2f), FlagPar("fRotAm", 0.2f)));
        mat.SetShaderParameter("ang", new Vector4(FlagPar("fAlfaStep", 0.2f), FlagPar("fAlfaMax", 4.71f), FlagPar("fBetaStep", 0.3f), FlagPar("fBetaMax", 7.85f)));
        mat.SetShaderParameter("phase", new Vector2(flagRng.Randf() * MathF.Tau, flagRng.Randf() * MathF.Tau));
        mat.SetShaderParameter("rate", new Vector2((FlagPar("alfa_depend", 0.1f) + 0.5f * FlagPar("alpha_rand", 0.1f)) * 20f, (FlagPar("beta_depend", 0.06f) + 0.5f * FlagPar("beta_rand", 0.1f)) * 20f));
        flagMats.Add(mat);
        var mi = new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = mat, Name = "Flag", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        mi.CustomAabb = new Aabb(bm - new Vector3(1, 1, 1) * (bm.DistanceTo(em) * 1.5f + 2f), new Vector3(1, 1, 1) * (bm.DistanceTo(em) * 3f + 4f));
        return mi;
    }
}
