using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Formats;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine's "Animals" class (libs/animals, RESOURCE/INI/animals.ini; scripts: animals.c): seagulls circling over a point (location locators "seagull") or round the
/// ship at sea, butterflies in the jungle. Messages: SEAGULLS_SHOW 77100 / HIDE 77101 / ADD 77103 "lfff", FISHSCHOOLS_SHOW 77106 / HIDE 77107 (fish live under the
/// water surface, which this port draws opaque: nothing to show), BUTTERFLIES_SHOW 77108 / HIDE 77109.
/// </summary>
public partial class AnimalsView : Node3D
{
    sealed class Gull { public Node3D Node = null!; public Vector3 Center; public bool FollowCamera; public float Radius, Height, Phase, Omega; }
    sealed class Butterfly { public Node3D Node = null!, Left = null!, Right = null!; public Vector3 Vel; public float Flap, FlapSpeed, Turn; }

    readonly List<Gull> gulls = new();
    readonly List<Butterfly> butterflies = new();
    bool seagullsOn, butterfliesOn;
    PackedScene? gullScene;
    Node3D? gullProto;
    ArrayMesh? wingMesh;
    ShaderMaterial? wingMat;
    readonly RandomNumberGenerator rng = new();

    // animals.ini [seagulls] (defaults of animals_defines.h)
    float gullRadius = 60, gullAngleSpeed = 30, gullHeight = 50, gullDistance = 100;
    int gullAddCount = 20;

    public AnimalsView()
    {
        Name = "Animals";
        rng.Randomize();
        string iniPath = GameAssets.Resource("INI\\animals.ini");
        var ini = File.Exists(iniPath) ? IniFile.Load(iniPath) : new IniFile();
        if (ini.Sections.TryGetValue("seagulls", out var s))
        {
            float F(string k, float def) => float.TryParse(s.Get(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
            gullRadius = F("radius", gullRadius); gullAngleSpeed = F("angle speed", gullAngleSpeed); gullHeight = F("height", gullHeight);
            gullDistance = F("distance", gullDistance); gullAddCount = (int)F("add count", gullAddCount);
        }
    }

    public V OnMessage(V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        switch (msg)
        {
            case 77100: seagullsOn = true; foreach (var g in gulls) g.Node.Visible = true; break;
            case 77101: seagullsOn = false; ClearGulls(); break;                      // HIDE also forgets the flock (a new location starts empty)
            case 77103:                                                              // ADD "lfff": a flock over a point; (0,0,0) = round the camera (at sea)
                if (d.Length >= 4 && seagullsOn) AddFlock(new Vector3(Conv.ToFloat(d[1]), Conv.ToFloat(d[2]), -Conv.ToFloat(d[3])));
                break;
            case 77108: butterfliesOn = true; if (butterflies.Count == 0) SpawnButterflies(); break;
            case 77109: butterfliesOn = false; ClearButterflies(); break;
            case 77106: case 77107: break;                                            // fish schools: below the water surface
        }
        return V.Int(1);
    }

    /// <summary>The class was deleted (DeleteAnimals): no flocks, nothing shown.</summary>
    public void Clear() { seagullsOn = butterfliesOn = false; ClearGulls(); ClearButterflies(); }

    void ClearGulls() { foreach (var g in gulls) g.Node.QueueFree(); gulls.Clear(); }
    void ClearButterflies() { foreach (var b in butterflies) b.Node.QueueFree(); butterflies.Clear(); }

    // ------------------------------------------------------------------ seagulls
    Node3D? NewGull()
    {
        if (gullProto == null)
        {
            string f = GameAssets.Resource("MODELS\\seagull.gm");
            if (!File.Exists(f)) return null;
            try { gullProto = ModelFactory.Build(GmFile.Parse(File.ReadAllBytes(f)), "seagull", "", null); }
            catch (Exception e) { GD.PrintErr("[animals] seagull: " + e.Message); return null; }
        }
        return (Node3D)gullProto.Duplicate();
    }

    void AddFlock(Vector3 center)
    {
        bool camera = center.LengthSquared() < 1e-6f;
        var cam = GetViewport().GetCamera3D();
        int n = Math.Max(1, camera ? gullAddCount / 2 : gullAddCount / 3);
        for (int i = 0; i < n; i++)
        {
            var node = NewGull();
            if (node == null) return;
            var g = new Gull
            {
                Node = node, Center = camera ? (cam?.GlobalPosition ?? Vector3.Zero) : center, FollowCamera = camera,
                Radius = 6f + rng.Randf() * gullRadius * 0.5f, Height = (camera ? 25f : 6f) + rng.Randf() * gullHeight * 0.6f,
                Phase = rng.Randf() * Mathf.Tau,
                Omega = Mathf.DegToRad(4f + rng.Randf() * gullAngleSpeed * 0.2f) * (rng.Randf() < 0.5f ? -1f : 1f),
            };
            if (camera) g.Center = new Vector3(g.Center.X + (rng.Randf() - 0.5f) * gullDistance * 2, 0, g.Center.Z + (rng.Randf() - 0.5f) * gullDistance * 2);
            AddChild(node);
            gulls.Add(g);
        }
        GD.Print($"[animals] {n} seagulls over {(camera ? "the camera" : center.ToString())}, {gulls.Count} in all");
    }

    // ------------------------------------------------------------------ butterflies (t_butterfly.cpp: two wings of a 0.05 m quad that flap about the body axis, a 4x4 sheet butter.tga)
    void SpawnButterflies()
    {
        var tex = GameAssets.Texture("butter.tga");
        if (tex == null) return;
        const string sh = @"shader_type spatial; render_mode unshaded, cull_disabled, depth_draw_always;
uniform sampler2D tex : source_color, filter_linear_mipmap;
void fragment() { vec4 c = texture(tex, UV); ALBEDO = c.rgb; ALPHA = c.a; ALPHA_SCISSOR_THRESHOLD = 0.4; }";
        wingMat ??= new ShaderMaterial { Shader = new Shader { Code = sh } };
        wingMat.SetShaderParameter("tex", tex);
        for (int i = 0; i < 24; i++)
        {
            int ti = rng.RandiRange(0, 3), tj = rng.RandiRange(0, 3);
            var b = new Butterfly { Node = new Node3D(), Left = new Node3D(), Right = new Node3D(), Flap = rng.Randf() * 6f, FlapSpeed = 18f + rng.Randf() * 10f };
            b.Left.AddChild(Wing(ti, tj, -1)); b.Right.AddChild(Wing(ti, tj, 1));
            b.Node.AddChild(b.Left); b.Node.AddChild(b.Right);
            b.Vel = new Vector3(rng.Randf() - 0.5f, 0, rng.Randf() - 0.5f).Normalized() * (0.8f + rng.Randf() * 0.8f);
            AddChild(b.Node);
            butterflies.Add(b);
            Respawn(b, 14f);
        }
        GD.Print($"[animals] {butterflies.Count} butterflies");
    }

    MeshInstance3D Wing(int ti, int tj, int side)
    {
        const float s = 0.05f, t = 0.25f;
        float u0 = ti * t, v0 = tj * t;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector3 a = new(0, 0, -s), bb = new(0, 0, s), c = new(side * s, 0, -s), dd = new(side * s, 0, s);
        void V(Vector3 p, float u, float v) { st.SetUV(new Vector2(u, v)); st.AddVertex(p); }
        // inner edge (at the body) -> outer edge; the left wing mirrors the texture
        float uIn = side > 0 ? u0 : u0 + t, uOut = side > 0 ? u0 + t : u0;
        V(a, uIn, v0 + t); V(bb, uIn, v0); V(c, uOut, v0 + t);
        V(c, uOut, v0 + t); V(bb, uIn, v0); V(dd, uOut, v0);
        var mi = new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = wingMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        return mi;
    }

    Vector3 FocusPoint()
    {
        var pl = CharacterView.All.Values.FirstOrDefault(c => c.IsPlayer && c.Placed);
        if (pl != null) return pl.Position;
        return GetViewport().GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
    }

    void Respawn(Butterfly b, float radius)
    {
        var f = FocusPoint();
        b.Node.Position = f + new Vector3((rng.Randf() - 0.5f) * 2 * radius, 0.4f + rng.Randf() * 1.8f, (rng.Randf() - 0.5f) * 2 * radius);
    }

    // ------------------------------------------------------------------ per frame
    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var cam = GetViewport().GetCamera3D();
        foreach (var g in gulls)
        {
            if (g.FollowCamera && cam != null)         // at sea the flock stays within gullDistance of the ship
            {
                var d = new Vector3(cam.GlobalPosition.X - g.Center.X, 0, cam.GlobalPosition.Z - g.Center.Z);
                if (d.Length() > gullDistance * 2.2f) g.Center += d * 1.6f;
            }
            g.Phase += g.Omega * dt;
            var p = g.Center + new Vector3(Mathf.Cos(g.Phase) * g.Radius, g.Height + Mathf.Sin(g.Phase * 3f) * 0.8f, Mathf.Sin(g.Phase) * g.Radius);
            var tangent = new Vector3(-Mathf.Sin(g.Phase), 0, Mathf.Cos(g.Phase)) * Math.Sign(g.Omega);
            g.Node.Position = p;
            if (tangent.LengthSquared() > 0) g.Node.Rotation = new Vector3(0, Mathf.Atan2(-tangent.X, -tangent.Z) + Mathf.Pi, Mathf.Sin(g.Phase * 5f) * 0.25f - Math.Sign(g.Omega) * 0.35f);
        }
        if (butterfliesOn)
        {
            var f = FocusPoint();
            foreach (var b in butterflies)
            {
                b.Flap += dt * b.FlapSpeed;
                float ang = Mathf.Sin(b.Flap) * 0.9f + 0.4f;
                b.Left.Rotation = new Vector3(0, 0, -ang); b.Right.Rotation = new Vector3(0, 0, ang);
                b.Turn -= dt;
                if (b.Turn <= 0) { b.Turn = 0.5f + rng.Randf() * 2f; b.Vel = (b.Vel + new Vector3(rng.Randf() - 0.5f, 0, rng.Randf() - 0.5f) * 1.4f).Normalized() * (0.8f + rng.Randf()); }
                var np = b.Node.Position + b.Vel * dt + new Vector3(0, Mathf.Sin(b.Flap * 0.3f) * 0.4f * dt, 0);
                np.Y = Mathf.Clamp(np.Y, f.Y + 0.3f, f.Y + 2.6f);
                b.Node.Position = np;
                b.Node.Rotation = new Vector3(0, Mathf.Atan2(-b.Vel.X, -b.Vel.Z), 0);
                if (new Vector2(np.X - f.X, np.Z - f.Z).Length() > 22f) Respawn(b, 14f);
            }
        }
    }
}
