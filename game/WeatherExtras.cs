using System;
using System.Collections.Generic;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Weather extras on top of WeatherFx (all driven by the script's weather record and the game clock):
/// * the baked light of land locations and islands (.col vertex colours of the hour the scene was loaded at) is re-tinted by the ratio of the current record's
///   light (ambient + sun) to the one at load, so the day cycle shows on terrain, houses and islands without swapping lighting folders;
/// * rain splashes on the ground, on the player's deck, rain drops running on the lens, the rainbow of the rain record (rain.cpp);
/// * stars and a moon (phase from the game date) in the night sky, hidden by the clouds of the sky texture and by the fog sphere.
/// </summary>
public partial class WeatherFx
{
    /// <summary>Set by Main: a random point of the player's ship deck in Godot world coordinates (the ship's walk path), or null.</summary>
    public Func<Random, Vector3?>? DeckPoint;

    bool wxHas, wxSea;
    Attr? wxWeather;
    readonly Random wxRnd = new(11);

    // ------------------------------------------------------------------ re-tint of the baked light
    Color tintRef = Colors.White, tintCur = Colors.White, tintApplied = Colors.White, tintTarget = Colors.White;
    bool tintHasRef;
    readonly List<StandardMaterial3D> tintMats = new();
    readonly HashSet<StandardMaterial3D> tintSet = new();
    float tintScan;
    public static Color LastTint = Colors.White;

    static Color LightOf(Attr w)
    {
        var amb = ArgbToColor(w.Find("Sun.Ambient", false)?.Value, new Color(0.4f, 0.4f, 0.4f));
        var sun = ArgbToColor(w.Find("Sun.Color", false)?.Value, new Color(0.6f, 0.6f, 0.6f));
        float k = MathF.Max(0f, MathF.Sin(F(w, "Sun.HeightAngle", 0.8f))) * 0.9f;
        return new Color(amb.R + sun.R * k, amb.G + sun.G * k, amb.B + sun.B * k);
    }

    void GatherTint()
    {
        var stack = new Stack<Node>();
        stack.Push(GetTree().Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is MeshInstance3D { Mesh: { } mesh } mi)
            {
                int cnt = mesh.GetSurfaceCount();
                for (int s = 0; s < cnt; s++)
                    if (mi.GetActiveMaterial(s) is StandardMaterial3D { VertexColorUseAsAlbedo: true } m && tintSet.Add(m)) { tintMats.Add(m); m.AlbedoColor = tintCur; }
            }
            foreach (var c in n.GetChildren()) stack.Push(c);
        }
    }

    void ResetTint()
    {
        foreach (var m in tintMats) if (GodotObject.IsInstanceValid(m)) m.AlbedoColor = Colors.White;
        tintMats.Clear(); tintSet.Clear(); tintHasRef = false; tintCur = tintApplied = tintTarget = Colors.White; LastTint = Colors.White;
    }

    void TintSync(float dt)
    {
        if (!wxHas || wxWeather == null || System.Environment.GetEnvironmentVariable("SEADOGS_NOTINT") != null) { if (tintHasRef || tintMats.Count > 0) ResetTint(); return; }
        var L = LightOf(wxWeather);
        if (!tintHasRef) { tintRef = L; tintHasRef = true; tintCur = tintApplied = Colors.White; tintScan = 0f; }
        tintScan -= dt;
        if (tintScan <= 0f) { tintScan = 3f; tintMats.RemoveAll(m => !GodotObject.IsInstanceValid(m)); tintSet.RemoveWhere(m => !GodotObject.IsInstanceValid(m)); GatherTint(); }
        tintTarget = new Color(Math.Clamp((L.R + 0.02f) / (tintRef.R + 0.02f), 0.15f, 1.6f), Math.Clamp((L.G + 0.02f) / (tintRef.G + 0.02f), 0.15f, 1.6f), Math.Clamp((L.B + 0.02f) / (tintRef.B + 0.02f), 0.15f, 1.6f));
        tintCur = tintCur.Lerp(tintTarget, 1f - MathF.Exp(-dt / 2.5f));
        LastTint = tintCur;
        if (MathF.Abs(tintCur.R - tintApplied.R) + MathF.Abs(tintCur.G - tintApplied.G) + MathF.Abs(tintCur.B - tintApplied.B) > 0.006f)
        {
            tintApplied = tintCur;
            foreach (var m in tintMats) if (GodotObject.IsInstanceValid(m)) m.AlbedoColor = tintCur;
        }
    }

    /// <summary>Text for the log: the tint the scene is gliding to and a pass/fail word for the numeric checks of the weathercycle tests.</summary>
    string TintLog(int hour)
    {
        float avg = (tintTarget.R + tintTarget.G + tintTarget.B) / 3f;
        string chk = hour >= 23 || hour <= 3 ? (avg < 0.6f ? " grade-check night-dark" : " grade-check NIGHT-NOT-DARK") : hour >= 9 && hour <= 15 ? (avg > 0.5f ? " grade-check day-light" : " grade-check DAY-NOT-LIGHT") : "";
        return $" tint={tintTarget.R:0.00},{tintTarget.G:0.00},{tintTarget.B:0.00} mats={tintMats.Count}{chk}";
    }

    // ------------------------------------------------------------------ splashes on the ground and the deck
    const int SplashN = 140;
    MultiMeshInstance3D? splashNode;
    MultiMesh? splashMM;
    readonly float[] sAge = new float[SplashN];
    readonly bool[] sLive = new bool[SplashN];
    readonly Vector3[] sPos = new Vector3[SplashN];
    float sAcc;
    static readonly string SplashShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never;
uniform vec3 col = vec3(0.85, 0.9, 0.95);
varying float age;
void vertex() { age = INSTANCE_CUSTOM.x; }
void fragment() {
    float d = length(UV - 0.5) * 2.0;
    float ring = smoothstep(0.16, 0.0, abs(d - (0.12 + age * 0.88)));
    float crown = smoothstep(0.3, 0.0, d) * clamp(1.0 - age * 3.0, 0.0, 1.0);
    float a = (ring * 0.75 + crown * 0.6) * (1.0 - age) * 0.85;
    if (a < 0.02) discard;
    ALBEDO = col; ALPHA = a;
}";

    void SplashSync(Camera3D? cam, float dt, int drops, bool raining)
    {
        if (!raining || cam == null || !cam.IsInsideTree()) { if (splashNode != null) { splashNode.Visible = false; for (int i = 0; i < SplashN; i++) sLive[i] = false; } return; }
        if (splashNode == null)
        {
            splashMM = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = new PlaneMesh { Size = Vector2.One }, InstanceCount = SplashN };
            splashNode = new MultiMeshInstance3D { Multimesh = splashMM, MaterialOverride = new ShaderMaterial { Shader = new Shader { Code = SplashShader }, RenderPriority = 2 }, Name = "RainSplashes", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 4096f };
            AddChild(splashNode);
        }
        splashNode.Visible = true;
        splashNode.GlobalPosition = Vector3.Zero;
        for (int i = 0; i < SplashN; i++) if (sLive[i]) { sAge[i] += dt / 0.55f; if (sAge[i] >= 1f) sLive[i] = false; }
        sAcc += Math.Clamp(drops / 45f, 12f, 220f) * rainBlend * dt;
        var space = cam.GetWorld3D()?.DirectSpaceState;
        int tries = 0;
        while (sAcc >= 1f && tries++ < 12)
        {
            sAcc -= 1f;
            int slot = -1;
            for (int i = 0; i < SplashN; i++) if (!sLive[i]) { slot = i; break; }
            if (slot < 0) break;
            Vector3? p = null;
            if (wxSea && DeckPoint != null && wxRnd.NextSingle() < 0.35f) p = DeckPoint(wxRnd);
            if (p == null && space != null)
            {
                float a = wxRnd.NextSingle() * MathF.Tau, r = MathF.Sqrt(wxRnd.NextSingle()) * 13f;
                var from = cam.GlobalPosition + new Vector3(MathF.Cos(a) * r, 30f, MathF.Sin(a) * r);
                var res = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 80f));
                if (res.Count > 0) p = (Vector3)res["position"];
            }
            if (p == null) continue;
            sLive[slot] = true; sAge[slot] = 0f; sPos[slot] = p.Value;
        }
        for (int i = 0; i < SplashN; i++)
        {
            if (sLive[i]) { splashMM!.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(Vector3.One * 0.8f), sPos[i] + Vector3.Up * 0.04f)); splashMM.SetInstanceCustomData(i, new Color(sAge[i], 0, 0, 0)); }
            else splashMM!.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(Vector3.One * 0.0001f), Vector3.Zero));
        }
    }

    // ------------------------------------------------------------------ rain drops running on the lens
    ColorRect? lens;
    ShaderMaterial? lensMat;
    static readonly string LensShader = @"
shader_type canvas_item;
uniform sampler2D scr : hint_screen_texture, filter_linear, repeat_disable;
uniform float t = 0.0;
uniform float amount = 0.3;
uniform float aspect = 1.78;
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
vec3 layer(vec2 uv, float scale, float seed) {
    vec2 p = vec2(uv.x * aspect, uv.y) * scale;
    vec2 id = floor(p); vec2 f = fract(p) - 0.5;
    float r1 = hash(id + seed), r2 = hash(id + seed + 3.7), r3 = hash(id + seed + 9.1);
    if (r1 > amount) return vec3(0.0);
    float life = fract(t * (0.05 + 0.05 * r2) + r3);
    float grow = smoothstep(0.0, 0.2, life);
    float slide = pow(smoothstep(0.35, 1.0, life), 1.5) * 0.5;
    vec2 c = vec2((r2 - 0.5) * 0.4, 0.22 - slide);
    vec2 d = f - c;
    d.y *= 1.0 - 0.35 * smoothstep(0.35, 1.0, life);
    float rad = (0.09 + 0.07 * r3) * grow * (1.0 - smoothstep(0.88, 1.0, life));
    float m = smoothstep(rad, rad * 0.55, length(d));
    return vec3(d / max(rad, 0.001) * m, m);
}
void fragment() {
    vec2 uv = SCREEN_UV;
    vec3 a = layer(uv, 6.0, 0.0), b = layer(uv, 11.0, 11.0);
    vec2 off = -(a.xy * a.z + b.xy * b.z) * 0.05;
    float m = max(a.z, b.z);
    vec3 c = texture(scr, uv + off).rgb;
    c += m * 0.05;
    COLOR = vec4(c, 1.0);
}";

    void LensSync(float amount)
    {
        if (amount < 0.02f) { if (lens != null) lens.Visible = false; return; }
        if (lens == null)
        {
            lensMat = new ShaderMaterial { Shader = new Shader { Code = LensShader } };
            lens = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore, Material = lensMat, Color = Colors.White, Name = "RainLens" };
            lens.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            var layer = new CanvasLayer { Layer = -1, Name = "RainLensLayer" };
            layer.AddChild(lens);
            AddChild(layer);
        }
        lens.Visible = true;
        var vs = GetViewport().GetVisibleRect().Size;
        lensMat!.SetShaderParameter("t", time);
        lensMat.SetShaderParameter("amount", amount);
        lensMat.SetShaderParameter("aspect", vs.X / Math.Max(vs.Y, 1f));
    }

    // ------------------------------------------------------------------ rainbow (rain.cpp: a 1600 m rect 1900 m ahead of the world +z axis at height 350, additive)
    MeshInstance3D? rainbow;
    ShaderMaterial? rainbowMat;
    string rainbowTex = "";
    void RainbowSync(Entity? rainEnt, Camera3D? cam)
    {
        bool on = rainEnt != null && cam != null && rainBlend > 0.3f && F(rainEnt.Root, "Rainbow.Enable", 0f) > 0.5f && System.Environment.GetEnvironmentVariable("SEADOGS_NORAINBOW") == null;
        if (!on) { if (rainbow != null) rainbow.Visible = false; return; }
        string tex = rainEnt!.Root.Find("Rainbow.Texture", false)?.Value ?? "weather\\rainbow\\rainbow.tga";
        if (rainbow == null)
        {
            sunQuad ??= new QuadMesh { Size = Vector2.One };
            sunShader ??= new Shader { Code = SunShaderCode };
            rainbowMat = new ShaderMaterial { Shader = sunShader, RenderPriority = 90 };
            rainbow = new MeshInstance3D { Mesh = sunQuad, MaterialOverride = rainbowMat, Name = "Rainbow", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 16384f };
            AddChild(rainbow);
        }
        if (tex != rainbowTex) { rainbowTex = tex; rainbowMat!.SetShaderParameter("tex", GameAssets.Texture(tex)); }
        rainbow.Visible = true;
        var cp = cam!.GlobalPosition;
        rainbow.GlobalPosition = new Vector3(cp.X + 0.32f * 1900f, 350f, cp.Z - 0.72f * 1900f);       // engine z is Godot -z
        rainbow.Scale = new Vector3(1600f, 1600f, 1f);
        float k = Math.Clamp((rainBlend - 0.3f) / 0.7f, 0f, 1f) * 0.55f;
        rainbowMat!.SetShaderParameter("color", new Color(k, k, k));
    }

    // ------------------------------------------------------------------ stars and the moon
    MeshInstance3D? nightDome;
    ShaderMaterial? nightMat;
    static readonly string NightShader = @"
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, fog_disabled;
uniform float night = 1.0;
uniform float t = 0.0;
uniform vec3 moon_dir = vec3(0.3, 0.5, -0.5);
uniform float phase = 0.5;
uniform float moon_k = 1.0;
uniform float fog_h = 100.0;
uniform sampler2D sky_tex : filter_linear, repeat_disable;
uniform sampler2D up_tex : filter_linear, repeat_disable;
uniform bool has_sky = false;
uniform bool has_up = false;
varying vec3 dir;
float hash13(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }
void vertex() { dir = normalize(VERTEX); }
// luminance of the sky texture in direction d (the clouds hide the stars)
float cloud(vec3 d) {
    const float RR = 1500.0, TOP = 1300.0, BOT = -150.0;
    float m = max(max(abs(d.x), abs(d.z)), 0.0001);
    float tt = RR / m;
    float y = d.y * tt;
    if (y > TOP) {
        if (!has_up) return 0.0;
        vec3 p = d * (TOP / d.y);
        vec3 c = textureLod(up_tex, vec2(p.x / (2.0 * RR) + 0.5, p.z / (2.0 * RR) + 0.5), 2.0).rgb;
        return dot(c, vec3(0.333));
    }
    if (!has_sky) return 0.0;
    float lv = (TOP - y) / (TOP - BOT);
    float wall; float u;
    if (abs(d.z) >= abs(d.x)) {
        if (d.z > 0.0) { wall = 0.0; u = (d.x * tt + RR) / (2.0 * RR); }
        else { wall = 2.0; u = (RR - d.x * tt) / (2.0 * RR); }
    } else {
        if (d.x > 0.0) { wall = 1.0; u = (RR - d.z * tt) / (2.0 * RR); }
        else { wall = 3.0; u = (d.z * tt + RR) / (2.0 * RR); }
    }
    float v = (wall + clamp(lv, 0.01, 0.99)) * 0.25;
    return dot(textureLod(sky_tex, vec2(clamp(u, 0.002, 0.998), v), 2.0).rgb, vec3(0.333));
}
void fragment() {
    vec3 d = normalize(dir);
    if (d.y < 0.0) discard;
    float lum = cloud(d);
    float fogv = smoothstep(0.0, 1.0, d.y * 2000.0 / max(fog_h, 1.0));          // the fog sphere hides the low sky
    float vis = (1.0 - smoothstep(0.07, 0.3, lum)) * fogv * smoothstep(0.0, 0.1, d.y);
    vec3 col = vec3(0.0);
    vec3 p = d * 170.0; vec3 id = floor(p); vec3 f = fract(p) - 0.5;
    float r = hash13(id);
    if (r > 0.975) {
        vec3 c = (vec3(hash13(id + 1.3), hash13(id + 2.7), hash13(id + 4.1)) - 0.5) * 0.5;
        float s = smoothstep(0.25 + 0.4 * hash13(id + 9.0), 0.0, length(f - c));
        float tw = 0.7 + 0.3 * sin(t * (2.0 + r * 6.0) + r * 50.0);
        vec3 tint = mix(vec3(0.75, 0.85, 1.0), vec3(1.0, 0.9, 0.75), hash13(id + 5.5));
        col += tint * s * tw * (0.8 + (r - 0.975) * 40.0);
    }
    col *= vis * night;
    vec3 m = normalize(moon_dir);
    vec3 up0 = abs(m.y) < 0.95 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 right = normalize(cross(up0, m)); vec3 upv = cross(m, right);
    float ang = 0.04;
    if (dot(d, m) > 0.0) {
        vec2 q = vec2(dot(d, right), dot(d, upv)) / ang;
        float rr = length(q);
        float a = phase * 6.2832;
        float illum = 0.5 - 0.5 * cos(a);
        float mvis = (1.0 - 0.85 * smoothstep(0.08, 0.4, lum)) * fogv * moon_k;
        vec3 mc = vec3(0.5, 0.56, 0.72) * 0.3 * exp(-rr * 0.55) * (0.25 + 0.75 * illum);
        if (rr < 1.0) {
            vec3 n = vec3(q, sqrt(1.0 - rr * rr));
            vec3 L = vec3(sin(a), 0.0, -cos(a));
            float lit = smoothstep(-0.03, 0.06, dot(n, L));
            float tex = 0.8 + 0.2 * sin(q.x * 13.0 + 1.0) * sin(q.y * 11.0 + 2.0) * sin((q.x + q.y) * 7.0);
            mc += vec3(1.0, 0.97, 0.88) * lit * tex * 1.15 + vec3(0.03, 0.04, 0.06);
        }
        col += mc * mvis;
    }
    ALBEDO = col; ALPHA = 1.0;
}";

    static float MoonPhase(Interp it)
    {
        try
        {
            if (it.Globals.TryGetValue("Environment", out var e) && e.Val.Attr is { } ea)
            {
                int y = (int)F(ea, "date.year", 1665f), mo = (int)F(ea, "date.month", 1f), d = (int)F(ea, "date.day", 1f);
                double days = (new DateTime(Math.Clamp(y, 1, 9999), Math.Clamp(mo, 1, 12), Math.Clamp(d, 1, 28)).AddDays(Math.Max(0, d - 28)) - new DateTime(2000, 1, 6, 18, 14, 0)).TotalDays;
                double ph = days / 29.530588853 % 1.0;
                return (float)(ph < 0 ? ph + 1.0 : ph);
            }
        }
        catch { }
        return 0.5f;
    }

    void NightSync(Interp it, Camera3D? cam)
    {
        float t = 12f;
        if (it.Globals.TryGetValue("Environment", out var e) && e.Val.Attr is { } ea) t = F(ea, "time", 12f);
        float night = t >= 12f ? Mathf.SmoothStep(19.5f, 22f, t) : 1f - Mathf.SmoothStep(4.5f, 6.5f, t);
        if (!wxHas || cam == null || night < 0.01f || System.Environment.GetEnvironmentVariable("SEADOGS_NOSTARS") != null || OutdoorEnv.SkySide == null) { if (nightDome != null) nightDome.Visible = false; return; }
        if (nightDome == null)
        {
            nightMat = new ShaderMaterial { Shader = new Shader { Code = NightShader }, RenderPriority = -7 };
            nightDome = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1250f, Height = 2500f, RadialSegments = 48, Rings = 24 }, MaterialOverride = nightMat, Name = "NightSky", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = new Aabb(new Vector3(-1300, -1300, -1300), new Vector3(2600, 2600, 2600)) };
            AddChild(nightDome);
        }
        nightDome.Visible = true;
        nightDome.GlobalPosition = cam.GlobalPosition;
        float phase = MoonPhase(it);
        // the moon rises at 18 h at full moon and 6 h at new moon, stays up 12 h
        float rise = 18f + (phase - 0.5f) * 24f;
        float f = (((t - rise) % 24f) + 24f) % 24f / 12f;
        float elev = f <= 1f ? 0.12f + 0.95f * MathF.Sin(MathF.PI * f) : -0.3f;
        float az = MathF.PI * (1f - Math.Clamp(f, 0f, 1f));
        var mdir = new Vector3(MathF.Cos(az) * MathF.Cos(elev), MathF.Sin(elev), -0.55f * MathF.Cos(elev)).Normalized();
        if (System.Environment.GetEnvironmentVariable("SEADOGS_MOONDIR") is "cam") mdir = (-cam.GlobalBasis.Z + Vector3.Up * 0.15f).Normalized(); else if (System.Environment.GetEnvironmentVariable("SEADOGS_MOONDIR") is { } md) { var q = md.Split(','); mdir = new Vector3(float.Parse(q[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(q[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(q[2], System.Globalization.CultureInfo.InvariantCulture)).Normalized(); }
        nightMat!.SetShaderParameter("night", night);
        nightMat.SetShaderParameter("t", time);
        nightMat.SetShaderParameter("moon_dir", mdir);
        nightMat.SetShaderParameter("phase", phase);
        nightMat.SetShaderParameter("moon_k", night * (f <= 1f || System.Environment.GetEnvironmentVariable("SEADOGS_MOONDIR") != null ? 1f : 0f));
        nightMat.SetShaderParameter("fog_h", OutdoorEnv.FogDomeHeight);
        nightMat.SetShaderParameter("has_sky", true); nightMat.SetShaderParameter("sky_tex", OutdoorEnv.SkySide);
        nightMat.SetShaderParameter("has_up", OutdoorEnv.SkyUp != null);
        if (OutdoorEnv.SkyUp != null) nightMat.SetShaderParameter("up_tex", OutdoorEnv.SkyUp);
    }
}
