using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Storms of the world map (MSG_WORLDMAP_CREATESTORM 31000 "l", MSG_WORLDMAP_CREATESTORM_TIME 31001 "lf" = life time in seconds). The script decides when
/// (wdmStormGen: one storm at a time, worldMap.storm.num); the engine places the storm stormBrnDistMin..Max from the player, drifts it over the open water
/// (islands are solid for it, like for the ships), fades it with distance (stormViewDistMin..Max), kills it beyond stormDistKill and raises
/// WorldMap_PlayerInStorm (x, z, ay, index) when the player's ship sails into its core, with worldMap.info.playerInStorm = 1 (wdmReloadToSea passes it to
/// SeaLogin as Login.Storm: storm weather, wind damage of AIShip.c, maybe a tornado). worldMap.storm.cur selects the storm whose x/z/time are then readable.
/// Visual: dark cloud puffs of RESOURCE\Textures\WorldMap\clouds.tga (2x2 atlas) and csmoke.tga, rain streaks, lightning bolts of
/// Weather\lightning\lightning_storm.tga and a shadow on the water.
/// </summary>
public partial class WorldMapView
{
    const float StormCore = 34f;           // radius of the cloud mass: inside it the ship is in the storm

    sealed class Storm
    {
        public float X, Z, Dir, Speed, Life, Age, Alpha, Flash, BoltIn, Spin;
        public bool Timed, Reported, Dying;
        public float Live;                   // WdmStorm liveAlpha: 0..1
        public float InTime;
        public Node3D Node = null!;
        public readonly List<(Sprite3D s, float ang, float rad, float h, float size, Color tint)> Puffs = new();
        public ShaderMaterial RainMat = null!, ShadowMat = null!;
        public Sprite3D? Bolt;
        public float BoltLife;
    }

    readonly List<Storm> storms = new();
    float stormClock;
    bool stormsHooked;

    const string StormRainShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
uniform float t;
uniform float alpha;
void vertex() {
    vec3 b = COLOR.rgb;
    float y = fract(b.y - t * 1.4);
    float a = b.x * 6.2832;
    float r = sqrt(b.z) * 30.0;
    vec3 p = vec3(cos(a) * r, y * 20.0, sin(a) * r);
    if (UV.x > 0.5) p += vec3(0.6, 3.4, 0.0);
    VERTEX = p;
}
void fragment() { ALBEDO = vec3(0.72, 0.78, 0.9); ALPHA = alpha * (UV.x > 0.5 ? 0.35 : 0.95); }";

    const string StormShadowShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;
uniform float alpha;
void fragment() {
    float r = length(UV - vec2(0.5)) * 2.0;
    ALBEDO = vec3(0.01, 0.02, 0.05);
    ALPHA = alpha * 0.75 * (1.0 - smoothstep(0.2, 1.0, r));
}";

    void HookStormAttrs()
    {
        stormsHooked = true;
        var root = Root!;
        var st = root.GetOrCreate("storm");
        st.GetOrCreate("num").Value = storms.Count.ToString();
        st.GetOrCreate("cur").OnWrite = _ => PublishStorm();
        root.GetOrCreate("info").GetOrCreate("playerInStorm").Value = "0";
    }

    /// <summary>The script wrote storm.cur: fill x/z/time of that storm.</summary>
    void PublishStorm()
    {
        var root = Root; if (root == null) return;
        var st = root.GetOrCreate("storm");
        st.GetOrCreate("num").Value = storms.Count.ToString();
        int cur = (int)Conv.ParseFloatPrefix(st.Get("cur")?.Value);
        if (cur < 0 || cur >= storms.Count) return;
        var s = storms[cur];
        st.GetOrCreate("x").Value = Conv.FloatToConcat(s.X); st.GetOrCreate("z").Value = Conv.FloatToConcat(s.Z);
        st.GetOrCreate("time").Value = Conv.FloatToConcat(s.Timed ? Math.Max(0f, s.Life) : 1000f);
    }

    /// <summary>31000 / 31001: a new storm on the open water around the player.</summary>
    bool CreateStorm(float life)
    {
        var rnd = Rng;
        float bMin = F(Root, "stormBrnDistMin", 100f), bMax = F(Root, "stormBrnDistMax", 140f);
        float x = 0f, z = 0f;
        bool ok = false;
        for (int k = 0; k < 40 && !ok; k++)
        {
            float a = (float)rnd.NextDouble() * MathF.Tau, r = bMin + (float)rnd.NextDouble() * (bMax - bMin);
            x = shipX + MathF.Sin(a) * r; z = shipZ + MathF.Cos(a) * r;
            ok = land == null || !land.IsLand(x, z);
        }
        if (!ok) { GD.Print("[map] storm: no open water around the player"); return false; }
        // wdm_storm.cpp WdmStorm(): heading for the ship, turned aside by up to WDM_STORM_DIR_DIS (0.6 of a right angle) plus a little of the ship's own
        // heading; speed WDM_STORM_DIR_SPD_MIN..MAX = 5..8
        float dx = shipX - x, dz = shipZ - z, dl = MathF.Sqrt(dx * dx + dz * dz);
        if (dl > 0f) { dx /= dl; dz /= dl; }
        float dev = 0.6f * (1f - 2f * (float)rnd.NextDouble()), ox = dx;
        dx -= dev * dz; dz += dev * ox;
        float sk = (float)rnd.NextDouble() * 0.1f * 0.6f;
        dx += MathF.Sin(shipAy) * sk; dz += MathF.Cos(shipAy) * sk;
        var s = new Storm
        {
            X = x, Z = z, Dir = MathF.Atan2(dx, dz), Speed = 5f + (float)rnd.NextDouble() * 3f,
            Timed = true, Life = life > 0f ? life : 60f + (float)rnd.NextDouble() * 60f, Spin = (rnd.Next(2) == 0 ? -1f : 1f) * (0.05f + (float)rnd.NextDouble() * 0.05f),
            BoltIn = 1f + (float)rnd.NextDouble() * 2f,
        };
        BuildStormNode(s, rnd);
        storms.Add(s);
        Root!.GetOrCreate("storm").GetOrCreate("num").Value = storms.Count.ToString();
        GD.Print($"[map] storm created at ({x:0},{z:0}), {Math.Sqrt((x - shipX) * (x - shipX) + (z - shipZ) * (z - shipZ)):0} from the ship, life {s.Life:0} s");
        return true;
    }

    void BuildStormNode(Storm s, Random rnd)
    {
        s.Node = new Node3D { Name = "Storm" };
        scene!.AddChild(s.Node);
        var clouds = GameAssets.Texture("WorldMap\\clouds.tga");
        var smoke = SmokeTexture();
        // the cloud mass: big dark puffs on top, lighter smoke lower down, spread over the core
        for (int i = 0; i < 36; i++)
        {
            bool top = i < 24;
            var tex = top ? clouds : smoke;
            if (tex == null) continue;
            Texture2D t = tex;
            float cell = tex.GetWidth();
            if (top) { int q = rnd.Next(4); cell = tex.GetWidth() / 2f; t = new AtlasTexture { Atlas = tex, Region = new Rect2(q % 2 * cell, q / 2 * cell, cell, cell) }; }
            float size = top ? 34f + (float)rnd.NextDouble() * 22f : 24f + (float)rnd.NextDouble() * 12f;
            float rad = (float)Math.Sqrt(rnd.NextDouble()) * (top ? StormCore * 0.95f : StormCore * 0.8f);
            float h = top ? 22f + (float)rnd.NextDouble() * 16f : 13f + (float)rnd.NextDouble() * 7f;
            // tops catch the light, the belly is dark
            float g = top ? 0.08f + (h - 22f) / 16f * 0.3f + (float)rnd.NextDouble() * 0.08f : 0.22f + (float)rnd.NextDouble() * 0.12f;
            var sp = new Sprite3D
            {
                Texture = t, PixelSize = size / cell, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Shaded = false, Transparent = true,
                DoubleSided = true, AlphaCut = SpriteBase3D.AlphaCutMode.Disabled, Name = "Cloud",
            };
            s.Node.AddChild(sp);
            s.Puffs.Add((sp, (float)rnd.NextDouble() * MathF.Tau, rad, h, size, new Color(g, g * 1.04f, g * 1.15f)));
        }
        // rain under the clouds
        int n = 1400;
        var verts = new Vector3[n * 2]; var uv = new Vector2[n * 2]; var col = new Color[n * 2];
        for (int i = 0; i < n; i++)
        {
            var c = new Color((float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble());
            for (int k = 0; k < 2; k++) { verts[i * 2 + k] = Vector3.Zero; uv[i * 2 + k] = new Vector2(k, 0); col[i * 2 + k] = c; }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts; arrays[(int)Mesh.ArrayType.TexUV] = uv; arrays[(int)Mesh.ArrayType.Color] = col;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        s.RainMat = new ShaderMaterial { Shader = new Shader { Code = StormRainShader } };
        s.Node.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = s.RainMat, Name = "StormRain", CustomAabb = new Aabb(new Vector3(-40, -5, -40), new Vector3(80, 45, 80)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        // its shadow on the water
        s.ShadowMat = new ShaderMaterial { Shader = new Shader { Code = StormShadowShader } };
        s.Node.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(StormCore * 3.2f, StormCore * 3.2f) }, MaterialOverride = s.ShadowMat, Position = new Vector3(0, 0.2f, 0), Name = "StormShadow", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    static ImageTexture? smokeTex;

    /// <summary>csmoke.tga is R5G6B5 (no alpha: a white puff on black, drawn additively by the original); here its brightness becomes the alpha of a white puff.</summary>
    static ImageTexture? SmokeTexture()
    {
        if (smokeTex != null) return smokeTex;
        var src = GameAssets.Texture("WorldMap\\csmoke.tga")?.GetImage();
        if (src == null) return null;
        var img = (Image)src.Duplicate();
        if (img.IsCompressed()) img.Decompress();
        img.Convert(Image.Format.Rgba8);
        for (int y = 0; y < img.GetHeight(); y++)
            for (int x = 0; x < img.GetWidth(); x++)
            {
                float l = img.GetPixel(x, y).V;
                img.SetPixel(x, y, new Color(1f, 1f, 1f, l));
            }
        return smokeTex = ImageTexture.CreateFromImage(img);
    }

    void RemoveStorm(Storm s)
    {
        s.Node.QueueFree(); storms.Remove(s);
        if (Root != null) Root.GetOrCreate("storm").GetOrCreate("num").Value = storms.Count.ToString();
    }

    void ClearStorms()
    {
        foreach (var s in storms) s.Node.QueueFree();
        storms.Clear(); stormsHooked = false;
    }

    /// <summary>Moves the storms, animates them and reports the player entering one. Returns false if the script left the map.</summary>
    bool TickStorms(float dt)
    {
        if (!stormsHooked) HookStormAttrs();
        stormClock += dt;
        float viewMin = F(Root, "stormViewDistMin", 90f), viewMax = F(Root, "stormViewDistMax", 180f), kill = F(Root, "stormDistKill", 190f);
        bool inStorm = false;
        Storm? hit = null;
        foreach (var s in storms.ToList())
        {
            s.Age += dt;
            // WdmStorm::Update: born at +0.1 alpha per second; when the life is over or the ship is stormDistKill away it fades at -0.3 per second, then goes
            if (s.Timed) s.Life -= dt;
            float dk = MathF.Sqrt((s.X - shipX) * (s.X - shipX) + (s.Z - shipZ) * (s.Z - shipZ));
            if (!s.Dying && ((s.Timed && s.Life <= 0f) || dk >= kill)) { s.Dying = true; GD.Print(dk >= kill ? "[map] storm left far behind" : "[map] storm died out"); }
            if (s.Dying) { s.Live -= 0.3f * dt; if (s.Live <= 0f) { RemoveStorm(s); continue; } }
            else s.Live = Math.Min(1f, s.Live + 0.1f * dt);
            // drift; islands are solid: turn away from land ahead, never move onto it
            if (land != null && land.LandAhead(s.X, s.Z, s.Dir, StormCore + 12f)) s.Dir += 0.9f * dt;
            float nx = s.X + MathF.Sin(s.Dir) * s.Speed * dt, nz = s.Z + MathF.Cos(s.Dir) * s.Speed * dt;
            if (land == null || !land.IsLand(nx, nz)) { s.X = nx; s.Z = nz; }
            else s.Dir += 1.5f * dt;
            float d = MathF.Sqrt((s.X - shipX) * (s.X - shipX) + (s.Z - shipZ) * (s.Z - shipZ));
            // visibility: the life alpha times the distance alpha (1 inside stormViewDistMin, 0 at stormViewDistMax)
            float a = s.Live;
            float view = 1f - Math.Clamp((d - viewMin) / Math.Max(1f, viewMax - viewMin), 0f, 1f);
            s.Alpha = a * view;
            AnimateStorm(s, dt);
            // the player is in a storm that is not being removed (killMe) and whose clouds are near (CheckIntersection); a storm still fading in counts
            if (d < StormCore && !s.Dying && s.Age > 2f) { inStorm = true; s.InTime += dt; if (hit == null || s.InTime > hit.InTime) hit = s; }
            else { s.InTime = 0f; s.Reported = false; }
        }
        Root!.GetOrCreate("info").GetOrCreate("playerInStorm").Value = inStorm ? "1" : "0";
        if (hit != null && hit.InTime >= 0.4f && !hit.Reported)
        {
            hit.Reported = true;
            hit.InTime = -2.6f;          // if the script cannot leave the map now (wdmLockReload) it is asked again after 3 s
            GD.Print($"[map] the ship sailed into storm {storms.IndexOf(hit)}: WorldMap_PlayerInStorm");
            it.FireEvent("WorldMap_PlayerInStorm", new List<V> { V.Float(shipX), V.Float(shipZ), V.Float(shipAy), V.Int(storms.IndexOf(hit)) });
            if (!Active || !built) return false;
            hit.Reported = false;
        }
        return true;
    }

    void AnimateStorm(Storm s, float dt)
    {
        s.Node.Position = new Vector3(s.X, 0f, -s.Z);
        // lightning: a bolt from the clouds every 1..4 s
        s.BoltIn -= dt;
        if (s.BoltIn <= 0f)
        {
            s.BoltIn = 1f + (float)Rng.NextDouble() * 3f;
            var tex = GameAssets.Texture("Weather\\lightning\\lightning_storm.tga");
            if (tex != null && s.Alpha > 0.2f)
            {
                s.Bolt?.QueueFree();
                float cw = tex.GetWidth() / 4f, ch = tex.GetHeight();
                float ang = (float)Rng.NextDouble() * MathF.Tau, r = (float)Rng.NextDouble() * StormCore * 0.6f;
                s.Bolt = new Sprite3D
                {
                    Texture = new AtlasTexture { Atlas = tex, Region = new Rect2(Rng.Next(4) * cw, 0, cw, ch) }, Billboard = BaseMaterial3D.BillboardModeEnum.FixedY,
                    Shaded = false, Transparent = true, DoubleSided = true, PixelSize = 24f / ch, Position = new Vector3(MathF.Cos(ang) * r, 12f, MathF.Sin(ang) * r), Name = "Bolt",
                };
                s.Node.AddChild(s.Bolt);
                s.BoltLife = 0.35f;
                s.Flash = 1f;
            }
        }
        if (s.Bolt != null)
        {
            s.BoltLife -= dt;
            if (s.BoltLife <= 0f) { s.Bolt.QueueFree(); s.Bolt = null; }
            else s.Bolt.Modulate = new Color(1f, 1f, 1f, s.Alpha * (0.55f + 0.45f * MathF.Sin(s.BoltLife * 70f)));
        }
        s.Flash = Math.Max(0f, s.Flash - dt * 4f);
        // the cloud mass turns slowly; puffs breathe a little
        float t = s.Age;
        foreach (var (sp, ang, rad, h, size, tint) in s.Puffs)
        {
            float a2 = ang + t * s.Spin * (1.2f - rad / StormCore * 0.5f);
            sp.Position = new Vector3(MathF.Cos(a2) * rad, h + MathF.Sin(t * 0.7f + ang * 3f) * 0.8f, MathF.Sin(a2) * rad);
            var c = tint.Lerp(new Color(0.85f, 0.88f, 1f), s.Flash * 0.6f);
            sp.Modulate = new Color(c.R, c.G, c.B, s.Alpha * 0.92f);
        }
        s.RainMat.SetShaderParameter("t", stormClock);
        s.RainMat.SetShaderParameter("alpha", s.Alpha);
        s.ShadowMat.SetShaderParameter("alpha", s.Alpha);
    }

    /// <summary>Test hook --mapstorm=dx,dz@N: a storm (asked from the script if there is none) placed at the ship + (dx, dz).</summary>
    public bool TestStorm(float dx, float dz)
    {
        if (!built) return false;
        if (storms.Count == 0) it.CallTop("wdmCreateStorm");
        if (storms.Count == 0) CreateStorm(-1f);
        if (storms.Count == 0) return false;
        var s = storms[0];
        s.X = shipX + dx; s.Z = shipZ + dz; s.Age = Math.Max(s.Age, 3f);
        GD.Print($"[map] test: storm placed at ({s.X:0},{s.Z:0})");
        return true;
    }
}
