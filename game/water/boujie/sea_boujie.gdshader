// Sea water in the manner of "Boujie Water Shader" (MIT, Chrisknyfe / Zach Bernal, see LICENSE.md and NOTICE.txt), rewritten for the port:
//  * waves: our own harmonics (SeaWaves.cs, the ones the ships ride on) as Gerstner waves, no wave parameters of the original;
//  * kept from the original: foam texture on wave crests (foam_alpha), shore foam from the depth texture, depth fog (Beer's law) between a shallow
//    and a deep colour, refraction of the screen texture, Fresnel, albedo texture;
//  * kept from the port: the script's colours / fog / sky box reflection / sun road / bump map (bump.zap) of the original game.
// Compatibility renderer (OpenGL 3.3): hint_screen_texture and hint_depth_texture are used, there is no compute / SDFGI / SSR.
shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled, depth_draw_always;

uniform vec3 water_color : source_color;
uniform vec3 sky_color : source_color;
uniform vec3 fog_color : source_color;
uniform vec3 pena_color : source_color;
uniform vec3 sun_color : source_color;
uniform vec3 road_color : source_color;
uniform vec3 sun_dir;
uniform float road_power;
uniform float fog_density;
uniform float refl_k;
uniform float fresnel_k;
uniform float t;
uniform float disp;                  // 0 on the far plane
uniform float chop;
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
uniform sampler2D foam_tex : source_color, filter_linear_mipmap, repeat_enable;     // the boujie foam (alpha = foam cover)
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;   // the boujie ocean albedo (grey ripples)
uniform bool has_sky;
uniform bool has_bump;
uniform bool use_screen = true;      // refraction + depth fog + shore foam (needs the screen / depth texture)
uniform float refraction = 0.05;
uniform float beers_law = 0.55;
uniform float depth_offset = -0.3;
uniform float shore_start = 0.2;
uniform float shore_end = 2.2;
uniform float shore_gain = 1.0;
uniform float debug_depth = 0.0;
uniform float detail = 1.0;          // ripple strength (bump map + chop) for a close view

uniform vec3 shallow_color = vec3(0.12, 0.42, 0.45);
uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear_mipmap;
uniform sampler2D depth_texture : hint_depth_texture, repeat_disable, filter_nearest;
varying vec3 wpos;
varying float hh;

void vertex() {
    vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float h = 0.0; vec2 d = vec2(0.0);
    for (int i = 0; i < 6; i++) {
        if (wave_k[i] > 0.4) continue;                  // short chop: per pixel only, the mesh cannot carry it
        float ph = wave_k[i] * dot(wave_dir[i], w.xz) - wave_w[i] * t;
        h += wave_a[i] * sin(ph);
        d += wave_dir[i] * wave_a[i] * cos(ph);
    }
    float fade = 1.0 - smoothstep(330.0, 480.0, length(w.xz - CAMERA_POSITION_WORLD.xz));
    h *= disp * fade; d *= disp * fade * chop;
    VERTEX.y += h;
    VERTEX.xz -= d;
    hh = h;
    wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

vec2 rot(vec2 p, float a) { float c = cos(a), s = sin(a); return vec2(c * p.x - s * p.y, s * p.x + c * p.y); }
vec2 bump_xy(vec2 p, float fr) {
    int f0 = int(floor(fr)) % 31; int f1 = (f0 + 1) % 31; float k = fract(fr);
    vec2 a = texture(bump, vec3(p, float(f0))).rg;
    vec2 b = texture(bump, vec3(p, float(f1))).rg;
    return (mix(a, b, k) - vec2(0.5)) * 2.0;
}
// distance along the view ray between the water surface and the opaque thing behind it (the original depth_from_water_to_object)
float scene_depth_diff(vec2 uv, mat4 inv_proj, vec3 view_pos) {
    float raw = textureLod(depth_texture, uv, 0.0).r;
    vec3 ndc = vec3(uv * 2.0 - 1.0, raw * 2.0 - 1.0);      // OpenGL: depth in 0..1 -> clip -1..1
    vec4 v = inv_proj * vec4(ndc, 1.0);
    v.xyz /= v.w;
    return -v.z - (-view_pos.z);
}

void fragment() {
    vec3 toCam = CAMERA_POSITION_WORLD - wpos;
    float dist = length(toCam);
    vec3 V = toCam / dist;
    vec2 s = vec2(0.0);
    float crestH = 0.0;
    for (int i = 0; i < 6; i++) {
        float ph = wave_k[i] * dot(wave_dir[i], wpos.xz) - wave_w[i] * t;
        s += wave_a[i] * wave_k[i] * cos(ph) * wave_dir[i];
        crestH += wave_a[i] * sin(ph);
    }
    vec2 b = vec2(0.0);
    float near = 1.0 - smoothstep(60.0, 420.0, dist);
    if (has_bump) {
        vec2 p1 = wpos.xz * bump_tile + vec2(t * bump_speed, t * bump_speed * 0.6);
        vec2 p2 = rot(wpos.xz, bump_ang + 0.9) * bump_tile * 1.73 - vec2(t * bump_speed * 0.7, 0.0) + vec2(0.37, 0.11);
        b = bump_xy(p1, bump_frame) + bump_xy(p2, bump_frame + 11.0) * 0.8;
        b *= 0.17 * bump_scale * mix(0.35, 1.0, near) * detail;
    }
    vec3 n = normalize(vec3(-s.x - b.x, 1.0, -s.y - b.y));
    float ndv = clamp(dot(V, n), 0.0, 1.0);
    float f = pow(1.0 - ndv, 5.0 / (0.35 + fresnel_k * 0.65 + 0.001) * 0.7);
    f = clamp(0.03 + 0.97 * f, 0.0, 1.0);

    // sky box reflection (as the classic shader)
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

    // body: deep water colour lit by the sun, with the grey ocean albedo texture as fine ripple shading
    float lit = clamp(0.5 + 0.5 * dot(n, normalize(sun_dir)), 0.0, 1.0);
    float alb = texture(albedo_tex, wpos.xz * 0.07 + vec2(t * 0.01, t * 0.007)).r * 0.6 + texture(albedo_tex, wpos.xz * 0.19 - vec2(t * 0.012, 0.0)).r * 0.4;
    vec3 deep = water_color * (1.1 + 1.3 * lit) + vec3(0.004, 0.01, 0.012);
    float chn = clamp(0.5 + 0.5 * crestH / max(amp_total, 0.15), 0.0, 1.0);
    deep *= mix(0.78, 1.28, chn);
    deep += shallow_color * 0.10 * smoothstep(0.55, 1.0, chn) * (0.4 + 0.6 * lit);     // light through the thin crests
    deep *= mix(1.0, 0.7 + 0.3 * alb, near);
    vec3 body = deep;
    float zd = 1000.0;
    float shore = 0.0;
    float scr_ok = use_screen && dist < 900.0 ? 1.0 : 0.0;
    if (scr_ok > 0.5) {
        // refraction: the screen behind the water is shifted by the wave slope; the depth fog (Beer's law) blends it from the shallow to the deep colour
        vec2 off = (s + b) * refraction * clamp(60.0 / dist, 0.0, 1.0);
        vec2 uv = clamp(SCREEN_UV - off, vec2(0.002), vec2(0.998));
        zd = scene_depth_diff(uv, INV_PROJECTION_MATRIX, VERTEX);
        if (zd < 0.0) { uv = SCREEN_UV; zd = scene_depth_diff(uv, INV_PROJECTION_MATRIX, VERTEX); }   // something stands in front of the refracted point: do not smear it
        float zraw = zd;
        zd = max(zd, 0.0);
        float blend = clamp(1.0 - exp((zd + depth_offset) * -beers_law), 0.0, 1.0);
        float bp = pow(blend, 2.0);
        // far away the depth buffer is imprecise: fade the feature out
        bp = clamp(bp + smoothstep(350.0, 800.0, dist), 0.0, 1.0);
        vec3 seen = textureLod(screen_texture, uv, bp * 2.5).rgb;
        seen = mix(seen, shallow_color * (0.4 + 0.8 * lit) * (0.5 + 0.5 * length(sky_color)), 0.45);
        body = mix(seen, deep, bp);
        // shore foam from the depth: where the sea is shallow (island coast, hull waterline) the foam texture shows
        float sz = scene_depth_diff(SCREEN_UV, INV_PROJECTION_MATRIX, VERTEX);
        if (sz > 0.0 && sz < shore_end * 1.2) {
            vec2 fp = wpos.xz * 0.12 + vec2(t * 0.02, -t * 0.015);
            float ft = smoothstep(0.5, 1.0, texture(foam_tex, fp).a * 0.6 + texture(foam_tex, fp * 2.3 + 0.4).a * 0.4);
            float ripple = 0.55 + 0.45 * sin(sz * 5.0 - t * 1.8 + ft * 3.0);
            shore = (1.0 - smoothstep(shore_start, shore_end, sz + (ft - 0.5) * 0.9)) * (0.5 + 0.5 * ft) * ripple * shore_gain * (1.0 - smoothstep(300.0, 700.0, dist));
        }
    }
    vec3 col = mix(body, refl, clamp(f * refl_k * 1.25, 0.0, 0.95));

    vec3 L = normalize(sun_dir);
    vec3 H = normalize(L + V);
    float nh = clamp(dot(n, H), 0.0, 1.0);
    float glint = pow(nh, 220.0) * 1.6 * mix(0.3, 1.0, near);
    float broad = pow(nh, 18.0) * 0.18;
    col += (glint + broad) * road_color * (road_power > 1.0 ? 0.6 : 1.0);

    // crest foam: the foam texture's alpha cut by the wave height (the original foam_alpha), grows with the swell
    float foam = 0.0;
    if (foam_gain > 0.001) {
        float ch = crestH / max(amp_total, 0.1);
        float cap = smoothstep(0.38 - 0.2 * foam_gain, 0.95, ch + length(b) / detail * 0.5 + 0.1 * foam_gain);
        vec2 fp = wpos.xz * 0.11 + vec2(t * 0.012, -t * 0.008);
        float ft = smoothstep(0.5, 1.0, texture(foam_tex, fp).a * 0.6 + texture(foam_tex, fp * 2.7 + 0.3).a * 0.4);
        foam = clamp(cap * (0.06 + 1.0 * ft) * smoothstep(0.08, 0.35, foam_gain) * (0.35 + 0.65 * near), 0.0, 1.0);
    }
    float fa = clamp(max(foam, shore), 0.0, 1.0);
    col = mix(col, pena_color, fa);

    float fg = 1.0 - exp(-dist * fog_density);
    col = mix(col, fog_color, clamp(fg, 0.0, 1.0));
    if (debug_depth > 0.5) col = vec3(shore, clamp(zd / 10.0, 0.0, 1.0), 0.0);
    ALBEDO = col;
}
