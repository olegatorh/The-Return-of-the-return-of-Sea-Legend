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
    ShaderMaterial? seaMat;
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
varying vec3 wpos;
void vertex() { wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
void fragment() {
    vec3 V = normalize(CAMERA_POSITION_WORLD - wpos);
    float a = wpos.x * 0.7 + t * 1.3;
    float b = wpos.z * 0.9 - t * 1.1;
    float c = (wpos.x + wpos.z) * 0.37 + t * 0.7;
    vec3 n = normalize(vec3(cos(a) * 0.06 + cos(c) * 0.03, 1.0, cos(b) * 0.06 + cos(c) * 0.03));
    float f = pow(1.0 - clamp(dot(V, n), 0.0, 1.0), 3.0);
    float w = (sin(a) + sin(b) + sin(c)) * 0.012;
    ALBEDO = mix(water_color, sky_color, clamp(f * 0.85 + 0.12, 0.0, 1.0)) + vec3(w);
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
                sea = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(8000, 8000) }, MaterialOverride = seaMat, Name = "Sea" };
                AddChild(sea);
            }
            seaMat!.SetShaderParameter("water_color", new Vector3(water.R * 2.2f, water.G * 2.2f, water.B * 2.2f));
            seaMat.SetShaderParameter("sky_color", new Vector3(fog.R * 0.8f, fog.G * 0.8f, fog.B * 0.85f));
            seaMat.SetShaderParameter("t", time);
            if (cam != null) sea.GlobalPosition = new Vector3(cam.GlobalPosition.X, 0, cam.GlobalPosition.Z);
            float density = root.Find("Fog.Density", false)?.Value is { } d && float.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dv) ? dv : 0.001f;
            SetEnvironment(fog, density * 1.2f);
        }
        else
        {
            if (sea != null) { sea.QueueFree(); sea = null; seaMat = null; }
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
