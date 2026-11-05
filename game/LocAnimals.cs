using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Formats;

namespace SeaDogs;

/// <summary>
/// Small creatures of a location, created by the location's EX_MSG commands (locations_loader.c): AddRats n (loc_rats.cpp), AddLizards (lizards.cpp), AddEagle
/// (loc_eagle.cpp), AddCrabs n (no crab model in the data), AddFlys x y z (location_effects.cpp: fireflies round a lamp). Rats and lizards walk on the walking
/// patch of the location and run from the player; the eagle circles over the middle of the patch. Positions are kept in Direct3D space (z of Godot = -z).
/// </summary>
public partial class LocAnimals : Node3D
{
    sealed class Walker
    {
        public CharacterRig Rig = null!;
        public Vector3 Pos;                 // Direct3D coordinates
        public float Ay, TargetAy;
        public float Timer;                 // time left in the current state
        public bool Moving, Panic, IsRat;
        public float Speed;
    }

    readonly PtcFile? patch;
    readonly Func<Vector3?> player;         // Godot position of the player
    readonly RandomNumberGenerator rng = new();
    readonly List<Walker> walkers = new();
    CharacterRig? eagle;
    float eagleAngle, eagleRadius, eagleHeight;
    Vector3 eagleCenter;                    // Godot
    readonly List<(Node3D node, Vector3 center, float r, Vector3 freq, Vector3 phase)> flies = new();

    static LocAnimals? last;
    /// <summary>Test hook (SEADOGS_LANDCAM=animal|rat|eagle): where the first walker / the eagle is (Godot).</summary>
    public static Vector3? DebugFocus(string what) => last is { } l && GodotObject.IsInstanceValid(l) ? (what == "eagle" ? l.eagle?.Position : what == "rat" ? l.walkers.LastOrDefault()?.Rig.Position : l.walkers.FirstOrDefault()?.Rig.Position) : null;

    public LocAnimals(PtcFile? patch, Func<Vector3?> player)
    {
        Name = "LocAnimals";
        this.patch = patch;
        this.player = player;
        rng.Randomize();
        last = this;
    }

    /// <summary>Middle of the patch (ptc.middle) for MSG_LOCATION_EX_MSG "GetPatchMiddlePos".</summary>
    public static Vector3 PatchMiddle(PtcFile? p) => p == null ? Vector3.Zero : new Vector3((p.Min[0] + p.Max[0]) / 2, (p.Min[1] + p.Max[1]) / 2, (p.Min[2] + p.Max[2]) / 2);

    Vector3? RandomPatchPoint(Vector3? avoid, float minDist)
    {
        if (patch == null || patch.Tris.Length == 0) return null;
        for (int tries = 0; tries < 30; tries++)
        {
            var t = patch.Tris[rng.RandiRange(0, patch.Tris.Length - 1)];
            float a = rng.Randf(), b = rng.Randf();
            if (a + b > 1) { a = 1 - a; b = 1 - b; }
            float c = 1 - a - b;
            var p = new Vector3(a * patch.Vx[t.A] + b * patch.Vx[t.B] + c * patch.Vx[t.C], a * patch.Vy[t.A] + b * patch.Vy[t.B] + c * patch.Vy[t.C], a * patch.Vz[t.A] + b * patch.Vz[t.B] + c * patch.Vz[t.C]);
            if (avoid is { } av && new Vector2(p.X - av.X, p.Z + av.Z).Length() < minDist) continue;
            return p;
        }
        return null;
    }

    // ------------------------------------------------------------------ creation
    public void AddRats(int n) => AddWalkers("rat", "Rat", n, true);
    public void AddLizards() => AddWalkers("lizard", "Lizard", 2 + rng.RandiRange(0, 3), false);

    void AddWalkers(string model, string anim, int n, bool rat)
    {
        n = Math.Clamp(n, 1, 40);
        int made = 0;
        for (int i = 0; i < n; i++)
        {
            var at = RandomPatchPoint(player(), 2.5f);
            if (at == null) break;
            var rig = CharacterRig.Create(model, anim, null, "Animals");
            if (rig == null) return;
            AddChild(rig);
            var w = new Walker { Rig = rig, Pos = at.Value, Ay = rng.Randf() * Mathf.Tau, IsRat = rat, Timer = 0.5f + rng.Randf() * 3f };
            w.TargetAy = w.Ay;
            if (rat) rig.Play("idle_stand", false); else { rig.Play("run", true); rig.TimeScale = 0; }
            Place(w);
            walkers.Add(w);
            made++;
        }
        GD.Print($"[animals] {made} {model}(s) in the location" + (made > 0 ? $", the first at ({walkers[^made].Pos.X:0.0}, {walkers[^made].Pos.Y:0.0}, {-walkers[^made].Pos.Z:0.0}) Godot" : ""));
    }

    public void AddEagle()
    {
        var mid = PatchMiddle(patch);
        eagle = CharacterRig.Create("eagle", "Eagle", null, "Animals");
        if (eagle == null) return;
        eagle.Play("flight", true);
        AddChild(eagle);
        eagleCenter = new Vector3(mid.X, mid.Y, -mid.Z);
        eagleRadius = 20f + rng.Randf() * 15f;
        eagleHeight = 28f + rng.Randf() * 12f;
        eagleAngle = rng.Randf() * Mathf.Tau;
        GD.Print($"[animals] eagle over {eagleCenter}");
    }

    /// <summary>AddFlys x y z (Direct3D): 1..8 fireflies round the lamp, radius 0.6 m.</summary>
    public void AddFlys(float x, float y, float z)
    {
        var tex = GameAssets.Texture("LocEfx\\firefly.tga");
        int n = 1 + rng.RandiRange(0, 7);
        for (int i = 0; i < n; i++)
        {
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, AlbedoTexture = tex, AlbedoColor = new Color(1f, 0.95f, 0.6f),
                NoDepthTest = false,
            };
            var mi = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(0.09f, 0.09f) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(mi);
            flies.Add((mi, new Vector3(x, y, -z), 0.6f, new Vector3(1.2f + rng.Randf() * 2f, 1.5f + rng.Randf() * 2f, 1f + rng.Randf() * 2f), new Vector3(rng.Randf(), rng.Randf(), rng.Randf()) * Mathf.Tau));
        }
    }

    public void DelFlys() { foreach (var f in flies) f.node.QueueFree(); flies.Clear(); }

    // ------------------------------------------------------------------ movement
    void Place(Walker w)
    {
        w.Rig.Position = new Vector3(w.Pos.X, w.Pos.Y, -w.Pos.Z);
        w.Rig.Rotation = new Vector3(0, -w.Ay, 0);
    }

    bool Inside(float x, float y, float z, out float gy)
    {
        gy = y;
        if (patch == null || !patch.Snap(x, y, z, out float ox, out float oy, out float oz)) return false;
        gy = oy;
        return Math.Abs(ox - x) < 0.01f && Math.Abs(oz - z) < 0.01f && Math.Abs(oy - y) < 0.6f;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var pl = player();
        Vector3? plD = pl is { } pg ? new Vector3(pg.X, pg.Y, -pg.Z) : null;
        foreach (var w in walkers) StepWalker(w, dt, plD);
        if (eagle != null)
        {
            float omega = 8f / eagleRadius;
            eagleAngle += omega * dt;
            eagle.Position = eagleCenter + new Vector3(Mathf.Cos(eagleAngle) * eagleRadius, eagleHeight, Mathf.Sin(eagleAngle) * eagleRadius);
            var tan = new Vector3(-Mathf.Sin(eagleAngle), 0, Mathf.Cos(eagleAngle));
            eagle.Rotation = new Vector3(0, Mathf.Atan2(-tan.X, -tan.Z), -0.25f);
        }
        if (flies.Count > 0)
        {
            float t = (float)(Time.GetTicksMsec() / 1000.0);
            foreach (var (node, c, r, fr, ph) in flies)
                node.Position = c + new Vector3(Mathf.Sin(t * fr.X + ph.X), Mathf.Sin(t * fr.Y + ph.Y) * 0.5f, Mathf.Sin(t * fr.Z + ph.Z)) * r;
        }
    }

    void StepWalker(Walker w, float dt, Vector3? pl)
    {
        float dPl = pl is { } p ? new Vector2(w.Pos.X - p.X, w.Pos.Z - p.Z).Length() : 999f;
        float near = w.IsRat ? 1.0f : 2.5f;
        if (!w.Panic && dPl < near)
        {
            // run away from the player
            w.Panic = true; w.Moving = true; w.Timer = 2f + rng.Randf() * 3f;
            if (pl is { } p0) w.TargetAy = Mathf.Atan2(w.Pos.X - p0.X, w.Pos.Z - p0.Z) + (rng.Randf() - 0.5f) * 0.8f;
            w.Rig.TimeScale = 1f;
            w.Rig.Play(w.IsRat ? "panic_run" : "run", true);
        }
        w.Timer -= dt;
        if (w.Moving)
        {
            float speed = (w.IsRat ? 1.6f : 1.1f) * (w.Panic ? (w.IsRat ? 2.2f : 3f) : 1f);
            // turn towards the target heading
            float diff = Mathf.Wrap(w.TargetAy - w.Ay, -Mathf.Pi, Mathf.Pi);
            w.Ay += Mathf.Clamp(diff, -7f * dt, 7f * dt);
            var step = new Vector3(Mathf.Sin(w.Ay), 0, Mathf.Cos(w.Ay)) * speed * dt;
            var np = w.Pos + step;
            if (Inside(np.X, w.Pos.Y, np.Z, out float gy)) { w.Pos = new Vector3(np.X, gy, np.Z); }
            else { w.TargetAy = w.Ay + Mathf.Pi * (0.5f + rng.Randf()); if (w.Panic && rng.Randf() < 0.3f) w.Timer = 0; }
            if (w.Panic && dPl > 4.5f) { w.Panic = false; w.Rig.Play("run", true); }
            if (w.Timer <= 0)
            {
                w.Moving = false; w.Panic = false;
                w.Timer = 1f + rng.Randf() * 3f;
                if (w.IsRat) w.Rig.Play(rng.Randf() < 0.5f ? "idle" : "eat", true); else w.Rig.TimeScale = 0;
            }
        }
        else if (w.Timer <= 0 && dPl > near)
        {
            // start a new walk in a random direction
            w.Moving = true; w.Timer = 3f + rng.Randf() * 8f; w.TargetAy = rng.Randf() * Mathf.Tau;
            w.Rig.TimeScale = 1f;
            w.Rig.Play("run", true);
        }
        Place(w);
    }
}
