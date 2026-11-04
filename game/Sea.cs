using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Formats;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Sea mode. The scripts (SEA_AI/*.c) create the engine entities sea_ai, Ship, Island, SEA_CAMERAS ... and talk to them with messages;
/// this class implements them: the ships (model, movement from the script-computed Ship.MaxSpeedZ / MaxSpeedY and the sail state), the island
/// geometry and the third-person ship camera. Coordinates: the engine is left-handed, x/z are the sea plane, ay = heading (forward = sin ay, cos ay);
/// Godot z = -engine z, node yaw = -ay.
/// </summary>
public partial class SeaView : Node3D
{
    // ---- message ids (SEA_AI/Script_Defines.h, messages.h)
    const int AI_ADD_SHIP = 51001, AI_GROUP_XYZ_AY = 51010, AI_GROUP_COMMANDER = 51022, AI_UNLOAD = 51025, AI_SHIP_SAIL_STATE = 51021;
    const int ISLAND_LOAD_GEO = 50100, ISLAND_SET_GEO = 50101;

    sealed class ShipRt
    {
        public Attr Chr = null!;
        public int Index;
        public Node3D Node = null!;
        public float Sail;                 // 0, 0.5, 1
        public float Speed, TurnRate;      // current forward speed and yaw rate
        public float X, Z, Ay;
        public float ParamTimer;
        public bool IsPlayer, Aground;
        public float Length = 30f, Height = 12f;
        public List<Node3D> Sails = new();
    }

    readonly Interp it;
    public readonly Camera3D Cam = new() { Far = 12000f, Near = 0.5f, Fov = 62f, Name = "SeaCamera" };
    readonly List<ShipRt> ships = new();
    readonly Dictionary<string, (float x, float z, float ay)> groupPos = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Attr> groupCommander = new(StringComparer.OrdinalIgnoreCase);
    Node3D? island;
    float camYawOff, camPitch = 0.30f, camDist = -1f;
    bool camInit;
    int logCounter;
    float situationTimer = 1f;

    public bool Active => ships.Count > 0 || island != null || it.Entities.Any(e => !e.Deleted && e.Class.Equals("sea_ai", StringComparison.OrdinalIgnoreCase));
    public Vector3 PlayerPosition => ships.FirstOrDefault(s => s.IsPlayer) is { } p ? new Vector3(p.X, 0, -p.Z) : Vector3.Zero;

    readonly SeaHud hud = new() { Name = "SeaHud" };

    public SeaView(Interp interp)
    {
        it = interp;
        Name = "SeaView";
        AddChild(hud);
        AddChild(Cam);
        Cam.Current = false;
    }

    // ------------------------------------------------------------------ helpers
    static float F(Attr? a, string path, float def = 0f) => a?.Find(path, false)?.Value is { } v && v.Length > 0 ? Conv.ParseFloatPrefix(v) : def;
    static void SetF(Attr a, string path, float v) => a.Find(path, true)!.Value = Conv.FloatToConcat(v);

    public void Register(Interp i)
    {
        i.EntityHandlers["sea_ai"] = (e, fmt, d) => OnSeaAi(d);
        i.EntityHandlers["Island"] = (e, fmt, d) => OnIsland(e, d);
        i.EntityHandlers["SEA_CAMERAS"] = (e, fmt, d) => V.Int(1);
        i.EntityHandlers["locator"] = (e, fmt, d) => d.Length > 1 && Conv.ToInt(d[0]) == 1504 && d[1].Attr is { } isl ? LocateIsland(isl) : V.Int(0);
    }

    /// <summary>
    /// LM_LOCATE_I: reads the island's locator model (Islands[n].locators) and fills, for every reload entry lK (name = locator group):
    /// lK.x/y/z/ay = the "Reload" label of that name and lK.ships.l0..l8 = the ship1..ship9 labels of the group (where our ships appear when leaving that port).
    /// </summary>
    static V LocateIsland(Attr island)
    {
        string models = island.Find("filespath.models", false)?.Value?.Replace('/', '\\').Trim('\\') ?? "", loc = island.Get("locators")?.Value ?? "";
        string file = GameAssets.Resource("MODELS\\" + models + "\\" + loc + ".gm");
        if (models.Length == 0 || !File.Exists(file)) return V.Int(0);
        GmFile gm;
        try { gm = GmFile.Parse(File.ReadAllBytes(file)); } catch { return V.Int(0); }
        var reload = island.Get("reload");
        if (reload == null) return V.Int(1);
        foreach (var l in reload.Children)
        {
            string group = l.Get("name")?.Value ?? "";
            if (group.Length == 0) continue;
            var at = gm.Labels.FirstOrDefault(x => x.Group.Equals("Reload", StringComparison.OrdinalIgnoreCase) && x.Name.Equals(group, StringComparison.OrdinalIgnoreCase));
            if (at != null) Put(l, at);
            var ships = gm.Labels.Where(x => x.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && x.Name.StartsWith("ship", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => int.TryParse(x.Name.Substring(4), out var n) ? n : 99).ToList();
            for (int k = 0; k < ships.Count; k++) Put(l.GetOrCreate("ships").GetOrCreate("l" + k), ships[k]);
        }
        return V.Int(1);
    }

    /// <summary>Saves keep the island attributes the engine filled at start-up only if they were written by this port: refill them after a load.</summary>
    public void RelocateIslands()
    {
        if (it.Globals.TryGetValue("Islands", out var g) && g.Arr != null)
            foreach (var slot in g.Arr)
                if (slot.Val.Attr is { } a && a.Get("locators") != null) LocateIsland(a);
    }

    static void Put(Attr a, GmFile.Label l)
    {
        SetF(a, "x", l.M[12]); SetF(a, "y", l.M[13]); SetF(a, "z", l.M[14]);
        SetF(a, "ay", MathF.Atan2(l.M[8], l.M[10]));
    }

    public void OnEntityDeleted(Entity e)
    {
        if (e.Class.Equals("sea_ai", StringComparison.OrdinalIgnoreCase) || e.Class.Equals("Island", StringComparison.OrdinalIgnoreCase))
        {
            if (e.Class.Equals("Island", StringComparison.OrdinalIgnoreCase)) { island?.QueueFree(); island = null; }
            else Clear();
        }
    }

    void Clear()
    {
        foreach (var s in ships) s.Node.QueueFree();
        ships.Clear(); groupPos.Clear(); groupCommander.Clear();
        island?.QueueFree(); island = null;
        camInit = false; camDist = -1f;
        Cam.Current = false;
        hud.Set(false, "", 0, 0, 0, "");
    }

    // ------------------------------------------------------------------ entity: sea_ai
    V OnSeaAi(V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        string S(int i) => i < d.Length ? Conv.ToStr(d[i]) : "";
        float Fl(int i) => i < d.Length ? Conv.ToFloat(d[i]) : 0f;
        switch (msg)
        {
            case AI_GROUP_XYZ_AY: groupPos[S(1)] = (Fl(2), Fl(4), Fl(5)); return V.Int(1);
            case AI_GROUP_COMMANDER: if (d.Length > 2 && d[2].Attr is { } cmd) { groupCommander[S(1)] = cmd; cmd.Find("SeaAI.Group.Name", true)!.Value = S(1); } return V.Int(1);
            case AI_ADD_SHIP: if (d.Length > 3 && d[2].Attr is { } chr && d[3].Attr is { } type) AddShip(Conv.ToInt(d[1]), chr, type); return V.Int(1);
            case AI_SHIP_SAIL_STATE:                                                      // "lif": character index, sail state
            {
                int idx = Conv.ToInt(d.Length > 1 ? d[1] : V.Int(-1));
                if (ships.FirstOrDefault(s => s.Index == idx) is { } sh) sh.Sail = Math.Clamp(Fl(2), 0f, 1f);
                return V.Int(1);
            }
            case AI_UNLOAD: Clear(); return V.Int(1);
        }
        return V.Int(1);
    }

    void AddShip(int index, Attr chr, Attr type)
    {
        string group = chr.Find("SeaAI.Group.Name", false)?.Value ?? "";
        var rt = new ShipRt { Chr = chr, Index = index, IsPlayer = index == 0 };
        // placement: the group leader stands at the group position, the others behind it
        if (groupPos.TryGetValue(group, out var gp))
        {
            int n = ships.Count(s => (s.Chr.Find("SeaAI.Group.Name", false)?.Value ?? "") == group);
            rt.X = gp.x - MathF.Sin(gp.ay) * 90f * n; rt.Z = gp.z - MathF.Cos(gp.ay) * 90f * n; rt.Ay = gp.ay;
        }
        else { rt.X = F(chr, "Ship.Pos.x"); rt.Z = F(chr, "Ship.Pos.z"); rt.Ay = F(chr, "Ship.Ang.y"); }
        string name = type.Get("Name")?.Value ?? "";
        var model = LoadShipModel(name, (int)F(chr, "Nation", 3));
        rt.Node = model.Root; rt.Length = model.Length; rt.Height = model.Height; rt.Sails = model.Sails;
        AddChild(rt.Node);
        // the script sets Ship.Speed.z = 1 for a ship that sails away from the coast and 0 for one standing at the shore: that is the starting sail state
        float sp = F(chr, "Ship.Speed.z");
        rt.Sail = sp >= 0.75f ? 1f : sp >= 0.25f ? 0.5f : 0f;
        rt.Speed = 0f;
        ships.Add(rt);
        Publish(rt);
        it.FireEvent("ShpCreate", new List<V> { V.Int(index), V.A(chr.Get("Ship")) });
        it.FireEvent("Shp_UpdParams", new List<V> { V.Int(index), V.Float(rt.Sail) });
        GD.Print($"[sea] ship {chr.Get("id")?.Value} group='{group}' groups=[{string.Join(",", groupPos.Keys)}] type={name} at ({rt.X:0},{rt.Z:0}) ay={rt.Ay:0.00} sail={rt.Sail} length={rt.Length:0.0}");
    }

    sealed class ShipModel { public Node3D Root = null!; public readonly List<Node3D> Sails = new(); public float Length = 30f, Height = 12f; }

    /// <summary>Row-major D3D label matrix (vx, vy, vz, pos) -> Godot transform (z negated).</summary>
    static Transform3D LabelTransform(float[] m) =>
        new(new Basis(new Vector3(m[0], m[1], -m[2]), new Vector3(m[4], m[5], -m[6]), new Vector3(-m[8], -m[9], m[10])), new Vector3(m[12], m[13], -m[14]));

    static string SailTexture(int nation) => nation switch
    {
        0 => "ships\\parus_england.tga", 1 => "ships\\parus_france.tga", 2 => "ships\\parus_spain.tga", 3 => "ships\\parus_pirate.tga",
        4 => "ships\\parus_holland.tga", 5 => "ships\\parus_portugal.tga", _ => "ships\\parus_common.tga",
    };

    /// <summary>
    /// The ship is assembled the way the engine does it: the hull model has "geometry/mastN" labels (placement of the mast models), a mast has "geometry/rey_*"
    /// labels (its yards) and a yard has four-corner sail groups (sail1..sail4), from which the cloth is built.
    /// </summary>
    static ShipModel LoadShipModel(string typeName, int nation)
    {
        var sm = new ShipModel { Root = new Node3D { Name = "Ship_" + typeName } };
        string dir = GameAssets.Resource("MODELS\\Ships\\" + typeName);
        if (!Directory.Exists(dir)) { GD.Print("[sea] no model folder for ship " + typeName); return sm; }
        GmFile? Load(string name)
        {
            string f = Path.Combine(dir, typeName + name + ".gm");
            if (!File.Exists(f)) return null;
            try { return GmFile.Parse(File.ReadAllBytes(f)); } catch (Exception ex) { GD.PrintErr("[sea] " + f + ": " + ex.Message); return null; }
        }
        var hull = Load("");
        if (hull == null) return sm;
        var hullNode = ModelFactory.Build(hull, typeName, "ships", null);
        sm.Root.AddChild(hullNode);
        var b = ModelFactory.Bounds(hullNode);
        sm.Length = Math.Max(b.Size.Z, b.Size.X); sm.Height = b.Size.Y;
        var sailTex = GameAssets.Texture(SailTexture(nation));
        foreach (var ml in hull.Labels.Where(l => l.Group.Equals("geometry", StringComparison.OrdinalIgnoreCase) && l.Name.StartsWith("mast", StringComparison.OrdinalIgnoreCase)))
        {
            var mast = Load("_" + ml.Name);
            if (mast == null) continue;
            var mastNode = ModelFactory.Build(mast, ml.Name, "ships", null);
            mastNode.Transform = LabelTransform(ml.M);
            sm.Root.AddChild(mastNode);
            foreach (var rl in mast.Labels.Where(l => l.Group.Equals("geometry", StringComparison.OrdinalIgnoreCase) && l.Name.StartsWith("rey", StringComparison.OrdinalIgnoreCase)))
            {
                var rey = Load("_" + rl.Name);
                if (rey == null) continue;
                var reyNode = ModelFactory.Build(rey, rl.Name, "ships", null);
                reyNode.Transform = LabelTransform(rl.M);
                mastNode.AddChild(reyNode);
                foreach (var grp in rey.Labels.Where(l => l.Group.StartsWith("sail", StringComparison.OrdinalIgnoreCase) && l.Name.StartsWith("sail", StringComparison.OrdinalIgnoreCase)).GroupBy(l => l.Group))
                {
                    var c = new Vector3[4];
                    int have = 0;
                    foreach (var l in grp)
                        if (l.Name.Length == 5 && l.Name[4] >= '1' && l.Name[4] <= '4') { c[l.Name[4] - '1'] = new Vector3(l.M[12], l.M[13], -l.M[14]); have++; }
                    if (have != 4) continue;
                    var sail = BuildSail(c, sailTex);
                    reyNode.AddChild(sail);
                    sm.Sails.Add(sail);
                }
            }
        }
        return sm;
    }

    /// <summary>A sail: bilinear patch over the four corners (perimeter order: top-left, top-right, bottom-right, bottom-left) with a bulge towards one side.</summary>
    static MeshInstance3D BuildSail(Vector3[] c, Texture2D? tex)
    {
        const int N = 8;
        Vector3 top0 = c[0], top1 = c[1], bot1 = c[2], bot0 = c[3];
        var normal = ((top1 - top0).Cross(bot0 - top0)).Normalized();
        float width = (top1 - top0).Length();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector3 P(int i, int j)
        {
            float u = i / (float)N, v = j / (float)N;
            var p = (top0.Lerp(top1, u)).Lerp(bot0.Lerp(bot1, u), v);
            return p + normal * (MathF.Sin(MathF.PI * u) * MathF.Sin(MathF.PI * Math.Min(1f, v * 1.1f)) * width * 0.12f);
        }
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                var a = (P(i, j), new Vector2(i / (float)N, j / (float)N)); var bb = (P(i + 1, j), new Vector2((i + 1) / (float)N, j / (float)N));
                var d = (P(i, j + 1), new Vector2(i / (float)N, (j + 1) / (float)N)); var e = (P(i + 1, j + 1), new Vector2((i + 1) / (float)N, (j + 1) / (float)N));
                foreach (var t in new[] { a, bb, e, a, e, d }) { st.SetUV(t.Item2); st.AddVertex(t.Item1); }
            }
        st.GenerateNormals();
        var mat = new StandardMaterial3D
        {
            AlbedoTexture = tex, CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            AlphaScissorThreshold = 0.5f, Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel, Roughness = 1f,
        };
        return new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = mat, Name = "Sail" };
    }

    // ------------------------------------------------------------------ entity: Island
    V OnIsland(Entity e, V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        string S(int i) => i < d.Length ? Conv.ToStr(d[i]) : "";
        if (msg != ISLAND_LOAD_GEO) return V.Int(1);
        string path = S(2).Replace('/', '\\').Trim('\\'), model = S(3);
        string file = GameAssets.Resource("MODELS\\" + path + "\\" + model + ".gm");
        if (!File.Exists(file)) { GD.Print("[sea] island model not found: " + file); return V.Int(0); }
        try
        {
            var g = GmFile.Parse(File.ReadAllBytes(file));
            Color[]? cols = null;
            string light = e.Root.Get("LightingPath")?.Value?.Trim('\\') ?? "";
            string col = GameAssets.Resource("MODELS\\" + path + "\\" + model + "_" + light + ".col");
            if (light.Length > 0 && File.Exists(col))
            {
                var b = File.ReadAllBytes(col);
                cols = new Color[b.Length / 4];
                for (int i = 0; i < cols.Length; i++) cols[i] = new Color(b[i * 4 + 2] / 255f, b[i * 4 + 1] / 255f, b[i * 4] / 255f);
            }
            island?.QueueFree();
            island = ModelFactory.Build(g, model, path, cols);
            AddChild(island);
            BuildIslandCollision(island);
            foreach (var c in island.GetChildren().OfType<MeshInstance3D>()) GD.Print($"[sea]   object {c.Name} visible={c.Visible} aabb={c.Mesh.GetAabb()} mat={(c.Mesh.SurfaceGetMaterial(0) as StandardMaterial3D)?.AlbedoTexture?.GetSize()}");
            var bb = ModelFactory.Bounds(island);
            GD.Print($"[sea] island {model}: {g.Objects.Count} objects, {g.TotalVertices} vertices, light={(cols != null ? light : "none")}, bounds {bb.Position} .. {bb.End}");
            return V.Int(1);
        }
        catch (Exception ex) { GD.PrintErr("[sea] island " + file + ": " + ex.Message); return V.Int(0); }
    }

    // ------------------------------------------------------------------ island collision
    StaticBody3D? islandBody;

    /// <summary>
    /// A static trimesh of the island (without piers and palms) so that ships can ask "is there land under this point". The engine used a dedicated
    /// collision model; the visible geometry is close enough, apart from objects that stand in the water on purpose.
    /// </summary>
    void BuildIslandCollision(Node3D isl)
    {
        islandBody?.QueueFree();
        islandBody = new StaticBody3D { Name = "IslandCollision", CollisionLayer = 1 << 4, CollisionMask = 0 };
        var faces = new List<Vector3>();
        foreach (var mi in isl.GetChildren().OfType<MeshInstance3D>())
        {
            string n = mi.Name.ToString();
            if (n.StartsWith("Palm", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Prichal", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Pier", StringComparison.OrdinalIgnoreCase)) continue;
            faces.AddRange(mi.Mesh.GetFaces());
        }
        var shape = new ConcavePolygonShape3D { Data = faces.ToArray(), BackfaceCollision = true };
        islandBody.AddChild(new CollisionShape3D { Shape = shape });
        AddChild(islandBody);
    }

    /// <summary>Height of the island surface under the engine point (x, z), or -1000 when there is none.</summary>
    float LandHeight(float x, float z)
    {
        if (islandBody == null || !islandBody.IsInsideTree()) return -1000f;
        var space = GetWorld3D()?.DirectSpaceState;
        if (space == null) return -1000f;
        var q = PhysicsRayQueryParameters3D.Create(new Vector3(x, 600f, -z), new Vector3(x, -60f, -z), 1u << 4);
        q.HitBackFaces = true;
        var hit = space.IntersectRay(q);
        return hit.Count > 0 ? ((Vector3)hit["position"]).Y : -1000f;
    }

    /// <summary>True if any of the ship's four corner points (bow, stern, sides) is over land shallower than the draught.</summary>
    bool AgroundAt(ShipRt s, float x, float z, float ay)
    {
        float sn = MathF.Sin(ay), cs = MathF.Cos(ay);
        float hl = s.Length * 0.38f, hb = s.Length * 0.08f;
        foreach (var (lx, lz) in new[] { (0f, hl), (0f, -hl), (hb, 0f), (-hb, 0f) })
        {
            // local (lx right, lz forward) -> world engine: forward = (sin, cos), right = (cos, -sin)
            float wx = x + lx * cs + lz * sn, wz = z - lx * sn + lz * cs;
            if (LandHeight(wx, wz) > -3.0f) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ per frame
    void Publish(ShipRt s)
    {
        SetF(s.Chr, "Ship.Pos.x", s.X); SetF(s.Chr, "Ship.Pos.y", 0f); SetF(s.Chr, "Ship.Pos.z", s.Z);
        SetF(s.Chr, "Ship.Ang.y", s.Ay); SetF(s.Chr, "Ship.Speed.z", s.Speed);
        s.Node.Position = new Vector3(s.X, 0f, -s.Z);
        s.Node.Rotation = new Vector3(0f, -s.Ay, 0f);
        foreach (var sail in s.Sails) sail.Visible = s.Sail > 0.01f;
    }

    public float TestTurn;     // test hook (--move): steering without the keyboard

    public void Tick(float dt, bool inputEnabled)
    {
        if (!Active) return;
        foreach (var s in ships.ToList())
        {
            var sh = s.Chr.Find("Ship", false);
            if (sh == null) continue;
            float maxZ = F(sh, "MaxSpeedZ"), maxY = F(sh, "MaxSpeedY");
            float turnIn = 0f;
            if (s.IsPlayer && inputEnabled)
            {
                if (Godot.Input.IsKeyPressed(Key.A)) turnIn -= 1f;
                if (Godot.Input.IsKeyPressed(Key.D)) turnIn += 1f;
                if (TestTurn != 0f) turnIn = TestTurn;
            }
            float target = maxZ * s.Sail;
            float k = target > s.Speed ? 0.25f : 0.5f;
            s.Speed += (target - s.Speed) * Math.Min(1f, k * dt);
            float wantTurn = turnIn * maxY;
            s.TurnRate += (wantTurn - s.TurnRate) * Math.Min(1f, 2.5f * dt);
            s.Ay += s.TurnRate * dt * (0.35f + 0.65f * Math.Clamp(s.Speed / Math.Max(1f, maxZ), 0f, 1f));
            float nx = s.X + MathF.Sin(s.Ay) * s.Speed * dt, nz = s.Z + MathF.Cos(s.Ay) * s.Speed * dt;
            if (island != null && s.Speed > 0.01f && AgroundAt(s, nx, nz, s.Ay) && !AgroundAt(s, s.X, s.Z, s.Ay))
            {
                if (!s.Aground) { s.Aground = true; GD.Print($"[sea] {s.Chr.Get("id")?.Value} touches the shore at ({nx:0},{nz:0})"); }
                s.Speed = 0f;                                              // stopped by the shore: only turning gets it free again
            }
            else { if (s.Aground && s.Speed > 0.5f) s.Aground = false; s.X = nx; s.Z = nz; }
            Publish(s);
            // the script recalculates MaxSpeedZ/MaxSpeedY (wind, load, damage) when the engine reports the ship parameters
            if (++logCounter % 300 == 0) GD.Print($"[sea] {s.Chr.Get("id")?.Value} pos=({s.X:0.0},{s.Z:0.0}) ay={s.Ay:0.00} speed={s.Speed:0.0} turn={turnIn} in={inputEnabled} sail={s.Sail} maxZ={maxZ:0.0} maxY={maxY:0.00}");
            if (s.IsPlayer) { situationTimer += dt; if (situationTimer >= 1f) { situationTimer = 0f; UpdateSituation(s); } }
            s.ParamTimer += dt;
            if (s.ParamTimer >= 1f) { s.ParamTimer = 0f; it.FireEvent("Shp_UpdParams", new List<V> { V.Int(s.Index), V.Float(s.Sail) }); }
        }
        UpdateCamera(dt, inputEnabled);
        UpdateHud();
    }

    void UpdateHud()
    {
        var p = ships.FirstOrDefault(s => s.IsPlayer);
        if (p == null) { hud.Set(false, "", 0, 0, 0, ""); return; }
        var sh = p.Chr.Find("Ship", false);
        float baseHp = 1f;
        if (sh != null && it.Globals.TryGetValue("ShipsTypes", out var st) && st.Arr != null)
        {
            int ti = (int)F(sh, "Type", -1);
            if (ti >= 0 && ti < st.Arr.Length) baseHp = Math.Max(1f, F(st.Arr[ti].Val.Attr, "HP", 1f));
        }
        float windAng = 0f, windSpd = 0f;
        if (it.Globals.TryGetValue("Weather", out var w) && w.Val.Attr is { } wa) { windAng = F(wa, "Wind.Angle"); windSpd = F(wa, "Wind.Speed"); }
        string sails = p.Sail >= 0.99f ? "повні" : p.Sail >= 0.4f ? "половина" : "згорнуті";
        int deg = ((int)Math.Round(p.Ay * 180f / MathF.PI) % 360 + 360) % 360;
        string info = string.Join("\n", $"{sh?.Get("Name")?.Value ?? "?"}",
                      $"Корпус {F(sh, "HP"):0}/{baseHp:0}   Вітрила {F(sh, "SP"):0}%   Екіпаж {F(sh, "Crew.Quantity"):0}",
                      $"Швидкість {p.Speed:0.0} (макс. {F(sh, "MaxSpeedZ"):0.0})   Вітрила: {sails}",
                      $"Курс {deg}°   Вітер {windSpd:0.0}");
        string act = it.Globals.TryGetValue("g_ActiveActionName", out var ga) ? Conv.ToStr(ga.Val) : "";
        string actText = act switch { "Moor" => "Enter — зайти в порт", "Board" => "Enter — абордаж", "LandTroops" => "Enter — десант", "Map" => "Enter / M — карта світу", _ => "" };
        hud.Set(true, info, p.Ay, windAng, windSpd, actText);
    }

    /// <summary>
    /// The engine's sea AI keeps SeaAI.Update.{Ships,Forts} of the player's character: the nearby ships (idx, Distance, relation, d_ay, RelativeSpeed) and forts.
    /// The script (Ship_CheckMainCharacter) decides from them whether the player may board, moor, enter the map...
    /// </summary>
    void UpdateSituation(ShipRt p)
    {
        var upd = p.Chr.Find("SeaAI.Update", true)!;
        upd.Remove("Ships"); upd.Remove("Forts");
        var list = upd.GetOrCreate("Ships");
        upd.GetOrCreate("Forts");
        int k = 0;
        foreach (var o in ships)
        {
            if (ReferenceEquals(o, p)) continue;
            var n = list.GetOrCreate("s" + k++);
            n.GetOrCreate("idx").Value = o.Index.ToString();
            SetF(n, "Distance", MathF.Sqrt((o.X - p.X) * (o.X - p.X) + (o.Z - p.Z) * (o.Z - p.Z)));
            n.GetOrCreate("relation").Value = Conv.ToInt(it.CallTop("GetRelation", V.Int(p.Index), V.Int(o.Index))).ToString();
            SetF(n, "d_ay", MathF.Cos(o.Ay - p.Ay));
            SetF(n, "RelativeSpeed", MathF.Abs(o.Speed - p.Speed));
        }
    }

    /// <summary>Test hook: put the player's ship somewhere.</summary>
    public void Teleport(float x, float z, float ay)
    {
        if (ships.FirstOrDefault(s => s.IsPlayer) is { } p) { p.X = x; p.Z = z; p.Ay = ay; p.Speed = 0f; Publish(p); }
    }

    public void SailKey(int dir)
    {
        if (ships.FirstOrDefault(s => s.IsPlayer) is not { } p) return;
        p.Sail = Math.Clamp(p.Sail + 0.5f * dir, 0f, 1f);
        it.FireEvent("Shp_UpdParams", new List<V> { V.Int(p.Index), V.Float(p.Sail) });
        p.ParamTimer = 0f;
    }

    void UpdateCamera(float dt, bool inputEnabled)
    {
        var p = ships.FirstOrDefault(s => s.IsPlayer);
        if (p == null) return;
        var camEnt = it.Entities.FirstOrDefault(e => !e.Deleted && e.Class.Equals("SHIP_CAMERA", StringComparison.OrdinalIgnoreCase));
        float dMin = F(camEnt?.Root, "MinDistance", 25f), dMax = F(camEnt?.Root, "MaxDistance", 70f);
        if (camDist < 0f) camDist = Math.Clamp(p.Length * 1.5f, dMin, dMax);
        if (inputEnabled)
        {
            if (Godot.Input.IsKeyPressed(Key.Left)) camYawOff += 1.4f * dt;
            if (Godot.Input.IsKeyPressed(Key.Right)) camYawOff -= 1.4f * dt;
            if (Godot.Input.IsKeyPressed(Key.Up)) camPitch = Math.Clamp(camPitch + 0.8f * dt, 0.05f, 1.4f);
            if (Godot.Input.IsKeyPressed(Key.Down)) camPitch = Math.Clamp(camPitch - 0.8f * dt, 0.05f, 1.4f);
            if (Godot.Input.IsKeyPressed(Key.Pageup)) camDist = Math.Max(dMin * 0.5f, camDist - 30f * dt);
            if (Godot.Input.IsKeyPressed(Key.Pagedown)) camDist = Math.Min(dMax * 1.6f, camDist + 30f * dt);
        }
        if (!camInit && System.Environment.GetEnvironmentVariable("SEADOGS_SEACAM") is { } sc)      // debug: "distance,pitch,yawOffset"
        {
            var v = sc.Split(','); camDist = float.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture); camPitch = float.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture);
            camYawOff = v.Length > 2 ? float.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture) : 0f;
        }
        if (System.Environment.GetEnvironmentVariable("SEADOGS_SEACAM") == null) camYawOff *= MathF.Pow(0.97f, dt * 60f);           // drifts back behind the ship
        camInit = true;
        float yaw = p.Ay + camYawOff;
        var target = new Vector3(p.X, p.Height * 0.55f, -p.Z);
        var behind = new Vector3(-MathF.Sin(yaw) * MathF.Cos(camPitch), MathF.Sin(camPitch), MathF.Cos(yaw) * MathF.Cos(camPitch));   // godot: forward = (sin, 0, -cos)
        Cam.GlobalPosition = target + behind * camDist;
        Cam.LookAt(target);
        Cam.Current = true;
    }
}
