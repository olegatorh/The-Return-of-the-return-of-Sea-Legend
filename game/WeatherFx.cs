using System;
using System.Collections.Generic;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Rain and lightning, driven by the script's weather: the "rain" entity (NumDrops, Color, DropLength, Height, Radius, Speed ...) and the "Lightning" entity
/// (texture atlas, bolts added by message 54000 from Lightning_DoIt). The rain is a static mesh of line segments animated entirely in the vertex shader and wrapped around the camera.
/// </summary>
public partial class WeatherFx : Node3D
{
    static readonly string RainShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
uniform vec3 cam;
uniform float t;
uniform float speed;
uniform float height;
uniform float radius;
uniform float len;
uniform vec2 wind;
uniform vec4 color;
uniform float blend;
varying float fade;
void vertex() {
    vec3 b = COLOR.rgb;
    float y = fract(b.y - t * speed / height);
    vec2 slant = wind * 0.15;                                   // metres sideways per metre of fall
    vec2 xz = (b.xz - 0.5) * 2.0 * radius + slant * (1.0 - y) * height;
    // the drops hang in the world (not on the camera): the column wraps round the camera, so a moving ship really passes the rain
    vec2 w = xz + 2.0 * radius * floor((cam.xz - xz) / (2.0 * radius) + 0.5);
    vec3 world = vec3(w.x, cam.y + y * height - height * 0.35, w.y);
    if (UV.x > 0.5) world += vec3(-slant.x * len, len, -slant.y * len);      // the streak lies along the path of the drop
    fade = (1.0 - smoothstep(0.55, 1.0, length(w - cam.xz) / radius)) * blend;
    VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
}
void fragment() { ALBEDO = color.rgb; ALPHA = color.a * fade * (UV.x > 0.5 ? 0.45 : 1.0); }";

    // rings of the drops on the sea: world-fixed cells, each with its own expanding ring; the grid rides the same wave sum as the sea mesh
    static readonly string RingShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never;
uniform float t;
uniform float density;
uniform vec3 cam;
uniform vec3 col;
uniform float wave_k[6];
uniform float wave_w[6];
uniform float wave_a[6];
uniform vec2 wave_dir[6];
varying vec3 wp;
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
void vertex() {
    vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float h = 0.0;
    for (int i = 0; i < 6; i++) h += wave_a[i] * sin(wave_k[i] * dot(wave_dir[i], w.xz) - wave_w[i] * t);
    VERTEX.y += h + 0.05;
    wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}
void fragment() {
    vec2 p = wp.xz / 1.3; vec2 id = floor(p); vec2 f = fract(p);
    float h1 = hash(id), h2 = hash(id + 13.7), h3 = hash(id + 5.1);
    if (h1 > density) discard;
    float ph = fract(t / (0.7 + h2 * 0.8) + h3);
    vec2 c = vec2(0.25 + 0.5 * hash(id + 1.7), 0.25 + 0.5 * hash(id + 3.9));
    float ring = smoothstep(0.08, 0.0, abs(length(f - c) - ph * 0.45));
    float a = ring * (1.0 - ph) * (1.0 - ph) * 0.6 * (1.0 - smoothstep(16.0, 30.0, length(wp.xz - cam.xz)));
    if (a < 0.01) discard;
    ALBEDO = col; ALPHA = a;
}";

    static readonly string TornadoShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
uniform float t;
void vertex() {
    float h = UV.y;
    VERTEX.x += sin(t * 1.3 + h * 6.0) * 6.0 * h;
    VERTEX.z += cos(t * 1.1 + h * 5.0) * 6.0 * h;
}
void fragment() {
    float band = 0.5 + 0.5 * sin(UV.x * 6.2832 * 4.0 + UV.y * 22.0 - t * 5.0);
    float fade = smoothstep(0.0, 0.12, UV.y) * (1.0 - smoothstep(0.85, 1.0, UV.y) * 0.4);
    ALBEDO = mix(vec3(0.16, 0.17, 0.19), vec3(0.46, 0.47, 0.5), band);
    ALPHA = (0.28 + 0.4 * band) * fade;
}";

    MeshInstance3D? tornado;
    Vector2 tornadoPos, tornadoDir;
    float tornadoRadius = 400f;

    MeshInstance3D? rain;
    ShaderMaterial? rainMat;
    int rainDrops;
    float rainBlend, rainFadeTime = 2f, rainDensity = 0.4f;
    Vector2 rainWind;
    MeshInstance3D? rings;
    ShaderMaterial? ringMat;
    float time;
    readonly List<(Sprite3D sprite, float life, float total)> bolts = new();
    readonly ColorRect flash = new() { Color = new Color(0.85f, 0.9f, 1f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore };
    float flashAlpha;
    readonly List<(float delay, float amp)> flashQueue = new();
    string boltTexture = "";
    int subX = 4, subY = 1;

    public WeatherFx()
    {
        Name = "WeatherFx";
        var layer = new CanvasLayer { Layer = -1 };       // under the HUD, over the 3D scene: an additive flash lights terrain, ships and sky alike
        flash.Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
        flash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(flash);
        AddChild(layer);
    }

    static float F(Attr? a, string k, float def) => a?.Find(k, false)?.Value is { Length: > 0 } v ? Conv.ParseFloatPrefix(v) : def;

    /// <summary>Set by Main every frame: the active location is an interior (no environment.weather: tavern, house, shop, church, dungeon, cabin) - no rain, drops, splashes or flashes there.</summary>
    public bool Indoor;
    /// <summary>The active location object (null at sea); a change resets all rain effects at once, so a reload never shows residual rain frames.</summary>
    public object? LocKey;
    object? lastLocKey; bool lastIndoor; string lastRainState = "";

    void ResetRainNow()
    {
        rainBlend = 0f; flashAlpha = 0f; flashQueue.Clear();
        foreach (var b in bolts) b.sprite.QueueFree();
        bolts.Clear();
        if (rain != null) { rain.QueueFree(); rain = null; rainMat = null; rainDrops = 0; }
    }

    public void Sync(Interp it, Camera3D? cam, float dt)
    {
        time += dt;
        if (!ReferenceEquals(LocKey, lastLocKey) || Indoor != lastIndoor) { lastLocKey = LocKey; lastIndoor = Indoor; ResetRainNow(); }
        CycleWeather(it);
        if (System.Environment.GetEnvironmentVariable("SEADOGS_NOSUNFX") == null) SyncSunGlow(it, cam, dt);
        Entity? rainEnt = null, lightEnt = null;
        foreach (var e in it.Entities)
        {
            if (e.Deleted) continue;
            if (e.Class.Equals("rain", StringComparison.OrdinalIgnoreCase)) rainEnt = e;
            else if (e.Class.Equals("Lightning", StringComparison.OrdinalIgnoreCase)) lightEnt = e;
        }
        // ---- rain (fades in / out over Rain.TimeBlend, drops fixed in the world, rings on the sea)
        int drops = rainEnt == null ? 0 : (int)F(rainEnt.Root, "NumDrops", 0);
        if (Indoor) drops = 0;
        bool wantRain = drops > 0 && cam != null;
        float fadeTime = rainEnt != null ? Math.Max(0.5f, F(rainEnt.Root, "TimeBlend", 2000f) / 1000f) : rainFadeTime;
        rainFadeTime = fadeTime;
        rainBlend = Math.Clamp(rainBlend + (wantRain ? dt : -dt) / fadeTime, 0f, 1f);
        if (!wantRain && rainBlend <= 0f && rain != null) { rain.QueueFree(); rain = null; rainMat = null; rainDrops = 0; }
        if (wantRain)
        {
            var r = rainEnt!.Root;
            int n = Math.Clamp(drops, 200, 14000);
            if (rain == null || n != rainDrops) { rain?.QueueFree(); rain = BuildRain(n); AddChild(rain); rainDrops = n; }
            var c = r.Find("Color", false)?.Value is { } cv && long.TryParse(cv, out long ci) ? new Color(((ci >> 16) & 255) / 128f, ((ci >> 8) & 255) / 128f, (ci & 255) / 128f, Math.Clamp(((ci >> 24) & 255) / 255f, 0.35f, 0.85f)) : new Color(0.7f, 0.75f, 0.85f, 0.5f);
            float wa = 0f, ws = 0f;
            if (it.Globals.TryGetValue("Weather", out var w) && w.Val.Attr is { } wa2) { wa = F(wa2, "Wind.Angle", 0f); ws = F(wa2, "Wind.Speed", 0f); }
            rainWind = new Vector2(MathF.Sin(wa), -MathF.Cos(wa)) * Math.Clamp(ws / 12f, 0f, 1.5f);
            rainMat!.SetShaderParameter("speed", F(r, "Speed", 18f));
            rainMat.SetShaderParameter("height", Math.Max(5f, F(r, "Height", 30f)));
            rainMat.SetShaderParameter("radius", Math.Max(5f, F(r, "Radius", 30f)));
            rainMat.SetShaderParameter("len", Math.Max(0.4f, F(r, "DropLength", 2f)));
            rainMat.SetShaderParameter("wind", rainWind);
            rainMat.SetShaderParameter("color", new Vector4(c.R, c.G, c.B, c.A));
            rainDensity = Math.Clamp(drops / 5000f, 0.15f, 1f);
        }
        if (rain != null && rainMat != null && cam != null)
        {
            rainMat.SetShaderParameter("cam", cam.GlobalPosition);
            rainMat.SetShaderParameter("t", time);
            rainMat.SetShaderParameter("blend", rainBlend);
            rain.GlobalPosition = Vector3.Zero;
        }
        SyncRings(it, cam, rain != null && rainBlend > 0.01f);
        SplashSync(cam, dt, drops, rain != null && rainBlend > 0.05f);
        LensSync(rain != null ? Math.Clamp(drops / 6000f, 0.12f, 0.6f) * rainBlend * 0.7f : 0f);
        RainbowSync(rainEnt, cam);
        TintSync(dt);
        NightSync(it, cam);
        // ---- tornado (entity "Tornado": the script gives only the size; the engine moves it and reports the position with the event TornadoDamage)
        Entity? torEnt = null;
        foreach (var e in it.Entities) if (!e.Deleted && e.Class.Equals("Tornado", StringComparison.OrdinalIgnoreCase)) torEnt = e;
        if (torEnt == null || cam == null) { if (tornado != null) { tornado.QueueFree(); tornado = null; } }
        else
        {
            if (tornado == null)
            {
                var mat = new ShaderMaterial { Shader = new Shader { Code = TornadoShader } };
                tornado = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 70f, BottomRadius = 7f, Height = 420f, RadialSegments = 28, Rings = 24, CapTop = false, CapBottom = false }, MaterialOverride = mat, Name = "Tornado", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                AddChild(tornado);
                float a = (float)Random.Shared.NextDouble() * MathF.Tau;
                tornadoPos = new Vector2(cam.GlobalPosition.X + MathF.Sin(a) * 900f, cam.GlobalPosition.Z - MathF.Cos(a) * 900f);
                tornadoDir = new Vector2(MathF.Cos(a + 1.2f), MathF.Sin(a + 1.2f));
            }
            // it wanders, drifting slowly towards the observer
            var toCam = new Vector2(cam.GlobalPosition.X, cam.GlobalPosition.Z) - tornadoPos;
            tornadoDir = (tornadoDir + toCam.Normalized() * 0.05f * dt + new Vector2((float)Random.Shared.NextDouble() - 0.5f, (float)Random.Shared.NextDouble() - 0.5f) * 0.2f * dt).Normalized();
            tornadoPos += tornadoDir * 5f * dt;
            tornado.GlobalPosition = new Vector3(tornadoPos.X, 210f, tornadoPos.Y);
            ((ShaderMaterial)tornado.MaterialOverride!).SetShaderParameter("t", time);
            it.FireEvent("TornadoDamage", new List<V> { V.Float(dt), V.Float(tornadoPos.X), V.Float(-tornadoPos.Y) });
        }
        // ---- lightning
        if (lightEnt != null)
        {
            boltTexture = lightEnt.Root.Get("Texture")?.Value ?? boltTexture;
            subX = Math.Max(1, (int)F(lightEnt.Root, "SubTexX", 4)); subY = Math.Max(1, (int)F(lightEnt.Root, "SubTexY", 1));
        }
        for (int i = bolts.Count - 1; i >= 0; i--)
        {
            var (s, life, total) = bolts[i];
            life -= dt;
            if (life <= 0f) { s.QueueFree(); bolts.RemoveAt(i); continue; }
            bolts[i] = (s, life, total);
            s.Modulate = new Color(1, 1, 1, Math.Clamp(life / total * 1.5f, 0f, 1f) * (0.6f + 0.4f * MathF.Sin(life * 90f)));
        }
        for (int i = flashQueue.Count - 1; i >= 0; i--)
        {
            var (d, a) = flashQueue[i]; d -= dt;
            if (d <= 0f) { flashAlpha = Math.Max(flashAlpha, a); flashQueue.RemoveAt(i); } else flashQueue[i] = (d, a);
        }
        flashAlpha = Math.Max(0f, flashAlpha - dt * 3.5f);
        flash.Color = new Color(0.8f, 0.85f, 1f, flashAlpha * 0.9f);
        string st = $"indoor={(Indoor ? 1 : 0)} entity={(rainEnt != null ? 1 : 0)} node={(rain != null ? 1 : 0)} lens={(lens != null && lens.Visible ? 1 : 0)} splash={(splashNode != null && splashNode.Visible ? 1 : 0)} rainbow={(rainbow != null && rainbow.Visible ? 1 : 0)} blend={(rainBlend > 0.01f ? 1 : 0)}";
        if (st != lastRainState) { lastRainState = st; GD.Print("[rain] " + st + $" drops={drops}"); }
    }

    // ---------------------------------------------------------------- the weather follows the game clock
    // The script picks a weather record for the current hour whenever a location or the sea is created (CreateWeatherEnvironment); the original engine did not
    // re-pick while you stayed. Here the clock runs on land and at sea, so when the hour changes the same script function is called again (what the original's
    // developer key Whr_UpdateWeather did): a new record of that hour (clear / cloudy / rain / fog ...; storms only stay storms) with its own wind, sky, sun, fog,
    // sea harmonics. On land the static geometry carries the light of the hour it was loaded at (.col files), so there a record is only taken if its lighting
    // folder is the same; otherwise the old one is put back.
    int lastHour = -1;
    static int GInt(Interp it, string n) => it.Globals.TryGetValue(n, out var s) ? Conv.ToInt(s.Val) : 0;
    static string GStr(Interp it, string n) => it.Globals.TryGetValue(n, out var s) ? Conv.ToStr(s.Val) : "";

    /// <summary>Re-runs the script's weather creation for the current hour on the layers of the running one (Whr_UpdateWeather without the table re-roll).</summary>
    public static void Apply(Interp it)
    {
        it.CallTop("CreateWeatherEnvironment");
        it.CallTop("MoveWeatherToLayers", V.Str(GStr(it, "sNewExecuteLayer")), V.Str(GStr(it, "sNewRealizeLayer")));
    }

    /// <summary>Test hook --weather=Blue_Sky@N: the next weather is that record (SetNextWeather) and the environment is rebuilt.</summary>
    public void Force(Interp it, string id)
    {
        if (id.StartsWith('#') && it.Globals.TryGetValue("iNextWeatherNum", out var nx)) nx.Val = V.Int(int.Parse(id.Substring(1)));       // #N: the record with that number
        else it.CallTop("SetNextWeather", V.Str(id));
        Apply(it);
        Log(it, "forced " + id);
    }

    /// <summary>Test hook --weathertable@N: one log line per distinct kind of record (id, hours, rain, fog, storm, numbers).</summary>
    public static void DumpTable(Interp it)
    {
        if (!it.Globals.TryGetValue("Weathers", out var ws) || ws.Arr == null) return;
        var kinds = new Dictionary<string, List<int>>();
        for (int i = 0; i < ws.Arr.Length; i++)
        {
            if (ws.Arr[i].Val.Attr is not { } r || r.Get("id") == null) continue;
            string k = $"{r.Get("id")!.Value} hours={r.Find("Hour.Min", false)?.Value}-{r.Find("Hour.Max", false)?.Value} storm={r.Get("Storm")?.Value} skip={r.Get("Skip")?.Value} rain={r.Find("Rain.NumDrops", false)?.Value} fog={r.Find("Fog.Density", false)?.Value} seafog={r.Find("SpecialSeaFog.Density", false)?.Value} sky={r.Find("Sky.Dir", false)?.Value?.Trim((char)92)} wind={r.Find("Wind.Speed", false)?.Value}";
            if (!kinds.TryGetValue(k, out var l)) kinds[k] = l = new();
            l.Add(i);
        }
        foreach (var (k, l) in kinds) GD.Print($"[weathertable] {k} first={l[0]} count={l.Count}");
    }

    public static string Describe(Interp it)
    {
        string id = "?";
        if (it.Globals.TryGetValue("Weathers", out var ws) && ws.Arr != null)
        {
            int n = GInt(it, "iCurWeatherNum");
            if (n >= 0 && n < ws.Arr.Length) id = ws.Arr[n].Val.Attr?.Get("id")?.Value ?? "?";
        }
        string sky = "", drops = "0", fog = "", wind = "", storm = GInt(it, "bWeatherIsStorm").ToString();
        foreach (var e in it.Entities)
        {
            if (e.Deleted) continue;
            if (e.Class.Equals("Sky", StringComparison.OrdinalIgnoreCase)) sky = e.Root.Get("Dir")?.Value ?? "";
            else if (e.Class.Equals("rain", StringComparison.OrdinalIgnoreCase)) drops = e.Root.Get("NumDrops")?.Value ?? "0";
        }
        if (it.Globals.TryGetValue("Weather", out var w) && w.Val.Attr is { } wa) { fog = (wa.Find("Fog.Density", false)?.Value ?? ""); wind = F(wa, "Wind.Speed", 0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "@" + F(wa, "Wind.Angle", 0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); }
        string sun = "";
        if (it.Globals.TryGetValue("Weather", out var w2) && w2.Val.Attr is { } wb) sun = F(wb, "Sun.HeightAngle", 0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return $"num={GInt(it, "iCurWeatherNum")} hour={GInt(it, "iCurWeatherHour")} id='{id}' storm={storm} night={GInt(it, "bWeatherIsNight")} rain={drops} wind={wind} fog={fog} sun={sun} sky={sky.Trim((char)92)}";
    }

    void Log(Interp it, string what)
    {
        string t = it.Globals.TryGetValue("Environment", out var e) && e.Val.Attr is { } ea ? ea.Find("time", false)?.Value ?? "" : "";
        GD.Print($"[weather] {what}: time={t} {Describe(it)}");
    }

    // The records give one fixed sun position per weather (Blue Sky: 7 - 17 h, height 54 deg). The sun is moved along a day arc inside the record's hour range:
    // lower and further east/west at its start and end, the record's own value at the middle; every reader (sky, sea glitter, glow, character light, shadows) reads Weather.Sun.*.
    string? arcWrittenH, arcWrittenAz; float arcBaseH, arcBaseAz;
    void SunArc(Interp it, Attr weather, Attr env)
    {
        var hAttr = weather.Find("Sun.HeightAngle", false); var azAttr = weather.Find("Sun.AzimuthAngle", false);
        if (hAttr?.Value == null || azAttr?.Value == null) return;
        if (hAttr.Value != arcWrittenH || azAttr.Value != arcWrittenAz) { arcBaseH = F(weather, "Sun.HeightAngle", 0.9f); arcBaseAz = F(weather, "Sun.AzimuthAngle", 0f); }   // a fresh record was written by the script
        int n = GInt(it, "iCurWeatherNum");
        if (!it.Globals.TryGetValue("Weathers", out var ws) || ws.Arr == null || n < 0 || n >= ws.Arr.Length || ws.Arr[n].Val.Attr is not { } rec) return;
        float min = F(rec, "Hour.Min", 0f), max = F(rec, "Hour.Max", 24f), t = F(env, "time", 12f);
        float span = ((max - min) % 24f + 24f) % 24f + 1f;
        float f = Math.Clamp((((t - min) % 24f + 24f) % 24f) / span, 0f, 1f);
        float h = Math.Max(0.1f, arcBaseH * (0.55f + 0.45f * MathF.Sin(MathF.PI * f)));
        float az = arcBaseAz + (f - 0.5f) * 1.5f;
        arcWrittenH = h.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture); arcWrittenAz = az.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
        hAttr.Value = arcWrittenH; azAttr.Value = arcWrittenAz;
    }

    void CycleWeather(Interp it)
    {
        if (!it.Globals.TryGetValue("Environment", out var env) || env.Val.Attr is not { } ea || !it.Globals.ContainsKey("iCurWeatherNum")) return;
        bool hasWeather = false, atSea = false;
        foreach (var e in it.Entities)
        {
            if (e.Deleted) continue;
            if (e.Class.Equals("Weather", StringComparison.OrdinalIgnoreCase)) hasWeather = true;
            else if (e.Class.Equals("sea", StringComparison.OrdinalIgnoreCase)) atSea = true;
        }
        wxHas = hasWeather; wxSea = atSea; wxWeather = it.Globals.TryGetValue("Weather", out var wgl) ? wgl.Val.Attr : null;
        if (!hasWeather) { lastHour = -1; return; }
        if (it.Globals.TryGetValue("Weather", out var wg) && wg.Val.Attr is { } wattr) SunArc(it, wattr, ea);
        int hour = (int)MathF.Floor(F(ea, "time", 0f));
        if (lastHour < 0) { lastHour = hour; Log(it, "begin"); return; }
        if (hour == lastHour) return;
        lastHour = hour;
        if (GInt(it, "iCurWeatherHour") == hour) return;
        int oldNum = GInt(it, "iCurWeatherNum"); string oldLight = GStr(it, "sLightingPath");
        bool wasRain = GInt(it, "bWeatherIsRain") != 0, wasStorm = GInt(it, "bWeatherIsStorm") != 0;
        Apply(it);
        if (atSea && (GInt(it, "bWeatherIsRain") != 0) != wasRain && GInt(it, "bWeatherIsStorm") == (wasStorm ? 1 : 0) && GInt(it, "seaAlarmed") == 0) it.CallTop("SetSchemeForSea");     // rain loop on / off (the script sets sounds only when the sea is created)
        // the baked light (.col of the hour the scene was loaded at) is re-tinted by TintSync, so land and islands follow the new record too
        if (atSea && it.Globals.TryGetValue("Island", out var isl) && isl.Val.Attr is { } ia) ia.GetOrCreate("LightingPath").Value = GStr(it, "sLightingPath");
        TintSync(0f);
        Log(it, "hour changed" + TintLog(hour));
    }

    void SyncRings(Interp it, Camera3D? cam, bool on)
    {
        bool sea = false;
        foreach (var e in it.Entities) if (!e.Deleted && e.Class.Equals("sea", StringComparison.OrdinalIgnoreCase)) { sea = true; break; }
        if (!on || !sea || cam == null) { if (rings != null) { rings.QueueFree(); rings = null; ringMat = null; } return; }
        if (rings == null)
        {
            ringMat = new ShaderMaterial { Shader = new Shader { Code = RingShader }, RenderPriority = 1 };
            rings = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(64, 64), SubdivideWidth = 63, SubdivideDepth = 63 }, MaterialOverride = ringMat, Name = "RainRings", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = new Aabb(new Vector3(-40, -10, -40), new Vector3(80, 20, 80)) };
            AddChild(rings);
        }
        rings.GlobalPosition = new Vector3(MathF.Round(cam.GlobalPosition.X), 0f, MathF.Round(cam.GlobalPosition.Z));
        ringMat!.SetShaderParameter("t", SeaWaves.Time);
        ringMat.SetShaderParameter("density", rainDensity * Math.Clamp(rainBlend, 0f, 1f));
        ringMat.SetShaderParameter("cam", cam.GlobalPosition);
        ringMat.SetShaderParameter("col", new Vector3(0.85f, 0.9f, 0.95f));
        ringMat.SetShaderParameter("wave_k", (float[])SeaWaves.K.Clone()); ringMat.SetShaderParameter("wave_w", (float[])SeaWaves.W.Clone());
        ringMat.SetShaderParameter("wave_a", (float[])SeaWaves.A.Clone()); ringMat.SetShaderParameter("wave_dir", (Vector2[])SeaWaves.Dir.Clone());
    }

    MeshInstance3D BuildRain(int n)
    {
        var rnd = new Random(7);
        var verts = new Vector3[n * 2]; var uv = new Vector2[n * 2]; var col = new Color[n * 2];
        for (int i = 0; i < n; i++)
        {
            float bx = (float)rnd.NextDouble(), by = (float)rnd.NextDouble(), bz = (float)rnd.NextDouble();
            for (int k = 0; k < 2; k++)
            {
                verts[i * 2 + k] = Vector3.Zero;
                uv[i * 2 + k] = new Vector2(k, 0);
                col[i * 2 + k] = new Color(bx, by, bz);        // the drop's base position in the unit cube travels in the vertex colour
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts; arrays[(int)Mesh.ArrayType.TexUV] = uv; arrays[(int)Mesh.ArrayType.Color] = col;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        rainMat = new ShaderMaterial { Shader = new Shader { Code = RainShader } };
        return new MeshInstance3D { Mesh = mesh, MaterialOverride = rainMat, Name = "Rain", CustomAabb = new Aabb(new Vector3(-2000, -2000, -2000), new Vector3(4000, 4000, 4000)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }

    /// <summary>Message 54000 of the Lightning entity: (id, subtexture, name, time, flicker, size, scaleX, scaleY, x, y, z, flash name, flash size, flash time).</summary>
    public V OnLightning(V[] d)
    {
        if (d.Length < 11 || Conv.ToInt(d[0]) != 54000 || Indoor) return V.Int(1);   // fmt llsflffffffsff
        int sub = Conv.ToInt(d[1]);
        float t = Math.Max(0.15f, Conv.ToFloat(d[3])), size = Conv.ToFloat(d[5]), sy = Conv.ToFloat(d[7]);
        var pos = new Vector3(Conv.ToFloat(d[8]), Conv.ToFloat(d[9]), -Conv.ToFloat(d[10]));
        var tex = GameAssets.Texture(boltTexture.Length > 0 ? boltTexture : "Weather\\lightning\\lightning_storm.tga");
        if (tex != null)
        {
            float cw = tex.GetWidth() / (float)subX, ch = tex.GetHeight() / (float)subY;
            var s = new Sprite3D
            {
                Texture = new AtlasTexture { Atlas = tex, Region = new Rect2((sub % subX) * cw, (sub / subX) * ch, cw, ch) },
                Billboard = BaseMaterial3D.BillboardModeEnum.FixedY, Shaded = false, Transparent = true, DoubleSided = true,
                PixelSize = Math.Max(0.01f, size * Math.Max(0.2f, sy) / ch), Position = pos, TopLevel = true,
            };
            AddChild(s);
            bolts.Add((s, t + 0.2f, t + 0.2f));
        }
        float dist = new Vector2(pos.X, pos.Z).Length();
        float amp = Math.Clamp(0.6f - dist / 6000f, 0.14f, 0.6f);
        GD.Print($"[weather] lightning bolt at {dist:0} m, flash {amp:0.00}, thunder in {dist / 333f:0.0} s");
        flashAlpha = Math.Max(flashAlpha, amp);
        flashQueue.Add((0.13f, amp * 0.8f)); flashQueue.Add((0.27f, amp * 0.55f));      // the bolt flickers: main flash and two weaker returns
        return V.Int(1);
    }
}
