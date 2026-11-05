using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using StormScript.Formats;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine classes "Sail" and "MAST" (sail.cpp / mast.cpp of the original), as far as the scripts and the picture need them:
/// <list type="bullet">
/// <item>every sail of the model (label groups saild*, sailf* = four corners, sailt* = three corners) is cloth computed in a vertex shader: it rolls up to its
/// yard (square sails) or folds to its stay (triangular ones) when the sail state goes down (0 furled, 0.5 half: square sails reefed half way, 1 full),
/// bulges with the wind relative to the sail (rigging.ini [SAILS] fSsailWindDepend / fTsailWindDepend) and flaps when the wind runs along it;</item>
/// <item>the yards turn to the wind up to MAXTURNANGL;</item>
/// <item>holes: a ball through a sail raises "DoSailHole" (BattleInterface.c ProcessSailDamage: the script counts the damage, asks the engine for new holes
/// with the native RandomHole2Sail and keeps them in Ship.sails.(node).(group).hc/hd); storms send MSG_SAIL_SCRIPT_PROCESSING "RandomSailsDmg" which is answered by
/// "evntRandomSailDmg" per hole. The holes are drawn with parus_hole.tga (its right half is the torn cloth, 4 x 3 cells per square sail, 5 x 2 per triangle);</item>
/// <item>masts: a ball that touches a mast raises "ShpMastDamage" (the script adds the damage of the ball type and returns the new value, kept in Ship.Masts.mastN);
/// at 1 the mast falls: "EventMastFall" (the script tells PEOPLE_ON_SHIP: MSG_PEOPLES_ON_SHIP_MASTFALL), "DoSailHole" with the mast name for every sail on it
/// (the script writes those sails off from Ship.SP), and the mast with its yards, sails and flags tips over into the sea and sinks.</item>
/// </list>
/// </summary>
public partial class SeaView
{
    const int MSG_SAIL_SCRIPT_PROCESSING = 40099, SHIP_MAST_TOUCH_BALL = 2;

    // ------------------------------------------------------------------ rigging.ini [SAILS]
    sealed class SailIni
    {
        public float SDepend = 0.3f, TDepend = 0.1f, MaxTurn = 0.6f, RollSpeed = 0.3f, HoleDepend = 1f, RollZ = 0.05f, RollY = 0.06f, TurnMul = 1.5f;
        public float[] SForm = { 0.2f, 0.8f, 1.0f, 0.8f, 0.4f, 1.0f, 1.3f, 1.0f, 0.4f, 0.8f, 1.0f, 0.8f, 0.2f };
        public float[] TForm = { 0.15f, 0.5f, 0.25f, 0.8f, 0.15f };
    }
    static SailIni? sailIni;

    static SailIni Ini
    {
        get
        {
            if (sailIni != null) return sailIni;
            sailIni = new SailIni();
            try
            {
                var sec = IniFile.Load(GameAssets.Resource("INI\\rigging.ini")).Get("SAILS");
                if (sec != null)
                {
                    float Fv(string k, float d) => float.TryParse(sec.Get(k)?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
                    float[] Arr(string k, float[] d)
                    {
                        var parts = sec.Get(k)?.Split(',');
                        if (parts == null || parts.Length < 2) return d;
                        var r = new List<float>();
                        foreach (var p in parts) if (float.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) r.Add(v);
                        return r.Count == d.Length ? r.ToArray() : d;
                    }
                    sailIni.SDepend = Fv("fSsailWindDepend", 0.3f); sailIni.TDepend = Fv("fTsailWindDepend", 0.1f);
                    sailIni.MaxTurn = Fv("MAXTURNANGL", 0.6f); sailIni.RollSpeed = Fv("ROLLINGSPEED", 0.0003f) * 1000f;       // per millisecond in the ini
                    sailIni.HoleDepend = Fv("fHoleDepend", 1f); sailIni.RollZ = Fv("ROLL_Z_VAL", 0.05f); sailIni.RollY = Fv("ROLL_Y_VAL", 0.06f);
                    sailIni.TurnMul = Fv("tr_form_mul", 1.5f);
                    sailIni.SForm = Arr("rollSSailForm", sailIni.SForm); sailIni.TForm = Arr("rollTSailForm", sailIni.TForm);
                }
            }
            catch (Exception ex) { GD.PrintErr("[sail] rigging.ini: " + ex.Message); }
            return sailIni;
        }
    }

    // ------------------------------------------------------------------ runtime data
    sealed class SailRt
    {
        public MeshInstance3D Mi = null!;
        public ShaderMaterial Mat = null!;
        public Node3D Host = null!;                 // the yard (or the mast for sails set on stays)
        public MastRt Mast = null!;
        public string Node = "";                    // script name of the host node: Ship.sails.(Node).(Group)
        public int Group;
        public bool Tri, ForeAft;                   // three corners; the cloth stands along the ship (bulges to either side)
        public Vector3[] C = null!;                 // corners in host space: square tl, tr, br, bl; triangle tack, head, clew
        public Vector3 N;                           // normal of the flat sail in host space (cross of the first two edges)
        public float Fwd = 1f;                      // +1 when N points to the bow
        public float Depth, Area, Power;
        public int MaxHoles, HoleCount, Holes;
        public float Roll, Bulge, Flap, Phase;
        public float SetRoll = -1f, SetBulge = -99f, SetFlap = -1f;
        public bool Gone;
    }

    sealed class YardRt { public Node3D Node = null!; public Transform3D Rest; public Vector3 Axis; }

    sealed class MastRt
    {
        public Node3D Node = null!;
        public string Name = "";                    // label name of the hull ("mast1"), the attribute name in Ship.Masts
        public int Num;
        public float Top = 10f;                     // height of the mast model in its own space
        public Vector3 Base;                        // foot of the mast in the ship model's engine space (x right, y up, z forward)
        public bool Bowsprit, Down;
        public readonly List<SailRt> Sails = new();
        public readonly List<YardRt> Yards = new();
    }

    sealed class FallingMast { public Node3D Node = null!; public Transform3D Start; public Vector3 Axis; public float Ang, Vel, T, Sink; }
    readonly List<FallingMast> fallingMasts = new();

    void ClearSails()
    {
        foreach (var f in fallingMasts) if (GodotObject.IsInstanceValid(f.Node)) f.Node.QueueFree();
        fallingMasts.Clear();
    }

    // ------------------------------------------------------------------ building
    static Shader? sailShader;
    static ArrayMesh? sailGrid;
    static Texture2D? holeTex;

    static Shader SailShader()
    {
        if (sailShader != null) return sailShader;
        var ini = Ini;
        string Arr(float[] a) => string.Join(", ", a.Select(x => x.ToString("0.0###", CultureInfo.InvariantCulture)));
        string Fl(float x) => x.ToString("0.0#####", CultureInfo.InvariantCulture);
        string code = @"
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque, specular_disabled;
uniform sampler2D tex : source_color, filter_linear_mipmap;
uniform sampler2D holes : filter_linear_mipmap;
uniform vec3 c0; uniform vec3 c1; uniform vec3 c2; uniform vec3 c3;
uniform vec3 nrm;
uniform float tri = 0.0;
uniform float roll = 0.0;
uniform float bulge = 0.0;
uniform float flap = 0.0;
uniform float phase = 0.0;
uniform int hmask = 0;
const float SF[13] = { $SF };
const float TF[5] = { $TF };

float sform(float u) { float x = clamp(u, 0.0, 1.0) * 12.0; int i = min(int(x), 11); return mix(SF[i], SF[i + 1], x - float(i)); }
float tform(float u) { float x = clamp(u, 0.0, 1.0) * 4.0; int i = min(int(x), 3); return mix(TF[i], TF[i + 1], x - float(i)); }

vec3 sailPos(vec2 uv, float t) {
    float u = uv.x, v = uv.y, s = 1.0 - roll;
    float w = sin(PI * clamp(u, 0.0, 1.0));
    float wave = flap * sin(u * 7.0 + v * 4.0 + t * 8.0 + phase) * sin(t * 2.3 + phase);
    if (tri > 0.5) {
        // triangle: the luff on the stay (c0 tack, c1 head), the clew c2 is pulled to the stay when the sail is taken in
        vec3 stay = mix(c0, c1, u);
        vec3 p = mix(stay, c2, v * s);
        float b = w * sin(PI * clamp(v, 0.0, 1.0));
        p += nrm * (b * s * (bulge + wave));
        p += nrm * (roll * $RZ * length(c1 - c0) * tform(u) * sin(PI * clamp(v, 0.0, 1.0)));
        return p;
    }
    // square: the head on the yard (c0, c1); taking the sail in gathers the cloth to the yard into a bundle (rollSSailForm along the yard)
    vec3 top = mix(c0, c1, u), bot = mix(c3, c2, u);
    vec3 p = mix(top, bot, v * s);
    float h = length(bot - top);
    vec3 dn = (bot - top) / max(h, 0.001);
    float a = PI * clamp(v, 0.0, 1.0);
    p += roll * sform(u) * h * (nrm * $RZ * sin(a) + dn * $RY * 0.5 * (1.0 - cos(a)));
    float b = pow(w, 0.8) * sin(PI * clamp(v, 0.0, 1.0) * 0.85);
    p += nrm * (b * s * (bulge + wave));
    return p;
}

void vertex() {
    vec3 p = sailPos(UV, TIME);
    vec3 du = sailPos(UV + vec2(0.02, 0.0), TIME) - p;
    vec3 dv = sailPos(UV + vec2(0.0, 0.02), TIME) - p;
    vec3 n = cross(du, dv);
    NORMAL = length(n) > 1e-6 ? normalize(n) : nrm;
    VERTEX = p;
}

void fragment() {
    vec4 c = texture(tex, UV);
    float a = c.a;
    if (hmask != 0) {
        int cell = tri > 0.5 ? min(int(UV.y * 2.0), 1) * 5 + min(int(UV.x * 5.0), 4) : min(int(UV.y * 3.0), 2) * 4 + min(int(UV.x * 4.0), 3);
        if (((hmask >> cell) & 1) != 0) a = min(a, texture(holes, vec2(0.5 + clamp(UV.x, 0.0, 1.0) * 0.499, clamp(UV.y, 0.0, 1.0) * 0.499)).a);
    }
    if (a < 0.5) discard;
    ALBEDO = c.rgb;
    ROUGHNESS = 1.0;
}";
        code = code.Replace("$SF", Arr(ini.SForm)).Replace("$TF", Arr(ini.TForm)).Replace("$RZ", Fl(ini.RollZ)).Replace("$RY", Fl(ini.RollY));
        sailShader = new Shader { Code = code };
        return sailShader;
    }

    /// <summary>One flat grid shared by all sails: vertex = (u, v, 0); the shader places it between the corners.</summary>
    static ArrayMesh SailGrid()
    {
        if (sailGrid != null) return sailGrid;
        const int N = 12;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                Vector2 a = new(i / (float)N, j / (float)N), b = new((i + 1) / (float)N, j / (float)N), c = new((i + 1) / (float)N, (j + 1) / (float)N), d = new(i / (float)N, (j + 1) / (float)N);
                foreach (var t in new[] { a, b, c, a, c, d }) { st.SetUV(t); st.SetNormal(Vector3.Back); st.AddVertex(new Vector3(t.X, t.Y, 0f)); }
            }
        sailGrid = st.Commit();
        return sailGrid;
    }

    static int TrailingNumber(string s)
    {
        int i = s.Length;
        while (i > 0 && char.IsDigit(s[i - 1])) i--;
        return i < s.Length && int.TryParse(s.AsSpan(i), out var n) ? n : 0;
    }

    /// <summary>The sails of one model part (a yard, or a mast for the stay sails): label groups sail* with corners sail1..sail4 (sail1..sail3 for triangles).</summary>
    static IEnumerable<SailRt> MakeSails(GmFile gm, Node3D host, Node3D root, MastRt mast, Texture2D? tex, Texture2D? triTex)
    {
        foreach (var grp in gm.Labels.Where(l => l.Group.StartsWith("sail", StringComparison.OrdinalIgnoreCase) && l.Name.StartsWith("sail", StringComparison.OrdinalIgnoreCase)).GroupBy(l => l.Group.ToLowerInvariant()))
        {
            var c = new Vector3[4];
            int have = 0;
            foreach (var l in grp)
                if (l.Name.Length == 5 && l.Name[4] >= '1' && l.Name[4] <= '4') { c[l.Name[4] - '1'] = new Vector3(l.M[12], l.M[13], -l.M[14]); have |= 1 << (l.Name[4] - '1'); }
            bool tri = grp.Key.Length > 4 && grp.Key[4] == 't';
            if (tri ? (have & 7) != 7 : have != 15) continue;
            var toRoot = RiggingFx.ToRoot(host, root);
            if (tri)
            {
                // head = the highest corner, tack = the one nearer the bow (Godot -z), clew = the other: the luff runs along the stay
                var r = c.Take(3).Select(p => toRoot * p).ToArray();
                int head = r[0].Y >= r[1].Y && r[0].Y >= r[2].Y ? 0 : r[1].Y >= r[2].Y ? 1 : 2;
                int o1 = (head + 1) % 3, o2 = (head + 2) % 3;
                int tack = r[o1].Z <= r[o2].Z ? o1 : o2, clew = tack == o1 ? o2 : o1;
                c = new[] { c[tack], c[head], c[clew], c[clew] };
            }
            var n = tri ? (c[1] - c[0]).Cross(c[2] - c[0]) : (c[1] - c[0]).Cross(c[3] - c[0]);
            float area = tri ? n.Length() * 0.5f : n.Length();
            if (area < 0.05f) continue;
            n = n.Normalized();
            var rn = (toRoot.Basis * n).Normalized();
            var sl = new SailRt
            {
                Host = host, Mast = mast, Node = host.Name.ToString().ToLowerInvariant(), Group = TrailingNumber(grp.Key), Tri = tri, C = c, N = n, Area = area,
                ForeAft = tri || MathF.Abs(rn.Z) < 0.5f, Fwd = rn.Z <= 0f ? 1f : -1f, MaxHoles = tri ? 10 : 12, Phase = (float)Random.Shared.NextDouble() * 6.28f,
            };
            float width = (c[1] - c[0]).Length(), height = tri ? (c[2] - c[0]).Length() : (c[3] - c[0]).Length();
            sl.Depth = sl.ForeAft ? Ini.TDepend * 1.2f * Math.Max(width, height) : Ini.SDepend * 0.4f * width;
            holeTex ??= GameAssets.Texture("ships\\parus_hole.tga");
            sl.Mat = new ShaderMaterial { Shader = SailShader() };
            sl.Mat.SetShaderParameter("tex", tri ? triTex : tex);
            sl.Mat.SetShaderParameter("holes", holeTex);
            for (int k = 0; k < 4; k++) sl.Mat.SetShaderParameter("c" + k, c[k]);
            sl.Mat.SetShaderParameter("nrm", n);
            sl.Mat.SetShaderParameter("tri", tri ? 1f : 0f);
            sl.Mat.SetShaderParameter("phase", sl.Phase);
            var box = new Aabb(c[0], Vector3.Zero);
            foreach (var p in c) box = box.Expand(p);
            float grow = Math.Max(1f, sl.Depth * 1.8f);
            sl.Mi = new MeshInstance3D { Mesh = SailGrid(), MaterialOverride = sl.Mat, Name = "Sail_" + grp.Key, CustomAabb = box.Grow(grow) };
            host.AddChild(sl.Mi);
            mast.Sails.Add(sl);
            yield return sl;
        }
    }

    /// <summary>Ropes that end on a turning yard are kept in a second mesh which is rebuilt when the yards turn; the others are built once.</summary>
    static void BuildShipRopes(Node3D root, List<RiggingFx.Rope> ropes, HashSet<Node3D> yards, ref MeshInstance3D? fixedRopes, ref MeshInstance3D? yardRopes, bool all)
    {
        if (all)
        {
            fixedRopes?.QueueFree();
            fixedRopes = RiggingFx.BuildRopes(root, ropes.Where(r => !r.Nodes.Any(yards.Contains)), "Ropes");
            if (fixedRopes != null) root.AddChild(fixedRopes);
        }
        yardRopes?.QueueFree();
        yardRopes = RiggingFx.BuildRopes(root, ropes.Where(r => r.Nodes.Any(yards.Contains)), "YardRopes");
        if (yardRopes != null) root.AddChild(yardRopes);
    }

    void RebuildRopes(ShipRt s, bool all)
    {
        var f = s.FixedRopes; var y = s.YardRopes;
        BuildShipRopes(s.Node, s.Ropes, s.YardNodes, ref f, ref y, all);
        s.FixedRopes = f; s.YardRopes = y;
    }

    /// <summary>After the ship is created: masts already broken (Ship.Masts.mastN >= 1) are gone, the holes stored in Ship.sails are shown.</summary>
    void ApplyRiggingState(ShipRt s)
    {
        var sh = s.Chr.Find("Ship", false);
        if (sh == null) return;
        bool removed = false;
        foreach (var m in s.Masts)
        {
            if (F(sh, "Masts." + m.Name) < 1f) continue;
            m.Down = true; removed = true;
            foreach (var sl in m.Sails) sl.Gone = true;
            foreach (var y in m.Yards) s.YardNodes.Remove(y.Node);
            s.Ropes.RemoveAll(r => r.Nodes.Any(n => n == m.Node || m.Node.IsAncestorOf(n)));
            m.Node.GetParent()?.RemoveChild(m.Node);
            m.Node.QueueFree();
        }
        if (removed) RebuildRopes(s, true);
        foreach (var sl in s.Sails)
            if (!sl.Gone && sh.Find("sails." + sl.Node + "." + sl.Group, false) is { } a) SetHoles(sl, (int)F(a, "hd"));
    }

    static void SetHoles(SailRt sl, int mask)
    {
        mask &= (1 << sl.MaxHoles) - 1;
        sl.Holes = mask;
        sl.HoleCount = System.Numerics.BitOperations.PopCount((uint)mask);
        sl.Mat.SetShaderParameter("hmask", mask);
    }

    static int AddHoles(int mask, int max, int add)
    {
        var free = Enumerable.Range(0, max).Where(i => (mask & (1 << i)) == 0).OrderBy(_ => Random.Shared.Next()).Take(Math.Max(0, add));
        foreach (var i in free) mask |= 1 << i;
        return mask;
    }

    static int RemoveHoles(int mask, int max, int del)
    {
        var set = Enumerable.Range(0, max).Where(i => (mask & (1 << i)) != 0).OrderBy(_ => Random.Shared.Next()).Take(Math.Max(0, del));
        foreach (var i in set) mask &= ~(1 << i);
        return mask;
    }

    SailRt? FindSail(int chrIdx, string node, int group) =>
        ships.FirstOrDefault(x => x.Index == chrIdx)?.Sails.FirstOrDefault(sl => sl.Group == group && sl.Node.Equals(node, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ script interface
    void RegisterSails(Interp i)
    {
        i.EntityHandlers["Sail"] = (e, fmt, d) => OnSail(d);
        // native int RandomHole2Sail(int chrIdx, string reyName, int groupNum, int maxHole, int holeData, int addHoleQ): holeData with addHoleQ more random holes
        i.HostFunctions["RandomHole2Sail"] = a =>
        {
            int mask = AddHoles(Conv.ToInt(Arg(a, 4)), Math.Clamp(Conv.ToInt(Arg(a, 3)), 0, 16), Conv.ToInt(Arg(a, 5)));
            if (FindSail(Conv.ToInt(Arg(a, 0)), Conv.ToStr(Arg(a, 1)), Conv.ToInt(Arg(a, 2))) is { } sl) SetHoles(sl, mask);
            return V.Int(mask);
        };
        // native int DeleteOneSailHole(int chrIdx, string groupName, string reyName, int holeMask, int deleteHoleQ): repair takes holes away
        i.HostFunctions["DeleteOneSailHole"] = a =>
        {
            var sl = FindSail(Conv.ToInt(Arg(a, 0)), Conv.ToStr(Arg(a, 2)), Conv.ToInt(Arg(a, 1)));
            int mask = RemoveHoles(Conv.ToInt(Arg(a, 3)), sl?.MaxHoles ?? 16, Conv.ToInt(Arg(a, 4)));
            if (sl != null) SetHoles(sl, mask);
            return V.Int(mask);
        };
        // native float funcGetSailSpeed(int holeQ, int holeMax, float sailPow): the share of the speed lost by the holes of one sail (INTERFACE/ship.c sums it as "speed decreased")
        i.HostFunctions["funcGetSailSpeed"] = a =>
        {
            int q = Conv.ToInt(Arg(a, 0)), max = Conv.ToInt(Arg(a, 1));
            return V.Float(max > 0 ? Conv.ToFloat(Arg(a, 2)) * Ini.HoleDepend * q / max : 0f);
        };
    }

    static V Arg(V[] a, int i) => i < a.Length ? a[i] : V.Void;

    /// <summary>MSG_SAIL_SCRIPT_PROCESSING "RandomSailsDmg" (MakeSailDmg, storms): random holes in random sails until the damage is spent, each asked from the script.</summary>
    V OnSail(V[] d)
    {
        if (d.Length < 4 || Conv.ToInt(d[0]) != MSG_SAIL_SCRIPT_PROCESSING || !Conv.ToStr(d[1]).Equals("RandomSailsDmg", StringComparison.OrdinalIgnoreCase)) return V.Int(1);
        int chr = Conv.ToInt(d[2]);
        float dmg = Conv.ToFloat(d[3]);
        var s = ships.FirstOrDefault(x => x.Index == chr);
        if (s == null) return V.Int(0);
        int idle = 0, holes = 0;
        for (int guard = 0; dmg > 0.0001f && guard < 60; guard++)
        {
            var live = s.Sails.Where(x => !x.Gone).ToList();
            if (live.Count == 0) break;
            var sl = live[Random.Shared.Next(live.Count)];
            int mask = sl.HoleCount < sl.MaxHoles ? AddHoles(sl.Holes, sl.MaxHoles, 1) : sl.Holes;
            int hc = System.Numerics.BitOperations.PopCount((uint)mask);
            float r = Conv.ToFloat(it.FireEvent("evntRandomSailDmg", new List<V> { V.Int(chr), V.Str(sl.Node), V.Int(sl.Group), V.Float(dmg), V.Float(sl.Power), V.Int(hc), V.Int(sl.MaxHoles), V.Int(mask) }));
            if (r > 0f) { SetHoles(sl, mask); holes++; }
            if (r == 0f) { if (++idle > live.Count * 2) break; continue; }
            dmg -= MathF.Abs(r);
        }
        if (holes > 0) GD.Print($"[sea] random sail damage on {s.Chr.Get("id")?.Value}: {holes} new holes, SP {F(s.Chr, "Ship.SP"):0}");
        return V.Int(1);
    }

    /// <summary>A ball went through a sail: the script computes the damage and the holes (DoSailHole, "*" = a ball).</summary>
    void SailHole(ShipRt s, SailRt sl, int shooter, Vector3 p, string mastName = "*")
    {
        if (mastName == "*") it.FireEvent("ShpSailDamage", new List<V> { V.Int(s.Index), V.Float(p.X), V.Float(p.Y), V.Float(p.Z) });
        it.FireEvent("DoSailHole", new List<V> { V.Int(shooter), V.Int(s.Index), V.Str(mastName), V.Str(sl.Node), V.Int(sl.Group), V.Int(sl.HoleCount), V.Int(sl.Holes), V.Int(sl.MaxHoles), V.Float(sl.Power) });
        if (s.Chr.Find("Ship.sails." + sl.Node + "." + sl.Group, false) is { } a && mastName == "*") SetHoles(sl, (int)F(a, "hd"));
        if (mastName == "*") GD.Print($"[sea] sail {sl.Node}.{sl.Group} of {s.Chr.Get("id")?.Value} hit: holes {sl.HoleCount}/{sl.MaxHoles}, SP {F(s.Chr, "Ship.SP"):0}");
    }

    /// <summary>A ball touched a mast: the script adds the damage of the ball type (Ship_MastDamage) and returns the new value; at 1 the mast falls.</summary>
    void MastHit(ShipRt s, MastRt m, Vector3 p)
    {
        var sh = s.Chr.Find("Ship", false);
        if (sh == null || m.Down) return;
        float old = F(sh, "Masts." + m.Name);
        float dmg = Conv.ToFloat(it.FireEvent("ShpMastDamage", new List<V> { V.Int(SHIP_MAST_TOUCH_BALL), V.Int(m.Num), V.Float(p.X), V.Float(p.Y), V.Float(p.Z), V.Float(old), V.A(s.Chr) }));
        SetF(sh, "Masts." + m.Name, dmg);
        GD.Print($"[sea] mast {m.Name} of {s.Chr.Get("id")?.Value} hit: damage {old:0.00} -> {dmg:0.00}");
        if (dmg >= 1f) MastFall(s, m);
    }

    void MastFall(ShipRt s, MastRt m)
    {
        if (m.Down) return;
        m.Down = true;
        it.FireEvent("EventMastFall", new List<V> { V.Int(s.Index), V.Str(m.Name), V.Int(1) });
        foreach (var sl in m.Sails) { sl.Gone = true; SailHole(s, sl, -1, Vector3.Zero, m.Name); }
        foreach (var y in m.Yards) s.YardNodes.Remove(y.Node);
        s.Ropes.RemoveAll(r => r.Nodes.Any(n => n == m.Node || m.Node.IsAncestorOf(n)));
        RebuildRopes(s, true);
        GD.Print($"[sea] mast {m.Name} of {s.Chr.Get("id")?.Value} falls, SP {F(s.Chr, "Ship.SP"):0}");
        if (!m.Node.IsInsideTree()) return;
        // the mast tips over the side around its foot, lies down on the water and sinks
        var g = m.Node.GlobalTransform;
        m.Node.GetParent().RemoveChild(m.Node);
        AddChild(m.Node);
        m.Node.GlobalTransform = g;
        var fwd = (s.Node.GlobalBasis * Vector3.Forward) with { Y = 0f };
        if (fwd.LengthSquared() < 1e-4f) fwd = Vector3.Forward;
        if (m.Bowsprit) fwd = (s.Node.GlobalBasis * Vector3.Right) with { Y = 0f };
        fallingMasts.Add(new FallingMast { Node = m.Node, Start = g, Axis = fwd.Normalized() * (Random.Shared.Next(2) == 0 ? 1f : -1f), Vel = 0.05f });
    }

    void TickFallingMasts(float dt)
    {
        for (int i = fallingMasts.Count - 1; i >= 0; i--)
        {
            var f = fallingMasts[i];
            if (!GodotObject.IsInstanceValid(f.Node)) { fallingMasts.RemoveAt(i); continue; }
            f.T += dt;
            if (f.Ang < 1.45f) { f.Vel += 1.7f * MathF.Sin(f.Ang + 0.12f) * dt; f.Ang = Math.Min(1.45f, f.Ang + f.Vel * dt); }
            else f.Sink += dt * (0.4f + f.Sink * 0.3f);
            var b = new Basis(f.Axis, f.Ang) * f.Start.Basis;
            f.Node.GlobalTransform = new Transform3D(b, f.Start.Origin + Vector3.Down * (f.Sink + MathF.Min(1f, f.Ang / 1.45f) * MathF.Max(0f, f.Start.Origin.Y - 0.5f)));
            if (f.T > 16f) { f.Node.QueueFree(); fallingMasts.RemoveAt(i); }
        }
    }

    // ------------------------------------------------------------------ per frame
    static float MoveTo(float v, float target, float step) => v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);

    void TickSails(float dt)
    {
        float windAng = 0f, windSpd = 0f;
        if (it.Globals.TryGetValue("Weather", out var w) && w.Val.Attr is { } wa) { windAng = F(wa, "Wind.Angle"); windSpd = F(wa, "Wind.Speed"); }
        var windW = new Vector3(MathF.Sin(windAng), 0f, -MathF.Cos(windAng));                   // where the wind blows to (Godot)
        float k = 0.55f + 0.45f * Math.Clamp(windSpd / 14f, 0f, 1f);
        var cam = Cam.GlobalPosition;
        float ease = 1f - MathF.Exp(-dt * 1.5f);
        var ini = Ini;
        foreach (var s in ships)
        {
            bool near = s.Node.IsInsideTree() && cam.DistanceSquaredTo(s.Node.GlobalPosition) < 900f * 900f;
            // yards: braced round to the wind (relative wind angle, at most MAXTURNANGL)
            float rel = windAng - s.Ay;
            while (rel > MathF.PI) rel -= MathF.Tau;
            while (rel < -MathF.PI) rel += MathF.Tau;
            s.YardYaw = MoveTo(s.YardYaw, Math.Clamp(rel, -ini.MaxTurn, ini.MaxTurn), 0.3f * dt);
            if (near && MathF.Abs(s.YardYaw - s.YardSet) > 0.003f)
            {
                s.YardSet = s.YardYaw;
                foreach (var m in s.Masts)
                    if (!m.Down) foreach (var y in m.Yards) y.Node.Transform = new Transform3D(new Basis(y.Axis, -s.YardSet) * y.Rest.Basis, y.Rest.Origin);
            }
            s.RopeTimer += dt;
            if (near && s.RopeTimer > 0.25f && MathF.Abs(s.YardSet - s.RopeYaw) > 0.01f) { s.RopeTimer = 0f; s.RopeYaw = s.YardSet; RebuildRopes(s, false); }
            float turnMul = 1f + (ini.TurnMul - 1f) * MathF.Abs(s.YardSet) / Math.Max(0.01f, ini.MaxTurn);
            foreach (var sl in s.Sails)
            {
                if (sl.Gone) continue;
                float target = s.Sail >= 0.99f ? 0f : s.Sail >= 0.49f ? (sl.ForeAft ? 0f : 0.5f) : 1f;
                sl.Roll = MoveTo(sl.Roll, target, ini.RollSpeed * dt);
                if (near)
                {
                    float dd = (sl.Host.GlobalBasis * sl.N).Normalized().Dot(windW);
                    float b, fl;
                    if (sl.ForeAft)
                    {
                        float m = MathF.Abs(dd);
                        b = sl.Depth * k * (dd < 0f ? -1f : 1f) * Math.Max(m, 0.35f);
                        fl = sl.Depth * 0.18f * (1f - m);
                    }
                    else
                    {
                        float df = dd * sl.Fwd;                                     // > 0: the wind fills the sail from aft
                        df = df < -0.2f ? Math.Max(df, -0.35f) : Math.Max(df, 0.25f);
                        b = sl.Depth * k * df * sl.Fwd * (sl.Host == sl.Mast.Node ? 1f : turnMul);
                        fl = sl.Depth * 0.18f * (1f - MathF.Abs(dd));
                    }
                    sl.Bulge += (b - sl.Bulge) * ease;
                    sl.Flap += (fl - sl.Flap) * ease;
                }
                if (MathF.Abs(sl.Roll - sl.SetRoll) > 0.002f) { sl.SetRoll = sl.Roll; sl.Mat.SetShaderParameter("roll", sl.Roll); }
                if (MathF.Abs(sl.Bulge - sl.SetBulge) > 0.01f) { sl.SetBulge = sl.Bulge; sl.Mat.SetShaderParameter("bulge", sl.Bulge); }
                if (MathF.Abs(sl.Flap - sl.SetFlap) > 0.01f) { sl.SetFlap = sl.Flap; sl.Mat.SetShaderParameter("flap", sl.Flap); }
            }
        }
        TickFallingMasts(dt);
    }

    // ------------------------------------------------------------------ balls in the rigging
    static Vector3 ToGodot(Vector3 e) => new(e.X, e.Y, -e.Z);

    /// <summary>A ball inside the rigging box of a ship this frame (engine coordinates from -> to): masts it touches, sails it passes through.</summary>
    void RiggingPass(ShipRt s, Ball b, Vector3 from)
    {
        if (!s.Node.IsInsideTree()) return;
        Vector3 p0 = ToGodot(from), p1 = ToGodot(b.P);
        for (int i = 0; i < s.Masts.Count; i++)
        {
            var m = s.Masts[i];
            int key = s.Index * 1000 + i;
            if (m.Down || b.SailHit.Contains(key) || !m.Node.IsInsideTree()) continue;
            var gt = m.Node.GlobalTransform;
            if (SegSegDist(p0, p1, gt.Origin, gt * new Vector3(0f, m.Top, 0f)) > 0.9f) continue;
            b.SailHit.Add(key);
            SetBallType(b);
            MastHit(s, m, b.P);
        }
        for (int i = 0; i < s.Sails.Count; i++)
        {
            var sl = s.Sails[i];
            int key = s.Index * 1000 + 500 + i;
            if (sl.Gone || sl.Roll > 0.85f || b.SailHit.Contains(key)) continue;
            var gt = sl.Host.GlobalTransform;
            float k = 1f - sl.Roll;
            Vector3 a0 = gt * sl.C[0], a1 = gt * sl.C[1], a2, a3;
            if (sl.Tri) { a2 = gt * ((sl.C[0] + sl.C[1]) * 0.5f).Lerp(sl.C[2], k); a3 = a2; }
            else { a2 = gt * sl.C[1].Lerp(sl.C[2], k); a3 = gt * sl.C[0].Lerp(sl.C[3], k); }
            if (!SegTri(p0, p1, a0, a1, a2) && (sl.Tri || !SegTri(p0, p1, a0, a2, a3))) continue;
            b.SailHit.Add(key);
            SetBallType(b);
            SailHole(s, sl, b.Owner, b.P);
        }
    }

    static bool SegTri(Vector3 p0, Vector3 p1, Vector3 a, Vector3 b, Vector3 c)
    {
        var d = p1 - p0;
        Vector3 e1 = b - a, e2 = c - a, pv = d.Cross(e2);
        float det = e1.Dot(pv);
        if (MathF.Abs(det) < 1e-8f) return false;
        float inv = 1f / det;
        var tv = p0 - a;
        float u = tv.Dot(pv) * inv;
        if (u < 0f || u > 1f) return false;
        var qv = tv.Cross(e1);
        float v = d.Dot(qv) * inv;
        if (v < 0f || u + v > 1f) return false;
        float t = e2.Dot(qv) * inv;
        return t >= 0f && t <= 1f;
    }

    static float SegSegDist(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = d1.Dot(d1), e = d2.Dot(d2), f = d2.Dot(r);
        float s, t;
        if (a < 1e-8f && e < 1e-8f) return r.Length();
        if (a < 1e-8f) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            float c = d1.Dot(r);
            if (e < 1e-8f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                float bb = d1.Dot(d2), den = a * e - bb * bb;
                s = den > 1e-8f ? Math.Clamp((bb * f - c * e) / den, 0f, 1f) : 0f;
                t = (bb * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((bb - c) / a, 0f, 1f); }
            }
        }
        return ((p1 + d1 * s) - (p2 + d2 * t)).Length();
    }

    // ------------------------------------------------------------------ test hooks
    /// <summary>--mastfall@N (--mastfall=me@N: of the player's ship): the main mast of the nearest other ship takes a ball with 0.95 damage already on it; --sailholes@N: eight balls through its sails.</summary>
    public void RigTest(string what)
    {
        var p = ships.FirstOrDefault(x => x.IsPlayer);
        bool me = what.EndsWith("=me");
        var o = me ? p : ships.Where(x => !x.IsPlayer && !x.Dying).OrderBy(x => p == null ? 0f : Dist(p, x)).FirstOrDefault() ?? p;
        if (o == null) { GD.Print("[sea] rig test: no ship"); return; }
        if (what.StartsWith("shipfire")) { FireTest(o); return; }
        if (what.StartsWith("shipboom")) { it.CallTop("Ship_Serious_Boom", V.Float(o.X), V.Float(o.Height * 0.5f), V.Float(o.Z)); return; }       // explosion: blast + debris + blast_inv
        if (what.StartsWith("mastfall"))
        {
            var m = o.Masts.Where(x => !x.Down && !x.Bowsprit).OrderByDescending(x => x.Top).FirstOrDefault();
            if (m == null) return;
            SetF(o.Chr, "Ship.Masts." + m.Name, 0.95f);
            var at = m.Node.GlobalTransform * new Vector3(0f, m.Top * 0.4f, 0f);
            MastHit(o, m, new Vector3(at.X, at.Y, -at.Z));
        }
        else
        {
            var live = o.Sails.Where(x => !x.Gone).ToList();
            for (int i = 0; i < 8 && live.Count > 0; i++)
            {
                var sl = live[Random.Shared.Next(live.Count)];
                var at = sl.Host.GlobalTransform * ((sl.C[0] + sl.C[2]) * 0.5f);
                SailHole(o, sl, me ? -1 : p?.Index ?? -1, new Vector3(at.X, at.Y, -at.Z));
            }
        }
    }
}
