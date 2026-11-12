using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace SeaDogs;

/// <summary>
/// The actions the game window carries out itself and the script control each one stands for. The original game's control system is script-driven (CONTROLS/init_pc.c,
/// remapped on the options pages, saved with the options): <see cref="Get"/> asks the control table for the key the player has bound. Only the original controls exist
/// there; the few port-only actions without a control (name tags, map wait, debug keys) are fixed keys in settings.ini [keys] and are not shown on the pages.
/// </summary>
public static class Keys
{
    /// <summary>Ctl = the control in the script's table (null: a settings.ini key), MapCtl = the control to use on the world map instead.</summary>
    public sealed record Act(string Id, string Title, Key Default, string? Ctl, string? MapCtl = null);

    public static readonly Act[] All =
    {
        // land (CONTROLS/init_pc.c: PrimaryLand, FightModeControls)
        new("LandLeft", "Крок вліво / поворот ліворуч", Key.A, "ChrTurnHL"), new("LandRight", "Крок вправо / поворот праворуч", Key.D, "ChrTurnHR"),
        new("LandSword", "Дістати / сховати шаблю", Key.E, "ChrFightMode"), new("LandBlock", "Блок", Key.Ctrl, "ChrBlock"),
        new("LandFire", "Постріл з пістоля", Key.Q, "ChrFire"), new("LandRecoil", "Відскок назад", Key.Z, "ChrRecoil"), new("LandPotion", "Випити зілля", Key.C, "BOAL_UsePotion"),
        new("LandCommands", "Командне меню на суші", Key.Enter, "LICommandsActivate"),
        new("LandLook", "Погляд з очей / камера ззаду", Key.Tab, "ChrCamCameraSwitch"),

        // sea (Sailing3Pers, Sailing1Pers) and world map (WorldMapControls)
        new("SeaLeft", "Поворот ліворуч", Key.A, "Ship_TurnLeft", "WMapShipTurnLeft"), new("SeaRight", "Поворот праворуч", Key.D, "Ship_TurnRight", "WMapShipTurnRight"),
        new("SeaSailUp", "Підняти вітрила", Key.W, "Ship_SailUp", "WMapShipSailUp"), new("SeaSailDown", "Опустити вітрила", Key.S, "Ship_SailDown", "WMapShipSailDown"),
        new("SeaAction", "Швидка дія (суша, море; на карті - вихід)", Key.Space, "BIFastCommand", "WMapCancel"),
        new("SeaCommands", "Командне меню (накази ескадрі)", Key.Enter, "BICommandsActivate"),
        new("SeaMap", "Карта світу", Key.M, "BI_MapEnter"), new("SeaTelescope", "Підзорна труба", Key.Ctrl, "TelescopeIn"),
        new("SeaBalls", "Ядра", Key.Key1, "BI_ChargeCannonballs"), new("SeaGrapes", "Картеч", Key.Key2, "BI_ChargeGrapes"),
        new("SeaKnippels", "Книпелі", Key.Key3, "BI_ChargeKnippels"), new("SeaBombs", "Бомби", Key.Key4, "BI_ChargeBombs"),
        new("SeaCameraSwitch", "Камера з палуби / камера корабля", Key.Tab, "Sea_CameraSwitch", "WMapCameraSwitch"),

        // general
        new("QuickSave", "Швидке збереження", Key.F4, "Quick_Save"), new("QuickLoad", "Швидке завантаження", Key.F6, "Quick_Load"),
        new("Interface", "Екран персонажа / інтерфейс", Key.F2, "Interface"), new("MainMenu", "Головне меню", Key.F1, "MainMenu"),
        new("MainEsc", "Головне меню (Esc)", Key.Escape, "MainMenuDouble"),
        new("NameTags", "Імена над персонажами (налагодження)", Key.F3, null), new("MapWait", "Карта: очікування", Key.B, null),
        new("FlyCam", "Вільна камера (налагодження)", Key.F9, null), new("BugReport", "Звіт про помилку (знімок + лог + стан)", Key.F8, null),
    };

    static readonly Dictionary<string, Act> byId = All.ToDictionary(a => a.Id);
    static readonly Dictionary<string, Key> own = All.Where(a => a.Ctl == null).ToDictionary(a => a.Id, a => a.Default);

    /// <summary>The world map is shown: the actions use the WMap* controls.</summary>
    public static bool InWorldMap;

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
        if (k == Key.None) return byId.TryGetValue(id, out var a) && ControlOf(id) is { } c && Ctl.T.Bound(c) is > 0 and < 7 && Ctl.Down(c);       // bound to a mouse button
        return Input.IsPhysicalKeyPressed(k) || k == Key.Enter && Input.IsPhysicalKeyPressed(Key.KpEnter);
    }

    public static bool Is(InputEventKey e, string id)
    {
        var k = Get(id);
        return k != Key.None && (e.PhysicalKeycode == k || k == Key.Enter && e.PhysicalKeycode == Key.KpEnter);
    }

    public static string Name(Key k) => k == Key.None ? "-" : OS.GetKeycodeString(k);

    public static void Load(string iniFile)
    {
        if (!File.Exists(iniFile)) return;
        bool inKeys = false;
        foreach (var raw in File.ReadAllLines(iniFile))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inKeys = line.Equals("[keys]", StringComparison.OrdinalIgnoreCase); continue; }
            int eq = line.IndexOf('=');
            if (!inKeys || eq < 0 || line.StartsWith(';')) continue;
            string id = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
            if (own.ContainsKey(id) && int.TryParse(val, out var code)) own[id] = (Key)code;
        }
    }
}
