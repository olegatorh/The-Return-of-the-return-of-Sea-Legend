using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Formats;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// One particle system description of RESOURCE\INI\PARTICLES\*.ini (the engine class "particles", PARTICLES.dll). Only the keys the DLL actually reads are used
/// (its string table: particles_num, emissiontime(_rand), emitdelta, emit_radius, lifetime, speed(_deviation), ddeviation, gravity, weight(_deviation),
/// spin(_deviation), windeffect, color, texture, technique, frames_u/v, nonstopemit, randomdirection, inversecolor and the key_* curves); keys the files carry
/// but the DLL never reads (pscale, psize_min/max, emitradius, initial_speed, ptime, double) are ignored like in the original.
/// Units as read from the data (the C++ source is not available): times in milliseconds, the key curves are (value, time) pairs interpolated linearly over the
/// particle age; key_psize = sprite size in metres; velocity = direction * speed * key_pspeed in metres per millisecond; weight * gravity is a downward
/// acceleration in m/ms per second (fountain 0.005 = 5 m/s^2, cancloud -0.00025 = smoke slowly rising); windeffect * key_windeffect * windpower likewise
/// accelerates along Particles.winddirection; spin in rad/ms.
/// </summary>
public sealed class ParticleDef
{
    public string Name = "";
    public int Num = 16;
    public float EmitInterval;        // ms between two particles (emissiontime or emitdelta); 0 = all at once
    public float EmitRand, EmitRadius, Life = 1000f, Speed, SpeedDev, DDev = 0.1f, Gravity = 1f, Weight, WeightDev, Spin, SpinDev, Wind;
    public bool NonStop, RandomDir, Inverse, Additive;
    public int FramesU = 1, FramesV = 1;
    public Color Color = Colors.White;
    public string Texture = "";
    public (float t, float v)[] Alpha = Array.Empty<(float, float)>(), Size = Array.Empty<(float, float)>(), PSpeed = Array.Empty<(float, float)>(),
        KSpin = Array.Empty<(float, float)>(), KWind = Array.Empty<(float, float)>();

    public static float Key((float t, float v)[] k, float t, float def = 1f)
    {
        if (k.Length == 0) return def;
        if (t <= k[0].t) return k[0].v;
        for (int i = 1; i < k.Length; i++)
            if (t <= k[i].t) { var a = k[i - 1]; var b = k[i]; return b.t > a.t ? a.v + (b.v - a.v) * (t - a.t) / (b.t - a.t) : b.v; }
        return k[^1].v;
    }

    public static ParticleDef From(IniFile.Section s)
    {
        float Fl(string key, float def) => s.Get(key) is { } v && float.TryParse(v.Split(';')[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;
        (float, float)[] Keys(string key) => s.All(key).Select(v =>
        {
            var p = v.Split(';')[0].Split(',');
            float val = p.Length > 0 && float.TryParse(p[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : 0f;
            float tm = p.Length > 1 && float.TryParse(p[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : 0f;
            return (tm, val);
        }).OrderBy(x => x.tm).ToArray();
        var d = new ParticleDef { Name = s.Name.Trim() };
        d.Num = Math.Clamp((int)Fl("particles_num", 16), 1, 1024);
        d.EmitInterval = Fl("emissiontime", 0f); if (d.EmitInterval <= 0f) d.EmitInterval = Fl("emitdelta", 0f);
        d.EmitRand = Fl("emissiontime_rand", 0f);
        d.EmitRadius = Fl("emit_radius", 0f);
        d.Life = Math.Max(10f, Fl("lifetime", 1000f));
        d.Speed = Fl("speed", 0f); d.SpeedDev = Fl("speed_deviation", 0f);
        d.DDev = Fl("ddeviation", 0.1f); d.Gravity = Fl("gravity", 1f);
        d.Weight = Fl("weight", 0f); d.WeightDev = Fl("weight_deviation", 0f);
        d.Spin = Fl("spin", 0f); d.SpinDev = Fl("spin_deviation", 0f);
        d.Wind = Fl("windeffect", 0f);
        d.NonStop = s.Has("nonstopemit"); d.RandomDir = s.Has("randomdirection");
        string tech = (s.Get("technique") ?? "particles").Trim().ToLowerInvariant();
        d.Inverse = s.Has("inversecolor") || tech == "particles_inv";
        d.Additive = tech is "particlesfire" or "particlesartifact";
        d.Texture = (s.Get("texture") ?? "").Split(';')[0].Trim();
        // deviation: the flame texture fire3.tga is stored pre-darkened (rgb ~ alpha); blended with the plain Particles technique (ship_fire, fort_fire, fire_mix)
        // it gives a dull brown smear on the deck, so flames are always drawn additively (like the ParticlesFire technique of the torches) and glow
        if (d.Texture.StartsWith("fire", StringComparison.OrdinalIgnoreCase)) d.Additive = true;
        d.FramesU = Math.Max(1, (int)Fl("frames_u", 1)); d.FramesV = Math.Max(1, (int)Fl("frames_v", 1));
        if (s.Get("color") is { } c && uint.TryParse(c.Trim(), out var argb))
            d.Color = new Color(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, ((argb >> 24) & 255) / 255f);
        d.Alpha = Keys("key_alpha"); d.Size = Keys("key_psize"); d.PSpeed = Keys("key_pspeed"); d.KSpin = Keys("key_spin"); d.KWind = Keys("key_windeffect");
        return d;
    }
}

/// <summary>
/// The engine class "particles" (PROGRAM\particles.c): PS_CREATE 10000 / PS_CREATEX 10002 (name, position, angles or a direction vector, emitter lifetime in seconds,
/// 0 = endless) return a system id; PS_DELETE 10007, PS_CLEARALL 10006, PS_PAUSEALL 10010, PS_SETVECTORS 10001; the _RIC variants 10008/10009 behave like
/// the plain ones. Systems of the sea (ship fires) are created here too through <see cref="Create"/>. Particles are simulated on the CPU in world space and drawn
/// as camera-facing quads of one MultiMesh per system, with the three blend techniques of MODULES\Techniques\Particles\Particles.sha:
/// Particles (alpha blend), Particles_Inv (complement of the texture colour: dark smoke from the white smoke texture) and ParticlesFire (additive).
/// Also the class "blast" (LM_SETPOINT 1600 / LM_SETPOINTANDANGLES 1601): wooden debris of [geo] (MODELS\particles\palka01..05) thrown out of an explosion.
/// Positions: engine x, y, z -> Godot (x, y, -z).
/// </summary>
public partial class ParticleService : Node3D
{
    public static ParticleService? Instance;

    const int PS_CREATE = 10000, PS_SETVECTORS = 10001, PS_CREATEX = 10002, PS_CREATEXX = 10003, PS_CLEARALL = 10006, PS_DELETE = 10007, PS_CREATE_RIC = 10008,
        PS_CREATEX_RIC = 10009, PS_PAUSEALL = 10010;

    readonly Dictionary<string, ParticleDef> defs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, Sys> systems = new();
    readonly Dictionary<string, Material> materials = new();
    readonly List<Debris> debris = new();
    readonly List<Node3D> debrisTemplates = new();
    bool debrisLoaded;
    int nextId = 1;
    Entity? entity;
    readonly Random rnd = new();
    static QuadMesh? quad;
    public int Alive => systems.Count;

    struct P { public Vector3 Pos, Vel, Dir; public float Age, SpeedK, Weight, Spin, Angle; public int Frame; }

    sealed class Sys
    {
        public int Id;
        public ParticleDef D = null!;
        public Vector3 Pos, Dir = Vector3.Up;           // Godot space
        public Node3D? Attach; public Vector3 Local;      // emitter that moves with a node (ship fire places)
        public float Age, EmitLife, EmitAcc;               // ms; EmitLife 0 = endless
        public int Emitted;
        public bool Stopped, Paused, Owned;                 // Owned: created by the engine (ship fires), PS_CLEARALL leaves it to its owner
        public P[] Parts = null!;
        public int Count;
        public MultiMeshInstance3D Node = null!;
        public float[] Buf = null!;
    }

    sealed class Debris { public Node3D Node = null!; public Vector3 Vel, Rot; public float Age; }

    public ParticleService()
    {
        Name = "Particles";
        Instance = this;
    }

    public void Register(Interp it)
    {
        LoadDefs();
        it.EntityHandlers["particles"] = (e, fmt, d) => { entity = e; return OnMessage(d); };
        it.EntityHandlers["blast"] = (e, fmt, d) =>
        {
            int m = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
            if ((m == 1600 || m == 1601) && d.Length > 3) Blast(new Vector3(Conv.ToFloat(d[1]), Conv.ToFloat(d[2]), -Conv.ToFloat(d[3])));
            return V.Int(1);
        };
    }

    void LoadDefs()
    {
        string dir = GameAssets.Resource("INI\\PARTICLES");
        if (!Directory.Exists(dir)) { GD.Print("[particles] no INI\\PARTICLES folder"); return; }
        foreach (var f in Directory.GetFiles(dir, "*.ini").OrderBy(x => x))
        {
            IniFile ini;
            try { ini = IniFile.Load(f); } catch (Exception ex) { GD.PrintErr("[particles] " + f + ": " + ex.Message); continue; }
            foreach (var s in ini.Sections.Values)
                if (s.Has("texture") && !defs.ContainsKey(s.Name.Trim())) defs[s.Name.Trim()] = ParticleDef.From(s);
        }
        GD.Print($"[particles] {defs.Count} particle systems from INI\\PARTICLES: {string.Join(",", defs.Keys)}");
    }

    public bool Has(string name) => defs.ContainsKey(name);

    V OnMessage(V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        float Fl(int i) => i < d.Length ? Conv.ToFloat(d[i]) : 0f;
        switch (msg)
        {
            case PS_CREATE: case PS_CREATE_RIC:                  // "lsffffffl": name, x, y, z, ax, ay, az, lifetime
            case PS_CREATEX: case PS_CREATEX_RIC: case PS_CREATEXX:   // the same with a direction vector instead of the angles
            {
                if (d.Length < 5) return V.Int(0);
                string name = Conv.ToStr(d[1]);
                var pos = new Vector3(Fl(2), Fl(3), Fl(4));
                Vector3 dir = msg is PS_CREATE or PS_CREATE_RIC ? AnglesToDir(Fl(5), Fl(6)) : new Vector3(Fl(5), Fl(6), Fl(7));
                float life = d.Length > 8 ? Fl(8) : 0f;
                return V.Int(Create(name, pos, dir, life));
            }
            case PS_SETVECTORS:                                  // "lffffff"? id, position, direction
                if (d.Length > 4 && systems.TryGetValue(Conv.ToInt(d[1]), out var sv))
                {
                    sv.Pos = new Vector3(Fl(2), Fl(3), -Fl(4));
                    if (d.Length > 7) { var dv = new Vector3(Fl(5), Fl(6), -Fl(7)); if (dv.LengthSquared() > 1e-6f) sv.Dir = dv.Normalized(); }
                }
                return V.Int(1);
            case PS_DELETE: Delete(Conv.ToInt(d.Length > 1 ? d[1] : V.Int(0)), true); return V.Int(1);
            case PS_CLEARALL: ClearAll(false); return V.Int(1);
            case PS_PAUSEALL: Pause(d.Length > 1 && Conv.ToInt(d[1]) != 0); return V.Int(1);
        }
        return V.Int(0);
    }

    /// <summary>Engine angles (ax = pitch about x, ay = heading) of the forward vector (0, 0, 1) -> engine direction. ax = -1.57 points up.</summary>
    public static Vector3 AnglesToDir(float ax, float ay) => new(MathF.Cos(ax) * MathF.Sin(ay), -MathF.Sin(ax), MathF.Cos(ax) * MathF.Cos(ay));

    /// <summary>Starts a system; <paramref name="pos"/> and <paramref name="dir"/> in engine coordinates, <paramref name="lifeSec"/> = emitter lifetime (0 = until done / endless).</summary>
    public int Create(string name, Vector3 pos, Vector3 dir, float lifeSec, Node3D? attach = null, Vector3 local = default, bool owned = false)
    {
        if (!defs.TryGetValue(name.Trim(), out var def)) { GD.Print("[particles] unknown system '" + name + "'"); return 0; }
        var gd = new Vector3(dir.X, dir.Y, -dir.Z);
        var s = new Sys
        {
            Id = nextId++, D = def, Pos = new Vector3(pos.X, pos.Y, -pos.Z), Dir = gd.LengthSquared() > 1e-6f ? gd.Normalized() : Vector3.Up,
            EmitLife = Math.Max(0f, lifeSec) * 1000f, Attach = attach, Local = local, Owned = owned, Parts = new P[def.Num], Buf = new float[def.Num * 20],
        };
        quad ??= new QuadMesh { Size = Vector2.One };
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = quad, InstanceCount = def.Num, VisibleInstanceCount = 0 };
        s.Node = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = MaterialFor(def), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "ps_" + def.Name };
        AddChild(s.Node);
        systems[s.Id] = s;
        if (def.EmitInterval <= 0f) Emit(s, def.Num);                                    // a burst: everything at once
        if (System.Environment.GetEnvironmentVariable("SEADOGS_PSLOG") != null)
            GD.Print($"[particles] create {def.Name} id {s.Id} at {s.Pos} attach={attach?.Name} local={local} tex={def.Texture} alpha={GameAssets.HasAlpha(def.Texture)} inv={def.Inverse} add={def.Additive}");
        return s.Id;
    }

    /// <summary>Stops emitting; with <paramref name="fade"/> the living particles finish their life, otherwise the system disappears at once.</summary>
    public void Delete(int id, bool fade)
    {
        if (!systems.TryGetValue(id, out var s)) return;
        if (fade) { s.Stopped = true; return; }
        s.Node.QueueFree(); systems.Remove(id);
    }

    public void ClearAll(bool includingOwned)
    {
        foreach (var s in systems.Values.ToList())
            if (includingOwned || !s.Owned) { s.Node.QueueFree(); systems.Remove(s.Id); }
        if (includingOwned) { foreach (var b in debris) b.Node.QueueFree(); debris.Clear(); }
    }

    /// <summary>PS_PAUSEALL: the systems alive now freeze and are hidden (the sea's smoke must not hang over a deck or cabin location); later systems run.</summary>
    void Pause(bool on)
    {
        foreach (var s in systems.Values)
        {
            if (on && !s.Paused) { s.Paused = true; s.Node.Visible = false; }
            else if (!on && s.Paused) { s.Paused = false; s.Node.Visible = true; }
        }
        foreach (var b in debris) b.Node.Visible = !on;
    }

    Material MaterialFor(ParticleDef d)
    {
        string key = d.Texture + "|" + d.Inverse + "|" + d.Additive + "|" + d.FramesU + "x" + d.FramesV;
        if (materials.TryGetValue(key, out var m)) return m;
        var sh = new Shader { Code = d.Additive ? ShaderAdd : ShaderMix };
        var mat = new ShaderMaterial { Shader = sh };
        var tex = GameAssets.Texture(d.Texture);
        mat.SetShaderParameter("tex", tex ?? (Texture2D)SoftDot());
        mat.SetShaderParameter("lum_alpha", tex != null && !GameAssets.HasAlpha(d.Texture));     // watersplash.tga has no alpha: a light splash on black
        mat.SetShaderParameter("inverse", d.Inverse);
        mat.SetShaderParameter("frames", new Vector2(d.FramesU, d.FramesV));
        materials[key] = mat;
        return mat;
    }

    static GradientTexture2D? soft;
    static GradientTexture2D SoftDot() => soft ??= new GradientTexture2D
    {
        Width = 64, Height = 64, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f),
        Gradient = new Gradient { Colors = new[] { new Color(1, 1, 1, 1), new Color(1, 1, 1, 0) }, Offsets = new[] { 0f, 1f } },
    };

    const string ShaderBody = @"
uniform sampler2D tex : source_color, filter_linear_mipmap, repeat_disable;
uniform bool inverse = false;
uniform bool lum_alpha = false;
uniform vec2 frames = vec2(1.0, 1.0);
void vertex() {
    float s = INSTANCE_CUSTOM.x;
    float a = INSTANCE_CUSTOM.y;
    vec2 v = VERTEX.xy * s;
    v = vec2(v.x * cos(a) - v.y * sin(a), v.x * sin(a) + v.y * cos(a));
    vec4 c = VIEW_MATRIX * vec4(MODEL_MATRIX[3].xyz, 1.0);
    VERTEX = c.xyz + vec3(v, 0.0);
    NORMAL = vec3(0.0, 0.0, 1.0);
    float f = INSTANCE_CUSTOM.z;
    UV = (UV + vec2(mod(f, frames.x), floor(f / frames.x))) / frames;
}
void fragment() {
    vec4 t = texture(tex, UV);
    if (lum_alpha) t.a = max(t.r, max(t.g, t.b));
    vec3 rgb = inverse ? (vec3(1.0) - t.rgb) : t.rgb;
    ALBEDO = rgb * COLOR.rgb;
    ALPHA = t.a * COLOR.a;
}
";
    const string ShaderMix = "shader_type spatial;\nrender_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, skip_vertex_transform;\n" + ShaderBody;
    const string ShaderAdd = "shader_type spatial;\nrender_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, skip_vertex_transform, fog_disabled;\n" + ShaderBody;

    float R() => (float)rnd.NextDouble();
    float R2() => (float)rnd.NextDouble() * 2f - 1f;

    void Emit(Sys s, int n)
    {
        var d = s.D;
        var origin = s.Pos;
        if (s.Attach != null)
        {
            if (!GodotObject.IsInstanceValid(s.Attach) || !s.Attach.IsInsideTree()) { s.Stopped = true; return; }
            origin = s.Attach.GlobalTransform * s.Local;
        }
        for (int k = 0; k < n && s.Count < s.Parts.Length; k++)
        {
            if (!d.NonStop && s.Emitted >= d.Num) { s.Stopped = true; return; }
            Vector3 dir;
            if (d.RandomDir) { do dir = new Vector3(R2(), R2(), R2()); while (dir.LengthSquared() > 1f || dir.LengthSquared() < 1e-4f); }
            else dir = s.Dir + new Vector3(R2(), R2(), R2()) * d.DDev;
            dir = dir.LengthSquared() > 1e-8f ? dir.Normalized() : Vector3.Up;
            var off = Vector3.Zero;
            if (d.EmitRadius > 0f) { do off = new Vector3(R2(), R2(), R2()); while (off.LengthSquared() > 1f); off *= d.EmitRadius; }
            s.Parts[s.Count++] = new P
            {
                Pos = origin + off, Dir = dir, Vel = Vector3.Zero, Age = 0f, SpeedK = d.Speed + R2() * d.SpeedDev, Weight = d.Weight + R2() * d.WeightDev,
                Spin = d.Spin + R2() * d.SpinDev, Angle = R() * MathF.Tau, Frame = d.FramesU * d.FramesV > 1 ? rnd.Next(d.FramesU * d.FramesV) : 0,
            };
            s.Emitted++;
        }
    }

    Vector3 WindDir(out float power)
    {
        power = 0f;
        var a = entity?.Root;
        if (a == null) return Vector3.Zero;
        float P(string path, float def) => a.Find(path, false)?.Value is { } v && v.Length > 0 ? Conv.ParseFloatPrefix(v) : def;
        power = P("windpower", 0f);
        var w = new Vector3(P("winddirection.x", 0f), 0f, -P("winddirection.z", 0f));
        return w.LengthSquared() > 1e-6f ? w.Normalized() : Vector3.Zero;
    }

    public override void _Process(double delta)
    {
        float dt = Math.Min((float)delta, 0.1f);
        if (systems.Count > 0) Step(dt);
        if (debris.Count > 0) StepDebris(dt);
    }

    void Step(float dt)
    {
        float ms = dt * 1000f;
        var wind = WindDir(out float wpow);
        var cam = GetViewport()?.GetCamera3D();
        var camPos = cam?.GlobalPosition ?? Vector3.Zero;
        List<int>? dead = null;
        foreach (var s in systems.Values)
        {
            if (s.Paused) continue;
            var d = s.D;
            s.Age += ms;
            if (s.EmitLife > 0f && s.Age >= s.EmitLife) s.Stopped = true;
            if (!s.Stopped && d.EmitInterval > 0f)
            {
                s.EmitAcc += ms;
                int n = 0;
                while (s.EmitAcc >= d.EmitInterval && n < 64) { s.EmitAcc -= d.EmitInterval + (d.EmitRand > 0f ? R() * d.EmitRand : 0f); n++; }
                if (n > 0) Emit(s, n);
            }
            // simulate
            for (int i = 0; i < s.Count; i++)
            {
                ref var p = ref s.Parts[i];
                p.Age += ms;
                if (p.Age >= d.Life) { s.Parts[i] = s.Parts[--s.Count]; i--; continue; }
                p.Vel.Y -= p.Weight * d.Gravity * 1000f * dt;
                if (d.Wind != 0f && wpow != 0f)
                {
                    p.Vel += wind * (d.Wind * ParticleDef.Key(d.KWind, p.Age) * wpow * 1000f * dt);
                    float h = new Vector2(p.Vel.X, p.Vel.Z).Length();
                    if (h > 8f) { p.Vel.X *= 8f / h; p.Vel.Z *= 8f / h; }
                }
                p.Pos += (p.Dir * (p.SpeedK * ParticleDef.Key(d.PSpeed, p.Age) * 1000f) + p.Vel) * dt;
                p.Angle += p.Spin * ParticleDef.Key(d.KSpin, p.Age) * ms;
            }
            if (s.Stopped && s.Count == 0) { (dead ??= new()).Add(s.Id); continue; }
            if (s.Owned && logT > 2f && s.Count > 0) GD.Print($"[particles] {d.Name} id {s.Id}: {s.Count} alive, first at {s.Parts[0].Pos} size {ParticleDef.Key(d.Size, s.Parts[0].Age):0.0} alpha {ParticleDef.Key(d.Alpha, s.Parts[0].Age):0.00} visible={s.Node.Visible}");
            Upload(s, camPos);
        }
        if (dead != null) foreach (var id in dead) { systems[id].Node.QueueFree(); systems.Remove(id); }
        logT = logT > 2f ? 0f : logT + (pslog ? dt : 0f);
    }

    readonly List<(float d, int i)> order = new();
    float logT;
    static readonly bool pslog = System.Environment.GetEnvironmentVariable("SEADOGS_PSLOG") != null;

    void Upload(Sys s, Vector3 camPos)
    {
        var d = s.D;
        order.Clear();
        for (int i = 0; i < s.Count; i++) order.Add((d.Additive ? 0f : -(s.Parts[i].Pos - camPos).LengthSquared(), i));
        if (!d.Additive) order.Sort((a, b) => a.d.CompareTo(b.d));                   // far ones first
        var buf = s.Buf;
        Vector3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue), hi = -lo;
        float maxSize = 0f;
        int o = 0;
        foreach (var (_, i) in order)
        {
            ref var p = ref s.Parts[i];
            float size = ParticleDef.Key(d.Size, p.Age), alpha = Math.Clamp(ParticleDef.Key(d.Alpha, p.Age), 0f, 1f) * d.Color.A;
            buf[o] = 1; buf[o + 1] = 0; buf[o + 2] = 0; buf[o + 3] = p.Pos.X;
            buf[o + 4] = 0; buf[o + 5] = 1; buf[o + 6] = 0; buf[o + 7] = p.Pos.Y;
            buf[o + 8] = 0; buf[o + 9] = 0; buf[o + 10] = 1; buf[o + 11] = p.Pos.Z;
            buf[o + 12] = d.Color.R; buf[o + 13] = d.Color.G; buf[o + 14] = d.Color.B; buf[o + 15] = alpha;
            buf[o + 16] = size; buf[o + 17] = p.Angle; buf[o + 18] = p.Frame; buf[o + 19] = 0;
            o += 20;
            lo = lo.Min(p.Pos); hi = hi.Max(p.Pos); maxSize = Math.Max(maxSize, size);
        }
        var mm = s.Node.Multimesh;
        mm.Buffer = buf;
        mm.VisibleInstanceCount = s.Count;
        if (s.Count > 0) s.Node.CustomAabb = new Aabb(lo - Vector3.One * maxSize, hi - lo + Vector3.One * (2f * maxSize));
    }

    // ------------------------------------------------------------------ class "blast": wooden debris
    void Blast(Vector3 at)
    {
        if (!debrisLoaded)
        {
            debrisLoaded = true;
            for (int k = 1; k <= 5; k++)
            {
                string f = GameAssets.Resource("MODELS\\particles\\palka0" + k + ".gm");
                if (!File.Exists(f)) continue;
                try { debrisTemplates.Add(ModelFactory.Build(GmFile.Parse(File.ReadAllBytes(f)), "palka0" + k, null, null)); }
                catch (Exception ex) { GD.PrintErr("[particles] " + f + ": " + ex.Message); }
            }
        }
        if (debrisTemplates.Count == 0) return;
        int n = 6 + rnd.Next(6);
        for (int k = 0; k < n && debris.Count < 160; k++)
        {
            var node = (Node3D)debrisTemplates[rnd.Next(debrisTemplates.Count)].Duplicate();
            node.Position = at + new Vector3(R2(), R(), R2()) * 0.8f;
            node.Rotation = new Vector3(R() * MathF.Tau, R() * MathF.Tau, R() * MathF.Tau);
            AddChild(node);
            var v = new Vector3(R2(), 0.6f + R(), R2()).Normalized() * (8f + R() * 14f);
            debris.Add(new Debris { Node = node, Vel = v, Rot = new Vector3(R2(), R2(), R2()) * 8f });
        }
    }

    void StepDebris(float dt)
    {
        for (int i = debris.Count - 1; i >= 0; i--)
        {
            var b = debris[i];
            if (!b.Node.Visible) continue;                                    // paused
            b.Age += dt;
            b.Vel.Y -= 9.81f * dt;
            b.Node.Position += b.Vel * dt;
            b.Node.Rotation += b.Rot * dt;
            if (b.Node.Position.Y < -1.5f || b.Age > 8f) { b.Node.QueueFree(); debris.RemoveAt(i); }
        }
    }
}
