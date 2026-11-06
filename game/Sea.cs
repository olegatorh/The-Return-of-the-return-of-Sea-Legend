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
    const int AI_CANNON_FIRE = 51002, AI_CANNON_RELOAD = 51003, AI_SHIP_CHANGE_GROUP = 51012, AI_UPDATE_RELATIONS = 51016, AI_GROUP_NEAR_GROUP = 51019, AI_GROUP_SET_TYPE = 51020,
              AI_SET_COMPANION_ENEMY = 51028;
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
        public string? TaskGroup;              // AITASK_DEFEND_GROUP: the group to protect
        public float TaskX, TaskZ;
        public string TypeName = "";          // the model folder of the ship type (MODELS\Ships\<name>)
        public string Group = "";             // the sea AI group of the ship (SeaAI.Group.Name at login, AI_MESSAGE_SHIP_CHANGE_GROUP / SET_COMPANION_ENEMY later)
        public float CheckT;                  // countdown to the next SHIP_CHECK_SITUATION event (SeaAI.cs)
        public float Length = 30f, Height = 12f, Width = 8f;
        public List<SailRt> Sails = new();             // SeaSails.cs
        public List<MastRt> Masts = new();
        public List<RiggingFx.Rope> Ropes = new();
        public HashSet<Node3D> YardNodes = new();       // yards that turn to the wind (their ropes are in YardRopes)
        public MeshInstance3D? FixedRopes, YardRopes;
        public float YardYaw, YardSet, RopeYaw, RopeTimer;
        public float SailSpeed = -1f, RollK = 1f, RollKT;   // MSG_SAIL_GET_SPEED share of MaxSpeedZ, crew rolling speed factor (SeaSails.cs)
        public float RockY, RockPitch, RockRoll;           // smoothed ShipRocking state (SeaPhysics.cs)
        public List<(string bort, Vector3 pos)> Cannons = new();
        public List<Vector3> FirePlaces = new();          // "fireplace" labels of the hull (engine local coordinates), SeaFires.cs
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
        i.EntityHandlers["battle_interface"] = (e, fmt, d) => OnBattleInterface(d);   // BattleInterface.cs
        RiggingFx.FlagTexNum = nation => RetInt(it.FireEvent("GetRiggingData", new List<V> { V.Str("GetFlagTexNum"), V.Int(0), V.Int(nation) }), 0, 0);
        // the class "particles" itself lives in Particles.cs (ParticleService); BallSplash (MSG_BALLSPLASH_ADD 77200) is the grid + splash.tga column of SeaSplash.cs
        i.EntityHandlers["BallSplash"] = (e, fmt, d) =>
        {
            if (d.Length > 3 && Conv.ToInt(d[0]) == 77200) BallSplashAdd(Conv.ToFloat(d[1]), Conv.ToFloat(d[3]));
            return V.Int(1);
        };
        i.EntityHandlers["Ship"] = (e, fmt, d) => OnShipMessage(e, d);                     // fire places, character index (SeaFires.cs)
        i.EntityHandlers["AIFort"] = (e, fmt, d) => { int m = d.Length > 0 ? Conv.ToInt(d[0]) : 0; return m == 51005 ? AddFort(d) : m == 51008 ? FortHit(d) : V.Int(1); };
        // AIBalls: MSG_MODEL_RELEASE 20504 when boarding starts (Abordage_ReloadStartFade) - the balls in flight are dropped; the ballistics live in SeaCombat.cs
        i.EntityHandlers["AIBalls"] = (e, fmt, d) => { if (d.Length > 0 && Conv.ToInt(d[0]) == 20504) ClearCombat(); return V.Int(1); };
        i.EntityHandlers["Island"] = (e, fmt, d) => OnIsland(e, d);
        i.EntityHandlers["SEA_CAMERAS"] = (e, fmt, d) => OnSeaCameras(d);              // ship / deck camera switch (SeaDeckCamera.cs)
        RegisterTelescope(i);                                                             // TELESCOPE, ispyglass, shippointer (SeaTelescope.cs)
        i.EntityHandlers["PEOPLE_ON_SHIP"] = (e, fmt, d) => OnPeopleOnShip(e, d);      // sailors on deck (SeaSailors.cs)
        RegisterFx(i);                                                                    // Seafoam, SINKEFFECT, AISeaGoods (SeaFx.cs)
        RegisterSails(i);                                                                 // Sail entity and the sail natives (SeaSails.cs)
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
        if (e.Class.Equals("battle_interface", StringComparison.OrdinalIgnoreCase)) ResetBattleInterface();
        if (e.Class.Equals("sea_ai", StringComparison.OrdinalIgnoreCase) || e.Class.Equals("Island", StringComparison.OrdinalIgnoreCase))
        {
            if (e.Class.Equals("Island", StringComparison.OrdinalIgnoreCase)) { island?.QueueFree(); island = null; }
            else Clear();
        }
    }

    void Clear()
    {
        foreach (var s in ships) s.Node.QueueFree();
        ships.Clear(); groupPos.Clear(); groupCommander.Clear(); groupType.Clear(); pursuit.Clear(); pursuitPlaced.Clear(); relCache.Clear(); groupCheckT = 0f; ClearCombat(); ClearFires(); ClearForts(); ClearSailors(); ClearSails(); ClearFx(); wakes.Clear(); collCool.Clear();
        island?.QueueFree(); island = null;
        camInit = false; camDist = -1f;
        deckCam = false; scopeTarget = -1; scopeInfoShown = false; scopeActiveSent = false; pointerChr = -1; lastSelectedShip = -2;
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
            case AI_GROUP_COMMANDER:
                if (d.Length > 2 && d[2].Attr is { } cmd) { groupCommander[S(1)] = cmd; cmd.Find("SeaAI.Group.Name", true)!.Value = S(1); if (ShipOf(cmd) is { } cs) cs.Group = S(1); }
                return V.Int(1);
            case AI_SHIP_CHANGE_GROUP:                                                    // "las": character, new group (ChangeCharacterShipGroup)
                if (d.Length > 2 && d[1].Attr is { } gc && ShipOf(gc) is { } gs0) { GD.Print($"[sea] {gc.Get("id")?.Value} changes group '{gs0.Group}' -> '{S(2)}'"); gs0.Group = S(2); relCache.Clear(); }
                return V.Int(1);
            case AI_UPDATE_RELATIONS: UpdateRelations(); return V.Int(1);                // "l": the script changed relations: re-read them (HUD colours, AI targets)
            case AI_GROUP_SET_TYPE: groupType[S(1)] = S(2); return V.Int(1);              // "lss": war | trade | pirate (the script's AIAttack_SelectTask uses it; kept for the log)
            case AI_GROUP_NEAR_GROUP:                                                     // "lss": the group appears near another one (pursuit: coast guards, quest hunters)
                pursuit[S(1)] = S(2); PlaceNearGroup(S(1));
                return V.Int(1);
            case AI_SET_COMPANION_ENEMY:                                                  // "la": a companion left the squadron (SeaAI_SetCompanionEnemy already moved it to "cmpenemy_N")
                if (d.Length > 1 && d[1].Attr is { } ce && ShipOf(ce) is { } ces)
                {
                    ces.Group = ce.Find("SeaAI.Group.Name", false)?.Value ?? ces.Group; ces.Task = -1; ces.TaskTarget = null; ces.TaskGroup = null; relCache.Clear();
                    GD.Print($"[sea] companion {ce.Get("id")?.Value} turns enemy, group '{ces.Group}'");
                }
                return V.Int(1);
            case AI_CANNON_FIRE:                                                          // "lal": main character, camera outside: Ship_DoFire (the fire control of the script)
                if (d.Length > 1 && d[1].Attr is { } fc && ShipOf(fc) is { IsPlayer: true }) FirePlayer();
                return V.Int(1);
            case AI_CANNON_RELOAD:                                                        // "la": the charge changes: the loaded balls are dropped, every bort reloads
                if (d.Length > 1 && d[1].Attr is { } rc && ShipOf(rc) is { } rs) ReloadAll(rs);
                return V.Int(1);
            case 51004: case 51009: return V.Int(1);                                      // SET_CAMERAS_ATTRIBUTE (cameras are ours), GROUP_SET_TASK (not sent by the scripts)
            case AI_ADD_SHIP: if (d.Length > 3 && d[2].Attr is { } chr && d[3].Attr is { } type) AddShip((int)F(chr, "index"), chr, type); return V.Int(1);
            case 51018: SailToPoint(Fl(1), Fl(3), null, 0f, 0f); return V.Int(1);                          // AI_MESSAGE_SAIL_2_LOCATOR "lfff": the port's reload locator (x, y, z)
            case 51017:                                                                                     // AI_MESSAGE_SAIL_2_CHARACTER "laff": character, distance, angle
                if (d.Length > 1 && d[1].Attr is { } toChr && ships.FirstOrDefault(s => ReferenceEquals(s.Chr, toChr)) is { } to) SailToPoint(to.X, to.Z, to, Fl(2), Fl(3));
                return V.Int(1);
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
                    var (oldTask, oldTarget, oldGroup) = (sh.Task, sh.TaskTarget, sh.TaskGroup);
                    sh.Task = Conv.ToInt(d[1]); sh.TaskTarget = null; sh.TaskGroup = null;
                    if (d.Length > 5 && d[4].T != VT.Attr) { sh.TaskX = Fl(4); sh.TaskZ = Fl(5); }
                    else if (d.Length > 4 && d[4].T == VT.Attr) sh.TaskTarget = d[4].Attr;
                    else if (d.Length > 4) sh.TaskGroup = S(4);                           // "lllas": AITASK_DEFEND_GROUP, group name
                    if (!sh.IsPlayer && (oldTask != sh.Task || !ReferenceEquals(oldTarget, sh.TaskTarget) || oldGroup != sh.TaskGroup))
                        GD.Print($"[sea] {who.Get("id")?.Value} task: {TaskName(sh.Task)} {sh.TaskTarget?.Get("id")?.Value ?? sh.TaskGroup ?? (sh.Task == 7 ? $"({sh.TaskX:0},{sh.TaskZ:0})" : "")}");
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
                foreach (var gs in ships.Where(x => !x.Dying && x.Group.Equals(S(1), StringComparison.OrdinalIgnoreCase))) tot += AttackHp(gs.Chr, Fl(2));
                if (d.Length > 3) it.WriteRef(d[3], V.Float(tot));
                return V.Int(1);
            }
            case AI_UNLOAD: Clear(); return V.Int(1);
            case 51014:                                                                   // AI_MESSAGE_SWAP_SHIPS "laa": the two characters change ships (ransack "swap"; the script swapped their Ship attributes)
                if (d.Length > 2 && d[1].Attr is { } c1 && d[2].Attr is { } c2) SwapShips(c1, c2);
                return V.Int(1);
            case 51013:                                                                   // AI_MESSAGE_SET_OFFICER_2_SHIP "laa": officer, captured enemy: the officer takes command of that ship
                if (d.Length > 2 && d[1].Attr is { } off && d[2].Attr is { } cap && ShipOf(cap) is { } taken)
                {
                    taken.Chr = off; taken.Index = (int)F(off, "index"); taken.IsPlayer = taken.Index == 0; taken.Task = -1; taken.TaskTarget = null; taken.Dying = false;
                    taken.Group = off.Find("SeaAI.Group.Name", false)?.Value is { Length: > 0 } og ? og : ships.FirstOrDefault(x => x.IsPlayer)?.Group ?? taken.Group;
                    relCache.Clear();
                    Publish(taken);
                    it.FireEvent("Shp_UpdParams", new List<V> { V.Int(taken.Index), V.Float(taken.Sail) });
                    GD.Print($"[sea] officer {off.Get("id")?.Value} takes the ship of {cap.Get("id")?.Value}");
                }
                return V.Int(1);
            case 51026:                                                                   // AI_MESSAGE_CHARACTER_DEAD "la": ShipDead (sunk, or killed after boarding): the ship goes down
                if (d.Length > 1 && d[1].Attr is { } dead && ShipOf(dead) is { Dying: false } ds) { ds.Dying = true; ds.Task = -1; GD.Print($"[sea] {dead.Get("id")?.Value} is dead: the ship sinks"); }
                return V.Int(1);
        }
        return V.Int(1);
    }

    ShipRt? ShipOf(Attr chr) => ships.FirstOrDefault(s => ReferenceEquals(s.Chr, chr)) ?? ships.FirstOrDefault(s => s.Index == (int)F(chr, "index", -1));

    /// <summary>
    /// AI_MESSAGE_SWAP_SHIPS (AIHelper::SwapShips): the ships keep their places, models and damage; their captains change. The script has already exchanged the
    /// characters' Ship attributes, so after the swap every ship again reads the attributes that describe it. The positions are written back to the new owners.
    /// </summary>
    void SwapShips(Attr c1, Attr c2)
    {
        var a = ShipOf(c1); var b = ShipOf(c2);
        if (a == null || b == null || ReferenceEquals(a, b)) { GD.Print("[sea] swap ships: a ship is missing"); return; }
        (a.Chr, b.Chr) = (b.Chr, a.Chr);
        (a.Index, b.Index) = (b.Index, a.Index);
        a.IsPlayer = a.Index == 0; b.IsPlayer = b.Index == 0;
        a.Task = b.Task = -1; a.TaskTarget = b.TaskTarget = null;
        (a.Group, b.Group) = (b.Group, a.Group);                                        // the group belongs to the captain
        relCache.Clear();
        Publish(a); Publish(b);
        camInit = false;
        it.FireEvent("Shp_UpdParams", new List<V> { V.Int(a.Index), V.Float(a.Sail) });
        it.FireEvent("Shp_UpdParams", new List<V> { V.Int(b.Index), V.Float(b.Sail) });
        GD.Print($"[sea] swap ships: {c1.Get("id")?.Value} <-> {c2.Get("id")?.Value}");
    }

    void AddShip(int index, Attr chr, Attr type)
    {
        string group = chr.Find("SeaAI.Group.Name", false)?.Value ?? "";
        var rt = new ShipRt { Chr = chr, Index = index, IsPlayer = index == 0, Group = group, CheckT = 1f + 0.25f * (index % 5) };
        relCache.Clear();
        PlaceNearGroup(group);                                                            // a pursuing group: next to the group it chases (once)
        // placement: the group leader stands at the group position, the others behind it
        if (groupPos.TryGetValue(group, out var gp))
        {
            int n = ships.Count(s => s.Group == group);
            rt.X = gp.x - MathF.Sin(gp.ay) * 90f * n; rt.Z = gp.z - MathF.Cos(gp.ay) * 90f * n; rt.Ay = gp.ay;
        }
        else { rt.X = F(chr, "Ship.Pos.x"); rt.Z = F(chr, "Ship.Pos.z"); rt.Ay = F(chr, "Ship.Ang.y"); }
        if (rt.IsPlayer && float.TryParse(System.Environment.GetEnvironmentVariable("SEADOGS_PLAYERHP"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var testHp)) SetF(chr, "Ship.HP", testHp);   // test knob: the squadron hits as the original does
        string name = type.Get("Name")?.Value ?? "";
        rt.TypeName = name;
        var model = LoadShipModel(name, (int)F(chr, "Nation", 3));
        rt.Node = model.Root; rt.Length = model.Length; rt.Height = model.Height; rt.Width = model.Width; SetF(chr, "Ship.BoxSize.x", model.Width); SetF(chr, "Ship.BoxSize.y", model.Height); SetF(chr, "Ship.BoxSize.z", model.Length); rt.Sails = model.Sails; rt.Masts = model.Masts; rt.Cannons = model.Cannons; rt.FirePlaces = model.FirePlaces; rt.Ropes = model.Ropes; rt.YardNodes = model.YardNodes; rt.FixedRopes = model.FixedRopes; rt.YardRopes = model.YardRopes;
        AddChild(rt.Node);
        // the script sets Ship.Speed.z = 1 for a ship that sails away from the coast and 0 for one standing at the shore: that is the starting sail state
        float sp = F(chr, "Ship.Speed.z");
        rt.Sail = sp >= 0.75f ? 1f : sp >= 0.25f ? 0.5f : 0f;
        rt.Speed = 0f;
        foreach (var sl in rt.Sails) sl.Roll = rt.Sail < 0.3f || (rt.Sail <= 0.7f && sl.RollFast) ? 1f : 0f;      // SeaSails.cs TickSails
        ApplySailTextures(rt);
        ApplyRiggingState(rt);
        ships.Add(rt);
        Publish(rt);
        it.FireEvent("ShpCreate", new List<V> { V.Int(index), V.A(chr.Get("Ship")) });
        it.FireEvent("Shp_UpdParams", new List<V> { V.Int(index), V.Float(rt.Sail) });
        GD.Print($"[sea] ship {chr.Get("id")?.Value} group='{group}' groups=[{string.Join(",", groupPos.Keys)}] type={name} at ({rt.X:0},{rt.Z:0}) ay={rt.Ay:0.00} sail={rt.Sail} length={rt.Length:0.0}");
    }

    sealed class ShipModel
    {
        public readonly List<MastRt> Masts = new(); public readonly List<(string bort, Vector3 pos)> Cannons = new(); public readonly List<Vector3> FirePlaces = new(); public Node3D Root = null!; public readonly List<SailRt> Sails = new();
        public float Length = 30f, Height = 12f, Width = 8f;
        public List<RiggingFx.Rope> Ropes = new(); public readonly HashSet<Node3D> YardNodes = new(); public MeshInstance3D? FixedRopes, YardRopes;
    }

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
        foreach (var l in hull.Labels.Where(l => l.Group.Equals("fireplace", StringComparison.OrdinalIgnoreCase)))
            sm.FirePlaces.Add(new Vector3(l.M[12], l.M[13], l.M[14]));
        var sailTex = GameAssets.Texture(SailTexture(nation));
        var triTex = GameAssets.Texture("ships\\parus_common.tga") ?? sailTex;
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
            var mr = new MastRt { Node = mastNode, Name = ml.Name.ToLowerInvariant(), Num = TrailingNumber(ml.Name), Top = Math.Max(2f, ModelFactory.Bounds(mastNode).End.Y), Bowsprit = mastNode.Basis.Y.Normalized().Y < 0.7f };
            mr.Base = new Vector3(mastNode.Position.X, mastNode.Position.Y, -mastNode.Position.Z);
            sm.Masts.Add(mr);
            sm.Sails.AddRange(MakeSails(mast, mastNode, sm.Root, mr, sailTex, triTex));          // sails on the stays of this mast
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
                var sails = MakeSails(rey, reyNode, sm.Root, mr, sailTex, triTex).ToList();
                sm.Sails.AddRange(sails);
                // a yard with square sails turns to the wind round the vertical of the ship (not on the bowsprit)
                if (!mr.Bowsprit && sails.Any(x => !x.ForeAft))
                {
                    mr.Yards.Add(new YardRt { Node = reyNode, Rest = reyNode.Transform, Axis = (mastNode.Basis.Inverse() * Vector3.Up).Normalized() });
                    sm.YardNodes.Add(reyNode);
                }
            }
        }
        float total = sm.Sails.Sum(x => x.Area);
        foreach (var sl in sm.Sails) sl.Power = total > 0f ? sl.Area / total : 0f;
        sm.Ropes = RiggingFx.CollectRopes(parts);
        BuildShipRopes(sm.Root, sm.Ropes, sm.YardNodes, ref sm.FixedRopes, ref sm.YardRopes, true);
        foreach (var (host, flag) in RiggingFx.BuildFlags(parts, nation)) host.AddChild(flag);
        return sm;
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
    }

    const float MassInertia = 0.1f, KnotsToMs = 1853f / 3600f;   // ship.cpp: fMassInertia, KNOTS2METERS (math_inlines.h: x * 1853 / 3600)
    public float TestTurn;     // test hook (--move): steering without the keyboard

    public void Tick(float dt, bool inputEnabled)
    {
        if (!Active) return;
        dt = Math.Min(dt, 0.1f);                                                        // SHIP::Execute clamps the frame time
        rockDt = dt;
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
            // Ship.Impulse (AIShip.c: storm yaw, ...): added once to the rotation (and speed) and deleted, as SHIP::Execute does
            if (sh.Find("Impulse", false) is { } imp)
            {
                s.TurnRate += F(imp, "Rotate.y") * dt;
                s.Speed += F(imp, "Speed.z") * dt; s.Drift += F(imp, "Speed.x") * dt;
                sh.Remove("Impulse");
            }
            // target speed: MaxSpeedZ (the script: ship type, wind power and angle, load, sail damage) x the share the sails give (MSG_SAIL_GET_SPEED, SeaSails.cs)
            float target = maxZ * (s.SailSpeed >= 0f ? s.SailSpeed : s.Sail);
            if (target > 0.00002f) s.Speed += fK * (target - s.Speed) * accZ; else s.Speed -= fK * s.Speed * brkZ;
            if (MathF.Abs(s.Speed) < 0.001f && target <= 0.00002f) s.Speed = 0f;
            float wantTurn = turnIn * maxY;
            if (MathF.Abs(wantTurn) > 0.00002f) s.TurnRate += fK * (wantTurn - s.TurnRate) * accY; else s.TurnRate -= fK * s.TurnRate * brkY;
            if (MathF.Abs(s.TurnRate) < 0.001f && wantTurn == 0f) s.TurnRate = 0f;
            // sideways speed (knots): only impulses (collisions, explosions) give it; nothing drives it, so it brakes by InertiaBrakingX (ApplyStrength, x axis)
            s.Drift += fK * -s.Drift * brkX;
            if (MathF.Abs(s.Drift) < 0.001f) s.Drift = 0f;
            // SHIP::Move: the position advances along the current heading (forward and sideways speed in knots), then the heading turns
            float oldAy = s.Ay;
            float vms = s.Speed * KnotsToMs, sms = s.Drift * KnotsToMs;
            float sn0 = MathF.Sin(s.Ay), cs0 = MathF.Cos(s.Ay);
            float nx = s.X + (sn0 * vms + cs0 * sms) * dt, nz = s.Z + (cs0 * vms - sn0 * sms) * dt;
            s.Ay += s.TurnRate * dt;              // the script already scales MaxSpeedY by speed, sail state, load, crew and skill
            MoveInWater(s, nx, nz, oldAy);                                  // never onto / into the island (SeaGrounding.cs)
            Publish(s);
            // the script recalculates MaxSpeedZ/MaxSpeedY (wind, load, damage) when the engine reports the ship parameters
            if (++logCounter % 300 == 0) GD.Print($"[sea] {s.Chr.Get("id")?.Value} pos=({s.X:0.0},{s.Z:0.0}) ay={s.Ay:0.00} speed={s.Speed:0.0} turn={turnIn} in={inputEnabled} sail={s.Sail} sails={s.SailSpeed:0.00} maxZ={maxZ:0.0} maxY={maxY:0.00}");
            if (s.IsPlayer) { situationTimer += dt; if (situationTimer >= 1f) { situationTimer = 0f; UpdateSituation(s); } CheckSeaState(); }
            s.ParamTimer += dt;
            if (s.ParamTimer >= 1f) { s.ParamTimer = 0f; it.FireEvent("Shp_UpdParams", new List<V> { V.Int(s.Index), V.Float(s.Sail) }); }
        }
        ResolveShipCollisions();
        TickSeaAI(dt);
        TickCombat(dt);
        TickFires();
        TickForts(dt);
        TickSailors(dt);
        TickSails(dt);
        TickFx(dt);
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
            int rel = Relation(p.Index, o.Index);
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
        // the navigator: the script's picture of the loaded charge (GetCurrentCharge) and the other ships for the minimap
        d.NavCharge = Conv.ToInt(it.FireEvent("GetCurrentCharge", new List<V>()));
        foreach (var o in ships)
            if (!ReferenceEquals(o, p)) d.NavShips.Add(new NavShip { Dx = o.X - p.X, Dz = o.Z - p.Z, Ay = o.Ay, Relation = Relation(p.Index, o.Index), Dead = o.Dying });
        d.Menu = BuildMenu();
        d.BiVisible = biVisible; d.NotEnoughBalls = biNotEnoughBalls;
        ReportSelectedShip();
        UpdatePointer();
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
        var fl = upd.GetOrCreate("Forts");
        // ai_ship.cpp SetSeaAIAttributes: every fort of the island (l<N>: idx, relation, distance, d_ay); AIShip decides from it whether the fort can be stormed (FORT_ABORDAGE) or entered (FORT_DEAD)
        for (int fi = 0; fi < forts.Count; fi++)
        {
            var fn = fl.GetOrCreate("l" + fi);
            fn.GetOrCreate("idx").Value = forts[fi].Index.ToString();
            fn.GetOrCreate("relation").Value = Relation(p.Index, forts[fi].Index).ToString();
            SetF(fn, "Distance", MathF.Sqrt((forts[fi].X - p.X) * (forts[fi].X - p.X) + (forts[fi].Z - p.Z) * (forts[fi].Z - p.Z)));
            SetF(fn, "d_ay", MathF.Cos(p.Ay));
        }
        int k = 0;
        foreach (var o in ships)
        {
            if (ReferenceEquals(o, p)) continue;
            var n = list.GetOrCreate("s" + k++);
            n.GetOrCreate("idx").Value = o.Index.ToString();
            SetF(n, "Distance", MathF.Sqrt((o.X - p.X) * (o.X - p.X) + (o.Z - p.Z) * (o.Z - p.Z)));
            n.GetOrCreate("relation").Value = Relation(p.Index, o.Index).ToString();
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
        if (task == 4) { tgt = GroupLeader(s.TaskGroup); if (tgt == s) tgt = null; task = 3; }   // AITASK_DEFEND_GROUP: keep with the group's leader like a defender
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
                    var mates = ships.Where(o => !o.Dying && o.Task == s.Task && ReferenceEquals(o.TaskTarget, s.TaskTarget) && o.TaskGroup == s.TaskGroup).OrderBy(o => o.Index).ToList();
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
        GD.Print($"[sea] duel: {o.Chr.Get("id")?.Value} (character {o.Index}) placed at ({o.X:0},{o.Z:0})");
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

    public void Look(float dx, float dy)
    {
        if (DeckCamera) { DeckLook(dx, dy); return; }
        camYawOff -= dx * 0.005f; camPitch = Math.Clamp(camPitch + dy * 0.004f, 0.05f, 1.4f);
    }
    public void Zoom(int dir) { if (!DeckCamera) camDist = Math.Clamp((camDist < 0 ? 40f : camDist) + dir * 4f, 12f, 120f); }

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
        if (scopeFromScript) return;                     // MSG_TELESCOPE_SET_TYPE from the script (the equipped item's scope.*) wins
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
        // the telescope: from the mast top in the ship camera, from the eye in the deck camera
        // test hook: env SEADOGS_SCOPE=1 holds the telescope up all the time, SEADOGS_SCOPE=N (> 1) from process frame N on
        bool forced = System.Environment.GetEnvironmentVariable("SEADOGS_SCOPE") is { } fs && (!int.TryParse(fs, out var fromFrame) || fromFrame <= 1 || (long)Engine.GetProcessFrames() >= fromFrame);
        bool scope = inputEnabled && (Keys.Down("SeaTelescope") || forced) && !CommandsOpen;
        float prev = scopeT;
        // the zoom grows during the spyglass's activation time (ISPYGLASS::UpdateCamera) and drops back at once when the key is released
        scopeT = scope ? Math.Clamp(scopeT + dt / Math.Max(0.1f, scopeActivateMs / 1000f), 0f, 1f) : 0f;
        if (scopeT > 0f && prev <= 0f) LoadScopeType(p);
        hud.ScopeTexture = scopeT > 0f ? scopeTex : "";
        hud.ScopeAmount = scopeT;
        if (DeckCamera)
        {
            UpdateDeckCamera(p, dt, inputEnabled);
            UpdateTelescopeInfo(p, dt);
            return;
        }
        Cam.Near = 0.5f;
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
        // ship_camera.cpp: the angle is relative to the ship and stays where the player left it (it does not drift back behind the ship)
        camInit = true;
        float yaw = p.Ay + camYawOff;
        var target = new Vector3(p.X, p.Height * 0.55f, -p.Z);
        var behind = new Vector3(-MathF.Sin(yaw) * MathF.Cos(camPitch), MathF.Sin(camPitch), MathF.Cos(yaw) * MathF.Cos(camPitch));   // godot: forward = (sin, 0, -cos)
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
            if (System.Environment.GetEnvironmentVariable("SEADOGS_LOOKSUN") is { } ls && WeatherFx.LastSunDir.LengthSquared() > 0.1f)       // debug: aim the camera at the sun (+ pitch offset in degrees)
            {
                float off = float.TryParse(ls, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var o) ? o : 0f;
                var sd = WeatherFx.LastSunDir;
                Cam.LookAt(want + sd * 100f + Vector3.Up * (100f * MathF.Tan(off * MathF.PI / 180f)));
            }
            Cam.Fov = 62f;
        }
        Cam.Current = true;
        UpdateTelescopeInfo(p, dt);
    }
}
