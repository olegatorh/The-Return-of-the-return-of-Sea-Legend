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

        // ---- sky box
        if (skyEnt != null)
        {
            string dir = skyEnt.Root.Get("Dir")?.Value ?? "";
            if (dir.Length > 0 && (sky == null || dir != skyDir)) { sky?.QueueFree(); sky = BuildSky(dir); skyDir = dir; if (sky != null) AddChild(sky); }
        }
        else if (sky != null) { sky.QueueFree(); sky = null; skyDir = ""; }
        if (sky != null && cam != null) sky.GlobalPosition = cam.GlobalPosition;

        // ---- sea + fog
        if (seaEnt != null)
        {
            var root = seaEnt.Root;
            var water = Rgb(root.Find("Water.Color", false)?.Value, new Color(0.12f, 0.16f, 0.22f));
            var skyCol = Rgb(root.Find("Sky.Color", false)?.Value, Colors.White);
            var fog = Rgb(root.Find("Fog.Color", false)?.Value, new Color(0.86f, 0.86f, 0.9f));
            if (sea == null)
            {
                seaMat = new ShaderMaterial { Shader = new Shader { Code = SeaSurface.Shader } };
                // a fine grid near the camera carries the waves; the far sea is a flat plane under the fog
                sea = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(1000, 1000), SubdivideWidth = System.Environment.GetEnvironmentVariable("SEADOGS_SEAGRID") is { } sg ? int.Parse(sg) : 199, SubdivideDepth = System.Environment.GetEnvironmentVariable("SEADOGS_SEAGRID") is { } sg2 ? int.Parse(sg2) : 399 }, MaterialOverride = seaMat, Name = "Sea", CustomAabb = new Aabb(new Vector3(-500, -20, -500), new Vector3(1000, 40, 1000)) };
                farMat = new ShaderMaterial { Shader = seaMat.Shader };
                seaFar = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(12000, 12000) }, MaterialOverride = farMat, Name = "SeaFar" };
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
            SetEnvironment(fog, density * 1.2f);
        }
        else
        {
            if (sea != null) { sea.QueueFree(); sea = null; seaMat = null; }
            if (seaFar != null) { seaFar.QueueFree(); seaFar = null; farMat = null; }
            if (world != null) { world.QueueFree(); world = null; }
        }
    }


    static float Fl(Attr a, string path, float def) => float.TryParse(a.Find(path, false)?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
    static Texture2DArray? bumpTex; static string bumpDir = "";
    static ImageTexture? foamTex;
    public static ImageTexture? SkySide;
    float bumpFrame;

    /// <summary>Feeds the sea shader (SeaSurface.Shader) from the script's sea entity (colours, bump map, reflection) and the wave field.</summary>
    void SetSeaParams(ShaderMaterial m, Attr root, Interp it, bool far)
    {
        Color C(string p, Color d) => Rgb(root.Find(p, false)?.Value, d);
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

    static Node3D? BuildSky(string dir)
    {
        var side = GameAssets.Texture(dir.TrimEnd((char)92) + (char)92 + "sky_side.tga");
        var up = GameAssets.Texture(dir.TrimEnd((char)92) + (char)92 + "sky_up.tga");
        if (side == null) return null;
        SkySide = side;
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
        wall.MaterialOverride = SkyMaterial(side);
        root.AddChild(wall);
        if (up != null)
        {
            var quad = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(R * 2, R * 2), Orientation = PlaneMesh.OrientationEnum.Y }, Position = new Vector3(0, top, 0), Name = "SkyTop" };
            quad.MaterialOverride = SkyMaterial(up);
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
