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
    const int AI_SHIP_SET_TASK = 51011, AI_ADD_SHIP = 51001, AI_GROUP_XYZ_AY = 51010, AI_GROUP_COMMANDER = 51022, AI_UNLOAD = 51025, AI_SHIP_SAIL_STATE = 51021;
    const int ISLAND_LOAD_GEO = 50100, ISLAND_SET_GEO = 50101;

    sealed class ShipRt
    {
        public Attr Chr = null!;
        public int Index;
        public Node3D Node = null!;
        public float Sail;                 // 0, 0.5, 1
        public float Speed, TurnRate, Drift;      // current forward speed, yaw rate and sideways slide
        public float X, Z, Ay;
        public float ParamTimer, AvoidT, AvoidOff;
        public bool IsPlayer, Aground, Dying;
        public float SinkT;
        public int Task = -1;                 // AITASK_*: 0 drift, 1 attack, 2 follow, 3 defend, 6 runaway, 7 move
        public Attr? TaskTarget;
        public float TaskX, TaskZ;
        public float Length = 30f, Height = 12f, Width = 8f;
        public List<Node3D> Sails = new();
        public List<(Node3D node, List<Node3D> sails)> Masts = new();
        public readonly HashSet<int> MastsDown = new();
        public List<(string bort, Vector3 pos)> Cannons = new();
        public readonly Dictionary<string, float> ReadyAt = new();
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
    public bool HasPlayerShip => ships.Any(s => s.IsPlayer);
    public Vector3 PlayerPosition => ships.FirstOrDefault(s => s.IsPlayer) is { } p ? new Vector3(p.X, 0, -p.Z) : Vector3.Zero;

    readonly SeaHud hud = new() { Name = "SeaHud" };

    public SeaView(Interp interp)
    {
        it = interp;
        Name = "SeaView";
        AddChild(hud);
        hud.SetPickHandler(Pick);
        AddChild(Cam);
        Cam.Current = false;
    }

    // ------------------------------------------------------------------ helpers
    /// <summary>A value of the ship type table (ShipsTypes[Ship.Type]) for the ship <paramref name="sh"/>.</summary>
    float TypeF(Attr? sh, string key, float def)
    {
        if (sh != null && it.Globals.TryGetValue("ShipsTypes", out var st) && st.Arr != null)
        {
            int ti = (int)F(sh, "Type", -1);
            if (ti >= 0 && ti < st.Arr.Length) return F(st.Arr[ti].Val.Attr, key, def);
        }
        return def;
    }

    static float F(Attr? a, string path, float def = 0f) => a?.Find(path, false)?.Value is { } v && v.Length > 0 ? Conv.ParseFloatPrefix(v) : def;
    static void SetF(Attr a, string path, float v) => a.Find(path, true)!.Value = Conv.FloatToConcat(v);

    public void Register(Interp i)
    {
        i.EntityHandlers["sea_ai"] = (e, fmt, d) => OnSeaAi(d);
        i.EntityHandlers["particles"] = (e, fmt, d) =>
        {
            if (d.Length > 4 && Conv.ToInt(d[0]) == 10000) ParticleFx.Spawn(this, Conv.ToStr(d[1]), new Vector3(Conv.ToFloat(d[2]), Conv.ToFloat(d[3]), Conv.ToFloat(d[4])));
            return V.Int(1);
        };
        i.EntityHandlers["BallSplash"] = (e, fmt, d) =>
        {
            if (d.Length > 3 && Conv.ToInt(d[0]) == 77200) ParticleFx.Splash(this, new Vector3(Conv.ToFloat(d[1]), Conv.ToFloat(d[2]), Conv.ToFloat(d[3])));
            return V.Int(1);
        };
        i.EntityHandlers["AIFort"] = (e, fmt, d) => { int m = d.Length > 0 ? Conv.ToInt(d[0]) : 0; return m == 51005 ? AddFort(d) : m == 51008 ? FortHit(d) : V.Int(1); };
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
        // every locator group of the file becomes island.<group>.<name>.{x,y,z,ay} (quest ship berths: Quest_ships.quest_ship_5 ...); the "Reload" group is the port markers used below
        foreach (var lab in gm.Labels)
        {
            if (lab.Group.Equals("Reload", StringComparison.OrdinalIgnoreCase) || lab.Group.Equals("reload", StringComparison.OrdinalIgnoreCase)) continue;
            if (reloadGroups(island).Contains(lab.Group)) continue;                    // ship1..ship8 of a port are handled below
            Put(island.GetOrCreate(lab.Group).GetOrCreate(lab.Name), lab);
        }
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

    static HashSet<string> reloadGroups(Attr island)
    {
        var r = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (island.Get("reload") is { } rl) foreach (var l in rl.Children) if (l.Get("name")?.Value is { Length: > 0 } n) r.Add(n);
        return r;
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
        ships.Clear(); groupPos.Clear(); groupCommander.Clear(); ClearCombat(); ClearForts(); wakes.Clear(); collCool.Clear();
        island?.QueueFree(); island = null;
        camInit = false; camDist = -1f;
        Cam.Current = false;
        hud.Update(new HudData());
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
            case AI_ADD_SHIP: if (d.Length > 3 && d[2].Attr is { } chr && d[3].Attr is { } type) AddShip((int)F(chr, "index"), chr, type); return V.Int(1);
            case AI_SHIP_SAIL_STATE:                                                      // "lif": character index, sail state
            {
                int idx = Conv.ToInt(d.Length > 1 ? d[1] : V.Int(-1));
                if (ships.FirstOrDefault(s => s.Index == idx) is { } sh) sh.Sail = Math.Clamp(Fl(2), 0f, 1f);
                return V.Int(1);
            }
            case AI_SHIP_SET_TASK:                                                        // "lllaff" / "lllaa": task, priority, character, (x, z | target character)
            {
                if (d.Length > 3 && d[3].Attr is { } who && ships.FirstOrDefault(s => ReferenceEquals(s.Chr, who)) is { } sh)
                {
                    sh.Task = Conv.ToInt(d[1]); sh.TaskTarget = null;
                    if (d.Length > 5 && d[4].T != VT.Attr) { sh.TaskX = Fl(4); sh.TaskZ = Fl(5); }
                    else if (d.Length > 4) sh.TaskTarget = d[4].Attr;
                }
                return V.Int(1);
            }
            case 51027:                                                                   // AI_MESSAGE_GET_RELATION "laae": two characters, &relation
                if (d.Length > 3 && d[1].Attr is { } ra && d[2].Attr is { } rb) it.WriteRef(d[3], V.Int(Relation((int)F(ra, "index"), (int)F(rb, "index"))));
                return V.Int(1);
            case 51023:                                                                   // AI_MESSAGE_SHIP_GET_ATTACK_HP "lafe": character, distance, &hp: damage per volley that it can deal at that distance
                if (d.Length > 3 && d[1].Attr is { } ca) it.WriteRef(d[3], V.Float(AttackHp(ca, Fl(2))));
                return V.Int(1);
            case 51024:                                                                   // AI_MESSAGE_GROUP_GET_ATTACK_HP "lsfe"
            {
                float tot = 0f;
                foreach (var gs in ships.Where(x => (x.Chr.Find("SeaAI.Group.Name", false)?.Value ?? "") == S(1))) tot += AttackHp(gs.Chr, Fl(2));
                if (d.Length > 3) it.WriteRef(d[3], V.Float(tot));
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
        rt.Node = model.Root; rt.Length = model.Length; rt.Height = model.Height; rt.Width = model.Width; SetF(chr, "Ship.BoxSize.x", model.Width); SetF(chr, "Ship.BoxSize.y", model.Height); SetF(chr, "Ship.BoxSize.z", model.Length); rt.Sails = model.Sails; rt.Masts = model.Masts; rt.Cannons = model.Cannons;
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

    sealed class ShipModel { public readonly List<(Node3D node, List<Node3D> sails)> Masts = new(); public readonly List<(string bort, Vector3 pos)> Cannons = new(); public Node3D Root = null!; public readonly List<Node3D> Sails = new(); public float Length = 30f, Height = 12f, Width = 8f; }

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
        if (System.Environment.GetEnvironmentVariable("SEADOGS_LABELS") != null) foreach (var f in Directory.GetFiles(dir, "*.gm")) { var gg = GmFile.Parse(File.ReadAllBytes(f)); GD.Print("[labels] " + Path.GetFileName(f) + ": " + string.Join(" ", gg.Labels.GroupBy(l => l.Group).Select(g => g.Key + "(" + g.Count() + ":" + string.Join(",", g.Take(4).Select(l => l.Name)) + ")"))); }
        var hullNode = ModelFactory.Build(hull, typeName, "ships", null);
        sm.Root.AddChild(hullNode);
        var parts = new List<RiggingFx.Part> { new(hullNode, hull) };
        if (System.Environment.GetEnvironmentVariable("SEADOGS_SHIPLOG") != null) ShipDebug.Dump(typeName, hullNode);
        var b = ModelFactory.Bounds(hullNode);
        sm.Length = Math.Max(b.Size.Z, b.Size.X); sm.Height = b.Size.Y; sm.Width = Math.Min(b.Size.Z, b.Size.X);
        foreach (var l in hull.Labels.Where(l => l.Group.StartsWith("cannon", StringComparison.OrdinalIgnoreCase)))
            sm.Cannons.Add((l.Group.ToLowerInvariant(), new Vector3(l.M[12], l.M[13], l.M[14])));
        var sailTex = GameAssets.Texture(SailTexture(nation));
        foreach (var ml in hull.Labels.Where(l => l.Group.Equals("geometry", StringComparison.OrdinalIgnoreCase) && l.Name.StartsWith("mast", StringComparison.OrdinalIgnoreCase)))
        {
            var mast = Load("_" + ml.Name);
            if (mast == null) continue;
            var mastNode = ModelFactory.Build(mast, ml.Name, "ships", null);
            ShipDebug.CullLoose(mastNode, 4.0f, 60);
            mastNode.Transform = LabelTransform(ml.M);
            if (System.Environment.GetEnvironmentVariable("SEADOGS_SHIPLOG") != null) ShipDebug.Dump("MAST " + ml.Name, mastNode);
            if (false) foreach (var n in mastNode.FindChildren("*", "MeshInstance3D", true, false)) { var mi = (MeshInstance3D)n; var bb = mi.Mesh.GetAabb(); GD.Print(string.Format(System.Globalization.CultureInfo.InvariantCulture, "[mastobj] {0} {1}: center ({2:0.0};{3:0.0};{4:0.0}) size ({5:0.0};{6:0.0};{7:0.0}) surfaces {8}", ml.Name, mi.Name, bb.GetCenter().X, bb.GetCenter().Y, bb.GetCenter().Z, bb.Size.X, bb.Size.Y, bb.Size.Z, mi.Mesh.GetSurfaceCount())); }
            if (System.Environment.GetEnvironmentVariable("SEADOGS_NOMAST") is not { } nm || !nm.Split(',').Contains(ml.Name)) sm.Root.AddChild(mastNode);
            parts.Add(new(mastNode, mast));
            var mastSails = new List<Node3D>();
            sm.Masts.Add((mastNode, mastSails));
            foreach (var rl in mast.Labels.Where(l => l.Group.Equals("geometry", StringComparison.OrdinalIgnoreCase) && l.Name.StartsWith("rey", StringComparison.OrdinalIgnoreCase)))
            {
                var rey = Load("_" + rl.Name);
                if (rey == null) continue;
                var reyNode = ModelFactory.Build(rey, rl.Name, "ships", null);
                ShipDebug.CullLoose(reyNode, 0.3f, 40, true);
                reyNode.Transform = LabelTransform(rl.M);
                if (System.Environment.GetEnvironmentVariable("SEADOGS_SHIPLOG") != null) ShipDebug.Dump("REY " + rl.Name, reyNode);
                mastNode.AddChild(reyNode);
                parts.Add(new(reyNode, rey));
                foreach (var grp in rey.Labels.Where(l => l.Group.StartsWith("sail", StringComparison.OrdinalIgnoreCase) && l.Name.StartsWith("sail", StringComparison.OrdinalIgnoreCase)).GroupBy(l => l.Group))
                {
                    var c = new Vector3[4];
                    int have = 0;
                    foreach (var l in grp)
                        if (l.Name.Length == 5 && l.Name[4] >= '1' && l.Name[4] <= '4') { c[l.Name[4] - '1'] = new Vector3(l.M[12], l.M[13], -l.M[14]); have++; }
                    if (have != 4) continue;
                    var sail = BuildSail(c, sailTex);
                    reyNode.AddChild(sail);
                    sm.Sails.Add(sail); mastSails.Add(sail);
                }
            }
        }
        if (RiggingFx.BuildRopes(sm.Root, parts) is { } ropes) sm.Root.AddChild(ropes);
        foreach (var flag in RiggingFx.BuildFlags(sm.Root, parts, nation)) sm.Root.AddChild(flag);
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
    Aabb islandBox;

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
            var xf = mi.Transform;
            foreach (var f in mi.Mesh.GetFaces()) faces.Add(xf * f);
        }
        if (faces.Count > 0) { var bx = new Aabb(faces[0], Vector3.Zero); foreach (var f in faces) bx = bx.Expand(f); islandBox = bx; }
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
        return OnLand(s, x, z, ay);
    }

    // ------------------------------------------------------------------ per frame
    void Publish(ShipRt s)
    {
        SetF(s.Chr, "Ship.Pos.x", s.X); SetF(s.Chr, "Ship.Pos.y", 0f); SetF(s.Chr, "Ship.Pos.z", s.Z);
        SetF(s.Chr, "Ship.Ang.y", s.Ay); SetF(s.Chr, "Ship.Speed.z", s.Speed);
        s.Node.Position = new Vector3(s.X, 0f, -s.Z);
        s.Node.Rotation = new Vector3(0f, -s.Ay, 0f);
        ApplyRocking(s);
        foreach (var sail in s.Sails) sail.Visible = s.Sail > 0.01f;
    }

    const float MassInertia = 0.1f, KnotsToMs = 0.514444f;       // ship.cpp: fMassInertia, KNOTS2METERS
    public float TestTurn;     // test hook (--move): steering without the keyboard

    public void Tick(float dt, bool inputEnabled)
    {
        if (!Active) return;
        foreach (var s in ships.ToList())
        {
            var sh = s.Chr.Find("Ship", false);
            if (sh == null) continue;
            if (!s.Dying && sh.Find("HP", false) != null && F(sh, "HP", 1f) <= 0f) { s.Dying = true; GD.Print($"[sea] {s.Chr.Get("id")?.Value} sinks"); }
            if (s.Dying)                                                  // the script has declared the ship dead (ShipDead): sink and remove it
            {
                s.SinkT += dt; s.Sail = 0f; s.Speed *= 0.97f;
                s.Node.Position = new Vector3(s.X, -s.SinkT * 1.2f, -s.Z);
                s.Node.Rotation = new Vector3(0.12f * s.SinkT, -s.Ay, 0.07f * s.SinkT);
                if (s.SinkT > 9f) { s.Node.QueueFree(); ships.Remove(s); it.FireEvent("ShpDelete", new List<V> { V.Int(s.Index) }); }
                continue;
            }
            float maxZ = F(sh, "MaxSpeedZ"), maxY = F(sh, "MaxSpeedY");
            float turnIn = 0f;
            if (!s.IsPlayer) turnIn = Steer(s, sh, dt);
            if (s.IsPlayer && inputEnabled)
            {
                if (Keys.Down("SeaLeft")) turnIn -= 1f;
                if (Keys.Down("SeaRight")) turnIn += 1f;
                if (TestTurn != 0f) turnIn = TestTurn;
                // "sail to" from the command menu: the script gives the player's ship a move task; the helm is automatic until the player steers
                if (turnIn != 0f && s.Task == 7) s.Task = -1;
                else if (s.Task == 7) turnIn = Steer(s, sh, dt);
            }
            // the engine's ApplyStrength (ship.cpp): per axis, with a commanded target the value is pulled to it by dt * 0.1 * (target - v) * InertiaAcceleration,
            // without one it brakes by dt * 0.1 * v * InertiaBraking. Speed is in knots (MaxSpeedZ = SpeedRate * wind ... from the script), turn rate in rad/s (MaxSpeedY)
            float accZ = TypeF(sh, "InertiaAccelerationZ", 1f), brkZ = TypeF(sh, "InertiaBrakingZ", 1f), accY = TypeF(sh, "InertiaAccelerationY", 1f), brkY = TypeF(sh, "InertiaBrakingY", 1f), brkX = TypeF(sh, "InertiaBrakingX", 1f);
            float fK = dt * MassInertia;
            float target = maxZ * s.Sail;
            if (target > 0.00002f) s.Speed += fK * (target - s.Speed) * accZ; else s.Speed -= fK * s.Speed * brkZ;
            if (MathF.Abs(s.Speed) < 0.001f && target <= 0.00002f) s.Speed = 0f;
            float wantTurn = turnIn * maxY;
            if (MathF.Abs(wantTurn) > 0.00002f) s.TurnRate += fK * (wantTurn - s.TurnRate) * accY; else s.TurnRate -= fK * s.TurnRate * brkY;
            if (MathF.Abs(s.TurnRate) < 0.001f && wantTurn == 0f) s.TurnRate = 0f;
            float oldAy = s.Ay;
            s.Ay += s.TurnRate * dt;              // the script already scales MaxSpeedY by speed, sail state, load, crew and skill
            float vms = s.Speed * KnotsToMs;
            // in a turn the hull slides outwards: lateral speed grows with speed * turn rate and dies out with the ship's sideways braking (InertiaBrakingX)
            s.Drift += (-s.TurnRate * vms * 0.5f - s.Drift * Math.Min(8f, fK / dt * 10f * brkX)) * dt;
            float sn0 = MathF.Sin(s.Ay), cs0 = MathF.Cos(s.Ay);
            float nx = s.X + (sn0 * vms + cs0 * s.Drift) * dt, nz = s.Z + (cs0 * vms - sn0 * s.Drift) * dt;
            MoveInWater(s, nx, nz, oldAy);                                  // never onto / into the island (SeaGrounding.cs)
            Publish(s);
            // the script recalculates MaxSpeedZ/MaxSpeedY (wind, load, damage) when the engine reports the ship parameters
            if (++logCounter % 300 == 0) GD.Print($"[sea] {s.Chr.Get("id")?.Value} pos=({s.X:0.0},{s.Z:0.0}) ay={s.Ay:0.00} speed={s.Speed:0.0} turn={turnIn} in={inputEnabled} sail={s.Sail} maxZ={maxZ:0.0} maxY={maxY:0.00}");
            if (s.IsPlayer) { situationTimer += dt; if (situationTimer >= 1f) { situationTimer = 0f; UpdateSituation(s); ReportSeaState(); } }
            s.ParamTimer += dt;
            if (s.ParamTimer >= 1f) { s.ParamTimer = 0f; it.FireEvent("Shp_UpdParams", new List<V> { V.Int(s.Index), V.Float(s.Sail) }); }
        }
        ResolveShipCollisions();
        TickCombat(dt);
        TickForts(dt);
        RiggingFx.UpdateFlags((float)(Time.GetTicksMsec() / 1000.0));
        UpdateCamera(dt, inputEnabled);
        UpdateHud();
    }

    string NationGroup(Attr chr, out string nationName)
    {
        int nat = (int)F(chr, "Nation", 3);
        nationName = Conv.ToStr(it.CallTop("GetNationNameByType", V.Int(nat)));
        return Conv.ToStr(it.CallTop("GetBlazonryGroup", V.Int(nat)));
    }

    HudShip MakeHudShip(ShipRt s, float? dist = null)
    {
        var sh = s.Chr.Find("Ship", false);
        float baseHp = 1f; string type = "";
        if (sh != null && it.Globals.TryGetValue("ShipsTypes", out var st) && st.Arr != null)
        {
            int ti = (int)F(sh, "Type", -1);
            if (ti >= 0 && ti < st.Arr.Length) { var tt = st.Arr[ti].Val.Attr; baseHp = Math.Max(1f, F(tt, "HP", 1f)); type = tt?.Get("name")?.Value ?? ""; }
        }
        float baseSp = 100f, crewMax = Math.Max(1f, F(sh, "Crew.Max", F(sh, "Crew.Quantity", 1f)));
        var r = new HudShip
        {
            Name = $"{sh?.Get("Name")?.Value ?? s.Chr.Get("id")?.Value}", Type = type, Own = s.IsPlayer,
            Hull = F(sh, "HP") / baseHp, Sails = F(sh, "SP") / baseSp, Crew = F(sh, "Crew.Quantity") / crewMax, CrewCount = (int)F(sh, "Crew.Quantity"),
            Distance = dist ?? 0f,
        };
        r.NationGroup = NationGroup(s.Chr, out r.NationName);
        return r;
    }

    float windCache;

    void UpdateHud()
    {
        var p = ships.FirstOrDefault(s => s.IsPlayer);
        var d = new HudData();
        if (p == null) { hud.Update(d); return; }
        d.On = true;
        var sh = p.Chr.Find("Ship", false);
        float windAng = 0f, windSpd = 0f;
        if (it.Globals.TryGetValue("Weather", out var w) && w.Val.Attr is { } wa) { windAng = F(wa, "Wind.Angle"); windSpd = F(wa, "Wind.Speed"); }
        d.Heading = p.Ay; d.WindTo = windAng; d.WindSpeed = windSpd; d.Speed = p.Speed; d.MaxSpeed = F(sh, "MaxSpeedZ"); d.SailState = p.Sail;
        // gun readiness of the four sides
        string[] borts = { "cannonf", "cannonr", "cannonb", "cannonl" };
        for (int i = 0; i < 4; i++)
        {
            d.HasGuns[i] = p.Cannons.Any(c => c.bort == borts[i]) || (i == 2 && p.Cannons.Any(c => c.bort != "cannonf" && c.bort != "cannonr" && c.bort != "cannonl"));
            if (p.ReadyAt.TryGetValue(i == 2 && !p.ReadyAt.ContainsKey("cannonb") ? p.ReadyAt.Keys.FirstOrDefault(k => k != "cannonf" && k != "cannonr" && k != "cannonl") ?? borts[i] : borts[i], out var at) && clock < at)
                d.Ready[i] = Math.Clamp(1f - (at - clock) / 10f, 0f, 0.95f);
        }
        if (sh?.Find("Cannons.Charge.Type", false)?.Value is { Length: > 0 } ct) d.Charge = (int)Conv.ParseFloatPrefix(ct);
        string[] goods = { "Balls", "Grapes", "Knippels", "Bombs" };
        for (int i = 0; i < 4; i++) d.Ammo[i] = (int)F(sh, "Cargo.Goods." + goods[i]);
        string act = it.Globals.TryGetValue("g_ActiveActionName", out var ga) ? Conv.ToStr(ga.Val) : "";
        d.Action = act switch { "Moor" => "Enter — зайти в порт", "Board" => "Enter — абордаж", "LandTroops" => "Enter — десант", "Map" => "Enter / M — карта світу", _ => "" };
        // squadron: the player and the companions
        int q = Math.Max(1, Conv.ToInt(it.CallTop("GetCompanionQuantity", V.A(p.Chr))));
        for (int i = 0; i < q && i < 6; i++)
        {
            int idx = i == 0 ? p.Index : Conv.ToInt(it.CallTop("GetCompanionIndex", V.A(p.Chr), V.Int(i)));
            var cs = ships.FirstOrDefault(x => x.Index == idx);
            if (cs != null) d.Squadron.Add(MakeHudShip(cs));
        }
        // the nearest foreign ship in front of the camera is the target; markers over all ships in view
        ShipRt? target = null; float best = float.MaxValue;
        var cam = Cam;
        foreach (var o in ships)
        {
            if (o.IsPlayer || o.Dying) continue;
            float dist = Dist(p, o);
            var pos = new Vector3(o.X, 22f, -o.Z);
            if (cam.IsPositionBehind(pos)) continue;
            var scr = cam.UnprojectPosition(pos);
            int rel = Conv.ToInt(it.CallTop("GetRelation", V.Int(p.Index), V.Int(o.Index)));
            var vp = cam.GetViewport().GetVisibleRect().Size;
            if (scr.X < 0 || scr.Y < 0 || scr.X > vp.X || scr.Y > vp.Y || dist > 3000f) continue;
            float off = (scr - vp / 2).Length();
            if (off < best && dist < 1500f) { best = off; target = o; }
            d.Markers.Add(new HudMarker { Screen = scr, Relation = rel, Distance = dist, Name = o.Chr.Find("Ship.Name", false)?.Value ?? o.Chr.Get("id")?.Value ?? "" });
        }
        if (target != null)
        {
            d.Target = MakeHudShip(target, Dist(p, target));
            foreach (var m in d.Markers) if (m.Name == d.Target.Name && Math.Abs(m.Distance - d.Target.Distance) < 0.5f) m.Target = true;
        }
        d.Menu = BuildMenu();
        hud.Update(d);
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
            SetF(n, "RelativeSpeed", MathF.Sqrt(MathF.Pow(MathF.Sin(o.Ay) * o.Speed - MathF.Sin(p.Ay) * p.Speed, 2) + MathF.Pow(MathF.Cos(o.Ay) * o.Speed - MathF.Cos(p.Ay) * p.Speed, 2)));
        }
    }

    /// <summary>Test hook: put the player's ship somewhere.</summary>
    public void Teleport(float x, float z, float ay)
    {
        if (ships.FirstOrDefault(s => s.IsPlayer) is { } p) { p.X = x; p.Z = z; p.Ay = ay; p.Speed = 0f; Publish(p); }
    }

    /// <summary>
    /// What the engine's battle interface reports to the script every second (BI_SetSeaState): sail-to possible, ?, map exit possible, attack / defend orders,
    /// reload (cargo transfer) possible, its target. The map can be entered anywhere but in a port's mooring zone (the script also forbids it near enemies and in storms).
    /// </summary>
    void ReportSeaState()
    {
        var pl = ships.FirstOrDefault(x => x.IsPlayer);
        bool canMoor = it.Globals.TryGetValue("bCanEnterToLand", out var g) && Conv.ToInt(g.Val) != 0;
        // what the command menu may offer: sail to a place of the island, attack an enemy / defend a friend that is near
        bool sailTo = island != null, attack = false, defend = false;
        if (pl != null)
            foreach (var o in ships)
            {
                if (o.IsPlayer || o.Dying) continue;
                int rel = Relation(pl.Index, o.Index);
                if (rel == 2 && Dist(pl, o) < 2500f) attack = true;
                if (rel == 0 && Dist(pl, o) < 2500f) defend = true;
            }
        it.FireEvent("BI_SetSeaState", new List<V> { V.Int(sailTo ? 1 : 0), V.Int(0), V.Int(canMoor ? 0 : 1), V.Int(attack ? 1 : 0), V.Int(defend ? 1 : 0), V.Int(0), V.Int(-1) });
    }

    /// <summary>
    /// Ship AI of the escorts and enemies. Tasks come from the script (AITASK_*): follow keeps a formation slot beside / behind the leader, defend stays close and
    /// engages enemies that come near, attack closes in to a broadside distance, run away flees from the threat, move goes to a point, aboard / brander ram the target.
    /// Every ship also steers around islands.
    /// </summary>
    float Steer(ShipRt s, Attr sh, float dt)
    {
        float tx = s.X, tz = s.Z;
        ShipRt? tgt = s.TaskTarget != null ? ships.FirstOrDefault(o => ReferenceEquals(o.Chr, s.TaskTarget) && !o.Dying) : null;
        int task = s.Task;
        bool go = true, away = false, match = false; float standoff = 0f;
        // a defender engages an enemy that comes close
        if (task == 3 && ships.Where(o => !o.Dying && !ReferenceEquals(o, s) && Relation(s.Index, o.Index) == 2 && Dist(s, o) < 320f).OrderBy(o => Dist(s, o)).FirstOrDefault() is { } foe) { tgt = foe; task = 1; }
        switch (task)
        {
            case 7: if (tgt != null) { tx = tgt.X; tz = tgt.Z; standoff = 60f; } else { tx = s.TaskX; tz = s.TaskZ; standoff = 40f; } break;
            case 1: if (tgt != null) { tx = tgt.X; tz = tgt.Z; standoff = 110f; } else go = false; break;
            case 2: case 3:
                if (tgt != null)
                {
                    // formation slot: followers of the same leader alternate to its sides, each row further back
                    var mates = ships.Where(o => !o.Dying && o.Task == s.Task && ReferenceEquals(o.TaskTarget, s.TaskTarget)).OrderBy(o => o.Index).ToList();
                    int k = Math.Max(0, mates.IndexOf(s));
                    float side = (k % 2 == 0 ? 1f : -1f) * (task == 2 ? 28f + 22f * (k / 2) : 45f), back = -(task == 2 ? 30f + 28f * (k / 2) : 10f);
                    float sn = MathF.Sin(tgt.Ay), cs = MathF.Cos(tgt.Ay);
                    tx = tgt.X + cs * side + sn * back; tz = tgt.Z - sn * side + cs * back;
                    standoff = 18f; match = true;
                }
                else go = false;
                break;
            case 5: case 8: if (tgt != null) { tx = tgt.X; tz = tgt.Z; standoff = 12f; match = true; } else go = false; break;      // board / ram the target
            case 6:
            {
                var threat = tgt ?? ships.Where(o => !o.Dying && !ReferenceEquals(o, s) && Relation(s.Index, o.Index) == 2).OrderBy(o => Dist(s, o)).FirstOrDefault() ?? ships.FirstOrDefault(o => o.IsPlayer);
                if (threat != null) { tx = threat.X; tz = threat.Z; away = true; } else go = false;
                break;
            }
            default: go = false; break;
        }
        float dx = tx - s.X, dz = tz - s.Z, dist = MathF.Sqrt(dx * dx + dz * dz);
        float want = MathF.Atan2(dx, dz);
        if (away) want += MathF.PI;
        else if (task == 1 && dist < standoff * 2f) want += MathF.PI / 2f * (MathF.Sin(want - s.Ay) >= 0 ? -1f : 1f);     // come alongside
        else if ((task == 2 || task == 3) && tgt != null && dist < 45f) want = Mathf.LerpAngle(want, tgt.Ay, 0.8f);         // in the slot: take the leader's heading
        want = AvoidLand(s, want, dt);
        float diff = want - s.Ay;
        while (diff > MathF.PI) diff -= MathF.Tau;
        while (diff < -MathF.PI) diff += MathF.Tau;
        float newSail;
        if (!go) newSail = 0f;
        else if (away) newSail = 1f;
        else if (match && tgt != null) newSail = dist < standoff * 2f ? (tgt.Sail > 0f ? Math.Min(1f, tgt.Sail + (dist > standoff * 1.2f ? 0.5f : 0f)) : 0f) : 1f;
        else newSail = dist < standoff && task != 1 ? 0f : 1f;
        newSail = MathF.Round(newSail * 2f) / 2f;
        if (newSail != s.Sail) { s.Sail = newSail; it.FireEvent("Shp_UpdParams", new List<V> { V.Int(s.Index), V.Float(s.Sail) }); }
        return Math.Clamp(diff * 2f, -1f, 1f);
    }

    /// <summary>Looks ahead along the wished heading; if land is in the way, takes the nearest clear heading beside it (re-checked a few times a second).</summary>
    float AvoidLand(ShipRt s, float want, float dt)
    {
        if (island == null) return want;
        s.AvoidT -= dt;
        if (s.AvoidT <= 0f)
        {
            s.AvoidT = 0.35f + 0.1f * (s.Index % 3);
            float look = Math.Clamp(s.Speed * KnotsToMs * 12f, 70f, 260f);
            s.AvoidOff = 0f;
            foreach (float off in new[] { 0f, 0.45f, -0.45f, 0.9f, -0.9f, 1.4f, -1.4f, 2.0f, -2.0f })
            {
                float a = want + off;
                if (!AgroundAt(s, s.X + MathF.Sin(a) * look, s.Z + MathF.Cos(a) * look, a) && !AgroundAt(s, s.X + MathF.Sin(a) * look * 0.5f, s.Z + MathF.Cos(a) * look * 0.5f, a)) { s.AvoidOff = off; break; }
                s.AvoidOff = off;
            }
        }
        return want + s.AvoidOff;
    }

    /// <summary>Test hook: park the first other ship 80 m abeam (right) of the player and stop it.</summary>
    static readonly float DuelDist = float.TryParse(System.Environment.GetEnvironmentVariable("SEADOGS_DUELDIST"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dd) ? dd : 80f;

    /// <returns>false when there is no opponent (yet); <paramref name="quiet"/> = do not log that (the hook retries)</returns>
    public bool Duel(bool quiet = false)
    {
        var p = ships.FirstOrDefault(s => s.IsPlayer); var o = ships.FirstOrDefault(s => !s.IsPlayer);
        if (p == null || o == null) { if (!quiet) GD.Print("[sea] duel: no opponent " + string.Join(",", ships.Select(x => x.Index + (x.IsPlayer ? "P" : "")))); return false; }
        o.X = p.X + MathF.Cos(p.Ay) * DuelDist; o.Z = p.Z - MathF.Sin(p.Ay) * DuelDist; o.Task = -1; o.Speed = 0f; o.Ay = p.Ay;
        foreach (var x in ships) if (!x.IsPlayer && x != o) { x.X = p.X + 3000f; x.Z = p.Z + 3000f; x.Task = -1; x.Speed = 0f; }       // the rest of the squadron out of the way
        GD.Print($"[sea] duel: {o.Chr.Get("id")?.Value} placed at ({o.X:0},{o.Z:0})");
        return true;
    }

    /// <summary>What a ship can do to an enemy at the given distance in one volley (used by the script's AI to decide whether to attack): its side guns times the ball's hull damage.</summary>
    float AttackHp(Attr chr, float dist)
    {
        var sh = chr.Find("Ship", false);
        if (sh == null) return 0f;
        float v0 = F(sh, "Cannons.SpeedV0", 0f), range = v0 * v0 / G;
        if (dist > range) return 0f;
        int cannons = (int)F(sh, "Cannons.Borts.cannonr.quantity", 0f);
        int total = ships.FirstOrDefault(s => ReferenceEquals(s.Chr, chr))?.Cannons.Count(c => c.bort == "cannonr") ?? Math.Max(cannons, 4);
        return total * 20f * (1f - dist / Math.Max(1f, range) * 0.5f);
    }

    public void Look(float dx, float dy) { camYawOff -= dx * 0.005f; camPitch = Math.Clamp(camPitch + dy * 0.004f, 0.05f, 1.4f); }
    public void Zoom(int dir) { camDist = Math.Clamp((camDist < 0 ? 40f : camDist) + dir * 4f, 12f, 120f); }

    public void SailKey(int dir)
    {
        if (ships.FirstOrDefault(s => s.IsPlayer) is not { } p) return;
        p.Sail = Math.Clamp(p.Sail + 0.5f * dir, 0f, 1f);
        it.FireEvent("Shp_UpdParams", new List<V> { V.Int(p.Index), V.Float(p.Sail) });
        p.ParamTimer = 0f;
    }

    // ---- telescope: hold the key, the view goes to the mast top and zooms (RESOURCE\INI	elescope.ini: eyepiece texture and zoom of the spyglass type)
    float scopeT;
    float scopeZoom = 5.5f;
    string scopeTex = "telescope.tga";

    void LoadScopeType(ShipRt p)
    {
        string id = p.Chr.Find("equip.spyglass", false)?.Value ?? "";
        int n = 0;
        foreach (char ch in id) if (char.IsDigit(ch)) n = n * 10 + (ch - '0');
        int type = n is >= 1 and <= 4 ? n : 0;
        try
        {
            var ini = IniFile.Load(GameAssets.Resource("INI\\telescope.ini")).Get("Type" + type);
            scopeTex = ini?.Get("texture") ?? scopeTex;
            if (float.TryParse(ini?.Get("zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z)) scopeZoom = z;
        }
        catch { }
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
            if (Keys.Down("SeaCamLeft")) camYawOff += 1.4f * dt;
            if (Keys.Down("SeaCamRight")) camYawOff -= 1.4f * dt;
            if (Keys.Down("SeaCamUp")) camPitch = Math.Clamp(camPitch + 0.8f * dt, 0.05f, 1.4f);
            if (Keys.Down("SeaCamDown")) camPitch = Math.Clamp(camPitch - 0.8f * dt, 0.05f, 1.4f);
            if (Keys.Down("SeaZoomIn")) camDist = Math.Max(dMin * 0.5f, camDist - 30f * dt);
            if (Keys.Down("SeaZoomOut")) camDist = Math.Min(dMax * 1.6f, camDist + 30f * dt);
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
        bool scope = inputEnabled && (Keys.Down("SeaTelescope") || System.Environment.GetEnvironmentVariable("SEADOGS_SCOPE") != null) && !CommandsOpen;
        float prev = scopeT;
        scopeT = Math.Clamp(scopeT + (scope ? 1f : -1f) * dt * 4f, 0f, 1f);
        if (scopeT > 0f && prev <= 0f) LoadScopeType(p);
        hud.ScopeTexture = scopeT > 0f ? scopeTex : "";
        hud.ScopeAmount = scopeT;
        if (scopeT > 0f)
        {
            // from the top of the mast along the camera's heading; the zoom narrows the field of view
            var eye = new Vector3(p.X, p.Height * 0.9f, -p.Z);
            var fwdDir = new Vector3(MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
            Cam.GlobalPosition = target.Lerp(eye, scopeT);
            Cam.LookAt(Cam.GlobalPosition + fwdDir * 200f + Vector3.Up * 200f * MathF.Tan((camPitch - 0.3f) * 0.6f));
            Cam.Fov = 62f / Mathf.Lerp(1f, scopeZoom, scopeT);
        }
        else
        {
            var want = target + behind * camDist;
            // the camera must not sit inside the island or under the water: pull it in front of any island surface on the way and keep it above the waves
            if (islandBody != null && islandBody.IsInsideTree() && GetWorld3D()?.DirectSpaceState is { } space)
            {
                var q = PhysicsRayQueryParameters3D.Create(target, want, 1u << 4);
                var hit = space.IntersectRay(q);
                if (hit.Count > 0) want = ((Vector3)hit["position"]).Lerp(target, 0.06f) + ((Vector3)hit["normal"]) * 0.8f;
            }
            float minY = SeaWaves.Height(want.X, -want.Z) + 1.6f;
            if (want.Y < minY) want.Y = minY;
            Cam.GlobalPosition = want;
            Cam.LookAt(target);
            Cam.Fov = 62f;
        }
        Cam.Current = true;
    }
}
