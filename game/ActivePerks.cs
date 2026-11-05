using System;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine class "ActivePerkShower" (BATTLE_INTERFACE\ActivePerksShow.c): icons of the abilities that are working right now (Turn180, ImmediateReload,
/// Rush ...). The script fills the object: Textures.t&lt;N&gt;.{file, horz, vert} (icon sheets: sea_abilitys / Sinking / TimeSpeed at sea, LandCommands /
/// TimeSpeed on land), ShowParam.PosRect / IconSize / IconSpace in the screen coordinates of showWindow, and PerkList.list.&lt;perk&gt;.{texture, pic_idx};
/// MSG_ACTIVE_PERK_LIST_REFRESH "lsa" ("add" / "del", the perk) tells the engine the list has changed. The icons fill PosRect from its top right corner
/// downwards, then the next column to the left (storm-engine active_perk_shower.cpp).
/// </summary>
public partial class ActivePerkView : CanvasLayer
{
    Entity? ent;
    Interp? it;
    readonly Control canvas = new() { MouseFilter = Control.MouseFilterEnum.Ignore };

    public ActivePerkView()
    {
        Layer = 6;
        canvas.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        canvas.Draw += DrawIcons;
        AddChild(canvas);
    }

    public V OnMessage(Interp interp, Entity e, V[] d)
    {
        it = interp; ent = e;
        if (d.Length > 1 && d[1].T == VT.String) GD.Print($"[perks] {Conv.ToStr(d[1])} {(d.Length > 2 ? d[2].Attr?.Name : "")}");
        canvas.QueueRedraw();
        return V.Int(1);
    }

    public void OnDeleted(Entity e) { if (ReferenceEquals(ent, e)) { ent = null; canvas.QueueRedraw(); } }

    public override void _Process(double delta) { if (ent != null) canvas.QueueRedraw(); }

    static float F(Attr? a, string path, float def) => a?.Find(path, false)?.Value is { Length: > 0 } v ? Conv.ParseFloatPrefix(v) : def;

    void DrawIcons()
    {
        if (ent == null || ent.Deleted || it == null) return;
        var list = ent.Root.Find("PerkList.list", false);
        if (list == null || list.Count == 0) return;
        var sw = it.Globals.TryGetValue("showWindow", out var g) ? g.Val.Attr : null;
        var vp = canvas.Size;
        float left, top, right, bottom, iw, ih, gx, gy;
        float swW = F(sw, "width", 0f), swH = F(sw, "height", 0f);
        if (swW > 0f && swH > 0f)
        {
            float kx = vp.X / swW, ky = vp.Y / swH;
            var sp = ent.Root.Find("ShowParam", false);
            left = F(sp, "PosRect.left", 0f) * kx; top = F(sp, "PosRect.top", 0f) * ky; right = F(sp, "PosRect.right", 0f) * kx; bottom = F(sp, "PosRect.bottom", 0f) * ky;
            iw = F(sp, "IconSize.horz", 64f) * kx; ih = F(sp, "IconSize.vert", 64f) * ky; gx = F(sp, "IconSpace.horz", 4f) * kx; gy = F(sp, "IconSpace.vert", 4f) * ky;
        }
        else
        {
            // this engine does not fill showWindow (the original called SetShowWindowParameters with the screen size), so ShowParam holds offsets from 0:
            // the same InitActivePerkShower formula on an 800 x 600 screen scaled to the window height
            float s = vp.Y / 600f;
            left = vp.X - 148f * s; right = vp.X - 16f * s; bottom = vp.Y - 28f * s;
            top = Math.Max(200f * s, 248f);                                  // below the sea navigator of SeaHud (238 px)
            iw = ih = 64f * s; gx = gy = 4f * s;
        }
        float x = right - iw, y = top;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list.ChildAt(i);
            if (p == null) continue;
            int texN = (int)F(p, "texture", -1), pic = (int)F(p, "pic_idx", 0);
            var tex = ent.Root.Find("Textures", false)?.ChildAt(Math.Max(0, texN));
            if (texN < 0 || tex == null) continue;
            var t = GameAssets.Texture(tex.Get("file")?.Value ?? "");
            if (t == null) continue;
            int cols = Math.Max(1, (int)F(tex, "horz", 1)), rows = Math.Max(1, (int)F(tex, "vert", 1));
            float cw = t.GetWidth() / (float)cols, ch = t.GetHeight() / (float)rows;
            var src = new Rect2(pic % cols * cw, pic / cols % rows * ch, cw, ch);
            canvas.DrawTextureRectRegion(t, new Rect2(x, y, iw, ih), src);
            // ActivePerkShower::InitShowPlaces: the places go down the rightmost column first, then the next column to the left
            y += ih + gy;
            if (y + ih > bottom + 0.5f) { y = top; x -= iw + gx; if (x < left - 0.5f) break; }
        }
    }
}
