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
void vertex() {
    vec3 b = COLOR.rgb;
    float y = fract(b.y - t * speed / height);
    vec3 p = vec3((b.x - 0.5) * 2.0 * radius, y * height - height * 0.35, (b.z - 0.5) * 2.0 * radius);
    p.xz += wind * (1.0 - y) * height * 0.15;
    vec3 world = p + cam;
    if (UV.x > 0.5) world += vec3(wind.x * 0.12, len, wind.y * 0.12);
    VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
}
void fragment() { ALBEDO = color.rgb; ALPHA = color.a * (UV.x > 0.5 ? 0.5 : 1.0); }";

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
    float time;
    readonly List<(Sprite3D sprite, float life, float total)> bolts = new();
    readonly ColorRect flash = new() { Color = new Color(0.85f, 0.9f, 1f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore };
    float flashAlpha;
    string boltTexture = "";
    int subX = 4, subY = 1;

    public WeatherFx()
    {
        Name = "WeatherFx";
        var layer = new CanvasLayer { Layer = 3 };
        flash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(flash);
        AddChild(layer);
    }

    static float F(Attr? a, string k, float def) => a?.Find(k, false)?.Value is { Length: > 0 } v ? Conv.ParseFloatPrefix(v) : def;

    public void Sync(Interp it, Camera3D? cam, float dt)
    {
        time += dt;
        if (System.Environment.GetEnvironmentVariable("SEADOGS_NOSUNFX") == null) SyncSunGlow(it, cam, dt);
        Entity? rainEnt = null, lightEnt = null;
        foreach (var e in it.Entities)
        {
            if (e.Deleted) continue;
            if (e.Class.Equals("rain", StringComparison.OrdinalIgnoreCase)) rainEnt = e;
            else if (e.Class.Equals("Lightning", StringComparison.OrdinalIgnoreCase)) lightEnt = e;
        }
        // ---- rain
        int drops = rainEnt == null ? 0 : (int)F(rainEnt.Root, "NumDrops", 0);
        if (drops <= 0 || cam == null) { if (rain != null) { rain.QueueFree(); rain = null; rainMat = null; rainDrops = 0; } }
        else
        {
            var r = rainEnt!.Root;
            int n = Math.Clamp(drops, 200, 14000);
            if (rain == null || n != rainDrops) { rain?.QueueFree(); rain = BuildRain(n); AddChild(rain); rainDrops = n; }
            var c = r.Find("Color", false)?.Value is { } cv && long.TryParse(cv, out long ci) ? new Color(((ci >> 16) & 255) / 128f, ((ci >> 8) & 255) / 128f, (ci & 255) / 128f, Math.Clamp(((ci >> 24) & 255) / 255f, 0.2f, 0.8f)) : new Color(0.7f, 0.75f, 0.85f, 0.4f);
            float wa = 0f, ws = 0f;
            if (it.Globals.TryGetValue("Weather", out var w) && w.Val.Attr is { } wa2) { wa = F(wa2, "Wind.Angle", 0f); ws = F(wa2, "Wind.Speed", 0f); }
            var wind = new Vector2(MathF.Sin(wa), -MathF.Cos(wa)) * Math.Clamp(ws / 12f, 0f, 1.5f);
            rainMat!.SetShaderParameter("cam", cam.GlobalPosition);
            rainMat.SetShaderParameter("t", time);
            rainMat.SetShaderParameter("speed", F(r, "Speed", 18f));
            rainMat.SetShaderParameter("height", Math.Max(5f, F(r, "Height", 30f)));
            rainMat.SetShaderParameter("radius", Math.Max(5f, F(r, "Radius", 30f)));
            rainMat.SetShaderParameter("len", Math.Max(0.4f, F(r, "DropLength", 2f)));
            rainMat.SetShaderParameter("wind", wind);
            rainMat.SetShaderParameter("color", new Vector4(c.R, c.G, c.B, c.A));
            rain.GlobalPosition = Vector3.Zero;
        }
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
        flashAlpha = Math.Max(0f, flashAlpha - dt * 2.2f);
        flash.Color = new Color(0.85f, 0.9f, 1f, flashAlpha);
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
        if (d.Length < 11 || Conv.ToInt(d[0]) != 54000) return V.Int(1);   // fmt llsflffffffsff
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
        flashAlpha = Math.Max(flashAlpha, Math.Clamp(0.55f - dist / 6000f, 0.12f, 0.55f));
        return V.Int(1);
    }
}
