using System;
using System.Collections.Generic;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine's "ILogAndActions": the message log (top left, lines fade out after a while) and the hint for the action
/// available at the player's position (door, item, chest, ...). The script drives it with LOG_ADD_STRING / LOG_SET_ACTIVE_ACTION.
/// </summary>
public partial class LogHud : Control
{
    readonly List<(Label label, double born)> lines = new();
    readonly Label action;
    double clock;
    bool visibleLog = true, visibleAction = true;
    bool allVisible = true, showLogStrings = true, showActiveCommand = true;      // LI_SET_VISIBLE; LOG_AND_ACTIONS_INIT / CHANGE (options: log strings, action hint)
    string actionName = "Nothing";
    public Func<string, string> Translate = s => s;
    bool actionSuppressed;

    /// <summary>The land command menu is open: it offers the same actions as icons, so the text hint steps aside.</summary>
    public bool ActionSuppressed { set { if (value != actionSuppressed) { actionSuppressed = value; Refresh(); } } }

    static readonly Dictionary<string, string> ActionText = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Reload"] = "Увійти / перейти", ["Closed"] = "Зачинено", ["Pick"] = "Підняти", ["OpenBox"] = "Відкрити скриню",
        ["Moor"] = "Причалити", ["Map"] = "Карта", ["Board"] = "Абордаж", ["LandTroops"] = "Висадити десант", ["Action"] = "Дія", ["Talk"] = "Говорити",
    };

    public LogHud()
    {
        Name = "LogHud";
        Size = new Vector2(640, 480);
        Position = new Vector2(40, 30);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 50;
        action = new Label { HorizontalAlignment = HorizontalAlignment.Center, Size = new Vector2(400, 30), Position = new Vector2(120, 430), MouseFilter = MouseFilterEnum.Ignore };
        Style(action, 18, new Color(1f, 0.85f, 0.4f));
        AddChild(action);
        action.Visible = false;
    }

    static void Style(Label l, int size, Color c)
    {
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", c);
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 4);
    }

    public V OnMessage(string fmt, V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        switch (msg)
        {
            case 45020: AddLine(d.Length > 2 ? Conv.ToStr(d[2]) : ""); break;                     // LOG_ADD_STRING (flag, text)
            case 45021: actionName = d.Length > 1 ? Conv.ToStr(d[1]) : "Nothing"; Refresh(); break;   // LOG_SET_ACTIVE_ACTION
            case 45022: case 45026:                                                              // LOG_AND_ACTIONS_INIT / _CHANGE "lll": show the active action hint, show the log strings
                showActiveCommand = d.Length > 1 && Conv.ToInt(d[1]) != 0; showLogStrings = d.Length > 2 && Conv.ToInt(d[2]) != 0;
                visibleLog = allVisible && showLogStrings; visibleAction = allVisible && showActiveCommand; Refresh(); break;
            case 45027:
                allVisible = d.Length > 1 && Conv.ToInt(d[1]) != 0;
                visibleLog = allVisible && showLogStrings; visibleAction = allVisible && showActiveCommand; Refresh(); break;
            case 45028: foreach (var (l, _) in lines) l.QueueFree(); lines.Clear(); break;        // LI_CLEAR_STRINGS
        }
        return V.Int(0);
    }

    void AddLine(string text)
    {
        if (text.Length == 0) return;
        var l = new Label { Text = text.Replace("\\n", " "), MouseFilter = MouseFilterEnum.Ignore };
        Style(l, 13, Colors.White);
        AddChild(l);
        lines.Add((l, clock));
        while (lines.Count > 8) { lines[0].label.QueueFree(); lines.RemoveAt(0); }
        Layout();
    }

    void Layout()
    {
        for (int i = 0; i < lines.Count; i++) lines[i].label.Position = new Vector2(10, 8 + i * 17);
    }

    void Refresh()
    {
        bool show = visibleAction && !actionSuppressed && !actionName.Equals("Nothing", StringComparison.OrdinalIgnoreCase) && actionName.Length > 0;
        action.Visible = show;
        if (show) action.Text = "[Пробіл]  " + (ActionText.TryGetValue(actionName, out var t) ? t : actionName);
    }

    public override void _Process(double delta)
    {
        Position = new Vector2(40, 30) + UiLayout.Extra(this);
        Process2(delta);
    }

    void Process2(double delta)
    {
        clock += delta;
        bool changed = false;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            double age = clock - lines[i].born;
            if (age > 9) { lines[i].label.QueueFree(); lines.RemoveAt(i); changed = true; }
            else lines[i].label.Modulate = new Color(1, 1, 1, age > 7 ? (float)(9 - age) / 2f : 1f);
        }
        if (changed) Layout();
        foreach (var (l, _) in lines) l.Visible = visibleLog;
    }
}

/// <summary>
/// The engine's "IBoardingStatus" (BATTLE_INTERFACE): two strips with the crews' strength during boarding. The script sets the place and colours as attributes
/// (myLeft/myTop/enemyLeft/enemyTop/width/height/myColor/enemyColor), LOG_INIT_CHARCTER_HP 45025 shows it and LOG_SET_CHARCTER_HP 45024 "ff" sets both fractions
/// (DrawCharacterHP in LogInterface.c; no script of the addon calls it, the entity only exists if a mod does).
/// </summary>
public partial class BoardingStatus : Control
{
    readonly ColorRect myBack = new(), myBar = new(), enemyBack = new(), enemyBar = new();
    public readonly Entity Ent;

    public BoardingStatus(Entity e)
    {
        Ent = e;
        Name = "BoardingStatus";
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 50;
        foreach (var r in new[] { myBack, myBar, enemyBack, enemyBar }) { r.MouseFilter = MouseFilterEnum.Ignore; AddChild(r); }
        myBack.Color = enemyBack.Color = new Color(0, 0, 0, 0.5f);
        Visible = false;
    }

    static Color Argb(string? v, Color def)
    {
        if (v == null || !long.TryParse(v, out long x)) return def;
        uint u = (uint)x;
        return new Color(((u >> 16) & 255) / 255f, ((u >> 8) & 255) / 255f, (u & 255) / 255f, ((u >> 24) & 255) / 255f);
    }

    float A(string name, float def) => Ent.Root.Get(name)?.Value is { Length: > 0 } v ? Conv.ParseFloatPrefix(v) : def;

    public V OnMessage(V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        if (msg == 45025)
        {
            // interface space (640x480 core at 40,30 like the log)
            float w = A("width", 120), h = A("height", 8);
            myBack.Position = myBar.Position = new Vector2(40 + A("myLeft", 16), 30 + A("myTop", 340));
            enemyBack.Position = enemyBar.Position = new Vector2(40 + A("enemyLeft", 16), 30 + A("enemyTop", 352));
            myBack.Size = myBar.Size = enemyBack.Size = enemyBar.Size = new Vector2(w, h);
            myBar.Color = Argb(Ent.Root.Get("myColor")?.Value, new Color(0, 0, 0.5f));
            enemyBar.Color = Argb(Ent.Root.Get("enemyColor")?.Value, new Color(0.5f, 0, 0));
            Visible = true;
        }
        else if (msg == 45024 && d.Length > 2)
        {
            float w = A("width", 120);
            myBar.Size = new Vector2(w * Math.Clamp(Conv.ToFloat(d[1]), 0f, 1f), myBar.Size.Y);
            enemyBar.Size = new Vector2(w * Math.Clamp(Conv.ToFloat(d[2]), 0f, 1f), enemyBar.Size.Y);
        }
        return V.Int(1);
    }
}
