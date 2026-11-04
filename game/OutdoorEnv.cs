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
    ShaderMaterial? seaMat, farMat;
    MeshInstance3D? seaFar;
    WorldEnvironment? world;
    float time;

    static Color Rgb(string? v, Color def)
    {
        if (!int.TryParse(v, out int c)) return def;
        return new Color(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f);
    }

    static readonly string SeaShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled;
uniform vec3 water_color;
uniform vec3 sky_color;
uniform float t;
uniform float wave_k[3];
uniform float wave_w[3];
uniform float wave_a[3];
uniform vec2 wave_dir[3];
varying vec3 wpos;
varying vec2 slope;
void vertex() {
    vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float h = 0.0; vec2 s = vec2(0.0);
    for (int i = 0; i < 3; i++) {
        float ph = wave_k[i] * dot(wave_dir[i], w.xz) - wave_w[i] * t;
        h += wave_a[i] * sin(ph);
        s += wave_a[i] * wave_k[i] * cos(ph) * wave_dir[i];
    }
    VERTEX.y += h;
    slope = s;
    wpos = w + vec3(0.0, h, 0.0);
}
void fragment() {
    vec3 V = normalize(CAMERA_POSITION_WORLD - wpos);
    float a = wpos.x * 0.7 + t * 1.3;
    float b = wpos.z * 0.9 - t * 1.1;
    float c = (wpos.x + wpos.z) * 0.37 + t * 0.7;
    vec3 n = normalize(vec3(-slope.x + cos(a) * 0.04 + cos(c) * 0.02, 1.0, -slope.y + cos(b) * 0.04 + cos(c) * 0.02));
    float f = pow(1.0 - clamp(dot(V, n), 0.0, 1.0), 3.0);
    float w = (sin(a) + sin(b) + sin(c)) * 0.010;
    float crest = clamp((wpos.y) * 0.5, 0.0, 0.25);
    ALBEDO = mix(water_color, sky_color, clamp(f * 0.85 + 0.12, 0.0, 1.0)) + vec3(w + crest * 0.25);
}";

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
                seaMat = new ShaderMaterial { Shader = new Shader { Code = SeaShader } };
                // a fine grid near the camera carries the waves; the far sea is a flat plane under the fog
                sea = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(1600, 1600), SubdivideWidth = 399, SubdivideDepth = 399 }, MaterialOverride = seaMat, Name = "Sea", CustomAabb = new Aabb(new Vector3(-800, -20, -800), new Vector3(1600, 40, 1600)) };
                farMat = new ShaderMaterial { Shader = seaMat.Shader };
                seaFar = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(12000, 12000) }, MaterialOverride = farMat, Name = "SeaFar" };
                AddChild(seaFar);
                AddChild(sea);
            }
            seaMat!.SetShaderParameter("water_color", new Vector3(water.R * 2.2f, water.G * 2.2f, water.B * 2.2f));
            seaMat.SetShaderParameter("sky_color", new Vector3(fog.R * 0.8f, fog.G * 0.8f, fog.B * 0.85f));
            seaMat.SetShaderParameter("t", time);
            SeaWaves.Time = time; SeaWaves.Configure(root);
            seaMat.SetShaderParameter("wave_k", (float[])SeaWaves.K.Clone()); seaMat.SetShaderParameter("wave_w", (float[])SeaWaves.W.Clone()); seaMat.SetShaderParameter("wave_a", (float[])SeaWaves.A.Clone()); seaMat.SetShaderParameter("wave_dir", (Vector2[])SeaWaves.Dir.Clone());
            if (cam != null)
            {
                // the wave grid follows the camera in whole 4 m steps (the waves are a function of the world position, so the surface does not swim)
                sea.GlobalPosition = new Vector3(MathF.Round(cam.GlobalPosition.X / 4f) * 4f, 0, MathF.Round(cam.GlobalPosition.Z / 4f) * 4f);
                if (seaFar != null) seaFar.GlobalPosition = new Vector3(cam.GlobalPosition.X, -0.7f, cam.GlobalPosition.Z);
            }
            if (farMat != null)
            {
                foreach (var prm in new[] { "water_color", "sky_color", "t" }) farMat.SetShaderParameter(prm, seaMat.GetShaderParameter(prm));
                farMat.SetShaderParameter("wave_k", (float[])SeaWaves.K.Clone()); farMat.SetShaderParameter("wave_w", (float[])SeaWaves.W.Clone());
                farMat.SetShaderParameter("wave_a", new float[3]); farMat.SetShaderParameter("wave_dir", (Vector2[])SeaWaves.Dir.Clone());
            }
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
