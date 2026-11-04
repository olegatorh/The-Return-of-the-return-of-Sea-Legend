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
            case 45027: visibleLog = visibleAction = d.Length > 1 && Conv.ToInt(d[1]) != 0; Refresh(); break;
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
