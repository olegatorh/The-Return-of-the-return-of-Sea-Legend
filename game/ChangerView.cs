using System;
using System.Collections.Generic;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// CHANGER: a set of rectangles ("place = x1,y1,x2,y2") one of which is current. The script reads GameInterface.&lt;node&gt;.current (the index of the place, 0-based), so the engine
/// keeps that attribute: the mouse over / click on a place selects it, Up / Down move through the places. Clicks and steps are reported as commands of the node.
/// The selected place is outlined (the character screen: the row whose skill the +/- buttons change).
/// </summary>
public partial class ChangerView : Control
{
    readonly List<Rect2> places = new();
    readonly Func<Attr?> attr;
    readonly Action<string, string> run;
    readonly string node;
    int shown = -1;

    public ChangerView(string name, IEnumerable<float[]> placeRects, Func<Attr?> attr, Action<string, string> run)
    {
        node = name; this.attr = attr; this.run = run; Name = name;
        foreach (var r in placeRects) places.Add(new Rect2(r[0], r[1], r[2] - r[0], r[3] - r[1]));
        MouseFilter = MouseFilterEnum.Ignore;
        Position = Vector2.Zero; Size = new Vector2(640, 480);
        ZIndex = 5;
    }

    int Current
    {
        get => (int)Conv.ParseFloatPrefix(attr()?.Find("current", false)?.Value);
        set { var a = attr(); if (a != null) a.GetOrCreate("current").Value = value.ToString(); }
    }

    public void Step(int d)
    {
        if (places.Count == 0) return;
        Current = Math.Clamp(Current + d, 0, places.Count - 1);
        QueueRedraw();
    }

    int At(Vector2 p)
    {
        for (int i = 0; i < places.Count; i++) if (places[i].HasPoint(p)) return i;
        return -1;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsVisibleInTree()) return;
        if (e is InputEventMouseMotion)
        {
            int i = At(GetLocalMousePosition());
            if (i >= 0 && i != Current) { Current = i; QueueRedraw(); }
        }
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            int i = At(GetLocalMousePosition());
            if (i >= 0) { Current = i; QueueRedraw(); run(node, "click"); }
        }
    }

    public override void _Process(double delta)
    {
        if (Current != shown) { shown = Current; QueueRedraw(); }
    }

    public override void _Draw()
    {
        int c = Current;
        if (c < 0 || c >= places.Count) return;
        DrawRect(places[c], new Color(1f, 0.85f, 0.4f, 0.16f));
        DrawRect(places[c], new Color(1f, 0.85f, 0.4f, 0.55f), false, 1.2f);
    }
}
