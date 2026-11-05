using System;
using System.Collections.Generic;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine class "BallSplash" (libs/ball_splash/src/ball_splash.cpp + t_splash.cpp; AIBalls.c sends MSG_BALLSPLASH_ADD 77200 "lffffff" when a ball hits the water). Up to 100 splashes,
/// each lives SPLASH_FADE_TIME 4.7 s and is made of two parts:
/// 1. a foam bubble: an 8x8 grid of 4.1 m (times a random growth factor of 1 to 2) lying on the water with explos.tga; the inner 6x6 vertices rise by HeightF (a damped ripple),
///    the border stays 0.15 m under the surface; the alpha fades in for 0.7 s and out from the half of the animation (technique "splash": modulate4x with the ambient colour);
/// 2. a water column: four vertical planes (turned by 45 degrees against each other, 0.5 to 1 x 2 m wide, 5 to 10 m high) that play the 64 frames of splash.tga
///    (2048 x 128, 32 x 128 per frame, 65 ms per frame), two neighbouring frames are blended by the fraction of the frame time (technique "splash2").
/// The ambient colour is the weather's Sun.Ambient desaturated (the original reads D3DRS_AMBIENT once). The sound "ball_splash" is played by the script.
/// </summary>
public partial class SeaView
{
    const int SplashMax = 100, SplashGridSteps = 8, SplashFrames = 64, SplashFrameDelay = 65, SplashFadeTime = 4700, SplashFadeIn = 700;
    const float SplashGridLength = 4.1f, SplashHeight = 10f, SplashWidth = 4f, SplashMoveY = -0.05f, SplashMoveY2 = -0.5f;

    sealed class BallSplashRt
    {
        public Node3D Root = null!; public MeshInstance3D Grid = null!, Planes = null!; public ShaderMaterial GridMat = null!, PlaneMat = null!;
        public float Time; public bool Active; public float GrowK, Height, Width2;
    }

    readonly List<BallSplashRt> splashes = new();
    static Shader? splashGridShader, splashPlaneShader;
    static ArrayMesh? splashGridMesh, splashPlaneMesh;

    const string SplashGridShaderCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
uniform sampler2D tex : source_color, filter_linear_mipmap;
uniform float time_ms;
uniform float grow_k;
uniform vec3 amb;
uniform float alpha;
" + WaveGlsl + @"
void vertex() {
    float inner = COLOR.r;                                      // 1 for the inner 6x6 vertices
    float rho = length(VERTEX.xz);
    float rk = (4.1 - rho) / 4.1;
    float k = 1.0 - min(time_ms / 4700.0, 1.0);
    float hgt = (0.55 + k * abs(cos(rk * 10.0 + time_ms / 700.0))) * rk * rk;
    vec2 xz = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xz;
    float y = sea_h(xz) - 0.05 + (inner > 0.5 ? hgt : -0.1);
    vec3 w = (MODEL_MATRIX * vec4(grow_k * VERTEX.x, 0.0, grow_k * VERTEX.z, 1.0)).xyz;
    w.y = y;
    VERTEX = (inverse(MODEL_MATRIX) * vec4(w, 1.0)).xyz;
}
void fragment() {
    vec4 c = texture(tex, UV);
    ALBEDO = min(c.rgb * amb * 4.0, vec3(1.0));
    ALPHA = clamp(c.a * alpha, 0.0, 1.0);
}";

    const string SplashPlaneShaderCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
uniform sampler2D tex : source_color, filter_linear;
uniform float u0;                                               // left edge of the current frame in the 2048 x 128 strip
uniform float frac;                                             // fraction of the frame time: lerp between this frame and the next one
uniform float height;
uniform float width2;
uniform vec3 amb;
" + WaveGlsl + @"
void vertex() {
    vec2 xz = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xz;
    vec3 w = (MODEL_MATRIX * vec4(VERTEX.x * width2, 0.0, VERTEX.z * width2, 1.0)).xyz;
    w.y = sea_h(xz) - 0.05 - 0.5 + VERTEX.y * height;
    VERTEX = (inverse(MODEL_MATRIX) * vec4(w, 1.0)).xyz;
}
void fragment() {
    float d = 1.0 / 64.0;
    vec4 a = texture(tex, vec2(u0 + UV.x * d, UV.y));
    vec4 b = texture(tex, vec2(u0 + d + UV.x * d, UV.y));
    ALBEDO = min(a.rgb * amb * 4.0, vec3(1.0));
    ALPHA = clamp(mix(a.a, b.a, frac), 0.0, 1.0);
}";

    Color SplashAmbient()
    {
        // Desaturate(ambient, 1): the mean of the channels
        if (it.Globals.TryGetValue("Weather", out var w) && w.Val.Attr?.Find("Sun.Ambient", false)?.Value is { Length: > 0 } v)
        {
            long n;
            try { n = v.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(v.Trim().Substring(2), 16) : long.Parse(v.Trim(), System.Globalization.CultureInfo.InvariantCulture); }
            catch { n = 0x808080; }
            float g = (((n >> 16) & 255) + ((n >> 8) & 255) + (n & 255)) / 3f / 255f;
            return new Color(g, g, g);
        }
        return new Color(0.4f, 0.4f, 0.4f);
    }

    float RandUpper(float r) { float half = r * 0.5f; return half + Rand(half); }

    /// <summary>MSG_BALLSPLASH_ADD: x, y, z in engine coordinates.</summary>
    void BallSplashAdd(float x, float z)
    {
        BallSplashRt? s = null;
        foreach (var o in splashes) if (!o.Active) { s = o; break; }
        if (s == null)
        {
            if (splashes.Count >= SplashMax) return;
            s = MakeSplash();
            splashes.Add(s);
        }
        s.Active = true; s.Time = 0f;
        s.GrowK = RandUpper(2f); s.Height = RandUpper(SplashHeight); s.Width2 = RandUpper(SplashWidth * 0.5f);
        s.Root.Position = new Vector3(x, 0f, -z);
        s.Root.Visible = true;
        var amb = SplashAmbient();
        s.GridMat.SetShaderParameter("amb", new Vector3(amb.R, amb.G, amb.B));
        s.PlaneMat.SetShaderParameter("amb", new Vector3(amb.R, amb.G, amb.B));
        s.GridMat.SetShaderParameter("grow_k", s.GrowK);
        s.PlaneMat.SetShaderParameter("height", s.Height);
        s.PlaneMat.SetShaderParameter("width2", s.Width2);
    }

    BallSplashRt MakeSplash()
    {
        splashGridShader ??= new Shader { Code = SplashGridShaderCode };
        splashPlaneShader ??= new Shader { Code = SplashPlaneShaderCode };
        if (splashGridMesh == null)
        {
            var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
            float step = SplashGridLength / SplashGridSteps, half = SplashGridLength * 0.5f;
            int n = SplashGridSteps;
            void V(int x, int z)
            {
                st.SetColor(new Color(x > 0 && z > 0 && x < n - 1 && z < n - 1 ? 1f : 0f, 0, 0, 1));
                st.SetUV(new Vector2(x / (float)(n - 1), z / (float)(n - 1)));
                st.AddVertex(new Vector3(step * x - half, 0f, step * z - half));
            }
            for (int z = 0; z < n - 1; z++)
                for (int x = 0; x < n - 1; x++) { V(x, z); V(x, z + 1); V(x + 1, z + 1); V(x, z); V(x + 1, z + 1); V(x + 1, z); }
            splashGridMesh = st.Commit();
            splashGridMesh.CustomAabb = new Aabb(new Vector3(-6, -2, -6), new Vector3(12, 14, 12));
            // four planes at angles i * 45 degrees: x, z = +-(cos a, sin a), y 0..1 scaled by the shader
            var pt = new SurfaceTool(); pt.Begin(Mesh.PrimitiveType.Triangles);
            for (int i = 0; i < 4; i++)
            {
                float a = i * MathF.PI / 4f, cx = MathF.Cos(a), sz = -MathF.Sin(a);       // engine z = -godot z
                void P(float side, float y, float u, float v) { pt.SetUV(new Vector2(u, v)); pt.AddVertex(new Vector3(side * cx, y, side * sz)); }
                P(-1, 1, 0, 0); P(-1, 0, 0, 1); P(1, 0, 1, 1);
                P(-1, 1, 0, 0); P(1, 0, 1, 1); P(1, 1, 1, 0);
            }
            splashPlaneMesh = pt.Commit();
            splashPlaneMesh.CustomAabb = new Aabb(new Vector3(-4, -2, -4), new Vector3(8, 14, 8));
        }
        var s = new BallSplashRt { Root = new Node3D { Name = "BallSplash", Visible = false } };
        s.GridMat = new ShaderMaterial { Shader = splashGridShader };
        s.GridMat.SetShaderParameter("tex", GameAssets.Texture("explos.tga"));
        s.PlaneMat = new ShaderMaterial { Shader = splashPlaneShader };
        s.PlaneMat.SetShaderParameter("tex", GameAssets.Texture("splash.tga"));
        s.Grid = new MeshInstance3D { Mesh = splashGridMesh, MaterialOverride = s.GridMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        s.Planes = new MeshInstance3D { Mesh = splashPlaneMesh, MaterialOverride = s.PlaneMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        s.Root.AddChild(s.Grid); s.Root.AddChild(s.Planes);
        AddChild(s.Root);
        return s;
    }

    void TickSplashes(float dt)
    {
        foreach (var s in splashes)
        {
            if (!s.Active) continue;
            s.Time += dt * 1000f;
            float t = s.Time;
            if (t > SplashFadeTime) { s.Active = false; s.Root.Visible = false; continue; }
            // alpha of the bubble (TSplash::Process)
            float dtt = t - SplashFrames * SplashFrameDelay * 0.5f, alpha;
            if (t < SplashFadeIn) alpha = t / SplashFadeIn;
            else if (dtt < 0f) alpha = 1f;
            else alpha = 1f - dtt / (SplashFadeTime - SplashFrames * SplashFrameDelay * 0.5f);
            s.GridMat.SetShaderParameter("time_ms", t);
            s.GridMat.SetShaderParameter("alpha", Math.Clamp(alpha, 0f, 1f));
            SetWaves(s.GridMat);
            // the column (Process2): frame by time, the plane stops after 63 frames
            bool planes = t <= (SplashFrames - 1) * SplashFrameDelay;
            s.Planes.Visible = planes;
            if (planes)
            {
                int frame = Math.Min((int)(t / SplashFrameDelay), SplashFrames - 1);
                s.PlaneMat.SetShaderParameter("u0", frame / (float)SplashFrames);
                s.PlaneMat.SetShaderParameter("frac", (t % SplashFrameDelay) / SplashFrameDelay);
                SetWaves(s.PlaneMat);
            }
        }
    }

    void ClearSplashes()
    {
        foreach (var s in splashes) if (GodotObject.IsInstanceValid(s.Root)) s.Root.QueueFree();
        splashes.Clear();
    }

    /// <summary>Test hook --splash@N: three splashes 14 to 20 m abeam of the player's ship.</summary>
    public void SplashTest()
    {
        var p = ships.Find(x => x.IsPlayer);
        if (p == null) return;
        float sx = MathF.Sin(p.Ay), sz = MathF.Cos(p.Ay);
        foreach (var (f, r) in new[] { (4f, 14f), (-3f, 18f), (9f, 22f) })
            BallSplashAdd(p.X + sz * r + sx * f, p.Z - sx * r + sz * f);
        GD.Print("[sea] splash test: 3 splashes");
    }
}
