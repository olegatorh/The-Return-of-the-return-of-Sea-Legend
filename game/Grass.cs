using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine's "Grass" entity (location/src/grass.cpp + effects/grass.fx): the location's *.grs file (models.always.grassPatch) holds blades of grass
/// in 1 m blocks (the density map: the level designer painted it, so there is none on roads, stone and walls); every blade is a vertical quad turned
/// by one of 16 angles with a frame of the 4x4 grass atlas (Textures\Grass\grass.tga). Here the blades are static vertex data in chunks of 8x8 blocks
/// (Godot culls the chunks); the shader repeats the engine's vertex shader and CPU sway: wind waves (EWhr_GetWindAngle / Speed = Weather.Wind.*),
/// the characters pushing the blades aside, the 4 LOD levels per block (fewer blades with distance, fading out at 50 m), light = ambient + sun by direction.
/// </summary>
public partial class GrassView : Node3D
{
    const int Chunk = 8;             // blocks per chunk side
    const float MaxDist = 50f, MinDist = 10f;   // GRASS_MAX_DIST / GRASS_MIN_DIST
    const float MaxW = 1f, MaxH = 0.2f;         // GRASS_MAX_W / GRASS_MAX_H
    const int MaxChr = 8;

    public static int Blades;                    // blades loaded in the current location (test hook, log [grass])
    public static int LiveViews;

    readonly Interp it;
    readonly List<MeshInstance3D> chunks = new();
    ShaderMaterial? mat;
    // sway state (Grass::Execute)
    readonly float[] phase = { 0.1f, 0.51f, 0.01f, -0.2f, 0.03f, 0.04f, 0.08f };
    float winForce = 0.3f, windAng;
    int initForce;
    Vector2 winDir = new(0, 1);
    readonly Dictionary<CharacterView, Vector3> lastPos = new();
    public int BladeCount;

    public GrassView(Interp it) { this.it = it; Name = "Grass"; }

    public override void _ExitTree() { Blades -= BladeCount; LiveViews--; }

    static readonly string Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;
uniform sampler2D tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec3 cam;
uniform vec4 ph0;      // phase0, cosPh1, sinPh2, sinPh5
uniform vec4 ph1;      // phase3, phase4, sinPh6, winPow
uniform vec4 wk;       // winF10, winForce, kAmpWF, kLitWF
uniform vec4 wdir;     // winDir.xz, kwDir.xz
uniform vec2 wadd;
uniform vec3 acol;
uniform vec3 lcol;
uniform vec2 ldir;
uniform vec2 asize = vec2(1.0, 0.2);
uniform float minLod = 0.0;
uniform float lodMin = 10.0;
uniform float lodMax = 50.0;
uniform int nchr = 0;
uniform vec4 chrp[8];   // xyz = position, w = unused
uniform vec4 chrl[8];   // xyz = inertial position
uniform float amount = 1.0;
varying vec3 vcol;
varying vec2 vuv;
varying float valpha;

void vertex() {
    vec3 p = vec3(VERTEX.x, VERTEX.y, -VERTEX.z);       // engine (Direct3D) coordinates
    float w = COLOR.r, h = COLOR.g;
    float angI = floor(COLOR.b * 15.0 + 0.5);
    float uvI = floor(COLOR.a * 15.0 + 0.5);
    float m = floor(UV.y * 0.5);
    vec2 off = vec2(UV.x, UV.y - 2.0 * m);

    // LOD of the block: how many of its blades are still drawn and how transparent the last ones are
    float dist = length(vec2(UV2.x - cam.x, UV2.y - cam.z));
    float kLod = max((dist - lodMin) / (lodMax - lodMin), minLod);
    float lx = kLod * 3.9999;
    float alpha = clamp(m + 1.0 - lx, 0.0, 1.0);

    // wind
    float winx = sin(p.x * ph0.y + p.z * 0.06 + ph0.x);
    float winz = cos(p.x * 0.11 + p.z * ph0.z + ph0.x);
    float dx = wdir.x * p.x * 0.5 + ph1.x;
    float dz = wdir.y * p.z * 0.5 + ph1.y;
    float k1 = sin(dx + dz);
    float a1 = 0.001 + ph0.w * sin((dx + dz) * 0.5);
    float k2 = cos(wdir.y * p.x * (a1 + ph1.z) - wdir.x * p.z * a1);
    float kamp = pow(max(k1 * k2 * 0.5 + 0.5, 0.0), ph1.w) + wk.x + wk.y * 0.7;
    kamp = min(kamp, 1.0) * wk.z;
    winx = (0.9 * winx + wdir.z) * kamp + wadd.x;
    winz = (0.9 * winz + wdir.w) * kamp + wadd.y;
    for (int i = 0; i < 8; i++) {
        if (i >= nchr) break;
        if (abs(chrp[i].y - p.y) < 0.7) {
            float pdx = ((p.x - chrp[i].x) + (p.x - chrl[i].x)) * 0.5;
            float pdz = ((p.z - chrp[i].z) + (p.z - chrl[i].z)) * 0.5;
            float d2 = pdx * pdx + pdz * pdz;
            if (d2 < 0.64 && d2 > 0.0) {
                float d = sqrt(d2);
                float k = 0.5 / d;
                pdx *= k; pdz *= k;
                d = 1.0 - d / 0.8;
                d *= d;
                winx += (pdx - winx) * d;
                winz += (pdz - winz) * d;
            }
        }
    }
    float wl = sqrt(winx * winx + winz * winz);
    if (wl > 1.2) { wl = 1.2 / wl; winx *= wl; winz *= wl; }
    winx *= 0.4; winz *= 0.4;

    // orientation, size and lighting (grass.fx)
    float a = angI * 6.2831853 / 16.0;
    vec2 ang = vec2(sin(a), cos(a));
    float angz = clamp(abs(-ang.y * ldir.x + ang.x * ldir.y), 0.0, 1.0);
    float kc = clamp(dot(vec2(winx, winz), ldir), -0.5, 1.0);
    kc = (kc * 0.9 + angz * 0.8) * wk.w * off.y;
    vcol = acol + lcol * kc;
    vec2 size = vec2(w * 0.3 * asize.x + 0.7 * asize.x, h * 0.4 * asize.y + 0.6 * asize.y);
    vec3 q;
    q.xz = p.xz - ang * size.x * (off.x - 0.5) + vec2(winx, winz) * off.y;
    q.y = p.y + sqrt(max(size.y - (winx * winx + winz * winz), 0.0)) * off.y;
    vuv = vec2(mod(uvI, 4.0) * 0.25 + off.x * 0.245, floor(uvI / 4.0) * 0.25 - off.y * 0.245);
    valpha = alpha;
    VERTEX = vec3(q.x, q.y, -q.z);
}

void fragment() {
    vec4 t = texture(tex, vuv);
    ALBEDO = min(t.rgb * vcol * 2.0, vec3(1.0));
    ALPHA = t.a * valpha;
    ALPHA_SCISSOR_THRESHOLD = 0.32;
}";

    public bool Load(string grsFile, string? texName)
    {
        byte[] d;
        try { d = File.ReadAllBytes(grsFile); }
        catch (Exception e) { GD.PrintErr($"[grass] {grsFile}: {e.Message}"); return false; }
        if (d.Length < 28 || BitConverter.ToUInt32(d, 0) != 0x73737247u || BitConverter.ToUInt32(d, 4) != 0x312E3030u && BitConverter.ToUInt32(d, 4) != 0x30302E31u)
        { GD.PrintErr($"[grass] {grsFile}: bad id/version"); return false; }
        int miniX = BitConverter.ToInt32(d, 8), miniZ = BitConverter.ToInt32(d, 12);
        float startX = BitConverter.ToSingle(d, 16), startZ = BitConverter.ToSingle(d, 20);
        int n = BitConverter.ToInt32(d, 24);
        if (miniX <= 0 || miniZ <= 0 || miniX > 100000 || miniZ > 100000 || d.Length != 28L + (long)miniX * miniZ * 28 + (long)n * 8)
        { GD.PrintErr($"[grass] {grsFile}: bad size"); return false; }
        int elems = 28 + miniX * miniZ * 28;

        var img = GameAssets.Texture(string.IsNullOrWhiteSpace(texName) ? "Grass\\grass.tga" : texName!) ?? GameAssets.Texture("Grass\\grass.tga");
        if (img == null) { GD.PrintErr("[grass] texture Grass\\grass.tga missing"); return false; }
        mat = new ShaderMaterial { Shader = new Shader { Code = Code } };
        mat.SetShaderParameter("tex", img);

        int cx = (miniX + Chunk - 1) / Chunk, cz = (miniZ + Chunk - 1) / Chunk;
        var lists = new Dictionary<int, List<(int mx, int mz, int start, int cnt, int[] num, float y0, float y1)>>();
        for (int bz = 0; bz < miniZ; bz++)
            for (int bx = 0; bx < miniX; bx++)
            {
                int o = 28 + (bz * miniX + bx) * 28;
                int start = BitConverter.ToInt32(d, o), cnt = BitConverter.ToInt32(d, o + 4);
                if (cnt <= 0) continue;
                var num = new[] { cnt, BitConverter.ToInt32(d, o + 8), BitConverter.ToInt32(d, o + 12), BitConverter.ToInt32(d, o + 16) };
                int key = (bz / Chunk) * cx + bx / Chunk;
                if (!lists.TryGetValue(key, out var l)) lists[key] = l = new();
                l.Add((bx, bz, start, cnt, num, BitConverter.ToSingle(d, o + 20), BitConverter.ToSingle(d, o + 24)));
            }

        foreach (var (key, blocks) in lists)
        {
            int total = blocks.Sum(b => b.cnt);
            var pos = new Vector3[total * 4]; var col = new Color[total * 4]; var uv = new Vector2[total * 4]; var uv2 = new Vector2[total * 4];
            var idx = new int[total * 6];
            int v = 0, ii = 0;
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (var b in blocks)
            {
                float bcx = startX + (b.mx + 0.5f), bcz = startZ + (b.mz + 0.5f);
                y0 = MathF.Min(y0, b.y0); y1 = MathF.Max(y1, b.y1);
                for (int i = 0; i < b.cnt; i++)
                {
                    int o = elems + (b.start + i) * 8;
                    float x = startX + b.mx + d[o] / 32f, z = startZ + b.mz + d[o + 1] / 32f, y = BitConverter.ToSingle(d, o + 4);
                    int w = d[o + 2] & 15, h = d[o + 2] >> 4, frame = d[o + 3] & 15, ang = d[o + 3] >> 4;
                    int m = 0;
                    for (int l = 1; l < 4; l++) if (i < b.num[l]) m = l;
                    // the shader's names: angle table index = "frame" byte, atlas cell = "ang" byte (grass.fx reads them from COLOR0.z / .w)
                    var c = new Color(w / 15f, h / 15f, frame / 15f, ang / 15f);
                    for (int k = 0; k < 4; k++)
                    {
                        pos[v + k] = new Vector3(x, y, -z);
                        col[v + k] = c;
                        uv[v + k] = new Vector2(k & 1, (k >> 1) + 2 * m);
                        uv2[v + k] = new Vector2(bcx, bcz);
                    }
                    idx[ii++] = v; idx[ii++] = v + 1; idx[ii++] = v + 2; idx[ii++] = v + 2; idx[ii++] = v + 1; idx[ii++] = v + 3;
                    v += 4;
                    x0 = MathF.Min(x0, x); x1 = MathF.Max(x1, x); z0 = MathF.Min(z0, z); z1 = MathF.Max(z1, z);
                }
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = pos; arrays[(int)Mesh.ArrayType.Color] = col;
            arrays[(int)Mesh.ArrayType.TexUV] = uv; arrays[(int)Mesh.ArrayType.TexUV2] = uv2; arrays[(int)Mesh.ArrayType.Index] = idx;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            const float pad = 1.6f;      // width 1 m + wind sway
            mesh.CustomAabb = new Aabb(new Vector3(x0 - pad, y0 - 0.2f, -z1 - pad), new Vector3(x1 - x0 + 2 * pad, y1 - y0 + 1.0f, z1 - z0 + 2 * pad));
            var mi = new MeshInstance3D
            {
                Mesh = mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEnd = MaxDist + 16f, VisibilityRangeEndMargin = 2f,
            };
            AddChild(mi);
            chunks.Add(mi);
            BladeCount += total;
        }
        Blades += BladeCount; LiveViews++;
        GD.Print($"[grass] {Path.GetFileName(grsFile)}: {BladeCount} blades in {chunks.Count} chunks ({miniX}x{miniZ} blocks) texture={(string.IsNullOrWhiteSpace(texName) ? "Grass\\grass.tga" : texName)}");
        return true;
    }

    float F(Attr a, string path, float def)
        => float.TryParse(a.Find(path, false)?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;

    public override void _Process(double delta)
    {
        if (mat == null) return;
        float dt = (float)delta;
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return;
        // wind (Weather.Wind.Angle / Speed)
        float ang = 0f, spd = 0f;
        if (it.Globals.TryGetValue("Weather", out var w) && w.Val.Attr is { } wa) { ang = F(wa, "Wind.Angle", 0f); spd = F(wa, "Wind.Speed", 0f); }
        winDir = new Vector2(MathF.Sin(ang), MathF.Cos(ang));
        float wf = MathF.Pow(Math.Clamp(spd, 0f, 30f) / 30f, 0.5f);
        if (initForce < 20) { initForce++; winForce = wf; windAng = ang; }
        else winForce = Math.Clamp(winForce + (wf - winForce) * MathF.Min(1f, dt * 0.3f), 0f, 1f);
        float dtp = MathF.Min(dt, 0.05f);
        phase[0] += dtp * 0.3f; phase[1] += dtp * 0.21f; phase[2] += dtp * 0.01f;
        phase[3] += winDir.X * dtp * 0.52f; phase[4] += winDir.Y * dtp * 0.55f;
        phase[5] += dtp * 0.019f; phase[6] += dtp * 0.057f;
        for (int i = 0; i < 7; i++) if (phase[i] > 6.2831853f * 256f) phase[i] -= 6.2831853f * 256f;
        float cosPh1 = 0.4f + 0.15f * MathF.Cos(phase[1]), sinPh2 = 0.51f + 0.09f * MathF.Sin(phase[2]);
        float sinPh5 = 0.1f * MathF.Sin(phase[5]), sinPh6 = 0.01f * MathF.Sin(phase[6]);
        float winPow = 5f - MathF.Pow(winForce, 4f) * 4.5f, winF10 = MathF.Pow(winForce, 10f) * 0.5f;
        float kAmpWF = MathF.Pow(winForce, 2f) * 0.8f, kDirWF = 0.8f * (1f - MathF.Pow(winForce, 10f) * 0.999f);
        float kLitWF = 0.7f * (1f - MathF.Pow(winForce, 4f) * 0.7f);
        float wAdd = winForce * (1f + MathF.Cos(phase[1] + sinPh5)) * 0.25f;
        mat.SetShaderParameter("ph0", new Vector4(phase[0], cosPh1, sinPh2, sinPh5));
        mat.SetShaderParameter("ph1", new Vector4(phase[3], phase[4], sinPh6, winPow));
        mat.SetShaderParameter("wk", new Vector4(winF10, winForce, kAmpWF, kLitWF));
        mat.SetShaderParameter("wdir", new Vector4(winDir.X, winDir.Y, winDir.X * kDirWF, winDir.Y * kDirWF));
        mat.SetShaderParameter("wadd", new Vector2(winDir.X * wAdd, winDir.Y * wAdd));

        // light (Grass::Realize): ambient softened towards its luminance, the sun colour raised to 0.2; without a sun the engine's default light
        var amb = LocationLights.Ambient; var sun = LocationLights.SunColor;
        Vector3 a = new(Math.Clamp(amb.R, 0, 1), Math.Clamp(amb.G, 0, 1), Math.Clamp(amb.B, 0, 1)), l, ld;
        if (sun.R + sun.G + sun.B > 0.01f)
        {
            float y = a.X * 0.299f + a.Y * 0.587f + a.Z * 0.114f;
            a = (a * 0.3f + new Vector3(y, y, y) * 0.7f) * 0.8f;
            l = new Vector3(MathF.Pow(Math.Clamp(sun.R, 0, 1), 0.2f), MathF.Pow(Math.Clamp(sun.G, 0, 1), 0.2f), MathF.Pow(Math.Clamp(sun.B, 0, 1), 0.2f));
            var sd = LocationLights.SunDir;                       // towards the sun, Godot space; the light travels the other way, in engine space
            var v2 = new Vector2(-sd.X, sd.Z);
            ld = v2.LengthSquared() > 1e-6f ? new Vector3(v2.Normalized().X, 0, v2.Normalized().Y) : new Vector3(0.9f, 0, 0.5f).Normalized();
        }
        else { l = new Vector3(0.3f, 0.3f, 0.3f); ld = new Vector3(0.9f, 0, 0.5f).Normalized(); }
        mat.SetShaderParameter("acol", a); mat.SetShaderParameter("lcol", l); mat.SetShaderParameter("ldir", new Vector2(ld.X, ld.Z));

        var cp = cam.GlobalPosition;
        mat.SetShaderParameter("cam", new Vector3(cp.X, cp.Y, -cp.Z));

        // characters push the blades aside (only those near the camera: the engine does it per block)
        var cps = new Vector4[MaxChr]; var cls = new Vector4[MaxChr];
        int n = 0;
        foreach (var cv in CharacterView.All.Values)
        {
            if (!cv.Placed || !cv.Visible || n >= MaxChr) continue;
            var p = cv.GlobalPosition;
            if ((p - cp).LengthSquared() > 60f * 60f) continue;
            var cur = new Vector3(p.X, p.Y, -p.Z);
            lastPos.TryGetValue(cv, out var last);
            if (last == Vector3.Zero || last.DistanceSquaredTo(cur) > 25f) last = cur;
            last = last.Lerp(cur, 1f - MathF.Exp(-dt * 3f));
            lastPos[cv] = last;
            cps[n] = new Vector4(cur.X, cur.Y, cur.Z, 0); cls[n] = new Vector4(last.X, last.Y, last.Z, 0); n++;
        }
        for (int i = n; i < MaxChr; i++) { cps[i] = Vector4.Zero; cls[i] = Vector4.Zero; }
        mat.SetShaderParameter("nchr", n);
        mat.SetShaderParameter("chrp", cps); mat.SetShaderParameter("chrl", cls);
    }

    /// <summary>EX_MSG "SetGrassParams": scale (unused: 1), max width, max height, min/max visible distance, min lod.</summary>
    public void SetParams(float scale, float maxW, float maxH, float minDist, float maxDist, float minLod)
    {
        if (mat == null) return;
        mat.SetShaderParameter("asize", new Vector2(maxW, maxH));
        mat.SetShaderParameter("lodMin", minDist); mat.SetShaderParameter("lodMax", maxDist); mat.SetShaderParameter("minLod", minLod);
        foreach (var c in chunks) c.VisibilityRangeEnd = maxDist + 16f;
    }
}
