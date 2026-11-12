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
                var ns = BuildSky(dir, out var mats);
                FreeOldSky();
                var prev = sky;
                if (ns == null) { prev?.QueueFree(); sky = null; skyDir = ""; skyMats = new(); }
                else
                {
                    if (prev != null) { skyOld = prev; skyFade = 0f; foreach (var m in mats) { m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.RenderPriority = -9; } }
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
        if (skyOld != null) { skyFade += dt / 6f; if (skyFade >= 1f) { skyFade = 1f; FreeOldSky(); foreach (var m in skyMats) { m.Transparency = BaseMaterial3D.TransparencyEnum.Disabled; m.RenderPriority = -10; } } }
        foreach (var m in skyMats) m.AlbedoColor = new Color(skyTint.R, skyTint.G, skyTint.B, skyOld != null ? skyFade : 1f);
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
                seaMat = new ShaderMaterial { Shader = new Shader { Code = SeaSurface.Shader } };
                // a fine grid near the camera carries the waves; the far sea is a flat plane under the fog
                sea = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(1000, 1000), SubdivideWidth = System.Environment.GetEnvironmentVariable("SEADOGS_SEAGRID") is { } sg ? int.Parse(sg) : 199, SubdivideDepth = System.Environment.GetEnvironmentVariable("SEADOGS_SEAGRID") is { } sg2 ? int.Parse(sg2) : 399 }, MaterialOverride = seaMat, Name = "Sea", CustomAabb = new Aabb(new Vector3(-500, -20, -500), new Vector3(1000, 40, 1000)) };
                farMat = new ShaderMaterial { Shader = seaMat.Shader };
                seaFar = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(26000, 26000) }, MaterialOverride = farMat, Name = "SeaFar" };
                AddChild(seaFar);
                AddChild(sea);
            }
            seaMat!.SetShaderParameter("t", time);
            SeaWaves.Time = time; SeaWaves.Configure(root);
            SetSeaParams(seaMat, root, it, false);
            if (cam != null)
            {
                // the wave grid follows the camera in whole 5 m steps (the waves are a function of the world position, so the surface does not swim)
                sea.GlobalPosition = new Vector3(MathF.Round(cam.GlobalPosition.X / 5f) * 5f, 0, MathF.Round(cam.GlobalPosition.Z / 5f) * 5f);
                if (seaFar != null) seaFar.GlobalPosition = new Vector3(cam.GlobalPosition.X, -0.4f, cam.GlobalPosition.Z);
            }
            if (farMat != null) SetSeaParams(farMat, root, it, true);
            float density = root.Find("Fog.Density", false)?.Value is { } d && float.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dv) ? dv : 0.001f;
            smDens = sea == null || smDens < 0f ? density : smDens + (density - smDens) * (1f - MathF.Exp(-dt / 2f));
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
        if (!smooth.TryGetValue(key, out var c)) c = target; else c = c.Lerp(target, 1f - MathF.Exp(-smDt / 2f));
        smooth[key] = c;
        return c;
    }
    Vector3 smSunN = Vector3.Zero, smSunF = Vector3.Zero;

    // ---- sky: old folder fading out, texture factor, rotation
    Node3D? skyOld;
    float skyFade = 1f, skyRot, skyYaw;
    Color skyTint = Colors.White;
    System.Collections.Generic.List<StandardMaterial3D> skyMats = new();
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
        reflKey = key;
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
        if (far) { smSunF = smSunF == Vector3.Zero ? sunDir : smSunF.Lerp(sunDir, 1f - MathF.Exp(-smDt / 2f)); sunDir = smSunF; }
        else { smSunN = smSunN == Vector3.Zero ? sunDir : smSunN.Lerp(sunDir, 1f - MathF.Exp(-smDt / 2f)); sunDir = smSunN; }
        m.SetShaderParameter("water_color", water);
        m.SetShaderParameter("sky_color", skyc);
        m.SetShaderParameter("fog_color", fog);
        m.SetShaderParameter("pena_color", new Color(Math.Min(1f, pena.R * 2.4f + fog.R * 0.8f + 0.12f), Math.Min(1f, pena.G * 2.4f + fog.G * 0.8f + 0.12f), Math.Min(1f, pena.B * 2.4f + fog.B * 0.8f + 0.12f)));
        m.SetShaderParameter("sun_color", sunc);
        float rp = Fl(root, "Road.Power", 1f);
        var rc = new Color((road.R + road2.R) * 0.5f, (road.G + road2.G) * 0.5f, (road.B + road2.B) * 0.5f);
        m.SetShaderParameter("road_color", rc);
        m.SetShaderParameter("road_power", rp);
        m.SetShaderParameter("sun_dir", sunDir.Normalized());
        m.SetShaderParameter("fog_density", Fl(root, "Fog.SeaDensity", 0.002f));
        m.SetShaderParameter("refl_k", Fl(root, "WaterReflection", 0.7f));
        m.SetShaderParameter("fresnel_k", Fl(root, "FrenelCoefficient", 0.7f));
        m.SetShaderParameter("t", time);
        m.SetShaderParameter("disp", far ? 0f : 1f);
        float amp = SeaWaves.TotalAmp;
        m.SetShaderParameter("amp_total", amp);
        m.SetShaderParameter("chop", Math.Clamp(0.35f + amp * 0.25f, 0.35f, 0.95f));
        m.SetShaderParameter("foam_gain", Math.Clamp((amp - 0.45f) / 1.6f, 0f, 1f));
        string bd = root.Find("Bump.Dir", false)?.Value ?? "";
        if (bd.Length > 0 && (bumpTex == null || bd != bumpDir)) { bumpTex = SeaSurface.LoadBump(bd); bumpDir = bd; }
        foamTex ??= GameAssets.Texture("pena.tga");
        float tile = Fl(root, "Bump.Tile", 0.05f), spd = Fl(root, "Bump.AnimSpeed", 15f);
        if (!far) bumpFrame += (float)GetProcessDeltaTime() * spd * 0.5f;
        m.SetShaderParameter("has_bump", bumpTex != null);
        if (bumpTex != null) m.SetShaderParameter("bump", bumpTex);
        m.SetShaderParameter("bump_tile", tile);
        m.SetShaderParameter("bump_ang", Fl(root, "Bump.Ang", 0f));
        m.SetShaderParameter("bump_speed", Fl(root, "Bump.Speed", 0.01f) * 6f);
        m.SetShaderParameter("bump_scale", Fl(root, "Bump.Scale", 2f));
        m.SetShaderParameter("bump_frame", bumpFrame);
        m.SetShaderParameter("has_sky", SkySide != null);
        if (SkySide != null) m.SetShaderParameter("sky_tex", SkySide);
        if (foamTex != null) m.SetShaderParameter("foam_tex", foamTex);
        m.SetShaderParameter("wave_k", (float[])SeaWaves.K.Clone()); m.SetShaderParameter("wave_w", (float[])SeaWaves.W.Clone());
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

    static Node3D? BuildSky(string dir, out System.Collections.Generic.List<StandardMaterial3D> mats)
    {
        mats = new();
        var side = GameAssets.Texture(dir.TrimEnd((char)92) + (char)92 + "sky_side.tga");
        var up = GameAssets.Texture(dir.TrimEnd((char)92) + (char)92 + "sky_up.tga");
        if (side == null) return null;
        SkySide = side; SkyUp = up;
        const float R = 1500f, top = 1300f, bottom = -150f;
        var root = new Node3D { Name = "Sky" };
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        // four walls, each uses one 512x256 strip of the side texture (the strip's bottom is the horizon)
        var corners = new[] { new Vector3(-R, 0, R), new Vector3(R, 0, R), new Vector3(R, 0, -R), new Vector3(-R, 0, -R) };
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i]; var b = corners[(i + 1) % 4];
            float v0 = i * 0.25f, v1 = (i + 1) * 0.25f;
            void V(Vector3 p, float y, Vector2 uv) { st.SetUV(uv); st.AddVertex(new Vector3(p.X, y, p.Z)); }
            V(a, top, new Vector2(0, v0)); V(b, top, new Vector2(1, v0)); V(b, bottom, new Vector2(1, v1));
            V(a, top, new Vector2(0, v0)); V(b, bottom, new Vector2(1, v1)); V(a, bottom, new Vector2(0, v1));
        }
        var wall = new MeshInstance3D { Mesh = st.Commit(), Name = "SkySides" };
        var wallMat = SkyMaterial(side); mats.Add(wallMat); wall.MaterialOverride = wallMat;
        root.AddChild(wall);
        if (up != null)
        {
            var quad = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(R * 2, R * 2), Orientation = PlaneMesh.OrientationEnum.Y }, Position = new Vector3(0, top, 0), Name = "SkyTop" };
            var upMat = SkyMaterial(up); mats.Add(upMat); quad.MaterialOverride = upMat;
            root.AddChild(quad);
        }
        return root;
    }

    static StandardMaterial3D SkyMaterial(Texture2D tex) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        AlbedoTexture = tex,
        DisableFog = true,
        NoDepthTest = false,
        RenderPriority = -10,
    };
}
