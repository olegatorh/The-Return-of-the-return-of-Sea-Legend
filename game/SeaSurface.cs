using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace SeaDogs;

/// <summary>
/// The look of the water: the shader of the sea mesh and the animated bump map of the original (RESOURCE\Textures\Weather\Sea\BUMP_G3\bump.zap).
/// bump.zap = int32 frames (31), width, height, mip levels (4) and then per frame the mip chain of a V8U8 map (two signed bytes du, dv per texel).
/// The sea object's Bump.Tile / Ang / Speed / AnimSpeed / Scale (from the weather scripts) drive two crossing layers of it, exactly the way the original
/// builds its water normal from an animated, tiled bump map; the colours come from Sea.Water.Color / Sky.Color / Pena.Color / fog, the strength of the
/// reflection from Sea.WaterReflection and Sea.FrenelCoefficient. The sky reflected in the water is the very sky box texture of the scene.
/// </summary>
public static class SeaSurface
{
    static readonly Dictionary<string, Texture2DArray?> bumps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bump frames as a texture array (RG8: 0.5 = no slope), or null if the file is missing.</summary>
    public static Texture2DArray? LoadBump(string dir)
    {
        string key = dir.Replace('/', '\\').Trim('\\');
        if (bumps.TryGetValue(key, out var t)) return t;
        Texture2DArray? res = null;
        try
        {
            string file = GameAssets.Resource("TEXTURES\\" + (key.StartsWith("resource\\textures\\", StringComparison.OrdinalIgnoreCase) ? key.Substring(18) : key) + "\\bump.zap");
            if (File.Exists(file))
            {
                var b = File.ReadAllBytes(file);
                int frames = BitConverter.ToInt32(b, 0), w = BitConverter.ToInt32(b, 4), h = BitConverter.ToInt32(b, 8), mips = BitConverter.ToInt32(b, 12);
                int chain = 0; for (int m = 0, ww = w, hh = h; m < mips; m++, ww = Math.Max(1, ww / 2), hh = Math.Max(1, hh / 2)) chain += ww * hh * 2;
                var imgs = new Godot.Collections.Array<Image>();
                for (int f = 0; f < frames; f++)
                {
                    var px = new byte[w * h * 2];
                    int o = 16 + f * chain;
                    for (int i = 0; i < px.Length; i++) px[i] = (byte)(b[o + i] ^ 0x80);       // signed -> offset binary
                    var img = Image.CreateFromData(w, h, false, Image.Format.Rg8, px);
                    img.GenerateMipmaps();
                    imgs.Add(img);
                }
                res = new Texture2DArray();
                res.CreateFromImages(imgs);
                GD.Print($"[seasurface] bump map {file}: {frames} frames {w}x{h}");
            }
            else GD.Print("[seasurface] bump map not found: " + file);
        }
        catch (Exception e) { GD.PrintErr("[seasurface] bump: " + e.Message); }
        bumps[key] = res;
        return res;
    }

    /// <summary>settings.ini [game] WaterShader = boujie | classic (default boujie): the sea shader built after the Boujie Water Shader (game/water/boujie), or the earlier own one below.</summary>
    public static bool Boujie = true;
    /// <summary>SEADOGS_NOSCREEN=1: boujie without refraction / depth fog / shore foam (debug).</summary>
    public static readonly bool NoScreen = System.Environment.GetEnvironmentVariable("SEADOGS_NOSCREEN") == "1";
    static Shader? boujieShader;
    static readonly Dictionary<string, ImageTexture?> boujieTex = new();

    /// <summary>The shader the sea mesh uses (falls back to the classic one if the boujie file cannot be loaded).</summary>
    public static Shader MakeShader()
    {
        if (Boujie)
        {
            boujieShader ??= GD.Load<Shader>("res://water/boujie/sea_boujie.gdshader");
            if (boujieShader != null) return boujieShader;
            GD.PrintErr("[seasurface] boujie shader missing, using classic");
            Boujie = false;
        }
        return new Shader { Code = Shader };
    }

    /// <summary>A texture of game/water/boujie read straight from the file (no editor import needed), repeating, with mipmaps.</summary>
    public static ImageTexture? BoujieTexture(string name)
    {
        if (boujieTex.TryGetValue(name, out var t)) return t;
        ImageTexture? res = null;
        try
        {
            var img = Image.LoadFromFile(ProjectSettings.GlobalizePath("res://water/boujie/" + name));
            if (img != null) { img.GenerateMipmaps(); res = ImageTexture.CreateFromImage(img); }
        }
        catch (Exception e) { GD.PrintErr("[seasurface] " + name + ": " + e.Message); }
        boujieTex[name] = res;
        return res;
    }

    public const string Shader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled;
uniform vec3 water_color : source_color;
uniform vec3 sky_color : source_color;
uniform vec3 fog_color : source_color;
uniform vec3 pena_color : source_color;
uniform vec3 sun_color : source_color;
uniform vec3 road_color : source_color;
uniform vec3 sun_dir;
uniform float road_power;
uniform float road_start;
uniform float fog_density;
uniform float refl_k;
uniform float fresnel_k;
uniform float t;
uniform float disp;                  // 0 on the far plane
uniform vec3 far_hole = vec3(0.0, 0.0, -1.0);   // far plane: cut out under the near mesh
uniform float chop;                  // horizontal pull of the crests
uniform float foam_gain;
uniform float bump_scale;
uniform float bump_tile;
uniform float bump_ang;
uniform float bump_speed;
uniform float bump_frame;
uniform float amp_total;
uniform float wave_k[6];
uniform float wave_w[6];
uniform float wave_a[6];
uniform vec2 wave_dir[6];
uniform sampler2DArray bump : filter_linear_mipmap, repeat_enable;
uniform sampler2D sky_tex : filter_linear, repeat_disable;
uniform sampler2D foam_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has_sky;
uniform bool has_bump;
varying vec3 wpos;
varying float hh;
void vertex() {
    vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float h = 0.0; vec2 d = vec2(0.0);
    for (int i = 0; i < 6; i++) {
        float ph = wave_k[i] * dot(wave_dir[i], w.xz) - wave_w[i];
        h += wave_a[i] * sin(ph);
        d += wave_dir[i] * wave_a[i] * cos(ph);
    }
    float fade = 1.0 - smoothstep(330.0, 480.0, length(w.xz - CAMERA_POSITION_WORLD.xz));
    h *= disp * fade; d *= disp * fade * chop;
    VERTEX.y += h;
    VERTEX.xz -= d;                    // crests are pulled together (Gerstner): sharper crests, wider troughs
    hh = h;
    wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}
vec2 rot(vec2 p, float a) { float c = cos(a), s = sin(a); return vec2(c * p.x - s * p.y, s * p.x + c * p.y); }
vec2 bump_xy(vec2 p, float layer_t, float fr) {
    int f0 = int(floor(fr)) % 31; int f1 = (f0 + 1) % 31; float k = fract(fr);
    vec2 a = texture(bump, vec3(p, float(f0))).rg;
    vec2 b = texture(bump, vec3(p, float(f1))).rg;
    return (mix(a, b, k) - vec2(0.5)) * 2.0;
}
void fragment() {
    if (far_hole.z > 0.0 && abs(wpos.x - far_hole.x) < far_hole.z && abs(wpos.z - far_hole.y) < far_hole.z) discard;
    vec3 toCam = CAMERA_POSITION_WORLD - wpos;
    float dist = length(toCam);
    vec3 V = toCam / dist;
    // swell slope, analytic per pixel (the mesh is only coarse): no interpolation bands
    vec2 s = vec2(0.0);
    float crestH = 0.0;
    for (int i = 0; i < 6; i++) {
        float ph = wave_k[i] * dot(wave_dir[i], wpos.xz) - wave_w[i];
        s += wave_a[i] * wave_k[i] * cos(ph) * wave_dir[i];
        crestH += wave_a[i] * sin(ph);
    }
    vec2 b = vec2(0.0);
    float near = 1.0 - smoothstep(60.0, 420.0, dist);
    if (has_bump) {
        vec2 p1 = wpos.xz * bump_tile + vec2(t * bump_speed, t * bump_speed * 0.6);
        vec2 p2 = rot(wpos.xz, bump_ang + 0.9) * bump_tile * 1.73 - vec2(t * bump_speed * 0.7, 0.0) + vec2(0.37, 0.11);
        b = bump_xy(p1, 0.0, bump_frame) + bump_xy(p2, 0.0, bump_frame + 11.0) * 0.8;
        b *= 0.17 * bump_scale * mix(0.35, 1.0, near);
    }
    vec3 n = normalize(vec3(-s.x - b.x, 1.0, -s.y - b.y));
    float ndv = clamp(dot(V, n), 0.0, 1.0);
    float f = pow(1.0 - ndv, 5.0 / (0.35 + fresnel_k * 0.65 + 0.001) * 0.7);
    f = clamp(0.03 + 0.97 * f, 0.0, 1.0);

    // reflected sky: the real sky box texture (4 walls of one strip texture, bottom of each strip = horizon)
    vec3 R = reflect(-V, n);
    R.y = abs(R.y);
    vec3 refl = sky_color;
    if (has_sky) {
        const float RR = 1500.0, TOP = 1300.0, BOT = -150.0;
        float m = max(max(abs(R.x), abs(R.z)), 0.0001);
        float tt = RR / m;
        float y = clamp(R.y * tt, 0.0, TOP);
        float lv = (TOP - y) / (TOP - BOT);
        float wall; float u;
        if (abs(R.z) >= abs(R.x)) {
            if (R.z > 0.0) { wall = 0.0; u = (R.x * tt + RR) / (2.0 * RR); }
            else { wall = 2.0; u = (RR - R.x * tt) / (2.0 * RR); }
        } else {
            if (R.x > 0.0) { wall = 1.0; u = (RR - R.z * tt) / (2.0 * RR); }
            else { wall = 3.0; u = (R.z * tt + RR) / (2.0 * RR); }
        }
        float v = (wall + clamp(lv, 0.01, 0.99)) * 0.25;
        refl = textureLod(sky_tex, vec2(clamp(u, 0.002, 0.998), v), 1.5).rgb * sky_color;
    }

    // body colour: dark deep water, a little lighter on the sunlit side of a swell
    float lit = clamp(0.5 + 0.5 * dot(n, normalize(sun_dir)), 0.0, 1.0);
    vec3 body = water_color * (1.1 + 1.3 * lit) + vec3(0.004, 0.01, 0.012);
    body *= mix(0.85, 1.25, clamp(0.5 + crestH * 0.15, 0.0, 1.0));
    vec3 col = mix(body, refl, clamp(f * refl_k * 1.25, 0.0, 0.95));

    // sun glitter / sun road (SunRoad.* of the weather)
    vec3 L = normalize(sun_dir);
    vec3 H = normalize(L + V);
    float nh = clamp(dot(n, H), 0.0, 1.0);
    float glint = pow(nh, 220.0) * 1.6 * mix(0.3, 1.0, near);
    float broad = pow(nh, 18.0) * 0.18;
    col += (glint + broad) * road_color * (road_power > 1.0 ? 0.6 : 1.0);

    // foam: white caps on the steep crests (grows with the swell), streaks from the sea foam texture
    float foam = 0.0;
    if (foam_gain > 0.001) {
        float ch = crestH / max(amp_total, 0.1);
        float cap = smoothstep(0.42 - 0.18 * foam_gain, 0.95, ch + length(b) * 0.5 + 0.1 * foam_gain);
        vec2 fp = wpos.xz * 0.11 + vec2(t * 0.012, -t * 0.008);
        float ft = texture(foam_tex, fp).r * 0.6 + texture(foam_tex, fp * 2.7 + 0.3).r * 0.4;
        foam = clamp(cap * (0.35 + 1.1 * ft) * smoothstep(0.08, 0.35, foam_gain) * (0.35 + 0.65 * near), 0.0, 1.0);
        col = mix(col, pena_color, foam);
    }

    // own fog (Sea.Fog.SeaDensity)
    float fg = 1.0 - exp(-dist * fog_density);
    col = mix(col, fog_color, clamp(fg, 0.0, 1.0));
    ALBEDO = col;
}";
}
