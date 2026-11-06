using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine class "battle_interface" (BATTLE_INTERFACE\BattleInterface.c InitBattleInterface; original: battle_interface/src/sea/i_battle.cpp
/// BATTLE_INTERFACE::ProcessMessage / CheckSeaState, ships_list.cpp SHIP_DESCRIBE_LIST, island_descr.cpp ISLAND_DESCRIBER). The drawing is SeaHud.cs and the
/// command menu SeaCommands.cs; this file keeps the state the original class keeps and raises its events:
/// <list type="bullet">
/// <item>BI_IN_CREATE_SHIP 46011 "llaall" (character index, character, ship type, my ship, relation) adds a ship to the list and asks the script for its
/// picture (BI_GetData "ll" BIDT_SHIPPICTURE, index: int[4] = picture, selected picture, texture, dead); BI_IN_DELETE_SHIP 46012 / BI_IN_DEAD_SHIP 46009;
/// BI_MSG_REFRESH 46005 rebuilds the list by raising "BI_CallUpdateShip" (index) for every ship of the sea and re-reads the fort relations
/// (BI_GetFortRelation "a");</item>
/// <item>BI_MSG_SET_ISLAND 46003 / BI_SET_ISLAND_LOCATORS 46002 "la": the places of the island (Islands[].reload.*), each described by the script
/// (evntGetLandData "a": int[4..6] = land/fort/town, relation, texture, picture);</item>
/// <item>BI_MSG_ADD_NEWTEXTURE 46007 "lsll" appends a texture to the icon texture list and returns its number (BICommandList::AddTexture);</item>
/// <item>BI_SET_VISIBLE 46014 "ll", BI_MSG_NOT_ENOUGH_BALLS_FLAG 46004 "ll" (the navigator's charge blinks), BI_IN_SET_COMMAND_MODE 46013 "llllll" (kept; the
/// reference ignores it as well), BI_MSG_SET_MSG_ICONS 46006 (the reference ignores it);</item>
/// <item>every frame CheckSeaState raises BI_SetSeaState "lllllll" (sail-to, land troops, map, attack, defend, reload, reload target) from that list.</item>
/// </list>
/// </summary>
public partial class SeaView
{
    const int BI_SET_ISLAND_LOCATORS = 46002, BI_MSG_SET_ISLAND = 46003, BI_MSG_NOT_ENOUGH_BALLS_FLAG = 46004, BI_MSG_REFRESH = 46005, BI_MSG_SET_MSG_ICONS = 46006,
        BI_MSG_ADD_NEWTEXTURE = 46007, BI_IN_DEAD_SHIP = 46009, BI_IN_CREATE_SHIP = 46011, BI_IN_DELETE_SHIP = 46012, BI_IN_SET_COMMAND_MODE = 46013, BI_SET_VISIBLE = 46014;
    const int BiRelEnemy = 0, BiRelFriend = 1, BiRelNeutral = 2;
    const int BiLocLand = 0, BiLocFort = 1, BiLocTown = 2;

    sealed class BiShip { public int Index; public bool My, Transferable, Dead; public int Relation = BiRelNeutral, Pic, SelPic, Tex = -1; }
    sealed class BiLocator { public Attr A = null!; public int Type = BiLocLand, Relation = BiRelNeutral, Tex = -1, Pic = -1, SelPic; public bool Diseased; }

    readonly List<BiShip> biShips = new();
    readonly List<BiLocator> biLocators = new();
    // the icon texture list: BattleInterface.CommandTextures.list t0..t12 (SetParameterData), then what BI_MSG_ADD_NEWTEXTURE appends
    readonly List<(string file, int xs, int ys)> biTextures = new();
    readonly Dictionary<(int chr, int rel), (Texture2D? back, Texture2D? flag)> biGerald = new();
    bool biVisible = true, biNotEnoughBalls;
    int biCommandMode, biCommandChar = -1, biCommandState = -1;

    void ResetBattleInterface()
    {
        biShips.Clear(); biLocators.Clear(); biTextures.Clear(); biGerald.Clear();
        for (int i = 0; i < 13; i++) biTextures.Add(("battle_interface\\" + CmdTex[i].file, CmdTex[i].xs, CmdTex[i].ys));
        biVisible = true; biNotEnoughBalls = false; biCommandMode = 0; biCommandChar = -1;
    }

    /// <summary>An int array returned by a script handler as "ref" (&amp;BI_intNRetValue): element <paramref name="i"/>, or <paramref name="def"/>.</summary>
    static int RetInt(V v, int i, int def)
    {
        if (v.Slot?.Arr is { } arr) return i < arr.Length ? Conv.ToInt(arr[i].Val) : def;
        if (v.Slot is { } s) return i == 0 ? Conv.ToInt(s.Val) : def;
        if (v.T is VT.Int or VT.Float) return i == 0 ? Conv.ToInt(v) : def;
        return def;
    }

    V OnBattleInterface(V[] d)
    {
        if (biTextures.Count == 0) ResetBattleInterface();
        int code = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        switch (code)
        {
            case BI_MSG_SET_ISLAND:
            case BI_SET_ISLAND_LOCATORS:
                if (d.Length > 1 && d[1].Attr is { } isl) SetBiIsland(isl);
                break;
            case BI_IN_CREATE_SHIP:
                if (d.Length > 5)
                {
                    int idx = Conv.ToInt(d[1]);
                    bool my = Conv.ToInt(d[4]) != 0;
                    var bs = new BiShip { Index = idx, My = my, Transferable = d.Length > 7 ? Conv.ToInt(d[7]) != 0 : my, Relation = Conv.ToInt(d[5]) };
                    var pic = it.FireEvent("BI_GetData", new List<V> { V.Int(0), V.Int(idx) });          // BIDT_SHIPPICTURE
                    // this game's BI_GetData(BIDT_SHIPPICTURE) returns int[3] = picture, texture, dead (the highlighted picture is the right half of the same cell)
                    bs.Pic = RetInt(pic, 0, 0); bs.SelPic = bs.Pic; bs.Tex = RetInt(pic, 1, -1); bs.Dead = RetInt(pic, 2, 0) != 0;
                    biShips.Add(bs);
                }
                break;
            case BI_IN_DELETE_SHIP:
                if (d.Length > 1) { int idx = Conv.ToInt(d[1]); biShips.RemoveAll(s => s.Index == idx); }
                break;
            case BI_IN_DEAD_SHIP:
                if (d.Length > 1) { int idx = Conv.ToInt(d[1]); foreach (var s in biShips) if (s.Index == idx) s.Dead = true; }
                break;
            case BI_IN_SET_COMMAND_MODE:
                if (d.Length > 4) { biCommandMode = Conv.ToInt(d[1]); biCommandChar = Conv.ToInt(d[4]); biCommandState = d.Length > 5 ? Conv.ToInt(d[5]) : -1; }
                break;
            case BI_SET_VISIBLE:
                if (d.Length > 1) biVisible = Conv.ToInt(d[1]) != 0;
                break;
            case BI_MSG_NOT_ENOUGH_BALLS_FLAG:
                if (d.Length > 1) biNotEnoughBalls = Conv.ToInt(d[1]) != 0;
                break;
            case BI_MSG_REFRESH:
                biShips.Clear();
                foreach (var s in ships.ToList()) it.FireEvent("BI_CallUpdateShip", new List<V> { V.Int(s.Index) });   // SHIP_DESCRIBE_LIST::Refresh
                foreach (var l in biLocators.Where(l => l.Type == BiLocFort))                                           // ISLAND_DESCRIBER::Refresh
                    l.Relation = RetInt(it.FireEvent("BI_GetFortRelation", new List<V> { V.A(l.A) }), 0, BiRelNeutral);
                break;
            case BI_MSG_ADD_NEWTEXTURE:
                if (d.Length > 3) { biTextures.Add((Conv.ToStr(d[1]), Math.Max(1, Conv.ToInt(d[2])), Math.Max(1, Conv.ToInt(d[3])))); return V.Int(biTextures.Count - 1); }
                return V.Int(-1);
            case BI_MSG_SET_MSG_ICONS:
                break;
        }
        return V.Int(0);
    }

    /// <summary>ISLAND_DESCRIBER::SetIsland: the places of Islands[].reload, described by the script (evntGetLandData).</summary>
    void SetBiIsland(Attr isl)
    {
        biLocators.Clear();
        var rel = isl.Get("reload");
        for (int i = 0; rel != null && i < rel.Count; i++)
        {
            if (rel.ChildAt(i) is not { } a) continue;
            var l = new BiLocator { A = a };
            var v = it.FireEvent("evntGetLandData", new List<V> { V.A(a) });
            int t = RetInt(v, 0, 0);
            l.Type = t == 0 ? BiLocLand : t == 1 ? BiLocFort : BiLocTown;
            l.Relation = RetInt(v, 1, BiRelNeutral); l.Tex = RetInt(v, 2, -1); l.Pic = RetInt(v, 3, -1); l.SelPic = RetInt(v, 4, l.Pic); l.Diseased = RetInt(v, 5, 0) != 0;
            biLocators.Add(l);
        }
        GD.Print($"[sea] battle interface: island {isl.Get("id")?.Value}, {biLocators.Count} places ({biLocators.Count(l => l.Type == BiLocFort)} forts)");
    }

    /// <summary>The picture of a ship in the command list (BI_GetData BIDT_SHIPPICTURE at BI_IN_CREATE_SHIP), highlighted = the right half of the cell.</summary>
    Texture2D? ShipPicture(int chr, bool highlighted) => biShips.FirstOrDefault(b => b.Index == chr) is { } b ? BiIcon(b.Tex, b.Pic, highlighted) : null;

    /// <summary>The nation shield of a ship (BI_GetData BIDT_GERALD_DATA: int[4] = texture, relation shield picture, texture, nation picture), asked once per relation.</summary>
    (Texture2D? back, Texture2D? flag) Gerald(int chr, int relation)
    {
        if (biGerald.TryGetValue((chr, relation), out var g)) return g;
        var v = it.FireEvent("BI_GetData", new List<V> { V.Int(1), V.Int(chr) });
        g = (BiIcon(RetInt(v, 0, -1), RetInt(v, 1, -1), false), BiIcon(RetInt(v, 2, -1), RetInt(v, 3, -1), false));
        biGerald[(chr, relation)] = g;
        return g;
    }

    /// <summary>An icon of the battle interface texture list (textures appended by BI_MSG_ADD_NEWTEXTURE included).</summary>
    Texture2D? BiIcon(int tex, int pic, bool highlighted)
    {
        if (tex < 0 || tex >= biTextures.Count) return null;
        var (file, xs, ys) = biTextures[tex];
        var texture = GameAssets.Texture(file);
        if (texture == null || pic < 0) return null;
        float w = texture.GetWidth(), h = texture.GetHeight(), cw = w / xs, ch = h / ys;
        bool doubled = cw / ch > 1.5f;
        if (doubled) cw /= 2;
        float x = (pic % xs) * cw + (doubled && highlighted ? w / 2 : 0), y = (pic / xs) * ch;
        return new AtlasTexture { Atlas = texture, Region = new Rect2(x, y, cw, ch) };
    }

    /// <summary>
    /// BATTLE_INTERFACE::CheckSeaState (every frame): sail-to = another ship in the list or a place on the island; map = no living enemy ship closer than
    /// BattleInterface.freeDistance; attack = a living enemy ship or an enemy fort; defend = one of my ships; reload = a transferable ship within
    /// BattleInterface.boardRadius (the nearest is the target). Nothing is raised while the main character's ship is not in the list.
    /// </summary>
    void CheckSeaState()
    {
        var p = ships.FirstOrDefault(s => s.IsPlayer);
        if (p == null || biShips.All(b => b.Index != p.Index)) return;
        var bia = BI;
        float board = F(bia, "boardRadius", 0f), free = F(bia, "freeDistance", 500f);
        bool sailTo = false, map = true, attack = false, defend = false, reload = false;
        int reloadTarget = -1; float minReload = board;
        foreach (var b in biShips)
        {
            if (b.Index == p.Index) continue;
            sailTo = true;
            var o = ships.FirstOrDefault(s => s.Index == b.Index);
            float dist = o != null ? Dist(p, o) : float.MaxValue;
            if (b.Transferable && dist < board)
            {
                reload = true;
                if (dist < minReload) { minReload = dist; reloadTarget = b.Index; }
            }
            if (b.My) defend = true;
            else if (b.Relation == BiRelEnemy && !b.Dead)
            {
                attack = true;
                if (dist < free) map = false;
            }
        }
        if (biLocators.Count > 0) sailTo = true;
        if (biLocators.Any(l => l.Type == BiLocFort && l.Relation == BiRelEnemy)) attack = true;
        it.FireEvent("BI_SetSeaState", new List<V> { V.Int(sailTo ? 1 : 0), V.Int(0), V.Int(map ? 1 : 0), V.Int(attack ? 1 : 0), V.Int(defend ? 1 : 0), V.Int(reload ? 1 : 0), V.Int(reloadTarget) });
    }
}
