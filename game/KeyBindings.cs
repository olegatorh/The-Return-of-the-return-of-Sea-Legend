using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace SeaDogs;

/// <summary>
/// The player's key bindings (keyboard, by physical key so they work with any layout). The original game's control system is script-driven (CONTROLS/init_pc.c);
/// this port keeps its own table for the actions it implements in the engine and stores it in settings.ini [keys]. Edited in the options screen (Controls -> Change).
/// </summary>
public static class Keys
{
    public sealed record Act(string Id, string Group, string Title, Key Default);

    public static readonly Act[] All =
    {
        new("LandForward", "На суші", "Рух вперед", Key.W), new("LandBack", "На суші", "Рух назад", Key.S),
        new("LandLeft", "На суші", "Крок вліво", Key.A), new("LandRight", "На суші", "Крок вправо", Key.D),
        new("LandRun", "На суші", "Біг (сучасне) / повільний крок (класичне)", Key.Shift), new("LandTalk", "На суші", "Розмова", Key.E), new("LandSword", "На суші", "Дістати / сховати шаблю", Key.F), new("LandBlock", "На суші", "Блок (утримувати, шабля добута)", Key.Ctrl), new("LandFire", "На суші", "Постріл з пістоля", Key.Q), new("LandRecoil", "На суші", "Відскок назад", Key.Z),
        new("LandCamLeft", "На суші", "Камера вліво", Key.Left), new("LandCamRight", "На суші", "Камера вправо", Key.Right),
        new("LandCamUp", "На суші", "Камера вгору", Key.Up), new("LandCamDown", "На суші", "Камера вниз", Key.Down),
        new("LandLook", "На суші", "Погляд з очей / камера ззаду", Key.Tab),

        new("SeaLeft", "На морі й карті", "Поворот ліворуч", Key.A), new("SeaRight", "На морі й карті", "Поворот праворуч", Key.D),
        new("SeaSailUp", "На морі й карті", "Підняти вітрила", Key.W), new("SeaSailDown", "На морі й карті", "Опустити вітрила", Key.S),
        new("SeaAction", "На морі й карті", "Дія (порт / абордаж / вихід з карти)", Key.Enter), new("SeaMap", "На морі й карті", "Карта світу", Key.M), new("SeaCommands", "На морі й карті", "Командне меню (накази ескадрі)", Key.C), new("SeaTelescope", "На морі й карті", "Підзорна труба (утримувати)", Key.Ctrl),
        new("SeaBalls", "На морі й карті", "Ядра", Key.Key1), new("SeaGrapes", "На морі й карті", "Картеч", Key.Key2),
        new("SeaKnippels", "На морі й карті", "Книпелі", Key.Key3), new("SeaBombs", "На морі й карті", "Бомби", Key.Key4),
        new("SeaCamLeft", "На морі й карті", "Камера вліво", Key.Left), new("SeaCamRight", "На морі й карті", "Камера вправо", Key.Right),
        new("SeaCamUp", "На морі й карті", "Камера вгору", Key.Up), new("SeaCamDown", "На морі й карті", "Камера вниз", Key.Down),
        new("SeaZoomIn", "На морі й карті", "Наблизити камеру", Key.Pageup), new("SeaZoomOut", "На морі й карті", "Віддалити камеру", Key.Pagedown),
        new("SeaCameraSwitch", "На морі й карті", "Камера з палуби / камера корабля", Key.Tab),
        new("DeckForward", "На морі й карті", "Палуба: крок уперед (також права кнопка миші)", Key.Up), new("DeckBack", "На морі й карті", "Палуба: крок назад (також середня кнопка миші)", Key.Down),
        new("MapWait", "На морі й карті", "Карта: очікування (пропуск часу)", Key.T),

        new("QuickSave", "Загальні", "Швидке збереження", Key.F4), new("QuickLoad", "Загальні", "Швидке завантаження", Key.F6),
        new("Interface", "Загальні", "Екран персонажа / інтерфейс", Key.F2), new("MainMenu", "Загальні", "Головне меню", Key.F1),
        new("LandStyle", "Загальні", "Керування на суші: сучасне / класичне (як в оригіналі)", Key.F10), new("NameTags", "Загальні", "Імена над персонажами", Key.F3), new("FlyCam", "Загальні", "Вільна камера (налагодження)", Key.F9), new("BugReport", "Загальні", "Звіт про помилку (знімок + лог + стан)", Key.F8),
    };

    static readonly Dictionary<string, Key> map = All.ToDictionary(a => a.Id, a => a.Default);
    static string path = "";

    /// <summary>
    /// Land control scheme. false = this port's: WASD walks relative to the camera, the right mouse button / arrows turn the camera. true = the original
    /// (CONTROLS/init_pc.c): the mouse turns the hero and the camera height, A/D turn, W/S (or the right/middle mouse button) walk forward/back, the hero runs
    /// unless Shift is held, and the camera follows the hero's heading with inertia.
    /// </summary>
    public static bool Classic;

    public static Key Get(string id) => map.TryGetValue(id, out var k) ? k : Key.None;
    public static bool Down(string id) => Input.IsPhysicalKeyPressed(Get(id));
    public static bool Is(InputEventKey e, string id) => e.PhysicalKeycode == Get(id);

    public static void Set(string id, Key k) { map[id] = k; Save(); }
    public static void SetClassic(bool on) { Classic = on; Save(); }
    public static void ResetAll() { foreach (var a in All) map[a.Id] = a.Default; Save(); }
    public static string Name(Key k) => k == Key.None ? "—" : OS.GetKeycodeString(k);

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
            else if (map.ContainsKey(id) && int.TryParse(val, out var code)) map[id] = (Key)code;
        }
    }

    /// <summary>Rewrites the [keys] section of settings.ini, keeping the other sections.</summary>
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
        lines.Add("; Godot physical key codes; edit them in the game (Options -> Controls -> Change)");
        foreach (var a in All) lines.Add($"{a.Id} = {(int)map[a.Id]}");
        lines.Add($"LandClassic = {(Classic ? 1 : 0)}");
        File.WriteAllLines(path, lines);
    }
}

/// <summary>Full-screen list of actions with their keys; click a key, press the new one (Esc cancels).</summary>
public partial class KeyBindingsScreen : CanvasLayer
{
    string? waiting;
    readonly Dictionary<string, Button> buttons = new();
    readonly Label hint = new();

    public KeyBindingsScreen()
    {
        Layer = 60;
        var bg = new ColorRect { Color = new Color(0.02f, 0.05f, 0.14f, 1f) };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);
        var root = new VBoxContainer();
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.OffsetLeft = 60; root.OffsetRight = -60; root.OffsetTop = 30; root.OffsetBottom = -30;
        AddChild(root);
        var title = new Label { Text = "Налаштування керування", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 28);
        root.AddChild(title);
        hint.Text = "Натисніть кнопку з клавішею, потім нову клавішу (Esc — скасувати)";
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        root.AddChild(hint);
        var style = new CheckButton { Text = "Класичне керування на суші (як в оригіналі: миша повертає героя, A/D — поворот, W/S — хід)", ButtonPressed = Keys.Classic };
        style.Toggled += on => Keys.SetClassic(on);
        root.AddChild(style);
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        root.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(list);
        string group = "";
        foreach (var a in Keys.All)
        {
            if (a.Group != group)
            {
                group = a.Group;
                var g = new Label { Text = "— " + group + " —", Modulate = new Color(1f, 0.85f, 0.4f) };
                g.AddThemeFontSizeOverride("font_size", 20);
                list.AddChild(g);
            }
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = a.Title, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            var b = new Button { Text = Keys.Name(Keys.Get(a.Id)), CustomMinimumSize = new Vector2(180, 0) };
            var id = a.Id;
            b.Pressed += () => { waiting = id; b.Text = "…"; };
            buttons[id] = b;
            row.AddChild(b);
            list.AddChild(row);
        }
        var bottom = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        var reset = new Button { Text = "За замовчуванням" };
        reset.Pressed += () => { Keys.ResetAll(); Refresh(); };
        var close = new Button { Text = "Закрити" };
        close.Pressed += QueueFree;
        bottom.AddChild(reset); bottom.AddChild(close);
        root.AddChild(bottom);
    }

    void Refresh() { foreach (var (id, b) in buttons) b.Text = Keys.Name(Keys.Get(id)); waiting = null; }

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (waiting != null)
        {
            if (k.PhysicalKeycode != Key.Escape) Keys.Set(waiting, k.PhysicalKeycode);
            Refresh();
        }
        else if (k.PhysicalKeycode == Key.Escape) { QueueFree(); }
        GetViewport().SetInputAsHandled();
    }
}
