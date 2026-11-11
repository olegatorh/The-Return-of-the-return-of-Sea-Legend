using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Formats;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Water effects of the sea around the ships: the hull foam (class Seafoam, libs/sea_foam), the sinking vortex with floating wreckage (class SINKEFFECT,
/// libs/sink_effect) and the floating goods of a sunk ship (class AISeaGoods, libs/sea_ai/ai_sea_goods.cpp). The wave function of SeaWaves is also evaluated
/// inside the shaders of these effects, so they lie on the water the sea mesh draws.
/// </summary>
public partial class SeaView
{
    readonly Random fxRnd = new();

    // the sea shader's wave function (OutdoorEnv.cs) for the effects that must follow the water surface
    const string WaveGlsl = @"
uniform float sea_t;
uniform float wave_k[6];
uniform float wave_w[6];
uniform float wave_a[6];
uniform vec2 wave_dir[6];
float sea_h(vec2 p) { float h = 0.0; for (int i = 0; i < 6; i++) h += wave_a[i] * sin(wave_k[i] * dot(wave_dir[i], p) - wave_w[i] * sea_t); return h; }
";

    static void SetWaves(ShaderMaterial m)
    {
        m.SetShaderParameter("sea_t", SeaWaves.Time);
        m.SetShaderParameter("wave_k", (float[])SeaWaves.K.Clone()); m.SetShaderParameter("wave_w", (float[])SeaWaves.W.Clone());
        m.SetShaderParameter("wave_a", (float[])SeaWaves.A.Clone()); m.SetShaderParameter("wave_dir", (Vector2[])SeaWaves.Dir.Clone());
    }

    Entity? fxEntity(string cls) => it.Entities.FirstOrDefault(e => !e.Deleted && e.Class.Equals(cls, StringComparison.OrdinalIgnoreCase));

    float Rand(float r) => (float)fxRnd.NextDouble() * r;
    float RandCentered(float r) => Rand(r) - r * 0.5f;

    void RegisterFx(Interp i)
    {
        // MSG_SHIP_DELETE 50301 "la" (Ship_OnDead of shipDead.c, sent when the script declares a ship dead): the foam of the hull stops, the ship's place gets the vortex
        i.EntityHandlers["Seafoam"] = (e, fmt, d) =>
        {
            if (d.Length > 1 && Conv.ToInt(d[0]) == 50301 && d[1].Attr is { } c && ShipOf(c) is { } s) FoamOff(s);
            return V.Int(0);
        };
        i.EntityHandlers["SINKEFFECT"] = (e, fmt, d) =>
        {
            if (d.Length > 1 && Conv.ToInt(d[0]) == 50301 && d[1].Attr is { } c && ShipOf(c) is { } s) AddSink(new Vector3(s.X, 0f, -s.Z), s.Length * 0.5f);
            return V.Int(0);
        };
        i.EntityHandlers["AISeaGoods"] = (e, fmt, d) => V.Int(0);
        RegisterBlots(i);
    }

    void TickFx(float dt)
    {
        TickFoam(dt);
        TickSinks(dt);
        TickGoods(dt);
        TickSharks(dt);
        TickBlots(dt);
        TickSplashes(dt);
    }

    void ClearFx()
    {
        foreach (var f in foams.Values) FoamFree(f);
        foams.Clear(); foamOff.Clear();
        foreach (var s in sinks) s.Free();
        sinks.Clear();
        foreach (var g in goods) g.Node?.QueueFree();
        goods.Clear();
        seaGoodsEnt = null;
        ClearSharks();
        ClearBlots();
        ClearSplashes();
    }

    // ================================================================== Seafoam (libs/sea_foam)
    // Per ship: two particle emitters at the bow (systems "seafoam" and "seafoam2" of particles.ini, a third "seafoam_front" in a storm) and two foam strips
    // (TCarcass: 13 levels along the hull x 3 points across) that start at the waterline of both sides, widen towards the stern, fade with the speed and scroll
    // along the hull; the texture is seafoam_2.tga.
    const int TraceStepsZ = 13, MeasurePoints = 3, ProfileBins = 48;
    const float StartFadeSpeed = 5f, MinDrawableSpeed = 0.5f;

    sealed class FoamRt
    {
        public ShipRt Ship = null!;
        public MeshInstance3D[] Strips = new MeshInstance3D[2];
        public ShaderMaterial Mat = null!;
        public float U, V;
        public int[] Bow = new int[3];
        public bool Emitting;
        public float BowZ, BowOldY, FrontT;
        public int BowSound;
    }

    readonly Dictionary<ShipRt, FoamRt> foams = new();
    readonly HashSet<ShipRt> foamOff = new();
    static Shader? foamShader;
    const string FoamShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
uniform sampler2D tex : source_color, repeat_enable, filter_linear_mipmap;
uniform float speed_a;
uniform vec2 scroll;
uniform vec3 tint;
uniform float lum_alpha;
" + WaveGlsl + @"
void vertex() {
    vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    w.y = sea_h(w.xz) + 0.06;
    VERTEX = (inverse(MODEL_MATRIX) * vec4(w, 1.0)).xyz;
}
void fragment() {
    vec4 c = texture(tex, UV + vec2(scroll.x, -scroll.y));
    float a = mix(c.a, c.r, lum_alpha);
    ALBEDO = tint * c.rgb;
    ALPHA = clamp(speed_a * COLOR.a * a, 0.0, 1.0);
}";

    void FoamOff(ShipRt s)
    {
        foamOff.Add(s);
        if (foams.Remove(s, out var f)) FoamFree(f);
    }

    void FoamFree(FoamRt f)
    {
        foreach (var m in f.Strips) if (m != null && GodotObject.IsInstanceValid(m)) m.QueueFree();
        if (ParticleService.Instance is { } ps) { foreach (var id in f.Bow) if (id != 0) ps.Delete(id, true); }
    }

    /// <summary>Half width of the hull at the waterline along its length (engine z from the stern to the bow): the vertices near y = 0 of the hull model.</summary>
    static (float zMin, float zMax, float[] half) HullProfile(Node3D? hull, float length)
    {
        var half = new float[ProfileBins];
        float zMin = -length * 0.5f, zMax = length * 0.5f;
        if (hull == null) return (zMin, zMax, half);
        bool any = false; float lo = float.MaxValue, hi = float.MinValue;
        var verts = new List<Vector3>();
        foreach (var mi in hull.GetChildren().OfType<MeshInstance3D>())
        {
            if (mi.Mesh == null || mi.Mesh.GetSurfaceCount() == 0 || mi.Visible == false) continue;
            var arr = mi.Mesh.SurfaceGetArrays(0);
            if (arr[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.Nil) continue;
            foreach (var v0 in arr[(int)Mesh.ArrayType.Vertex].AsVector3Array())
            {
                var v = mi.Transform * v0;
                verts.Add(v);
                lo = Math.Min(lo, -v.Z); hi = Math.Max(hi, -v.Z); any = true;
            }
        }
        if (!any) return (zMin, zMax, half);
        zMin = lo; zMax = hi;
        foreach (var v in verts)
        {
            if (v.Y < -1.2f || v.Y > 0.8f) continue;
            int b = Math.Clamp((int)((-v.Z - zMin) / Math.Max(0.1f, zMax - zMin) * (ProfileBins - 1) + 0.5f), 0, ProfileBins - 1);
            half[b] = Math.Max(half[b], MathF.Abs(v.X));
        }
        // gaps (no vertex at the waterline): neighbours; the tips close to the centre line
        for (int pass = 0; pass < 2; pass++)
            for (int b = 0; b < ProfileBins; b++)
                if (half[b] <= 0.01f)
                {
                    float l = 0f, r = 0f; int nl = 0, nr = 0;
                    for (int k = b - 1; k >= 0 && nl == 0; k--) if (half[k] > 0.01f) { l = half[k]; nl = 1; }
                    for (int k = b + 1; k < ProfileBins && nr == 0; k++) if (half[k] > 0.01f) { r = half[k]; nr = 1; }
                    half[b] = nl + nr == 0 ? 0f : (l + r) / (nl + nr);
                }
        return (zMin, zMax, half);
    }

    static float ProfileAt((float zMin, float zMax, float[] half) p, float z)
    {
        float f = (z - p.zMin) / Math.Max(0.1f, p.zMax - p.zMin) * (ProfileBins - 1);
        int a = Math.Clamp((int)MathF.Floor(f), 0, ProfileBins - 1), b = Math.Min(a + 1, ProfileBins - 1);
        return Mathf.Lerp(p.half[a], p.half[b], Math.Clamp(f - a, 0f, 1f));
    }

    /// <summary>One foam strip along the hull (TCarcass): <paramref name="side"/> -1 = port, +1 = starboard.</summary>
    static ArrayMesh BuildFoamStrip(ShipRt s, (float zMin, float zMax, float[] half) prof, int side)
    {
        float L = Math.Max(4f, prof.zMax - prof.zMin), halfZ = L * 0.5f, zc = (prof.zMin + prof.zMax) * 0.5f;
        float wideK = MathF.Sqrt(Math.Max(1f, s.Height) / 17f), d = wideK * 1.4f, kx = side * wideK;
        float zStep = 0.15f * L / TraceStepsZ;
        var zs = new float[TraceStepsZ];
        float curZ = zc + halfZ;
        for (int l = 0; l < TraceStepsZ; l++, curZ -= zStep * (1f + 8f * MathF.Sin(MathF.PI * 0.5f * (zc + halfZ - curZ) / halfZ))) zs[l] = curZ;
        float lengthK = 0.2f * MathF.Abs(zs[0] - zs[TraceStepsZ - 1]);
        var pos = new Vector3[TraceStepsZ * MeasurePoints]; var uv = new Vector2[pos.Length]; var col = new Color[pos.Length];
        for (int l = 0; l < TraceStepsZ; l++)
        {
            float levelK = l / (float)(TraceStepsZ - 1);
            float x0 = side * Math.Max(0.3f, ProfileAt(prof, zs[l]));
            for (int m = 0; m < MeasurePoints; m++)
            {
                float t = m * MathF.PI / (MeasurePoints - 1);
                float dx = (MathF.Cos(t) + 1f) * 0.5f * d * kx;
                int i = l * MeasurePoints + m;
                pos[i] = new Vector3(x0 + (1f + 3f * levelK) * dx, 0.02f, -zs[l]);
                float levelA = l <= 3 ? l / 3f : 1f - levelK;
                float measureK = m == 0 ? 1f : 0f;
                col[i] = new Color(1f, 1f, 1f, MathF.Sqrt(levelA) * (1f - measureK));
                float u = lengthK * (1f - levelK);
                uv[i] = new Vector2(u, 1f - m / (float)(MeasurePoints - 1) + u * 0.5f);
            }
        }
        var idx = new List<int>();
        for (int l = 0; l < TraceStepsZ - 1; l++)
            for (int m = 0; m < MeasurePoints - 1; m++)
            {
                int a = MeasurePoints * l + m, b = MeasurePoints * (l + 1) + m;
                idx.AddRange(new[] { a, b + 1, b, a, a + 1, b + 1 });
            }
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = pos; arrays[(int)Mesh.ArrayType.TexUV] = uv; arrays[(int)Mesh.ArrayType.Color] = col; arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(new Vector3(-30, -10, -L), new Vector3(60, 20, 2 * L));
        return mesh;
    }

    FoamRt MakeFoam(ShipRt s)
    {
        var f = new FoamRt { Ship = s };
        var prof = HullProfile(s.Node.GetNodeOrNull<Node3D>(s.TypeName), s.Length);
        foamShader ??= new Shader { Code = FoamShader };
        var tex = GameAssets.Texture("seafoam_2.tga");
        f.Mat = new ShaderMaterial { Shader = foamShader };
        f.Mat.SetShaderParameter("tex", tex);
        f.Mat.SetShaderParameter("tint", new Vector3(0.9f, 0.94f, 0.97f));
        f.Mat.SetShaderParameter("lum_alpha", tex != null && GameAssets.HasAlpha("seafoam_2.tga") ? 0f : 1f);
        for (int side = 0; side < 2; side++)
        {
            var m = new MeshInstance3D { Mesh = BuildFoamStrip(s, prof, side == 0 ? -1 : 1), MaterialOverride = f.Mat, Name = "Foam" + side, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            s.Node.AddChild(m);
            f.Strips[side] = m;
        }
        f.BowZ = prof.zMax;
        GD.Print($"[sea] foam for {s.Chr.Get("id")?.Value}: hull z {prof.zMin:0.0}..{prof.zMax:0.0}, half width bow/mid/stern {ProfileAt(prof, prof.zMax * 0.9f):0.0}/{ProfileAt(prof, 0f):0.0}/{ProfileAt(prof, prof.zMin * 0.9f):0.0}");
        return f;
    }

    void TickFoam(float dt)
    {
        var ent = fxEntity("Seafoam");
        if (ent == null) { if (foams.Count > 0 || foamOff.Count > 0) { foreach (var f in foams.Values) FoamFree(f); foams.Clear(); foamOff.Clear(); } return; }
        bool storm = string.Equals(ent.Root.Get("storm")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        var ps = ParticleService.Instance;
        foreach (var gone in foams.Keys.Where(k => !ships.Contains(k) || k.Dying).ToList()) { FoamFree(foams[gone]); foams.Remove(gone); }
        foreach (var s in ships)
        {
            if (s.Dying || foamOff.Contains(s) || s.Node == null || !GodotObject.IsInstanceValid(s.Node)) continue;
            if (!foams.TryGetValue(s, out var f)) foams[s] = f = MakeFoam(s);
            float speed = Math.Max(0f, s.Speed), speedA = speed > StartFadeSpeed ? 1f : speed / StartFadeSpeed;
            f.U += 2.4f * speed / Math.Max(4f, s.Length) * dt; f.V += 0.1f * speed * dt;     // U_SPEED_K * speed / boxsize.z, V_SPEED_K * speed per millisecond
            bool draw = speed >= MinDrawableSpeed;
            foreach (var m in f.Strips) m.Visible = draw;
            f.Mat.SetShaderParameter("speed_a", speedA);
            f.Mat.SetShaderParameter("scroll", new Vector2(f.U, f.V));
            SetWaves(f.Mat);
            if (ps == null) continue;
            bool want = draw && (s.Node.GlobalPosition - Cam.GlobalPosition).LengthSquared() < 700f * 700f;
            if (want && !f.Emitting)
            {
                float z = f.BowZ * 0.92f + 0.1f + 0.25f * Math.Max(speed, 4f);
                f.Bow[0] = ps.Create("seafoam", Vector3.Zero, Vector3.Up, 0f, s.Node, new Vector3(-0.35f, -0.2f, -z), true);
                f.Bow[1] = ps.Create("seafoam2", Vector3.Zero, Vector3.Up, 0f, s.Node, new Vector3(0.35f, -0.2f, -z), true);
                f.Emitting = true;
            }
            else if (!want && f.Emitting)
            {
                foreach (var id in f.Bow) if (id != 0) ps.Delete(id, true);
                f.Bow = new int[3]; f.Emitting = false;
            }
            // seafoam.cpp RealizeShipFoam_Particles: in a storm the front emitter works while the bow rises by 5e-4 m/ms or more (doSplash); then the sound "ship_bow" plays
            // at the bow, again when it has finished. (The original keeps the previous height in one static for all ships; here it is the last frame's height of the ship.)
            if (storm && f.Emitting && SoundView.Instance is { } snd)
            {
                float z = f.BowZ * 0.92f + 0.1f + 0.25f * Math.Max(speed, 4f);
                var bow = s.Node.GlobalTransform * new Vector3(0f, -0.2f, -z);
                bool rising = dt > 0f && (bow.Y - f.BowOldY) / (dt * 1000f) >= 5e-4f;
                f.BowOldY = bow.Y;
                // the front emitter (the tall spray) runs only while the bow rises over a wave (doSplash), not all the time
                if (rising) f.FrontT = 0.7f; else f.FrontT -= dt;
                if (f.FrontT > 0f && f.Bow[2] == 0) f.Bow[2] = ps.Create("seafoam_front", Vector3.Zero, new Vector3(0f, 1f, -0.15f), 0f, s.Node, new Vector3(0f, -0.2f, -z), true);
                else if (f.FrontT <= 0f && f.Bow[2] != 0) { ps.Delete(f.Bow[2], true); f.Bow[2] = 0; }
                if (rising && !snd.VoiceAlive(f.BowSound)) f.BowSound = snd.Play3D("ship_bow", bow.X, SeaWaves.Height(bow.X, -bow.Z), -bow.Z);
            }
        }
    }

    // ================================================================== SINKEFFECT (libs/sink_effect)
    // A sunk ship leaves a vortex: up to 25 grids of foam (a cap of 6 x 6 m that rises, rolls out and fades in 3 s, explos.tga) appear at random places around the
    // wreck and are renewed for two minutes; 25 pieces of wreckage (particles\palka01..04) rise from below, float up to 30 s and sink again.
    const int SinkSplashes = 25, SinkFlotsams = 25, SinkGrid = 11;
    const float SinkGridLength = 6f, SinkSplashFade = 1f, SinkMoveY = -0.3f, SinkTime = 2f, SinkTotal = 122f;

    sealed class SplashRt { public MeshInstance3D Node = null!; public ShaderMaterial Mat = null!; public float T; public float X, Z; public bool Active; public float Wait; }
    sealed class FlotsamRt { public Node3D? Node; public float Wait, X, Y, Z, VX, VY, VZ, Ang, Turn, FloatT, MaxFloat; public int State = -1; }       // State: -1 off, 0 rise, 1 float, 2 sink

    sealed class SinkRt
    {
        public Vector3 C; public float R, T;
        public readonly SplashRt[] Splashes = new SplashRt[SinkSplashes];
        public readonly FlotsamRt[] Flotsams = new FlotsamRt[SinkFlotsams];
        public Node3D Root = null!;
        public void Free() { if (GodotObject.IsInstanceValid(Root)) Root.QueueFree(); }
    }

    readonly List<SinkRt> sinks = new();
    static Shader? sinkShader;
    static ArrayMesh? sinkGrid;
    static readonly List<Node3D> flotsamTemplates = new();
    const string SinkShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
uniform sampler2D tex : source_color, filter_linear_mipmap;
uniform float age;
uniform float grow;
uniform float base_y;
uniform vec3 tint;
" + WaveGlsl + @"
void vertex() {
    float edge = COLOR.r;                                   // 0 on the border of the grid
    float rho = length(VERTEX.xz);
    float k = min(age / 1.0, 1.0);
    float second = ((6.0 - rho) / 6.0) * (1.0 + abs(sin(-3.14159265 * age / 1.0 + 6.2831853 * rho / 6.0)));
    float hgt = ((6.0 - rho) / 6.0) * (0.5 * (k * second));
    vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    w.y = sea_h(w.xz) - 0.3 + edge * grow * hgt;
    VERTEX = (inverse(MODEL_MATRIX) * vec4(w, 1.0)).xyz;
}
void fragment() {
    vec4 c = texture(tex, UV);
    ALBEDO = tint * c.rgb;
    ALPHA = clamp(c.a * (3.0 - age) / 3.0 * 0.85, 0.0, 1.0);
}";

    void AddSink(Vector3 godotPos, float radius)
    {
        var sk = new SinkRt { C = godotPos, R = Math.Max(2f, radius), Root = new Node3D { Name = "SinkEffect" } };
        AddChild(sk.Root);
        if (sinkShader == null)
        {
            sinkShader = new Shader { Code = SinkShader };
            var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
            float step = SinkGridLength / SinkGrid, half = SinkGridLength * 0.5f;
            Vector3 P(int x, int z) => new(step * x - half, 0f, step * z - half);
            Vector2 UV(int x, int z) => new(x / (float)SinkGrid, z / (float)SinkGrid);
            Color C(int x, int z) => new(x > 0 && z > 0 && x < SinkGrid - 1 && z < SinkGrid - 1 ? 1f : 0f, 0, 0, 1);
            void V(int x, int z) { st.SetColor(C(x, z)); st.SetUV(UV(x, z)); st.AddVertex(P(x, z)); }
            for (int z = 0; z < SinkGrid - 1; z++)
                for (int x = 0; x < SinkGrid - 1; x++)
                {
                    V(x, z); V(x + 1, z + 1); V(x, z + 1);
                    V(x, z); V(x + 1, z); V(x + 1, z + 1);
                }
            sinkGrid = st.Commit();
            sinkGrid.CustomAabb = new Aabb(new Vector3(-4, -3, -4), new Vector3(8, 8, 8));
        }
        var tex = GameAssets.Texture("explos.tga");
        for (int i = 0; i < SinkSplashes; i++)
        {
            var mat = new ShaderMaterial { Shader = sinkShader };
            mat.SetShaderParameter("tex", tex); mat.SetShaderParameter("tint", new Vector3(0.9f, 0.95f, 1f));
            var node = new MeshInstance3D { Mesh = sinkGrid, MaterialOverride = mat, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            sk.Root.AddChild(node);
            sk.Splashes[i] = new SplashRt { Node = node, Mat = mat, Wait = Rand(SinkTime) };
        }
        if (flotsamTemplates.Count == 0)
            for (int k = 1; k <= 4; k++)
            {
                string f = GameAssets.Resource("MODELS\\particles\\palka0" + k + ".gm");
                try { flotsamTemplates.Add(ModelFactory.Build(GmFile.Parse(File.ReadAllBytes(f)), "palka0" + k, null, null)); } catch { }
            }
        for (int i = 0; i < SinkFlotsams; i++) sk.Flotsams[i] = new FlotsamRt { Wait = Rand(SinkTime) };
        sinks.Add(sk);
        GD.Print($"[sea] sink effect at ({godotPos.X:0},{-godotPos.Z:0}) radius {sk.R:0}");
    }

    void TickSinks(float dt)
    {
        for (int n = sinks.Count - 1; n >= 0; n--)
        {
            var sk = sinks[n];
            sk.T += dt;
            if (sk.T > SinkTotal) { sk.Free(); sinks.RemoveAt(n); continue; }
            foreach (var sp in sk.Splashes)
            {
                if (sp.Active)
                {
                    sp.T += dt;
                    if (sp.T > 3f * SinkSplashFade) { sp.Active = false; sp.Node.Visible = false; sp.Wait = Rand(SinkTime); continue; }
                    sp.Node.Position = new Vector3(sp.X, 0f, sp.Z);
                    sp.Mat.SetShaderParameter("age", sp.T);
                    SetWaves(sp.Mat);
                }
                else if (sp.Wait > 0f) sp.Wait -= dt;
                else
                {
                    sp.Active = true; sp.T = 0f;
                    sp.X = sk.C.X + RandCentered(2f * sk.R); sp.Z = sk.C.Z + RandCentered(2f * sk.R);
                    sp.Mat.SetShaderParameter("grow", Rand(1.5f) + 0.1f);
                    sp.Node.Position = new Vector3(sp.X, 0f, sp.Z); sp.Node.Visible = true;
                }
            }
            foreach (var fl in sk.Flotsams)
            {
                if (fl.Wait > 0f) { fl.Wait -= dt; continue; }
                if (fl.State < 0) { StartFlotsam(sk, fl); continue; }
                fl.X += fl.VX * dt; fl.Z += fl.VZ * dt;
                float sea = SeaWaves.Height(fl.X, fl.Z);
                switch (fl.State)
                {
                    case 0: fl.Y += fl.VY * dt; if (fl.Y > sea + 0.3f) fl.State = 1; break;
                    case 1:
                        fl.FloatT += dt; fl.Ang = (fl.Ang + fl.Turn * dt) % MathF.Tau;
                        if (fl.FloatT > fl.MaxFloat) fl.State = 2;
                        break;
                    default:
                        fl.Y -= fl.VY * dt;
                        if (fl.Y <= sea - 2f) { fl.State = -1; fl.Node?.QueueFree(); fl.Node = null; }
                        break;
                }
                if (fl.Node != null) fl.Node.Position = new Vector3(fl.X, fl.State == 1 ? sea + 0.3f : fl.Y, fl.Z);
                if (fl.Node != null) fl.Node.Rotation = new Vector3(0f, fl.Ang, 0f);
            }
        }
    }

    void StartFlotsam(SinkRt sk, FlotsamRt fl)
    {
        if (flotsamTemplates.Count == 0) { fl.Wait = 1e6f; return; }
        fl.X = sk.C.X + RandCentered(sk.R); fl.Z = sk.C.Z + RandCentered(sk.R);
        fl.Y = SeaWaves.Height(fl.X, fl.Z) - 2f;
        fl.VX = RandCentered(1f) * 0.8f; fl.VZ = RandCentered(1f) * 0.8f; fl.VY = Math.Max(0.05f, Rand(1f)) * 1.2f;
        fl.Ang = Rand(MathF.Tau); fl.Turn = RandCentered(1f) * 3f;
        fl.FloatT = 0f; fl.MaxFloat = Math.Max(1f, Rand(30f)); fl.State = 0;
        fl.Node = (Node3D)flotsamTemplates[fxRnd.Next(flotsamTemplates.Count)].Duplicate();
        sk.Root.AddChild(fl.Node);
    }

    // ================================================================== AISeaGoods (libs/sea_ai/ai_sea_goods.cpp)
    // The script (AISeaGoods_ShipDead) puts barrels and boxes of the sunk ship's cargo on the water: it fills Good, Pos.x/z, CharIndex, Time, Quantity, Model and
    // writes Add. The goods bob on the waves, sink after Time seconds and vanish 20 s later. Once a second every ship near a good (within box length x
    // DistanceMultiply) raises ShpEatSwimGood "llsl" (ship character, owner character, good, quantity); the script puts the goods into the hold.
    sealed class GoodRt { public string Model = "", Good = ""; public int CharIndex, Quantity; public float Time, X, Z; public Node3D? Node; }

    readonly List<GoodRt> goods = new();
    Entity? seaGoodsEnt;
    float goodsCheckT, goodsLookT;
    readonly Dictionary<string, Node3D?> goodModels = new(StringComparer.OrdinalIgnoreCase);

    void TickGoods(float dt)
    {
        goodsLookT -= dt;
        if ((seaGoodsEnt == null || seaGoodsEnt.Deleted) && goodsLookT <= 0f) { goodsLookT = 0.5f; seaGoodsEnt = fxEntity("AISeaGoods"); }
        if (seaGoodsEnt != null && !seaGoodsEnt.Deleted)
        {
            var add = seaGoodsEnt.Root.GetOrCreate("Add");
            var root = seaGoodsEnt.Root;
            if (add.OnWrite == null) add.OnWrite = _ => AddGood(root);
        }
        if (goods.Count == 0) return;
        float distMul = seaGoodsEnt != null ? F(seaGoodsEnt.Root, "DistanceMultiply", 0.5f) : 0.5f;
        bool deleteAnyway = seaGoodsEnt == null || F(seaGoodsEnt.Root, "DeleteGoodAnyway", 1f) != 0f;
        goodsCheckT -= dt;
        bool check = goodsCheckT <= 0f;
        if (check) goodsCheckT = 1f;
        for (int i = goods.Count - 1; i >= 0; i--)
        {
            var g = goods[i];
            g.Time -= dt;
            float y = SeaWaves.Height(g.X, -g.Z);
            if (g.Time <= 0f)
            {
                y -= MathF.Abs(g.Time) * 0.05f;
                if (g.Time < -20f) { g.Node?.QueueFree(); goods.RemoveAt(i); continue; }
            }
            if (g.Node != null)
            {
                var sl = SeaWaves.Slope(g.X, -g.Z);
                var n = new Vector3(-sl.X, 1f, -sl.Y).Normalized();
                var basis = new Basis(Quaternion.FromEuler(Vector3.Zero)) ;
                var ax = Vector3.Up.Cross(n);
                if (ax.LengthSquared() > 1e-8f) basis = new Basis(ax.Normalized(), MathF.Acos(Math.Clamp(n.Y, -1f, 1f)));
                g.Node.Transform = new Transform3D(basis, new Vector3(g.X, y, -g.Z));
            }
            if (!check) continue;
            foreach (var s in ships)
            {
                if (s.Dying || s.Index == g.CharIndex) continue;
                float dist = MathF.Sqrt((s.X - g.X) * (s.X - g.X) + (s.Z - g.Z) * (s.Z - g.Z));
                if (dist > s.Length * distMul) continue;
                bool taken = Conv.ToInt(it.FireEvent("ShpEatSwimGood", new List<V> { V.Int(s.Index), V.Int(g.CharIndex), V.Str(g.Good), V.Int(g.Quantity) })) != 0;
                if (taken || deleteAnyway) { g.Node?.QueueFree(); goods.RemoveAt(i); break; }
            }
        }
    }

    /// <summary>AISeaGoods.Add was written: the item described by the attributes set before it joins the list.</summary>
    void AddGood(Attr root)
    {
        var g = new GoodRt
        {
            Model = root.Get("Model")?.Value ?? "", Good = root.Get("Good")?.Value ?? "", CharIndex = (int)F(root, "CharIndex", -1), Quantity = (int)F(root, "Quantity", 1),
            Time = F(root, "Time"), X = F(root, "Pos.x"), Z = F(root, "Pos.z"),
        };
        string path = (root.Get("ModelsPath")?.Value ?? "SwimGoods").Trim('\\', '/');
        if (!goodModels.TryGetValue(g.Model, out var tpl))
        {
            string f = GameAssets.Resource("MODELS\\" + path + "\\" + g.Model + ".gm");
            try { tpl = File.Exists(f) ? ModelFactory.Build(GmFile.Parse(File.ReadAllBytes(f)), g.Model, null, null) : null; } catch (Exception ex) { GD.PrintErr("[sea] " + f + ": " + ex.Message); tpl = null; }
            goodModels[g.Model] = tpl;
        }
        if (tpl != null) { g.Node = (Node3D)tpl.Duplicate(); AddChild(g.Node); g.Node.Position = new Vector3(g.X, 0f, -g.Z); }
        goods.Add(g);
        GD.Print($"[sea] swim good {g.Good} x{g.Quantity} ({g.Model}) at ({g.X:0},{g.Z:0}) for {g.Time:0} s, owner {g.CharIndex}");
    }
}
