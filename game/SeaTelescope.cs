using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The telescope's ship information and the ship pointer of the sea HUD.
/// TELESCOPE (sea.c): the script tells the engine the spyglass type with MSG_TELESCOPE_SET_TYPE 77302 "lsflll" (eyepiece texture, zoom, show items,
/// activation time ms, update time ms; ispyglass.c setTelescopeInitParameters, taken from the equipped item's scope.* attributes). While the player looks
/// through it the engine raises TELESCOPE_ACTIVE ("TelescopeActive", 1/0: AICameras.c forbids the camera switch meanwhile); a ship or fort that stays in the
/// middle of the eyepiece for the activation time is reported with the event "SetTelescopeInfo" (character index), again every update time, and
/// "ReleaseTelescopeInfo" when it leaves. The script (BATTLE_INTERFACE\ispyglass.c SetSpyGlassData) decides what the spyglass may show
/// (scope.show.* of the item) and sends it to the class "ispyglass": MSG_ISG_UPDATE 46202 "sslllflll" (name, type, hull %, sail %, crew, speed, cannons,
/// charge, nation; -1 / "" = hidden) and MSG_ISG_VISIBLE 46201. The panel is drawn by SeaHud (layout of FillISpyGlassParameters).
/// shippointer (BattleInterface.c): the battle interface raises "evntBISelectShip" (character, is it ours) for the ship highlighted in the command menu;
/// the script answers with MSG_SP_CHANGESHIP 46301 "lllfffff" (character or -1, mine, bob speed, bob amplitude, width, height, height above the ship) and
/// the class draws the arrow (Frendly.tga / Enemy.tga) bobbing over that ship.
/// </summary>
public partial class SeaView
{
    const int MSG_TELESCOPE_REQUEST = 77301, MSG_TELESCOPE_SET_TYPE = 77302, MSG_ISG_VISIBLE = 46201, MSG_ISG_UPDATE = 46202, MSG_SP_CHANGESHIP = 46301;

    bool scopeFromScript;                      // the script sent the spyglass type (77302): telescope.ini is not needed
    float scopeActivateMs = 500f, scopeUpdateMs = 150f;
    bool scopeActiveSent;
    bool scopeOn, scopeCtrlWas;                // ISPYGLASS::m_bIsOn (set by MSG_TELESCOPE_REQUEST), the Ctrl state of the last frame
    int scopeTarget = -1;                    // the character under the eyepiece
    bool scopeInfoShown;
    string lastSpyKey = "";
    float scopeTimerMs;

    // ship pointer
    int pointerChr = -1;
    bool pointerMine;
    float pointerSpeed = 0.005f, pointerAmp = 1f, pointerW = 1f, pointerH = 1f, pointerTop = 10f;
    int lastSelectedShip = -2;

    void RegisterTelescope(Interp i)
    {
        i.EntityHandlers["TELESCOPE"] = (e, fmt, d) =>
        {
            int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
            if (msg == MSG_TELESCOPE_SET_TYPE && d.Length > 5)
            {
                string tex = Conv.ToStr(d[1]);
                if (tex.Length > 0) scopeTex = tex;
                float z = Conv.ToFloat(d[2]);
                if (z > 0.5f) scopeZoom = z;
                scopeActivateMs = Math.Max(0, Conv.ToInt(d[4])); scopeUpdateMs = Math.Max(30, Conv.ToInt(d[5]));
                scopeFromScript = true;
                GD.Print($"[sea] telescope type: {scopeTex} zoom {scopeZoom} activate {scopeActivateMs} ms update {scopeUpdateMs} ms");
            }
            if (msg == MSG_TELESCOPE_REQUEST)   // Telescope_Request (SEA_AI\telescope.c) sends only the code: the key press and release each flip the telescope (Ctrl = TelescopeIn / TelescopeOut)
            {
                bool want = d.Length > 1 ? Conv.ToInt(d[1]) != 0 : !scopeOn;
                if (want != scopeOn) { scopeOn = want; scopeTimerMs = 1e6f; GD.Print($"[sea] telescope {(want ? "raised" : "lowered")} (MSG_TELESCOPE_REQUEST)"); }
            }
            return V.Int(1);                    // 77300 INIT_ARRAYS (the tables are read from the characters directly), 50301 SHIP_DELETE
        };
        i.EntityHandlers["ispyglass"] = (e, fmt, d) =>
        {
            int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
            if (msg == MSG_ISG_VISIBLE) hud.SpyVisible = d.Length > 1 && Conv.ToInt(d[1]) != 0;
            else if (msg == MSG_ISG_UPDATE && d.Length > 9)
            {
                hud.Spy = new SpyGlassInfo
                {
                    Name = Conv.ToStr(d[1]), Type = Conv.ToStr(d[2]), Hull = Conv.ToInt(d[3]), Sail = Conv.ToInt(d[4]), Crew = Conv.ToInt(d[5]),
                    Speed = Conv.ToFloat(d[6]), Cannons = Conv.ToInt(d[7]), Charge = Conv.ToInt(d[8]), Nation = Conv.ToInt(d[9]),
                };
                var sp = hud.Spy;
                string spKey = $"{sp.Hull}/{sp.Sail}/{sp.Crew}/{sp.Cannons}/{sp.Charge}";
                if (spKey != lastSpyKey) { lastSpyKey = spKey; GD.Print($"[sea] spyglass panel: type '{sp.Type}' hull {sp.Hull} sails {sp.Sail} crew {sp.Crew} speed {sp.Speed:0.0} cannons {sp.Cannons} charge {sp.Charge} nation {sp.Nation} (-1 = hidden by the glass)"); }
            }
            return V.Int(1);
        };
        i.EntityHandlers["shippointer"] = (e, fmt, d) =>
        {
            if (d.Length > 7 && Conv.ToInt(d[0]) == MSG_SP_CHANGESHIP)
            {
                pointerChr = Conv.ToInt(d[1]); pointerMine = Conv.ToInt(d[2]) != 0;
                pointerSpeed = Conv.ToFloat(d[3]); pointerAmp = Conv.ToFloat(d[4]); pointerW = Conv.ToFloat(d[5]); pointerH = Conv.ToFloat(d[6]); pointerTop = Conv.ToFloat(d[7]);
                GD.Print($"[sea] ship pointer: character {pointerChr} (mine {pointerMine})");
            }
            return V.Int(1);
        };
    }

    /// <summary>
    /// The sea is torn down (boarding, a landing, a reload): the telescope goes down for good. The script is told, otherwise SeaCameras_TelescopeActive leaves
    /// bCanSwitchCameras false and the battle interface hidden (BI_VISIBLE 0) after the boarding; the eyepiece, the spyglass strip and the target are cleared.
    /// </summary>
    void LowerTelescope()
    {
        if (scopeActiveSent)
        {
            it.FireEvent("TelescopeActive", new List<V> { V.Int(0) });
            it.FireEvent("BI_VISIBLE", new List<V> { V.Int(1) });
            if (scopeInfoShown) it.FireEvent("ReleaseTelescopeInfo", new List<V>());
            GD.Print("[sea] telescope lowered: the sea is unloaded while it was up (script told TelescopeActive 0)");
        }
        scopeT = 0f; scopeOn = false; scopeTarget = -1; scopeInfoShown = false; scopeActiveSent = false; lastSpyKey = "";
        hud.ScopeTexture = ""; hud.ScopeAmount = 0f; hud.SpyVisible = false; hud.Spy = null;
    }

    /// <summary>Per frame while the telescope is up: what is in the middle of the eyepiece, and the script events for it.</summary>
    /// <summary>The engine side of the telescope key: raises the event, the script decides (only in the deck camera) and answers with the TELESCOPE message.</summary>
    void ScopeRequest(int on) => it.FireEvent("MSG_TELESCOPE_REQUEST", new List<V> { V.Int(on) });

    void UpdateTelescopeInfo(ShipRt p, float dt)
    {
        bool on = scopeOn;                                   // TurnOnTelescope: TELESCOPE_ACTIVE and BI_VISIBLE go out at once, the zoom grows during the activation time
        if (on != scopeActiveSent)
        {
            scopeActiveSent = on;
            it.FireEvent("TelescopeActive", new List<V> { V.Int(on ? 1 : 0) });
            it.FireEvent("BI_VISIBLE", new List<V> { V.Int(on ? 0 : 1) });            // spyglass.cpp: the battle interface hides under the eyepiece
        }
        if (!on)
        {
            if (scopeInfoShown) it.FireEvent("ReleaseTelescopeInfo", new List<V>());
            scopeTarget = -1; scopeInfoShown = false; scopeTimerMs = 0f; scopeKeepS = 0f;
            return;
        }
        // ISPYGLASS::Execute: the zoom has grown during the activation time (UpdateCamera); every update time the view axis is traced against the ships
        // and the fort, the hit is reported with SetTelescopeInfo; a lost target is kept for m_fMaxInfoKeepDelay = 1.5 s
        if (scopeKeepS > 0f) scopeKeepS -= dt;
        scopeTimerMs += dt * 1000f;
        if (scopeTimerMs < scopeUpdateMs && scopeInfoShown) return;
        scopeTimerMs = 0f;
        int tgt = ScopeTarget(p);
        if (tgt >= 0)
        {
            scopeKeepS = 1.5f;
            if (tgt != scopeTarget) GD.Print($"[sea] telescope: character {tgt} in view");
            scopeTarget = tgt; scopeInfoShown = true;
            it.FireEvent("SetTelescopeInfo", new List<V> { V.Int(tgt) });
        }
        else if (scopeInfoShown && scopeKeepS <= 0f)
        {
            it.FireEvent("ReleaseTelescopeInfo", new List<V>());
            scopeTarget = -1; scopeInfoShown = false;
        }
        else if (scopeInfoShown) it.FireEvent("SetTelescopeInfo", new List<V> { V.Int(scopeTarget) });
    }

    float scopeKeepS;

    /// <summary>The ship (or fort) closest to the middle of the eyepiece: within the angle its size covers, up to 4 km.</summary>
    int ScopeTarget(ShipRt p)
    {
        var eye = Cam.GlobalPosition; var fwd = -Cam.GlobalTransform.Basis.Z;
        int best = -1; float bestK = 1f;
        void Try(int idx, Vector3 at, float radius)
        {
            var v = at - eye; float dist = v.Length();
            if (dist < 1f || dist > 4000f) return;
            float ang = v.Normalized().AngleTo(fwd), allowed = MathF.Atan2(radius, dist) + 0.004f;
            float k = ang / allowed;
            if (k < bestK) { bestK = k; best = idx; }
        }
        foreach (var o in ships)
            if (!o.IsPlayer && !o.Dying) Try(o.Index, new Vector3(o.X, o.Height * 0.35f, -o.Z), Math.Max(40f, Math.Max(o.Length * 0.5f, o.Height * 0.5f)));   // the C++ falls back to a 40 m sphere
        foreach (var f in forts)
        {
            if (f.Cannons.Count == 0) continue;
            var c = Vector3.Zero; foreach (var fc in f.Cannons) c += new Vector3(fc.Pos.X, fc.Pos.Y, -fc.Pos.Z);
            Try(f.Index, c / f.Cannons.Count, 60f);
        }
        return best;
    }

    /// <summary>The command menu highlights a ship: the battle interface tells the script (evntBISelectShip), which moves the ship pointer.</summary>
    void ReportSelectedShip()
    {
        int sel = -1; bool mine = false;
        if ((menuLevel == MenuLevel.Ship || menuLevel == MenuLevel.Target) && menuSel >= 0 && menuSel < menuItems.Count && menuItems[menuSel].Id == "ship")
        {
            sel = menuItems[menuSel].Target;
            if (ships.FirstOrDefault(s => s.IsPlayer) is { } pl) mine = Companions(pl).Any(s => s.Index == sel);
        }
        if (sel == lastSelectedShip) return;
        lastSelectedShip = sel;
        it.FireEvent("evntBISelectShip", new List<V> { V.Int(sel), V.Int(mine ? 1 : 0) });
    }

    /// <summary>
    /// The ship pointer as SHIPPOINTER::UpdateShipPointer draws it: a camera-facing square of side k = perspective * 0.05 * distance (at least 1 m),
    /// its lower edge k above fTop + box height + amp * sin(phase), the phase growing by speed per ms. (The script's width/height values are unused there too.)
    /// </summary>
    void UpdatePointer()
    {
        var s = pointerChr >= 0 ? ships.FirstOrDefault(x => x.Index == pointerChr && !x.Dying) : null;
        if (s == null) { hud.SetPointer(null, false, Vector2.Zero); return; }
        float bob = pointerAmp * MathF.Sin((float)(Time.GetTicksMsec() * (double)pointerSpeed % Math.Tau));
        var at = new Vector3(s.X, s.Height + pointerTop + bob, -s.Z);
        float k = Math.Max(1f, Mathf.DegToRad(Cam.Fov) * 0.05f * Cam.GlobalPosition.DistanceTo(at));
        Vector3 bottom = at + Vector3.Up * k, top = bottom + Vector3.Up * k;
        if (Cam.IsPositionBehind(bottom) || Cam.IsPositionBehind(top)) { hud.SetPointer(null, false, Vector2.Zero); return; }
        Vector2 b = Cam.UnprojectPosition(bottom), t = Cam.UnprojectPosition(top);
        float side = Math.Max(4f, b.DistanceTo(t));
        hud.SetPointer(b, pointerMine, new Vector2(side, side));
    }
}
