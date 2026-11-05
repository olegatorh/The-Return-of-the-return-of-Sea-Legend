using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine's "Lights" class: point light types from RESOURCE\INI\lights.ini (colour, attenuation Att = 1/(att0 + d*att1 + d*d*att2), range,
/// flicker, corona sprite) and the lights the location script adds with MSG_LOCATION_ADD_LIGHT (locators of the groups listed in
/// loc.models.day/night.lights: candles, torches, town lamps (fonar) at night...).
/// Like the original, the dynamic lights shade the characters only (the static geometry has the light baked into its .col vertex colours):
/// every character picks the strongest lights near it (<see cref="Pick"/>) and lights itself in <see cref="CharShading"/>.
/// The coronas are additive billboards at the light positions.
/// </summary>
public static class LocationLights
{
    public sealed class LightType
    {
        public string Name = "";
        public Color Color;
        public float Att0 = 1, Att1, Att2, Range = 10, Flicker, FlickerSlow, Freq, FreqSlow, CoronaRange, CoronaSize;
        public string? Corona;
    }

    public sealed class PointLight
    {
        public LightType Type = null!;
        public Vector3 Pos;                 // Godot space
        public float Fast, SlowFrom, SlowTo, SlowT, FastT;
        public float K = 1f;                // current flicker factor
        public Node? Owner;
        public MeshInstance3D? CoronaNode;
        public StandardMaterial3D? CoronaMat;
    }

    static Dictionary<string, LightType>? types;
    public static readonly List<PointLight> Lights = new();
    static readonly Random rnd = new(7);

    // global light of the scene (weather sun + ambient) for the characters
    public static Color Ambient = new(0.9f, 0.9f, 0.9f);
    public static Color SunColor = new(0, 0, 0);
    public static Vector3 SunDir = Vector3.Up;
    public static int Version;          // bumped whenever Ambient/Sun change

    static float F(IniFile.Section s, string k, float def)
    {
        var v = s.Get(k);
        if (v == null) return def;
        int sc = v.IndexOf(';');
        if (sc >= 0) v = v.Substring(0, sc);
        return float.TryParse(v.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;
    }

    public static LightType? Type(string name)
    {
        if (types == null)
        {
            types = new(StringComparer.OrdinalIgnoreCase);
            string path = GameAssets.Resource("INI\\lights.ini");
            if (File.Exists(path))
                foreach (var s in IniFile.Load(path).Sections.Values)
                {
                    string? cor = s.Get("corona");
                    if (cor != null) { int sc = cor.IndexOf(';'); if (sc >= 0) cor = cor.Substring(0, sc); cor = cor.Trim(); }
                    types[s.Name] = new LightType
                    {
                        Name = s.Name,
                        Color = new Color(F(s, "r", 1), F(s, "g", 1), F(s, "b", 1)),
                        Att0 = F(s, "att0", 1), Att1 = F(s, "att1", 0), Att2 = F(s, "att2", 0), Range = F(s, "range", 10),
                        Flicker = F(s, "flicker", 0), FlickerSlow = F(s, "flickerSlow", 0), Freq = F(s, "freq", 0), FreqSlow = F(s, "freqSlow", 0),
                        Corona = string.IsNullOrEmpty(cor) ? null : cor, CoronaRange = F(s, "coronaRange", 0), CoronaSize = F(s, "coronaSize", 0),
                    };
                }
            GD.Print($"[lights] {types.Count} light types from lights.ini");
        }
        return types.TryGetValue(name, out var t) ? t : null;
    }

    /// <summary>MSG_LOCATION_ADD_LIGHT: a light of a lights.ini type at a D3D position; the corona (if the type has one) goes under <paramref name="parent"/>.</summary>
    public static bool Add(Node3D parent, string type, float x, float y, float z)
    {
        var t = Type(type);
        if (t == null) { GD.Print("[lights] unknown light type " + type); return false; }
        var l = new PointLight { Type = t, Pos = new Vector3(x, y, -z), Owner = parent, SlowFrom = (float)rnd.NextDouble(), SlowTo = (float)rnd.NextDouble(), SlowT = (float)rnd.NextDouble() };
        if (t.Corona != null && t.CoronaSize > 0 && GameAssets.FindTexture(t.Corona, null) is { } rel && GameAssets.Texture(rel) is { } tex)
        {
            l.CoronaMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlbedoTexture = tex,
                DisableFog = false,
                NoDepthTest = false,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                RenderPriority = 5,
            };
            l.CoronaNode = new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(t.CoronaSize * 2f, t.CoronaSize * 2f) },
                MaterialOverride = l.CoronaMat,
                Position = l.Pos,
                Name = "Corona_" + t.Name,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEnd = t.CoronaRange > 0 ? t.CoronaRange : 30f,
                VisibilityRangeEndMargin = Math.Max(t.CoronaRange * 0.3f, 1f),
                VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
            };
            parent.AddChild(l.CoronaNode);
        }
        Lights.Add(l);
        if (System.Environment.GetEnvironmentVariable("SEADOGS_LIGHTLOG") != null) GD.Print($"[lights] {type} at ({x:0.0},{y:0.0},{z:0.0}) corona={l.CoronaNode != null} total={Lights.Count}");
        return true;
    }

    /// <summary>Forget the lights of a location that goes away.</summary>
    public static void RemoveOwned(Node owner) => Lights.RemoveAll(l => l.Owner == owner);

    /// <summary>Flicker: a fast random amplitude (no interpolation, freq Hz) plus a slow interpolated one (freqSlow Hz).</summary>
    public static void Tick(float dt, Vector3 cam)
    {
        foreach (var l in Lights)
        {
            var t = l.Type;
            if (t.Flicker > 0 && t.Freq > 0)
            {
                l.FastT += dt;
                if (l.FastT > 1f / t.Freq) { l.FastT = 0; l.Fast = t.Flicker * (float)rnd.NextDouble(); }
            }
            float slow = 0;
            if (t.FlickerSlow > 0 && t.FreqSlow > 0)
            {
                l.SlowT += dt * t.FreqSlow;
                if (l.SlowT >= 1f) { l.SlowT -= 1f; l.SlowFrom = l.SlowTo; l.SlowTo = (float)rnd.NextDouble(); }
                slow = t.FlickerSlow * Mathf.Lerp(l.SlowFrom, l.SlowTo, l.SlowT);
            }
            l.K = Math.Clamp(1f - l.Fast - slow, 0f, 1f);
            if (l.CoronaMat != null)
            {
                // the corona fades out towards its range and flickers with the light
                float d = l.Pos.DistanceTo(cam);
                float r = t.CoronaRange > 0 ? t.CoronaRange : 30f;
                float a = Math.Clamp(1f - d / r, 0f, 1f);
                float k = 0.55f + 0.45f * l.K;
                l.CoronaMat.AlbedoColor = new Color(k, k, k, a);
            }
        }
    }

    /// <summary>The strongest lights at a point (up to <paramref name="max"/>), strongest first.</summary>
    public static int Pick(Vector3 p, PointLight[] dst, float[] tmp)
    {
        int n = 0;
        foreach (var l in Lights)
        {
            var t = l.Type;
            float d = l.Pos.DistanceTo(p);
            if (d > t.Range + 0.5f) continue;
            float lum = (t.Color.R + t.Color.G + t.Color.B) / Math.Max(t.Att0 + d * t.Att1 + d * d * t.Att2, 0.01f);
            if (lum < 0.01f) continue;
            // insert sorted
            int i = n < dst.Length ? n++ : dst.Length;
            if (i == dst.Length) { if (lum <= tmp[dst.Length - 1]) continue; i = dst.Length - 1; }
            while (i > 0 && tmp[i - 1] < lum) { dst[i] = dst[i - 1]; tmp[i] = tmp[i - 1]; i--; }
            dst[i] = l; tmp[i] = lum;
        }
        return n;
    }

    static Color Rgb(string? v, Color def)
    {
        if (!long.TryParse(v, out long c)) return def;
        return new Color(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f);
    }

    static float Fl(string? v, float def)
        => float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;

    /// <summary>
    /// The scene light for the characters, from the "Weather" entity (Sun.Color, Sun.Ambient, Sun.HeightAngle/AzimuthAngle) like the original's
    /// D3D sun; without a weather (interiors: loc.environment.weather is not "true") a neutral indoor ambient.
    /// </summary>
    public static void SyncWeather(Interp it, bool indoor)
    {
        Color amb, sun; Vector3 dir;
        Entity? w = null;
        if (!indoor)
            foreach (var e in it.Entities)
                if (!e.Deleted && e.Class.Equals("Weather", StringComparison.OrdinalIgnoreCase)) { w = e; break; }
        if (w != null)
        {
            var r = w.Root;
            amb = Rgb(r.Find("Sun.Ambient", false)?.Value, new Color(0.4f, 0.4f, 0.4f));
            sun = Rgb(r.Find("Sun.Color", false)?.Value, new Color(0.6f, 0.6f, 0.6f));
            float h = Fl(r.Find("Sun.HeightAngle", false)?.Value, 0.8f), az = Fl(r.Find("Sun.AzimuthAngle", false)?.Value, 0f);
            dir = new Vector3(MathF.Cos(az) * MathF.Cos(h), MathF.Sin(h), -MathF.Sin(az) * MathF.Cos(h)).Normalized();
        }
        else if (indoor) { amb = new Color(0.42f, 0.4f, 0.38f); sun = new Color(0.18f, 0.17f, 0.15f); dir = new Vector3(0.3f, 0.9f, 0.3f).Normalized(); }
        else { amb = new Color(0.9f, 0.9f, 0.9f); sun = new Color(0, 0, 0); dir = Vector3.Up; }
        if (amb != Ambient || sun != SunColor || dir != SunDir) { Ambient = amb; SunColor = sun; SunDir = dir; Version++; }
    }
}

/// <summary>
/// Character material: texture x (ambient + sun * N.L + sum of the nearest point lights with the original attenuation) - an unshaded shader that does
/// the original's per-character lighting itself (the gl_compatibility renderer's own lights would be limited and attenuate differently).
/// </summary>
public static class CharShading
{
    public const int MaxLights = 4;
    public static float Gain = 1.35f;       // the original's characters are brighter than a plain ambient + sun sum (modulate)

    static Shader? shader;
    static readonly string Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled;
uniform sampler2D tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_tex = true;
uniform float scissor = -1.0;
uniform vec3 tint = vec3(1.0);
uniform vec3 ambient = vec3(0.9);
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
uniform vec3 sun_color = vec3(0.0);
uniform int nl = 0;
uniform vec4 lpos[4];
uniform vec4 lcol[4];
uniform vec4 latt[4];
varying vec3 wpos;
varying vec3 wn;
void vertex() {
    wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    wn = mat3(MODEL_MATRIX) * NORMAL;
}
void fragment() {
    vec4 t = has_tex ? texture(tex, UV) : vec4(1.0);
    if (scissor >= 0.0 && t.a < scissor) discard;
    vec3 n = normalize(wn);
    vec3 lit = ambient + sun_color * max(dot(n, sun_dir), 0.0);
    for (int i = 0; i < 4; i++) {
        if (i >= nl) break;
        vec3 d = lpos[i].xyz - wpos;
        float dist = max(length(d), 0.05);
        if (dist < lpos[i].w) {
            float att = 1.0 / max(latt[i].x + dist * latt[i].y + dist * dist * latt[i].z, 0.05);
            lit += lcol[i].rgb * att * (0.35 + 0.65 * max(dot(n, d / dist), 0.0));
        }
    }
    ALBEDO = t.rgb * tint * min(lit, vec3(1.8));
}";

    public static ShaderMaterial Make(StandardMaterial3D src)
    {
        shader ??= new Shader { Code = Code };
        var m = new ShaderMaterial { Shader = shader };
        if (src.AlbedoTexture != null) m.SetShaderParameter("tex", src.AlbedoTexture);
        else m.SetShaderParameter("has_tex", false);
        if (src.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor) m.SetShaderParameter("scissor", 0.5f);
        return m;
    }

    /// <summary>Per-rig lighting state: updates the uniforms of the rig's materials.</summary>
    public sealed class State
    {
        readonly List<ShaderMaterial> mats = new();
        int version = -1;
        int lastN = -1;
        readonly LocationLights.PointLight[] pick = new LocationLights.PointLight[MaxLights];
        readonly float[] tmp = new float[MaxLights];
        readonly Vector4[] pos = new Vector4[MaxLights], col = new Vector4[MaxLights], att = new Vector4[MaxLights];
        public void Add(ShaderMaterial m) => mats.Add(m);
        static readonly bool log = System.Environment.GetEnvironmentVariable("SEADOGS_LIGHTLOG") != null;

        public void Update(Vector3 at)
        {
            if (version != LocationLights.Version)
            {
                version = LocationLights.Version;
                var a = LocationLights.Ambient * Gain; var s = LocationLights.SunColor * Gain;
                foreach (var m in mats)
                {
                    m.SetShaderParameter("ambient", new Vector3(a.R, a.G, a.B));
                    m.SetShaderParameter("sun_color", new Vector3(s.R, s.G, s.B));
                    m.SetShaderParameter("sun_dir", LocationLights.SunDir);
                }
            }
            int n = LocationLights.Lights.Count == 0 ? 0 : LocationLights.Pick(at + new Vector3(0, 1f, 0), pick, tmp);
            if (log && Engine.GetProcessFrames() % 300 == 0)
                GD.Print($"[lights] rig at {at} picks {n}: " + string.Join(", ", System.Linq.Enumerable.Range(0, n).Select(i => $"{pick[i].Type.Name} d={pick[i].Pos.DistanceTo(at):0.0} k={pick[i].K:0.00}")));
            if (n == 0 && lastN == 0) return;
            for (int i = 0; i < n; i++)
            {
                var l = pick[i]; var t = l.Type;
                pos[i] = new Vector4(l.Pos.X, l.Pos.Y, l.Pos.Z, t.Range);
                col[i] = new Vector4(t.Color.R * l.K, t.Color.G * l.K, t.Color.B * l.K, 0);
                att[i] = new Vector4(t.Att0, t.Att1, t.Att2, 0);
            }
            foreach (var m in mats)
            {
                m.SetShaderParameter("nl", n);
                if (n > 0) { m.SetShaderParameter("lpos", pos); m.SetShaderParameter("lcol", col); m.SetShaderParameter("latt", att); }
            }
            lastN = n;
        }
    }
}
