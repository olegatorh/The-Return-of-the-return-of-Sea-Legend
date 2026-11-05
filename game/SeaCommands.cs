using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>One choice of the command menu: icon, caption, enabled flag and what it means.</summary>
public sealed class HudMenuItem
{
    public Texture2D? Icon, IconSelected;
    public string Label = "";
    public bool Enabled = true;
    public string Id = "";            // command event / sail state / ...
    public int Target = -1;           // character index or charge number
    public string Loc = "";
}

public sealed class HudMenu
{
    public bool Open;
    public string Title = "";
    public List<HudMenuItem> Items = new();
    public int Selected;
}

/// <summary>
/// The command menu of the sea (the original "battle_interface" icon row). The script owns everything: which commands exist and are enabled
/// (BattleInterface.Commands.*, refreshed by the event BI_SetPossibleCommands), what a command needs next (event BI_CommandEndChecking returns
/// the BI_COMMODE_* flags: nothing / a ship to aim at / a gun charge / a sail state ...) and what it does (event BI_LaunchCommand).
/// The engine shows the choices and reports the player's pick.
/// </summary>
public partial class SeaView
{
    const int ModeMyShip = 1, ModeEnemyShip = 2, ModeCommand = 32, ModeLand = 64, ModeAllLocator = 128, ModeCharge = 256, ModeUser = 512, ModeAbility = 1024, ModeFriendShip = 2048, ModeNeutralShip = 4096;

    enum MenuLevel { None, Ship, Command, Target, Charge, Speed, Ability }
    MenuLevel menuLevel;
    int menuChar = -1;                 // the character whose ship is being commanded
    string menuCommand = "";           // the event name of the chosen command
    readonly List<HudMenuItem> menuItems = new();
    int menuSel;
    string menuTitle = "";
    string? menuNote;                  // short remark shown when the command cannot be completed

    public bool CommandsOpen => menuLevel != MenuLevel.None;

    /// <summary>The HUD is hidden (and the command menu dropped) while a location, the world map or an interface owns the screen.</summary>
    /// <summary>
    /// Boarding keeps the sea alive under a deck/cabin location (the script freezes the SEA_EXECUTE/SEA_REALIZE layers, AIAbordage.c): ships, island and forts
    /// are hidden while a location is loaded and come back with the sea afterwards.
    /// </summary>
    public void HideWorld(bool hidden)
    {
        foreach (var n in GetChildren().OfType<Node3D>())
            if (n != Cam) n.Visible = !hidden;
    }

    public void SuspendHud(bool suspended)
    {
        hud.Suspended = suspended;
        if (suspended && menuLevel != MenuLevel.None) CloseCommands();
    }

    Attr? BI => it.Globals.TryGetValue("BattleInterface", out var b) ? b.Val.Attr : null;

    /// <summary>Texture of an icon of the command textures list (BattleInterface.CommandTextures.list.t&lt;N&gt;: file, columns, rows); the right half of the file holds the highlighted copy.</summary>
    // BattleInterface.CommandTextures.list is deleted by the script after the engine class has read it, so the table (BattleInterface.c SetParameterData) is repeated here
    static readonly (string file, int xs, int ys)[] CmdTex =
    {
        ("cicons_charge.tga", 1, 4), ("cicons_command.tga", 4, 4), ("ships_16.tga", 4, 4), ("ships_1.tga", 1, 1), ("ships_2.tga", 1, 2), ("SailControls.tga", 4, 1),
        ("sea_abilitys.tga", 4, 2), ("scull.tga", 1, 1), ("small_nations.tga", 8, 1), ("Artefact.tga", 1, 1), ("08shs_02.tga", 1, 1), ("LandCommands.tga", 2, 4), ("LandTarget4.tga", 8, 1), ("cicons_locators.tga", 4, 1),
    };

    /// <summary>Icon <paramref name="pic"/> (row by row) of command texture <paramref name="tex"/>; a file twice as wide as its cells holds the highlighted copy in its right half.</summary>
    Texture2D? CmdIcon(int tex, int pic, bool highlighted)
    {
        if (tex < 0 || tex >= CmdTex.Length) return null;
        var (file, xs, ys) = CmdTex[tex];
        var texture = GameAssets.Texture("battle_interface\\" + file);
        if (texture == null) return null;
        float w = texture.GetWidth(), h = texture.GetHeight(), cw = w / xs, ch = h / ys;
        bool doubled = cw / ch > 1.5f;
        if (doubled) cw /= 2;
        float x = (pic % xs) * cw + (doubled && highlighted ? w / 2 : 0), y = (pic / xs) * ch;
        return new AtlasTexture { Atlas = texture, Region = new Rect2(x, y, cw, ch) };
    }

    Texture2D? ChargeIcon(int n, bool hi) => CmdIcon(0, n, hi);

    // ------------------------------------------------------------------ opening / closing
    public void OpenCommands()
    {
        if (!Active || ships.FirstOrDefault(s => s.IsPlayer) is not { } p) return;
        var squad = Companions(p);
        if (squad.Count > 1) { ShowShips(squad, "Оберіть корабель"); menuLevel = MenuLevel.Ship; }
        else ShowCommands(p.Index);
    }

    void CloseCommands() { menuLevel = MenuLevel.None; menuItems.Clear(); menuNote = null; }

    List<ShipRt> Companions(ShipRt p)
    {
        var list = new List<ShipRt> { p };
        int q = Math.Max(1, Conv.ToInt(it.CallTop("GetCompanionQuantity", V.A(p.Chr))));
        for (int i = 1; i < q && i < 6; i++)
        {
            int idx = Conv.ToInt(it.CallTop("GetCompanionIndex", V.A(p.Chr), V.Int(i)));
            if (ships.FirstOrDefault(x => x.Index == idx && !x.Dying) is { } cs) list.Add(cs);
        }
        return list;
    }

    HudMenuItem ShipItem(ShipRt s, float? dist = null)
    {
        var hs = MakeHudShip(s, dist);
        var icon = Atlas.Get("SHIPS16", hs.Type) ?? Atlas.Get("Ships16", hs.Type);
        return new HudMenuItem { Icon = icon, IconSelected = icon, Label = dist is { } d ? $"{hs.Name}  {d:0} м" : hs.Name, Id = "ship", Target = s.Index };
    }

    void ShowShips(IEnumerable<ShipRt> list, string title)
    {
        menuItems.Clear(); menuSel = 0; menuTitle = title;
        foreach (var s in list) menuItems.Add(ShipItem(s, ships.FirstOrDefault(x => x.IsPlayer) is { } pl && !ReferenceEquals(pl, s) ? Dist(pl, s) : null));
    }

    // ------------------------------------------------------------------ levels
    void ShowCommands(int charIdx)
    {
        menuChar = charIdx; menuLevel = MenuLevel.Command; menuSel = 0;
        it.FireEvent("BI_SetPossibleCommands", new List<V> { V.Int(charIdx) });          // the script sets Commands.*.enable for this ship
        menuItems.Clear();
        var cmds = BI?.Find("Commands", false);
        var chr = ships.FirstOrDefault(s => s.Index == charIdx)?.Chr;
        menuTitle = chr?.Find("Ship.Name", false)?.Value ?? chr?.Get("id")?.Value ?? "";
        if (cmds != null)
            for (int i = 0; i < cmds.Count; i++)
            {
                var c = cmds.ChildAt(i);
                if (c == null || c.Name == "Cancel" || c.Get("event") == null) continue;
                if ((int)Conv.ParseFloatPrefix(c.Get("enable")?.Value) == 0) continue;
                int tex = (int)F(c, "texNum", 1), pic = (int)F(c, "picNum", 0);
                menuItems.Add(new HudMenuItem { Icon = CmdIcon(tex, pic, false), IconSelected = CmdIcon(tex, pic, true), Label = c.Get("note")?.Value ?? c.Name, Id = c.Get("event")!.Value ?? "" });
            }
        if (menuItems.Count == 0) menuNote = "Немає доступних команд";
    }

    public void CommandKey(Key k)
    {
        if (menuLevel == MenuLevel.None) return;
        int n = menuItems.Count;
        switch (k)
        {
            case Key.Left: case Key.A: if (n > 0) menuSel = (menuSel + n - 1) % n; break;
            case Key.Right: case Key.D: if (n > 0) menuSel = (menuSel + 1) % n; break;
            case Key.Escape: case Key.Backspace: Back(); break;
            case Key.Enter: case Key.KpEnter: case Key.Space: Pick(menuSel); break;
            default:
                if (k >= Key.Key1 && k <= Key.Key9 && (int)(k - Key.Key1) < n) Pick((int)(k - Key.Key1));
                break;
        }
    }

    void Back()
    {
        switch (menuLevel)
        {
            case MenuLevel.Command:
                if (ships.FirstOrDefault(s => s.IsPlayer) is { } p && Companions(p).Count > 1) { ShowShips(Companions(p), "Оберіть корабель"); menuLevel = MenuLevel.Ship; }
                else CloseCommands();
                break;
            case MenuLevel.Ship: CloseCommands(); break;
            default: ShowCommands(menuChar); break;
        }
    }

    public void Pick(int i)
    {
        if (i < 0 || i >= menuItems.Count || !menuItems[i].Enabled) return;
        var item = menuItems[i];
        switch (menuLevel)
        {
            case MenuLevel.Ship: ShowCommands(item.Target); break;
            case MenuLevel.Command: ChooseCommand(item.Id, item.Label); break;
            case MenuLevel.Target:
                if (item.Loc.Length > 0) Launch(menuCommand, -1, item.Loc);                // a place of the island
                else if (menuCommand == "BI_CompanionCommand") ShowCommands(item.Target);   // an order to a companion: pick the companion, then its commands
                else Launch(menuCommand, item.Target, "");
                break;
            case MenuLevel.Charge: Launch(menuCommand, item.Target, ""); break;
            case MenuLevel.Speed: Launch(menuCommand, -1, item.Loc); break;
            case MenuLevel.Ability: Launch(item.Id, -1, ""); break;
        }
    }

    void ChooseCommand(string ev, string label)
    {
        menuCommand = ev;
        var modeV = it.FireEvent("BI_CommandEndChecking", new List<V> { V.Str(ev) });
        int mode = Conv.ToInt(modeV);
        GD.Print($"[sea] BI_CommandEndChecking({ev}) = {mode} ({modeV}) value='{modeV.Attr?.Value}'");
        var p = ships.FirstOrDefault(s => s.IsPlayer);
        if (mode == 0) { Launch(ev, -1, ""); return; }
        if ((mode & ModeCharge) != 0)
        {
            menuLevel = MenuLevel.Charge; menuTitle = label; menuSel = 0; menuItems.Clear();
            string[] names = { "Ядра", "Картеч", "Книпелі", "Бомби" }, goods = { "Balls", "Grapes", "Knippels", "Bombs" };
            var sh = ships.FirstOrDefault(s => s.Index == menuChar)?.Chr.Find("Ship", false);
            for (int c = 0; c < 4; c++)
            {
                int have = (int)F(sh, "Cargo.Goods." + goods[c]);
                menuItems.Add(new HudMenuItem { Icon = ChargeIcon(c, false), IconSelected = ChargeIcon(c, true), Label = $"{names[c]} ({have})", Enabled = have > 0, Target = c + 1 });
            }
            return;
        }
        if ((mode & ModeUser) != 0)
        {
            menuLevel = MenuLevel.Speed; menuTitle = label; menuSel = 0; menuItems.Clear();
            var ui = BI?.Find("UserIcons", false);
            string[] cap = { "Стоп", "Півходу", "Повний хід" };
            for (int c = 0; ui != null && c < ui.Count; c++)
            {
                var u = ui.ChildAt(c)!;
                int tex = (int)F(u, "tex", 5), pic = (int)F(u, "pic", c);
                menuItems.Add(new HudMenuItem { Icon = CmdIcon(tex, pic, false), IconSelected = CmdIcon(tex, pic, true), Label = c < cap.Length ? cap[c] : u.Name, Loc = u.Get("name")?.Value ?? "" });
            }
            return;
        }
        if ((mode & ModeAbility) != 0)
        {
            menuLevel = MenuLevel.Ability; menuTitle = label; menuSel = 0; menuItems.Clear();
            var ab = BI?.Find("AbilityIcons", false);
            for (int c = 0; ab != null && c < ab.Count; c++)
            {
                var a = ab.ChildAt(c)!;
                if ((int)Conv.ParseFloatPrefix(a.Get("enable")?.Value) == 0) continue;
                int tex = (int)F(a, "texNum", 6), pic = (int)F(a, "picNum", 0);
                menuItems.Add(new HudMenuItem { Icon = CmdIcon(tex, pic, false), IconSelected = CmdIcon(tex, pic, true), Label = a.Name, Id = a.Get("event")?.Value ?? "" });
            }
            if (menuItems.Count == 0) { menuNote = "Немає доступних здібностей"; ShowCommands(menuChar); }
            return;
        }
        // a target: ships of the allowed kinds
        var cand = new List<ShipRt>();
        if (p != null)
            foreach (var s in ships)
            {
                if (s.Dying || s.Index == menuChar) continue;
                bool mine = Companions(p).Contains(s);
                int rel = s.IsPlayer ? 0 : Conv.ToInt(it.CallTop("GetRelation", V.Int(p.Index), V.Int(s.Index)));
                bool ok = (mine && (mode & ModeMyShip) != 0) || (!mine && ((rel == 2 && (mode & ModeEnemyShip) != 0) || (rel == 0 && (mode & ModeFriendShip) != 0) || (rel == 1 && (mode & ModeNeutralShip) != 0)));
                if (ok) cand.Add(s);
            }
        // places of the island (ports, bays: Islands[].reload.l<N>.{label, name, pic}) when the command accepts a locator, e.g. sail to the port
        var places = new List<HudMenuItem>();
        if ((mode & ModeAllLocator) != 0 && it.Globals.TryGetValue("AISea", out var ai) && ai.Val.Attr?.Get("Island")?.Value is { Length: > 0 } islId
            && Conv.ToInt(it.CallTop("FindIsland", V.Str(islId))) is var ii && ii >= 0 && it.Globals.TryGetValue("Islands", out var isls) && isls.Arr != null && ii < isls.Arr.Length)
        {
            var rel = isls.Arr[ii].Val.Attr?.Get("reload");
            for (int c = 0; rel != null && c < rel.Count; c++)
            {
                var l = rel.ChildAt(c)!;
                string lab = l.Get("label")?.Value ?? "", nm = l.Get("name")?.Value ?? "";
                if (lab.Length == 0 || nm.Length == 0 || l.Get("x") == null) continue;
                int pic = (int)F(l, "pic", 0);
                places.Add(new HudMenuItem { Icon = CmdIcon(13, pic, false), IconSelected = CmdIcon(13, pic, true), Label = lab, Id = "place", Loc = nm });
            }
        }
        if (cand.Count == 0 && places.Count == 0) { menuNote = "Немає підходящих цілей"; ShowCommands(menuChar); return; }
        menuLevel = MenuLevel.Target; menuTitle = label; menuSel = 0;
        var pl = p!;
        ShowShips(cand.OrderBy(s => Dist(pl, s)), label);
        menuItems.AddRange(places);
        menuLevel = MenuLevel.Target;
    }

    void Launch(string ev, int target, string loc)
    {
        int ch = menuChar;
        GD.Print($"[sea] command {ev} by {ch}, target {target}, '{loc}'");
        CloseCommands();
        it.FireEvent("BI_LaunchCommand", new List<V> { V.Int(ch), V.Str(ev), V.Int(target), V.Str(loc) });
    }

    HudMenu BuildMenu() => new()
    {
        Open = menuLevel != MenuLevel.None, Title = menuNote != null ? menuTitle + "  —  " + menuNote : menuTitle,
        Items = menuItems.ToList(), Selected = Math.Clamp(menuSel, 0, Math.Max(0, menuItems.Count - 1)),
    };
}
