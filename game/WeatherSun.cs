using System;
using System.Collections.Generic;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine class "SunGlow" (libs/weather/src/sun_glow.cpp, script Weather/WhrSun.c): a glow round the sun and a chain of lens flares on the line between the sun
/// and the screen centre. Attributes Glow.* (Dist, Size, Texture, Color, DecayTime), Flare.* (Dist, Scale, Texture, TexSizeX/Y) and Flares.f1... "dist,size,subtexture,0xRGB",
/// written by the weather and finished by isDone. The sun's place comes from the weather's Sun.HeightAngle / AzimuthAngle (sun_glow.cpp reads whv_sun_pos, which
/// weather.cpp builds from the same two angles). The glow is drawn without depth test; it fades out (Glow.DecayTime per second) while the island or sails hide the sun,
/// and the flares fade while the sun is outside the view. A quad pixel below the sea level is not drawn (the glow is cut at the horizon like DrawRect's bottom clip).
/// </summary>
public partial class WeatherFx
{
    sealed class FlareRt { public float Dist, Size, Alpha; public int Sub; public Color Col; public MeshInstance3D Node = null!; public ShaderMaterial Mat = null!; }

    public static Vector3 LastSunDir;      // the last sun direction (test hook SEADOGS_LOOKSUN)
    MeshInstance3D? sunGlowNode;
    ShaderMaterial? sunGlowMat;
    readonly List<FlareRt> flares = new();
    float glowAlpha, flareAlpha;
    bool sunBuilt;
    string sunKey = "";
    static QuadMesh? sunQuad;
    static Shader? sunShader;
    const string SunShaderCode = @"
shader_type spatial;
// fog_disabled: the quads sit 3500 m away, behind the fog end, and would be painted in the fog colour (the old 'white squares')
render_mode unshaded, blend_add, fog_disabled, depth_test_disabled, cull_disabled, depth_draw_never;
uniform sampler2D tex : source_color, filter_linear_mipmap, repeat_disable;
uniform vec4 color : source_color = vec4(1.0);
uniform vec2 uv_scale = vec2(1.0);
uniform vec2 uv_off = vec2(0.0);
uniform bool clip_sea = true;
varying float wy;
void vertex() {
    // a camera facing quad
    vec3 centre = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    vec3 right = INV_VIEW_MATRIX[0].xyz, up = INV_VIEW_MATRIX[1].xyz;
    float sx = length(MODEL_MATRIX[0].xyz), sy = length(MODEL_MATRIX[1].xyz);
    vec3 w = centre + right * VERTEX.x * sx + up * VERTEX.y * sy;
    wy = w.y;
    POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(w, 1.0);
}
void fragment() {
    if (clip_sea && wy < 0.0) discard;
    vec3 c = texture(tex, UV * uv_scale + uv_off).rgb;
    ALBEDO = c * color.rgb;
    ALPHA = 1.0;
}";

    static Color ArgbToColor(string? v, Color def)
    {
        if (v == null) return def;
        v = v.Trim();
        long n;
        try { n = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(v.Substring(2), 16) : long.Parse(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return def; }
        return new Color(((n >> 16) & 255) / 255f, ((n >> 8) & 255) / 255f, (n & 255) / 255f);
    }

    /// <summary>The sun direction (Godot space) from the weather: engine (cos az cos h, sin h, -sin az cos h).</summary>
    static Vector3 SunDirection(Attr weather)
    {
        float h = F(weather, "Sun.HeightAngle", 0.9f), az = F(weather, "Sun.AzimuthAngle", 0f);
        return new Vector3(MathF.Cos(az) * MathF.Cos(h), MathF.Sin(h), MathF.Sin(az) * MathF.Cos(h)).Normalized();
    }

    Vector3 smSun; Color smGc; bool smGcSet;
    void SyncSunGlow(Interp it, Camera3D? cam, float dt)
    {
        Entity? ent = null;
        foreach (var e in it.Entities)
            if (!e.Deleted && e.Class.Equals("SunGlow", StringComparison.OrdinalIgnoreCase)) { ent = e; break; }
        var wr = it.Globals.TryGetValue("Weather", out var w) ? w.Val.Attr : null;
        if (ent == null || cam == null || wr == null || ent.Root.Get("isDone") == null || !cam.IsInsideTree())
        {
            if (sunBuilt) { ClearSun(); }
            return;
        }
        var r = ent.Root;
        string key = (r.Find("Glow.Texture", false)?.Value ?? "") + "|" + (r.Find("Flare.Texture", false)?.Value ?? "") + "|" + (r.Get("Flares")?.Count ?? 0);
        if (!sunBuilt || key != sunKey) BuildSun(r, key);
        var sunTarget = SunDirection(wr);
        smSun = !sunBuilt || smSun == Vector3.Zero ? sunTarget : smSun.Lerp(sunTarget, 1f - MathF.Exp(-dt / Math.Max(0.05f, SeaWaves.Tau))).Normalized();      // the sun glides to the new record's position during a weather transition
        var sunDir = smSun;
        LastSunDir = sunDir;
        var cp = cam.GlobalPosition;
        float glowDist = F(r, "Glow.Dist", 3500f), glowSize = F(r, "Glow.Size", 1250f);
        float far = Math.Max(500f, cam.Far * 0.85f), d = Math.Min(glowDist, far), k = d / glowDist;      // the same angles at a distance the far plane lets through
        // is the sun hidden by the island? (SUN_TRACE layer); ships' sails would add the same
        bool visible = true;
        if (cam.GetWorld3D()?.DirectSpaceState is { } space)
        {
            var q = PhysicsRayQueryParameters3D.Create(cp, cp + sunDir * 6000f, 1u << 4);
            if (space.IntersectRay(q).Count > 0) visible = false;
        }
        float decay = Math.Max(0.1f, F(r, "Glow.DecayTime", 8f));
        glowAlpha = Math.Clamp(glowAlpha + (visible ? 1f : -1f) * dt * decay, 0f, 1f);
        var fwd = -cam.GlobalTransform.Basis.Z;
        bool inView = fwd.Dot(sunDir) > 0.35f;
        flareAlpha = Math.Clamp(flareAlpha + (inView && visible ? 1f : -1f) * dt * 8f, 0f, 1f);

        var gcT = ArgbToColor(r.Find("Glow.Color", false)?.Value, Colors.White);
        smGc = smGcSet ? smGc.Lerp(gcT, 1f - MathF.Exp(-dt / Math.Max(0.05f, SeaWaves.Tau))) : gcT; smGcSet = true;
        var gc = smGc;
        if (sunGlowNode != null && sunGlowMat != null)
        {
            sunGlowNode.Visible = r.Find("Glow.Texture", false) != null && glowAlpha > 0.01f;
            sunGlowNode.GlobalPosition = cp + sunDir * d;
            sunGlowNode.Scale = new Vector3(glowSize * k, glowSize * k, 1f);
            sunGlowMat.SetShaderParameter("color", new Color(gc.R * glowAlpha, gc.G * glowAlpha, gc.B * glowAlpha));
        }
        // flares: from the sun along the line to the centre of the view, f.Dist = position on the line (1 = at the sun)
        float flareDist = F(r, "Flare.Dist", 3500f), scale = F(r, "Flare.Scale", 1f);
        var centre = fwd * (flareDist * 0.5f);
        var sunRel = sunDir * flareDist;
        var delta = flareDist * (centre - sunRel).Normalized();
        foreach (var f in flares)
        {
            f.Node.Visible = flareAlpha > 0.01f;
            if (!f.Node.Visible) continue;
            var rel = sunRel + delta * (1f - f.Dist);
            var dir = rel.Normalized();
            f.Node.GlobalPosition = cp + dir * d;
            f.Node.Scale = new Vector3(f.Size * scale * k, f.Size * scale * k, 1f);
            float fa = flareAlpha * glowAlpha;
            var fc = new Color(Math.Min(1f, f.Col.R * fa * 4f), Math.Min(1f, f.Col.G * fa * 4f), Math.Min(1f, f.Col.B * fa * 4f));
            f.Mat.SetShaderParameter("color", fc);
            if (System.Environment.GetEnvironmentVariable("SEADOGS_SUNDBG") != null && (int)(time * 10) % 50 == 0) GD.Print($"[sun] flare {f.Sub} dist {f.Dist} size {f.Size} col {fc} fa {fa} glowA {glowAlpha} flareA {flareAlpha}");
        }
    }

    void BuildSun(Attr r, string key)
    {
        ClearSun();
        sunKey = key; sunBuilt = true;
        sunQuad ??= new QuadMesh { Size = Vector2.One };
        sunShader ??= new Shader { Code = SunShaderCode };
        if (r.Find("Glow.Texture", false)?.Value is { Length: > 0 } gt)
        {
            sunGlowMat = new ShaderMaterial { Shader = sunShader, RenderPriority = 100 };
            sunGlowMat.SetShaderParameter("tex", GameAssets.Texture(gt));
            sunGlowNode = new MeshInstance3D { Mesh = sunQuad, MaterialOverride = sunGlowMat, Name = "SunGlow", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 16384f };
            AddChild(sunGlowNode);
        }
        if (r.Find("Flare.Texture", false)?.Value is { Length: > 0 } ft && r.Get("Flares") is { } fl)
        {
            int tx = Math.Max(1, (int)F(r, "Flare.TexSizeX", 2)), ty = Math.Max(1, (int)F(r, "Flare.TexSizeY", 2));
            var tex = GameAssets.Texture(ft);
            foreach (var a in fl.Children)
            {
                var p = (a.Value ?? "").Split(',');
                if (p.Length < 4) continue;
                var f = new FlareRt
                {
                    Dist = (float)Conv.ParseFloatPrefix(p[0]), Size = (float)Conv.ParseFloatPrefix(p[1]), Sub = (int)Conv.ParseFloatPrefix(p[2]),
                    Col = ArgbToColor(p[3], new Color(0.06f, 0.06f, 0.06f)),
                };
                var mat = new ShaderMaterial { Shader = sunShader, RenderPriority = 101 };
                mat.SetShaderParameter("tex", tex);
                mat.SetShaderParameter("clip_sea", false);
                mat.SetShaderParameter("uv_scale", new Vector2(1f / tx, 1f / ty));
                mat.SetShaderParameter("uv_off", new Vector2((f.Sub % tx) / (float)tx, (f.Sub / tx) / (float)ty));
                f.Node = new MeshInstance3D { Mesh = sunQuad, MaterialOverride = mat, Name = "SunFlare", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 16384f };
                f.Mat = mat;
                AddChild(f.Node);
                flares.Add(f);
            }
        }
        GD.Print($"[weather] sun glow: glow={(sunGlowNode != null)} flares={flares.Count}");
    }

    void ClearSun()
    {
        sunGlowNode?.QueueFree(); sunGlowNode = null; sunGlowMat = null;
        foreach (var f in flares) f.Node.QueueFree();
        flares.Clear();
        sunBuilt = false; glowAlpha = 0f; flareAlpha = 0f; smSun = Vector3.Zero; smGcSet = false;
    }
}
