using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Formats;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The world map ("worldmap" engine class): the Caribbean as a flat map with the player's ship, island zones and town/fort markers. The script owns the data
/// (worldMap.islands.*, worldMap.date.*, worldMap.playerShip*, wdmCurrentIsland); the engine moves the ship, runs the date, tells the script which island zone the ship
/// is in (WorldMap_inside_islandzone / outside_islandzone), asks it for encounters (WorldMap_EncounterCreate) and raises ExitFromWorldMap to go back to the sea.
/// Map units = script units; Godot z = -z like everywhere else.
/// </summary>
public partial class WorldMapView : Node3D
{
    readonly Interp it;
    // SetPerspective((1.57 + 1.0) / 2) of world_map.cpp is the horizontal field of view (1.285 rad = 73.6 deg)
    public readonly Camera3D Cam = new() { Far = 20000f, Near = 1f, Fov = 73.6f, KeepAspect = Camera3D.KeepAspectEnum.Width, Name = "MapCamera" };
    Entity? ent;
    Node3D? scene, shipNode;
    bool built;
    float shipX, shipZ, shipAy, sail = float.TryParse(System.Environment.GetEnvironmentVariable("SEADOGS_MAPSAIL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var msl) ? msl : 0.5f, camYawOff, camElasticAy, shipVel, shipTurn, mapTime;
    bool camInit;
    // wdm_camera.cpp: the pitch (ang.x) is fixed at 1.0 rad, the height runs WDM_CAMERA_HEIGHT_MIN..MAX = 50..300 (the script's wdmCameraY = 750 is clamped to 300),
    // the distance to the point looked at is height / sin(pitch)
    const float CamPitch = 1.0f, CamHeightMin = 50f, CamHeightMax = 300f;
    float camDist = CamHeightMax / MathF.Sin(CamPitch);
    string inside = "";
    public bool IsBuilt => built;
    public string Inside => inside;
    public WorldLand? land;
    float encTimer, dateTimer;
    double secOfDay;
    readonly WorldHud hud = new() { Name = "MapHud" };
    readonly WorldMapUi ui = new() { Name = "MapUi" };
    // day / night: a multiply tint over the 3D map (below the HUD layer), the colour follows the map clock
    readonly CanvasLayer tintLayer = new() { Name = "MapDaylight", Layer = 3, Visible = false };
    readonly ColorRect tint = new() { Name = "Tint", MouseFilter = Control.MouseFilterEnum.Ignore, Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Mul } };
    int pubH = -1, pubM = -1, pubS = -1, lastTintHour = -1;

    /// <summary>The world map exists as soon as the script has created the entity (it sends no message to it first).</summary>
    public bool Active
    {
        get
        {
            if (ent is { Deleted: true }) Clear();
            if (ent == null && it.Entities.FirstOrDefault(e => !e.Deleted && e.Class.Equals("worldmap", StringComparison.OrdinalIgnoreCase)) is { } found) { ent = found; built = false; }
            return ent != null;
        }
    }

    public WorldMapView(Interp interp)
    {
        it = interp;
        Name = "WorldMapView";
        AddChild(Cam);
        tint.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        tintLayer.AddChild(tint);
        AddChild(tintLayer);
        AddChild(hud);
        AddChild(ui);
    }

    public void Register(Interp i)
    {
        i.EntityHandlers["worldmap"] = (e, fmt, d) =>
        {
            if (ent == null || !ReferenceEquals(ent, e)) { Clear(); ent = e; built = false; }
            return OnMessage(d);
        };
    }

    public void OnEntityDeleted(Entity e)
    {
        if (ent != null && ReferenceEquals(ent, e)) Clear();
    }

    void Clear()
    {
        if (encCreated > 0) GD.Print($"[map] encounter stats: {encCreated} created, {encArrived} merchants arrived, {encExpired} expired, {encFar} left behind, {encMet} met");
        encs.Clear(); encHooked = false;
        ClearStorms(); ui.Close();
        scene?.QueueFree(); scene = null; shipNode = null; ent = null; built = false; inside = "";
        Cam.Current = false;
        tintLayer.Visible = false; pubH = pubM = pubS = lastTintHour = -1;
        hud.Set(false, "", 0, 0);
    }

    static float F(Attr? a, string path, float def = 0f) => a?.Find(path, false)?.Value is { } v && v.Length > 0 ? Conv.ParseFloatPrefix(v) : def;
    static void SetF(Attr a, string path, float v) => a.Find(path, true)!.Value = Conv.FloatToConcat(v);

    Attr? Root => ent?.Root;

    // ------------------------------------------------------------------ scene
    void Build()
    {
        built = true;
        scene = new Node3D { Name = "MapScene" };
        AddChild(scene);
        var root = Root!;

        // water
        var seaTex = GameAssets.Texture("WorldMap\\Sea\\sea.tga");
        var seaMat = new StandardMaterial3D
        {
            AlbedoTexture = seaTex, Uv1Scale = new Vector3(40, 40, 1), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps, AlbedoColor = new Color(0.8f, 0.9f, 1f),
        };
        scene.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(6000, 6000) }, MaterialOverride = seaMat, Name = "MapSea", Position = new Vector3(0, -0.5f, 0) });

        // land
        string islands = GameAssets.Resource("MODELS\\WorldMap\\Islands.gm");
        land = null;
        if (File.Exists(islands))
        {
            var gm = GmFile.Parse(File.ReadAllBytes(islands));
            scene.AddChild(ModelFactory.Build(gm, "Islands", "WorldMap\\geometry", null));
            land = WorldLand.Build(gm);
        }

        // towns, forts, bays; island names
        var isls = root.Get("islands");
        if (land != null && System.Environment.GetEnvironmentVariable("SEADOGS_MAPLOG") != null && isls != null) LogIslandLand(isls);
        int placed = 0;
        if (isls != null)
            foreach (var isl in isls.Children)
            {
                var label = isl.Get("label");
                if (label?.Get("text")?.Value is { Length: > 0 } txt && label.Get("visible")?.Value != "false")
                    scene.AddChild(MakeLabel(txt, new Vector3(F(isl, "position.x"), 60f, -F(isl, "position.z")), 34f, new Color(1f, 0.92f, 0.6f)));
                var locs = isl.Get("locations");
                if (locs == null) continue;
                foreach (var loc in locs.Children)
                {
                    if (loc.Get("visible")?.Value == "false") continue;
                    string mn = loc.Get("modelName")?.Value ?? "";
                    var pos = new Vector3(F(loc, "position.x"), Math.Max(2f, F(loc, "position.y")), -F(loc, "position.z"));
                    if (mn.Length > 0)
                    {
                        string f = GameAssets.Resource("MODELS\\WorldMap\\" + mn + ".gm");
                        if (File.Exists(f))
                        {
                            var m = ModelFactory.Build(GmFile.Parse(File.ReadAllBytes(f)), mn, "WorldMap\\geometry", null);
                            m.Position = pos; m.Rotation = new Vector3(0, -F(loc, "position.ay"), 0);
                            scene.AddChild(m); placed++;
                        }
                    }
                    if (loc.Get("label")?.Get("text")?.Value is { Length: > 0 } lt && loc.Get("label")?.Get("visible")?.Value != "false")
                        scene.AddChild(MakeLabel(lt, pos + new Vector3(0, 14, 0), 14f, new Color(1f, 1f, 1f)));
                }
            }

        // the player's ship
        string shipFile = GameAssets.Resource("MODELS\\WorldMap\\ship.gm");
        shipNode = File.Exists(shipFile) ? ModelFactory.Build(GmFile.Parse(File.ReadAllBytes(shipFile)), "PlayerShip", "WorldMap\\geometry", null) : new Node3D();
        scene.AddChild(shipNode);

        // starting position: the island the player sailed from (curIslandId) + displacement, or the saved map position
        var pc = it.CallTop("GetMainCharacter").Attr;
        string curIsland = pc?.Get("curIslandId")?.Value ?? "";
        float dx = F(root, "playerShipDispX"), dz = F(root, "playerShipDispZ");
        if (curIsland.Length > 0 && isls?.Get(curIsland) is { } ci) { shipX = F(ci, "position.rx") + dx; shipZ = F(ci, "position.rz") + dz; }
        else if (pc?.Find("WMShip.Pos.x", false) != null) { shipX = F(pc, "WMShip.Pos.x"); shipZ = F(pc, "WMShip.Pos.z"); }
        else { shipX = dx; shipZ = dz; }
        shipAy = F(root, "playerShipAY");
        secOfDay = F(root, "date.hour") * 3600 + F(root, "date.min") * 60 + F(root, "date.sec");
        pubH = (int)F(root, "date.hour"); pubM = (int)F(root, "date.min"); pubS = (int)F(root, "date.sec");
        PublishShip(true);
        HookWindows();
        HookStormAttrs();          // a new map has no storms yet (the attributes outlive the entity)
        if (land != null && land.IsLand(shipX, shipZ)) { var (sx, sz) = land.NearestSea(shipX, shipZ); shipX = sx; shipZ = sz; }       // a spawn point on a coast cell: back into the water
        if (land != null && land.LandAhead(shipX, shipZ, shipAy, 12f))          // leaving an island the bow may point at the coast: turn it to the nearest open water
        {
            float best = shipAy, bestDiff = float.MaxValue;
            for (int k = 0; k < 36; k++)
            {
                float a = k * MathF.Tau / 36f;
                if (land.LandAhead(shipX, shipZ, a, 12f) || land.LandAhead(shipX, shipZ, a, 30f)) continue;
                float d = MathF.Abs(Mathf.AngleDifference(shipAy, a));
                if (d < bestDiff) { bestDiff = d; best = a; }
            }
            shipAy = best;
        }
        PublishShip(true);
        GD.Print($"[map] world map built: {isls?.Count} islands, {placed} markers; ship at ({shipX:0},{shipZ:0}) ay={shipAy:0.00}, from island '{curIsland}', date {F(root, "date.day"):0}.{F(root, "date.month"):0}.{F(root, "date.year"):0}");
    }

    static Label3D MakeLabel(string text, Vector3 pos, float size, Color c) => new()
    {
        Text = text, Position = pos, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = size / 140f, FontSize = 64, OutlineSize = 14,
        Modulate = c, OutlineModulate = Colors.Black, NoDepthTest = true, FixedSize = false, Name = "Label",
    };

    void PublishShip(bool force = false)
    {
        var root = Root!;
        SetF(root, "playerShipX", shipX); SetF(root, "playerShipZ", shipZ); SetF(root, "playerShipAY", shipAy);
        if (shipNode != null) { shipNode.Position = new Vector3(shipX, 0.5f, -shipZ); shipNode.Rotation = new Vector3(0, -shipAy, 0); }
    }

    // ------------------------------------------------------------------ per frame
    public float TestTurn;
    static readonly int MapSpeed = int.TryParse(System.Environment.GetEnvironmentVariable("SEADOGS_MAPSPEED"), out var msp) ? Math.Max(1, msp) : 1;

    public void Tick(float dt, bool input)
    {
        if (!Active) return;
        if (!built) Build();
        if (!built) return;
        var root = Root!;

        float turn = TestTurn;
        if (input)
        {
            if (Keys.Down("SeaLeft")) turn -= 1f;
            if (Keys.Down("SeaRight")) turn += 1f;
        }
        // an open interface screen or the map's own window freezes the map (the original freezes the execute layers: EngineLayersOffOn)
        bool run = input && !ui.IsOpen;
        for (int rep = 0, reps = run ? MapSpeed : 1; rep < reps; rep++)         // SEADOGS_MAPSPEED=N: N map steps per frame (long-run tests)
        {
        if (run && dt > 0f)
        {
            mapTime += dt;
            MovePlayerShip(Math.Min(dt, 0.1f), turn);
            if (System.Environment.GetEnvironmentVariable("SEADOGS_MAPLOG") != null && (int)(mapTime * 0.5f) != (int)((mapTime - dt) * 0.5f))
                GD.Print($"[maplog] t={mapTime:0.0} ship ({shipX:0.0},{shipZ:0.0}) ay={shipAy:0.00} vel={shipVel:0.0} turn={shipTurn:0.00} sail={sail} windK={WindSpeedFactor(shipX, shipZ, shipAy):0.00} enc={encs.Count}");
        }
        PublishShip();
        // the script (calendar.c, debug screens) may set worldMap.date.* itself: take the clock from the attributes when they differ from what the engine published last
        if ((int)F(root, "date.hour") != pubH || (int)F(root, "date.min") != pubM || (int)F(root, "date.sec") != pubS)
        {
            secOfDay = F(root, "date.hour") * 3600 + F(root, "date.min") * 60 + F(root, "date.sec");
            pubH = (int)F(root, "date.hour"); pubM = (int)F(root, "date.min"); pubS = (int)F(root, "date.sec");
        }
        ApplyDaylight();
        if (run)
        {
            UpdateDate(dt);
            UpdateZones();
            TickEncounters(dt);
            if (!Active) return;
            if (!TickStorms(dt)) return;
        }

        // encounters / storms: the script decides (wdmEvent_EncounterCreate); the engine gives it the elapsed time and the ship position
        if (run) encTimer += dt;
        if (encTimer >= 0.5f)
        {
            it.FireEvent("WorldMap_EncounterCreate", new List<V> { V.Float(encTimer), V.Float(shipX), V.Float(shipZ), V.Float(shipAy) });
            encTimer = 0f;
        }
        }

        // camera: behind and above the ship
        // WdmCamera::Move: the camera heading follows the ship's with k = 2 dt (the shortest way round)
        float ck = Math.Min(Math.Min(dt, 0.1f) * 2f, 1f);
        float dAng = shipAy - camElasticAy;
        if (dAng > MathF.PI) camElasticAy += MathF.Tau; else if (dAng < -MathF.PI) camElasticAy -= MathF.Tau;
        camElasticAy += (shipAy - camElasticAy) * ck;
        if (!camInit) { camElasticAy = shipAy; camInit = true; }
        float yaw = camElasticAy + camYawOff;
        var target = new Vector3(shipX, 0f, -shipZ);
        var off = new Vector3(-MathF.Sin(yaw) * MathF.Cos(CamPitch), MathF.Sin(CamPitch), MathF.Cos(yaw) * MathF.Cos(CamPitch)) * camDist;
        Cam.GlobalPosition = target + off;
        Cam.LookAt(target);
        Cam.Current = true;
        hud.Set(input, DateText(root), shipAy, sail);
    }

    float SeaSpeedRate()
    {
        var pc = it.CallTop("GetMainCharacter");
        return pc.Attr != null ? Math.Max(4f, Conv.ToFloat(it.CallTop("GetCharacterShipSpeedRate", pc))) : 12f;
    }

    public void Look(float dx, float dy) { camYawOff -= dx * 0.005f; }          // the pitch of the original camera never changes
    public void Zoom(int dir, float k = 1f) { camDist = Math.Clamp(camDist + dir * 25f * k, CamHeightMin / MathF.Sin(CamPitch), CamHeightMax / MathF.Sin(CamPitch)); }

    // ------------------------------------------------------------------ the player's ship (wdm_player_ship.cpp / wdm_ship.cpp)
    const float ShipMaxSpeed = 20f, ShipTurnSpeed = 1f, ShipIner = 2.5f, ShipBrake = 1.25f, ShipMaxTurn = 0.65f;
    const float SpeedOppositeWind = 0.8f, SpeedOverWind = 1.2f;

    /// <summary>The wind at a map point: direction (unit) and strength 0..1. The original (wdm_objects_wind.h) is a random smoothed field; this is a smooth deterministic stand-in.</summary>
    public float GetWind(float x, float z, out float dirX, out float dirZ)
    {
        float t = mapTime;
        float a = 1.7f * MathF.Sin(x * 0.004f + t * 0.05f) + 1.5f * MathF.Cos(z * 0.003f - t * 0.04f) + 1.2f * MathF.Sin((x + z) * 0.002f + 1.3f);
        dirX = MathF.Sin(a); dirZ = MathF.Cos(a);
        return Math.Clamp(0.6f + 0.4f * MathF.Sin(x * 0.0025f - z * 0.002f + t * 0.07f), 0f, 1f);
    }

    /// <summary>WdmShip::ShipUpdate speed factor: shipSpeedOppositeWind + (shipSpeedOverWind - shipSpeedOppositeWind) x wind x (cos of the angle to the wind)/2+1/2.</summary>
    public float WindSpeedFactor(float x, float z, float ay)
    {
        float k = GetWind(x, z, out float wx, out float wz);
        k *= (MathF.Sin(ay) * wx + MathF.Cos(ay) * wz) * 0.5f + 0.5f;
        float lo = Root != null && Root.Find("shipSpeedOppositeWind", false) != null ? F(Root, "shipSpeedOppositeWind") : SpeedOppositeWind;
        float hi = Root != null && Root.Find("shipSpeedOverWind", false) != null ? F(Root, "shipSpeedOverWind") : SpeedOverWind;
        return lo + (hi - lo) * k;
    }

    /// <summary>WdmPlayerShip::Move + WdmShip::ShipUpdate: sail up accelerates by INER_ST x MAX_SPEED / 2 (limit MAX_SPEED x the sail share), turning by INER_ST x TSPEED
    /// (limit MAX_TURNSPD), both braked by INER_BR; the ship moves along its old heading by speed x wind factor, then turns.</summary>
    void MovePlayerShip(float dt, float turn)
    {
        if (sail > 0f) shipVel += ShipIner * ShipMaxSpeed * dt * 0.5f;
        shipTurn += ShipIner * ShipTurnSpeed * dt * turn;
        float limit = ShipMaxSpeed * Math.Max(sail, 0f);
        if (shipVel > limit) shipVel = limit;
        shipTurn = Math.Clamp(shipTurn, -ShipMaxTurn, ShipMaxTurn);
        float move = shipVel * WindSpeedFactor(shipX, shipZ, shipAy) * dt;
        float nx = shipX + MathF.Sin(shipAy) * move, nz = shipZ + MathF.Cos(shipAy) * move;
        shipAy += shipTurn * dt;
        float k = Math.Min(ShipBrake * dt, 1f);
        shipTurn -= shipTurn * k; shipVel -= shipVel * k;
        // islands are solid for the ship (it can still sail out of a bay: only a move that ends on land is refused)
        if (land == null || !land.IsLand(nx, nz)) { shipX = nx; shipZ = nz; }
        else shipVel = 0f;
    }

    public void SailKey(int dir) => sail = Math.Clamp(sail + 0.5f * dir, 0f, 1f);

    string DateText(Attr root)
    {
        int day = (int)F(root, "date.day"), mon = (int)F(root, "date.month"), yr = (int)F(root, "date.year");
        string[] months = { "січня", "лютого", "березня", "квітня", "травня", "червня", "липня", "серпня", "вересня", "жовтня", "листопада", "грудня" };
        int h = (int)F(root, "date.hour"), m = (int)F(root, "date.min");
        string isl = inside.Length > 0 && Root?.Find("islands." + inside + ".label.text", false)?.Value is { } t ? $"\n{t}" : "";
        return $"{day} {months[Math.Clamp(mon - 1, 0, 11)]} {yr}, {h:00}:{m:00}{isl}";
    }

    // colour the whole map is multiplied with: (hour, rgb); night is a dark blue, dawn pink, dusk orange, noon untouched. Linear between keys, wrapping at 24 h.
    static readonly (float h, float r, float g, float b)[] DayKeys =
    {
        (0f, 0.38f, 0.44f, 0.70f), (4.5f, 0.40f, 0.46f, 0.72f), (6f, 0.88f, 0.64f, 0.58f), (7.5f, 1f, 0.94f, 0.86f), (9f, 1f, 1f, 1f),
        (16.5f, 1f, 1f, 0.98f), (18f, 1f, 0.82f, 0.62f), (19.5f, 0.78f, 0.52f, 0.50f), (21f, 0.44f, 0.47f, 0.72f), (24f, 0.38f, 0.44f, 0.70f),
    };

    public static Color DaylightColor(float hour)
    {
        hour = ((hour % 24f) + 24f) % 24f;
        for (int i = 1; i < DayKeys.Length; i++)
        {
            var a = DayKeys[i - 1]; var b = DayKeys[i];
            if (hour > b.h) continue;
            float k = (hour - a.h) / (b.h - a.h);
            return new Color(a.r + (b.r - a.r) * k, a.g + (b.g - a.g) * k, a.b + (b.b - a.b) * k);
        }
        return Colors.White;
    }

    void ApplyDaylight()
    {
        float hour = (float)(secOfDay / 3600.0);
        var c = DaylightColor(hour);
        tint.Color = c;
        tintLayer.Visible = true;
        int hh = (int)hour;
        if (hh != lastTintHour) { lastTintHour = hh; GD.Print($"[map] daylight at {hour:0.0} h: multiply {c.R:0.00},{c.G:0.00},{c.B:0.00}"); }
    }

    /// <summary>The map clock: worldMap.date.hourPerSec game hours per second (set by the script from the slowest ship); the script is told with WorldMap_UpdateDate and NextDay.</summary>
    void UpdateDate(float dt, double skipSec = 0)
    {
        var root = Root!;
        float hps = F(root, "date.hourPerSec", 4f);
        secOfDay += dt * hps * 3600.0 + skipSec;
        int days = 0;
        while (secOfDay >= 86400.0) { secOfDay -= 86400.0; days++; AdvanceDay(root); }
        root.Find("date.hour", true)!.Value = ((int)(secOfDay / 3600)).ToString();
        root.Find("date.min", true)!.Value = ((int)(secOfDay % 3600 / 60)).ToString();
        root.Find("date.sec", true)!.Value = ((int)(secOfDay % 60)).ToString();
        pubH = (int)(secOfDay / 3600); pubM = (int)(secOfDay % 3600 / 60); pubS = (int)(secOfDay % 60);
        dateTimer += dt;
        if (days > 0 || skipSec > 0 || dateTimer >= 0.25f)
        {
            dateTimer = 0f;
            it.FireEvent("WorldMap_UpdateDate", new List<V> { V.Float((float)(secOfDay / 3600.0)) });
            for (int i = 0; i < days && Active; i++) it.FireEvent("NextDay");
        }
    }

    // ------------------------------------------------------------------ the map's own windows
    static readonly (string text, float hours)[] WaitItems = { ("Чекати 1 годину", 1f), ("Чекати 3 години", 3f), ("Чекати 6 годин", 6f), ("Чекати 12 годин", 12f), ("Чекати добу", 24f), ("Скасувати", 0f) };

    public bool WindowOpen => ui.IsOpen;
    public bool WindowKey(Key k) => ui.Key(k);

    void HookWindows()
    {
        var root = Root!;
        root.GetOrCreate("eventWindow").GetOrCreate("update").OnWrite = _ => Callable.From(OpenEventWindow).CallDeferred();
        root.GetOrCreate("waitMenu").GetOrCreate("update").OnWrite = _ => Callable.From(OpenWaitMenu).CallDeferred();
    }

    /// <summary>worldMap.eventWindow.update: the script's message (wdmMessage) with two answers; WorldMap_EventWindowSelect(0 = yes, 1 = no).</summary>
    void OpenEventWindow()
    {
        if (!built || Root?.Get("eventWindow") is not { } ew) return;
        string text = ew.Get("text")?.Value ?? "", yes = ew.Get("yes")?.Value ?? "", no = ew.Get("no")?.Value ?? "";
        GD.Print($"[map] event window: '{text}' [{yes}] [{no}]");
        ui.OpenEvent(text, yes, no,
            r => { GD.Print("[map] event window choice " + r); it.FireEvent("WorldMap_EventWindowSelect", new List<V> { V.Int(r) }); },
            c => it.FireEvent("WorldMap_EventWindowChangeSelectoin", new List<V> { V.Int(c) }));
    }

    /// <summary>The wait menu (worldMap.waitMenu.update or the MapWait key): the clock jumps forward; WorldMap_WaitMenuSelect(item).</summary>
    public void OpenWaitMenu()
    {
        if (!built || ui.IsOpen) return;
        var names = new string[WaitItems.Length];
        for (int i = 0; i < names.Length; i++) names[i] = WaitItems[i].text;
        ui.OpenMenu("Очікування", names, r =>
        {
            it.FireEvent("WorldMap_WaitMenuSelect", new List<V> { V.Int(r) });
            if (!Active || r < 0 || r >= WaitItems.Length || WaitItems[r].hours <= 0f) return;
            GD.Print($"[map] waiting {WaitItems[r].hours:0} h");
            UpdateDate(0f, WaitItems[r].hours * 3600.0);
        }, c => it.FireEvent("WorldMap_WaitMenuChangeSelectoin", new List<V> { V.Int(c) }));
    }

    static void AdvanceDay(Attr root)
    {
        int day = (int)F(root, "date.day") + 1, mon = (int)F(root, "date.month"), yr = (int)F(root, "date.year");
        int[] dim = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
        int len = dim[Math.Clamp(mon - 1, 0, 11)] + (mon == 2 && yr % 4 == 0 ? 1 : 0);
        if (day > len) { day = 1; mon++; }
        if (mon > 12) { mon = 1; yr++; }
        root.Find("date.day", true)!.Value = day.ToString(); root.Find("date.month", true)!.Value = mon.ToString(); root.Find("date.year", true)!.Value = yr.ToString();
    }

    /// <summary>Island zones: the script learns which island the ship is over (wdmCurrentIsland) and may then leave the map to that island.</summary>
    void UpdateZones()
    {
        string now = "";
        float best = float.MaxValue;
        if (Root?.Get("islands") is { } isls)
            foreach (var isl in isls.Children)
            {
                float r = F(isl, "radius", 0f);
                if (r <= 0f) continue;
                float dx = shipX - F(isl, "position.rx"), dz = shipZ - F(isl, "position.rz");
                float d = MathF.Sqrt(dx * dx + dz * dz);
                if (d < r && d < best) { best = d; now = isl.Name; }
            }
        if (now == inside) return;
        if (inside.Length > 0) it.FireEvent("WorldMap_outside_islandzone", new List<V> { V.Str(inside) });
        inside = now;
        if (now.Length > 0) { GD.Print("[map] entered the zone of " + now); it.FireEvent("WorldMap_inside_islandzone", new List<V> { V.Str(now) }); }
    }

    /// <summary>Enter / Space: leave the map to the sea (at the island when the ship is in its zone).</summary>
    public void Enter()
    {
        if (MeetNearShip()) return;                // Enter next to a ship meets it (WdmPlayerShip::ExitFromMap)
        GD.Print("[map] exit to sea" + (inside.Length > 0 ? " at " + inside : " (open sea)"));
        it.FireEvent("ExitFromWorldMap");
    }

    void LogIslandLand(Attr isls)
    {
        foreach (var i in isls.Children)
        {
            float x = F(i, "position.x"), z = F(i, "position.z"), rx = F(i, "position.rx"), rz = F(i, "position.rz");
            GD.Print("[map] island " + i.Name + ": pos (" + x.ToString("0") + "," + z.ToString("0") + ") land=" + land!.IsLand(x, z) + ", rpos (" + rx.ToString("0") + "," + rz.ToString("0") + ") land=" + land.IsLand(rx, rz) + ", z mirrored land=" + land.IsLand(x, -z));
        }
    }

    public void Teleport(float x, float z, float ay)
    {
        shipX = x; shipZ = z; shipAy = ay;
        GD.Print($"[map] teleport to ({x:0},{z:0}) -> ({shipX:0},{shipZ:0}), built={built}");
        if (built) PublishShip();
    }
}

/// <summary>Date, heading and sail state of the map.</summary>
public partial class WorldHud : CanvasLayer
{
    readonly Label text = new();
    readonly Label help = new();

    public WorldHud()
    {
        Layer = 5;
        var panel = new PanelContainer { Position = new Vector2(12, 12) };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.5f), ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6 });
        text.AddThemeFontSizeOverride("font_size", 16);
        text.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.8f));
        panel.AddChild(text);
        AddChild(panel);
        help.AddThemeFontSizeOverride("font_size", 14);
        help.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.8f));
        help.Position = new Vector2(12, 0); help.AnchorTop = 1f; help.AnchorBottom = 1f; help.OffsetTop = -34; help.OffsetBottom = -8;
        help.Text = "A/D — поворот   W/S — вітрила   ←→ PgUp/PgDn — камера   Enter — зайти на острів / в море   T — чекати";
        AddChild(help);
        Visible = false;
    }

    public void Set(bool on, string date, float heading, float sail)
    {
        Visible = on;
        if (!on) return;
        int deg = ((int)Math.Round(heading * 180f / MathF.PI) % 360 + 360) % 360;
        text.Text = $"Карта світу\n{date}\nКурс {deg}°   Вітрила: {(sail >= 0.99f ? "повні" : sail >= 0.4f ? "половина" : "згорнуті")}";
    }
}
