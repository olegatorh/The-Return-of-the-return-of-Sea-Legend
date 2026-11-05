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
vec2 ripple(vec2 p, float tt) {
    // several crossing wave trains of different scale and direction: a cheap stand-in for a normal map
    vec2 g = vec2(0.0);
    g += vec2(cos(dot(p, vec2(0.83, 0.55)) * 0.9 + tt * 1.2), cos(dot(p, vec2(0.83, 0.55)) * 0.9 + tt * 1.2) * 0.0) * 0.5 * vec2(0.83, 0.55);
    g += cos(dot(p, vec2(-0.6, 0.8)) * 1.7 - tt * 1.5) * 0.35 * vec2(-0.6, 0.8);
    g += cos(dot(p, vec2(0.2, -0.98)) * 3.1 + tt * 2.1) * 0.22 * vec2(0.2, -0.98);
    g += cos(dot(p, vec2(-0.95, -0.3)) * 5.3 - tt * 2.6) * 0.14 * vec2(-0.95, -0.3);
    g += cos(dot(p, vec2(0.5, 0.86)) * 9.0 + tt * 3.4) * 0.08 * vec2(0.5, 0.86);
    return g;
}
void fragment() {
    vec3 V = normalize(CAMERA_POSITION_WORLD - wpos);
    float dist = length(CAMERA_POSITION_WORLD - wpos);
    float fade = 1.0 / (1.0 + dist * 0.012);                 // fine ripples vanish with distance (no shimmering)
    vec2 r = ripple(wpos.xz, t) * 0.11 * fade;
    vec3 n = normalize(vec3(-slope.x - r.x, 1.0, -slope.y - r.y));
    float ndv = clamp(dot(V, n), 0.0, 1.0);
    float f = 0.04 + 0.96 * pow(1.0 - ndv, 5.0);              // Schlick fresnel
    vec3 deep = water_color * 0.55;
    vec3 shallow = water_color * 1.35 + vec3(0.02, 0.05, 0.05);
    vec3 body = mix(deep, shallow, clamp(0.35 + (wpos.y) * 0.5 + n.x * 0.1, 0.0, 1.0));
    vec3 refl = mix(sky_color, vec3(1.0), 0.15);
    vec3 col = mix(body, refl, clamp(f * 1.1, 0.0, 0.9));
    vec3 L = normalize(vec3(-0.35, 0.55, 0.45));              // sun glint
    vec3 H = normalize(L + V);
    float spec = pow(clamp(dot(n, H), 0.0, 1.0), 180.0) * 1.2 * fade + pow(clamp(dot(n, H), 0.0, 1.0), 24.0) * 0.12;
    float crest = clamp((wpos.y - 0.4) * 0.5, 0.0, 0.3);      // light foam-ish lift on the crests
    ALBEDO = col + vec3(spec) * mix(vec3(1.0), sky_color, 0.4) + vec3(crest * 0.35);
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
                sea = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(1000, 1000), SubdivideWidth = System.Environment.GetEnvironmentVariable("SEADOGS_SEAGRID") is { } sg ? int.Parse(sg) : 199, SubdivideDepth = System.Environment.GetEnvironmentVariable("SEADOGS_SEAGRID") is { } sg2 ? int.Parse(sg2) : 399 }, MaterialOverride = seaMat, Name = "Sea", CustomAabb = new Aabb(new Vector3(-500, -20, -500), new Vector3(1000, 40, 1000)) };
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
                sea.GlobalPosition = new Vector3(MathF.Round(cam.GlobalPosition.X / 5f) * 5f, 0, MathF.Round(cam.GlobalPosition.Z / 5f) * 5f);
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
