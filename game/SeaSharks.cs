using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine class "Sharks" (libs/sea_creatures/src/sharks.cpp; created by Sea_Load in SEA_AI/sea.c): 3 to 6 sharks (MODELS\Animals\shark.gm, animation Shark.an:
/// stand, Shark_Swim, Shark_Jump) that swim round the camera at depths of 0 to 20 m, drift towards the viewer when they are further than 10 m, repel each other,
/// dive away from every ship and keep clear of the island. Near the surface a shark leaves a wedge-shaped track (Animals\SharkTrack.tga). The model sits below the sea
/// surface (the sea of this port is opaque), so fins and jumps are what the player sees. The constants are the original's (SHARK_*).
/// </summary>
public partial class SeaView
{
    const float SharkRepulsion = 80f, SharkRepulsionDist = 90f, SharkMaxRSpeed = 8.1f, SharkPullDist = 100f, SharkRepPulDist = 10f, SharkMaxY = 0.01f, SharkMinY = -20f,
        SharkMaxTurn = 0.2f, SharkKrowTurn = 2f, SharkImspdAcc = 0.1f, SharkMaxImspd = 1f, SharkKaxImspd = 0.3f, SharkMaxSpeed = 8f, SharkMinSpeed = 5f;

    sealed class SharkRt
    {
        public Vector3 Pos, Vel, Force, FForce, SPos, Angs, RForce;       // engine coordinates (x, y up, z)
        public float YDir, DirTime, Accs, RTime, Turn, Imspd, Speed = (SharkMaxSpeed + SharkMinSpeed) * 0.5f, ShipY, VBase, AniTime, JumpTime;
        public bool SpeedUp, Pinned;       // Pinned: test hook, ignores ships
        public CharacterRig? Rig;
        public MeshInstance3D? Track;
        public ShaderMaterial? TrackMat;
        public Vector3 IslandForce; public float IslandT;
    }

    readonly List<SharkRt> sharks = new();
    bool sharksBuilt;
    Entity? sharksEnt;
    static Shader? trackShader;
    static ArrayMesh? trackMesh;
    const string TrackShaderCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
uniform sampler2D tex : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform float vbase;
uniform float depth_k;
" + WaveGlsl + @"
void vertex() {
    vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    w.y = sea_h(w.xz) + 0.03;
    VERTEX = (inverse(MODEL_MATRIX) * vec4(w, 1.0)).xyz;
}
void fragment() {
    vec4 c = texture(tex, vec2(UV.x, UV.y - vbase));
    ALBEDO = c.rgb;
    ALPHA = c.a * COLOR.a * depth_k;
}";

    void TickSharks(float dt)
    {
        if (sharksEnt == null || sharksEnt.Deleted) { sharksEnt = fxEntity("Sharks"); if (sharksEnt == null) { if (sharksBuilt) ClearSharks(); return; } }
        if (!sharksBuilt) BuildSharks();
        if (sharks.Count == 0) return;
        dt = Math.Min(dt, 0.1f);
        var cp = Cam.GlobalPosition; float camX = cp.X, camZ = -cp.Z;                                    // engine coordinates
        foreach (var s in sharks) SharkReset(s, camX, camZ);
        for (int i = 0; i < sharks.Count - 1; i++)
            for (int j = i + 1; j < sharks.Count; j++) SharkRepel(sharks[i], sharks[j]);
        foreach (var sh in ships)
        {
            float rd2 = (sh.Width * sh.Width + sh.Length * sh.Length) * 3f;
            foreach (var s in sharks) if (!s.Pinned) SharkShip(s, sh.X, sh.Z, rd2);
        }
        foreach (var s in sharks) SharkStep(s, camX, camZ, dt);
    }

    void BuildSharks()
    {
        sharksBuilt = true;
        int n = 3 + fxRnd.Next(4);
        trackShader ??= new Shader { Code = TrackShaderCode };
        if (trackMesh == null)
        {
            // SharkTrack: a wedge that starts at the nose and widens to the tail (10 vertices, 30 indices of the original)
            var v = new Vector3[10]; var uv = new Vector2[10]; var col = new Color[10];
            var s0 = new Vector3(0f, 0f, -0.75f);                                 // engine z forward = -z here
            v[0] = s0; v[7] = s0 + new Vector3(-1.5f, 0f, 6f); v[9] = s0 + new Vector3(1.5f, 0f, 6f); v[8] = (v[7] + v[9]) * 0.5f;
            v[1] = s0 + (v[7] - s0) * 0.333f; v[2] = s0 + (v[8] - s0) * 0.333f; v[3] = s0 + (v[9] - s0) * 0.333f;
            v[4] = s0 + (v[7] - s0) * 0.666f; v[5] = s0 + (v[8] - s0) * 0.666f; v[6] = s0 + (v[9] - s0) * 0.666f;
            float[] us = { 0.5f, 0f, 0.5f, 1f, 0f, 0.5f, 1f, 0f, 0.5f, 1f }, vs = { 0f, 0.333f, 0.333f, 0.333f, 0.666f, 0.666f, 0.666f, 1f, 1f, 1f };
            for (int i = 0; i < 10; i++) { uv[i] = new Vector2(us[i], vs[i]); col[i] = new Color(1f, 1f, 1f, i >= 7 ? 0f : 0.31f); }
            int[] idx = { 0, 1, 2, 0, 2, 3, 1, 4, 2, 2, 4, 5, 2, 5, 6, 2, 6, 3, 4, 7, 8, 4, 8, 5, 5, 8, 6, 6, 8, 9 };
            var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = v; arrays[(int)Mesh.ArrayType.TexUV] = uv; arrays[(int)Mesh.ArrayType.Color] = col; arrays[(int)Mesh.ArrayType.Index] = idx;
            trackMesh = new ArrayMesh(); trackMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            trackMesh.CustomAabb = new Aabb(new Vector3(-8, -5, -8), new Vector3(16, 10, 16));
        }
        var tex = GameAssets.Texture("Animals\\SharkTrack.tga");
        for (int i = 0; i < n; i++)
        {
            var s = new SharkRt { Accs = 0.007f + Rand(0.008f), JumpTime = 10f + Rand(30f) };
            float radius = SharkPullDist * (0.4f + Rand(1.6f)), ang = MathF.PI * Rand(2f);
            s.Pos = new Vector3(radius * MathF.Cos(ang), SharkMinY + (SharkMaxY - SharkMinY) * Rand(1f), radius * MathF.Sin(ang));
            s.SPos = s.Pos; s.Angs.Y = MathF.PI * Rand(2f);
            s.Rig = CharacterRig.Create("shark", "Shark", null, "Animals");
            if (s.Rig != null) { AddChild(s.Rig); s.Rig.Play("stand", true); }
            var mat = new ShaderMaterial { Shader = trackShader };
            mat.SetShaderParameter("tex", tex);
            s.TrackMat = mat;
            s.Track = new MeshInstance3D { Mesh = trackMesh, MaterialOverride = mat, Visible = false, Name = "SharkTrack", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(s.Track);
            sharks.Add(s);
        }
        GD.Print($"[sea] sharks: {sharks.Count}" + (sharks.Count > 0 && sharks[0].Rig == null ? " (no model)" : ""));
    }

    void ClearSharks()
    {
        foreach (var s in sharks) { s.Rig?.QueueFree(); s.Track?.QueueFree(); }
        sharks.Clear(); sharksBuilt = false; sharksEnt = null;
    }

    void SharkReset(SharkRt s, float camX, float camZ)
    {
        s.Force = Vector3.Zero; s.FForce = Vector3.Zero;
        float dx = camX - s.Pos.X, dz = camZ - s.Pos.Z, max = SharkPullDist * 4f;
        if (dx * dx + dz * dz > max * max)
        {
            float radius = SharkPullDist * (0.4f + Rand(1.6f)), ang = MathF.PI * Rand(2f);
            s.Pos = new Vector3(camX + radius * MathF.Cos(ang), SharkMinY * 4f, camZ + radius * MathF.Sin(ang));
            s.SPos.X = s.Pos.X; s.SPos.Z = s.Pos.Z;
        }
        s.ShipY = SharkMaxY;
    }

    static void SharkRepel(SharkRt a, SharkRt b)
    {
        var v = a.Pos - b.Pos; float k = v.LengthSquared();
        if (k > 0f && k < SharkRepulsionDist * SharkRepulsionDist) { v *= SharkRepulsion / k; a.Force += v; b.Force -= v; }
        v = a.SPos - b.SPos; k = v.LengthSquared();
        if (k > 0f && k < 15f * 15f) { v *= 5f / k; a.FForce += v; b.FForce -= v; }
    }

    static void SharkShip(SharkRt s, float x, float z, float r2)
    {
        float dx = x - s.SPos.X, dz = z - s.SPos.Z, d = dx * dx + dz * dz, fy = 0f;
        if (d < r2)
        {
            float k = MathF.Sqrt(d / r2);
            fy = -(1f - k) * 100f;
            s.ShipY = SharkMinY + (SharkMaxY - SharkMinY) * k;
        }
        if (dx * s.Vel.X + dz * s.Vel.Z < 0f) fy *= 0.5f;
        s.Force.Y += fy; s.FForce.Y += fy;
    }

    /// <summary>Sharks::Shark::IslandCollision: probes on three rings round the shark; land under a probe pushes it away.</summary>
    Vector3 SharkIsland(SharkRt s)
    {
        if (islandBody == null) return Vector3.Zero;
        var f = Vector3.Zero;
        foreach (var (num, rad, frc) in new[] { (16, 5f, 100f), (12, 30f, 50f), (8, 50f, 20f) })
        {
            float vx = 0f, vz = 0f, step = MathF.Tau / num;
            for (int i = 0; i < num; i++)
            {
                float x = MathF.Sin(i * step), z = MathF.Cos(i * step);
                if (LandHeight(s.SPos.X + rad * x, s.SPos.Z + rad * z) > -3f) { vx -= x; vz -= z; }
            }
            float l = vx * vx + vz * vz;
            if (l > 1f) { l = 1f / MathF.Sqrt(l); vx *= l; vz *= l; }
            f.X += vx * frc; f.Z += vz * frc;
        }
        return f;
    }

    /// <summary>Sharks::Shark::Coordination.</summary>
    void SharkStep(SharkRt s, float camX, float camZ, float dt)
    {
        float l = s.Force.LengthSquared();
        if (l > 1600f) s.Force *= 40f / MathF.Sqrt(l);
        s.RTime -= dt;
        if (s.RTime < 0f)
        {
            float radius = Rand(10f), ang = MathF.PI * Rand(2f);
            s.RForce = new Vector3(radius * MathF.Sin(ang), 0f, radius * MathF.Cos(ang));
            s.RTime = 1f + Rand(10f);
        }
        s.Force += s.RForce;
        float vx = camX - s.Pos.X, vz = camZ - s.Pos.Z, k = vx * vx + vz * vz;
        if (k < SharkRepPulDist * SharkRepPulDist) { s.Force.X -= vx / SharkRepPulDist; s.Force.Z -= vz / SharkRepPulDist; }
        else
        {
            vx *= 0.1f; vz *= 0.1f; l = MathF.Sqrt(k) * 0.1f;
            if (l > 400f) { l = 20f / l; vx *= l; vz *= l; }
            s.Force.X += vx; s.Force.Z += vz;
        }
        s.Force.Y += 0.4f * (s.YDir - s.Pos.Y);
        s.Force += new Vector3(RandCentered(1f), RandCentered(1f), RandCentered(1f)) * 0.001f;
        s.Vel += s.Accs * s.Force * dt;
        l = s.Vel.LengthSquared();
        if (l > SharkMaxRSpeed * SharkMaxRSpeed) s.Vel *= SharkMaxRSpeed / MathF.Sqrt(l);
        s.Pos += s.Vel * dt;
        s.Pos.Y = Math.Clamp(s.Pos.Y, -20f, 0.1f);
        s.DirTime -= dt;
        if (s.DirTime <= 0f)
        {
            float p = Rand(1f);
            s.YDir = SharkMinY + (SharkMaxY - SharkMinY) * (1f - p * p);
            s.DirTime = 5f + 10f * Rand(1f);
        }
        var fff = s.Pos - s.SPos;
        l = fff.LengthSquared();
        if (l > 1f) fff *= 1f / MathF.Sqrt(l);
        l = s.FForce.LengthSquared();
        if (l > 100f) s.FForce *= 10f / MathF.Sqrt(l);
        s.FForce += fff;
        s.IslandT -= dt;
        if (s.IslandT <= 0f) { s.IslandT = 0.25f; s.IslandForce = SharkIsland(s); }
        s.FForce += s.IslandForce;
        float sx = MathF.Sin(s.Angs.Y), sz = MathF.Cos(s.Angs.Y);
        vx = s.FForce.X; vz = s.FForce.Z;
        l = vx * vx + vz * vz;
        if (l > 0f) { l = 1f / MathF.Sqrt(l); vx *= l; vz *= l; } else { vx = sx; vz = sz; }
        float sn = sx * vz - sz * vx, cs = sx * vx + sz * vz;
        s.Turn = Math.Clamp(s.Turn - 0.4f * sn * dt, -SharkMaxTurn, SharkMaxTurn);
        s.Angs.Y += s.Turn * dt;
        s.Angs.Z = -s.Turn * SharkKrowTurn;
        s.Speed = Math.Clamp(s.Speed + 0.3f * cs * dt, SharkMinSpeed, SharkMaxSpeed);
        s.SpeedUp = cs > 0f;
        if (s.FForce.Y < 0f)
        {
            if (s.SPos.Y > SharkMinY) s.Imspd = Math.Max(-SharkMaxImspd, s.Imspd - SharkImspdAcc * dt);
            else s.Imspd += SharkImspdAcc * dt;
        }
        else s.Imspd = Math.Min(SharkMaxImspd, s.Imspd + SharkImspdAcc * dt);
        s.Angs.X = -s.Imspd * SharkKaxImspd;
        s.SPos.X += s.Speed * MathF.Sin(s.Angs.Y) * dt; s.SPos.Y += s.Imspd * dt; s.SPos.Z += s.Speed * MathF.Cos(s.Angs.Y) * dt;
        var rpos = s.SPos;
        float seaY = SeaWaves.Height(rpos.X, -rpos.Z);
        if (rpos.Y > seaY) rpos.Y = seaY;
        if (rpos.Y > s.ShipY) rpos.Y = s.ShipY;
        float ax = s.Angs.X;
        if (rpos.Y > -3f)
        {
            float q = Math.Min(rpos.Y, 0f);
            q = 1f - (3f + q) / 3f;
            ax *= q * q;
        }
        s.AniTime -= dt; s.JumpTime -= dt;
        s.VBase += 0.2f * s.Speed * dt;
        if (s.VBase > 1f) s.VBase -= 1f;
        if (s.Rig != null)
        {
            s.Rig.Position = new Vector3(rpos.X, rpos.Y, -rpos.Z);
            s.Rig.Rotation = new Vector3(-ax, -s.Angs.Y, -s.Angs.Z);
            if (s.AniTime <= 0f) SharkAnimation(s);
        }
        // the wedge on the surface while the shark is within 1.2 m of it
        if (s.Track != null)
        {
            float y = rpos.Y;
            if (y <= -1.2f) s.Track.Visible = false;
            else
            {
                float kk = (1.2f + Math.Min(y, 0f)) / 1.2f;
                s.Track.Visible = true;
                s.Track.Position = new Vector3(rpos.X, 0f, -rpos.Z);
                s.Track.Rotation = new Vector3(0f, -s.Angs.Y, 0f);
                s.Track.Scale = new Vector3(kk, 1f, kk);
                s.TrackMat!.SetShaderParameter("vbase", s.VBase);
                s.TrackMat.SetShaderParameter("depth_k", 1f);
                SetWaves(s.TrackMat);
            }
        }
    }

    /// <summary>Sharks::Shark::Event: at the end of an animation the next one is chosen (swim, stand, or a jump when the shark is at the surface).</summary>
    void SharkAnimation(SharkRt s)
    {
        int rnd = fxRnd.Next();
        string cur = s.Rig!.ActionName, act = cur; bool loop = true;
        if ((s.Angs.X > 0f && (rnd & 1) != 0) || s.SpeedUp) { act = "Shark_Swim"; s.AniTime = 3f + Rand(3f); }
        if (cur == "Shark_Swim" || (rnd & 1) != 0) { act = "stand"; s.AniTime = 3f + Rand(3f); }
        else if (s.JumpTime <= 0f && s.SPos.Y > -0.1f && (rnd & 0xf0) != 0) { act = "Shark_Jump"; loop = false; s.JumpTime = 60f + Rand(100f); s.AniTime = 47f / Math.Max(1f, s.Rig.Fps); }
        else { act = "Shark_Swim"; s.AniTime = 2f + Rand(3f); }
        s.Rig!.Play(act, loop, true);
    }

    /// <summary>Test hook --sharks@N (=jump: also starts the jump animation): puts the first shark 14 m abeam of the player's ship at the surface so the camera sees fin, track and jump.</summary>
    public void SharkTest(string what)
    {
        var p = ships.FirstOrDefault(x => x.IsPlayer);
        if (p == null) { GD.Print("[sea] sharks test: no ship"); return; }
        if (!sharksBuilt) { sharksEnt ??= fxEntity("Sharks"); if (sharksEnt == null) { GD.Print("[sea] sharks test: no Sharks entity"); return; } BuildSharks(); }
        if (sharks.Count == 0) return;
        float sx = MathF.Sin(p.Ay), sz = MathF.Cos(p.Ay);
        var s = sharks[0];
        s.Pos = new Vector3(p.X + sz * 14f + sx * 6f, -0.3f, p.Z - sx * 14f + sz * 6f);
        s.SPos = s.Pos; s.Angs = new Vector3(0f, p.Ay, 0f); s.Vel = Vector3.Zero; s.YDir = -0.3f; s.DirTime = 100f; s.Imspd = 0f; s.Pinned = true;
        if (s.Rig != null)
        {
            bool jump = what.Contains("jump");
            s.Rig.Play(jump ? "Shark_Jump" : "Shark_Swim", !jump, true);
            s.AniTime = jump ? 47f / Math.Max(1f, s.Rig.Fps) : 100f;
        }
        GD.Print($"[sea] sharks test: shark 0 at {s.Pos.X:F1},{s.Pos.Z:F1} ({what})");
    }
}
