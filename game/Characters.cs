using System;
using System.Linq;
using System.Collections.Generic;
using Godot;
using StormScript.Formats;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine's "NPCharacter"/"Player" class: a person in a location. It owns position, heading, the skinned model and the
/// task the script gave it (MSG_NPCHARACTER_SETTASK: Stay, Goto point, Runto point, Follow character, Fight, Escape, Dead).
/// When a Goto/Run task ends it raises "Location_CharacterEndTask", which is what the script's AI templates wait for.
/// Positions are kept in Direct3D space (what the scripts see); rendering converts z.
/// </summary>
public partial class CharacterView : Node3D
{
    enum Task { None, Stay, Goto, Run, Follow, Fight, Escape, Dead }

    public static readonly Dictionary<Attr, CharacterView> All = new();

    readonly Interp it;
    public readonly Entity Ent;
    readonly Func<string, string, GmFile.Label?> findLocator;
    readonly Func<PtcFile?> getPatch;
    float x, y, z, ay;
    string model = "", anim = "";
    public bool IsPlayer => Ent.Class == "Player";
    public bool Placed;
    CharacterRig? rig;
    MeshInstance3D? placeholder;
    static readonly HashSet<string> warned = new();

    // task state
    Task task = Task.Stay;
    float tx, ty, tz;
    CharacterView? target;
    // locator detectors (AddCharacterLocatorGroup): Location_CharacterEntryToLocator / ExitFromLocator events
    readonly HashSet<string> detectGroups = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string, string), float> insideLocators = new();
    public Func<Attr?>? LocationRoot;
    int followState;
    List<(float x, float z)>? path;
    float pathTimer, pathX, pathZ;
    // melee: a swing takes ~0.35 s to reach the hit frame, then the script is told with Location_CharacterAttack
    bool fightMode;
    float atkCooldown, atkHitIn = -1f;
    CharacterView? atkTarget;
    public bool FightMode
    {
        get => fightMode;
        set
        {
            if (fightMode == value) return;
            fightMode = value;
            if (rig != null && Placed && rig.Play(value ? "Normal to fight" : "Fight to normal", false, true)) playing = "draw";
        }
    }

    // ---- melee / pistol: block, hit reactions, gun
    public bool Blocking;                          // the player holds the block key / an NPC raised its sword for a moment
    float blockUntil, hitUntil, gunCooldown, shotIn = -1f;
    CharacterView? shotTarget;
    bool Busy => playing is "draw" or "block_hit" or "Shot" || playing.StartsWith("attack") || playing.StartsWith("hit");

    float Skill(string key, float def) => Ent.Root.Find("chr_ai." + key, false)?.Value is { Length: > 0 } v ? Conv.ParseFloatPrefix(v) : def;

    /// <summary>A hit landed on this character: it blocks (script event Location_CharacterBlock, no damage) or takes the damage (Location_CharacterAttack).</summary>
    public bool Defends(CharacterView attacker)
    {
        if (!fightMode || !IsAlive) return false;
        if (Blocking) return true;
        if (IsPlayer) return false;
        if (idleRng.NextDouble() < Math.Clamp(Skill("block", 0.1f) * 2.5f, 0.05f, 0.7f)) { blockUntil = 0.5f; return true; }     // NPCs raise the sword by chance of their block skill
        return false;
    }

    void PlayReaction(bool blocked)
    {
        if (rig == null || !IsAlive) return;
        string a = blocked ? "block_hit" : "hit_" + (idleRng.Next(3) + 1);
        if (rig.Play(a, false, true)) { playing = a.StartsWith("hit") ? "hit" : a; hitUntil = 0.35f; }
    }

    /// <summary>Pistol shot (the original control ChrFire): the nearest living target in front within reach; the script applies the damage.</summary>
    public bool TryFire()
    {
        if (!fightMode || !IsAlive || gunCooldown > 0f || shotIn >= 0f || rig == null) return false;
        if (Skill("charge", 0f) < 1f) { it.FireEvent("Location_CharacterNoCharge"); return false; }
        CharacterView? best = null; float bd = 24f * 24f;
        foreach (var c in All.Values)
        {
            if (ReferenceEquals(c, this) || !c.Placed || !c.IsAlive) continue;
            float dx = c.x - x, dz = c.z - z, d2 = dx * dx + dz * dz;
            if (d2 > bd) continue;
            float da = MathF.Abs(Angle(dx, dz) - ay); while (da > MathF.PI) da = MathF.Abs(da - MathF.Tau);
            if (da < 0.45f) { bd = d2; best = c; }
        }
        shotTarget = best; shotIn = 0.25f; gunCooldown = 1.2f;
        if (best != null) ay = Angle(best.x - x, best.z - z);
        if (rig.Play("Shot", false, true)) playing = "Shot";
        return true;
    }

    void TickGun(float dt)
    {
        gunCooldown -= dt;
        if (hitUntil > 0f) hitUntil -= dt;
        if (blockUntil > 0f) { blockUntil -= dt; if (blockUntil <= 0f) Blocking = false; else Blocking = true; }
        // the engine recharges the gun after a shot: chr_ai.charge grows by charge_dlt per second while chargeprc = 1
        if (Ent.Root.Find("chr_ai.chargeprc", false) is { } cp && Conv.ParseFloatPrefix(cp.Value) > 0f)
        {
            float c = Skill("charge", 0f) + Skill("charge_dlt", 0.125f) * dt, mx = Skill("charge_max", 1f);
            var ca = Ent.Root.Find("chr_ai.charge", true)!;
            if (c >= mx) { ca.Value = Conv.FloatToConcat(mx); cp.Value = "0"; } else ca.Value = Conv.FloatToConcat(c);
        }
        if (shotIn < 0f) return;
        shotIn -= dt;
        if (shotIn >= 0f) return;
        var t = shotTarget; shotTarget = null;
        float d = t == null ? 0f : MathF.Sqrt((t.x - x) * (t.x - x) + (t.z - z) * (t.z - z));
        it.FireEvent("Location_CharacterFire", new List<V> { V.A(Ent.Root), V.A(t?.Ent.Root), V.Float(Math.Clamp(1f - d / 24f, 0f, 1f)), V.Int(t != null ? 1 : 0) });
        t?.PlayReaction(false);
    }
    public Vector3 ScriptPos => new(x, y, z);
    public bool IsAlive => task != Task.Dead && Ent.Root.Find("chr_ai.hp", false) is { } hp && Conv.ParseFloatPrefix(hp.Value) >= 1f;                // 0 = not reported yet, 1 = walking after the target, 2 = arrived (events FollowGo / FollowStay)
    string scriptAction = "";
    string playing = "";
    bool inDialog;

    // player control, set by the main loop every frame
    public Vector2 Input;           // x: strafe right, y: forward (camera relative)
    public bool Run;
    public float CameraYaw;         // Godot yaw of the camera

    public float X => x;
    public float Y => y;
    public float Z => z;
    public float Heading => ay;
    public string Id => Ent.Root.Get("id")?.Value ?? "?";
    public bool ScriptControlled => (Ent.Root.Get("chr_ai")?.Get("type")?.Value ?? "") != "player";

    public CharacterView(Interp it, Entity ent, Func<string, string, GmFile.Label?> findLocator, Func<PtcFile?> getPatch)
    {
        this.it = it;
        Ent = ent;
        this.findLocator = findLocator;
        this.getPatch = getPatch;
        string id = Id;
        Name = "Chr_" + id;
        All[ent.Root] = this;
        bool woman = ent.Root.Get("sex")?.Value == "woman";
        placeholder = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.3f, Height = 1.8f },
            Position = new Vector3(0, 0.9f, 0),
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = woman ? new Color(0.9f, 0.4f, 0.6f) : new Color(0.3f, 0.6f, 0.95f) },
        };
        AddChild(placeholder);
        AddChild(new Label3D { Text = id, Position = new Vector3(0, 2.1f, 0), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.004f, NoDepthTest = true, Name = "NameTag" });
    }

    public override void _ExitTree() => All.Remove(Ent.Root);

    public void ShowNameTag(bool on) { if (GetNodeOrNull<Label3D>("NameTag") is { } l) l.Visible = on; }

    // ------------------------------------------------------------------ placement
    /// <summary>The engine puts a character on the nearest point of the location's walkable patch.</summary>
    void SnapToPatch()
    {
        if (getPatch() is { } p && p.Snap(x, y, z, out var nx, out var ny, out var nz)) { x = nx; y = ny; z = nz; }
    }

    void Apply()
    {
        Position = new Vector3(x, y, -z);
        Rotation = new Vector3(0, -ay, 0);
        Placed = true;
    }

    void PlaceAtLocator(GmFile.Label l)
    {
        x = l.M[12]; y = l.M[13]; z = l.M[14];
        ay = MathF.Atan2(l.M[8], l.M[10]);      // heading from the locator's z axis
        SnapToPatch();
        Apply();
    }

    static float Angle(float dx, float dz) => MathF.Atan2(dx, dz);

    public string DebugState() => $"{Ent.Root.Get("id")?.Value} idx={Ent.Root.Get("index")?.Value} class={Ent.Class} placed={Placed} inDialog={inDialog} task={task} pos=({x:0.0},{y:0.0},{z:0.0}) target={target?.Ent.Root.Get("id")?.Value}";

    static float TurnToward(float cur, float want, float maxStep)
    {
        float d = want - cur;
        while (d > MathF.PI) d -= 2 * MathF.PI;
        while (d < -MathF.PI) d += 2 * MathF.PI;
        return cur + Math.Clamp(d, -maxStep, maxStep);
    }

    // ------------------------------------------------------------------ per-frame update (called by the main loop)
    void PlayMove(string action)
    {
        if (rig == null) return;
        if (fightMode) action = action switch { "walk" => "fight walk", "run" => "fight run", _ => action };
        if (playing == action) return;
        if (Busy && !rig.Finished) return;
        if (rig.Play(action, true)) playing = action;
    }

    readonly Random idleRng = new();

    /// <summary>The script's own list of idle animations (character.actions.idle.i1...): played one after another at random.</summary>
    List<string> IdleList()
    {
        var l = new List<string>();
        if (Ent.Root.Find("actions.idle", false) is { } idle)
            foreach (var k in idle.Children) if (!string.IsNullOrEmpty(k.Value)) l.Add(k.Value!);
        return l;
    }

    void PlayIdle()
    {
        if (rig == null) return;
        if (Busy && !rig.Finished) return;                                  // let a swing, a draw, a shot or a reaction finish
        if (Blocking && fightMode) { if (playing != "block" && rig.Play("block", true)) playing = "block"; return; }
        if (fightMode)
        {
            if (playing != "fight stand_1" && rig.Play("fight stand_1", true)) playing = "fight stand_1";
            return;
        }
        if (scriptAction.Length == 0)
        {
            var list = IdleList();
            if (list.Count > 0)
            {
                bool inList = list.Any(x => x.Equals(playing, StringComparison.OrdinalIgnoreCase));
                if (!inList || rig.Finished)
                {
                    string pick = list[idleRng.Next(list.Count)];
                    if (rig.Play(pick, false, true)) { playing = pick; return; }
                }
                else return;
            }
        }
        string want = scriptAction.Length > 0 ? scriptAction : "idle_2";
        if (playing == want) return;
        if (!rig.Play(want, true)) rig.Play("idle_2", true);
        playing = want;
    }

    /// <summary>Tries to walk by (dx,dz); stays inside the walkable patch and slides along its edge. Returns true if it moved.</summary>
    /// <summary>True if moving to (nx,nz) would push this character into another person (a move that increases the distance is always allowed).</summary>
    bool Blocked(float nx, float nz)
    {
        foreach (var c in All.Values)
        {
            if (ReferenceEquals(c, this) || !c.Placed || !c.Visible) continue;
            float cd = (c.x - x) * (c.x - x) + (c.z - z) * (c.z - z), nd = (c.x - nx) * (c.x - nx) + (c.z - nz) * (c.z - nz);
            if (nd < 0.45f * 0.45f && nd < cd) return true;
        }
        return false;
    }

    bool Step(float dx, float dz, bool useBounds)
    {
        var patch = getPatch();
        float nx = x + dx, nz = z + dz;
        if (Blocked(nx, nz))
        {
            // slide along the other person instead of stopping dead
            if (!Blocked(nx, z)) nz = z;
            else if (!Blocked(x, nz)) nx = x;
            else return false;
            dx = nx - x; dz = nz - z;
        }
        if (useBounds && patch != null)
        {
            if (patch.Contains(nx, nz, y, 1.0f, out var ny)) { x = nx; z = nz; y = ny; return true; }
            if (patch.Contains(nx, z, y, 1.0f, out ny)) { x = nx; y = ny; return true; }
            if (patch.Contains(x, nz, y, 1.0f, out ny)) { z = nz; y = ny; return true; }
            return false;
        }
        x = nx; z = nz;
        if (patch != null && patch.Snap(x, y, z, out var sx, out var sy, out var sz)) { y = sy; }
        return true;
    }

    void EndTask(string name)
    {
        task = Task.Stay;
        it.FireEvent("Location_CharacterEndTask", new List<V> { V.Str(name), V.A(Ent.Root) });
    }

    static float Num(Attr? a) => Conv.ParseFloatPrefix(a?.Value);

    string equipSig = "";

    /// <summary>Model file of an item id (Items[GetItemIndex(id)].model); items whose model is not in the game folder get a similar one.</summary>
    string? ItemModel(string? id, string fallback)
    {
        if (string.IsNullOrEmpty(id)) return null;
        int idx = Conv.ToInt(it.CallTop("GetItemIndex", V.Str(id)));
        string? model = idx >= 0 && it.Globals.TryGetValue("Items", out var items) && items.Arr != null && idx < items.Arr.Length ? items.Arr[idx].Val.Attr?.Get("model")?.Value : null;
        if (!string.IsNullOrEmpty(model) && System.IO.File.Exists(GameAssets.Resource("MODELS\\Items\\" + model + ".gm"))) return model;
        return fallback;
    }

    /// <summary>The sword and the pistol on the body follow the character's equipment (equip.blade / equip.gun) and the fight mode.</summary>
    void UpdateEquipment()
    {
        if (rig == null) return;
        string blade = Ent.Root.Find("equip.blade", false)?.Value ?? "", gun = Ent.Root.Find("equip.gun", false)?.Value ?? "";
        string sig = $"{model}|{blade}|{gun}|{fightMode}";
        if (sig == equipSig) return;
        equipSig = sig;
        rig.Equip(ItemModel(blade, "blade1"), ItemModel(gun, "pistol4"), fightMode);
    }

    void Detect(float dt)
    {
        if (detectGroups.Count == 0 || dt <= 0f) return;
        var root = LocationRoot?.Invoke();
        var locs = root?.Find("locators", false);
        if (root == null || locs == null) return;
        // collect the transitions first: "exit" events must reach the script before "entry" events (an exit resets the action hint the entry just set)
        List<(string g, string name)>? entered = null;
        List<(string g, string name, float t)>? left = null;
        foreach (var g in detectGroups)
        {
            var grp = locs.Find(g, false);
            if (grp == null) continue;
            foreach (var l in grp.Children)
            {
                float dx = Num(l.Find("x", false)) - x, dz = Num(l.Find("z", false)) - z;     // plan distance; some locators sit well below/above the floor
                float r = Num(l.Find("radius", false));
                bool inside = dx * dx + dz * dz <= r * r;
                var key = (g, l.Name);
                if (inside && !insideLocators.ContainsKey(key)) { insideLocators[key] = 0f; (entered ??= new()).Add(key); }
                else if (inside) insideLocators[key] += dt;
                else if (insideLocators.Remove(key, out var t)) (left ??= new()).Add((g, l.Name, t));
            }
        }
        if (left != null)
            foreach (var (g, n, t) in left)
                it.FireEvent("Location_CharacterExitFromLocator", new List<V> { V.A(root), V.A(Ent.Root), V.Str(g), V.Str(n), V.Float(t) });
        if (entered != null)
            foreach (var (g, n) in entered)
            {
                if (IsPlayer && System.Environment.GetEnvironmentVariable("SEADOGS_DETECTLOG") != null) GD.Print($"[detect] ENTER {g}.{n}");
                it.FireEvent("Location_CharacterEntryToLocator", new List<V> { V.A(root), V.A(Ent.Root), V.Str(g), V.Str(n) });
            }
    }

    public void Tick(float dt)
    {
        if (!Placed || rig == null) return;
        Detect(dt);
        UpdateEquipment();
        if (dt > 0f) { TickSwing(dt); TickGun(dt); }
        if (inDialog || dt <= 0f) { PlayIdle(); return; }

        float walk = rig.ActionSpeed("walk", 1.6f), run = rig.ActionSpeed("run", 3.3f), turn = rig.ActionTurnSpeed("walk", 5.5f);

        // player: keyboard control while the script lets the player act
        if (IsPlayer && !ScriptControlled && task is Task.Stay or Task.None)
        {
            if (Input.LengthSquared() > 0.01f)
            {
                // camera-relative move in Godot space -> Direct3D space (z flips)
                float s = MathF.Sin(CameraYaw), c = MathF.Cos(CameraYaw);
                var fwd = new Vector2(-s, -c);      // Godot (x, z)
                var right = new Vector2(c, -s);
                var m = (fwd * Input.Y + right * Input.X).Normalized();
                float dx = m.X, dz = -m.Y;
                float speed = Run ? run : walk;
                ay = TurnToward(ay, Angle(dx, dz), turn * 2f * dt);
                if (Step(dx * speed * dt, dz * speed * dt, true)) PlayMove(Run ? "run" : "walk"); else PlayIdle();
            }
            else PlayIdle();
            Apply();
            return;
        }

        switch (task)
        {
            case Task.Goto:
            case Task.Run:
            {
                float speed = task == Task.Run ? run : walk;
                float dx = tx - x, dz = tz - z, dist = MathF.Sqrt(dx * dx + dz * dz);
                if (dist <= Math.Max(0.25f, speed * dt))
                {
                    x = tx; z = tz;
                    if (getPatch() is { } p && p.Snap(x, y, z, out var sx, out var sy, out var sz)) { x = sx; y = sy; z = sz; }
                    PlayIdle();
                    Apply();
                    EndTask(task == Task.Run ? "Runto point" : "Goto point");
                    return;
                }
                ay = TurnToward(ay, Angle(dx, dz), turn * 2f * dt);
                Step(MathF.Sin(ay) * speed * dt, MathF.Cos(ay) * speed * dt, false);
                PlayMove(task == Task.Run ? "run" : "walk");
                break;
            }
            case Task.Follow:
            case Task.Fight:
            case Task.Escape:
            {
                if (target == null || !target.Placed) { PlayIdle(); break; }
                float dx = target.x - x, dz = target.z - z, dist = MathF.Sqrt(dx * dx + dz * dz);
                bool flee = task == Task.Escape;
                float stop = task == Task.Follow ? 1.8f : 1.2f;
                bool move = flee ? dist < 8f : dist > stop + (task == Task.Follow ? 0.7f : 0f);
                if (task == Task.Fight && dist <= 1.9f && target.IsAlive) { Swing(target, dt); break; }
                if (!move)
                {
                    ay = TurnToward(ay, Angle(dx, dz), turn * 2f * dt); PlayIdle();
                    if (task == Task.Follow && followState != 2) { followState = 2; it.FireEvent("Location_CharacterFollowStay", new List<V> { V.Str("Follow character"), V.A(Ent.Root) }); }
                    break;
                }
                if (task == Task.Follow && followState != 1) { followState = 1; it.FireEvent("Location_CharacterFollowGo", new List<V> { V.Str("Follow character"), V.A(Ent.Root) }); }
                float speed = flee || task == Task.Fight || dist > 6f ? run : walk;
                float ax = dx, az = dz;
                if (!flee && getPatch() is { } patch)
                {
                    // walk around obstacles: route over the walk patch, re-planned when the target moves
                    pathTimer -= dt;
                    if (path == null || pathTimer <= 0f || (target.x - pathX) * (target.x - pathX) + (target.z - pathZ) * (target.z - pathZ) > 1f)
                    {
                        path = patch.FindPath(x, z, y, target.x, target.z, target.y);
                        pathTimer = 0.8f; pathX = target.x; pathZ = target.z;
                    }
                    while (path != null && path.Count > 1 && (path[0].x - x) * (path[0].x - x) + (path[0].z - z) * (path[0].z - z) < 0.5f * 0.5f) path.RemoveAt(0);
                    if (path != null && path.Count > 0) { ax = path[0].x - x; az = path[0].z - z; }
                }
                float want = flee ? Angle(-dx, -dz) : Angle(ax, az);
                ay = TurnToward(ay, want, turn * 2f * dt);
                Step(MathF.Sin(ay) * speed * dt, MathF.Cos(ay) * speed * dt, true);
                PlayMove(speed == run ? "run" : "walk");
                break;
            }
            case Task.Dead:
                break;
            default:
                PlayIdle();
                break;
        }
        Apply();
    }

    /// <summary>Counts the swing down and raises the hit event at the hit frame.</summary>
    void TickSwing(float dt)
    {
        atkCooldown -= dt;
        if (atkHitIn < 0f) return;
        atkHitIn -= dt;
        if (atkHitIn >= 0f) return;
        if (atkTarget != null)
        {
            float d2 = (atkTarget.x - x) * (atkTarget.x - x) + (atkTarget.z - z) * (atkTarget.z - z);
            if (atkTarget.IsAlive && IsAlive && d2 < 2.3f * 2.3f)
            {
                bool blocked = atkTarget.Defends(this);
                it.FireEvent(blocked ? "Location_CharacterBlock" : "Location_CharacterAttack", new List<V> { V.A(Ent.Root), V.A(atkTarget.Ent.Root), V.Float(1f), V.Float(1f) });
                atkTarget.PlayReaction(blocked);
            }
        }
        atkTarget = null;
    }

    /// <summary>Begins a sword swing at a target in reach (no-op while the previous swing is still running).</summary>
    public bool StartSwing(CharacterView enemy)
    {
        if (atkCooldown > 0f || atkHitIn >= 0f || !IsAlive) return false;
        ay = Angle(enemy.x - x, enemy.z - z);
        atkCooldown = 1.5f; atkHitIn = 0.35f; atkTarget = enemy;
        string a = "attack_" + (idleRng.Next(4) + 1);
        if (rig != null && rig.Play(a, false, true)) playing = a;
        return true;
    }

    void Swing(CharacterView enemy, float dt)
    {
        ay = TurnToward(ay, Angle(enemy.x - x, enemy.z - z), 12f * dt);
        if (!StartSwing(enemy) && atkHitIn < 0f && rig != null && rig.Finished) PlayIdle();
    }

    /// <summary>The nearest living person in front of this character within reach (for the player's own attacks).</summary>
    public CharacterView? NearestInFront(float reach)
    {
        CharacterView? best = null;
        float bd = reach * reach;
        foreach (var c in All.Values)
        {
            if (ReferenceEquals(c, this) || !c.Placed || !c.IsAlive) continue;
            float dx = c.x - x, dz = c.z - z, d2 = dx * dx + dz * dz;
            if (d2 < bd) { bd = d2; best = c; }
        }
        return best;
    }

    /// <summary>Position and heading in script space (Direct3D, y up) - for the save/load of character positions.</summary>
    public string PositionText() => string.Join(",", new[] { x, y, z, ay }.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

    public void RestorePosition(string text)
    {
        var p = text.Split(',');
        if (p.Length < 4) return;
        float F(string t) => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
        x = F(p[0]); y = F(p[1]); z = F(p[2]); ay = F(p[3]);
        Apply();
    }

    /// <summary>Test hook: put the character on a locator of the location.</summary>
    public void TeleportToLocator(string group, string name)
    {
        if (findLocator(group, name) is not { } l) { GD.Print($"[test] no locator {group}.{name}"); return; }
        x = l.X; y = l.Y; z = l.Z; SnapToPatch(); Apply();
    }

    // ------------------------------------------------------------------ messages from the script
    public V OnMessage(string fmt, V[] d)
    {
        if (fmt == "s") return V.Int(0);                                                   // play a sound by name (no sound yet)
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        string S(int i) => i < d.Length ? Conv.ToStr(d[i]) : "";
        float Fl(int i) => i < d.Length ? Conv.ToFloat(d[i]) : 0f;
        CharacterView? Chr(int i) => i < d.Length && d[i].Attr is { } a && All.TryGetValue(a, out var c) ? c : null;
        switch (msg)
        {
            case 30200:                                                                    // SETMODEL "lss": model, animation
            {
                model = S(1); anim = S(2);
                var newRig = CharacterRig.Create(model, anim, Ent.Root.Get("headModel")?.Value);
                if (newRig == null) return V.Int(0);
                rig?.QueueFree();
                rig = newRig;
                AddChild(newRig);
                playing = "";
                if (placeholder != null) { placeholder.QueueFree(); placeholder = null; }
                return V.Int(1);
            }
            case 30201: it.WriteRef(d[1], V.Str(model)); return V.Int(1);                  // GETMODEL "le"
            case 30202:                                                                    // GETPOS
                it.WriteRef(d[1], V.Float(x)); it.WriteRef(d[2], V.Float(y)); it.WriteRef(d[3], V.Float(z));
                return V.Int(1);
            case 30203: it.WriteRef(d[1], V.Float(ay)); return V.Int(1);                   // GETAY
            case 30204:                                                                    // DIST2D "lie"
            case 30205:
                if (Chr(1) is { } oc) { it.WriteRef(d[2], V.Float(MathF.Sqrt((oc.x - x) * (oc.x - x) + (oc.z - z) * (oc.z - z)))); return V.Int(1); }
                return V.Int(0);
            case 30210: x = Fl(1); y = Fl(2); z = Fl(3); SnapToPatch(); Apply(); return V.Int(1);         // TELEPORT
            case 30211: x = Fl(1); y = Fl(2); z = Fl(3); ay = Fl(4); SnapToPatch(); Apply(); return V.Int(1);
            case 30212:                                                                    // TELEPORT_TO_LOCATOR
            case 30330:                                                                    // ENTRY_TO_LOCATION "lss"
            {
                var l = findLocator(S(1), S(2));
                if (l == null) return V.Int(0);
                PlaceAtLocator(l);
                return V.Int(1);
            }
            case 30331: Visible = false; return V.Int(1);                                  // EXIT_FROM_LOCATION
            case 30340:                                                                    // TURNBYLOC "lss"
                if (findLocator(S(1), S(2)) is { } tl) ay = Angle(tl.M[12] - x, tl.M[14] - z);
                Apply();
                return V.Int(1);
            case 30341:                                                                    // TURNBYCHR
                if (Chr(1) is { } tc) ay = Angle(tc.x - x, tc.z - z);
                Apply();
                return V.Int(1);
            case 30342: ay = Angle(Fl(1) - x, Fl(3) - z); Apply(); return V.Int(1);        // TURNBYPOINT "lfff"
            case 30343: ay = Fl(1); Apply(); return V.Int(1);                              // TURNAY
            case 30344: case 30345: return V.Int(1);
            case 30350:                                                                    // ACTIONPLAY "ls"
                scriptAction = S(1);
                playing = "";
                return V.Int(1);
            case 30220: detectGroups.Add(S(1)); return V.Int(1);                            // ADD_DETECTOR "ls"
            case 30221: detectGroups.Remove(S(1)); return V.Int(1);                         // DEL_DETECTOR
            case 30360: case 30361: case 30362: case 30371: case 30372:
            case 21011:
                return V.Int(1);
            case 30400:                                                                    // NPCHARACTER_SETTASK
                SetTask(S(1), d, Chr);
                return V.Int(1);
            case 30401: case 30402: return V.Int(1);                                       // PUSHTASK / POPTASK
            case 30373:                                                                    // EX_MSG: queries and switches
                switch (S(1))
                {
                    case "InDialog": inDialog = Conv.ToInt(d[2]) != 0; return V.Int(1);
                    case "ChangeFightMode": fightMode = d.Length > 2 && Conv.ToInt(d[2]) != 0; return V.Int(1);
                    case "SetSex": case "SetFightWOWeapon": case "LockFightMode": return V.Int(1);
                    case "IsFightMode": return V.Int(fightMode ? 1 : 0);
                    case "IsDead": return V.Int(task == Task.Dead ? 1 : 0);
                    default: return V.Int(0);                                              // IsActive, FindDialogCharacter, ...
                }
            case 30370: return V.Int(0);                                                   // FINDNEAR
        }
        if (warned.Add($"{msg}")) GD.Print($"[character] not implemented: message {msg} ({fmt})");
        return V.Int(0);
    }

    void SetTask(string name, V[] d, Func<int, CharacterView?> chr)
    {
        float Fl(int i) => i < d.Length ? Conv.ToFloat(d[i]) : 0f;
        switch (name)
        {
            case "None": task = Task.None; break;
            case "Stay": task = Task.Stay; break;
            case "Goto point": task = Task.Goto; tx = Fl(2); ty = Fl(3); tz = Fl(4); break;
            case "Runto point": task = Task.Run; tx = Fl(2); ty = Fl(3); tz = Fl(4); break;
            case "Follow character": task = Task.Follow; target = chr(2); followState = 0; break;
            case "Fight": task = Task.Fight; target = chr(2); break;
            case "Escape": task = Task.Escape; target = chr(2); break;
            case "Dead":
                task = Task.Dead;
                if (rig != null && (rig.Play("death_0", false) || rig.Play("death_1", false))) playing = "dead";
                break;
            default:
                if (warned.Add("task " + name)) GD.Print("[character] unknown task: " + name);
                break;
        }
    }

    /// <summary>Used by the camera/main loop.</summary>
    public void SetTimeScale(float s) { if (rig != null) rig.TimeScale = s; }
}
