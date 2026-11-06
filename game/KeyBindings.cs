using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace SeaDogs;

/// <summary>
/// The actions the game window carries out itself and the script control each one stands for. The original game's control system is script-driven (CONTROLS/init_pc.c,
/// remapped on the options pages, saved with the options): <see cref="Get"/> asks the control table for the key the player has bound, so the original pages (Options ->
/// Controls) change these keys. Actions the original does not have (modern WASD walking, camera on the arrow keys, ...) are controls named "Port_*" that the port adds to
/// the same table (<see cref="Ctl.EnsurePortControls"/>), so they are listed and remapped there too. Only the two debug actions without a control stay in settings.ini [keys].
/// </summary>
public static class Keys
{
    /// <summary>Ctl = the control in the script's table (null: a settings.ini key), MapCtl = the control to use on the world map instead, Group = key group(s) of a Port_ control.</summary>
    public sealed record Act(string Id, string Title, Key Default, string? Ctl, string? MapCtl = null, string Group = "");

    public static readonly Act[] All =
    {
        // land (CONTROLS/init_pc.c: PrimaryLand, FightModeControls)
        new("LandForward", "Рух вперед (сучасне керування)", Key.W, "Port_LandForward", null, "PrimaryLand"),
        new("LandBack", "Рух назад (сучасне керування)", Key.S, "Port_LandBack", null, "PrimaryLand"),
        new("LandLeft", "Крок вліво / поворот ліворуч", Key.A, "ChrTurnHL"), new("LandRight", "Крок вправо / поворот праворуч", Key.D, "ChrTurnHR"),
        new("LandRun", "Біг", Key.Shift, "ChrRun"), new("LandTalk", "Розмова, дія (сучасне керування)", Key.F, "Port_LandTalk", null, "PrimaryLand"),
        new("LandSword", "Дістати / сховати шаблю", Key.E, "ChrFightMode"), new("LandBlock", "Блок", Key.Ctrl, "ChrBlock"),
        new("LandFire", "Постріл з пістоля", Key.Q, "ChrFire"), new("LandRecoil", "Відскок назад", Key.Z, "ChrRecoil"), new("LandPotion", "Випити зілля", Key.C, "BOAL_UsePotion"),
        new("LandCommands", "Командне меню на суші", Key.Enter, "LICommandsActivate"),
        new("LandCamLeft", "Камера вліво", Key.Left, "Port_LandCamLeft", null, "PrimaryLand"), new("LandCamRight", "Камера вправо", Key.Right, "Port_LandCamRight", null, "PrimaryLand"),
        new("LandCamUp", "Камера вгору", Key.Up, "Port_LandCamUp", null, "PrimaryLand"), new("LandCamDown", "Камера вниз", Key.Down, "Port_LandCamDown", null, "PrimaryLand"),
        new("LandLook", "Погляд з очей / камера ззаду", Key.Tab, "ChrCamCameraSwitch"),
        new("LandStyle", "Керування на суші: сучасне / класичне", Key.F10, "Port_LandStyle", null, "PrimaryLand"),
        new("NameTags", "Імена над персонажами", Key.F3, "Port_NameTags", null, "PrimaryLand"),

        // sea (Sailing3Pers, Sailing1Pers) and world map (WorldMapControls)
        new("SeaLeft", "Поворот ліворуч", Key.A, "Ship_TurnLeft", "WMapShipTurnLeft"), new("SeaRight", "Поворот праворуч", Key.D, "Ship_TurnRight", "WMapShipTurnRight"),
        new("SeaSailUp", "Підняти вітрила", Key.W, "Ship_SailUp", "WMapShipSailUp"), new("SeaSailDown", "Опустити вітрила", Key.S, "Ship_SailDown", "WMapShipSailDown"),
        new("SeaAction", "Швидка дія (суша, море; на карті - вихід)", Key.Space, "BIFastCommand", "WMapCancel"),
        new("SeaCommands", "Командне меню (накази ескадрі)", Key.Enter, "BICommandsActivate"),
        new("SeaMap", "Карта світу", Key.M, "BI_MapEnter"), new("SeaTelescope", "Підзорна труба", Key.Ctrl, "TelescopeIn"),
        new("SeaBalls", "Ядра", Key.Key1, "BI_ChargeCannonballs"), new("SeaGrapes", "Картеч", Key.Key2, "BI_ChargeGrapes"),
        new("SeaKnippels", "Книпелі", Key.Key3, "BI_ChargeKnippels"), new("SeaBombs", "Бомби", Key.Key4, "BI_ChargeBombs"),
        new("SeaCamLeft", "Камера вліво", Key.Left, "Port_SeaCamLeft", null, "Sailing3Pers,Sailing1Pers,WorldMapControls"),
        new("SeaCamRight", "Камера вправо", Key.Right, "Port_SeaCamRight", null, "Sailing3Pers,Sailing1Pers,WorldMapControls"),
        new("SeaCamUp", "Камера вгору", Key.Up, "Port_SeaCamUp", null, "Sailing3Pers,Sailing1Pers,WorldMapControls"),
        new("SeaCamDown", "Камера вниз", Key.Down, "Port_SeaCamDown", null, "Sailing3Pers,Sailing1Pers,WorldMapControls"),
        new("SeaZoomIn", "Наблизити камеру", Key.Pageup, "Port_SeaZoomIn", null, "Sailing3Pers,Sailing1Pers,WorldMapControls"),
        new("SeaZoomOut", "Віддалити камеру", Key.Pagedown, "Port_SeaZoomOut", null, "Sailing3Pers,Sailing1Pers,WorldMapControls"),
        new("SeaCameraSwitch", "Камера з палуби / камера корабля", Key.Tab, "Sea_CameraSwitch", "WMapCameraSwitch"),
        new("DeckForward", "Палуба: крок уперед", Key.Up, "Port_DeckForward", null, "Sailing1Pers"), new("DeckBack", "Палуба: крок назад", Key.Down, "Port_DeckBack", null, "Sailing1Pers"),
        new("MapWait", "Карта: очікування (пропуск часу)", Key.B, "Port_MapWait", null, "WorldMapControls"),

        // general
        new("QuickSave", "Швидке збереження", Key.F4, "Quick_Save"), new("QuickLoad", "Швидке завантаження", Key.F6, "Quick_Load"),
        new("Interface", "Екран персонажа / інтерфейс", Key.F2, "Interface"), new("MainMenu", "Головне меню", Key.F1, "MainMenu"),
        new("MainEsc", "Головне меню (Esc)", Key.Escape, "MainMenuDouble"),
        new("FlyCam", "Вільна камера (налагодження)", Key.F9, null), new("BugReport", "Звіт про помилку (знімок + лог + стан)", Key.F8, null),
    };

    static readonly Dictionary<string, Act> byId = All.ToDictionary(a => a.Id);
    static readonly Dictionary<string, Key> own = All.Where(a => a.Ctl == null).ToDictionary(a => a.Id, a => a.Default);
    static string path = "";

    /// <summary>The world map is shown: the actions use the WMap* controls.</summary>
    public static bool InWorldMap;

    /// <summary>
    /// Land control scheme. false = this port's: WASD walks relative to the camera, the right mouse button / arrows turn the camera. true = the original
    /// (CONTROLS/init_pc.c): the mouse turns the hero and the camera height, A/D turn, W/S (or the right/middle mouse button) walk forward/back, the hero runs
    /// unless Shift is held, and the camera follows the hero's heading with inertia.
    /// </summary>
    public static bool Classic;

    /// <summary>The control standing for an action (the world map's own one while the map is shown).</summary>
    public static string? ControlOf(string id) => byId.TryGetValue(id, out var a) ? (InWorldMap && a.MapCtl != null ? a.MapCtl : a.Ctl) : null;

    /// <summary>The keyboard key bound to an action now (Key.None: it is bound to a mouse button or to nothing).</summary>
    public static Key Get(string id)
    {
        if (!byId.TryGetValue(id, out var a)) return Key.None;
        if (a.Ctl == null) return own.TryGetValue(id, out var k) ? k : Key.None;
        return Ctl.KeyOf(ControlOf(id)!, a.Default);
    }

    public static bool Down(string id)
    {
        var k = Get(id);
        return k != Key.None && (Input.IsPhysicalKeyPressed(k) || k == Key.Enter && Input.IsPhysicalKeyPressed(Key.KpEnter));
    }

    public static bool Is(InputEventKey e, string id)
    {
        var k = Get(id);
        return k != Key.None && (e.PhysicalKeycode == k || k == Key.Enter && e.PhysicalKeycode == Key.KpEnter);
    }

    public static void SetClassic(bool on) { Classic = on; Save(); }
    public static string Name(Key k) => k == Key.None ? "-" : OS.GetKeycodeString(k);

    public static void Load(string iniFile)
    {
        path = iniFile;
        if (!File.Exists(path)) return;
        bool inKeys = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inKeys = line.Equals("[keys]", StringComparison.OrdinalIgnoreCase); continue; }
            int eq = line.IndexOf('=');
            if (!inKeys || eq < 0 || line.StartsWith(';')) continue;
            string id = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
            if (id == "LandClassic") Classic = val == "1";
            else if (own.ContainsKey(id) && int.TryParse(val, out var code)) own[id] = (Key)code;
        }
    }

    /// <summary>Rewrites the [keys] section of settings.ini (the land control style and the debug keys), keeping the other sections.</summary>
    static void Save()
    {
        if (path.Length == 0) return;
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        int s = lines.FindIndex(l => l.Trim().Equals("[keys]", StringComparison.OrdinalIgnoreCase));
        if (s >= 0)
        {
            int e = s + 1;
            while (e < lines.Count && !lines[e].Trim().StartsWith('[')) e++;
            lines.RemoveRange(s, e - s);
        }
        lines.Add("[keys]");
        lines.Add("; the game keys are bound on the options pages (Options -> Controls) and saved with the game options; here: Godot physical key codes of the debug keys");
        foreach (var kv in own) lines.Add($"{kv.Key} = {(int)kv.Value}");
        lines.Add($"LandClassic = {(Classic ? 1 : 0)}");
        File.WriteAllLines(path, lines);
    }
}
