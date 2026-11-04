using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>Boots the original Storm scripts inside Godot and wires the engine classes (so far: xinterface) to them.</summary>
public partial class Main : Node
{
    ScriptHost host = null!;
    XView? view;
    FaderView? fader;
    DialogView? dialogView;
    LogHud? logHud;
    bool rebuildWorld;
    float clockAcc;
    SoundView? sound;
    readonly Dictionary<Entity, Node3D> modelNodes = new();
    readonly Dictionary<Entity, string> modelDirs = new();
    readonly Dictionary<Entity, LocationView> locations = new();
    readonly Dictionary<Entity, CharacterView> characters = new();
    bool started;
    bool camPlaced;
    int frames;
    Image? lastGameFrame;

    Image? GrabFrame()
    {
        var img = GetViewport().GetTexture()?.GetImage();
        if (img == null) return null;
        img.Resize(256, 192, Image.Interpolation.Bilinear);
        return img;
    }
    string? shotPath;
    string? programArg;
    string language = "Ukrainian";
    int shotAtFrame = -1;
    readonly List<(int frame, string node)> clicks = new();
    readonly List<(int frame, string id)> dialogTests = new();
    readonly List<(int frame, string name, string value)> sets = new();
    readonly List<(int frame, string fn)> calls = new();
    readonly List<(int frame, int answer)> answers = new();
    readonly List<(int from, int to, Vector2 dir)> moves = new();
    readonly List<(int frame, string path)> dumps = new();
    readonly List<(int frame, string name)> ctls = new();
    int fuzzAt = -1, fuzzNext, fuzzSince, fuzzAnswers;
    Queue<string>? fuzzQueue;
    readonly List<(int frame, string loc)> gotos = new();
    readonly List<(int frame, float x, float z, float ay)> seaTeleports = new();      // test hook --seapos=x,z,ay@N
    readonly List<int> seaFires = new();                      // test hook --fire@N: broadside
    readonly List<(int frame, Key key)> keyPresses = new();      // test hook --key=Escape@N: a real key press through the input system
    readonly List<(int frame, string name)> saveAt = new();       // test hook --save=name@N: write SAVE\name through the script's own save path
    readonly List<(int frame, string name)> loadAt = new();       // test hook --load=name@N
    readonly List<int> seaDuels = new();                      // test hook --duel@N
    readonly List<int> seaActions = new();                    // test hook --act@N: execute the offered sea action (Enter)
    readonly List<(int frame, int dir)> sails = new();       // test hook --sail=+1@N / --sail=-1@N: sail state up/down at sea
    int controlAt = -1;

    public override void _Ready()
    {
        string res = ProjectSettings.GlobalizePath("res://");
        string game = System.Environment.GetEnvironmentVariable("SEADOGS_GAME") ?? Path.GetFullPath(Path.Combine(res, "..", ".."));
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--game=")) game = a.Substring(7);
            else if (a.StartsWith("--program=")) programArg = a.Substring(10);
            else if (a.StartsWith("--lang=")) language = a.Substring(7);
            else if (a.StartsWith("--shot=")) shotPath = a.Substring(7);
            else if (a.StartsWith("--shot-frame=")) shotAtFrame = int.Parse(a.Substring(13));
            else if (a.StartsWith("--dialog=")) { var p = a.Substring(9).Split('@'); dialogTests.Add((int.Parse(p[1]), p[0].Replace('_', ' '))); }
            else if (a.StartsWith("--set=")) { var p = a.Substring(6).Split('@'); var kv = p[0].Split('='); sets.Add((int.Parse(p[1]), kv[0], kv[1])); }
            else if (a.StartsWith("--call="))  { var p = a.Substring(7).Split('@'); calls.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--move=")) { var p = a.Substring(7).Split('@'); var xy = p[0].Split(','); var fr = p[1].Split('-'); moves.Add((int.Parse(fr[0]), int.Parse(fr[1]), new Vector2(float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture)))); }
            else if (a.StartsWith("--dump=")) { var p = a.Substring(7).Split('@'); dumps.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--camdist=")) camDist = float.Parse(a.Substring(10), System.Globalization.CultureInfo.InvariantCulture);
            else if (a.StartsWith("--fuzz@")) fuzzAt = int.Parse(a.Substring(7));
            else if (a.StartsWith("--ctl=")) { var p = a.Substring(6).Split('@'); ctls.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--seapos=")) { var p = a.Substring(9).Split('@'); var v = p[0].Split(','); var ci = System.Globalization.CultureInfo.InvariantCulture; seaTeleports.Add((int.Parse(p[1]), float.Parse(v[0], ci), float.Parse(v[1], ci), float.Parse(v[2], ci))); }
            else if (a.StartsWith("--key=")) { var p = a.Substring(6).Split('@'); keyPresses.Add((int.Parse(p[1]), Enum.Parse<Key>(p[0], true))); }
            else if (a.StartsWith("--save=")) { var p = a.Substring(7).Split('@'); saveAt.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--load=")) { var p = a.Substring(7).Split('@'); loadAt.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--duel@")) seaDuels.Add(int.Parse(a.Substring(7)));
            else if (a.StartsWith("--fire@")) seaFires.Add(int.Parse(a.Substring(7)));
            else if (a.StartsWith("--act@")) seaActions.Add(int.Parse(a.Substring(6)));
            else if (a.StartsWith("--sail=")) { var p = a.Substring(7).Split('@'); sails.Add((int.Parse(p[1]), int.Parse(p[0]))); }
            else if (a.StartsWith("--goto=")) { var p = a.Substring(7).Split('@'); gotos.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--control@")) controlAt = int.Parse(a.Substring(10));
            else if (a.StartsWith("--answer=")) { var p = a.Substring(9).Split('@'); answers.Add((int.Parse(p[1]), int.Parse(p[0]))); }
            else if (a.StartsWith("--click=")) { var p = a.Substring(8).Split('@'); clicks.Add((int.Parse(p[1]), p[0])); }
        }
        GameAssets.GameDir = game;
        outdoor = new OutdoorEnv { Name = "Outdoor" };
        AddChild(outdoor);
        GD.Print("[main] game folder: " + game);

        // scripts: the addon tree (base PROGRAM overlaid with the files unpacked from stormex2.dll) if it was built, else plain PROGRAM
        string portDir = Path.GetFullPath(Path.Combine(res, ".."));
        string merged = Path.Combine(portDir, "addon", "merged", "PROGRAM");
        string program = programArg ?? (Directory.Exists(merged) ? merged : Path.Combine(game, "PROGRAM"));
        GD.Print("[main] scripts: " + program + ", language: " + language);
        host = new ScriptHost(program);
        // port settings (settings.ini next to the project): the hidden "beta/test" page of the options screen is enabled by the original #define MOD_BETTATESTMODE
        string settingsFile = Path.Combine(portDir, "settings.ini");
        if (!File.Exists(settingsFile))
            File.WriteAllText(settingsFile, "; Port settings. BetaTestMode: Off | Test | On  (Test/On show the extra beta page of the options screen: cheats, debug logs, locator display ...)\n[game]\nBetaTestMode = On\n");
        var settings = IniFile.Load(settingsFile);
        Keys.Load(settingsFile);
        string beta = settings.Sections.TryGetValue("game", out var gs) ? gs.Get("BetaTestMode")?.Trim() ?? "Off" : "Off";
        if (beta is "Test" or "On") host.Pp.DefineOverrides["MOD_BETTATESTMODE"] = beta;
        GD.Print("[main] BetaTestMode = " + beta);
        sea = new SeaView(host.It);
        AddChild(sea);
        worldMap = new WorldMapView(host.It);
        AddChild(worldMap);
        var colorCorrection = new ColorCorrection();
        AddChild(colorCorrection);
        host.It.HostFunctions["xi_setcolorcorrection"] = a => { if (a.Length >= 3) colorCorrection.Set(Conv.ToFloat(a[0]), Conv.ToFloat(a[1]), Conv.ToFloat(a[2])); return V.Void; };
        host.It.Language = new LanguageService(game, Path.Combine(portDir, "lang"), language);
        host.It.Log = s => GD.Print("[script] " + s);
        EngineStubs.Install(host.It);
        SaveFiles.Dir = Path.Combine(portDir, "saves");
        host.It.SaveHook = name =>
        {
            try
            {
                host.It.CallTop("OnSave");              // the engine calls the script's OnSave() first (remembers where the player is, positions of people)
                host.It.SaveState(SaveFiles.PathFor(name), host.LoadedSegments, "");
                SaveFiles.WriteThumbnail(name, lastGameFrame ?? GrabFrame());
                GD.Print("[save] saved " + name);
                return true;
            }
            catch (Exception e) { GD.PrintErr("[save] failed: " + e.Message); return false; }
        };
        host.It.LoadHook = name =>
        {
            try
            {
                string path = SaveFiles.PathFor(name);
                if (!File.Exists(path)) { GD.PrintErr("[save] no such save: " + name); return false; }
                var info = StormScript.Runtime.Interp.ReadInfo(path);
                foreach (var seg in info.Segments) if (!host.LoadedSegments.Contains(seg)) host.LoadSegment(seg);
                host.It.RestoreState(path);
                sea.RelocateIslands();
                GD.Print($"[save] restored {name}: {host.It.Globals.Count} globals, {host.It.Handlers.Count} event names");
                rebuildWorld = true;                    // the 3D world is rebuilt on the next frame, outside of the script handler that called us
                return true;
            }
            catch (Exception e) { GD.PrintErr("[save] load failed: " + e); return false; }
        };
        host.It.EntityHandlers["xinterface"] = (e, fmt, d) =>
        {
            if (view == null || !ReferenceEquals(view.Ent, e)) NewView(e);
            return view!.OnMessage(fmt, d);
        };
        // "fader": full-screen picture (FADER_PICTURE 33003, FADER_OUT 33000, FADER_IN 33001)
        host.It.EntityHandlers["fader"] = (e, fmt, d) =>
        {
            int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
            if (fader == null)
            {
                var ent = e;
                fader = new FaderView(n => host.It.FireEvent(n, new List<V> { V.Int(0), V.A(ent.Root) }), () => host.It.DeleteEntity(ent.Root));   // event data: (int, fader object)
                AddChild(fader);
            }
            if (msg == 33003 || msg == 33004) fader.Texture = GameAssets.Texture(Conv.ToStr(d[1]));
            else if (msg == 33000 || msg == 33001) fader.Start(msg == 33001, Conv.ToFloat(d[1]), d.Length > 2 && Conv.ToInt(d[2]) != 0);
            return V.Int(0);
        };
        // "location": static models + locators of the current location
        host.It.EntityHandlers["location"] = (e, fmt, d) =>
        {
            if (!locations.TryGetValue(e, out var lv))
            {
                GD.Print($"[main] new location view for entity {e.Id} at {e.Root.Path}");
                lv = new LocationView(host.It, e);
                locations[e] = lv;
                AddChild(lv);
            }
            return lv.OnMessage(fmt, d);
        };
        // "locationcamera": the script switches between following the hero, a fixed place and free flight
        host.It.EntityHandlers["locationcamera"] = (e, fmt, d) =>
        {
            int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
            switch (msg)
            {
                case 30500: camTarget = Conv.ToInt(d[1]); break;                                       // SETTARGET
                case 30510: camMode = 1; camInit = false; break;                                       // FOLLOW
                case 30512: camMode = 2; camToPos = new Vector3(Conv.ToFloat(d[1]), Conv.ToFloat(d[2]), -Conv.ToFloat(d[3])); break;   // TOPOS
                case 30514: camMode = 3; break;
            }
            return V.Int(1);
        };
        // "MODELR" / "MODEL": a free-standing model (items on the ground, chests, ...): load geometry, textures folder, position
        Func<Entity, string, V[], V> modelHandler = (e, fmt, d) =>
        {
            int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
            string S(int i) => i < d.Length ? Conv.ToStr(d[i]) : "";
            float F(int i) => i < d.Length ? Conv.ToFloat(d[i]) : 0f;
            if (!modelNodes.TryGetValue(e, out var holder)) { holder = new Node3D { Name = "Model_" + e.Id }; modelNodes[e] = holder; AddChild(holder); modelDirs[e] = ""; }
            switch (msg)
            {
                case 20509: modelDirs[e] = S(1); return V.Int(1);                            // SET_DIRPATH (textures folder)
                case 20500:                                                                 // LOAD_GEO "ls"
                {
                    string file = GameAssets.Resource("MODELS\\" + S(1).Replace('/', (char)92) + ".gm");
                    if (!File.Exists(file)) { GD.Print("[model] not found: " + file); return V.Int(0); }
                    try
                    {
                        var g = StormScript.Formats.GmFile.Parse(File.ReadAllBytes(file));
                        foreach (var c in holder.GetChildren()) c.QueueFree();
                        holder.AddChild(ModelFactory.Build(g, Path.GetFileNameWithoutExtension(file), modelDirs[e].TrimEnd((char)92), null));
                        return V.Int(1);
                    }
                    catch (Exception ex) { GD.PrintErr("[model] " + file + ": " + ex.Message); return V.Int(0); }
                }
                case 20506:                                                                 // SET_POSITION: x y z + three basis vectors (already in the renderer's handedness)
                    holder.Position = new Vector3(F(1), F(2), -F(3));
                    if (d.Length >= 13) holder.Basis = new Basis(new Vector3(F(4), F(5), F(6)), new Vector3(F(7), F(8), F(9)), new Vector3(F(10), F(11), F(12)));
                    return V.Int(1);
                case 20504: holder.QueueFree(); modelNodes.Remove(e); return V.Int(1);      // RELEASE
            }
            return V.Int(1);
        };
        host.It.EntityHandlers["MODELR"] = modelHandler;
        host.It.EntityHandlers["MODEL"] = modelHandler;
        host.It.EntityHandlers["CharactersGroups"] = (e, fmt, d) => GroupsState.OnMessage(host.It, d);
        // "Sound": sounds by alias/file, music, ambient schemes
        host.It.EntityHandlers["Sound"] = (e, fmt, d) =>
        {
            if (sound == null) { sound = new SoundView { it0 = host.It }; AddChild(sound); }
            return sound.OnMessage(fmt, d);
        };
        // "ILogAndActions": message log and the hint for the action under the player's feet
        host.It.EntityHandlers["ILogAndActions"] = (e, fmt, d) =>
        {
            if (logHud == null) { logHud = new LogHud(); AddChild(logHud); }
            return logHud.OnMessage(fmt, d);
        };
        // "dialog": NPC line + answers (messages 0/1 name the two participants)
        host.It.EntityHandlers["dialog"] = (e, fmt, d) =>
        {
            if (dialogView == null || !ReferenceEquals(dialogView.Ent, e))
            {
                dialogView?.QueueFree();
                dialogView = new DialogView(host.It, e);
                AddChild(dialogView);
            }
            return V.Int(1);
        };
        // "NPCharacter": every person in a location (placeholder visuals for now)
        Func<Entity, string, V[], V> chr = (e, fmt, d) =>
        {
            if (!characters.TryGetValue(e, out var cv))
            {
                cv = new CharacterView(host.It, e, (g, n) => CurrentLocation()?.FindLocator(g, n), () => CurrentLocation()?.Patch);
                cv.LocationRoot = () => CurrentLocation()?.Ent.Root;
                characters[e] = cv;
                AddChild(cv);
            }
            return cv.OnMessage(fmt, d);
        };
        sea.Register(host.It);
        worldMap.Register(host.It);
        host.It.EntityHandlers["NPCharacter"] = chr;
        host.It.EntityHandlers["Character"] = chr;
        host.It.EntityHandlers["Player"] = chr;
        host.It.EntityDeleted = e =>
        {
            sea.OnEntityDeleted(e);
            worldMap.OnEntityDeleted(e);
            if (view != null && ReferenceEquals(view.Ent, e)) { view.QueueFree(); view = null; }
            if (e.Class == "fader" && fader != null) { fader.QueueFree(); fader = null; }
            if (dialogView != null && ReferenceEquals(dialogView.Ent, e)) { dialogView.QueueFree(); dialogView = null; host.It.TimeScale = 1f; }   // the dialog class restores the game speed when it dies
            if (e.Class == "location") GD.Print($"[main] location entity {e.Id} deleted ({e.Root.Path}), view known: {locations.ContainsKey(e)}");
            if (modelNodes.Remove(e, out var mn)) { mn.QueueFree(); modelDirs.Remove(e); }
            if (e.Class == "ILogAndActions" && logHud != null) { logHud.QueueFree(); logHud = null; }
            if (e.Class == "location")      // the location owns the people standing in it
                foreach (var cls in new[] { "NPCharacter", "Player", "Character" }) host.It.DeleteAllEntities(cls);
            if (e.Class.Equals("locationcamera", StringComparison.OrdinalIgnoreCase)) { camMode = 1; camInit = false; }
            if (locations.Remove(e, out var lv)) lv.QueueFree();
            if (characters.Remove(e, out var cv2)) cv2.QueueFree();
        };

        if (!host.LoadFile("seadogs.c", "<main>")) { GD.PrintErr("cannot load seadogs.c"); GetTree().Quit(1); return; }
        GD.Print($"[main] loaded {host.It.Funcs.Count} script functions, {host.It.Handlers.Count} event names with handlers, TemplateComplite handler: {host.It.Handlers.ContainsKey("LAi_event_Character_TemplateComplite")}");
    }

    /// <summary>Test mode: talk to everybody in the location and click the first answer each time, to find engine functions the scripts need.</summary>
    void FuzzStep()
    {
        if (fuzzQueue == null)
        {
            fuzzQueue = new Queue<string>(characters.Values.Where(c => !c.IsPlayer && c.Placed).Select(c => c.Ent.Root.Get("id")?.Value ?? "").Where(x => x.Length > 0));
            GD.Print($"[fuzz] {fuzzQueue.Count} people to talk to");
            fuzzNext = frames;
        }
        if (frames < fuzzNext) return;
        if (dialogView != null)
        {
            if (++fuzzAnswers > 12 || dialogView.LinkCount == 0) { host.It.CallTop("DialogExit"); fuzzAnswers = 0; fuzzNext = frames + 60; }
            else { dialogView.Choose(0); fuzzNext = frames + 25; }
            return;
        }
        if (fuzzQueue.Count == 0) { if (fuzzAt >= 0) { GD.Print("[fuzz] done"); fuzzAt = -1; } return; }
        string id = fuzzQueue.Dequeue();
        GD.Print("[fuzz] dialog with " + id);
        fuzzAnswers = 0;
        StartDialog(id);
        fuzzNext = frames + 90;
    }

    /// <summary>Starts the script dialog with a character (what the engine does when the player talks to someone).</summary>
    void StartDialog(string id)
    {
        var chr = host.It.CallTop("CharacterFromID", V.Str(id));
        if (chr.Attr == null) { GD.Print("[dialog] no such character: " + id); return; }
        GD.Print("[dialog] starting dialog with " + id);
        var main = host.It.CallTop("GetMainCharacter");
        // the addon starts with a scripted intro (hero has the "actor" AI type); character movement is not emulated yet, so do what
        // the game does when the intro ends: give control to the player
        if (main.Attr?.Get("chr_ai")?.Get("type")?.Value != "player")
        {
            GD.Print("[dialog] (debug) main character type is '" + main.Attr?.Get("chr_ai")?.Get("type")?.Value + "', switching to player");
            host.It.CallTop("LAi_SetPlayerType", main);
        }
        string Flag(string fn, params V[] a) => Conv.ToInt(host.It.CallTop(fn, a)).ToString();
        GD.Print($"[dialog] main={main.Attr?.Get("id")?.Value} IsEntity(main)={Flag("IsEntity", main)} IsEntity(npc)={Flag("IsEntity", chr)} " +
                 $"dead(main)={Flag("LAi_IsDead", main)} dead(npc)={Flag("LAi_IsDead", chr)} canDlg(main,npc)={Flag("LAi_Character_CanDialog", main, chr)} " +
                 $"canDlg(npc,main)={Flag("LAi_Character_CanDialog", chr, main)} node={chr.Attr?.Get("Dialog")?.Get("CurrentNode")?.Value} main.type={main.Attr?.Get("chr_ai")?.Get("type")?.Value} main.tmpl={main.Attr?.Get("chr_ai")?.Get("tmpl")?.Value} npc.type={chr.Attr?.Get("chr_ai")?.Get("type")?.Value} npc.tmpl={chr.Attr?.Get("chr_ai")?.Get("tmpl")?.Value} dialogRun={host.It.Globals["dialogRun"].Val.I}");
        var ok = host.It.CallTop("DialogMain", chr);
        GD.Print("[dialog] DialogMain returned " + Conv.ToInt(ok));
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        // the original control scheme: the engine reports named controls to the script ("Control Activation")
        if (e is InputEventKey { Pressed: true, Echo: false } ck && !UiOpen && dialogView == null)
        {
            string? ctl = ck.PhysicalKeycode == Key.Escape ? "MainMenuDouble" : ck.PhysicalKeycode == Key.Space ? "ChrAction" : ck.PhysicalKeycode == Key.Enter ? "LICommandsActivate"
                : Keys.Is(ck, "MainMenu") ? "MainMenu" : Keys.Is(ck, "Interface") ? "Interface" : Keys.Is(ck, "QuickSave") ? "Quick_Save" : Keys.Is(ck, "QuickLoad") ? "Quick_Load" : null;
            if (ctl != null) host.It.FireEvent("Control Activation", new List<V> { V.Str(ctl) });
        }
        if (e is InputEventKey { Pressed: true, Echo: false } sk && (Keys.Is(sk, "SeaSailUp") || Keys.Is(sk, "SeaSailDown")) && sea.Active && locations.Count == 0 && dialogView == null && !UiOpen)
            sea.SailKey(Keys.Is(sk, "SeaSailUp") ? 1 : -1);
        if (e is InputEventKey { Pressed: true, Echo: false } mk && worldMap.Active && locations.Count == 0 && dialogView == null && !UiOpen)
        {
            if (Keys.Is(mk, "SeaSailUp") || Keys.Is(mk, "SeaSailDown")) worldMap.SailKey(Keys.Is(mk, "SeaSailUp") ? 1 : -1);
            if (Keys.Is(mk, "SeaAction") || mk.PhysicalKeycode is Key.KpEnter or Key.Space) worldMap.Enter();
        }
        if (e is InputEventKey { Pressed: true, Echo: false } sk2 && sea.Active && locations.Count == 0 && dialogView == null && !UiOpen)
        {
            string? ctl = Keys.Is(sk2, "SeaMap") ? "BI_MapEnter" : Keys.Is(sk2, "SeaBalls") ? "BI_ChargeCannonballs" : Keys.Is(sk2, "SeaGrapes") ? "BI_ChargeGrapes" : Keys.Is(sk2, "SeaKnippels") ? "BI_ChargeKnippels" : Keys.Is(sk2, "SeaBombs") ? "BI_ChargeBombs" : null;
            if (ctl != null) host.It.FireEvent("Control Activation", new List<V> { V.Str(ctl) });
            if (Keys.Is(sk2, "SeaAction") || sk2.PhysicalKeycode is Key.KpEnter or Key.Space) SeaAction();
        }
        // F = draw/sheathe the sword (the original used E; E talks here)
        if (e is InputEventKey { Pressed: true, Echo: false } fk && Keys.Is(fk, "LandSword") && dialogView == null && !UiOpen)
            if (characters.Values.FirstOrDefault(c => c.IsPlayer) is { } pf) { pf.FightMode = !pf.FightMode; logHud?.OnMessage("", new V[] { V.Int(45020), V.Int(0), V.Str(pf.FightMode ? "Шаблю добуто" : "Шаблю прибрано") }); }
        if (e is InputEventKey { Pressed: true } f9 && Keys.Is(f9, "FlyCam")) flyMode = !flyMode;          // free debug camera
        if (e is InputEventKey { Pressed: true } f3 && Keys.Is(f3, "NameTags"))                              // show/hide name tags
        {
            nameTags = !nameTags;
            foreach (var c in characters.Values) c.ShowNameTag(nameTags);
        }
        // E or T = talk to the nearest character within reach
        if (e is InputEventKey { Pressed: true } tk && (Keys.Is(tk, "LandTalk") || tk.PhysicalKeycode == Key.T) && dialogView == null && !UiOpen)
        {
            CharacterView? player = characters.Values.FirstOrDefault(c => c.IsPlayer);
            var near = characters.Values.Where(c => !c.IsPlayer && player != null && c.Placed).OrderBy(c => c.Position.DistanceTo(player!.Position)).FirstOrDefault();
            if (near != null && near.Position.DistanceTo(player!.Position) < 3.2f) StartDialog(near.Ent.Root.Get("id")?.Value ?? "");
            else if (near != null) GD.Print("[main] nobody close enough to talk to");
        }
    }

    // ------------------------------------------------------------------ engine loop for characters and the camera
    OutdoorEnv outdoor = null!;
    SeaView sea = null!;
    WorldMapView worldMap = null!;
    int camMode = 1, camTarget = -1;      // locationcamera: 1 follow, 2 fixed position (script), 3 free
    Vector3 camToPos;
    float camYaw, camPitch = 0.38f, camDist = 3.6f;
    bool camInit, flyMode, rmbDown;
    bool nameTags;

    void UpdateWorld(float delta)
    {
        float scale = host.It.TimeScale;
        float dt = delta * scale;
        var list = characters.Values.ToList();
        var player = list.FirstOrDefault(c => c.IsPlayer);

        // player input: only when no dialog/interface has the keyboard
        bool free = dialogView == null && !UiOpen && !flyMode;
        if (player != null)
        {
            var mv = Vector2.Zero;
            if (free)
            {
                if (Keys.Down("LandForward")) mv.Y += 1;
                if (Keys.Down("LandBack")) mv.Y -= 1;
                if (Keys.Down("LandRight")) mv.X += 1;
                if (Keys.Down("LandLeft")) mv.X -= 1;
                if (Keys.Down("LandCamLeft")) camYaw += 1.8f * delta;
                if (Keys.Down("LandCamRight")) camYaw -= 1.8f * delta;
                if (Keys.Down("LandCamUp")) camPitch = Math.Clamp(camPitch + 0.9f * delta, 0.05f, 1.3f);
                if (Keys.Down("LandCamDown")) camPitch = Math.Clamp(camPitch - 0.9f * delta, 0.05f, 1.3f);
            }
            foreach (var (f0, f1, dir) in moves) if (frames >= f0 && frames <= f1 && dialogView == null) mv = dir;     // test hook
            player.Input = mv;
            player.Run = Keys.Down("LandRun");
            player.CameraYaw = camYaw;
        }

        if (dt > 0f) GroupsState.Tick(dt);
        foreach (var c in list) { c.SetTimeScale(scale); c.ShowNameTag(nameTags); c.Tick(dt); }

        // the game clock (the engine's weather module kept Environment.time running): one game minute per 2.5 s of running game time, only while a
        // location is shown and nothing (dialog, interface screen) freezes the game
        if (locations.Count > 0 && dt > 0f && !UiOpen && dialogView == null && host.It.Globals.ContainsKey("Environment"))
        {
            clockAcc += dt;
            while (clockAcc >= 2.5f)
            {
                clockAcc -= 2.5f;
                host.It.CallTop("AddTimeToCurrent", V.Int(0), V.Int(1));
            }
        }

        // the engine tells the script about every frame of every character: this is what drives the AI templates
        if (dt > 0f && list.Any(c => c.Placed))
        {
            host.It.FireEvent("CharactersStateUpdate", new List<V> { V.Float(dt) });
            foreach (var c in list)
                if (c.Placed) host.It.FireEvent("CharacterUpdate", new List<V> { V.A(c.Ent.Root), V.Float(dt) });
        }

        // world map
        if (worldMap.Active && locations.Count == 0)
        {
            worldMap.TestTurn = 0f;
            foreach (var (f0, f1, dir) in moves) if (frames >= f0 && frames <= f1) worldMap.TestTurn = dir.X;
            worldMap.Tick(dt, dialogView == null && !UiOpen);
            return;
        }

        // sea mode: the ship camera replaces the location camera
        if (sea.Active && locations.Count == 0)
        {
            sea.TestTurn = 0f;
            foreach (var (f0, f1, dir) in moves) if (frames >= f0 && frames <= f1) sea.TestTurn = dir.X;
            sea.Tick(dt, dialogView == null && !UiOpen);
            outdoor.Sync(host.It, sea.Cam, delta);
            return;
        }

        // follow camera
        var cam = CurrentLocation()?.Cam;
        outdoor.Sync(host.It, cam, delta);
        if (cam == null) return;
        cam.Enabled = flyMode;
        if (camMode == 2 && !flyMode)
        {
            // script-placed camera ("locCameraToPos"): fixed position, looking at the camera target (the main character)
            var tc = list.FirstOrDefault(c => c.Ent.Root.Get("index")?.Value == camTarget.ToString()) ?? player;
            cam.GlobalPosition = camToPos;
            if (tc != null && tc.Placed) cam.LookAt(tc.Position + new Vector3(0, 1.2f, 0));
            return;
        }
        if (player == null || !player.Placed || flyMode) return;
        if (!camInit) { camYaw = -player.Heading; camInit = true; }
        var target = player.Position + new Vector3(0, 1.45f, 0);
        var off = new Vector3(MathF.Sin(camYaw) * MathF.Cos(camPitch), MathF.Sin(camPitch), MathF.Cos(camYaw) * MathF.Cos(camPitch)) * camDist;
        var want = target + off;
        // keep the camera inside the room: shorten the boom when a wall is in the way
        var space = cam.GetWorld3D()?.DirectSpaceState;
        if (space != null)
        {
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(target, want));
            if (hit.Count > 0)
            {
                var hp = (Vector3)hit["position"];
                float dist = Math.Max(0.5f, target.DistanceTo(hp) - 0.25f);
                want = target + (want - target).Normalized() * dist;
            }
        }
        cam.GlobalPosition = want;
        cam.LookAt(target);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } && sea.Active && locations.Count == 0 && dialogView == null && !UiOpen) { sea.FirePlayer(); return; }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } && dialogView == null && !UiOpen)
        {
            // attack with the sword when it is drawn
            var pl = characters.Values.FirstOrDefault(c => c.IsPlayer);
            if (pl != null && pl.FightMode && pl.Placed) { var en = pl.NearestInFront(2.2f); if (en != null) pl.StartSwing(en); }
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right } mb) rmbDown = mb.Pressed;
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }) { if (sea.Active && locations.Count == 0) sea.Zoom(-1); else if (worldMap.Active && locations.Count == 0) worldMap.Zoom(-1); else camDist = Math.Max(1.5f, camDist - 0.3f); }
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }) { if (sea.Active && locations.Count == 0) sea.Zoom(1); else if (worldMap.Active && locations.Count == 0) worldMap.Zoom(1); else camDist = Math.Min(9f, camDist + 0.3f); }
        else if (e is InputEventMouseMotion mm && rmbDown && !flyMode)
        {
            // the mouse sensitivity sliders and the invert option of the options screen live in InterfaceStates (script), 0.5 = normal
            float sx = RealSens("mouse.x_sens"), sy = RealSens("mouse.y_sens");
            bool inv = host.It.Globals.TryGetValue("InterfaceStates", out var ist) && ist.Val.Attr?.Get("InvertCameras")?.Value == "1";
            float dx = mm.Relative.X * sx, dy = mm.Relative.Y * sy * (inv ? -1f : 1f);
            if (sea.Active && locations.Count == 0) sea.Look(dx, dy);
            else if (worldMap.Active && locations.Count == 0) worldMap.Look(dx, dy);
            else { camYaw -= dx * 0.005f; camPitch = Math.Clamp(camPitch + dy * 0.004f, 0.05f, 1.3f); }
        }
    }

    float RealSens(string path)
    {
        float f = 0.5f;
        if (host.It.Globals.TryGetValue("InterfaceStates", out var g) && g.Val.Attr?.Find(path, false)?.Value is { Length: > 0 } v) f = Conv.ParseFloatPrefix(v);
        return f <= 0.5f ? 0.5f + f : f * 2f;          // SetRealMouseSensitivity() of CONTROLS\controls.c
    }

    LocationView? CurrentLocation() { foreach (var l in locations.Values) return l; return null; }

    /// <summary>Executes the sea action the HUD offers (Moor / Board / LandTroops / Map ...): the engine's battle interface raised "BI_FastCommand" with its name.</summary>
    void SeaAction()
    {
        if (worldMap.Active) { worldMap.Enter(); return; }
        string act = host.It.Globals.TryGetValue("g_ActiveActionName", out var g) ? Conv.ToStr(g.Val) : "";
        GD.Print("[sea] action: " + act);
        if (act.Length > 0 && act != "Nothing") host.It.FireEvent("BI_FastCommand", new List<V> { V.Str(act) });
    }

    /// <summary>An interface screen is on display (the xinterface entity also exists, empty, while the sea HUD talks to it).</summary>
    bool UiOpen => view != null && view.GetChildCount() > 0;

    void NewView(Entity e)
    {
        view?.QueueFree();
        view = new XView(host.It, e) { Name = "XInterface" };
        AddChild(view);
    }

    public override void _Process(double delta)
    {
        if (!started)
        {
            started = true;
            host.It.CallTop("Main");
        }
        if (rebuildWorld)
        {
            rebuildWorld = false;
            // after restoring the variables the engine calls the script's OnLoad(): it re-creates the interfaces, quests and the location itself
            camMode = 1; camInit = false;
            GD.Print("[save] OnLoad()");
            host.It.CallTop("OnLoad");
        }
        if (logHud != null) logHud.Visible = dialogView == null && !UiOpen;        // no log/action hint over dialogs and interface screens
        UpdateWorld((float)delta);
        host.It.Advance((int)(delta * 1000));
        // keep a small copy of the last frame without an interface on top: it becomes the thumbnail of the next save
        if (!UiOpen && dialogView == null && frames % 45 == 0) lastGameFrame = GrabFrame();
        view?.FillSaveStrings();
        host.It.FireEvent("frame");
        frames++;

        foreach (var (f, n) in clicks) if (f == frames) view?.Click(n);
        foreach (var (f, k, v) in sets) if (f == frames) host.It.SetGlobal(k, v);
        foreach (var (f, fn) in calls)
            if (f == frames)
            {
                // "Func" or "Func(arg,arg)": integers, strings (+ = space) and #CharacterId
                int lp = fn.IndexOf('(');
                var args = new List<V>();
                string name = lp < 0 ? fn : fn.Substring(0, lp);
                if (lp >= 0)
                    foreach (var x in fn.Substring(lp + 1).TrimEnd(')').Split(',', StringSplitOptions.RemoveEmptyEntries))
                        args.Add(x.StartsWith('#') ? host.It.CallTop("CharacterFromID", V.Str(x.Substring(1).Replace('+', ' '))) : int.TryParse(x, out int iv) ? V.Int(iv) : V.Str(x.Replace('+', ' ')));
                host.It.CallTop(name, args.ToArray());
            }
        if (controlAt == frames) host.It.CallTop("LAi_SetPlayerType", host.It.CallTop("GetMainCharacter"));    // test hook: end the scripted intro
        if (fuzzAt >= 0 && frames >= fuzzAt) FuzzStep();
        foreach (var (f, p) in dumps) if (f == frames) host.It.DumpPath(p);
        foreach (var (f, n) in ctls) if (f == frames) host.It.FireEvent("Control Activation", new List<V> { V.Str(n) });
        foreach (var (f, dir) in sails) if (f == frames) sea.SailKey(dir);
        foreach (var f in seaActions) if (f == frames) SeaAction();
        foreach (var f in seaFires) if (f == frames) sea.FirePlayer();
        foreach (var f in seaDuels) if (f == frames) sea.Duel();
        foreach (var (f, nm) in loadAt) if (f == frames)
        {
            host.It.SetHandler("evntLoad", "LoadGame", 1);
            host.It.PostEventInternal("evntLoad", 0, new List<V> { V.Str("SAVE\\" + nm) });
            host.It.FireEvent("evntPreLoad");
        }
        foreach (var (f, nm) in saveAt) if (f == frames)
        {
            host.It.SetHandler("evntSave", "SaveGame", 1);
            host.It.PostEventInternal("evntSave", 0, new List<V> { V.Str("SAVE\\" + nm), V.Str(nm.Replace('_', ' ')) });
        }
        foreach (var (f, k) in keyPresses) if (f == frames) { foreach (var down in new[] { true, false }) Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = down }); }
        foreach (var f in seaDuels) if (f == frames) sea.Duel();
        foreach (var (f, x, z, ay) in seaTeleports) if (f == frames) { sea.Teleport(x, z, ay); worldMap.Teleport(x, z, ay); }
        foreach (var (f, g) in gotos) if (f == frames) { var pl = characters.Values.FirstOrDefault(c => c.IsPlayer); var gp = g.Split('.'); pl?.TeleportToLocator(gp[0], gp[1]); }
        foreach (var (f, id) in dialogTests) if (f == frames) StartDialog(id);
        foreach (var (f, a) in answers) if (f == frames) dialogView?.Choose(a - 1);
        if (shotPath != null && frames == shotAtFrame)
        {
            GetViewport().GetTexture().GetImage().SavePng(shotPath);
            GD.Print("[main] screenshot saved: " + shotPath);
            foreach (var kv in host.It.MessageStats.Where(k => !host.It.EntityHandlers.ContainsKey(k.Key.Split('|')[0].Trim())).OrderByDescending(k => k.Value).Take(40)) GD.Print($"[nohandler] {kv.Value}x {kv.Key}");
            GD.Print($"[main] TimeScale={host.It.TimeScale}, locations={locations.Count}, entities alive: " + string.Join(", ", host.It.Entities.Where(en => !en.Deleted).GroupBy(en => en.Class).Select(g => g.Key + "x" + g.Count())));
            foreach (var ch in GetChildren()) GD.Print($"[tree] {ch.Name} {ch.GetType().Name} children={ch.GetChildCount()} queued={ch.IsQueuedForDeletion()}");
            foreach (var c in characters.Values) GD.Print("[chars] " + c.DebugState());
            foreach (var kv in host.It.RuntimeErrors.OrderByDescending(k => k.Value).Take(25)) GD.Print($"[errors] {kv.Value}x {kv.Key}");
            foreach (var kv in host.It.EventsFired.OrderByDescending(k => k.Value).Take(40)) GD.Print($"[events] {kv.Value}x {kv.Key} handlers={(host.It.Handlers.TryGetValue(kv.Key, out var hl) ? string.Join(",", hl.Select(h => h.fn)) : "-")}");
            foreach (var kv in host.It.UnresolvedFuncs.OrderByDescending(k => k.Value).Take(25)) GD.Print($"[unresolved] {kv.Value}x {kv.Key}");
            GetTree().Quit();
        }
        if (host.It.Exit) GetTree().Quit();
    }
}
