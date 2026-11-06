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
    BoardingStatus? boardingStatus;
    LandHud? landHud;
    bool rebuildWorld;
    float clockAcc;
    SoundView? sound;
    readonly Dictionary<Entity, Node3D> modelNodes = new();
    readonly Dictionary<Entity, string> modelDirs = new();
    readonly Dictionary<Node3D, (float from, float to, float t, float dur)> modelFades = new();     // MSG_MODEL_BLEND in progress
    readonly Dictionary<Entity, LocationView> locations = new();
    readonly Dictionary<Entity, CharacterView> characters = new();
    bool started;
    bool camPlaced;
    int frames;
    readonly bool prof = System.Environment.GetEnvironmentVariable("SEADOGS_PROF") != null;
    long profWorld, profAdv, profFrame; double profObj;
    Image? lastGameFrame;

    Image? GrabFrame(bool full = false)
    {
        var img = GetViewport().GetTexture()?.GetImage();
        if (img == null) return null;
        if (!full) img.Resize(256, 192, Image.Interpolation.Bilinear);
        return img;
    }
    string? shotPath;
    string? programArg;
    string language = "Ukrainian";
    int shotAtFrame = -1;
    readonly List<(int frame, string node)> clicks = new();
    readonly List<(int frame, string node)> mouseClicks = new();
    readonly List<(int frame, string id)> dialogTests = new();
    readonly List<(int frame, string name, string value)> sets = new();
    readonly List<(int frame, string fn)> calls = new();
    readonly List<(int frame, int answer)> answers = new();
    readonly List<(int from, int to, Vector2 dir)> moves = new();
    readonly List<(int frame, string path)> dumps = new();
    readonly List<(int frame, string name)> ctls = new();
    int fuzzAt = -1, fuzzNext, fuzzSince, fuzzAnswers;
    Queue<string>? fuzzQueue;
    readonly HashSet<int> fuzzList = new();
    readonly List<(int frame, string loc)> gotos = new();
    readonly List<(int frame, float x, float z, float ay)> seaTeleports = new();      // test hook --seapos=x,z,ay@N
    readonly List<int> seaFires = new();                      // test hook --fire@N: broadside
    readonly List<(int frame, Key key, bool shift)> keyPresses = new();      // test hook --key=Escape@N: a real key press through the input system
    readonly List<(int frame, string text)> typed = new();       // test hook --type=Text@N: real key presses with characters ('+' = space, %XXXX = Unicode hex: %0456 = і)

    static string TypedText(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '+') sb.Append(' ');
            else if (s[i] == '%' && i + 4 < s.Length && int.TryParse(s.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out int u)) { sb.Append((char)u); i += 4; }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Sends every character as a real key press: the keycode of a Latin letter/digit (layout-independent keys) and the character itself.</summary>
    static void TypeText(string s)
    {
        GD.Print($"[type] '{s}'");
        foreach (char ch in s)
        {
            char up = char.ToUpperInvariant(ch);
            Key key = up is >= 'A' and <= 'Z' or >= '0' and <= '9' ? (Key)up : ch == ' ' ? Key.Space : Key.None;
            foreach (var down in new[] { true, false })
                Godot.Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Unicode = ch, Pressed = down, ShiftPressed = char.IsUpper(ch) });
        }
    }
    readonly List<(int frame, string name)> saveAt = new();       // test hook --save=name@N: write SAVE\name through the script's own save path
    readonly List<(int frame, string name)> loadAt = new();       // test hook --load=name@N
    readonly List<int> seaDuels = new();                      // test hook --duel@N
    int actRetryUntil;
    readonly List<(int, string)> showHits = new();
    readonly List<int> swings = new();
    readonly List<(int frame, float x, float z, float ay)> mapTeleports = new();      // test hook --mappos=x,z,ay@N: place the WORLD MAP ship once the map is built (retries every 10 frames)
    readonly List<(int frame, float dx, float dz)> mapStorms = new();   // test hook --mapstorm=dx,dz@N: a world-map storm placed at the ship + (dx, dz) (retries every 10 frames until the map is built)
    readonly List<int> mapMeets = new();                      // test hook --mapmeet@N: the world-map ship is put next to the first encounter ship
    readonly List<int> seaActions = new();                  // test hook --act@N: execute the offered sea action (Enter)
    readonly List<(int frame, int dir)> sails = new();       // test hook --sail=+1@N / --sail=-1@N: sail state up/down at sea
    readonly List<(int frame, string what)> rigTests = new();   // test hooks --mastfall@N / --sailholes@N (=me: of the player's ship): break a mast / hole the sails of the nearest other ship
    int controlAt = -1;
    readonly List<(int frame, string group)> killGroups = new();   // test hook --killgroup=brdenemy@N: kill every living character of that script group (LAi_KillCharacter)

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
            else if (a.StartsWith("--showhit=")) { var p = a.Substring(10).Split('@'); showHits.Add((int.Parse(p[1]), p[0].Replace('_', ' '))); }       // test hook: the hero's-blow health numbers over a character, --showhit=Id:damage@N (damage 0 = blocked)
            else if (a.StartsWith("--bring=")) { var p = a.Substring(8).Split('@'); brings.Add((int.Parse(p[1]), p[0].Replace('_', ' '))); }       // test hook: put a character 1.6 m in front of the player
            else if (a.StartsWith("--dialog=")) { var p = a.Substring(9).Split('@'); dialogTests.Add((int.Parse(p[1]), p[0].Replace('_', ' '))); }
            else if (a.StartsWith("--set=")) { var p = a.Substring(6).Split('@'); var kv = p[0].Split('='); sets.Add((int.Parse(p[1]), kv[0], kv[1])); }
            else if (a.StartsWith("--call="))  { var p = a.Substring(7).Split('@'); calls.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--move=")) { var p = a.Substring(7).Split('@'); var xy = p[0].Split(','); var fr = p[1].Split('-'); moves.Add((int.Parse(fr[0]), int.Parse(fr[1]), new Vector2(float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture)))); }
            else if (a.StartsWith("--dump=")) { var p = a.Substring(7).Split('@'); dumps.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--at=")) { var p = a.Substring(5).Split('@'); var v = p[0].Split(','); var ci = System.Globalization.CultureInfo.InvariantCulture; atHooks.Add((int.Parse(p[1]), float.Parse(v[0], ci), float.Parse(v[1], ci), float.Parse(v[2], ci), float.Parse(v[3], ci))); }      // test hook: stand at x,y,z facing ay
            else if (a.StartsWith("--jump@")) jumpAtFrame = int.Parse(a.Substring(7));      // test hook: the hero jumps forward at frame N
            else if (a == "--run") forceRun = true;                                         // test hook: the hero runs whenever he moves
            else if (a == "--classic") Keys.Classic = true;                                // the original land control scheme (see Keys.Classic)
            else if (a.StartsWith("--look@")) lookAtFrame = int.Parse(a.Substring(7));       // Tab (first-person view) at frame N
            else if (a.StartsWith("--camdist=")) camDist = float.Parse(a.Substring(10), System.Globalization.CultureInfo.InvariantCulture);
            else if (a.StartsWith("--fuzz@")) fuzzList.Add(int.Parse(a.Substring(7)));
            else if (a.StartsWith("--ctl=")) { var p = a.Substring(6).Split('@'); ctls.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--mappos=")) { var p = a.Substring(9).Split('@'); var v = p[0].Split(','); var ci = System.Globalization.CultureInfo.InvariantCulture; mapTeleports.Add((int.Parse(p[1]), float.Parse(v[0], ci), float.Parse(v[1], ci), float.Parse(v[2], ci))); }
            else if (a.StartsWith("--mapmeet@")) mapMeets.Add(int.Parse(a.Substring(10)));
            else if (a.StartsWith("--mapstorm=")) { var p = a.Substring(11).Split('@'); var v = p[0].Split(','); var ci = System.Globalization.CultureInfo.InvariantCulture; mapStorms.Add((int.Parse(p[1]), float.Parse(v[0], ci), float.Parse(v[1], ci))); }
            else if (a.StartsWith("--seapos=")) { var p = a.Substring(9).Split('@'); var v = p[0].Split(','); var ci = System.Globalization.CultureInfo.InvariantCulture; seaTeleports.Add((int.Parse(p[1]), float.Parse(v[0], ci), float.Parse(v[1], ci), float.Parse(v[2], ci))); }
            else if (a.StartsWith("--key=")) { var p = a.Substring(6).Split('@'); bool sh = p[0].StartsWith("Shift+", StringComparison.OrdinalIgnoreCase); keyPresses.Add((int.Parse(p[1]), Enum.Parse<Key>(sh ? p[0].Substring(6) : p[0], true), sh)); }      // --key=Shift+Right@N: with Shift held (speed actions)
            else if (a.StartsWith("--type=")) { int at = a.LastIndexOf('@'); typed.Add((int.Parse(a.Substring(at + 1)), TypedText(a.Substring(7, at - 7)))); }
            else if (a.StartsWith("--save=")) { var p = a.Substring(7).Split('@'); saveAt.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--load=")) { var p = a.Substring(7).Split('@'); loadAt.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--duel@")) seaDuels.Add(int.Parse(a.Substring(7)));
            else if (a.StartsWith("--fire@")) seaFires.Add(int.Parse(a.Substring(7)));
            else if (a.StartsWith("--swing@")) swings.Add(int.Parse(a.Substring(8)));       // test hook: the hero strikes the nearest person (a quick click of the left button)
            else if (a.StartsWith("--animals@")) animalsAt = int.Parse(a.Substring(10));
            else if (a.StartsWith("--act@")) { int fr = int.Parse(a.Substring(6)); seaActions.Add(fr); actRetryUntil = Math.Max(actRetryUntil, fr + 400); }
            else if ((a.StartsWith("--mastfall") || a.StartsWith("--sailholes") || a.StartsWith("--shipfire") || a.StartsWith("--shipboom") || a.StartsWith("--sharks") || a.StartsWith("--blot") || a.StartsWith("--splash")) && a.Contains('@')) rigTests.Add((int.Parse(a.Substring(a.IndexOf('@') + 1)), a.Substring(2, a.IndexOf('@') - 2)));
            else if (a.StartsWith("--sail=")) { var p = a.Substring(7).Split('@'); sails.Add((int.Parse(p[1]), int.Parse(p[0]))); }
            else if (a.StartsWith("--goto=")) { var p = a.Substring(7).Split('@'); gotos.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--killgroup=")) { var p = a.Substring(12).Split('@'); killGroups.Add((int.Parse(p[1]), p[0])); }
            else if (a.StartsWith("--control@")) controlAt = int.Parse(a.Substring(10));
            else if (a.StartsWith("--answer=")) { var p = a.Substring(9).Split('@'); answers.Add((int.Parse(p[1]), int.Parse(p[0]))); }
            else if (a.StartsWith("--mouse=")) { var p = a.Substring(8).Split('@'); mouseClicks.Add((int.Parse(p[1]), p[0])); }      // a real mouse click on a node of the interface (input system, not a direct call)
            else if (a.StartsWith("--click=")) { var p = a.Substring(8).Split('@'); clicks.Add((int.Parse(p[1]), p[0])); }
        }
        GameAssets.GameDir = game;
        outdoor = new OutdoorEnv { Name = "Outdoor" };
        weatherFx = new WeatherFx();
        AddChild(weatherFx);
        AddChild(outdoor);
        GD.Print("[main] game folder: " + game);

        // scripts: the addon tree (base PROGRAM overlaid with the files unpacked from stormex2.dll) if it was built, else plain PROGRAM
        string portDir = Path.GetFullPath(Path.Combine(res, ".."));
        portDirCached = portDir;
        string merged = Path.Combine(portDir, "addon", "merged", "PROGRAM");
        string program = programArg ?? (Directory.Exists(merged) ? merged : Path.Combine(game, "PROGRAM"));
        GD.Print("[main] scripts: " + program + ", language: " + language);
        host = new ScriptHost(program);
        host.It.EntityHandlers["Lightning"] = (e, fmt, d) => weatherFx.OnLightning(d);
        // port settings (settings.ini next to the project): the hidden "beta/test" page of the options screen is enabled by the original #define MOD_BETTATESTMODE
        string settingsFile = Path.Combine(portDir, "settings.ini");
        if (!File.Exists(settingsFile))
            File.WriteAllText(settingsFile, "; Port settings. BetaTestMode: Off | Test | On  (Test/On show the extra beta page of the options screen: cheats, debug logs, locator display ...)\n[game]\nBetaTestMode = On\n");
        var settings = IniFile.Load(settingsFile);
        Keys.Load(settingsFile);
        string beta = settings.Sections.TryGetValue("game", out var gs) ? gs.Get("BetaTestMode")?.Trim() ?? "Off" : "Off";
        if (beta is "Test" or "On") host.Pp.DefineOverrides["MOD_BETTATESTMODE"] = beta;
        GD.Print("[main] BetaTestMode = " + beta);
        // [video]: Fullscreen = 1 starts in full screen; WindowWidth / WindowHeight set the window size (F11 toggles full screen). The 2D interface adapts to any aspect ratio.
        if (settings.Sections.TryGetValue("video", out var vs) && !OS.GetCmdlineUserArgs().Any(a => a.StartsWith("--shot")))
        {
            if (int.TryParse(vs.Get("WindowWidth"), out int ww) && int.TryParse(vs.Get("WindowHeight"), out int wh) && ww >= 640 && wh >= 480) GetWindow().Size = new Vector2I(ww, wh);
            if (vs.Get("Fullscreen")?.Trim() == "1") GetWindow().Mode = Window.ModeEnum.Fullscreen;
            if (GetWindow().Mode == Window.ModeEnum.Windowed)      // the window sits in the middle of the primary monitor, never across two screens
            {
                var scr = DisplayServer.GetPrimaryScreen();
                var area = DisplayServer.ScreenGetUsableRect(scr);
                var sz = GetWindow().Size;
                GetWindow().Position = area.Position + new Vector2I(Math.Max(0, (area.Size.X - sz.X) / 2), Math.Max(0, (area.Size.Y - sz.Y) / 2));
            }
        }
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
        // videos (.avi) are not played; the "you died" films of the game over get a black screen with a caption so the return to the main menu is not a surprise
        host.It.EntityHandlers["CAviPlayer"] = (e, fmt, d) =>
        {
            if (d.Length > 0 && Conv.ToInt(d[0]) == 49000)
            {
                string vid = d.Length > 1 ? Conv.ToStr(d[1]).ToLowerInvariant() : "";
                bool death = vid.Contains("dead") || vid.Contains("skeleton") || vid.Contains("undersea") || vid.Contains("mutiny");
                if (death) ShowCaption("Ви загинули.\nГру завершено.", 4.5f);
                host.It.PostEventInternal("ievntEndVideo", death ? 4600 : 50, new List<V>());
            }
            return V.Int(1);
        };
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
            if (System.Environment.GetEnvironmentVariable("SEADOGS_FADERLOG") != null) GD.Print($"[fader] entity {e.Id} msg {msg} view={(fader == null ? "new" : "existing")}");
            if (fader == null)
            {
                var ent = e;
                fader = new FaderView((n, fadeIn) => host.It.FireEvent(n, new List<V> { V.Int(fadeIn), V.A(ent.Root) }), () => host.It.DeleteEntity(ent.Root));   // event data: (int, fader object)
                AddChild(fader);
            }
            if (msg == 33002) fader.CaptureFrame(GrabFrame(true));            // FADER_STARTFRAME: freeze the current frame under the picture
            else if (msg == 33003 || msg == 33004) fader.Texture = GameAssets.Texture(Conv.ToStr(d[1]));
            else if (msg == 33000 || msg == 33001) fader.Start(msg == 33001, Conv.ToFloat(d[1]), d.Length > 2 && Conv.ToInt(d[2]) != 0);
            return V.Int(0);
        };
        // "scrshoter" (xinterface/scr_shoter): MSG_SCRSHOT_MAKE 45042 = texture of the screenshot taken for the new save; MSG_SCRSHOT_READ 45043 "lsse" (dir, file, &data)
        // = texture of a stored save picture (-1: none) + the save's description string; MSG_SCRSHOT_RELEASE 45044. The pictures are PNGs next to the saves
        // (SaveFiles.WriteThumbnail) and the list control loads them itself, so the texture ids are just 1 / -1.
        host.It.EntityHandlers["scrshoter"] = (e, fmt, d) =>
        {
            int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
            string file = d.Length > 2 ? Conv.ToStr(d[2]) : "";
            switch (msg)
            {
                case 45042: return V.Int(1);
                case 45043:
                    if (d.Length > 3) host.It.WriteRef(d[3], V.Str(file.Equals("newsave", StringComparison.OrdinalIgnoreCase) ? "" : SaveFiles.ReadDescription(file)));
                    return V.Int(file.Equals("newsave", StringComparison.OrdinalIgnoreCase) || SaveFiles.ReadThumbnail(file) != null ? 1 : -1);
                default: return V.Int(0);
            }
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
                case 30513: camLockPitch = Conv.ToFloat(d[1]); break;                                   // MOVE "lf": lock the vertical angle (boarding decks: lockCamAngle 0.4)
                case 30520: camSleep = Conv.ToInt(d[1]) != 0; break;                                    // SLEEP "ll": the camera stops (cut scenes, interfaces over a frozen picture)
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
                        var built = ModelFactory.Build(g, Path.GetFileNameWithoutExtension(file), modelDirs[e].TrimEnd((char)92), null);
                        holder.AddChild(built);
                        LandFlags.Register(e, built, g);
                        GD.Print("[model] loaded " + S(1));
                        return V.Int(1);
                    }
                    catch (Exception ex) { GD.PrintErr("[model] " + file + ": " + ex.Message); return V.Int(0); }
                }
                case 20506:                                                                 // SET_POSITION: x y z + the basis vectors vx vy vz the original engine used as they come
                    // (the scripts pass them with z negated). The meshes are built with z negated (S = diag(1,1,-1)), so the node gets S*M*S: z rows and z column flip sign.
                    holder.Position = new Vector3(F(1), F(2), -F(3));
                    if (d.Length >= 13) holder.Basis = new Basis(new Vector3(F(4), F(5), -F(6)), new Vector3(F(7), F(8), -F(9)), new Vector3(-F(10), -F(11), F(12)));
                    return V.Int(1);
                case 20504: holder.QueueFree(); modelNodes.Remove(e); return V.Int(1);      // RELEASE
                case 20507:                                                                 // BLEND "lslff": technique, time (ms), alpha from -> to (a picked item fades out, a placed one fades in)
                    GD.Print($"[model] blend {holder.Name} {F(3)} -> {F(4)}");
                    FadeModel(holder, F(3), F(4), Math.Max(0.05f, F(2) / 1000f));
                    return V.Int(1);
            }
            return V.Int(1);
        };
        // "Animals": seagulls over the harbour / round the ship, butterflies in the jungle (animals.c)
        animalsView = new AnimalsView();
        AddChild(animalsView);
        host.It.EntityHandlers["Animals"] = (e, fmt, d) => animalsView.OnMessage(d);
        animalsClear = animalsView.Clear;
        host.It.EntityHandlers["Flag"] = (e, fmt, d) => LandFlags.OnMessage(d);       // flags of towns, ports and forts (the ship flags are built by SeaView)
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
        // "IBoardingStatus": crew strength strips during boarding
        host.It.EntityHandlers["IBoardingStatus"] = (e, fmt, d) =>
        {
            if (boardingStatus == null || !ReferenceEquals(boardingStatus.Ent, e)) { boardingStatus?.QueueFree(); boardingStatus = new BoardingStatus(e); AddChild(boardingStatus); }
            return boardingStatus.OnMessage(d);
        };
        // "battle_land_interface": portraits of the hero and the officers, the land command menu
        host.It.EntityHandlers["battle_land_interface"] = (e, fmt, d) =>
        {
            if (landHud == null) { landHud = new LandHud(host.It); AddChild(landHud); }
            return landHud.OnMessage(e, fmt, d);
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
                CharShadow.ReceiversOf ??= () => CurrentLocation()?.Receivers();
                CharacterView.JumpPatchOf ??= () => CurrentLocation()?.JumpPatch;
                CharacterView.Obstacle ??= (a, b) => CurrentLocation()?.Cam?.GetWorld3D()?.DirectSpaceState is { } sp && sp.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(a.X, a.Y, -a.Z), new Vector3(b.X, b.Y, -b.Z))).Count > 0;
                CharacterView.SeaLevelOf ??= (px, pz) => outdoor.HasSea && CurrentLocation() is { } lv && lv.Ent.Root.Get("swimming")?.Value != "0" ? SeaWaves.Height(px, -pz) : null;
                cv = new CharacterView(host.It, e, (g, n) => CurrentLocation()?.FindLocator(g, n), () => CurrentLocation()?.Patch);
                cv.LocationRoot = () => CurrentLocation()?.Ent.Root;
                characters[e] = cv;
                AddChild(cv);
            }
            return cv.OnMessage(fmt, d);
        };
        // "ActivePerkShower": icons of the abilities in use (BATTLE_INTERFACE\ActivePerksShow.c), land and sea
        AddChild(perkView);
        host.It.EntityHandlers["ActivePerkShower"] = (e, fmt, d) => perkView.OnMessage(host.It, e, d);
        var particles = new ParticleService();
        AddChild(particles);
        particles.Register(host.It);
        sea.Register(host.It);
        worldMap.Register(host.It);
        CharacterView.FindDialog = () => NearestTalkable() is { } nt && nt.Ent.Root.Get("index")?.Value is { } ix ? (int)Conv.ParseFloatPrefix(ix) : -1;
        CharacterView.GroupLocators = g => CurrentLocation()?.LocatorsOfGroup(g) ?? Enumerable.Empty<StormScript.Formats.GmFile.Label>();
        host.It.EntityHandlers["NPCharacter"] = chr;
        host.It.EntityHandlers["Character"] = chr;
        host.It.EntityHandlers["Player"] = chr;
        // "WideScreen": the cinema bars of the quest movies appear when the entity is created, a message retracts them
        host.It.EntityCreated = e =>
        {
            if (!e.Class.Equals("WideScreen", StringComparison.OrdinalIgnoreCase)) return;
            wideScreen?.QueueFree();
            var ent = e;
            wideScreen = new WideScreenView(() => host.It.DeleteEntity(ent.Root));
            AddChild(wideScreen);
        };
        host.It.EntityHandlers["WideScreen"] = (e, fmt, d) => { wideScreen?.Retract(); return V.Int(0); };
        host.It.EntityDeleted = e =>
        {
            if (e.Class.Equals("WideScreen", StringComparison.OrdinalIgnoreCase) && wideScreen != null) { wideScreen.QueueFree(); wideScreen = null; }
            sea.OnEntityDeleted(e);
            worldMap.OnEntityDeleted(e);
            if (view != null && ReferenceEquals(view.Ent, e)) { view.QueueFree(); view = null; }
            if (e.Class == "Animals") animalsClear?.Invoke();
            if (e.Class == "fader" && fader != null) { fader.QueueFree(); fader = null; }
            if (dialogView != null && ReferenceEquals(dialogView.Ent, e)) { dialogView.QueueFree(); dialogView = null; host.It.TimeScale = 1f; }   // the dialog class restores the game speed when it dies
            if (e.Class == "location") GD.Print($"[main] location entity {e.Id} deleted ({e.Root.Path}), view known: {locations.ContainsKey(e)}");
            if (modelNodes.Remove(e, out var mn)) { mn.QueueFree(); modelDirs.Remove(e); }
            if (boardingStatus != null && ReferenceEquals(boardingStatus.Ent, e)) { boardingStatus.QueueFree(); boardingStatus = null; }
            if (e.Class == "ILogAndActions" && logHud != null) { logHud.QueueFree(); logHud = null; }
            landHud?.OnDeleted(e);
            perkView.OnDeleted(e);
            if (e.Class == "location")      // the location owns the people standing in it
                foreach (var cls in new[] { "NPCharacter", "Player", "Character" }) host.It.DeleteAllEntities(cls);
            if (e.Class.Equals("locationcamera", StringComparison.OrdinalIgnoreCase)) { camMode = 1; camInit = false; camLockPitch = null; camSleep = false; }
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
        if (id.Contains(' ') && Conv.ToInt(host.It.CallTop("GetCharacterIndex", V.Str(id))) < 0)       // ids that really contain underscores (eng_guber): retry with the literal spelling
        {
            var alt = host.It.CallTop("CharacterFromID", V.Str(id.Replace(' ', '_')));
            if (alt.Attr != null) { chr = alt; id = id.Replace(' ', '_'); }
        }
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
        if (System.Environment.GetEnvironmentVariable("SEADOGS_KEYLOG") != null && e is InputEventKey { Pressed: true, Echo: false } kl)
            GD.Print($"[key] {kl.PhysicalKeycode} UiOpen={UiOpen} dialog={dialogView != null} sea={sea.Active} locs={locations.Count} focus={GetViewport().GuiGetFocusOwner()?.Name}");
        if (e is InputEventKey { Pressed: true, Echo: false } fs && fs.PhysicalKeycode == Key.F11)       // full screen on / off
        {
            var w = GetWindow();
            w.Mode = w.Mode == Window.ModeEnum.Fullscreen ? Window.ModeEnum.Windowed : Window.ModeEnum.Fullscreen;
        }
        if (sea.Active && sea.CommandsOpen && e is InputEventKey { Pressed: true, Echo: false } cmk && locations.Count == 0 && !UiOpen && dialogView == null)
        {
            sea.CommandKey(cmk.PhysicalKeycode);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (sea.Active && locations.Count == 0 && dialogView == null && !UiOpen && e is InputEventKey { Pressed: true, Echo: false } ok && Keys.Is(ok, "SeaCommands"))
        {
            sea.OpenCommands();
            return;
        }
        // the land command menu has the keyboard while it is open (the original switches to "BattleInterfaceControls")
        if (landHud != null && landHud.MenuOpen && e is InputEventKey { Pressed: true, Echo: false } lk && locations.Count > 0 && !UiOpen && dialogView == null)
        {
            landHud.KeyPress(lk.PhysicalKeycode);
            GetViewport().SetInputAsHandled();
            return;
        }
        // the world map's own windows (event window, wait menu) take every key while open
        if (e is InputEventKey { Pressed: true, Echo: false } wk && worldMap.Active && worldMap.WindowOpen && locations.Count == 0 && !UiOpen)
        {
            worldMap.WindowKey(wk.PhysicalKeycode);
            GetViewport().SetInputAsHandled();
            return;
        }
        // the original control scheme: the engine reports named controls to the script ("Control Activation")
        if (e is InputEventKey { Pressed: true, Echo: false } ck && !UiOpen && dialogView == null)
        {
            string? ctl = ck.PhysicalKeycode == Key.Escape ? "MainMenuDouble" : ck.PhysicalKeycode == Key.Enter ? "LICommandsActivate"
                : Keys.Is(ck, "MainMenu") ? "MainMenu" : Keys.Is(ck, "Interface") ? "Interface" : Keys.Is(ck, "QuickSave") ? "Quick_Save" : Keys.Is(ck, "QuickLoad") ? "Quick_Load" : null;
            if (ctl == null && locations.Count > 0 && Keys.Is(ck, "LandPotion")) ctl = "BOAL_UsePotion";       // init_pc.c: KEY_C = quick heal (seadogs.c picks the first usable potion)
            if (ctl == "LICommandsActivate" && locations.Count > 0) landHud?.Command(ctl);       // Enter opens the land command menu
            if (ctl != null) host.It.FireEvent("Control Activation", new List<V> { V.Str(ctl) });
            if (ck.PhysicalKeycode == Key.Space && locations.Count > 0) LandAction();
        }
        if (e is InputEventKey { Pressed: true, Echo: false } sk && (Keys.Is(sk, "SeaSailUp") || Keys.Is(sk, "SeaSailDown")) && sea.Active && locations.Count == 0 && dialogView == null && !UiOpen)
            sea.SailKey(Keys.Is(sk, "SeaSailUp") ? 1 : -1);
        if (e is InputEventKey { Pressed: true, Echo: false } mk && worldMap.Active && locations.Count == 0 && dialogView == null && !UiOpen)
        {
            if (Keys.Is(mk, "SeaSailUp") || Keys.Is(mk, "SeaSailDown")) worldMap.SailKey(Keys.Is(mk, "SeaSailUp") ? 1 : -1);
            if (Keys.Is(mk, "SeaAction") || mk.PhysicalKeycode is Key.KpEnter or Key.Space) worldMap.Enter();
            else if (Keys.Is(mk, "MapWait")) worldMap.OpenWaitMenu();
        }
        if (e is InputEventKey { Pressed: true, Echo: false } sk2 && sea.Active && locations.Count == 0 && dialogView == null && !UiOpen)
        {
            string? ctl = Keys.Is(sk2, "SeaMap") ? "BI_MapEnter" : Keys.Is(sk2, "SeaBalls") ? "BI_ChargeCannonballs" : Keys.Is(sk2, "SeaGrapes") ? "BI_ChargeGrapes" : Keys.Is(sk2, "SeaKnippels") ? "BI_ChargeKnippels" : Keys.Is(sk2, "SeaBombs") ? "BI_ChargeBombs"
                : Keys.Is(sk2, "SeaCameraSwitch") ? "Sea_CameraSwitch" : null;
            if (ctl != null) host.It.FireEvent("Control Activation", new List<V> { V.Str(ctl) });
            if (Keys.Is(sk2, "SeaAction") || sk2.PhysicalKeycode is Key.KpEnter or Key.Space) SeaAction();
        }
        // Z = step back (the original ChrRecoil)
        if (e is InputEventKey { Pressed: true, Echo: false } zk && Keys.Is(zk, "LandRecoil") && dialogView == null && !UiOpen && locations.Count > 0)
            characters.Values.FirstOrDefault(c => c.IsPlayer)?.Recoil();
        // Q = pistol shot (the original ChrFire)
        if (e is InputEventKey { Pressed: true, Echo: false } qk && Keys.Is(qk, "LandFire") && dialogView == null && !UiOpen && locations.Count > 0)
            characters.Values.FirstOrDefault(c => c.IsPlayer)?.TryFire();
        // F = draw/sheathe the sword (the original used E; E talks here)
        if (e is InputEventKey { Pressed: true, Echo: false } fk && Keys.Is(fk, "LandSword") && dialogView == null && !UiOpen)
            if (characters.Values.FirstOrDefault(c => c.IsPlayer) is { } pf && pf.ToggleFightMode()) { logHud?.OnMessage("", new V[] { V.Int(45020), V.Int(0), V.Str(pf.FightMode ? "Шаблю добуто" : "Шаблю прибрано") }); }
        if (e is InputEventKey { Pressed: true, Echo: false } tabk && Keys.Is(tabk, "LandLook") && locations.Count > 0 && dialogView == null && !UiOpen && camMode == 1
            && characters.Values.FirstOrDefault(c => c.IsPlayer) is { Placed: true, FightMode: false } lp0 && lp0.IsAlive) lookMode = !lookMode;               // ChrCamCameraSwitch
        if (e is InputEventKey { Pressed: true, Echo: false } stk && Keys.Is(stk, "LandStyle") && locations.Count > 0 && dialogView == null && !UiOpen)
        {
            Keys.SetClassic(!Keys.Classic);
            logHud?.OnMessage("", new V[] { V.Int(45020), V.Int(0), V.Str(Keys.Classic ? "Керування на суші: класичне (миша повертає героя)" : "Керування на суші: сучасне (WASD відносно камери)") });
        }
        if (e is InputEventKey { Pressed: true, Echo: false } f8 && Keys.Is(f8, "BugReport")) SaveBugReport();
        if (e is InputEventKey { Pressed: true } f9 && Keys.Is(f9, "FlyCam")) flyMode = !flyMode;          // free debug camera
        if (e is InputEventKey { Pressed: true } f3 && Keys.Is(f3, "NameTags"))                              // show/hide name tags
        {
            nameTags = !nameTags;
            foreach (var c in characters.Values) c.ShowNameTag(nameTags);
        }
        // E or T = talk to the nearest character within reach
        if (e is InputEventKey { Pressed: true } tk && (Keys.Is(tk, "LandTalk") || tk.PhysicalKeycode == Key.T) && dialogView == null && !UiOpen)
        {
            var near = NearestTalkable();
            if (near != null) StartDialog(near.Ent.Root.Get("id")?.Value ?? "");
            else GD.Print("[main] nobody close enough to talk to");
        }
    }

    // ------------------------------------------------------------------ engine loop for characters and the camera
    OutdoorEnv outdoor = null!;
    WeatherFx weatherFx = null!;
    SeaView sea = null!;
    WorldMapView worldMap = null!;
    Action? animalsClear;
    WideScreenView? wideScreen;
    AnimalsView? animalsView;
    bool forceRun;
    int animalsAt = -1;                   // test hook --animals@N: what animals.c sends in fine weather (seagulls over the player, butterflies)
    int camMode = 1, camTarget = -1;      // locationcamera: 1 follow, 2 fixed position (script), 3 free
    Vector3 camToPos;
    float? camLockPitch;                  // locationcamera MSG_CAMERA_MOVE
    bool camSleep;                        // locationcamera MSG_CAMERA_SLEEP
    float camYaw, camPitch = 0.4f, camDist = 3.0f;       // LocationCamera: ax = 0.4, radius = 3
    readonly LandCamera landCam = new();
    static readonly bool CamLog = System.Environment.GetEnvironmentVariable("SEADOGS_CAMLOG") != null;
    float mouseDx, mouseDy;               // mouse motion since the last frame (the classic scheme turns the hero with it)
    float lookBlend, lookPitch;           // Tab: first-person view, blended in over a second like LocationCamera::kMorph
    bool lookMode, lookHidden;
    int lookAtFrame = -1, jumpAtFrame = -1;
    readonly List<(int frame, float x, float y, float z, float ay)> atHooks = new();
    // test hook: env SEADOGS_LANDCAM="x,y,z,tx,ty,tz" (Godot coordinates) fixes the location camera
    (Vector3 pos, Vector3 look)? landCamHook = ParseLandCam();
    static (Vector3, Vector3)? ParseLandCam()
    {
        var v = System.Environment.GetEnvironmentVariable("SEADOGS_LANDCAM")?.Split(',');
        if (v == null || v.Length != 6) return null;
        float F(int i) => float.Parse(v[i], System.Globalization.CultureInfo.InvariantCulture);
        return (new Vector3(F(0), F(1), F(2)), new Vector3(F(3), F(4), F(5)));
    }
    bool camInit, flyMode, rmbDown;
    ulong lmbDownAt;
    string portDirCached = "";
    readonly List<(int frame, string id)> brings = new();
    bool nameTags;

    /// <summary>
    /// F8: writes port/reports/&lt;time&gt;/ with a screenshot, the tail of the game log and a short state summary (where the player is, what is open, the last keys),
    /// so a problem can be described with one line and this folder.
    /// </summary>
    void SaveBugReport()
    {
        try
        {
            string dir = Path.Combine(portDirCached, "reports", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(dir);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, "screen.png"));
            var sb = new System.Text.StringBuilder();
            string G(string n) => host.It.Globals.TryGetValue(n, out var g) ? Conv.ToStr(g.Val) : "?";
            var pc = characters.Values.FirstOrDefault(c => c.IsPlayer);
            sb.AppendLine($"time: {DateTime.Now}, frames: {frames}, fps: {Engine.GetFramesPerSecond():0}");
            sb.AppendLine($"mode: sea={sea.Active} map={worldMap.Active} locations={locations.Count} dialog={dialogView != null} interfaceOpen={UiOpen}");
            sb.AppendLine($"player: location={pc?.Ent.Root.Get("location")?.Value} pos={pc?.PositionText()} fightMode={pc?.FightMode} hp={pc?.Ent.Root.Find("chr_ai.hp", false)?.Value}");
            sb.AppendLine($"action hint: {G("g_ActiveActionName")}, InterfaceStates.Launched={host.It.Globals.GetValueOrDefault("InterfaceStates").Val.Attr?.Get("Launched")?.Value}");
            sb.AppendLine($"window: {GetWindow().Size}, version: {Engine.GetVersionInfo()["string"]}");
            File.WriteAllText(Path.Combine(dir, "state.txt"), sb.ToString());
            string log = ProjectSettings.GlobalizePath("user://logs/godot.log");
            if (File.Exists(log))
            {
                using var fs = new System.IO.FileStream(log, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var lines = new Queue<string>();
                string? ln;
                while ((ln = sr.ReadLine()) != null) { lines.Enqueue(ln); if (lines.Count > 600) lines.Dequeue(); }
                File.WriteAllLines(Path.Combine(dir, "log_tail.txt"), lines);
            }
            GD.Print("[report] saved " + dir);
            logHud?.OnMessage("", new V[] { V.Int(45020), V.Int(0), V.Str("Звіт збережено: reports\\" + Path.GetFileName(dir)) });
        }
        catch (Exception e) { GD.PrintErr("[report] failed: " + e.Message); }
    }

    /// <summary>A black full-screen caption for a few seconds (game over).</summary>
    void ShowCaption(string text, float seconds)
    {
        var layer = new CanvasLayer { Layer = 95, Name = "Caption" };
        var back = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore }; back.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var lab = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        lab.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        lab.AddThemeFontSizeOverride("font_size", 34); lab.AddThemeColorOverride("font_color", new Color(0.9f, 0.82f, 0.55f));
        layer.AddChild(back); layer.AddChild(lab);
        AddChild(layer);
        GetTree().CreateTimer(seconds).Timeout += () => layer.QueueFree();
    }

    /// <summary>The character the player can talk to: the nearest living one within reach (the original engine finds it with FindDialogCharacter and offers the action "Talk").</summary>
    /// <summary>Character::FindDialogCharacter (character.cpp): within 3 m and 2 m of height, inside the 30 degree cone in front of the hero (half angle 15), the best by
    /// distance / cosine; nobody while the hero or the other one has the sword out or is dead.</summary>
    CharacterView? NearestTalkable()
    {
        var player = characters.Values.FirstOrDefault(c => c.IsPlayer);
        if (player == null || !player.Placed || player.FightMode || !player.IsAlive) return null;
        float testAng = MathF.Cos(0.5f * 30f * MathF.PI / 180f), ay = player.Heading;
        CharacterView? best = null;
        float bestD = float.MaxValue;
        foreach (var c in characters.Values)
        {
            if (c.IsPlayer || !c.Placed || !c.Visible || !c.IsAlive || c.FightMode || c.Ent.Root.Find("Dialog.Filename", false)?.Value is not { Length: > 0 }) continue;
            float dx = c.Position.X - player.Position.X, dz = player.Position.Z - c.Position.Z, dy = c.Position.Y - player.Position.Y;   // Direct3D: z is mirrored
            float d2 = dx * dx + dz * dz;
            if (MathF.Abs(dy) > 2f || d2 > 9f) continue;
            float d = MathF.Sqrt(d2), cs = d2 > 0f ? (dx * MathF.Sin(ay) + dz * MathF.Cos(ay)) / d : -1f;
            if (cs < testAng) continue;
            d /= cs;
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    void UpdateWorld(float delta)
    {
        float scale = host.It.TimeScale;
        float dt = delta * scale;
        var list = characters.Values.ToList();
        var player = list.FirstOrDefault(c => c.IsPlayer);

        if (frames == lookAtFrame) lookMode = !lookMode;          // test hook --look@N
        foreach (var h in atHooks) if (h.frame == frames && player != null) { player.TeleportTo(h.x, h.y, h.z, h.ay); camInit = false; }
        if (frames == jumpAtFrame && player != null) GD.Print($"[jump] requested: {(player.Jump() ? "started" : "no jump patch / no landing here")}");
        // player input: only when no dialog/interface has the keyboard
        bool free = dialogView == null && !UiOpen && !flyMode && landHud?.MenuOpen != true;
        if (player != null)
        {
            var mv = Vector2.Zero;
            bool classic = Keys.Classic;
            float turnAdd = 0f, pitchSpeed = 0f;
            if (free)
            {
                if (classic)
                {
                    // CONTROLS/init_pc.c: right mouse button = ChrForward, middle = ChrBackward, A/D = ChrTurnHL/HR (+-12 per frame), the mouse = ChrTurnH / ChrCamTurnV;
                    // W/S walk too. Shift (ChrRun) makes the hero walk: Player::IsRunMode returns "not pressed".
                    if (Keys.Down("LandForward") || Input.IsMouseButtonPressed(MouseButton.Right)) mv.Y += 1;
                    if (Keys.Down("LandBack") || Input.IsMouseButtonPressed(MouseButton.Middle)) mv.Y -= 1;
                    if (Keys.Down("LandRight")) turnAdd += 12f * 0.01f;
                    if (Keys.Down("LandLeft")) turnAdd -= 12f * 0.01f;
                    if (mouseCaptured) { turnAdd += mouseDx * RealSens("mouse.x_sens") * 0.01f; pitchSpeed = -mouseDy * RealSens("mouse.y_sens") * 0.004f * (InvertCameras() ? -1f : 1f) / Math.Max(delta, 0.001f); }
                }
                else
                {
                    if (Keys.Down("LandForward")) mv.Y += 1;
                    if (Keys.Down("LandBack")) mv.Y -= 1;
                    if (Keys.Down("LandRight")) mv.X += 1;
                    if (Keys.Down("LandLeft")) mv.X -= 1;
                    if (Keys.Down("LandCamLeft")) camYaw += 1.8f * delta;
                    if (Keys.Down("LandCamRight")) camYaw -= 1.8f * delta;
                }
                if (Keys.Down("LandCamUp")) pitchSpeed += 0.9f;
                if (Keys.Down("LandCamDown")) pitchSpeed -= 0.9f;
            }
            mouseDx = mouseDy = 0f;
            if (camLockPitch == null && !lookMode) camPitch = landCam.Pitch(camPitch, pitchSpeed, delta);
            else if (lookMode) lookPitch = Math.Clamp(lookPitch - pitchSpeed * delta * 0.7f, -0.8f, 0.8f);
            foreach (var (f0, f1, dir) in moves) if (frames >= f0 && frames <= f1 && dialogView == null) { mv = dir; if (classic) { mv = new Vector2(0, dir.Y); turnAdd += dir.X * 0.12f; } }     // test hook
            player.Input = mv;
            player.Classic = classic;
            player.TurnAdd += turnAdd;
            player.Blocking = free && player.FightMode && Keys.Down("LandBlock");
            player.Run = forceRun || (classic ? !Keys.Down("LandRun") : Keys.Down("LandRun"));
            player.CameraYaw = camYaw;
        }

        if (dt > 0f) GroupsState.Tick(host.It, dt);
        foreach (var c in list) { c.SetTimeScale(scale); c.ShowNameTag(nameTags); c.Tick(dt); }
        CharacterView.PostMove(list, dt);

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

        // the sea HUD and its command menu belong to the open sea only: on a deck or in a cabin (a location while the sea is still alive) they would hang there frozen
        sea.SuspendHud(locations.Count > 0 || worldMap.Active || UiOpen || dialogView != null);
        sea.HideWorld(locations.Count > 0);

        // sea mode: the ship camera replaces the location camera
        if (sea.Active && locations.Count == 0)
        {
            sea.TestTurn = 0f;
            foreach (var (f0, f1, dir) in moves) if (frames >= f0 && frames <= f1) sea.TestTurn = dir.X;
            sea.Tick(dt, dialogView == null && !UiOpen);
            LocationLights.SyncWeather(host.It, false);
            outdoor.Sync(host.It, sea.Cam, delta);
            weatherFx.Sync(host.It, sea.Cam, delta);
            return;
        }

        // follow camera
        var cam = CurrentLocation()?.Cam;
        LocationLights.SyncWeather(host.It, CurrentLocation()?.Indoor ?? false);
        if (cam != null) LocationLights.Tick((float)delta, cam.GlobalPosition);
        outdoor.Sync(host.It, cam, delta);
        weatherFx.Sync(host.It, cam, delta);
        if (cam == null) return;
        cam.Enabled = flyMode;
        if (landCamHook is { } lc && !flyMode) { cam.GlobalPosition = lc.pos; cam.LookAt(lc.look); return; }      // test hook SEADOGS_LANDCAM
        if (System.Environment.GetEnvironmentVariable("SEADOGS_LANDCAM") is "animal" or "rat" or "eagle" && LocAnimals.DebugFocus(System.Environment.GetEnvironmentVariable("SEADOGS_LANDCAM")!) is { } fp && !flyMode)
        { cam.GlobalPosition = fp + new Vector3(1.2f, 0.9f, 1.8f) * (fp.Y > 10 ? 6f : 1f); cam.LookAt(fp); return; }
        if (camSleep && !flyMode) return;
        if (camLockPitch is { } lp) camPitch = Math.Clamp(lp, 0.05f, 1.3f);
        if (camMode == 2 && !flyMode)
        {
            // script-placed camera ("locCameraToPos"): fixed position, looking at the camera target (the main character)
            var tc = list.FirstOrDefault(c => c.Ent.Root.Get("index")?.Value == camTarget.ToString()) ?? player;
            cam.GlobalPosition = camToPos;
            if (tc != null && tc.Placed) cam.LookAt(tc.Position + new Vector3(0, 1.2f, 0));
            return;
        }
        if (player == null || !player.Placed || flyMode) return;
        if (!camInit) { camYaw = -player.Heading; camInit = true; landCam.Born = true; }
        bool canLook = !player.FightMode && player.IsAlive && dialogView == null;
        if (lookMode && !canLook) lookMode = false;
        lookBlend = Math.Clamp(lookBlend + (lookMode ? 1f : -1f) * delta, 0f, 1f);       // kMorph: a second to blend between the views
        bool hide = lookBlend >= 1f;
        if (hide != lookHidden) { lookHidden = hide; player.LookHidden = hide; }
        // the classic scheme: the camera turns after the hero's heading with the inertia rotInertia (CameraFollow::MoveCamera)
        if (Keys.Classic && camMode == 1 && !lookMode)
        {
            float dAng = -player.Heading - camYaw;
            while (dAng > MathF.PI) dAng -= 2f * MathF.PI;
            while (dAng < -MathF.PI) dAng += 2f * MathF.PI;
            if (MathF.Abs(dAng) > 0.001f) camYaw += dAng * Math.Min(delta * LandCamera.RotInertia, 1f);
        }
        else if (lookMode) camYaw = -player.Heading;
        const float charHeight = CharacterView.CharHeight;
        var target = player.Position + new Vector3(0, charHeight * LandCamera.LookHeight, 0);
        landCam.Place(cam, target, player.Position.Y, charHeight, camYaw, camPitch, camDist, delta);
        if (CamLog && frames % 50 == 0) GD.Print($"[camera] frame {frames}: radius {landCam.Radius:0.00}/{camDist:0.0} yaw {camYaw:0.00} pitch {camPitch:0.00} heading {player.Heading:0.00} pos {cam.GlobalPosition} look={lookBlend:0.0} classic={Keys.Classic}");
        if (lookBlend > 0f)
        {
            // first person (ExecuteLook): the eyes, looking along the heading, tilted by the vertical angle
            float h = player.Heading;
            var eye = player.Position + new Vector3(0, charHeight, 0) + new Vector3(MathF.Sin(h), 0, -MathF.Cos(h)) * 0.1f;
            var dir = new Vector3(MathF.Sin(h) * MathF.Cos(lookPitch), -MathF.Sin(lookPitch), -MathF.Cos(h) * MathF.Cos(lookPitch));
            float k = lookBlend * lookBlend * (3f - 2f * lookBlend);
            cam.GlobalPosition = cam.GlobalPosition.Lerp(eye, k);
            cam.LookAt(target.Lerp(eye + dir, k));
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseMotion cm && mouseCaptured && Keys.Classic && locations.Count > 0 && !flyMode) { mouseDx += cm.Relative.X; mouseDy += cm.Relative.Y; return; }        // ChrTurnH / ChrCamTurnV
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } && sea.Active && locations.Count == 0 && dialogView == null && !UiOpen) { sea.FirePlayer(); return; }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } && dialogView == null && !UiOpen)
        {
            // attack with the sword when it is drawn
            var pl = characters.Values.FirstOrDefault(c => c.IsPlayer);
            if (pl != null && pl.FightMode && pl.Placed)
            {
                // a quick click strikes normally; sideways keys give a fast side cut; holding the button winds up a strong blow (released below)
                if (Keys.Down("LandLeft") || Keys.Down("LandRight")) { var en = pl.NearestInFront(2.2f); if (en != null) pl.StartSwing(en, CharacterView.Strike.Fast); }
                else lmbDownAt = Time.GetTicksMsec();
            }
            else if (pl != null && locations.Count > 0) LandAction();          // the original: the left mouse button is ChrAction
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } && lmbDownAt > 0)
        {
            var pl2 = characters.Values.FirstOrDefault(c => c.IsPlayer);
            ulong held = Time.GetTicksMsec() - lmbDownAt; lmbDownAt = 0;
            if (pl2 != null && pl2.FightMode && pl2.Placed && dialogView == null && !UiOpen) { var en = pl2.NearestInFront(2.2f); if (en != null) pl2.StartSwing(en, held > 450 ? CharacterView.Strike.Strong : CharacterView.Strike.Normal); }
        }
        // the deck camera: the right button walks forward, the middle one back (DeckCamera_Forward / _Backward), the mouse looks round without a button
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right or MouseButton.Middle } db && sea.DeckCamera)
        {
            sea.DeckWalkMouse = db.Pressed && locations.Count == 0 && !UiOpen && dialogView == null ? (db.ButtonIndex == MouseButton.Right ? 1 : -1) : 0;
            if (db.ButtonIndex == MouseButton.Right) rmbDown = false;
            return;
        }
        if (e is InputEventMouseMotion dm && sea.DeckCamera && locations.Count == 0 && !UiOpen && dialogView == null && !sea.CommandsOpen && !flyMode)
        {
            float sx0 = RealSens("mouse.x_sens"), sy0 = RealSens("mouse.y_sens");
            bool inv0 = host.It.Globals.TryGetValue("InterfaceStates", out var ist0) && ist0.Val.Attr?.Get("InvertCameras")?.Value == "1";
            sea.Look(dm.Relative.X * sx0, dm.Relative.Y * sy0 * (inv0 ? -1f : 1f));
            return;
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
            else if (lookMode) { if (characters.Values.FirstOrDefault(c => c.IsPlayer) is { } lp1) lp1.TurnAdd += dx * 0.005f; lookPitch = Math.Clamp(lookPitch + dy * 0.004f, -0.8f, 0.8f); }
            else { camYaw -= dx * 0.005f; if (camLockPitch == null) camPitch = Math.Clamp(camPitch + dy * 0.004f, LandCamera.PitchMin, LandCamera.PitchMax); }
        }
    }

    bool InvertCameras() => host.It.Globals.TryGetValue("InterfaceStates", out var ist) && ist.Val.Attr?.Get("InvertCameras")?.Value == "1";

    float RealSens(string path)
    {
        float f = 0.5f;
        if (host.It.Globals.TryGetValue("InterfaceStates", out var g) && g.Val.Attr?.Find(path, false)?.Value is { Length: > 0 } v) f = Conv.ParseFloatPrefix(v);
        return f <= 0.5f ? 0.5f + f : f * 2f;          // SetRealMouseSensitivity() of CONTROLS\controls.c
    }

    /// <summary>DX9RENDER::SetCamera: every frame the renderer tells the script where the camera is (position and angles in Direct3D space); the lightning of the storms uses it.</summary>
    void RaiseCameraPosAng()
    {
        if (GetViewport().GetCamera3D() is not { } c) return;
        var p = c.GlobalPosition;
        var f = -c.GlobalTransform.Basis.Z;                      // Godot -> Direct3D: z is mirrored
        var n = new Vector3(f.X, f.Y, -f.Z);
        float ay = MathF.Atan2(n.X, n.Z), ax = MathF.Atan2(-n.Y, MathF.Sqrt(n.X * n.X + n.Z * n.Z));
        host.It.FireEvent("CameraPosAng", new List<V> { V.Float(p.X), V.Float(p.Y), V.Float(-p.Z), V.Float(ax), V.Float(ay), V.Float(0f) });
    }

    LocationView? CurrentLocation() { foreach (var l in locations.Values) return l; return null; }

    /// <summary>
    /// Space on land is the original control "BIFastCommand": it carries out the action the log interface offers (g_ActiveActionName): Reload (door), Pick (item on the ground),
    /// OpenBox (chest), Action (use an item at a place), Talk ... The script's BI_FastCommand handler does the work.
    /// </summary>
    /// <summary>MSG_MODEL_BLEND of a free-standing model: fades every mesh between two opacities; fully faded out it is hidden.</summary>
    void FadeModel(Node3D holder, float from, float to, float seconds)
    {
        holder.Visible = true;
        modelFades[holder] = (from, to, 0f, seconds);
        SetModelAlpha(holder, from);
    }

    static void SetModelAlpha(Node n, float alpha)
    {
        if (n is GeometryInstance3D g) g.Transparency = 1f - Math.Clamp(alpha, 0f, 1f);
        foreach (var c in n.GetChildren()) SetModelAlpha(c, alpha);
    }

    /// <summary>Advances the MSG_MODEL_BLEND fades with the game's delta (scripted runs use a fixed step).</summary>
    void TickModelFades(float dt)
    {
        foreach (var (node, f) in modelFades.ToArray())
        {
            if (!IsInstanceValid(node)) { modelFades.Remove(node); continue; }
            float t = Math.Min(f.t + dt, f.dur);
            SetModelAlpha(node, f.from + (f.to - f.from) * (t / f.dur));
            if (t >= f.dur) { modelFades.Remove(node); if (f.to <= 0.01f) node.Visible = false; }
            else modelFades[node] = (f.from, f.to, t, f.dur);
        }
    }

    void LandAction()
    {
        string act = host.It.Globals.TryGetValue("g_ActiveActionName", out var g) ? Conv.ToStr(g.Val) : "";
        GD.Print("[land] action: " + (act.Length == 0 ? "Nothing" : act));
        if (act.Length == 0 || act == "Nothing" || act == "Closed")
        {
            host.It.FireEvent("Control Activation", new List<V> { V.Str("ChrAction") });       // a closed door answers with the knock
            return;
        }
        host.It.FireEvent("BI_FastCommand", new List<V> { V.Str(act) });
    }

    /// <summary>Executes the sea action the HUD offers (Moor / Board / LandTroops / Map ...): the engine's battle interface raised "BI_FastCommand" with its name.</summary>
    void SeaAction()
    {
        if (worldMap.Active)
        {
            if (worldMap.Inside.Length == 0 && frames < actRetryUntil) { seaActions.Add(frames + 10); return; }       // test hook: the island zone is not entered yet
            worldMap.Enter(); return;
        }
        string act = host.It.Globals.TryGetValue("g_ActiveActionName", out var g) ? Conv.ToStr(g.Val) : "";
        GD.Print("[sea] action: " + act);
        if (act.Length > 0 && act != "Nothing") host.It.FireEvent("BI_FastCommand", new List<V> { V.Str(act) });
        else if (frames < actRetryUntil) seaActions.Add(frames + 20);          // test hook: the offer is not there yet (zone not entered), ask again
    }

    /// <summary>
    /// Test hooks --seapos / --duel / --fire wait for the state they need (after a world map exit the sea loads at a varying frame): the frame is the earliest one,
    /// the hook runs once the player's ship is at sea, a duel only after the teleports planned before it, a volley only after the duels planned before it.
    /// </summary>
    void SeaTestHooks()
    {
        if (seaTeleports.Count == 0 && seaDuels.Count == 0 && seaFires.Count == 0) return;
        bool atSea = sea.HasPlayerShip && !worldMap.Active && locations.Count == 0;
        foreach (var m in seaTeleports.ToArray())
            if (frames >= m.frame && atSea) { sea.Teleport(m.x, m.z, m.ay); worldMap.Teleport(m.x, m.z, m.ay); seaTeleports.Remove(m); }
        foreach (var f in seaDuels.ToArray())
            if (frames >= f && atSea && !seaTeleports.Any(t => t.frame <= f) && (sea.Duel(frames < f + 600) || frames >= f + 600)) seaDuels.Remove(f);
        foreach (var f in seaFires.ToArray())
            if (frames >= f && atSea && !seaTeleports.Any(t => t.frame <= f) && !seaDuels.Any(d => d <= f)) { sea.FireAtNearest(); seaFires.Remove(f); }
    }

    /// <summary>An interface screen is on display (the xinterface entity also exists, empty, while the sea HUD talks to it).</summary>
    bool UiOpen => view != null && view.GetChildCount() > 0;
    bool mouseCaptured;
    readonly ActivePerkView perkView = new() { Name = "ActivePerkShower" };

    void NewView(Entity e)
    {
        view?.QueueFree();
        view = new XView(host.It, e) { Name = "XInterface" };
        AddChild(view);
    }

    public override void _Process(double delta)
    {
        // scripted test runs (--shot) advance a fixed 1/100 s per frame (the speed the hooks were written at): the frame-numbered hooks then mean the same game time whatever the machine's frame rate is
        if (shotPath != null && System.Environment.GetEnvironmentVariable("SEADOGS_REALDT") == null) delta = 1.0 / 100.0;
        if (modelFades.Count > 0) TickModelFades((float)delta);
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
        if (landHud != null) landHud.Suppressed = dialogView != null || UiOpen || locations.Count == 0;
        if (logHud != null) logHud.ActionSuppressed = landHud?.MenuOpen == true;
        perkView.Visible = dialogView == null && !UiOpen && !worldMap.Active;
        // the deck camera hides the cursor and turns with the mouse (not in scripted test runs, not while a menu or a screen needs the pointer)
        bool wantCapture = sea.DeckCamera && locations.Count == 0 && !UiOpen && dialogView == null && !sea.CommandsOpen && !worldMap.Active && shotPath == null;
        // the classic land scheme turns the hero with the mouse, which therefore stays hidden in the window while the game is running
        if (Keys.Classic && locations.Count > 0 && !UiOpen && dialogView == null && landHud?.MenuOpen != true && !flyMode && camMode == 1 && !worldMap.Active && shotPath == null) wantCapture = true;
        if (wantCapture != mouseCaptured) { mouseCaptured = wantCapture; Input.MouseMode = wantCapture ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateWorld((float)delta);
        RaiseCameraPosAng();
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        host.It.Advance((int)(delta * 1000));
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        // keep a small copy of the last frame without an interface on top: it becomes the thumbnail of the next save
        if (!UiOpen && dialogView == null && frames % 45 == 0 && System.Environment.GetEnvironmentVariable("SEADOGS_NOGRAB") == null) lastGameFrame = GrabFrame();
        view?.FillSaveStrings();
        host.It.FireEvent("frame");
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (prof)
        {
            profWorld += t1 - t0; profAdv += t2 - t1; profFrame += t3 - t2;
            { double pm = delta * 1000, oc = Performance.GetMonitor(Performance.Monitor.ObjectCount);
              if (frames > 100 && pm > 25 || Math.Abs(oc - profObj) > 3000) GD.Print($"[prof] frame {frames}: frame time {pm:0.0} ms, objects {profObj:0} -> {oc:0}, script part {(t3 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms, gc0 {GC.CollectionCount(0)} gc2 {GC.CollectionCount(2)}");
              profObj = oc; }
            if ((frames + 1) % 120 == 0)
            {
                double k = 1000.0 / System.Diagnostics.Stopwatch.Frequency / 120.0;
                GD.Print($"[prof] per frame ms: world {profWorld * k:0.0}, advance {profAdv * k:0.0}, frameEvent {profFrame * k:0.0}, fps {Engine.GetFramesPerSecond():0}, process {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:0.0} ms, draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0}, primitives {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame):0}, objects {Performance.GetMonitor(Performance.Monitor.ObjectCount):0}");
                if (sea.Active) GD.Print("[prof] sea ms: " + sea.ProfReport(120));
                profWorld = profAdv = profFrame = 0;
            }
        }
        frames++;

        foreach (var (f, id0) in showHits) if (f == frames) { var ip = id0.Split(':'); float dmg = ip.Length > 1 ? float.Parse(ip[1], System.Globalization.CultureInfo.InvariantCulture) : 4f; if (characters.Values.FirstOrDefault(c => !c.IsPlayer && (c.Ent.Root.Get("id")?.Value ?? "") == ip[0]) is { } hn) { hn.Ent.Root.GetOrCreate("chr_ai.hp").Value = Math.Max(0f, hn.HpNow - dmg).ToString(System.Globalization.CultureInfo.InvariantCulture); hn.ShowHit(dmg, dmg <= 0f); } }
        foreach (var (f, id0) in brings) if (f == frames) { var ip = id0.Split(':'); string id = ip[0]; float bdist = ip.Length > 1 ? float.Parse(ip[1], System.Globalization.CultureInfo.InvariantCulture) : 1.6f; if (characters.Values.FirstOrDefault(c => c.IsPlayer) is { } bp && characters.Values.FirstOrDefault(c => !c.IsPlayer && (c.Ent.Root.Get("id")?.Value ?? "") == id) is { } bn) bn.PlaceNear(bp, bdist); }       // --bring=Id@N or Id:metres@N
        foreach (var (f, n) in clicks) if (f == frames) view?.Click(n);
        foreach (var (f, n) in mouseClicks) if (f == frames) view?.MouseClick(n);
        foreach (var (f, k, v) in sets) if (f == frames) host.It.SetGlobal(k, v);
        foreach (var (f, fn) in calls)
            if (f == frames)
            {
                // "Func" or "Func(arg,arg)": integers, strings (+ = space) and #CharacterId
                if (fn.StartsWith("seg:")) { host.LoadSegment(fn.Substring(4).Replace('/', '\\').Replace('+', ' ')); continue; }    // test hook: load a script segment (dialog file)
                int lp = fn.IndexOf('(');
                var args = new List<V>();
                string name = lp < 0 ? fn : fn.Substring(0, lp);
                if (lp >= 0)
                    foreach (var x in fn.Substring(lp + 1).TrimEnd(')').Split(',', StringSplitOptions.RemoveEmptyEntries))
                        args.Add(x.StartsWith('#') ? host.It.CallTop("CharacterFromID", V.Str(x.Substring(1).Replace('+', ' '))) : int.TryParse(x, out int iv) ? V.Int(iv) : V.Str(x.Replace('+', ' ')));
                var callResult = host.It.CallTop(name, args.ToArray());
                if (System.Environment.GetEnvironmentVariable("SEADOGS_CALLLOG") == "1") GD.Print($"[call] {name}({string.Join(",", args.Select(a => a.ToString()))}) -> {callResult}");     // test hook: print what the script function returned
            }
        foreach (var (f, g) in killGroups)
            if (f == frames)
                foreach (var c in characters.Values.ToList())
                    if (!c.IsPlayer && c.IsAlive && c.Ent.Root.Find("chr_ai.group", false)?.Value == g) host.It.CallTop("LAi_KillCharacter", V.A(c.Ent.Root));
        if (controlAt == frames) host.It.CallTop("LAi_SetPlayerType", host.It.CallTop("GetMainCharacter"));    // test hook: end the scripted intro
        if (fuzzList.Contains(frames)) { fuzzAt = frames; fuzzQueue = null; }       // several --fuzz@N: every one restarts the round in the current location
        if (fuzzAt >= 0 && frames >= fuzzAt) FuzzStep();
        foreach (var (f, p) in dumps) if (f == frames) host.It.DumpPath(p);
        foreach (var (f, n) in ctls) if (f == frames) { if (landHud?.Command(n) != true) host.It.FireEvent("Control Activation", new List<V> { V.Str(n) }); }
        foreach (var (f, dir) in sails) if (f == frames) sea.SailKey(dir);
        foreach (var (f, what) in rigTests) if (f == frames) sea.RigTest(what);
        foreach (var f in seaActions.ToArray()) if (f == frames) SeaAction();
        if (swings.Contains(frames) && characters.Values.FirstOrDefault(c => c.IsPlayer) is { } swp && swp.NearestInFront(2.2f) is { } swe) { if (!swp.FightMode) swp.ToggleFightMode(); GD.Print($"[melee] frame {frames}: attack pressed"); swp.StartSwing(swe, CharacterView.Strike.Normal); }
        if (animalsAt == frames && animalsView != null)
        {
            var pp = characters.Values.FirstOrDefault(c => c.IsPlayer)?.Position ?? Vector3.Zero;
            animalsView.OnMessage(new[] { V.Int(77100) });
            animalsView.OnMessage(new[] { V.Int(77103), V.Float(pp.X), V.Float(pp.Y), V.Float(-pp.Z) });
            animalsView.OnMessage(new[] { V.Int(77108) });
            if (CurrentLocation() is { } cl)        // what locations_loader.c does by chance: an eagle over the jungle, rats
            {
                cl.OnMessage("ls", new[] { V.Int(30155), V.Str("AddEagle") });
                cl.OnMessage("lsl", new[] { V.Int(30155), V.Str("AddRats"), V.Int(6) });
            }
        }
        SeaTestHooks();
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
        foreach (var (f, s) in typed) if (f == frames) TypeText(s);
        foreach (var (f, k, sh) in keyPresses) if (f == frames) { foreach (var down in new[] { true, false }) Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = down, ShiftPressed = sh }); }
        foreach (var m in mapTeleports.ToArray()) if (frames >= m.frame && frames % 10 == 0 && worldMap.IsBuilt) { worldMap.Teleport(m.x, m.z, m.ay); mapTeleports.Remove(m); }
        foreach (var f in mapMeets.ToArray()) if (frames >= f && frames % 10 == 0 && worldMap.IsBuilt && worldMap.Active && worldMap.TestMeet()) mapMeets.Remove(f);
        foreach (var m in mapStorms.ToArray()) if (frames >= m.frame && frames % 10 == 0 && worldMap.IsBuilt && worldMap.Active && worldMap.TestStorm(m.dx, m.dz)) mapStorms.Remove(m);
        foreach (var (f, g) in gotos) if (f == frames) { var pl = characters.Values.FirstOrDefault(c => c.IsPlayer); var gp = g.Split('.'); pl?.TeleportToLocator(gp[0], gp[1]); }
        foreach (var (f, id) in dialogTests) if (f == frames) StartDialog(id);
        foreach (var (f, a) in answers) if (f == frames) dialogView?.Choose(a - 1);
        if (shotPath != null && frames == shotAtFrame)
        {
            GetViewport().GetTexture().GetImage().SavePng(shotPath);
            GD.Print("[main] screenshot saved: " + shotPath + $" (fps {Engine.GetFramesPerSecond():0})");
            foreach (var kv in host.It.MessageStats.Where(k => !host.It.EntityHandlers.ContainsKey(k.Key.Split('|')[0].Trim())).OrderByDescending(k => k.Value).Take(40)) GD.Print($"[nohandler] {kv.Value}x {kv.Key}");
            GD.Print($"[main] TimeScale={host.It.TimeScale}, locations={locations.Count}, entities alive: " + string.Join(", ", host.It.Entities.Where(en => !en.Deleted).GroupBy(en => en.Class).Select(g => g.Key + "x" + g.Count())));
            foreach (var ch in GetChildren()) GD.Print($"[tree] {ch.Name} {ch.GetType().Name} children={ch.GetChildCount()} queued={ch.IsQueuedForDeletion()}");
            foreach (var c in characters.Values) GD.Print("[chars] " + c.DebugState());
            GD.Print("[fight] " + CharacterView.FightStats() + $" player fightMode={characters.Values.FirstOrDefault(c => c.IsPlayer)?.FightMode} colThreshold={CharacterView.StatColThreshold} busyPos={CharacterView.StatBusyPos} escapeSlide={CharacterView.StatEscapeSlide}");
            if (landHud != null) GD.Print("[landhud] " + landHud.DebugState());
            foreach (var m in modelNodes.Values)
                foreach (var part in m.GetChildren().OfType<Node3D>()) GD.Print($"[models] {part.Name} at {m.GlobalPosition} visible={m.Visible} size={(m.Basis * ModelFactory.Bounds(part).Size).Abs()}"
                        + (GetViewport().GetCamera3D() is { } cam && !cam.IsPositionBehind(m.GlobalPosition) ? $" screen={cam.UnprojectPosition(m.GlobalPosition)}" : " off-screen"));
            foreach (var kv in host.It.RuntimeErrors.OrderByDescending(k => k.Value).Take(25)) GD.Print($"[errors] {kv.Value}x {kv.Key}");
            foreach (var kv in host.It.EventsFired.OrderByDescending(k => k.Value).Take(40)) GD.Print($"[events] {kv.Value}x {kv.Key} handlers={(host.It.Handlers.TryGetValue(kv.Key, out var hl) ? string.Join(",", hl.Select(h => h.fn)) : "-")}");
            foreach (var kv in host.It.UnresolvedFuncs.OrderByDescending(k => k.Value).Take(25)) GD.Print($"[unresolved] {kv.Value}x {kv.Key}");
            GetTree().Quit();
        }
        if (host.It.Exit) GetTree().Quit();
    }
}
