using System;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Outdoor surroundings driven by the script's engine entities "Sky" (texture folder), "sea" (water/fog colours) and "Weather":
/// a sky box that follows the camera, a water plane at y = 0 and fog. Colours in the script are 0xRRGGBB integers.
/// </summary>
public partial class OutdoorEnv : Node3D
{
    Node3D? sky;
    string skyDir = "";
    MeshInstance3D? sea;
    public bool HasSea => sea != null;
    /// <summary>Set by Main before Sync: the camera is in a land location (port seen from the dock): the water shader tunes shore foam / ripples for a close view.</summary>
    public bool OnLand;
    ShaderMaterial? seaMat, farMat;
    MeshInstance3D? seaFar;
    WorldEnvironment? world;
    float time;

    static Color Rgb(string? v, Color def)
    {
        if (!int.TryParse(v, out int c)) return def;
        return new Color(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f);
    }


    public void Sync(Interp it, Camera3D? cam, float dt)
    {
        time += dt;
        // debug: SEADOGS_SKYLOOK="yawDeg,pitchDeg" turns the camera to a sky direction (sky seam checks)
        if (cam != null && System.Environment.GetEnvironmentVariable("SEADOGS_SKYLOOK")?.Split(',') is { Length: 2 } sl)
            cam.Rotation = new Vector3(Mathf.DegToRad(Conv.ParseFloatPrefix(sl[1])), Mathf.DegToRad(Conv.ParseFloatPrefix(sl[0])), 0);
        Entity? skyEnt = null, seaEnt = null;
        foreach (var e in it.Entities)
        {
            if (e.Deleted) continue;
            if (e.Class.Equals("Sky", StringComparison.OrdinalIgnoreCase)) skyEnt = e;
            else if (e.Class.Equals("sea", StringComparison.OrdinalIgnoreCase)) seaEnt = e;
        }

        // ---- sky box (Sky entity: Dir = the texture folder, Color = texture factor, RotateSpeed rad/s, Angle rad). A new folder (the weather changed with the hour) is faded in over the old one
        smDt = dt;
        if (skyEnt != null)
        {
            string dir = skyEnt.Root.Get("Dir")?.Value ?? "";
            if (dir.Length > 0 && (sky == null || dir != skyDir))
            {
                var keepRefl = SkySide;
                var ns = BuildSky(dir, out var mats);
                FreeOldSky();
                var prev = sky;
                if (ns == null) { prev?.QueueFree(); sky = null; skyDir = ""; skyMats = new(); }
                else
                {
                    if (prev != null) { skyOld = prev; skyFade = 0f; reflPrev = keepRefl; foreach (var m in mats) SkyFading(m, true); GD.Print($"[weather] sky fade {Math.Max(6f, Tau * 3f):0} s: {skyDir} -> {dir}"); }
                    else skyFade = 1f;
                    sky = ns; skyMats = mats; skyDir = dir; reflKey = ""; AddChild(sky);
                }
            }
            var scol = skyEnt.Root.Get("Color")?.Value is { } scv && long.TryParse(scv, out long sci) ? new Color(((sci >> 16) & 255) / 255f, ((sci >> 8) & 255) / 255f, (sci & 255) / 255f) : Colors.White;
            skyRot += Conv.ParseFloatPrefix(skyEnt.Root.Get("RotateSpeed")?.Value) * dt;
            skyYaw = Conv.ParseFloatPrefix(skyEnt.Root.Get("Angle")?.Value) + skyRot;
            skyTint = Sm("skytint", scol);
        }
        else { FreeOldSky(); if (sky != null) { sky.QueueFree(); sky = null; skyDir = ""; skyMats = new(); } if (fogDome != null) { fogDome.QueueFree(); fogDome = null; } }
        if (skyOld != null) { skyFade += dt / Math.Max(6f, Tau * 3f); if (skyFade >= 1f) { skyFade = 1f; reflPrev = null; FreeOldSky(); foreach (var m in skyMats) SkyFading(m, false); } }
        foreach (var m in skyMats) m.SetShaderParameter("tint", new Vector4(skyTint.R, skyTint.G, skyTint.B, skyOld != null ? skyFade : 1f));
        if (sky != null && cam != null) { sky.GlobalPosition = cam.GlobalPosition; sky.Rotation = new Vector3(0, skyYaw, 0); }
        if (skyOld != null && cam != null) skyOld.GlobalPosition = cam.GlobalPosition;
        SyncFogDome(it, cam);

        // ---- sea + fog
        if (seaEnt != null)
        {
            var root = seaEnt.Root;
            if (sea == null) smooth.Clear();
            var water = Rgb(root.Find("Water.Color", false)?.Value, new Color(0.12f, 0.16f, 0.22f));
            var skyCol = Rgb(root.Find("Sky.Color", false)?.Value, Colors.White);
            var fog = Rgb(root.Find("Fog.Color", false)?.Value, new Color(0.86f, 0.86f, 0.9f));
            if (sea == null)
            {
                SeaWaves.Snap(); smF.Clear();
                GameAssets.Prefetch(new[] { "smoke8.tga", "smoke9.tga", "explos.tga", "splash.tga", "blot.tga", "fire3.tga", "seafoam_2.tga", "pena.tga" });      // fight effects decoded now, not on the first shot
                seaMat = new ShaderMaterial { Shader = SeaSurface.MakeShader(), RenderPriority = SeaSurface.Boujie ? -100 : 0 };   // boujie reads the screen: the sea is first in the transparent pass, wakes and foam come after
                // a fine grid near the camera carries the waves; the far sea is a flat plane under the fog
                sea = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(1000, 1000), SubdivideWidth = System.Environment.GetEnvironmentVariable("SEADOGS_SEAGRID") is { } sg ? int.Parse(sg) : 199, SubdivideDepth = System.Environment.GetEnvironmentVariable("SEADOGS_SEAGRID") is { } sg2 ? int.Parse(sg2) : 399 }, MaterialOverride = seaMat, Name = "Sea", CustomAabb = new Aabb(new Vector3(-500, -20, -500), new Vector3(1000, 40, 1000)) };
                farMat = new ShaderMaterial { Shader = seaMat.Shader, RenderPriority = seaMat.RenderPriority };
                seaFar = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(26000, 26000) }, MaterialOverride = farMat, Name = "SeaFar" };
                AddChild(seaFar);
                AddChild(sea);
            }
            seaMat!.SetShaderParameter("t", time);
            SeaWaves.Time = time; SeaWaves.Configure(root, dt);
            SetSeaParams(seaMat, root, it, false);
            if (cam != null)
            {
                // the wave grid follows the camera in whole 5 m steps (the waves are a function of the world position, so the surface does not swim)
                sea.GlobalPosition = new Vector3(MathF.Round(cam.GlobalPosition.X / 5f) * 5f, 0, MathF.Round(cam.GlobalPosition.Z / 5f) * 5f);
                if (seaFar != null) seaFar.GlobalPosition = new Vector3(sea.GlobalPosition.X, 0f, sea.GlobalPosition.Z);
            }
            if (farMat != null) SetSeaParams(farMat, root, it, true);
            float density = root.Find("Fog.Density", false)?.Value is { } d && float.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dv) ? dv : 0.001f;
            smDens = smDens < 0f ? density : smDens + (density - smDens) * (1f - MathF.Exp(-dt / Tau));
            SetEnvironment(Sm("envfog", fog), smDens * 1.2f);
        }
        else
        {
            if (sea != null) { sea.QueueFree(); sea = null; seaMat = null; }
            if (seaFar != null) { seaFar.QueueFree(); seaFar = null; farMat = null; }
            if (world != null) { world.QueueFree(); world = null; }
            smooth.Clear(); smDens = -1f;
        }
    }

    // ---- smooth changes: when the weather record changes with the hour, colours and fog glide to the new values instead of jumping
    readonly System.Collections.Generic.Dictionary<string, Color> smooth = new();
    float smDt, smDens = -1f;
    Color Sm(string key, Color target)
    {
        if (!smooth.TryGetValue(key, out var c)) c = target; else c = c.Lerp(target, 1f - MathF.Exp(-smDt / Tau));
        smooth[key] = c;
        return c;
    }
    static float Tau => SeaWaves.Tau;
    readonly System.Collections.Generic.Dictionary<string, float> smF = new();
    /// <summary>Glides a number to its target with the weather blend time constant (the first value is taken at once).</summary>
    float Smf(string key, float target)
    {
        if (!smF.TryGetValue(key, out var c)) c = target; else c += (target - c) * (1f - MathF.Exp(-smDt / Tau));
        smF[key] = c;
        return c;
    }
    Vector3 smSunN = Vector3.Zero, smSunF = Vector3.Zero;

    // ---- sky: old folder fading out, texture factor, rotation
    Node3D? skyOld;
    float skyFade = 1f, skyRot, skyYaw;
    Color skyTint = Colors.White;
    System.Collections.Generic.List<ShaderMaterial> skyMats = new();
    void FreeOldSky() { if (skyOld != null) { skyOld.QueueFree(); skyOld = null; } }

    // ---- the engine sky fog sphere (sky.cpp UpdateFogSphere): a hemisphere in the fog colour whose alpha falls from 1 at the horizon to 0 at Height (the fog Height of the weather record:
    // SpecialSeaFog 1000 at sea in the morning = a third of the sky is misty; the day's 10 = a thin band). Vertex y in the engine sphere units (radius 2000).
    MeshInstance3D? fogDome;
    string reflKey = ""; float lastDomeH = -1f;
    /// <summary>The sea reflects the sky together with its fog sphere (the original draws both into the reflection): the reflected texture (SkySide) gets the same fog by rows, rebuilt when the sky, fog colour or height change.</summary>
    void BakeFoggedReflection(Color col, float height)
    {
        string key = skyDir + "|" + col.ToHtml(false) + "|" + (int)height;
        if (key == reflKey || skyDir.Length == 0) return;
        if (reflKey != "" && time - lastBakeT < 1.2f) return;          // the fog colour glides for a minute: rebuild the reflection (a CPU pass over the sky strip) about once a second, not every frame
        reflKey = key; lastBakeT = time;
        var src = GameAssets.Texture(skyDir.TrimEnd((char)92) + (char)92 + "sky_side.tga");
        var img = src?.GetImage();
        if (img == null) return;
        if (img.IsCompressed()) img.Decompress();
        img.ClearMipmaps(); img.Convert(Image.Format.Rgba8);
        int w = img.GetWidth(), h = img.GetHeight(), wallH = Math.Max(1, h / 4);
        var data = img.GetData();
        byte cr = (byte)(col.R * 255f), cg = (byte)(col.G * 255f), cb = (byte)(col.B * 255f);
        for (int row = 0; row < h; row++)
        {
            float t = (row % wallH + 0.5f) / wallH;                 // 0 = top of the wall (y 1300), 1 = bottom (y -150)
            float yw = 1300f - t * 1450f;
            float ys = yw <= 0f ? 0f : 2000f * yw / MathF.Sqrt(1500f * 1500f + yw * yw);     // height on the engine fog sphere (radius 2000)
            float k = yw <= 0f ? 1f : 1f - Mathf.SmoothStep(0f, 1f, ys / Math.Max(height, 1f));
            if (k <= 0.001f) continue;
            int o = row * w * 4;
            for (int x = 0; x < w; x++, o += 4)
            {
                data[o] = (byte)(data[o] + (cr - data[o]) * k); data[o + 1] = (byte)(data[o + 1] + (cg - data[o + 1]) * k); data[o + 2] = (byte)(data[o + 2] + (cb - data[o + 2]) * k);
            }
        }
        img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, data);
        img.GenerateMipmaps(); SkySide = ImageTexture.CreateFromImage(img);
    }
    ShaderMaterial? fogDomeMat;
    ImageTexture? reflPrev; float lastBakeT = -10f;      // the reflection of the old sky, cross-faded in the sea shader while the sky box fades
    static readonly string FogDomeShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, fog_disabled;
uniform vec4 fog_col : source_color = vec4(1.0);
uniform float height = 10.0;
uniform float skirt = 10.0;
varying float a;
void vertex() {
    a = VERTEX.y <= 0.0 ? 1.0 : 1.0 - smoothstep(0.0, 1.0, VERTEX.y / max(height, 1.0));
    if (VERTEX.y < 0.0) VERTEX.y = -skirt;      // a skirt below the horizon closes the gap between the edge of the far sea and the sky
}
void fragment() { ALBEDO = fog_col.rgb; ALPHA = a * fog_col.a; }";
    void SyncFogDome(Interp it, Camera3D? cam)
    {
        if (sky == null || cam == null || System.Environment.GetEnvironmentVariable("SEADOGS_NOFOGDOME") != null) { if (fogDome != null) fogDome.Visible = false; return; }
        if (fogDome == null)
        {
            const int levels = 64, angles = 16; const float R = 2000f;
            var verts = new System.Collections.Generic.List<Vector3>(); var idx = new System.Collections.Generic.List<int>();
            for (int x = 0; x < angles; x++) { float an = x / (float)angles * MathF.Tau; verts.Add(new Vector3(R * MathF.Cos(an), -1f, R * MathF.Sin(an))); }
            for (int y = 0; y < levels; y++)
            {
                float h = y * R / levels, r1 = MathF.Sqrt(R * R - h * h);
                for (int x = 0; x < angles; x++) { float an = x / (float)angles * MathF.Tau; verts.Add(new Vector3(r1 * MathF.Cos(an), h, r1 * MathF.Sin(an))); }
            }
            verts.Add(new Vector3(0, R, 0));
            for (int x = 0; x < angles; x++) { int a1 = (x + 1) % angles, b0 = angles + x, b1 = angles + a1; idx.Add(x); idx.Add(a1); idx.Add(b0); idx.Add(a1); idx.Add(b1); idx.Add(b0); }
            for (int y = 0; y < levels; y++)
                for (int x = 0; x < angles; x++)
                {
                    int a0 = angles + y * angles + x, a1 = angles + y * angles + (x + 1) % angles;
                    if (y == levels - 1) { idx.Add(verts.Count - 1); idx.Add(a0); idx.Add(a1); }
                    else { int b0 = a0 + angles, b1 = a1 + angles; idx.Add(a0); idx.Add(a1); idx.Add(b0); idx.Add(a1); idx.Add(b1); idx.Add(b0); }
                }
            var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray(); arr[(int)Mesh.ArrayType.Index] = idx.ToArray();
            var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            fogDomeMat = new ShaderMaterial { Shader = new Shader { Code = FogDomeShader }, RenderPriority = -8 };
            fogDome = new MeshInstance3D { Mesh = mesh, MaterialOverride = fogDomeMat, Name = "SkyFog", Scale = Vector3.One * 0.65f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = new Aabb(new Vector3(-2100, -10, -2100), new Vector3(4200, 2200, 4200)) };
            AddChild(fogDome);
        }
        fogDome.Visible = true;
        fogDome.GlobalPosition = cam.GlobalPosition;
        Color col = new(0.8f, 0.8f, 0.85f); float height = 10f;
        if (it.Globals.TryGetValue("Weather", out var w) && w.Val.Attr is { } wa) col = Rgb(wa.Find("Fog.Color", false)?.Value, col);
        if (it.Globals.TryGetValue("Weathers", out var ws) && ws.Arr != null && it.Globals.TryGetValue("iCurWeatherNum", out var nn) && it.Globals.TryGetValue("sCurrentFog", out var cf))
        {
            int n = Conv.ToInt(nn.Val); string kind = Conv.ToStr(cf.Val);
            if (n >= 0 && n < ws.Arr.Length && ws.Arr[n].Val.Attr is { } rec) height = Conv.ParseFloatPrefix(rec.Find((kind.Length > 0 ? kind : "Fog") + ".Height", false)?.Value);
            if (height <= 0f) height = 10f;
        }
        if (height != lastDomeH && System.Environment.GetEnvironmentVariable("SEADOGS_WEATHERLOG") != null) { lastDomeH = height; GD.Print($"[weather] dome fog height={height} num={Conv.ToInt(it.Globals["iCurWeatherNum"].Val)}"); }
        height = Smf("domeh", height);
        FogDomeHeight = height;
        BakeFoggedReflection(col, height);
        fogDomeMat!.SetShaderParameter("fog_col", Sm("domefog", col));
        fogDomeMat.SetShaderParameter("height", height);
        fogDomeMat.SetShaderParameter("skirt", (float.TryParse(System.Environment.GetEnvironmentVariable("SEADOGS_SKIRT"), out var skx) ? skx : 0.7f * Math.Max(cam.GlobalPosition.Y, 0f) + 3f) / 0.65f);
    }


    static float Fl(Attr a, string path, float def) => float.TryParse(a.Find(path, false)?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
    static Texture2DArray? bumpTex; static string bumpDir = "";
    static ImageTexture? foamTex;
    public static ImageTexture? SkySide, SkyUp;
    public static float FogDomeHeight = 10f;
    float bumpFrame;

    /// <summary>Feeds the sea shader (SeaSurface.Shader) from the script's sea entity (colours, bump map, reflection) and the wave field.</summary>
    void SetSeaParams(ShaderMaterial m, Attr root, Interp it, bool far)
    {
        Color C(string p, Color d) => Sm((far ? "f" : "n") + p, Rgb(root.Find(p, false)?.Value, d));
        var water = C("Water.Color", new Color(0.12f, 0.16f, 0.22f));
        var skyc = C("Sky.Color", Colors.White);
        var fog = C("Fog.Color", new Color(0.86f, 0.86f, 0.9f));
        var pena = C("Pena.Color", new Color(0.6f, 0.6f, 0.6f));
        var sunc = C("Sun.Color", Colors.White);
        var road = C("Road.Color1", Colors.Black);
        var road2 = C("Road.Color2", Colors.Black);
        Vector3 sunDir = new Vector3(0.3f, 0.6f, 0.3f);
        if (it.Globals.TryGetValue("Weather", out var w) && w.Val.Attr is { } wr)
        {
            float h = Fl(wr, "Sun.HeightAngle", 0.9f), az = Fl(wr, "Sun.AzimuthAngle", 0f);
            sunDir = new Vector3(MathF.Cos(az) * MathF.Cos(h), MathF.Sin(h), MathF.Sin(az) * MathF.Cos(h));
        }
        if (far) { smSunF = smSunF == Vector3.Zero ? sunDir : smSunF.Lerp(sunDir, 1f - MathF.Exp(-smDt / Tau)); sunDir = smSunF; }
        else { smSunN = smSunN == Vector3.Zero ? sunDir : smSunN.Lerp(sunDir, 1f - MathF.Exp(-smDt / Tau)); sunDir = smSunN; }
        m.SetShaderParameter("water_color", water);
        m.SetShaderParameter("sky_color", skyc);
        m.SetShaderParameter("fog_color", fog);
        m.SetShaderParameter("pena_color", new Color(Math.Min(1f, pena.R * 2.4f + fog.R * 0.8f + 0.12f), Math.Min(1f, pena.G * 2.4f + fog.G * 0.8f + 0.12f), Math.Min(1f, pena.B * 2.4f + fog.B * 0.8f + 0.12f)));
        m.SetShaderParameter("sun_color", sunc);
        string fk = far ? "f" : "n";
        float rp = Smf(fk + "rp", Fl(root, "Road.Power", 1f));
        var rc = new Color((road.R + road2.R) * 0.5f, (road.G + road2.G) * 0.5f, (road.B + road2.B) * 0.5f);
        m.SetShaderParameter("road_color", rc);
        m.SetShaderParameter("road_power", rp);
        m.SetShaderParameter("sun_dir", sunDir.Normalized());
        m.SetShaderParameter("fog_density", Smf(fk + "fd", Fl(root, "Fog.SeaDensity", 0.002f)));
        m.SetShaderParameter("refl_k", Smf(fk + "rk", Fl(root, "WaterReflection", 0.7f)));
        m.SetShaderParameter("fresnel_k", Smf(fk + "fk", Fl(root, "FrenelCoefficient", 0.7f)));
        m.SetShaderParameter("t", time);
        m.SetShaderParameter("disp", far ? 0f : 1f);
        m.SetShaderParameter("far_hole", far && sea != null ? new Vector3(sea.GlobalPosition.X, sea.GlobalPosition.Z, 497f) : new Vector3(0, 0, -1f));
        float amp = SeaWaves.TotalAmp;
        m.SetShaderParameter("amp_total", amp);
        m.SetShaderParameter("chop", Math.Clamp(0.35f + amp * 0.25f, 0.35f, 0.95f));
        m.SetShaderParameter("foam_gain", Math.Clamp(((OnLand ? amp / 1.4f : amp) - 0.45f) / 1.6f, 0f, 1f));
        string bd = root.Find("Bump.Dir", false)?.Value ?? "";
        if (bd.Length > 0 && (bumpTex == null || bd != bumpDir)) { bumpTex = SeaSurface.LoadBump(bd); bumpDir = bd; }
        foamTex ??= GameAssets.Texture("pena.tga");
        float tile = Smf(fk + "bt", Fl(root, "Bump.Tile", 0.05f)), spd = Smf(fk + "bs", Fl(root, "Bump.AnimSpeed", 15f));
        if (!far) bumpFrame += (float)GetProcessDeltaTime() * spd * 0.5f;
        m.SetShaderParameter("has_bump", bumpTex != null);
        if (bumpTex != null) m.SetShaderParameter("bump", bumpTex);
        m.SetShaderParameter("bump_tile", tile);
        m.SetShaderParameter("bump_ang", Smf(fk + "ba", Fl(root, "Bump.Ang", 0f)));
        m.SetShaderParameter("bump_speed", Smf(fk + "bv", Fl(root, "Bump.Speed", 0.01f)) * 6f);
        m.SetShaderParameter("bump_scale", Smf(fk + "bc", Fl(root, "Bump.Scale", 2f)));
        m.SetShaderParameter("bump_frame", bumpFrame);
        m.SetShaderParameter("has_sky", SkySide != null);
        if (SkySide != null) m.SetShaderParameter("sky_tex", SkySide);
        m.SetShaderParameter("sky_prev", reflPrev != null ? reflPrev : SkySide);
        m.SetShaderParameter("sky_mix", reflPrev != null && skyOld != null ? skyFade : 1f);
        if (SeaSurface.Boujie)
        {
            // boujie look: its own foam and albedo textures; screen refraction / depth fog / shore foam only on the near mesh (Compatibility: screen + depth textures)
            if (SeaSurface.BoujieTexture("foam.png") is { } bf) m.SetShaderParameter("foam_tex", bf);
            if (SeaSurface.BoujieTexture("ocean_albedo.png") is { } ba) m.SetShaderParameter("albedo_tex", ba);
            m.SetShaderParameter("debug_depth", System.Environment.GetEnvironmentVariable("SEADOGS_WATERDBG") == "1" ? 1f : 0f);
            m.SetShaderParameter("use_screen", !far && !SeaSurface.NoScreen);
            m.SetShaderParameter("detail", OnLand ? 1.9f : 1.0f);
            m.SetShaderParameter("shore_end", OnLand ? 1.5f : 2.2f);
            m.SetShaderParameter("shore_gain", OnLand ? 0.85f : 1.0f);
            m.SetShaderParameter("shallow_color", new Color(Math.Min(1f, water.R * 2f + 0.06f), Math.Min(1f, water.G * 2.6f + 0.2f), Math.Min(1f, water.B * 2.2f + 0.2f)));
        }
        else if (foamTex != null) m.SetShaderParameter("foam_tex", foamTex);
        m.SetShaderParameter("wave_k", (float[])SeaWaves.K.Clone()); m.SetShaderParameter("wave_w", (float[])SeaWaves.Phase.Clone());
        m.SetShaderParameter("wave_a", (float[])SeaWaves.A.Clone()); m.SetShaderParameter("wave_dir", (Vector2[])SeaWaves.Dir.Clone());
    }

    void SetEnvironment(Color fog, float density)
    {
        if (world == null)
        {
            world = new WorldEnvironment { Environment = new Godot.Environment() };
            AddChild(world);
        }
        var env = world.Environment;
        env.BackgroundMode = Godot.Environment.BGMode.Color;
        env.BackgroundColor = fog;
        env.FogEnabled = true;
        env.FogLightColor = fog;
        env.FogDensity = density;
        env.FogSkyAffect = 0f;
    }

    // The sky box of the original (4 side strips of sky_side + sky_up) drawn by a shader on a sphere around the camera: the box faces are looked up by
    // direction and neighbouring faces / the top are cross-blended near the edges, so no box edge or texture border is visible; sampling is clamped
    // inside each strip (no bleeding between strips), directions below the horizon take the horizon row.
    const string SkyCode = @"shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled;
uniform sampler2D side : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D up : source_color, filter_linear_mipmap, repeat_disable;
uniform bool has_up = true;
uniform vec4 tint = vec4(1.0);
varying vec3 ld;
const float R = 1500.0; const float TOP = 1300.0; const float BOT = -150.0;
void vertex() { ld = VERTEX; }
vec4 wall(vec3 d, int f) {
    float t; float u;
    if (f == 0) { t = R / max(d.z, 1e-4); u = (d.x * t + R) / (2.0 * R); }
    else if (f == 1) { t = R / max(d.x, 1e-4); u = (R - d.z * t) / (2.0 * R); }
    else if (f == 2) { t = R / max(-d.z, 1e-4); u = (R - d.x * t) / (2.0 * R); }
    else { t = R / max(-d.x, 1e-4); u = (d.z * t + R) / (2.0 * R); }
    float y = d.y * t;
    vec2 ts = vec2(textureSize(side, 0));
    float v = (float(f) + clamp((TOP - y) / (TOP - BOT), 0.0, 1.0)) * 0.25;
    v = clamp(v, float(f) * 0.25 + 0.5 / ts.y, float(f + 1) * 0.25 - 0.5 / ts.y);
    return texture(side, vec2(clamp(u, 0.5 / ts.x, 1.0 - 0.5 / ts.x), v));
}
void fragment() {
    vec3 d = normalize(ld);
    d.y = max(d.y, -0.0995);
    d = normalize(d);
    float ax = abs(d.x), az = abs(d.z);
    vec4 sx = wall(d, d.x > 0.0 ? 1 : 3);
    vec4 sz = wall(d, d.z > 0.0 ? 0 : 2);
    vec4 c = mix(sx, sz, smoothstep(-0.2, 0.2, (az - ax) / max(az + ax, 1e-4)));
    float eh = (R * d.y / max(max(ax, az), 1e-4)) / TOP;
    if (has_up && eh > 0.75) {
        vec2 tp = d.xz * (TOP / max(d.y, 1e-4)) / (2.0 * R);
        vec4 tc = texture(up, clamp(UPUV, vec2(0.002), vec2(0.998)));
        c = mix(c, tc, smoothstep(0.75, 1.2, eh));
    }
    ALBEDO = c.rgb * tint.rgb;
//ALPHA//
}";
    static readonly Shader SkyShaderOpaque = new() { Code = SkyCode.Replace("//ALPHA//", "").Replace("UPUV", "(vec2(0.5) - tp)") };
    static readonly Shader SkyShaderFade = new() { Code = SkyCode.Replace("//ALPHA//", "    ALPHA = tint.a;").Replace("UPUV", "(vec2(0.5) - tp)") };

    static void SkyFading(ShaderMaterial m, bool fading) { m.Shader = fading ? SkyShaderFade : SkyShaderOpaque; m.RenderPriority = fading ? -9 : -10; }

    static Node3D? BuildSky(string dir, out System.Collections.Generic.List<ShaderMaterial> mats)
    {
        mats = new();
        var side = GameAssets.Texture(dir.TrimEnd((char)92) + (char)92 + "sky_side.tga");
        var up = GameAssets.Texture(dir.TrimEnd((char)92) + (char)92 + "sky_up.tga");
        if (side == null) return null;
        SkySide = side; SkyUp = up;
        var root = new Node3D { Name = "Sky" };
        var m = new ShaderMaterial { Shader = SkyShaderOpaque, RenderPriority = -10 };
        m.SetShaderParameter("side", side);
        m.SetShaderParameter("has_up", up != null);
        if (up != null) m.SetShaderParameter("up", up);
        mats.Add(m);
        root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 1400f, Height = 2800f, RadialSegments = 64, Rings = 32 }, MaterialOverride = m, Name = "SkyDome", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 16384f });
        return root;
    }
}
