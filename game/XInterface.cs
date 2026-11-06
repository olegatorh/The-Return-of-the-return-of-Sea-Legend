using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine's "xinterface" class: builds a screen from an .ini file (list of nodes in a 640x480 virtual screen),
/// talks to the script through SendMessage / events. Only the node types needed so far are implemented.
/// </summary>
public partial class XView : Control
{
    sealed record Cmd(string Name, List<string> Events, List<(string cmd, string node)> Coms, string? Select);

    readonly Interp it;
    public readonly Entity Ent;
    readonly Dictionary<string, Control> nodes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<Cmd>> cmds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Label> strings = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(Control c, float speed)> spinners = new();
    readonly Random rng = new();
    static readonly HashSet<string> warned = new(StringComparer.OrdinalIgnoreCase);

    public XView(Interp it, Entity ent)
    {
        this.it = it;
        Ent = ent;
        Size = new Vector2(640, 480);
        Position = new Vector2(40, 30);       // the visible interface area is x -40..680, y -30..510 (item positions are relative to the 640x480 core)
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>
    /// Save list screens: the script lists the files (GameInterface.saveslist.picN.descr = file name, savestr = "") and expects the engine to
    /// fill savestr with the description stored in the save ("place@face1..4@time@language"); an empty savestr means a corrupted save.
    /// </summary>
    public void FillSaveStrings()
    {
        var list = Ent.Root.Find("saveslist", false);
        if (list == null) return;
        for (int i = 1; i <= 200; i++)
        {
            var pic = list.Find("pic" + i, false);
            if (pic == null) { if (i > 1) break; continue; }
            var s = pic.Find("savestr", false);
            var d = pic.Find("descr", false)?.Value;
            if (s == null || string.IsNullOrEmpty(d) || !string.IsNullOrEmpty(s.Value)) continue;
            string desc = SaveFiles.ReadDescription(d);
            if (desc != "") s.Value = desc;
        }
    }

    /// <summary>Node captions are keys of the global strings file (common.ini): "New Game" -> "Нова гра".</summary>
    string Tr(string key) => it.Language?.Global(key) ?? key;

    static void Warn(string what)
    {
        if (warned.Add(what)) GD.Print("[xinterface] not implemented: " + what);
    }

    // ------------------------------------------------------------------ script -> interface
    public V OnMessage(string fmt, V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        string S(int i) => i < d.Length ? Conv.ToStr(d[i]) : "";
        switch (msg)
        {
            case 45010: Load(S(1)); return V.Int(1);                       // MSG_INTERFACE_INIT
            case 45011: Clear(); return V.Int(0);                          // MSG_INTERFACE_RELEASE
            case 45024:                                                    // SET_SELECTABLE
                if (nodes.TryGetValue(S(1), out var n) && n is Button b) b.Disabled = Conv.ToInt(d[2]) == 0;
                return V.Int(0);
            case 45026:                                                    // GET_SELECTABLE
                return V.Int(nodes.TryGetValue(S(1), out var n2) && n2 is Button b2 && !b2.Disabled ? 1 : 0);
            case 45016:                                                    // SET_NODE_USING
                if (nodes.TryGetValue(S(1), out var n3)) n3.Visible = Conv.ToInt(d[2]) != 0;
                return V.Int(0);
            case 45017:
            case 45021:                                                    // set current (focused) node
                SelectNode(S(1));
                return V.Int(0);
            case 45028:                                                    // GET_CURRENT_NODE (output string)
                it.WriteRef(d[1], V.Str(CurrentNode()));
                return V.Int(0);
            case 45040:                                                    // GET_STRWIDTH: string, font, scale -> width in interface units
            {
                int fpx = FontPx(S(2), d.Length > 3 ? Conv.ToFloat(d[3]) : 1f);
                return V.Int((int)Math.Ceiling(new Label().GetThemeDefaultFont().GetStringSize(S(1), HorizontalAlignment.Left, -1, fpx).X));
            }
            case 45030:                                                    // GET_LANGUAGE_STRING: key -> text (no language files yet: echo)
                it.WriteRef(d[2], V.Str(S(1)));
                return V.Int(0);
            case 45012: CreateString(d); return V.Int(0);                  // CREATE_STRING
            case 45014:
            case 45015:                                                    // ENABLE/DISABLE_STRING
                if (strings.TryGetValue(S(1), out var lab)) lab.Visible = msg == 45014;
                return V.Int(0);
            case 45045: if (System.Environment.GetEnvironmentVariable("SEADOGS_NODEMSG") != null) GD.Print($"[nodemsg] {fmt} {string.Join(" ; ", d.Select(x => Conv.ToStr(x)))}"); string ntext = d.Length > 4 ? S(4) : S(3); NodeMessage(S(1), Conv.ToInt(d[2]), ntext, d);
                // code 0 on a formatted text adds a text and returns the number of lines it occupies (the control pages count the key column's rows with it)
                return V.Int(Conv.ToInt(d[2]) == 0 && nodes.TryGetValue(S(1), out var nn) && (nn is Label || nn is FormattedListView) && d.Length <= 5 ? 1 + ntext.Count(ch => ch == '\n') : 0);   // MSG_TO_NODE
            case 45013:                                                    // PLACE_IMAGE: node, ?, x1, y1, x2, y2
                if (d.Length >= 7)
                {
                    Control? pn = null;
                    if (!nodes.TryGetValue(S(1), out pn))
                    {
                        // the script's own images: GameInterface.pictures.<name>.{tex,pic} (check marks of the option screens ...)
                        if (!dynImages.TryGetValue(S(1), out var di))
                        {
                            di = new TextureRect { Name = "img_" + S(1), MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, ZIndex = 3 };   // xinterface.cpp Realize: nodes, then the script's pictures, then the script's strings, then the modal nodes
                            dynImages[S(1)] = di; AddChild(di);
                        }
                        string tex = Ent.Root.Find("pictures." + S(1) + ".tex", false)?.Value ?? "", pic = Ent.Root.Find("pictures." + S(1) + ".pic", false)?.Value ?? "";
                        di.Texture = Atlas.Get(tex, pic);
                        pn = di;
                    }
                    int x1 = Conv.ToInt(d[3]), y1 = Conv.ToInt(d[4]), x2 = Conv.ToInt(d[5]), y2 = Conv.ToInt(d[6]);
                    pn.Visible = x2 > x1 && y2 > y1;
                    pn.Position = new Vector2(x1, y1);
                    pn.Size = new Vector2(Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
                }
                return V.Int(0);
            case 45051:                                                    // CHANGE_STR_COLOR: string key, argb
                if (collectionStrings.TryGetValue(S(1), out var cl) || strings.TryGetValue(S(1), out cl))
                {
                    uint c = unchecked((uint)Conv.ToInt(d[2]));
                    cl.AddThemeColorOverride("font_color", new Color((c >> 16 & 255) / 255f, (c >> 8 & 255) / 255f, (c & 255) / 255f, (c >> 24) / 255f));
                }
                return V.Int(0);
            case 45033: ShowQuestText(d.Length > 2 ? d[2].Attr : null, d.Length > 3 ? Conv.ToInt(d[3]) : 0); return V.Int(0);   // INIT_QTEXT_SHOW
            case 45034:                                                    // SET_SCROLLER: node, position 0..1
                if (nodes.TryGetValue(S(1), out var scn) && scn is ScrollerView scv) scv.SetPos(d.Length > 2 ? Conv.ToFloat(d[2]) : 0);
                return V.Int(0);
            case 45020:                                                    // FOURIMAGE_CHANGE "lsl": the script changed the pictures of a FOURIMAGES node (officer faces, ships)
                if (nodes.TryGetValue(S(1), out var f4n) && f4n is FourImagesView f4v) f4v.Refresh();
                return V.Int(0);
            case 45018:                                                    // SCROLL_CHANGE: the script changed the lists of a scroll image
                if (nodes.TryGetValue(S(1), out var sn) && sn is ScrollImageView ssv) ssv.Refresh();
                return V.Int(0);
            case 45056:                                                    // GETTIME: file ("" = now) -> time string, date string
                it.WriteRef(d[2], V.Str(DateTime.Now.ToString("HH:mm:ss")));
                it.WriteRef(d[3], V.Str(DateTime.Now.ToString("dd.MM.yyyy")));
                return V.Int(0);
            case 45038:                                                    // SET_FORMATEDTEXT: node, text
                if (System.Environment.GetEnvironmentVariable("SEADOGS_NODEMSG") != null) GD.Print($"[formatted] {S(1)}: {S(2)}");
                if (nodes.TryGetValue(S(1), out var ft) && ft is Label fl) fl.Text = S(2).Replace("\\n", "\n");
                return V.Int(0);
            case 45035: return FindSave(d);                                // SAVE_FILE_FIND (n, &name, &size)
            case 45036: return V.Int(SaveFiles.Exists(S(1)) ? 1 : 0);      // NEW_SAVE_FILE_NAME: does this name exist already
            case 45037: SaveFiles.Delete(S(1)); return V.Int(1);           // DELETE_SAVE_FILE
            case 45047: SaveFiles.WriteDescription(S(1), S(2)); return V.Int(1);   // DO_SAVE_DATA
            case 45048: it.WriteRef(d[2], V.Str(SaveFiles.ReadDescription(S(1)))); return V.Int(1);   // GET_SAVE_DATA
            case 45055: it.WriteRef(d[2], V.Str(S(1))); return V.Int(1);   // FILENAME2DATASTR
            case 45300: return V.Int(1000000);                             // GET_FREE_SPACE
            case 45042: case 45043: case 45044: return V.Int(-1);          // screenshot thumbnails of saves: not stored
            case 45046:                                                    // LOCK_NODE: 0 = unlock; 1 = only this node takes input; 2 = this one too
                if (Conv.ToInt(d[1]) == 0) Unlock(); else LockNode(S(2), Conv.ToInt(d[1]) == 1);
                return V.Int(0);
            case 45039:                                                    // SET_BLIND: script picture, on/off, colour min, colour max (pulses between them)
                if (Conv.ToInt(d[2]) != 0 && d.Length > 4) blinds[S(1)] = (unchecked((uint)Conv.ToInt(d[3])), unchecked((uint)Conv.ToInt(d[4])));
                else { blinds.Remove(S(1)); if (dynImages.TryGetValue(S(1), out var ub)) ub.Modulate = Colors.White; }
                return V.Int(0);
            case 45049:                                                    // DELETE_PICTURE: a script image (help screen)
                if (dynImages.Remove(S(1), out var dp)) { dp.QueueFree(); dynKeys.Remove(S(1)); blinds.Remove(S(1)); }
                return V.Int(0);
            case 45050:                                                    // DELETE_STRING: a script string
                if (strings.Remove(S(1), out var ds)) { ds.QueueFree(); strInfo.Remove(S(1)); }
                return V.Int(0);
            case 45029:                                                    // REFRESH_SCROLL: node
                if (nodes.TryGetValue(S(1), out var rs) && rs is ScrollImageView rsv) rsv.Refresh();
                return V.Int(0);
            case 45025: DeleteScrollImage(S(1), d.Length > 2 ? Conv.ToInt(d[2]) : -1); return V.Int(0);   // DEL_SCROLLIMAGE: node, index
            case 45022: ctxHelp?.SetTemp(S(1)); return V.Int(0);           // SETHELPSTRING: a temporary hint for the delay of the CONTEXTHELP node
            case 45052: if (d.Length > 2 && d[2].Attr is { } saveA) OptionsFile.Save(S(1), saveA); return V.Int(0);                  // SAVEOPTIONS: name, the option tree
            case 45053: return V.Int(d.Length > 2 && d[2].Attr is { } loadA && OptionsFile.Load(S(1), loadA) ? 1 : 0);              // LOADOPTIONS
            case 45031: SetQuestTitles(d); return V.Int(0);                 // SET_TITLE_STRINGS: node, QuestInfo, top
        }
        Warn($"message {msg} ({fmt})");
        return V.Int(0);
    }

    void NodeMessage(string node, int code, string param, V[] d)
    {
        if (!nodes.TryGetValue(node, out var c)) return;
        if (c is ContextHelpView chv0) { if (code == 0) chv0.ClearTemp(); return; }                    // MSG_TO_NODE 0: remove the temporary hint
        if (c is KeyChooserView kcv) { if (code == 0 && d.Length > 3) kcv.Codes = d[3].Attr; return; }   // the list of key codes (objControlsState.key_codes)
        if (c is EditBoxView) return;
        if (c is FormattedListView fl0)
        {
            switch (code)
            {
                case 0: fl0.Add(param); return;                                          // append a line
                case 2: fl0.ScrollTo(d.Length > 3 ? Conv.ToFloat(d[3]) : 0); return;     // scroll bar moved
                case 8: if (d.Length > 4) fl0.SetColor(Conv.ToInt(d[3]), Conv.ToInt(d[4])); return;      // colour of a text
                case 3:                                                                  // line0.. = the visible line where each text starts (-1: nothing)
                    if (d.Length > 3 && d[3].Attr is { } posA) { int k = 0; foreach (int ln in fl0.VisibleStarts()) posA.GetOrCreate("line" + k++).Value = ln.ToString(); if (k == 0) posA.GetOrCreate("line0").Value = "-1"; }
                    return;
                case 4:                                                                  // tops of the visible lines -> attributes l0..
                    if (d.Length > 3 && d[3].Attr is { } outA) { int k = 0; foreach (var top in fl0.VisibleTops()) outA.GetOrCreate("l" + k++).Value = ((int)top).ToString(); }
                    return;
            }
        }
        if (c is Label && code is 5 or 6 or 8 or 1) return;                         // formatted text: "recompute / scroll bar state" (the label wraps by itself)
        if (c is Label fl2 && code == 0 && d.Length <= 5) { fl2.Text = fl2.Text.Length == 0 ? param : fl2.Text + "\n" + param; return; }   // append a line
        if ((c is TextureButton || c is Button) && code == 0 && d.Length >= 7) { float x1 = Conv.ToFloat(d[3]), y1 = Conv.ToFloat(d[4]), x2 = Conv.ToFloat(d[5]), y2 = Conv.ToFloat(d[6]); c.Position = new Vector2(x1, y1); c.Size = new Vector2(x2 - x1, y2 - y1); return; }       // move / resize a button (the +/- arrows of the skill rows)
        if (c is ScrollImageView sciv && code == 0) { sciv.Modulate = Conv.ToInt(d.Length > 3 ? d[3] : V.Int(1)) != 0 ? Colors.White : new Color(1, 1, 1, 0.6f); return; }     // this list is the active one (the chest screen has two)
        if (c is TextureRect tr4 && code == 4) { uint col = unchecked((uint)Conv.ToInt(d[3])); tr4.Modulate = new Color((col >> 16 & 255) / 128f, (col >> 8 & 255) / 128f, (col & 255) / 128f, (col >> 24) / 255f); return; }   // tint of a picture
        if (code == 0 && d.Length >= 10)   // a captioned string placed on a node: name, text, font, x, y, colour, ?, alignment, ?, scale, width
        {
            string sname = "msg_" + node;
            var existing = c.GetNodeOrNull<Label>(sname);
            if (existing == null) { existing = new Label { Name = sname, MouseFilter = MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.WordSmart }; c.AddChild(existing); }
            float w = d.Length > 13 ? Conv.ToFloat(d[13]) : 400, sc = d.Length > 12 ? Conv.ToFloat(d[12]) : 1f;
            uint cc = unchecked((uint)Conv.ToInt(d[8]));
            existing.Text = Conv.ToStr(d[4]);
            existing.AddThemeFontSizeOverride("font_size", Math.Max(12, (int)(18 * (sc <= 0 ? 1 : sc))));
            existing.AddThemeColorOverride("font_color", new Color((cc >> 16 & 255) / 255f, (cc >> 8 & 255) / 255f, (cc & 255) / 255f));
            existing.AddThemeColorOverride("font_outline_color", Colors.Black);
            existing.AddThemeConstantOverride("outline_size", 3);
            existing.HorizontalAlignment = Conv.ToInt(d[10]) == 2 ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            existing.Size = new Vector2(w, 40);
            // positions are in the 640x480 core of the screen, the node sits somewhere inside it
            existing.Position = new Vector2(Conv.ToFloat(d[6]) - c.Position.X - (Conv.ToInt(d[10]) == 2 ? w / 2 : 0), Conv.ToFloat(d[7]) - c.Position.Y);
            return;
        }
        if (c is TextureRect tr2 && code == 2)  // SetNewPicture: replace the texture by file
        {
            tr2.Texture = GameAssets.Texture(param);
            return;
        }
        if (c is TextureRect tr && code == 3)   // "pick a picture from this folder"
        {
            var list = GameAssets.TexturesIn(param);
            if (list.Count > 0) tr.Texture = GameAssets.Texture(list[rng.Next(list.Count)]);
            return;
        }
        if (c is Button btn && code == 0) { btn.Text = param; return; }                                  // set the caption (ship name ...)
        if (c is SliderView sv && code == 0) { sv.Value = Conv.ParseFloatPrefix(param); return; }      // set the slider position (0..1)
        Warn($"node message {code} for {c.GetType().Name} '{node}' param '{param}'");
    }

    readonly Dictionary<string, TextureRect> dynImages = new();

    readonly Dictionary<string, (int px0, int align, int x, int y)> strInfo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Font size of a script string: it must fit its row (screen edge, buttons beside a centred one, the next string to the right of a left-aligned one).</summary>
    void RefitString(string name)
    {
        if (!strings.TryGetValue(name, out var lab) || !strInfo.TryGetValue(name, out var si)) return;
        var (px0, align, x, y) = si;
        float room = align == 1 ? x + 40 : align == 2 ? 2 * Math.Min(x + 40, 680 - x) : 680 - x;
        if (align == 2)
        {
            float half = room / 2;
            foreach (var bt in nodes.Values.OfType<Button>().Where(b => b.Visible && b.Size.X > 4 && b.Text.Length > 0))
            {
                if (bt.Position.Y > y + 20 || bt.Position.Y + bt.Size.Y < y + 4) continue;
                if (bt.Position.X > x) half = Math.Min(half, bt.Position.X - x - 2);
                else if (bt.Position.X + bt.Size.X < x) half = Math.Min(half, x - bt.Position.X - bt.Size.X - 2);
            }
            room = Math.Max(60, 2 * half);
            // a caption centred under / over a script picture (ship type under the ship picture of the ransack screen) must not be wider than the picture
            foreach (var di in dynImages.Values)
                if (di.Visible && di.Size.X > 20 && x > di.Position.X && x < di.Position.X + di.Size.X && y + 10 > di.Position.Y && y + 10 < di.Position.Y + di.Size.Y)
                    room = Math.Max(60, Math.Min(room, di.Size.X - 4));
        }
        else
        {
            room = Math.Max(120, room);
            if (align == 0)
                foreach (var (on, oi) in strInfo)
                    if (!on.Equals(name, StringComparison.OrdinalIgnoreCase) && Math.Abs(oi.y - y) < 8 && oi.x > x + 20 && strings.TryGetValue(on, out var ol) && ol.Text.Length > 0)
                        room = Math.Min(room, oi.x - x - 6);
            room = Math.Max(60, room);
        }
        int fpx = Fit(lab, lab.Text, px0, room);
        lab.AddThemeFontSizeOverride("font_size", fpx);
        if (align == 1 && lab.Text.Length > 0)
        {
            // a right-aligned value stands in the row of a caption of a string collection: the caption must end before the value starts
            float left = x - lab.GetThemeDefaultFont().GetStringSize(lab.Text, HorizontalAlignment.Left, -1, fpx).X;
            foreach (var cl in collectionLabels)
            {
                if (!GodotObject.IsInstanceValid(cl) || Math.Abs(cl.Position.Y + 2 - y) > 8 || cl.Position.X >= left || cl.HasMeta("aligned")) continue;
                if (!cl.HasMeta("px0")) cl.SetMeta("px0", cl.GetThemeFontSize("font_size"));
                cl.AddThemeFontSizeOverride("font_size", Fit(cl, cl.Text, (int)cl.GetMeta("px0"), Math.Max(30, left - cl.Position.X - 4)));
            }
        }
    }

    void CreateString(V[] d)
    {
        // "lssllllf": msg, name, font, color, x, y, alignment, scale ; the text is the script attribute strings.<name>
        string name = Conv.ToStr(d[1]);
        string text = Ent.Root.Find("strings." + name, false)?.Value ?? "";
        uint argb = unchecked((uint)Conv.ToInt(d[3]));
        int x = Conv.ToInt(d[4]), y = Conv.ToInt(d[5]), align = Conv.ToInt(d[6]);
        float scale = Conv.ToFloat(d[7]);
        if (!strings.TryGetValue(name, out var lab))
        {
            lab = new Label { Name = "str_" + name, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 5 };
            strings[name] = lab;
            AddChild(lab);
        }
        lab.Text = text;
        // the original uses bitmap fonts by name; Dialog21 is the small one, Dialog2 a title size, interface_normal the menu size
        string font = Conv.ToStr(d[2]);
        float basePx = font.Equals("Dialog21", StringComparison.OrdinalIgnoreCase) ? 13f : font.Equals("Dialog2", StringComparison.OrdinalIgnoreCase) ? 24f : 26f;
        int px0 = font.Contains("small", StringComparison.OrdinalIgnoreCase) ? FontPx(font, scale) : Math.Max(8, (int)(basePx * 0.85f * scale));
        strInfo[name] = (px0, align, x, y);
        RefitString(name);
        lab.AddThemeColorOverride("font_color", new Color((argb >> 16 & 255) / 255f, (argb >> 8 & 255) / 255f, (argb & 255) / 255f, (argb >> 24) / 255f));
        lab.AddThemeColorOverride("font_outline_color", Colors.Black);
        lab.AddThemeConstantOverride("outline_size", 3);
        // fixed-width box so alignment does not depend on the font being laid out yet: 0 = left, 1 = right, 2 = centre of x
        const float W = 620;
        lab.Size = new Vector2(W, 40);
        lab.HorizontalAlignment = align == 1 ? HorizontalAlignment.Right : align == 2 ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        lab.Position = new Vector2(align == 1 ? x - W : align == 2 ? x - W / 2 : x, y);
    }

    // ------------------------------------------------------------------ building the screen
    public void Clear()
    {
        foreach (var c in GetChildren()) c.QueueFree();
        ctxHelp = null; locks.Clear(); lockSaved.Clear(); blinds.Clear(); preLockNode = "";
        dynImages.Clear(); dynKeys.Clear(); snapped.Clear(); lastSnapExtra = new Vector2(-1, -1); nodes.Clear(); cmds.Clear(); strings.Clear(); strInfo.Clear(); spinners.Clear(); collectionStrings.Clear(); collectionLabels.Clear(); questTitleNode = null; questTextLabel = null;
    }

    static Color Argb(string? s, Color def)
    {
        if (s == null) return def;
        var p = s.Split(',').Select(x => int.TryParse(x.Trim(), out var v) ? v : 128).ToArray();
        if (p.Length < 4) return def;
        // the engine's interface colours are modulated "x2": 128 is neutral
        return new Color(p[1] / 128f, p[2] / 128f, p[3] / 128f, p[0] / 255f);
    }

    static float[] Floats(string? s, int n)
    {
        var r = new float[n];
        if (s == null) return r;
        var p = s.Split(',');
        for (int i = 0; i < n && i < p.Length; i++)
            float.TryParse(p[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r[i]);
        return r;
    }

    public void Load(string iniFile)
    {
        Clear();
        string path = System.IO.Path.IsPathRooted(iniFile) ? iniFile : System.IO.Path.Combine(GameAssets.GameDir, iniFile.Replace('\\', System.IO.Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(path)) { GD.PrintErr("interface ini not found: " + iniFile); return; }
        GD.Print("[xinterface] loading " + iniFile);
        var ini = IniFile.Load(path);
        var defs = IniFile.Load(GameAssets.Resource("INI\\INTERFACES\\defaultnode.ini"));
        var main = ini.Get("MAIN");
        if (main == null) return;

        var order = new List<(int prio, int idx, string type, string name)>();
        int idx = 0;
        foreach (var line in main.All("item"))
        {
            var parts = line.Split(',').Select(x => x.Trim()).ToList();
            int i = 0;
            int prio = 80;                                  // items without a priority sit between the background layers (60) and the top ones (100+)
            if (int.TryParse(parts[0], out int declared)) { prio = declared; i++; }
            bool skip = false;
            if (i < parts.Count && parts[i].Equals("PC", StringComparison.OrdinalIgnoreCase)) i++;
            if (i + 1 < parts.Count && parts[i].Equals("LANG", StringComparison.OrdinalIgnoreCase))
            {
                skip = !parts[i + 1].Equals("English", StringComparison.OrdinalIgnoreCase);
                i += 2;
            }
            if (skip || i + 1 >= parts.Count) continue;
            string type = parts[i].ToUpperInvariant(), name = parts[i + 1];
            if (order.Any(o => o.name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            order.Add((prio, idx++, type, name));
        }

        foreach (var (prio, _, type, name) in order.OrderBy(o => o.prio).ThenBy(o => o.idx))
        {
            var sec = ini.Get(name);
            var def = defs.Get(type);
            string? P(string key) => sec?.Get(key) ?? def?.Get(key);
            bool hidden = sec?.Has("bNotUse") == true;

            var pos = Floats(P("position"), 4);
            int snapFlags = 0;
            if (int.TryParse(P("bAbsoluteRectangle"), out int snap))      // 1/2/4/8: left/top/right/bottom edge follows the screen edge
            {
                snapFlags = snap;
                if ((snap & 1) != 0) pos[0] = -40;
                if ((snap & 2) != 0) pos[1] = -30;
                if ((snap & 4) != 0) pos[2] = 680;
                if ((snap & 8) != 0) pos[3] = 510;
            }
            float[] snapBase = (float[])pos.Clone();
            Control? node = type switch
            {
                "PICTURE" or "SLIDEPICTURE" => MakePicture(P, pos),
                "TEXTBUTTON" => MakeButton(name, P, pos),
                "SCROLLBAR" => new ScrollBarView(name, pos[0], pos[1], pos[2], pos[3], P("group") ?? "ICONS", P("leftPicture") ?? "small left arrow", P("rightPicture") ?? "small right arrow", P("centerPicture") ?? "scrollbar", (n, c) => Run(n, c, 0)),
                "LRCHANGER" => new ScrollBarView(name, pos[0], pos[1], pos[2], pos[3], P("group") ?? "ICONS", P("lpicture") ?? "left arrow", P("rpicture") ?? "right arrow", "", (n, c) => Run(n, c, 0),
                    (n, btn) => it.FireEvent("IEvnt_MouseClick", new List<V> { V.Str(n), V.Int(btn) })),
                "SCROLLER" => new ScrollerView(pos[0], pos[1], pos[2], pos[3], P("baseTexture") ?? "", P("rollerTexture") ?? "", Floats(P("scrollTexPos"), 4), Floats(P("rollTexPos"), 4), Floats(P("rollerHeight"), 1)[0],
                    name, (n, p) => it.FireEvent("ScrollPosChange", new List<V> { V.Float(p), V.Str(n) }), d => it.FireEvent("ScrollTopChange", new List<V> { V.Int(d) })),
                "BUTTON" => MakeIconButton(name, P, pos),
                "RECTANGLE" => MakeRectangle(P, pos),
                "BOUNDER" => MakeBounder(P, pos),
                "TITLE" => MakeTitle(P, pos),
                "IMAGECOLLECTION" => MakeCollection(sec, P),
                "FORMATEDTEXT" => P("videoName") != null ? MakeFormattedList(name, P, pos) : MakeFormatted(P, pos),
                "VIDEO" => new Control { MouseFilter = MouseFilterEnum.Ignore },     // background video: not played
                "STRINGCOLLECTION" => MakeStringCollection(sec),
                "LINECOLLECTION" => MakeLineCollection(sec),
                "STATUSLINE" => MakeStatusLine(P, pos),
                "SCROLLIMAGE" => MakeScrollImage(name, P, pos),
                "FOURIMAGES" => MakeFourImages(name, P),
                "SLIDER" => new SliderView(Ent, name, pos[0], pos[1], pos[2], pos[3], P("baseTexture") ?? "", P("selectTexture") ?? "", P("pointerTexture") ?? "",
                    Floats(P("pointerWidth"), 1)[0], Floats(P("pointerHeight"), 1)[0], Floats(P("baseLeft"), 1)[0]),
                "QUESTTITLE" => MakeQuestTitle(P, pos),
                "QUESTTEXT" => MakeQuestText(pos),
                "CONTEXTHELP" => MakeContextHelp(sec, P, pos),
                "CHANGER" => MakeChanger(name, sec),
                "EDITBOX" => new EditBoxView(Ent, name, pos[0], pos[1], pos[2], pos[3], (int)Floats(P("stringLength"), 1)[0],
                    FontPx(P("strFont") ?? "interface_normal", Floats(P("strScale"), 1)[0] is var es && es > 0 ? es : 1f), Plain(P("argbBoxColor"), new Color(0, 0, 0, 1)), Plain(P("argbBoundColor"), new Color(0.3f, 0.3f, 0.3f, 0.5f))),
                "KEYCHOOSER" => new KeyChooserView(),
                "GLOWS" or "GLOWCURSOR" or "XBOX" => new Control { MouseFilter = MouseFilterEnum.Ignore },   // purely decorative / console
                _ => null,
            };
            if (node == null) { Warn($"node type {type}"); continue; }
            if (snapFlags != 0) snapped.Add((node, snapFlags, snapBase));
            node.Name = name;
            if (prio >= 120) node.ZIndex = 10;      // modal layers (confirmation windows) sit above the script's strings, which are added later
            node.Visible = !hidden;
            nodes[name] = node;
            AddChild(node);

            if (sec != null)
            {
                var list = new List<Cmd>();
                foreach (var cl in sec.All("command"))
                {
                    var cp = cl.Split(',').Select(x => x.Trim()).ToList();
                    var rest = cp.Skip(1).ToList();
                    var coms = new List<(string, string)>();
                    foreach (var x in rest.Where(x => x.StartsWith("com:", StringComparison.OrdinalIgnoreCase)))
                    {
                        var q = x.Split(':');
                        if (q.Length >= 3) coms.Add((q[1], q[2]));
                    }
                    string? select = rest.FirstOrDefault(x => x.StartsWith("select:", StringComparison.OrdinalIgnoreCase))?.Substring(7);
                    list.Add(new Cmd(cp[0], rest.Where(x => x.StartsWith("event:", StringComparison.OrdinalIgnoreCase)).Select(x => x.Substring(6)).ToList(), coms, select));
                }
                cmds[name] = list;
            }
        }
        AvoidButtons();
        if (main.Get("start") is { Length: > 0 } startNode) SelectNode(startNode.Trim());     // [MAIN] start = the node that is current when the screen opens
        GD.Print($"[xinterface] built {nodes.Count} nodes");
    }

    /// <summary>
    /// The captions of string collections were laid out for the short English texts; a longer translation must not run under a button
    /// to its right: shrink such a caption so it ends before the nearest visible button on its row.
    /// </summary>
    void AvoidButtons()
    {
        var buttons = nodes.Values.OfType<Button>().Where(b => b.Visible && b.Size.X > 4 && b.Size.Y > 4).ToList();
        foreach (var lab in collectionLabels)
        {
            if (!GodotObject.IsInstanceValid(lab) || lab.Text.Length == 0 || lab.HasMeta("aligned")) continue;
            float top = lab.Position.Y, bottom = top + 20, x = lab.Position.X;
            float limit = float.MaxValue;
            foreach (var b in buttons)
            {
                var r = new Rect2(b.Position, b.Size);
                if (r.Position.X > x + 12 && r.Position.Y < bottom && r.End.Y > top + 4) limit = Math.Min(limit, r.Position.X - x - 4);
            }
            if (limit == float.MaxValue) continue;
            int cur = lab.GetThemeFontSize("font_size");
            lab.AddThemeFontSizeOverride("font_size", Fit(lab, lab.Text, cur, Math.Max(30, limit)));
        }
    }

    Control MakePicture(Func<string, string?> P, float[] pos)
    {
        var tr = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2(pos[0], pos[1]),
            Size = new Vector2(pos[2] - pos[0], pos[3] - pos[1]),
            Modulate = Argb(P("color"), Colors.White),
        };
        var tex = GameAssets.Texture(P("textureName") ?? "");
        if (tex == null && P("group") != null && P("picture") != null)
        {
            tr.Texture = Atlas.Get(P("group")!, P("picture")!);
            return tr;
        }
        if (tex != null)
        {
            var uv = Floats(P("textureRect"), 4);
            if (uv[2] > 0 && uv[3] > 0 && (uv[2] < 1 || uv[3] < 1 || uv[0] > 0 || uv[1] > 0))
            {
                var at = new AtlasTexture { Atlas = tex };
                float w = tex.GetWidth(), h = tex.GetHeight();
                at.Region = new Rect2(uv[0] * w, uv[1] * h, (Math.Min(uv[2], 1) - uv[0]) * w, (Math.Min(uv[3], 1) - uv[1]) * h);
                tr.Texture = at;
            }
            else tr.Texture = tex;
        }
        if (P("techniqueName") == "iRotate")
        {
            tr.PivotOffset = tr.Size / 2;
            spinners.Add((tr, Floats(P("rotate"), 2)[0]));
        }
        return tr;
    }

    Control MakeButton(string name, Func<string, string?> P, float[] pos)
    {
        // some screens put their buttons at the very bottom edge (y up to 515 on a 480 high screen): keep the caption visible
        float overflow = Math.Max(0, pos[3] - 510);
        pos[1] -= overflow; pos[3] -= overflow;
        var b = new Button
        {
            Text = Tr(P("string") ?? name),
            Flat = true,
            Position = new Vector2(pos[0], pos[1]),
            Size = new Vector2(pos[2] - pos[0], pos[3] - pos[1]),
            FocusMode = FocusModeEnum.All,
        };
        var empty = new StyleBoxEmpty();
        foreach (var s in new[] { "normal", "hover", "pressed", "focus", "disabled" }) b.AddThemeStyleboxOverride(s, empty);
        var gold = new Color(1f, 0.82f, 0.35f);
        b.AddThemeColorOverride("font_color", new Color(1, 1, 1));
        b.AddThemeColorOverride("font_hover_color", gold);
        b.AddThemeColorOverride("font_focus_color", gold);
        b.AddThemeColorOverride("font_pressed_color", gold);
        b.AddThemeColorOverride("font_disabled_color", new Color(0.5f, 0.5f, 0.5f));
        b.AddThemeColorOverride("font_outline_color", Colors.Black);
        b.AddThemeConstantOverride("outline_size", 4);
        int bpx = P("font") is { } bf ? FontPx(bf, 1f) + 2 : 22;
        b.AddThemeFontSizeOverride("font_size", Fit(b, b.Text, bpx, b.Size.X));
        b.Pressed += () => Click(name);
        return b;
    }

    /// <summary>Pixel height for the engine's bitmap font names (interface_small, INTERFACE_ULTRASMALL, Dialog21, ...).</summary>
    /// <summary>Ukrainian captions are longer than the Russian ones the layouts were drawn for: shrink the font until the text fits the room it has.</summary>
    static int Fit(Control c, string text, int px, float maxW)
    {
        var f = c.GetThemeDefaultFont();
        while (px > 9 && f.GetStringSize(text, HorizontalAlignment.Left, -1, px).X + 8 > maxW) px--;
        return px;
    }

    static int FontPx(string font, float scale)
    {
        string f = font.ToLowerInvariant();
        float px = f.Contains("ultrasmall") ? 11 : f.Contains("small") ? 15 : f.Contains("title") ? 28 : f == "dialog21" ? 13 : f == "dialog2" ? 22 : 22;
        return Math.Max(8, (int)(px * scale));
    }

    static Color Plain(string? s, Color def)
    {
        if (s == null) return def;
        var p = s.Split(',').Select(x => int.TryParse(x.Trim(), out var v) ? v : 255).ToArray();
        return p.Length < 4 ? def : new Color(p[1] / 255f, p[2] / 255f, p[3] / 255f, p[0] / 255f);
    }

    /// <summary>BUTTON: a picture from the atlas (group + picture) that clicks; shows a gold glow on mouse-over.</summary>
    Control MakeIconButton(string name, Func<string, string?> P, float[] pos)
    {
        var b = new TextureButton
        {
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.Scale,
            Position = new Vector2(pos[0], pos[1]),
            Size = new Vector2(pos[2] - pos[0], pos[3] - pos[1]),
            FocusMode = FocusModeEnum.All,
            TextureNormal = Atlas.Get(P("group") ?? "ICONS", P("picture") ?? ""),
        };
        b.MouseEntered += () => b.Modulate = new Color(1.35f, 1.25f, 1f);
        b.MouseExited += () => b.Modulate = Colors.White;
        b.Pressed += () => Click(name);
        return b;
    }

    sealed partial class RectDraw : Control
    {
        public Color Top, Bottom;
        public override void _Draw()
        {
            DrawPolygon(new[] { Vector2.Zero, new Vector2(Size.X, 0), Size, new Vector2(0, Size.Y) }, new[] { Top, Top, Bottom, Bottom });
        }
    }

    Control MakeRectangle(Func<string, string?> P, float[] pos)
    {
        return new RectDraw
        {
            Position = new Vector2(pos[0], pos[1]),
            Size = new Vector2(pos[2] - pos[0], pos[3] - pos[1]),
            Top = Plain(P("topColor"), new Color(0, 0, 0.3f, 0.8f)),
            Bottom = Plain(P("bottomColor"), new Color(0, 0, 0.3f, 0.8f)),
            MouseFilter = MouseFilterEnum.Ignore,
        };
    }

    sealed partial class BorderDraw : Control
    {
        public Texture2D? Angle, Line;
        public override void _Draw()
        {
            if (Angle == null || Line == null) return;
            float t = 16;
            // edges: the line picture tiled along each side, corners with the angle picture rotated by 90 degrees steps
            void Edge(Vector2 from, float len, float rot)
            {
                DrawSetTransform(from, rot, Vector2.One);
                for (float x = t; x < len - t; x += Line.GetWidth())
                    DrawTextureRect(Line, new Rect2(x, 0, Math.Min(Line.GetWidth(), len - t - x), t), false);
            }
            Edge(Vector2.Zero, Size.X, 0);
            Edge(new Vector2(Size.X, 0), Size.Y, MathF.PI / 2);
            Edge(Size, Size.X, MathF.PI);
            Edge(new Vector2(0, Size.Y), Size.Y, -MathF.PI / 2);
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);
            DrawTextureRect(Angle, new Rect2(0, 0, t, t), false);
        }
    }

    Control MakeBounder(Func<string, string?> P, float[] pos)
    {
        string g = P("groupName") ?? "ICONS";
        var pics = (P("pictures") ?? "border angle,border line").Split(',').Select(x => x.Trim()).ToArray();
        return new BorderDraw
        {
            Position = new Vector2(pos[0], pos[1]),
            Size = new Vector2(pos[2] - pos[0], pos[3] - pos[1]),
            Angle = Atlas.Get(g, pics[0]),
            Line = Atlas.Get(g, pics.Length > 1 ? pics[1] : pics[0]),
            Modulate = Argb(P("color"), Colors.White),
            MouseFilter = MouseFilterEnum.Ignore,
        };
    }

    /// <summary>TITLE: the screen header strip with the caption (GameInterface.title is a key of the global strings).</summary>
    Control MakeTitle(Func<string, string?> P, float[] pos)
    {
        var box = new Control { Position = new Vector2(pos[0], pos[1]), Size = new Vector2(pos[2] - pos[0], pos[3] - pos[1]), MouseFilter = MouseFilterEnum.Ignore };
        var tiled = Atlas.Get(P("imgGroupName") ?? "ICONS", P("titleTiled") ?? "titleTiled");
        if (tiled != null)
        {
            var strip = new TextureRect { Texture = tiled, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
            box.AddChild(strip); strip.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }
        string key = Ent.Root.Find("title", false)?.Value ?? "";
        var lab = new Label
        {
            Text = key.Length == 0 ? "" : Tr(key),
            Size = box.Size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        lab.AddThemeFontSizeOverride("font_size", Fit(lab, lab.Text, FontPx(P("font") ?? "interface_title", Floats(P("fontScale"), 1)[0] is var s && s > 0 ? s : 1f), box.Size.X));
        lab.AddThemeColorOverride("font_color", Colors.White);
        lab.AddThemeColorOverride("font_outline_color", Colors.Black);
        lab.AddThemeConstantOverride("outline_size", 4);
        box.AddChild(lab); lab.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return box;
    }

    /// <summary>IMAGECOLLECTION: several atlas pictures; "picture = name,col:{a,r,g,b},pos:{x1,y1,x2,y2}".</summary>
    Control MakeCollection(IniFile.Section? sec, Func<string, string?> P)
    {
        var root = new Control { MouseFilter = MouseFilterEnum.Ignore };
        string group = P("groupName") ?? "ICONS";
        if (sec == null) return root;
        foreach (var line in sec.All("picture"))
        {
            string name = line.Split(',')[0].Trim();
            float[] rect = [0, 0, 0, 0];
            var color = Colors.White;
            int pi = line.IndexOf("pos:{", StringComparison.OrdinalIgnoreCase);
            if (pi >= 0) rect = Floats(line.Substring(pi + 5, line.IndexOf('}', pi) - pi - 5), 4);
            int ci = line.IndexOf("col:{", StringComparison.OrdinalIgnoreCase);
            if (ci >= 0) color = Argb(line.Substring(ci + 5, line.IndexOf('}', ci) - ci - 5), Colors.White);
            root.AddChild(new TextureRect
            {
                Texture = Atlas.Get(group, name),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                Position = new Vector2(rect[0], rect[1]),
                Size = new Vector2(rect[2] - rect[0], rect[3] - rect[1]),
                Modulate = color,
                MouseFilter = MouseFilterEnum.Ignore,
            });
        }
        return root;
    }

    /// <summary>"key:{a,b,c}" -> the numbers inside the braces.</summary>
    static float[] Braced(string line, string key, int n)
    {
        int i = line.IndexOf(key + ":{", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return new float[n];
        int j = line.IndexOf('}', i);
        return Floats(line.Substring(i + key.Length + 2, j - i - key.Length - 2), n);
    }

    static string? BracedText(string line, string key)
    {
        int i = line.IndexOf(key + ":", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int st = i + key.Length + 1;
        int e = line.IndexOfAny(new[] { ',', '}' }, st);
        return (e < 0 ? line.Substring(st) : line.Substring(st, e - st)).Trim();
    }

    /// <summary>STRINGCOLLECTION: "string = KEY,font:F,pos:{x,y},fc:{a,r,g,b},bc:{..},scale:S,state:{S}" - captions drawn at fixed places.</summary>
    Control MakeStringCollection(IniFile.Section? sec)
    {
        var root = new Control { MouseFilter = MouseFilterEnum.Ignore };
        if (sec == null) return root;
        var all = sec.All("string").ToList();
        var places = all.Select(l => Braced(l, "pos", 2)).ToList();
        bool isSpecialList = all.Any(l => l.StartsWith("Strength,", StringComparison.OrdinalIgnoreCase));
        foreach (var line in all)
        {
            string key = line.Split(',')[0].Trim();
            var pos = Braced(line, "pos", 2);
            // a caption has the room up to the next caption of the same row (the top menu: an icon sits left of every caption)
            float nextDx = places.Where(q => Math.Abs(q[1] - pos[1]) < 1f && q[0] > pos[0] + 1f).Select(q => q[0] - pos[0]).DefaultIfEmpty(float.MaxValue).Min();
            var fc = Braced(line, "fc", 4);
            float scale = float.TryParse(BracedText(line, "scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc) ? sc : 1f;
            var lab = new Label { Text = Tr(key), Position = new Vector2(pos[0], pos[1] - 2), MouseFilter = MouseFilterEnum.Ignore, Name = "cs_" + key };
            // state:{..}: C / R in it = the position is the centre / the right end of the caption (S = visible)
            string state = (BracedText(line, "state") ?? "").ToUpperInvariant();
            bool right = state.Contains('R'), centre = !right && state.Contains('C');
            float room = Math.Max(40, Math.Min(628 - pos[0], nextDx == float.MaxValue ? 628 - pos[0] : nextDx - (pos[1] < 0 ? 46f : 6f)));
            // character screen: the script puts the "-"/"+" buttons of the selected SPECIAL row at x 75..115 (character.c); the names start at x 23, so a long
            // (Ukrainian) name is fitted into the 52 units before the arrows instead of running under them
            if (isSpecialList && pos[0] > 15 && pos[0] < 30 && pos[1] > 60 && pos[1] < 235 && !right && !centre) room = 52f;
            if (right) room = Math.Max(60, pos[0] + 30);
            else if (centre) room = Math.Max(60, 2 * Math.Min(pos[0] + 30, 658 - pos[0]));
            int px = Fit(lab, lab.Text, FontPx(BracedText(line, "font") ?? "interface_normal", scale), room);
            if (isSpecialList && room <= 52f)       // below Fit's floor if the name still does not fit before the arrows
            {
                var fnt = lab.GetThemeDefaultFont();
                px = 13;                                       // one size for the whole list: the largest at which the longest name fits
                foreach (var l2 in all.Where(l => l.Contains("state:{S}") && Braced(l, "pos", 2)[0] > 15 && Braced(l, "pos", 2)[0] < 30 && Braced(l, "pos", 2)[1] > 60 && Braced(l, "pos", 2)[1] < 235))
                    while (px > 7 && fnt.GetStringSize(Tr(l2.Split(',')[0].Trim()), HorizontalAlignment.Left, -1, px).X > room - 1) px--;
            }
            lab.AddThemeFontSizeOverride("font_size", px);
            if (right || centre)
            {
                const float W = 400;
                lab.Size = new Vector2(W, 30);
                lab.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                lab.Position = new Vector2(right ? pos[0] - W : pos[0] - W / 2, pos[1] - 2);
                lab.SetMeta("aligned", true);
            }
            lab.AddThemeColorOverride("font_color", new Color(fc[1] / 255f, fc[2] / 255f, fc[3] / 255f, fc[0] / 255f));
            lab.AddThemeColorOverride("font_outline_color", Colors.Black);
            lab.AddThemeConstantOverride("outline_size", 3);
            root.AddChild(lab);
            collectionStrings[key] = lab;
            collectionLabels.Add(lab);
        }
        return root;
    }

    sealed partial class LinesDraw : Control
    {
        public readonly List<(Vector2 a, Vector2 b, Color c)> Lines = new();
        public override void _Draw() { foreach (var (a, b, c) in Lines) DrawLine(a, b, c, 1.2f); }
    }

    /// <summary>LINECOLLECTION: "line = (x1,y1)-(x2,y2),col:{a,r,g,b}".</summary>
    Control MakeLineCollection(IniFile.Section? sec)
    {
        var d = new LinesDraw { MouseFilter = MouseFilterEnum.Ignore };
        if (sec == null) return d;
        foreach (var line in sec.All("line"))
        {
            var nums = System.Text.RegularExpressions.Regex.Matches(line.Split("col:")[0], @"-?\d+(\.\d+)?")
                .Select(m => float.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (nums.Length < 4) continue;
            var c = Braced(line, "col", 4);
            d.Lines.Add((new Vector2(nums[0], nums[1]), new Vector2(nums[2], nums[3]), new Color(c[1] / 255f, c[2] / 255f, c[3] / 255f, c[0] / 255f)));
        }
        return d;
    }

    /// <summary>STATUSLINE: a thin bar (empty picture + filled part) used for experience/health/reputation.</summary>
    Control MakeStatusLine(Func<string, string?> P, float[] pos)
    {
        var box = new Control { Position = new Vector2(pos[0], pos[1]), Size = new Vector2(pos[2] - pos[0], Math.Max(pos[3] - pos[1], 4)), MouseFilter = MouseFilterEnum.Ignore };
        var empty = Atlas.Get("ICONS", "status line empty");
        if (empty != null) box.AddChild(new TextureRect { Texture = empty, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, Size = box.Size, MouseFilter = MouseFilterEnum.Ignore });
        return box;
    }

    readonly Dictionary<string, Label> collectionStrings = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Label> collectionLabels = new();

    ContextHelpView? ctxHelp;

    Control MakeContextHelp(IniFile.Section? sec, Func<string, string?> P, float[] pos)
    {
        var list = new List<(string, string)>();
        if (sec != null)
            foreach (var l in sec.All("helpstr"))
            {
                var p = l.Split(',');
                if (p.Length >= 2 && p[0].Trim().Length > 0) list.Add((p[0].Trim(), p[1].Trim()));
            }
        var fill = Plain(P("color"), new Color(13 / 255f, 11 / 255f, 63 / 255f));
        var bord = Plain(P("borderColor"), fill);
        var font = Plain(P("fontColor"), new Color(0.5f, 0.5f, 0.5f));
        var v = new ContextHelpView(pos[0], pos[1], pos[2], pos[3], fill, bord, bord != fill, font, FontPx(P("font") ?? "interface_normal", 0.8f), Floats(P("offset"), 1)[0],
            Floats(P("delay"), 1)[0], list, P("defhelp") ?? "", CurrentNode, Tr);
        ctxHelp = v;
        return v;
    }

    Control MakeFourImages(string name, Func<string, string?> P)
    {
        var rects = new Rect2[4];
        for (int i = 0; i < 4; i++)
        {
            var p = Floats(P("position" + (i + 1)), 4);
            rects[i] = new Rect2(p[0], p[1], p[2] - p[0], p[3] - p[1]);
        }
        return new FourImagesView(Ent, name, rects, P("oneString") ?? "", P("twoString") ?? "", (cmd, n) => Run(n, cmd, 0));
    }

    V FindSave(V[] d)
    {
        var list = SaveFiles.List();
        int idx = d.Length > 1 ? Conv.ToInt(d[1]) : 0;
        if (idx < 0 || idx >= list.Count) return V.Int(0);
        it.WriteRef(d[2], V.Str(list[idx].name));
        it.WriteRef(d[3], V.Int((int)Math.Min(list[idx].size, int.MaxValue)));
        return V.Int(1);
    }

    // ------------------------------------------------------------------ quest book
    Control? questTitleNode;
    Attr? questInfo;
    int questTop;
    float questLine = 28;
    string questIconGroup = "ICONS", questDoneIcon = "complete", questOpenIcon = "noncomplete";
    Label? questTextLabel;

    Control MakeQuestTitle(Func<string, string?> P, float[] pos)
    {
        var c = new Control { Position = new Vector2(pos[0], pos[1]), Size = new Vector2(pos[2] - pos[0], pos[3] - pos[1]), MouseFilter = MouseFilterEnum.Ignore };
        float ls = Floats(P("lineSpace"), 1)[0];
        questLine = ls > 0 ? ls : 28;
        questIconGroup = P("iconGroup") ?? "ICONS";
        questDoneIcon = P("completeIcon") ?? "complete";
        questOpenIcon = P("noncompleteIcon") ?? "noncomplete";
        questTitleNode = c;
        return c;
    }

    /// <summary>MSG_INTERFACE_SET_TITLE_STRINGS (msg, node, QuestInfo, top): shows the list of quests starting at `top`.</summary>
    public V SetQuestTitles(V[] a)
    {
        questInfo = a.Length > 2 ? a[2].Attr : null;
        questTop = a.Length > 3 ? Conv.ToInt(a[3]) : 0;
        GD.Print($"[xinterface] quest titles: {questInfo?.Count} quests, top {questTop}, node={(questTitleNode != null)}");
        RefreshQuestTitles();
        return V.Void;
    }

    void RefreshQuestTitles()
    {
        if (questTitleNode == null) return;
        foreach (var ch in questTitleNode.GetChildren()) ch.QueueFree();
        if (questInfo == null) return;
        int rows = Math.Max(1, (int)(questTitleNode.Size.Y / questLine));
        int lang = it.Language?.Open("QuestBook.txt") ?? -1;
        for (int r = 0; r < rows; r++)
        {
            var q = questInfo.ChildAt(questTop + r);
            if (q == null) break;
            bool done = q.Get("Complete") != null;
            string title = it.Language?.Convert(lang, q.Name + "_title") ?? q.Name;
            if (string.IsNullOrEmpty(title)) title = q.Name;
            var row = new Button { Flat = true, Position = new Vector2(0, r * questLine), Size = new Vector2(questTitleNode.Size.X, questLine), Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None };
            var empty = new StyleBoxEmpty();
            foreach (var sname in new[] { "normal", "pressed", "focus", "disabled" }) row.AddThemeStyleboxOverride(sname, empty);
            row.AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.18f) });
            row.Text = "      " + title.Replace("\\n", " ");
            row.AddThemeFontSizeOverride("font_size", 15);
            row.AddThemeColorOverride("font_color", done ? new Color(0.5f, 0.5f, 0.5f) : Colors.White);
            row.AddThemeColorOverride("font_hover_color", new Color(1f, 0.9f, 0.5f));
            var icon = Atlas.Get(questIconGroup, done ? questDoneIcon : questOpenIcon);
            if (icon != null) row.AddChild(new TextureRect { Texture = icon, Size = new Vector2(questLine - 4, questLine - 4), Position = new Vector2(2, 2), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore });
            int idx = r;
            row.Pressed += () => it.FireEvent("QuestActivate", new List<V> { V.Int(idx) });
            questTitleNode.AddChild(row);
        }
        it.Language?.Close(lang);
    }

    Control MakeQuestText(float[] pos)
    {
        var c = new Control { Position = new Vector2(pos[0], pos[1]), Size = new Vector2(pos[2] - pos[0], pos[3] - pos[1]), MouseFilter = MouseFilterEnum.Ignore, ClipContents = true };
        questTextLabel = new Label { Size = c.Size, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = MouseFilterEnum.Ignore };
        questTextLabel.AddThemeFontSizeOverride("font_size", 15);
        questTextLabel.AddThemeColorOverride("font_color", Colors.White);
        questTextLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        questTextLabel.AddThemeConstantOverride("outline_size", 2);
        c.AddChild(questTextLabel);
        return c;
    }

    static readonly System.Text.RegularExpressions.Regex TagRx = new(@"#([sdfSDF])([^#\s]+)#");

    /// <summary>MSG_INTERFACE_INIT_QTEXT_SHOW (node, QuestInfo, index): composes the records of one quest ("@date@id,@date@id,...") from QuestBook.txt.</summary>
    void ShowQuestText(Attr? info, int qnum)
    {
        if (questTextLabel == null) return;
        var q = info?.ChildAt(qnum);
        if (q == null) { questTextLabel.Text = ""; return; }
        int lang = it.Language?.Open("QuestBook.txt") ?? -1;
        var sb = new System.Text.StringBuilder();
        foreach (var rec in (q.Get("Text")?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(rec, "^@([^@]*)@(.*)$");
            if (!m.Success) continue;
            string date = m.Groups[1].Value, id = m.Groups[2].Value;
            string dateKey = "at" + new string(date.Where(char.IsDigit).ToArray());
            var prm = q.Find(id + "." + dateKey, false);
            string text = it.Language?.Convert(lang, q.Name + "_" + id) ?? "";
            text = StormScript.Runtime.Builtins.AssembleString(text, prm);
            sb.Append(date).Append('\n').Append(text.Trim()).Append("\n\n");
        }
        questTextLabel.Text = sb.ToString();
        it.Language?.Close(lang);
    }

    /// <summary>CHANGER: rectangles "place = x1,y1,x2,y2"; without any place it is only a focus target (the transfer screens' quantity changer).</summary>
    Control MakeChanger(string name, IniFile.Section? sec)
    {
        var rects = new List<float[]>();
        if (sec != null) foreach (var line in sec.All("place")) { var r = Floats(line, 4); if (r[2] > r[0]) rects.Add(r); }
        if (rects.Count == 0) return new Control { MouseFilter = MouseFilterEnum.Ignore };
        return new ChangerView(name, rects, () => Ent.Root.Find(name, false), (n, c) => Run(n, c, 0));
    }

    Control MakeFormattedList(string name, Func<string, string?> P, float[] pos)
        => new FormattedListView(name, pos[0], pos[1], pos[2], pos[3], Floats(P("lineSpace"), 1)[0], Floats(P("leftoffset"), 1)[0], Floats(P("upOffset"), 1)[0],
            (n, a, b) => it.FireEvent("evntUpdateFormtText", new List<V> { V.Str(n), V.Int(a), V.Int(b) }),
            (n, i) => it.FireEvent("FormatedTextActivate", new List<V> { V.Str(n), V.Int(i) }),
            (n, p) => it.FireEvent("SetScrollerPos", new List<V> { V.Str(n), V.Float(p) }));

    Control MakeFormatted(Func<string, string?> P, float[] pos)
    {
        if (string.Equals(P("font"), "KEYBOARD_SYMBOL", StringComparison.OrdinalIgnoreCase))        // the key caps of the control pages
            return new KeyGlyphLabel(Floats(P("lineSpace"), 1)[0], 12f) { Position = new Vector2(pos[0], pos[1]), Size = new Vector2(pos[2] - pos[0], pos[3] - pos[1]) };
        string al = (P("alignment") ?? "center").ToLowerInvariant();
        var lab = new Label
        {
            Position = new Vector2(pos[0], pos[1]),
            Size = new Vector2(pos[2] - pos[0], pos[3] - pos[1]),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = al == "left" ? HorizontalAlignment.Left : al == "right" ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            ClipText = true,
        };
        float sc = Floats(P("fontScale"), 1)[0];
        lab.AddThemeFontSizeOverride("font_size", FontPx(P("font") ?? "interface_normal", sc > 0 ? sc : 1f));
        float ls = Floats(P("lineSpace"), 1)[0];
        if (ls > 0) lab.AddThemeConstantOverride("line_spacing", Math.Max(0, (int)ls - FontPx(P("font") ?? "interface_normal", sc > 0 ? sc : 1f) - 4));
        lab.AddThemeColorOverride("font_color", Plain(P("Color"), Colors.White));
        lab.AddThemeColorOverride("font_outline_color", Colors.Black);
        lab.AddThemeConstantOverride("outline_size", 2);
        return lab;
    }

    // ------------------------------------------------------------------ interface -> script
    /// <summary>Test hook: a real left mouse click (move, press, release through the input system) in the middle of a node.</summary>
    public void MouseClick(string node)
    {
        if (!nodes.TryGetValue(node, out var c) || !c.IsVisibleInTree()) { GD.Print($"[mouse] {node}: not visible"); return; }
        var pos = GetViewport().GetFinalTransform() * c.GetGlobalRect().GetCenter();
        GD.Print($"[mouse] click {node} at {pos}");
        Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = pos, GlobalPosition = pos, ButtonMask = MouseButtonMask.Left });
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = pos, GlobalPosition = pos });
    }

    public void Click(string node)
    {
        GD.Print($"[xinterface] click {node}");
        Run(node, "click", 0);
    }

    /// <summary>Executes command <paramref name="cmd"/> of <paramref name="node"/>: tells the script, runs the ini's event:/com:/select: entries.</summary>
    void Run(string node, string cmd, int depth)
    {
        if (depth > 4) return;
        it.FireEvent("ievnt_command", new List<V> { V.Str(cmd), V.Str(node) });
        if (nodes.TryGetValue(node, out var sl4) && sl4 is SliderView slv)
        {
            if (cmd.Equals("leftstep", StringComparison.OrdinalIgnoreCase)) slv.Step(-0.02f);
            else if (cmd.Equals("rightstep", StringComparison.OrdinalIgnoreCase)) slv.Step(0.02f);
            else if (cmd.Equals("speedleft", StringComparison.OrdinalIgnoreCase)) slv.Step(-0.1f);
            else if (cmd.Equals("speedright", StringComparison.OrdinalIgnoreCase)) slv.Step(0.1f);
        }
        if (nodes.TryGetValue(node, out var ch4) && ch4 is ChangerView chv)
        {
            if (cmd.Equals("upstep", StringComparison.OrdinalIgnoreCase)) chv.Step(-1);
            else if (cmd.Equals("downstep", StringComparison.OrdinalIgnoreCase)) chv.Step(1);
        }
        if (nodes.TryGetValue(node, out var fl4) && fl4 is FormattedListView flv)
        {
            if (cmd.Equals("upstep", StringComparison.OrdinalIgnoreCase)) flv.Move(-1);
            else if (cmd.Equals("downstep", StringComparison.OrdinalIgnoreCase)) flv.Move(1);
            else if (cmd.Equals("speedup", StringComparison.OrdinalIgnoreCase)) flv.Move(-4);
            else if (cmd.Equals("speeddown", StringComparison.OrdinalIgnoreCase)) flv.Move(4);
            else if (cmd.Equals("activate", StringComparison.OrdinalIgnoreCase)) flv.Activate();
        }
        if (questTitleNode != null && nodes.TryGetValue(node, out var qtn) && ReferenceEquals(qtn, questTitleNode))
        {
            // xi_quest_titles.cpp CommandExecute: the script moves the list with QuestTopChange (the engine only reports where the list was pushed)
            int rows = Math.Max(1, (int)(questTitleNode.Size.Y / questLine));
            int d = cmd.ToLowerInvariant() switch { "upstep" => -1, "downstep" => 1, "speedup" => -rows, "speeddown" => rows, _ => 0 };
            if (d != 0) it.FireEvent("QuestTopChange", new List<V> { V.Int(d) });
        }
        if (nodes.TryGetValue(node, out var fn4) && fn4 is FourImagesView fv)
        {
            if (cmd.Equals("leftstep", StringComparison.OrdinalIgnoreCase)) fv.Move(-1, 0);
            else if (cmd.Equals("rightstep", StringComparison.OrdinalIgnoreCase)) fv.Move(1, 0);
            else if (cmd.Equals("upstep", StringComparison.OrdinalIgnoreCase)) fv.Move(0, -1);
            else if (cmd.Equals("downstep", StringComparison.OrdinalIgnoreCase)) fv.Move(0, 1);
        }
        if (nodes.TryGetValue(node, out var n) && n is ScrollImageView sv)
        {
            if (cmd.Equals("leftstep", StringComparison.OrdinalIgnoreCase)) sv.Move(-1);
            else if (cmd.Equals("rightstep", StringComparison.OrdinalIgnoreCase)) sv.Move(1);
            // Shift + arrow: up to a page (the pictures shown) in one go. The ini's com:speedright:... of the arrow buttons only calls the strip's own CommandExecute,
            // so it is a key action: a click on an arrow (nested commands) must not jump a page
            else if (depth == 0 && cmd.Equals("speedleft", StringComparison.OrdinalIgnoreCase)) sv.Move(-sv.PageSize);
            else if (depth == 0 && cmd.Equals("speedright", StringComparison.OrdinalIgnoreCase)) sv.Move(sv.PageSize);
        }
        if (!cmds.TryGetValue(node, out var list)) return;
        foreach (var c in list.Where(c => c.Name.Equals(cmd, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (c.Select != null) SelectNode(c.Select);
            foreach (var (cc, cn) in c.Coms) Run(cn, cc, depth + 1);
            foreach (var e in c.Events) it.FireEvent(e);
        }
    }

    string selectedNode = "";

    /// <summary>The engine's "current node": the one the player (or the script, "select:" in the ini) has selected, even when it is not a focusable widget or still hidden.</summary>
    void SelectNode(string name)
    {
        selectedNode = name;
        if (nodes.TryGetValue(name, out var c) && c.Visible && c.FocusMode != FocusModeEnum.None && !(c is Button b && b.Disabled)) c.GrabFocus();
        else if (c != null && GetViewport()?.GuiGetFocusOwner() is Control f && f != c) f.ReleaseFocus();   // a picture/rectangle/text node is current now, not the last focused button
    }

    string CurrentNode()
    {
        if (GetViewport()?.GuiGetFocusOwner() is Control f && nodes.ContainsKey(f.Name.ToString())) { selectedNode = f.Name.ToString(); return selectedNode; }
        return selectedNode;
    }

    readonly List<(Control node, int flags, float[] pos)> snapped = new();
    Vector2 lastSnapExtra = new(-1, -1);

    /// <summary>Nodes that follow the screen edges (bAbsoluteRectangle) stretch over the whole visible area of a wide window; backgrounds keep their proportions and cover it.</summary>
    void ApplySnaps()
    {
        var ex = UiLayout.Extra(this);
        if (ex == lastSnapExtra) return;
        lastSnapExtra = ex;
        foreach (var (n, f, p) in snapped)
        {
            if (!GodotObject.IsInstanceValid(n)) continue;
            float x1 = (f & 1) != 0 ? -40 - ex.X : p[0], y1 = (f & 2) != 0 ? -30 - ex.Y : p[1];
            float x2 = (f & 4) != 0 ? 680 + ex.X : p[2], y2 = (f & 8) != 0 ? 510 + ex.Y : p[3];
            n.CustomMinimumSize = Vector2.Zero;
            n.Position = new Vector2(x1, y1); n.Size = new Vector2(x2 - x1, y2 - y1);
            if (n is TextureRect tr && f == 15) { tr.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered; tr.ClipContents = true; }
        }
    }

    // ------------------------------------------------------------------ LOCK_NODE: modal windows and text input
    readonly List<string> locks = new();
    readonly Dictionary<Control, (MouseFilterEnum mf, FocusModeEnum fm)> lockSaved = new();
    string preLockNode = "";

    /// <summary>MSG_INTERFACE_LOCK_NODE 1/2: while the lock list is not empty only its nodes take the mouse, the focus and the keyboard steps.</summary>
    void LockNode(string name, bool first)
    {
        if (first)
        {
            if (locks.Count == 0) preLockNode = CurrentNode();
            locks.Clear();
        }
        if (!locks.Contains(name, StringComparer.OrdinalIgnoreCase)) locks.Add(name);
        ApplyLocks();
        GD.Print($"[xinterface] lock: {string.Join(",", locks)}");
    }

    void Unlock()
    {
        if (locks.Count == 0) return;
        bool wasLocked = locks.Contains(selectedNode, StringComparer.OrdinalIgnoreCase);
        locks.Clear();
        ApplyLocks();
        GD.Print("[xinterface] unlock");
        // the locked node (a text input, a confirmation window) is about to be hidden: the selection goes back to where it was,
        // unless the script has already chosen another node
        if (!wasLocked && nodes.TryGetValue(selectedNode, out var cur) && cur.Visible) SelectNode(selectedNode);
        else if (preLockNode.Length > 0) SelectNode(preLockNode);
    }

    bool Locked(string name) => locks.Count == 0 || locks.Contains(name, StringComparer.OrdinalIgnoreCase);

    void ApplyLocks()
    {
        foreach (var (name, n) in nodes)
        {
            bool open = Locked(name);
            foreach (var c in SelfAndControls(n))
            {
                if (open)
                {
                    if (lockSaved.Remove(c, out var s) && GodotObject.IsInstanceValid(c)) { c.MouseFilter = s.mf; c.FocusMode = s.fm; }
                }
                else if (!lockSaved.ContainsKey(c))
                {
                    lockSaved[c] = (c.MouseFilter, c.FocusMode);
                    c.MouseFilter = MouseFilterEnum.Ignore;
                    c.FocusMode = FocusModeEnum.None;
                }
            }
        }
        if (GetViewport()?.GuiGetFocusOwner() is Control f && f.FocusMode == FocusModeEnum.None) f.ReleaseFocus();
    }

    static IEnumerable<Control> SelfAndControls(Control c)
    {
        yield return c;
        foreach (var ch in c.GetChildren())
            if (ch is Control cc) foreach (var x in SelfAndControls(cc)) yield return x;
    }

    /// <summary>The node that takes typed text now: a visible EDITBOX / KEYCHOOSER that is locked or current.</summary>
    Control? TextTarget()
    {
        foreach (var (name, n) in nodes)
        {
            if (n is not (EditBoxView or KeyChooserView) || !n.Visible) continue;
            if ((locks.Count > 0 && locks.Contains(name, StringComparer.OrdinalIgnoreCase)) || name.Equals(selectedNode, StringComparison.OrdinalIgnoreCase)) return n;
        }
        return null;
    }

    public override void _Input(InputEvent e)
    {
        // the mouse wheel over the quest titles scrolls the list through the script (QuestTopChange)
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wh && questTitleNode != null && questTitleNode.IsVisibleInTree()
            && questTitleNode.GetGlobalRect().HasPoint(wh.GlobalPosition))
        {
            it.FireEvent("QuestTopChange", new List<V> { V.Int(wh.ButtonIndex == MouseButton.WheelUp ? -1 : 1) });
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right or MouseButton.Middle } mbk && TextTarget() is KeyChooserView mkc && mkc.Codes != null)
        {
            // a mouse button is a key of the list too (VK_LBUTTON = 1, VK_RBUTTON = 2, VK_MBUTTON = 4)
            int midx = mkc.IndexOf(mbk.ButtonIndex == MouseButton.Left ? 1 : mbk.ButtonIndex == MouseButton.Right ? 2 : 4);
            GetViewport().SetInputAsHandled();
            if (midx >= 0) it.FireEvent("evntKeyChoose", new List<V> { V.Int(midx), V.Int(0) });
            return;
        }
        if (e is not InputEventKey { Pressed: true } k || GetChildCount() == 0) return;
        var target = TextTarget();
        if (target == null) return;
        GetViewport().SetInputAsHandled();       // the focused button must not see Enter/Space, the game no controls
        if (target is EditBoxView eb)
        {
            var ev = eb.Key(k);
            if (ev != null) { GD.Print($"[xinterface] editbox {eb.Name}: {ev} '{eb.Text}'"); it.FireEvent(ev, new List<V> { V.Str(eb.Name.ToString()) }); }
            return;
        }
        var kc = (KeyChooserView)target;
        // the addon's text input (key_hook.c) appends characters from its own En/Ru table (switched by Ctrl, no Ukrainian letters) to
        // strings.StrKeyHook ("text|"); printable characters are inserted here from the system layout instead, in the script's format
        var hook = Ent.Root.Find("strings.StrKeyHook", false);
        if (hook != null && k.Unicode >= 32 && !k.CtrlPressed && !k.AltPressed)
        {
            string s = hook.Value ?? "";
            if (s.EndsWith('|')) s = s.Substring(0, s.Length - 1);
            if (s.Length < 26) s += char.ConvertFromUtf32((int)k.Unicode);
            hook.Value = s + "|";
            return;
        }
        if (hook != null && k.Keycode == Key.Escape && it.Funcs.ContainsKey("Key_HideTextWindow"))
        {
            GD.Print("[xinterface] text input cancelled");
            it.CallTop("Key_HideTextWindow");     // the script's own close (no rename); the original had no cancel key
            return;
        }
        if (k.Echo && k.Keycode != Key.Backspace) return;
        int idx = kc.IndexOf(KeyChooserView.Vk(k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode));
        if (idx >= 0)
        {
            if (hook != null && k.Keycode is Key.Enter or Key.KpEnter) GD.Print($"[xinterface] text input: '{hook.Value?.TrimEnd('|')}'");
            it.FireEvent("evntKeyChoose", new List<V> { V.Int(idx), V.Int(0) });
        }
    }

    readonly Dictionary<string, (uint min, uint max)> blinds = new(StringComparer.OrdinalIgnoreCase);
    double blindTime;

    void DeleteScrollImage(string node, int index)
    {
        var r = Ent.Root.Find(node, false);
        if (r == null || index < 0) return;
        int n = (int)Conv.ParseFloatPrefix(r.Find("ListSize", false)?.Value);
        if (index >= n) return;
        // pic(index+1) goes away, the later ones move down by one
        for (int i = index + 2; i <= n; i++)
        {
            var dst = r.GetOrCreate("pic" + (i - 1));
            dst.Clear();
            if (r.Get("pic" + i) is { } src) dst.CopyFrom(src);
        }
        r.Remove("pic" + n);
        r.Find("ListSize", true)!.Value = (n - 1).ToString();
        int cur = (int)Conv.ParseFloatPrefix(r.Find("current", false)?.Value);
        if (cur >= n - 1) r.Find("current", true)!.Value = Math.Max(0, n - 2).ToString();
        if (nodes.TryGetValue(node, out var c) && c is ScrollImageView sv) sv.Refresh();
    }

    void Step(string command)
    {
        if (System.Environment.GetEnvironmentVariable("SEADOGS_NODEMSG") != null) GD.Print($"[step] {command} current='{CurrentNode()}'");
        if (locks.Count > 0 && !Locked(CurrentNode()))      // a modal window: the steps go to its first node
        {
            if (nodes.TryGetValue(locks[0], out var ln) && ln.Visible) Run(locks[0], command, 0);
            return;
        }
        if (CurrentNode() is { Length: > 0 } cn && nodes.TryGetValue(cn, out var cnode) && cnode.Visible) Run(cn, command, 0);
        else foreach (var kv in nodes) if (kv.Value is ScrollImageView || kv.Value.GetType().Name.StartsWith("FourImages")) { Run(kv.Key, command, 0); break; }
    }

    Control MakeScrollImage(string name, Func<string, string?> P, float[] pos)
    {
        var sz = Floats(P("imageSize"), 2);
        var v = new ScrollImageView(Ent, name, pos[0], pos[1], pos[2], pos[3], sz[0] > 0 ? sz[0] : 128, sz[1] > 0 ? sz[1] : 128, (cmd, n) => Run(n, cmd, 0));
        if (P("centerXoffset") is { Length: > 0 } cxo) v.CenterX = pos[0] + Conv.ParseFloatPrefix(cxo);         // the chosen picture's centre (screen units), e.g. between the arrows of the chest lists
        v.Translate = Tr;
        for (int k = 1; k <= 2; k++)
        {
            if (!(P(k == 1 ? "UseOneString" : "UseTwoString") is { Length: > 0 } use && Conv.ParseFloatPrefix(use) != 0)) continue;
            float sc = P("scale" + k) is { Length: > 0 } sct ? Conv.ParseFloatPrefix(sct) : 1f;
            string? fnt = P("font" + k);
            var cap = new ScrollImageView.CaptionStyle
            {
                Px = string.IsNullOrEmpty(fnt) ? Math.Max(8, (int)(16 * sc)) : FontPx(fnt, sc),
                X = Conv.ParseFloatPrefix(P("dwXOffset" + k) ?? "0"),
                Y = Conv.ParseFloatPrefix(P("dwYOffset" + k) ?? (k == 1 ? "92" : "0")),
            };
            if (P("dwForeColor" + k) is { Length: > 0 } fcs) cap.Fore = Plain(fcs, Colors.White);
            if (k == 1) v.Cap1 = cap; else v.Cap2 = cap;
        }
        return v;
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k) return;
        if (GetChildCount() == 0) return;          // the view outlives its screens: with nothing shown it must not swallow the game's keys (Space, Enter, arrows)
        // xinterface.cpp: with Shift held (ILeftShift / IRightShift) the arrows are the "speed" actions (page jumps in the strips, +10 in the quantity bars)
        if (k.Keycode == Key.Up) { Step(k.ShiftPressed ? "speedup" : "upstep"); GetViewport().SetInputAsHandled(); }
        else if (k.Keycode == Key.Down) { Step(k.ShiftPressed ? "speeddown" : "downstep"); GetViewport().SetInputAsHandled(); }
        else if (k.Keycode == Key.Left) { Step(k.ShiftPressed ? "speedleft" : "leftstep"); GetViewport().SetInputAsHandled(); }
        else if (k.Keycode == Key.Right) { Step(k.ShiftPressed ? "speedright" : "rightstep"); GetViewport().SetInputAsHandled(); }
        else if (k.Keycode is Key.Enter or Key.KpEnter or Key.Space) { Step("activate"); GetViewport().SetInputAsHandled(); }
        else if (k.Keycode == Key.Escape) { it.FireEvent("InterfaceBreak"); }
    }

    double gameTimeAcc;

    /// <summary>xinterface.cpp IncrementGameTime: while screens are shown the play time counts in seconds; every second the event ievent_SetGameTime (hour, min, sec) tells the script
    /// (InterfaceStates.GameTime, which the engine reads back when it starts).</summary>
    void TickGameTime(double delta)
    {
        if (GetChildCount() == 0) return;
        gameTimeAcc += delta;
        if (gameTimeAcc < 1.0) return;
        gameTimeAcc -= 1.0;
        if (!it.Globals.TryGetValue("InterfaceStates", out var st) || st.Val.Attr is not { } root) return;
        int H = (int)Conv.ParseFloatPrefix(root.Find("GameTime.hour", false)?.Value), M = (int)Conv.ParseFloatPrefix(root.Find("GameTime.min", false)?.Value), S = (int)Conv.ParseFloatPrefix(root.Find("GameTime.sec", false)?.Value) + 1;
        if (S >= 60) { M++; S -= 60; }
        if (M >= 60) { H++; M -= 60; }
        it.FireEvent("ievent_SetGameTime", new List<V> { V.Int(H), V.Int(M), V.Int(S) });
    }

    public override void _Process(double delta)
    {
        TickGameTime(delta);
        Position = new Vector2(40, 30) + UiLayout.Extra(this);
        ApplySnaps();
        foreach (var (c, speed) in spinners) c.Rotation += speed * (float)delta;
        // scripts change GameInterface.strings.<name> after the label was created (hero name, type, ...)
        foreach (var (name, lab) in strings)
        {
            string text = Ent.Root.Find("strings." + name, false)?.Value ?? "";
            if (lab.Text != text) { lab.Text = text; RefitString(name); }
        }
        // the same for the script's own images: it changes pictures.<name>.tex/pic after placing them (faces of the save list ...)
        foreach (var (name, img) in dynImages)
        {
            string tex = Ent.Root.Find("pictures." + name + ".tex", false)?.Value ?? "", pic = Ent.Root.Find("pictures." + name + ".pic", false)?.Value ?? "";
            if (dynKeys.TryGetValue(name, out var k) && k == tex + "|" + pic) continue;
            dynKeys[name] = tex + "|" + pic;
            img.Texture = Atlas.Get(tex, pic);
        }
        // SET_BLIND pictures pulse between two colours (the ship screen's selected state picture); 0x80 = the texture's own colour
        blindTime += delta;
        float bt = 0.5f + 0.5f * MathF.Sin((float)blindTime * MathF.PI);
        foreach (var (name, (mn, mx)) in blinds)
        {
            if (!dynImages.TryGetValue(name, out var img)) continue;
            float Ch(int sh) => (((mn >> sh) & 255) + (((int)((mx >> sh) & 255)) - (int)((mn >> sh) & 255)) * bt) / 128f;
            img.Modulate = new Color(Ch(16), Ch(8), Ch(0), Math.Min(1f, Ch(24) * 128f / 255f));
        }
    }

    readonly Dictionary<string, string> dynKeys = new();
}
