using System;
using System.Collections.Generic;
using Godot;

namespace SeaDogs;

/// <summary>
/// FORMATEDTEXT used as a selectable list with tall lines (the perks screen: "videoName" in the ini). The script appends the lines with node
/// message 0, the engine scrolls and selects them and tells the script through events: evntUpdateFormtText (node, first visible line, selected),
/// FormatedTextActivate (node, line), SetScrollerPos (node, position 0..1). Message 4 returns the top of every visible line so that the script
/// can place its own icons next to them.
/// </summary>
public partial class FormattedListView : Control
{
    readonly List<string> lines = new();
    readonly float lineSpace, left, up;
    readonly int visible;
    readonly string node;
    readonly Action<string, int, int> update;
    readonly Action<string, int> activate;
    readonly Action<string, float> scrollPos;
    int first, cur;

    public FormattedListView(string name, float x1, float y1, float x2, float y2, float lineSpace, float left, float up,
        Action<string, int, int> update, Action<string, int> activate, Action<string, float> scrollPos)
    {
        node = name; Name = name;
        Position = new Vector2(x1, y1); Size = new Vector2(x2 - x1, y2 - y1);
        this.lineSpace = lineSpace <= 0 ? 40 : lineSpace; this.left = left; this.up = up;
        visible = Math.Max(1, (int)(Size.Y / this.lineSpace));
        this.update = update; this.activate = activate; this.scrollPos = scrollPos;
        FocusMode = FocusModeEnum.All;
        ClipContents = false;
    }

    public int Count => lines.Count;
    public int Selected => cur;

    readonly Dictionary<int, Color> colors = new();

    public void Add(string text) { lines.Add(text); QueueRedraw(); }

    /// <summary>Message 8: colour of one text (group -1 = the last one added), an ARGB value.</summary>
    public void SetColor(int group, int argb)
    {
        if (group < 0) group = lines.Count - 1;
        uint c = unchecked((uint)argb);
        colors[group] = new Color((c >> 16 & 255) / 255f, (c >> 8 & 255) / 255f, (c & 255) / 255f, (c >> 24 & 255) / 255f);
        QueueRedraw();
    }

    /// <summary>Position 0..1 of the scroll bar.</summary>
    public void ScrollTo(float p)
    {
        int max = Math.Max(0, lines.Count - visible);
        SetFirst((int)Math.Round(Math.Clamp(p, 0, 1) * max), false);
    }

    /// <summary>Message 4: tops of the visible lines (the engine's answer is a list of attributes l0..).</summary>
    public IEnumerable<float> VisibleTops()
    {
        for (int i = 0; i < visible; i++)
            if (first + i < lines.Count) yield return i * lineSpace + up;
    }

    /// <summary>Message 3: for each text that starts in the visible part, the number of the visible line it starts in.</summary>
    public IEnumerable<int> VisibleStarts()
    {
        for (int i = 0; i < visible; i++)
            if (first + i < lines.Count && lines[first + i].Length > 0) yield return i;
    }

    void SetFirst(int f, bool notify)
    {
        f = Math.Clamp(f, 0, Math.Max(0, lines.Count - visible));
        if (f == first && !notify) return;
        first = f;
        QueueRedraw();
        Fire();
        scrollPos(node, lines.Count <= visible ? 0f : (float)first / (lines.Count - visible));
    }

    void Fire() => update(node, first, cur);

    public void Move(int delta)
    {
        if (lines.Count == 0) return;
        cur = Math.Clamp(cur + delta, 0, lines.Count - 1);
        if (cur < first) SetFirst(cur, true);
        else if (cur >= first + visible) SetFirst(cur - visible + 1, true);
        else { QueueRedraw(); Fire(); }
    }

    public void Activate() { if (cur >= 0 && cur < lines.Count) activate(node, cur); }

    public override void _Draw()
    {
        var font = GetThemeDefaultFont();
        for (int i = 0; i < visible && first + i < lines.Count; i++)
        {
            float top = i * lineSpace;
            bool sel = first + i == cur;
            if (sel) DrawRect(new Rect2(0, top - 14, Size.X, lineSpace - 4), new Color(1f, 0.85f, 0.4f, 0.18f));
            float room = Size.X - left - 8;
            int fs = 20;
            while (fs > 11 && font.GetStringSize(lines[first + i], HorizontalAlignment.Left, -1, fs).X > room) fs--;       // long titles are fitted to the window
            DrawString(font, new Vector2(left, top + 24), lines[first + i], HorizontalAlignment.Left, room, fs,
                sel ? new Color(1f, 0.92f, 0.6f) : colors.TryGetValue(first + i, out var lc) ? lc : Colors.White);
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.WheelDown) { SetFirst(first + 1, false); AcceptEvent(); }
            else if (mb.ButtonIndex == MouseButton.WheelUp) { SetFirst(first - 1, false); AcceptEvent(); }
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                int i = first + (int)(mb.Position.Y / lineSpace);
                if (i >= 0 && i < lines.Count)
                {
                    bool again = i == cur;
                    cur = i; QueueRedraw(); Fire();
                    GrabFocus();
                    Activate();
                }
                AcceptEvent();
            }
        }
    }
}
