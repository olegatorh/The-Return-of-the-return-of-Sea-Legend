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
    /// <summary>EX_MSG "FindDialogCharacter": index of the character the player can talk to, -1 = nobody (set by Main).</summary>
    public static Func<int>? FindDialog;
    /// <summary>The locators of a group of the current location in file order (FindForvardLocator).</summary>
    public static Func<string, IEnumerable<GmFile.Label>>? GroupLocators;

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
    CharShadow? shadow;
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
            if (!value) { swingAnim = false; atkHitIn = -1f; atkTarget = null; queuedSwing = null; Blocking = false; blockUntil = 0f; }      // fgtCurType = fgt_none
            if (rig != null && Placed && rig.Play(value ? "Normal to fight" : "Fight to normal", false, true)) playing = "draw";
        }
    }

    // ---- melee / pistol: block, hit reactions, gun
    bool blocking;
    float blockAge;                                // seconds since the sword went up: a hit in the first moments is parried
    public bool Blocking
    {
        get => blocking;
        set { if (value && !blocking) blockAge = 0f; if (!value) parryIntent = false; blocking = value; }
    }
    float atkDamage = 1f, swingAy;
    bool swingAnim;                                // the blow follows the rig playback (see TickSwing)
    string swingAction = "";
    /// <summary>NPC recovery after a blow: the next action blends in over its "Blend time" (0.2 s) - the original NPC cannot start the next blow before that; the hero's pace is his input.</summary>
    float SwingRecovery => IsPlayer ? 0f : 0.2f;
    (CharacterView enemy, Strike kind)? queuedSwing;
    float queuedUntil;
    ulong swingStartMs;
    static readonly bool FightLog = System.Environment.GetEnvironmentVariable("SEADOGS_FIGHTLOG") != null;
    float fightLogT, dbgT;
    static readonly bool MeleeLog =System.Environment.GetEnvironmentVariable("SEADOGS_MELEELOG") != null;
    public enum Strike { Normal, Strong, Fast }
    float blockUntil, hitUntil, gunCooldown, shotIn = -1f;
    CharacterView? shotTarget;
    bool Busy => playing is "draw" or "block_hit" or "Shot" or "Recoil" || playing.StartsWith("attack") || playing.StartsWith("hit");

    float Skill(string key, float def) => Ent.Root.Find("chr_ai." + key, false)?.Value is { Length: > 0 } v ? Conv.ParseFloatPrefix(v) : def;

    /// <summary>A hit landed on this character: it blocks (script event Location_CharacterBlock: the script decides whether the blow pierces the block)
    /// or takes the damage (Location_CharacterAttack). NPCs raise the block through their fight AI (<see cref="OnIncomingSwing"/>, <see cref="NpcFight"/>).</summary>
    public bool Defends(CharacterView attacker) => fightMode && IsAlive && Blocking && (!IsPlayer || (blockAge >= BlockRaise && atkHitIn < 0f && atkCooldown <= 0f));      // the hero cannot guard while his own blow is playing

    /// <summary>The sword takes the "Blend time" of the block action (0.2 s in man.ani) to come up: the hero's guard holds only after that; NPCs react through their AI timing.</summary>
    const float BlockRaise = 0.2f;

    // ---- NPC fight AI. The engine's NPCharacter asks the script for its fight behaviour (Loc_ai/LAi_fightparams.c, "NPC parameters"):
    //   NPC_Event_GetAttackActive  - probability of an attack, per second (0.3 + 0.7 * fight level)
    //   NPC_Event_GetDefenceActive - probability of a defence, per second (0.3 + 0.35 * fight level)
    //   NPC_Event_GetAggresive     - balance between defence and attack 0..1 (random every time it is asked)
    //   NPC_Event_GetFireActive / NPC_Event_EnableFire - pistol shots per second / whether the NPC may shoot at all
    // and reports the course of the fight task: Location_CharacterFightGo (walking to the enemy), Location_CharacterFightStay (in reach),
    // Location_CharacterTaskFailure "Fight" (the enemy is gone), and asks Location_CharacterIsFight / Location_CharacterIsFire (template: may it use
    // weapons here / is the pistol charged). The C++ source of the decision loop is not available: how the rates turn into actions (below) is ours.
    float npcAttack = 0.5f, npcDefence = 0.4f, npcAggressive = 0.5f, npcFire = 0.01f;
    bool npcFireEnable = true;
    double npcParamsAt = -1e9;
    int fightState;                       // 0 = not reported, 1 = FightGo sent, 2 = FightStay sent
    bool fightRun = true;
    bool? fightAllowed;                   // Location_CharacterIsFight, asked once per fight task
    static readonly HashSet<string> paramsLogged = new();
    /// <summary>Test knob (env SEADOGS_NPCFIRE): multiplies the script's NPC fire rate, so a short test run sees an NPC shot.</summary>
    static readonly float FireRateScale = float.TryParse(System.Environment.GetEnvironmentVariable("SEADOGS_NPCFIRE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fr) ? fr : 1f;
    public static int StatHeroLanded, StatHeroBlockedByNpc, StatHeroHits, StatNpcSwings, StatReactBlocks, StatGuardBlocks, StatBlocked, StatParried, StatHits, StatNpcShots, StatParamQueries, StatFightGo, StatFightStay, StatFightFail;
    public static string FightStats() => $"hero blows landed={StatHeroLanded} blocked by the NPC={StatHeroBlockedByNpc}; npc swings={StatNpcSwings} blocks raised={StatReactBlocks}+{StatGuardBlocks} hits: blocked={StatBlocked} parried={StatParried} body={StatHits} npc shots={StatNpcShots} param queries={StatParamQueries} FightGo={StatFightGo} FightStay={StatFightStay} failures={StatFightFail} script-driven={StatParamQueries > 0 && StatFightStay > 0 && StatHits + StatBlocked + StatParried > 0}";

    float AskF(string ev, float def)
    {
        var r = it.FireEvent(ev, new List<V> { V.A(Ent.Root) });
        return r.T is VT.Int or VT.Float ? Conv.ToFloat(r) : def;
    }

    /// <summary>The fight parameters from the script, asked again every 2 s (the aggressiveness is rolled anew each time).</summary>
    void NpcParams()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (now - npcParamsAt < 2.0) return;
        npcParamsAt = now;
        StatParamQueries++;
        npcAttack = Math.Max(0f, AskF("NPC_Event_GetAttackActive", npcAttack));
        npcDefence = Math.Max(0f, AskF("NPC_Event_GetDefenceActive", npcDefence));
        npcAggressive = Math.Clamp(AskF("NPC_Event_GetAggresive", npcAggressive), 0f, 1f);
        npcFire = Math.Max(0f, AskF("NPC_Event_GetFireActive", npcFire));
        npcFireEnable = AskF("NPC_Event_EnableFire", 1f) != 0f;
        if (paramsLogged.Add(Id))
            GD.Print(FormattableString.Invariant($"[fight] {Id} vs {target?.Id}: attack={npcAttack:0.###}/s defence={npcDefence:0.###}/s aggressive={npcAggressive:0.##} fire={npcFire:0.###}/s fireEnabled={npcFireEnable}"));
    }


    void SetFightState(int s)
    {
        if (fightState == s) return;
        fightState = s;
        if (s == 2) StatFightStay++; else StatFightGo++;
        it.FireEvent(s == 2 ? "Location_CharacterFightStay" : "Location_CharacterFightGo", new List<V> { V.Str("Fight"), V.A(Ent.Root) });
    }

    /// <summary>The fight task cannot go on (the enemy is dead or gone, weapons are not allowed): the template is told with Location_CharacterTaskFailure.</summary>
    void FailFight()
    {
        if (task != Task.Fight) return;
        task = Task.Stay; fightState = 0; fightAllowed = null; Blocking = false; blockUntil = 0f;
        StatFightFail++;
        it.FireEvent("Location_CharacterTaskFailure", new List<V> { V.Str("Fight"), V.A(Ent.Root) });
    }

    /// <summary>Location_CharacterIsFight: the template says whether weapons may be used here (LAi_LocationCanFight). No template = allowed.</summary>
    bool AskIsFight()
    {
        var r = it.FireEvent("Location_CharacterIsFight", new List<V> { V.A(Ent.Root) });
        return r.T == VT.Void || Conv.ToInt(r) != 0;
    }

    /// <summary>Location_CharacterIsFire: the template says whether the pistol can shoot now (LAi_CharacterCanFrie: chr_ai.charge >= 1).</summary>
    bool AskIsFire()
    {
        var r = it.FireEvent("Location_CharacterIsFire", new List<V> { V.A(Ent.Root) });
        return r.T != VT.Void && Conv.ToInt(r) != 0;
    }

    /// <summary>The player draws / sheathes the sword: drawing asks the template first (Location_CharacterIsFight; the script prints "CantFight").</summary>
    public bool ToggleFightMode()
    {
        if (isSwim || isJump) return false;                    // character.cpp: no sword while swimming or in the air
        if (rig != null && playing is "draw" or "Shot" or "Recoil" && !rig.Finished) return false;   // SetFightMode: ignored while a priority action plays
        if (!fightMode && !AskIsFight()) return false;
        FightMode = !fightMode;
        return true;
    }

    // np_character.cpp (UpdateFightCharacter / FightTick / DoFightAction*): every 0.2 s the NPC rolls its wishes against the script's values
    // (wantToAttack = PrTest(attackCur), wantToDefence = PrTest(defenceCur), wantToFire = PrTest(fireCur): the values are chances per roll, so a fighter
    // with attack 1.0 swings the moment its last blow is over). Free (no blow, no held guard) and an enemy in reach: a wish to defend while somebody
    // swings at it becomes a block or a parry held for blockTime, otherwise a wish to attack picks the best enemy around and a blow by the engine's
    // weights (fast 0.2, force 0.5, round 0.1, break 0.1, feint 0.1). The script's NPC_Event_GetAggresive (PotC only) decides when both are wanted.
    float fightTickT;
    bool wantAtk, wantDef, wantFire;
    CharacterView? parryVs;               // the attacker whose blow is to be parried: the guard goes up just before the strike
    bool parryIntent;                     // the raised guard is a parry (the attacker is thrown off balance)
    Strike lastKind = Strike.Normal;

    float FightLevel => Math.Clamp((npcAttack - 0.3f) / 0.7f, 0f, 1f);       // the script gives attack = 0.3 + 0.7 * fight level

    void NpcFightTick(float dt)
    {
        if ((fightTickT -= dt) > 0f) return;
        fightTickT = 0.2f;
        wantAtk = idleRng.NextDouble() < Math.Min(npcAttack, 1000f);
        wantDef = idleRng.NextDouble() < Math.Min(npcDefence, 1000f);
        wantFire = idleRng.NextDouble() < npcFire * FireRateScale;
    }

    /// <summary>The blow by the engine's weights; the animations of man.ani stand in for the engine's fast / force / break actions.</summary>
    Strike NpcStrike()
    {
        double r = idleRng.NextDouble() * 1.0;           // fast 0.2, force 0.5, break 0.1 (round needs 3+ enemies, feint has no animation here): normalised
        if (r < 0.25) return Strike.Fast;
        if (r < 0.88) return Strike.Normal;
        return Strike.Strong;
    }

    /// <summary>DoFightBlock: the guard goes up for blockTime = min + rand * (max - min), min = 0.8 + (1 - level) * 0.8, max = min + (1.1 - level) * 2.5; block or parry (0.9 / 0.1).</summary>
    void NpcBlock(bool needParry)
    {
        float fl = FightLevel, min = 0.8f + (1f - fl) * 0.8f, max = min + (1.1f - fl) * 2.5f;
        blockUntil = min + (float)idleRng.NextDouble() * (max - min);
        parryIntent = needParry || idleRng.NextDouble() * 1.0 > 0.9;
        Blocking = true; StatGuardBlocks++;
    }

    /// <summary>An NPC in reach of its enemy (DoFightActionAnalysisNone).</summary>
    void NpcFight(CharacterView enemy, float dist, float dt)
    {
        if (parryVs != null)                                  // a parry waits for the last moment of the blow
        {
            if (!parryVs.IsAlive || parryVs.atkTarget != this || parryVs.atkHitIn < 0f) parryVs = null;
            else if (parryVs.atkHitIn <= 0.14f) { parryVs = null; NpcBlock(true); }
        }
        // a hit reaction only stuns for hitUntil (0.35 s): the NPC may block or strike again then, even if the long reaction clip has not finished (it used to be locked for the whole clip, so a hero hitting every second was never answered)
        bool reacting = (playing == "hit" || playing == "block_hit") && hitUntil > 0f;
        bool clipBusy = Busy && rig != null && !rig.Finished && !((playing == "hit" || playing == "block_hit") && hitUntil <= 0f);
        if (atkHitIn >= 0f || shotIn >= 0f || blockUntil > 0f || reacting || clipBusy) { PlayIdle(); return; }
        ay = TurnToward(ay, Angle(enemy.x - x, enemy.z - z), 12f * dt);
        if (!wantAtk && !wantDef) { PlayIdle(); return; }
        // everybody around: the best victim (hurt, in front) and who is swinging at us
        CharacterView? victim = null; float best = float.MinValue; int attackers = 0, enemies = 0; CharacterView? threat = null;
        foreach (var c in All.Values)
        {
            if (ReferenceEquals(c, this) || !c.Placed || !c.IsAlive || !GroupsState.AreEnemies(Ent.Root, c.Ent.Root)) continue;
            float dx = c.x - x, dz = c.z - z, d = MathF.Sqrt(dx * dx + dz * dz);
            if (d > 2.4f) continue;
            enemies++;
            float cs = d > 0.01f ? (MathF.Sin(ay) * dx + MathF.Cos(ay) * dz) / d : 0f;
            float hp = Math.Clamp(c.HpNow / Math.Max(1f, c.HpMax), 0f, 1f);
            float k = 0.1f / (hp + 0.05f) + 0.2f / 1.5f + (cs + 1f) * 0.5f;
            if (k > best) { best = k; victim = c; }
            if (c.atkTarget == this && c.atkHitIn >= 0f && cs > -0.3f) { attackers++; threat ??= c; }
        }
        if (victim == null) { victim = enemy; }
        bool defend = wantDef && attackers > 0 && (!wantAtk || idleRng.NextDouble() >= npcAggressive);
        if (defend)
        {
            wantDef = false;
            float fl = FightLevel;
            bool needParry = threat!.lastKind == Strike.Strong && idleRng.NextDouble() < fl * fl * 0.9;       // a piercing blow: recognised by the better fighters
            // a step back instead of the guard (Character::Recoil, chance attacked^2 * 0.1 * (level + 0.3)), at most 0.9
            if (idleRng.NextDouble() < Math.Min(0.9, attackers * attackers * 0.1 * (fl + 0.3))) { Recoil(); return; }
            if (needParry || idleRng.NextDouble() * 1.0 > 0.9)
            {
                if (threat.atkHitIn > 0.14f) { parryVs = threat; PlayIdle(); return; }
                NpcBlock(true);
            }
            else NpcBlock(false);
            PlayIdle();
            return;
        }
        if (wantAtk)
        {
            wantAtk = false;
            if (atkCooldown <= 0f) StartSwing(victim, NpcStrike());
            return;
        }
        wantDef = false;
        PlayIdle();
    }

    /// <summary>An NPC with a pistol shoots when it wants to (the script's fire rate) and the template allows it (Location_CharacterIsFire: the gun is charged):
    /// np_character.cpp fires only beyond NPC_FIGHT_GO_DIST (2.4 m) unless the fighter is good (level above 0.6), at most 25 m away.</summary>
    void NpcMaybeFire(CharacterView enemy, float dist)
    {
        if (!wantFire) return;
        wantFire = false;
        if (!npcFireEnable || gunCooldown > 0f || shotIn >= 0f || atkHitIn >= 0f || dist > 25f || (dist <= 2.4f && FightLevel <= 0.6f)) return;
        if (string.IsNullOrEmpty(Ent.Root.Find("equip.gun", false)?.Value)) return;
        if (!AskIsFire()) return;
        if (TryFire(enemy)) StatNpcShots++;
    }

    public float HpNow => Ent.Root.Find("chr_ai.hp", false) is { } h ? Conv.ParseFloatPrefix(h.Value) : 0f;
    public float HpMax => Ent.Root.Find("chr_ai.hp_max", false) is { } h ? Conv.ParseFloatPrefix(h.Value) : 1f;

    /// <summary>Port aid (not in the original interface): after the hero's blow the target shows its health bar for a few seconds and the damage rises as a number.</summary>
    public void ShowHit(float damage, bool blocked)
    {
        HitFx.Show(this, damage, blocked, HpNow, Math.Max(1f, HpMax));
    }

    void PlayReaction(bool blocked)
    {
        if (rig == null || !IsAlive) return;
        if (!blocked) ChrSnd("Event_ChrSnd_Hit");                         // the script picks man_hit / woman_hit / skeleton_hit ... by sex
        string a = blocked ? "block_hit" : "hit_" + (idleRng.Next(3) + 1);
        if (rig.Play(a, false, true)) { playing = a.StartsWith("hit") ? "hit" : a; hitUntil = 0.35f; }
    }

    /// <summary>Pistol shot (the original control ChrFire): the nearest living target in front within reach; the script applies the damage.</summary>
    public bool TryFire(CharacterView? aim = null)
    {
        if (!fightMode || !IsAlive || gunCooldown > 0f || shotIn >= 0f || rig == null) return false;
        if (IsPlayer)
        {
            if (Skill("charge", 0f) < 1f) { it.FireEvent("Location_CharacterNoCharge"); return false; }
            if (!AskIsFire()) return false;
        }
        CharacterView? best = null; float bd = 24f * 24f;
        if (aim != null && aim.Placed && aim.IsAlive && (aim.x - x) * (aim.x - x) + (aim.z - z) * (aim.z - z) < bd) best = aim;
        else foreach (var c in All.Values)
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
        if (blocking) blockAge += dt;
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
        Snd("pistol_shot");
        if (t == null) Snd("bullet_miss");
        it.FireEvent("Location_CharacterFire", new List<V> { V.A(Ent.Root), V.A(t?.Ent.Root), V.Float(Math.Clamp(1f - d / 24f, 0f, 1f)), V.Int(t != null ? 1 : 0) });
        t?.PlayReaction(false);
    }
    public Vector3 ScriptPos => new(x, y, z);
    public bool IsAlive => task != Task.Dead && Ent.Root.Find("chr_ai.hp", false) is { } hp && Conv.ParseFloatPrefix(hp.Value) >= 1f;                // 0 = not reported yet, 1 = walking after the target, 2 = arrived (events FollowGo / FollowStay)
    string scriptAction = "";
    string playing = "";
    bool inDialog;
    /// <summary>The script announced a dialog to this character (EX_MSG "InDialog"); the dialog camera looks for the partner with it.</summary>
    public bool InDialog => inDialog;

    // player control, set by the main loop every frame
    public Vector2 Input;           // x: strafe right, y: forward (camera relative)
    public bool Run;
    public float CameraYaw;         // Godot yaw of the camera
    public bool Classic;            // the original scheme: Input.Y moves along the heading (negative = backwards), TurnAdd turns the hero
    public float TurnAdd;           // Player::Rotate: heading change wanted this frame (rad, already scaled; consumed by Tick)
    public bool LookHidden { set { if (rig != null) rig.Visible = !value; } }    // first-person camera: the model is not drawn

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
        AddChild(new MeshInstance3D { Mesh = BlobMesh(), MaterialOverride = BlobMaterial(), Position = new Vector3(0, 0.04f, 0), Name = "Shadow", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        AddChild(new Label3D { Text = id, Position = new Vector3(0, 2.1f, 0), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.004f, NoDepthTest = true, Name = "NameTag" });
    }

    public override void _ExitTree() => All.Remove(Ent.Root);

    // the engine's "shadow" class: a soft dark blob on the ground under every character
    static PlaneMesh? blobMesh;
    static ShaderMaterial? blobMat;
    static PlaneMesh BlobMesh() => blobMesh ??= new PlaneMesh { Size = new Vector2(1.2f, 1.2f) };
    // the walk patch lies a few centimetres under the drawn floor: the blob is pulled 0.1 m towards the camera in view space so it is not buried
    const string BlobShader = @"
shader_type spatial;
render_mode unshaded, depth_draw_never, cull_disabled, blend_mix;
varying float VERTEX_VIEW_D;
void vertex() {
    vec4 v = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    v.xyz -= normalize(v.xyz) * 0.1;
    VERTEX_VIEW_D = length(v.xyz);
    POSITION = PROJECTION_MATRIX * v;
}
void fragment() {
    float r = length(UV - vec2(0.5)) * 2.0;
    float d = VERTEX_VIEW_D;
    ALBEDO = vec3(0.0);
    // shadow.cpp: Shadow.Density.Foot 0x60 = 0.38; fades out between 8 and 16 m from the camera
    ALPHA = 0.38 * (1.0 - smoothstep(0.15, 1.0, r)) * (1.0 - smoothstep(8.0, 16.0, d));
}";
    static ShaderMaterial BlobMaterial() => blobMat ??= new ShaderMaterial { Shader = new Shader { Code = BlobShader }, RenderPriority = 1 };

    public void ShowNameTag(bool on) { if (GetNodeOrNull<Label3D>("NameTag") is { } l) l.Visible = on; }

    // ------------------------------------------------------------------ placement
    /// <summary>The engine puts a character on the nearest point of the location's walkable patch.</summary>
    /// <summary>Test hook: stand <paramref name="dist"/> metres in front of another character, facing it.</summary>
    public void PlaceNear(CharacterView o, float dist)
    {
        x = o.x + MathF.Sin(o.ay) * dist; z = o.z + MathF.Cos(o.ay) * dist; y = o.y; ay = o.ay + MathF.PI;
        SnapToPatch(); Apply();
    }

    void SnapToPatch()
    {
        if (getPatch() is { } p && p.Snap(x, y, z, out var nx, out var ny, out var nz)) { x = nx; y = ny; z = nz; }
        node = -1; vy = 0f;
    }

    void Apply()
    {
        Position = new Vector3(x, isSwim ? seaY : y, -z);
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

    public string DebugState() => $"{Ent.Root.Get("id")?.Value} idx={Ent.Root.Get("index")?.Value} class={Ent.Class} placed={Placed} inDialog={inDialog} task={task} pos=({x:0.0},{y:0.0},{z:0.0}) target={target?.Ent.Root.Get("id")?.Value} anim={rig?.ActionName} hp={Ent.Root.Find("chr_ai.hp", false)?.Value}";

    static float TurnToward(float cur, float want, float maxStep) => CharacterMath.TurnToward(cur, want, maxStep);

    // ------------------------------------------------------------------ per-frame update (called by the main loop)
    void PlayMove(string action)
    {
        if (rig == null) return;
        if (isSwim) action = "swim";
        if (fightMode) action = action switch { "walk" => "fight walk", "run" => "fight run", "back walk" => "fight back walk", "back run" => "fight back run", _ => action };
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
        if (isSwim) { if (playing != "swim" && rig.Play("swim", true)) playing = "swim"; return; }
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
        if (playing != want)
        {
            // ACTIONPLAY: the action plays as the .ani says (most stand-ins loop, "loop = false" ones play once)
            if (!rig.Play(want, scriptAction.Length == 0 || rig.ActionLoops(want))) rig.Play("idle_2", true);
            playing = want; endActionSent = false;
        }
        // Character::EventListener: the end of a one-shot user action is reported once (the "ani" template completes its animation by it)
        if (scriptAction.Length > 0 && !endActionSent && !rig.ActionLoops(want) && rig.Finished)
        {
            endActionSent = true;
            it.FireEvent("Location_Character_EndAction", new List<V> { V.A(Ent.Root) });
        }
    }
    bool endActionSent;

    // ------------------------------------------------------------------ locomotion (location/src/character.cpp: Move/Update, ai_character.cpp, supervisor.cpp)
    // The character walks along its heading at a speed blended towards the action's "Walk speed" (Blender: old + t^0.1 * delta over "Blend time"),
    // turns at the action's "Turn speed" (constant rate), falls with g = 9.8 and is kept on the walk patch by PtcData::Move (node graph, sliding along border edges).
    int node = -1;                          // triangle of the walk patch under the character (currentNode)
    float vy;                               // vertical speed
    float spd, bsOld, bsDlt, bsT = 1f, bsK; // blended speed (m/s, negative = backwards)
    float turnSpd = 2f, nay;
    bool hasNay, isMove, isBack, wantRun, pushMove;
    string curMove = "";
    float kSpd = 1f, kSpdCur = 1f;
    readonly float kSpdDlt = (float)(Random.Shared.NextDouble() * 0.1 - 0.05);       // character.cpp: rand() * 0.1 / RAND_MAX - 0.05
    readonly float likeKSpd = 0.9f + (float)Random.Shared.NextDouble() * 0.2f;       // ai_character.cpp
    float movecs = 1f;                      // smoothed floor normal y (stairs below CHARACTER_COS_STAIR 0.9)
    bool isUp;
    public bool IsSliding;                  // the last move ran into the patch border (ptc.isSlide)
    float slideX, slideZ;
    float gfX, gfZ;                         // goForce (read by the neighbours' alignment / bypass forces)
    CharacterView? exch;                    // the character we do not collide with (SetExCharacter)
    bool isCollision;
    float collisionValue;
    public const float RadiusNormal = 0.7f, RadiusFight = 1.5f, CharHeight = 1.8f;
    float Radius => fightMode ? RadiusFight : RadiusNormal;
    public static int StatColThreshold, StatBusyPos, StatEscapeSlide;
    static readonly bool PhysLog = System.Environment.GetEnvironmentVariable("SEADOGS_PHYSLOG") != null;
    int physN;

    // goto command state (CmdGotoPoint)
    float gotoWait;
    bool gotoIsWait;
    int gotoBusyCnt;

    void BlendSpeed(float to, float time)
    {
        bsOld = spd; bsDlt = to - spd;
        if (time > 0f) { bsK = 1f / time; bsT = 0f; } else { bsK = 1f; bsT = 1f; }
    }

    void SetMove(string action)
    {
        if (action == curMove) return;
        curMove = action;
        if (action.Length > 0 && rig?.MoveData(action) is { } m) { BlendSpeed(m.speed, m.blend); turnSpd = m.turn; }
        else
        {
            BlendSpeed(0f, lockBlend > 0f ? lockBlend : 0.4f);                             // standing: the idle's blend time (a priority action: its own), the file's "Turn speed"
            turnSpd = lockBlend > 0f && Blocking && fightMode ? 5f : rig?.IdleTurnSpeed ?? 2f;
        }
    }
    float lockBlend;
    bool drawLogged, wasRunning;

    /// <summary>Character::Turn(ay): the heading to turn to (at the action's turn rate).</summary>
    void Turn(float want) { nay = want; hasNay = true; }

    /// <summary>One frame of Character::Move + Character::Update: speed factor, blended speed, heading, gravity and the patch move.</summary>
    void Locomote(float dt)
    {
        if (rig == null) return;
        // speed factor
        kSpd = Math.Clamp(kSpd, 0f, 1f);
        kSpdCur += (kSpd - kSpdCur) * Math.Min(dt * 0.5f, 1f);
        float k = kSpdCur * 0.3f + 0.7f + kSpdDlt;
        rig.SpeedMul = k;
        // which move action (UpdateAnimation): stairs when the floor is steeper than cos 0.9 and the character does not run
        string act = "";
        // character.cpp UpdateAnimation / SetPriorityAction: the draw / sheathe animation (priority action), the blows, reactions, the block and the shot are set with
        // move speed 0 (SetAction(name, blend, 0.0f, rot)) and StartMove / StopMove / SetRunMode are ignored while a priority action plays: the character stops
        // where it stands (no running "on a skateboard" through the draw animation); a block can still turn (5 rad/s), the other actions cannot
        bool actionLock = rig.ActionLocked(Busy), guardLock = Blocking && fightMode && !actionLock;
        if (actionLock || guardLock)
        {
            isMove = false; wantRun = false;
            if (actionLock) hasNay = false;
            lockBlend = actionLock ? rig.MoveData(rig.ActionName)?.blend ?? 0.2f : 0.2f;
        }
        else lockBlend = 0f;
        if (IsPlayer)                                                     // the log of the regression run 'drawrun'
        {
            bool drawing = actionLock && playing == "draw";
            if (drawing && !drawLogged && Math.Abs(spd) > 1f) { drawLogged = true; wasRunning = true; GD.Print(FormattableString.Invariant($"[move] hero stopped for the draw animation: speed {spd:0.0} m/s, move input ignored until it ends")); }
            else if (!drawing && drawLogged && !actionLock && isMove) { drawLogged = false; GD.Print("[move] hero moves again after the draw animation"); }
        }
        if (runDisable) wantRun = false;                                  // no running in water above the ankles
        if (isMove)
        {
            if (isSwim) act = "swim";
            else if (fightMode) act = wantRun ? (isBack ? "fight back run" : "fight run") : (isBack ? "fight back walk" : "fight walk");
            else if (movecs > 0.9f || wantRun) act = wantRun ? (isBack ? "back run" : "run") : (isBack ? "back walk" : "walk");
            else act = isBack ? (isUp ? "back stairs up" : "back stairs down") : (isUp ? "stairs up" : "stairs down");
        }
        SetMove(act);
        if (bsK > 0f)
        {
            bsT += bsK * dt;
            if (bsT > 1f) { bsT = 1f; bsK = 0f; }
            spd = bsOld + MathF.Pow(bsT, 0.1f) * bsDlt;
        }
        // moving along the heading, turning
        float s = dt * spd * k;
        float nx = x + MathF.Sin(ay) * s, nz = z + MathF.Cos(ay) * s;
        if (hasNay) { float old = ay; ay = TurnToward(ay, nay, turnSpd * dt); if (MathF.Abs(ay - nay) < 1e-4f) hasNay = false; _ = old; }
        kSpd = 1f;
        // gravity + the patch
        vy -= 9.8f * dt;
        PatchMove(nx, y + vy * dt, nz);
        UpdateSwim(dt);
        if (isSwim) vy = 0f;
        // Character::Update: at a border of the walk patch over the jump patch the hero steps off the ledge
        if (EnableJump && IsSliding && !fightMode && !isSwim && !Busy && TestJump(new Vector3(x + 0.001f * MathF.Sin(ay), y - 0.001f, z + 0.001f * MathF.Cos(ay)))) BeginJump(false);
        var patch = getPatch();
        if (patch != null && node >= 0)
        {
            float kk = Math.Min(10f * dt, 1f);
            var nn = patch.NodeNormal(node);
            movecs += (nn.y - movecs) * kk;
            isUp = nn.x * MathF.Sin(ay) + nn.z * MathF.Cos(ay) <= 0f;
        }
        if (PhysLog && (isMove || MathF.Abs(spd) > 0.01f) && ++physN % 10 == 0) GD.Print($"[phys] {Id} pos=({x:0.00},{y:0.00},{z:0.00}) ay={ay:0.00} spd={spd:0.00} k={k:0.00} act={curMove} node={node} slide={IsSliding} stairs={movecs:0.00}");
    }

    /// <summary>Moves the character to (nx, ny, nz) over the walk patch: finds the node first when the character has none, then PtcData::Move.</summary>
    void PatchMove(float nx, float ny, float nz)
    {
        var patch = getPatch();
        if (patch == null || patch.Tris.Length == 0) { x = nx; z = nz; vy = 0f; return; }
        if (node < 0)
        {
            node = patch.FindNode(x, y, z, out _);
            if (node < 0) node = patch.NearestTri(x, z, y);
        }
        var pos = (x, y, z);
        node = patch.Move(node, (nx, ny, nz), ref pos);
        x = pos.x; y = pos.y; z = pos.z;
        if (patch.IsBearing) vy = 0f;
        IsSliding = patch.IsSlide;
        if (IsSliding) { slideX = patch.SlideDirX; slideZ = patch.SlideDirZ; }
        if (y < -1000f) { y = -500f; vy = 0f; }
    }

    /// <summary>Walks by an offset (the recoil step) over the patch; true if the character moved.</summary>
    bool Step(float dx, float dz)
    {
        float ox = x, oz = z;
        PatchMove(x + dx, y, z + dz);
        return (x - ox) * (x - ox) + (z - oz) * (z - oz) > 1e-8f;
    }

    /// <summary>Supervisor::CheckPosition: no other character stands at the point.</summary>
    bool PosFree(float px, float py, float pz)
    {
        foreach (var c in All.Values)
        {
            if (ReferenceEquals(c, this) || !c.Placed || !c.Visible) continue;
            if (MathF.Abs(py - c.y) > CharHeight * 0.8f) continue;
            float dx = px - c.x, dz = pz - c.z;
            if (dx * dx + dz * dz > c.Radius * 0.8f) continue;
            return false;
        }
        return true;
    }

    /// <summary>AICharacter::CalcRepulsionForces + Calculate: separation, alignment, bypass and patch-edge forces summed with the go force.</summary>
    (float x, float z) SteerForce(float goX, float goZ)
    {
        gfX = goX; gfZ = goZ;
        float sepX = 0, sepZ = 0, alX = 0, alZ = 0, arX = 0, arZ = 0;
        var near = new List<(CharacterView c, float d, float maxD)>();
        foreach (var c in All.Values)
        {
            if (ReferenceEquals(c, this) || !c.Placed || !c.Visible || !c.IsAlive) continue;
            float dx = c.x - x, dz = c.z - z, d = dx * dx + dz * dz, rr = (Radius + c.Radius) * 4f;
            if (d > rr * rr) continue;
            near.Add((c, MathF.Sqrt(d), rr));
        }
        if (near.Count > 0)
        {
            float kn = 1f / near.Count;
            foreach (var (c, d, maxD) in near)
            {
                if (d == 0f || ReferenceEquals(exch, c) || ReferenceEquals(c.exch, this)) continue;
                float dx = c.x - x, dz = c.z - z, kd = 1f / d, kr = kn * (maxD - d) / maxD;
                sepX -= dx * kr * kd * kd; sepZ -= dz * kr * kd * kd;
                float k = 1f - MathF.Abs(goX * c.gfX + goZ * c.gfZ);
                float afx = (goX + c.gfX) * (kn * kr * kd * k), afz = (goZ + c.gfZ) * (kn * kr * kd * k);
                if (afx * dx + afz * dz < 0f) { alX += afx; alZ += afz; } else { alX -= afx; alZ -= afz; }
                float kcs = goX * dx + goZ * dz;
                if (kcs > 0f)
                {
                    k = kn * kr * kd * kcs;
                    if (goX * dz - goZ * dx < 0f) k = -k;
                    arX += k * goZ; arZ += k * -goX;
                }
            }
        }
        CharacterMath.LimitLength(ref sepX, ref sepZ); CharacterMath.LimitLength(ref alX, ref alZ); CharacterMath.LimitLength(ref arX, ref arZ);
        float ks = fightMode ? 0.5f : 1.1f;
        float fx = sepX * ks + alX * 0.1f + arX * 0.1f + goX * 1.2f, fz = sepZ * ks + alZ * 0.1f + arZ * 0.1f + goZ * 1.2f;
        if (getPatch() is { } p && node >= 0) { var (ex, ez) = p.EdgeForce(node); fx += ex * 0.2f; fz += ez * 0.2f; }
        return (fx, fz);
    }

    /// <summary>The steering of AICharacter::CmdUpdateGotoPoint: turn along the summed force, slow down when it is weak.</summary>
    void FollowForce(float fx, float fz)
    {
        Turn(Angle(fx, fz));
        float l = fx * fx + fz * fz;
        kSpd = l < 0.7f ? likeKSpd * l / 0.7f : likeKSpd;
    }

    static float pushAng;

    /// <summary>Supervisor::Update after the moves: characters closer than the mean of their radii are pushed apart (the one that stands still gives way),
    /// then the patch is asked again; a character that keeps colliding raises Location_CharacterColThreshold (AICharacter::Update).</summary>
    public static void PostMove(IList<CharacterView> list, float dt)
    {
        if (dt <= 0f) return;
        var live = list.Where(c => c.Placed && c.Visible && c.rig != null && c.task != Task.Dead && c.IsAlive).ToList();
        var before = live.ToDictionary(c => c, c => (c.x, c.z));
        foreach (var c in live) c.isCollision = false;
        for (int i = 0; i < live.Count - 1; i++)
            for (int j = i + 1; j < live.Count; j++)
            {
                var ci = live[i]; var cj = live[j];
                float r = ci.Radius + cj.Radius;
                float dx = ci.x - cj.x, dz = ci.z - cj.z, d = dx * dx + dz * dz;
                if (d > r * r * 4f) continue;
                if (cj.y > ci.y + CharHeight || ci.y > cj.y + CharHeight) continue;
                r *= 0.5f;
                if (d >= r * r) continue;
                if (d <= 0.25f)
                {
                    dx = 0.5f * MathF.Cos(pushAng); dz = 0.5f * MathF.Sin(pushAng); d = dx * dx + dz * dz;
                    pushAng += MathF.PI / 64f;
                }
                d = MathF.Sqrt(d); d = (r - d) / d; dx *= d; dz *= d;
                ci.isCollision = cj.isCollision = true;
                bool moveI = ci.pushMove, moveJ = cj.pushMove;
                float wi, wj;                                   // shares of the push taken by i (away) and j
                if (ci.fightMode) (wi, wj) = moveI == moveJ ? (0.5f, 0.5f) : moveI ? (0.999f, 0.001f) : (0.001f, 0.999f);
                else (wi, wj) = moveI == moveJ ? (0.5f, 0.5f) : moveI ? (0.9f, 0.1f) : (0.1f, 0.9f);
                ci.x += dx * wi; ci.z += dz * wi; cj.x -= dx * wj; cj.z -= dz * wj;
            }
        foreach (var c in live)
        {
            var (ox, oz) = before[c];
            float px = c.x, pz = c.z;
            if (px != ox || pz != oz) { c.x = ox; c.z = oz; c.PatchMove(px, c.y, pz); c.Apply(); }
            if (c.isCollision)
            {
                c.collisionValue += dt;
                if (c.collisionValue > 1f)
                {
                    c.collisionValue = 0.8f;
                    StatColThreshold++;
                    c.it.FireEvent("Location_CharacterColThreshold", new List<V> { V.Str(c.TaskName), V.A(c.Ent.Root) });
                }
            }
            else c.collisionValue = Math.Max(0f, c.collisionValue - dt * 0.1f);
        }
    }

    string TaskName => task switch
    {
        Task.None => "None", Task.Stay => "Stay", Task.Goto => "Goto point", Task.Run => "Runto point", Task.Follow => "Follow character",
        Task.Fight => "Fight", Task.Escape => "Escape", _ => "Dead",
    };

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

    /// <summary>AICharacter::CmdProcessGotoPoint / CmdUpdateGotoPoint: turn along the summed force, move only when facing the way (dot >= 0.9 - fl * 0.1).</summary>
    void SteerGo(float gx, float gz, float distFinal, bool run, float dt)
    {
        float fl = MathF.Sqrt(gx * gx + gz * gz);
        if (fl > 0f) { gx /= fl; gz /= fl; } else { gx = MathF.Sin(ay); gz = MathF.Cos(ay); }
        float f2 = Math.Clamp((fl - 0.5f) / (2.0f - 0.5f), 0f, 1f);
        var (fx, fz) = SteerForce(gx, gz);
        pushMove = gx * MathF.Sin(ay) + gz * MathF.Cos(ay) >= 0.9f - f2 * 0.1f;     // Process: StartMove only when facing the way ...
        isMove = true;                                                              // ... but Update calls StartMove() again: the walker always walks and turns along the forces
        if (!pushMove && gotoCool <= 0f && idleRng.NextDouble() < 1.0 - Math.Exp(-dt * 60.0 / 1024.0))
        {
            // turned away from the goal by the other forces: now and then (1 frame in 1024 at 60 fps) stop for 1.2 s and face the goal
            gotoIsWait = true; gotoWait = 1.2f; waitTurn = true; waitAng = Angle(gx, gz);
            Turn(waitAng);
            return;
        }
        wantRun = run && distFinal >= 1.5f;                             // "slow down in front of the locator"
        if (PhysLog && ++physN % 20 == 0) GD.Print($"[steer] {Id} go=({gx:0.00},{gz:0.00}) fl={fl:0.00} force=({fx:0.00},{fz:0.00}) ay={ay:0.00} move={isMove} final={distFinal:0.0}");
        FollowForce(fx, fz);
    }

    float gotoCool, waitAng;
    bool waitTurn;

    /// <summary>The wait state of AICharacter::CmdUpdateGotoPoint: stand still (turning to the wanted way after a random hold-up), and when the wait is over
    /// continue if the goal is free, wait again up to 3 times, then the task fails. True while the character waits.</summary>
    bool GotoWaiting(float dt, bool busy, float px, float pz)
    {
        if (!gotoIsWait) { gotoCool -= dt; return false; }
        isMove = false;
        if (waitTurn) Turn(waitAng);
        gotoWait -= dt;
        if (gotoWait > 0f) return true;
        if (!busy || PosFree(px, y, pz))
        {
            gotoIsWait = false;
            gotoCool = 1f + (float)(idleRng.NextDouble() / (idleRng.NextDouble() + 0.0001));         // 1 + rand / (rand + 1)
            if (gotoCool > 4f) gotoCool = 4f;
            return false;
        }
        if (gotoBusyCnt < 3) { gotoBusyCnt++; gotoWait = 2f + (float)idleRng.NextDouble() * 2f; return true; }
        FailTask();
        return true;
    }

    /// <summary>The next waypoint towards (gx, gz) over the walk patch (triangle route re-planned when the goal moves), or the goal itself.</summary>
    (float x, float z) NextWaypoint(float gx, float gz, float gy, float dt)
    {
        if (getPatch() is not { } patch) return (gx, gz);
        pathTimer -= dt;
        if (path == null || pathTimer <= 0f || (gx - pathX) * (gx - pathX) + (gz - pathZ) * (gz - pathZ) > 1f)
        {
            path = patch.FindPath(x, z, y, gx, gz, gy);
            pathTimer = 0.8f; pathX = gx; pathZ = gz;
        }
        while (path != null && path.Count > 1 && (path[0].x - x) * (path[0].x - x) + (path[0].z - z) * (path[0].z - z) < 1.0f * 1.0f) path.RemoveAt(0);     // wider than the run turn radius (3.3 / 5.5 = 0.6 m), or the walker orbits the waypoint
        return path != null && path.Count > 0 ? path[0] : (gx, gz);
    }

    /// <summary>The goto command failed (the point stays occupied): the task ends and the template is told with Location_CharacterTaskFailure.</summary>
    void FailTask()
    {
        string name = TaskName;
        task = Task.Stay; gotoIsWait = false;
        it.FireEvent("Location_CharacterTaskFailure", new List<V> { V.Str(name), V.A(Ent.Root) });
    }

    public void Tick(float dt)
    {
        if (!Placed || rig == null) return;
        if (FightLog && task == Task.Fight && (dbgT -= dt) <= 0f) { dbgT = 1f; GD.Print($"[npc] {Id} tick: task={task} inDialog={inDialog} jump={isJump} target={target?.Id} fightAllowed={fightAllowed} fightState={fightState}"); }
        if (rig.Events.Count > 0) { var evs = rig.Events.ToArray(); rig.Events.Clear(); foreach (var e in evs) OnAnimEvent(e); }
        Detect(dt);
        UpdateEquipment();
        if (dt > 0f) { TickSwing(dt); TickGun(dt); TickHealLog(); }
        if (inDialog || dt <= 0f) { isMove = false; if (inDialog) DialogFace((float)GetProcessDeltaTime()); PlayIdle(); return; }
        isMove = false; pushMove = false; isBack = false; wantRun = false; hasNay = false;
        var patch = getPatch();
        EnableJump = IsPlayer && !ScriptControlled && task is Task.Stay or Task.None;
        if (isJump) { TickJump(dt); return; }

        // player: keyboard control while the script lets the player act (the engine's "special mode": turn to the wanted direction and walk forward)
        if (IsPlayer && !ScriptControlled && task is Task.Stay or Task.None)
        {
            if (TurnAdd != 0f) { Turn(ay + Math.Clamp(TurnAdd, -0.3f, 0.3f)); TurnAdd = 0f; }     // Player::Rotate: a = rotate * 0.01, at most 0.3 rad ahead of the heading
            if (Classic)
            {
                if (MathF.Abs(Input.Y) > 0.01f)
                {
                    isBack = Input.Y < 0f; isMove = true; pushMove = true; wantRun = Run;
                    PlayMove((isBack ? "back " : "") + (Run ? "run" : "walk"));
                }
                else PlayIdle();
            }
            else if (Input.LengthSquared() > 0.01f)
            {
                // camera-relative move in Godot space -> Direct3D space (z flips)
                float s = MathF.Sin(CameraYaw), c = MathF.Cos(CameraYaw);
                var fwd = new Vector2(-s, -c);      // Godot (x, z)
                var right = new Vector2(c, -s);
                var m = (fwd * Input.Y + right * Input.X).Normalized();
                Turn(Angle(m.X, -m.Y));
                isMove = true; pushMove = true; wantRun = Run;
                PlayMove(Run ? "run" : "walk");
            }
            else PlayIdle();
            Locomote(dt);
            Apply();
            return;
        }

        switch (task)
        {
            case Task.Goto:
            case Task.Run:
            {
                bool run = task == Task.Run;
                float rad = run ? 0.6f : 0.3f;                              // NPC_STOP_GOTODIST (x2 for running)
                float dx = tx - x, dz = tz - z, d2 = dx * dx + dz * dz;
                if (GotoWaiting(dt, true, tx, tz)) { PlayIdle(); break; }
                if (d2 < rad * rad)
                {
                    PlayIdle();
                    EndTask(run ? "Runto point" : "Goto point");
                    break;
                }
                // the point is taken by somebody: wait a little, tell the template (BusyPos), give up after a few tries (AICharacter::CmdProcessGotoPoint)
                if (d2 < 2f * 2f && !PosFree(tx, y, tz))
                {
                    if (gotoBusyCnt > 1 + idleRng.Next(4)) { PlayIdle(); FailTask(); break; }
                    StatBusyPos++;
                    gotoBusyCnt++; gotoIsWait = true; waitTurn = false; gotoWait = 2f + (float)idleRng.NextDouble() * 2f;
                    it.FireEvent("Location_CharacterBusyPos", new List<V> { V.A(Ent.Root), V.Float(tx), V.Float(ty), V.Float(tz) });
                    PlayIdle();
                    break;
                }
                var (wx, wz) = NextWaypoint(tx, tz, ty, dt);
                SteerGo(wx - x, wz - z, MathF.Sqrt(d2), run, dt);
                if (isMove) PlayMove(wantRun ? "run" : "walk"); else PlayIdle();
                break;
            }
            case Task.Follow:
            case Task.Fight:
            case Task.Escape:
            {
                if (target == null || !target.Placed || (task == Task.Fight && !target.IsAlive))
                {
                    if (FightLog && task == Task.Fight) GD.Print($"[npc] {Id}: fight task fails: target={target?.Id} placed={target?.Placed} alive={target?.IsAlive}");
                    if (task == Task.Fight) FailFight();
                    PlayIdle(); break;
                }
                float dx = target.x - x, dz = target.z - z, dist = MathF.Sqrt(dx * dx + dz * dz);
                bool flee = task == Task.Escape;
                exch = flee ? null : target;                                  // SetExCharacter: the followed / fought character does not push us away
                if (flee)
                {
                    if (IsSliding)                                            // np_character.cpp UpdateEscapeCharacter: stuck at the patch border
                    {
                        StatEscapeSlide++;
                        task = Task.Stay; IsSliding = false;
                        it.FireEvent("Location_CharacterEscapeSlide", new List<V> { V.Str("Escape"), V.A(Ent.Root) });
                        PlayIdle();
                        break;
                    }
                    if (dist >= 20f)                                          // NPC_STOP_ESCAPE
                    {
                        PlayIdle();
                        EndTask("Escape");
                        break;
                    }
                    // away from the enemy (goForce * 3 in the engine), always running
                    float awx = dist > 0.001f ? -dx / dist : 0f, awz = dist > 0.001f ? -dz / dist : 1f;
                    var (efx, efz) = SteerForce(awx * 3f, awz * 3f);
                    isMove = true; pushMove = true; wantRun = true;
                    FollowForce(efx, efz);
                    PlayMove("run");
                    break;
                }
                bool arrived;
                if (task == Task.Follow)
                {
                    // NPC_START_DIST_NPC 2.5 / NPC_STOP_DIST_NPC 1.5: walk after the target once it is farther than 2.5 m, stop within 1.5 m
                    arrived = followState != 1 ? dist <= 2.5f : dist < 1.5f;
                    if (followState == 1 && arrived) followState = 0;
                    if (arrived && followState != 2) { followState = 2; it.FireEvent("Location_CharacterFollowStay", new List<V> { V.Str("Follow character"), V.A(Ent.Root) }); }
                    if (!arrived && followState != 1) { followState = 1; fightRun = false; it.FireEvent("Location_CharacterFollowGo", new List<V> { V.Str("Follow character"), V.A(Ent.Root) }); }
                    if (dist > 10f) fightRun = true; else if (dist < 4f) fightRun = false;      // NPC_RUN_DIST_NPC / NPC_WALK_DIST_NPC
                }
                else
                {
                    fightAllowed ??= AskIsFight();
                    if (fightAllowed == false) { FailFight(); PlayIdle(); break; }
                    if (!fightMode) FightMode = true;
                    NpcParams();
                    bool inReach = dist <= (fightState == 2 ? 2.4f : 2.0f);        // np_character.cpp: NPC_FIGHT_STOP_DIST 2.0 / NPC_FIGHT_GO_DIST 2.4
                    SetFightState(inReach ? 2 : 1);
                    if (FightLog && (fightLogT -= dt) <= 0f)
                    {
                        fightLogT = 1f;
                        GD.Print(FormattableString.Invariant($"[npc] {Id} -> {target.Id}: dist={dist:0.00} reach={inReach} state={playing} cooldown={atkCooldown:0.00} swinging={atkHitIn >= 0f} blocking={Blocking} atk={npcAttack:0.##} agg={npcAggressive:0.##} hp={HpNow:0}"));
                    }
                    NpcFightTick(dt);
                    NpcMaybeFire(target, dist);
                    if (inReach) { Turn(Angle(dx, dz)); NpcFight(target, dist, dt); break; }
                    // fight: run while farther than NPC_FIGHT_RUN_DIST (4 m), walk inside NPC_FIGHT_WALK_DIST (2.5 m), keep the gait in between
                    fightRun = dist > 4f || (fightRun && dist >= 2.5f);
                    arrived = false;
                }
                if (arrived)
                {
                    Turn(Angle(dx, dz)); PlayIdle();
                    break;
                }
                if (GotoWaiting(dt, false, 0f, 0f)) { PlayIdle(); break; }
                var (ax, az) = NextWaypoint(target.x, target.z, target.y, dt);
                SteerGo(ax - x, az - z, dist, fightRun, dt);
                if (isMove) PlayMove(wantRun ? "run" : "walk"); else PlayIdle();
                break;
            }
            case Task.Dead:
                Apply();
                return;
            default:
                PlayIdle();
                break;
        }
        Locomote(dt);
        Apply();
    }

    // ---- dialog start: both speakers stop and turn to each other, also when the script's own type does not do it (hero in a cutscene = "actor" type,
    // "poor"/"huber" NPCs). The game clock is frozen (SetTimeScale(0) in DialogMain), so the turn runs on the real frame time at the idle turn rate.
    bool dlgFacing, dlgLogged;
    float dlgStartAy;
    static readonly bool DialogLog = System.Environment.GetEnvironmentVariable("SEADOGS_DIALOGLOG") != null;

    void DialogFace(float realDt)
    {
        if (!dlgFacing || realDt <= 0f) return;
        if (isSwim || isJump) { dlgFacing = false; return; }
        CharacterView? other = null;
        foreach (var c in All.Values) if (!ReferenceEquals(c, this) && c.inDialog && c.Placed) { other = c; break; }
        if (other == null) return;
        if (scriptAction.StartsWith("sit", StringComparison.OrdinalIgnoreCase) || playing.StartsWith("sit", StringComparison.OrdinalIgnoreCase)) { dlgFacing = false; return; }     // seated people keep their chair
        float want = Angle(other.x - x, other.z - z);
        if (!dlgLogged) { dlgLogged = true; dlgStartAy = ay; if (DialogLog) GD.Print(FormattableString.Invariant($"[dialog] {Id} stops and turns to {other.Id}: yaw {ay:0.00} -> {want:0.00}")); }
        float rate = Math.Max(rig?.IdleTurnSpeed ?? 4f, 4f);
        ay = TurnToward(ay, want, rate * realDt);
        nay = ay; hasNay = false;
        Apply();
        if (MathF.Abs(MathF.IEEERemainder(ay - want, MathF.Tau)) < 1e-3f)
        {
            dlgFacing = false;
            if (DialogLog) GD.Print(FormattableString.Invariant($"[dialog] {Id} now faces {other.Id}: yaw {ay:0.00} (was {dlgStartAy:0.00})"));
        }
    }

    // ---- healing log (test aid): a potion's health is poured in by the script (LAi_AllCharactersUpdate: hp_bottle -> hp); one line when the health has risen by 3
    float healLast = -1f, healBase = -1f; bool healLogged;
    void TickHealLog()
    {
        float h = HpNow;
        if (healLast < 0f || h < healLast - 0.01f) { healBase = h; healLogged = false; }
        healLast = h;
        if (!healLogged && h - healBase >= 3f) { healLogged = true; GD.Print(FormattableString.Invariant($"[heal] {Id}: health rose {healBase:0.#} -> {h:0.#} (bottle left {Skill("hp_bottle", 0f):0.#})")); }
    }

    /// <summary>Counts the swing down and raises the hit event at the hit frame.</summary>
    void TickSwing(float dt)
    {
        atkCooldown -= dt;
        bool strikeNow = false;
        if (swingAnim && rig != null)
        {
            // the blow is driven by the REAL playback of the animation (speed factor, time scale and all): the strike lands when the rig reaches the
            // "Attack*" event frame, the control comes back when the animation has ended (+ the blend into the next action for NPCs)
            if (rig.ActionName != swingAction) { swingAnim = false; atkCooldown = Math.Max(atkCooldown, 0f); }
            else
            {
                bool done = rig.Finished;
                if (atkHitIn >= 0f)
                {
                    float s = rig.SecondsToStrike() ?? 0f;
                    if (done || s <= 0f) { strikeNow = true; atkHitIn = -1f; }
                    else atkHitIn = s;
                }
                if (done) { swingAnim = false; atkCooldown = SwingRecovery; }
                else atkCooldown = rig.SecondsToEnd() + SwingRecovery;
            }
        }
        if (queuedSwing is { } q)
        {
            queuedUntil -= dt;
            if (queuedUntil <= 0f || !q.enemy.IsAlive) queuedSwing = null;
            else if (atkCooldown <= 0f && atkHitIn < 0f) { queuedSwing = null; StartSwing(q.enemy, q.kind); }
        }
        if (!strikeNow)
        {
            if (atkHitIn < 0f) return;
            if (swingAnim) return;                       // waiting for the rig
            atkHitIn -= dt;
            if (atkHitIn >= 0f) return;
        }
        if (atkTarget != null)
        {
            float d2 = (atkTarget.x - x) * (atkTarget.x - x) + (atkTarget.z - z) * (atkTarget.z - z);
            // character.cpp CheckAttackHit: reach 2.4 m, 60 degrees either side of the heading the blow was started with (the turn is locked meanwhile)
            float ca = d2 > 1e-4f ? (MathF.Sin(swingAy) * (atkTarget.x - x) + MathF.Cos(swingAy) * (atkTarget.z - z)) / MathF.Sqrt(d2) : 1f;
            if (atkTarget.IsAlive && IsAlive && d2 < 2.4f * 2.4f && ca >= 0.5f)
            {
                bool blocked = atkTarget.Defends(this);
                bool parried = blocked && (atkTarget.IsPlayer ? atkTarget.blockAge < BlockRaise + 0.3f : atkTarget.parryIntent);                 // a block raised just in time throws the attacker off balance
                if (parried) StatParried++; else if (blocked) StatBlocked++; else StatHits++;
                if (IsPlayer) { if (blocked) StatHeroBlockedByNpc++; else StatHeroLanded++; }
                atkTarget.Snd(!blocked ? "sword_inbody" : parried ? "sword_slide" : "blade");      // steel in flesh / blade on blade
                float hpBefore = atkTarget.HpNow;
                it.FireEvent(blocked ? "Location_CharacterBlock" : "Location_CharacterAttack", new List<V> { V.A(Ent.Root), V.A(atkTarget.Ent.Root), V.Float(atkDamage), V.Float(atkDamage) });
                if (IsPlayer) atkTarget.ShowHit(hpBefore - atkTarget.HpNow, blocked);
                if (!IsPlayer && atkTarget.IsPlayer && StatHeroHits++ < 3) GD.Print(FormattableString.Invariant($"[fight] the hero was {(blocked ? "attacked (blocked)" : "hit")} by {Id}: hp {hpBefore:0.#} -> {atkTarget.HpNow:0.#}, kind={lastKind}"));
                if (MeleeLog) GD.Print($"[melee] {Ent.Root.Get("id")?.Value} -> {atkTarget.Ent.Root.Get("id")?.Value}: strike registered {Time.GetTicksMsec() - swingStartMs} ms after the swing began (rig {swingAction} at frame {rig?.AnimFrame:0.00} of the strike frame, speed x{rig?.SpeedMul:0.00}),{(blocked ? "blocked" : "damage " + (hpBefore - atkTarget.HpNow))}, hp {atkTarget.HpNow}/{atkTarget.HpMax}; control back in {Math.Max(0f, atkCooldown):0.00}s");
                atkTarget.PlayReaction(blocked);
                if (parried) Stagger();
            }
        }
        atkTarget = null;
    }

    /// <summary>Begins a sword swing at a target in reach (no-op while the previous swing is still running).</summary>
    public bool StartSwing(CharacterView enemy, Strike kind = Strike.Normal)
    {
        if (!IsAlive) return false;
        if (atkCooldown > 0f || atkHitIn >= 0f)
        {
            // clicks made while a blow is still playing are NOT counted (no input queue): the next blow can only be started after the animation is over,
            // and a block asked for meanwhile (held key) simply goes up when the animation ends
            return false;
        }
        ay = Angle(enemy.x - x, enemy.z - z);
        atkTarget = enemy;
        string a;
        switch (kind)
        {
            case Strike.Strong: atkDamage = 1.7f; a = "attack_" + (idleRng.Next(4) + 5); break;                                            // heavier blow
            case Strike.Fast: atkDamage = 0.7f; a = idleRng.Next(2) == 0 ? "attack_right_1" : "attack_left_1"; break;
            default: atkDamage = 1f; a = "attack_" + (idleRng.Next(4) + 1); break;
        }
        // the strike moment and the length of the blow come from the animation (man.ani: "Attack_slash" event, frames / (fps * speed)), as in the engine
        var tm = rig?.SwingTiming(a) ?? rig?.SwingTiming("attack_1") ?? (0.2f, 0.45f);
        float spd = Math.Max(0.2f, rig?.SpeedMul ?? 1f);      // the script's speed factor (agility) speeds the animation up or down
        atkHitIn = tm.hit / spd; atkCooldown = tm.duration / spd; swingStartMs = Time.GetTicksMsec();
        if (MeleeLog) GD.Print($"[melee] {Ent.Root.Get("id")?.Value} swing {a}: strike in {atkHitIn:0.00}s, control back after {atkCooldown:0.00}s");
        swingAnim = false;
        if (rig != null && (rig.Play(a, false, true) || rig.Play("attack_" + (idleRng.Next(4) + 1), false, true))) { playing = a; swingAnim = true; swingAction = rig.ActionName; }
        if (kind == Strike.Strong || idleRng.NextDouble() < 0.4) ChrSnd("Event_ChrSnd_Attack");     // a battle cry, not on every swing
        lastKind = kind; swingAy = ay;
        if (!IsPlayer) StatNpcSwings++;
        return true;
    }

    /// <summary>Thrown off balance by a parry (or a step back, the original control ChrRecoil): the "Recoil" animation and a pause before the next swing.</summary>
    public void Stagger()
    {
        swingAnim = false;
        atkCooldown = Math.Max(atkCooldown, 1.2f); atkHitIn = -1f; atkTarget = null;
        if (rig != null && rig.Play("Recoil", false, true)) playing = "Recoil";
    }

    /// <summary>Step back from the enemy (the original control ChrRecoil): moves away a little while the animation plays.</summary>
    public void Recoil()
    {
        if (!fightMode || !IsAlive || Busy) return;
        Stagger(); atkCooldown = 0.4f;
        float bx = -MathF.Sin(ay) * 1.4f, bz = -MathF.Cos(ay) * 1.4f;
        Step(bx, bz);
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
    public string PositionText() => CharacterMath.FormatPosition(x, y, z, ay);

    public void RestorePosition(string text)
    {
        if (!CharacterMath.TryParsePosition(text, out float px, out float py, out float pz, out float pay)) return;
        x = px; y = py; z = pz; ay = pay;
        node = -1; vy = 0f;
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
        if (fmt == "s") { if (d.Length > 0) ChatterOrSnd(Conv.ToStr(d[0])); return V.Int(1); }   // LAi_CharacterPlaySound: an alias at the character
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
                shadow?.QueueFree();
                shadow = CharShadow.Create(this, newRig);
                if (shadow != null) { AddChild(shadow); GetNodeOrNull("Shadow")?.QueueFree(); }       // the silhouette shadow replaces the blob
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
                // DialogMain turns the speakers through the script types just before it announces the dialog (InDialog): that turn is eased in over
                // the following frames (DialogFace) instead of snapping
                if (it.Globals.TryGetValue("dialogRun", out var dr) && dr.Val.I != 0 && Placed && !inDialog) { if (Chr(1) != null) dlgFacing = true; }
                else if (Chr(1) is { } tc) ay = Angle(tc.x - x, tc.z - z);
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
            case 30403:                                                                    // GETTASK "le": the name np_character.cpp GetTaskName gives
                if (d.Length > 1) it.WriteRef(d[1], V.Str(task switch { Task.None => "None", Task.Stay => "Stay", Task.Goto => "Goto point", Task.Run => "Runto point", Task.Follow => "Follow character", Task.Fight => "Fight", Task.Escape => "Escape", Task.Dead => "Dead", _ => "Unknow task" }));
                return V.Int(1);
            case 30373:                                                                    // EX_MSG: queries and switches
                switch (S(1))
                {
                    case "InDialog":
                        inDialog = Conv.ToInt(d[2]) != 0;
                        dlgFacing = inDialog;                                           // see DialogFace
                        if (inDialog) { dlgLogged = false; Blocking = false; blockUntil = 0f; }
                        return V.Int(1);
                    case "ChangeFightMode": fightMode = d.Length > 2 && Conv.ToInt(d[2]) != 0; return V.Int(1);
                    case "SetFightMode": FightMode = d.Length > 2 && Conv.ToInt(d[2]) != 0; return V.Int(1);           // with the draw animation (location login, boarding)
                    case "IsSetBalde": return V.Int(string.IsNullOrEmpty(Ent.Root.Find("equip.blade", false)?.Value) ? 0 : 1);
                    case "SetSex": case "SetFightWOWeapon": case "LockFightMode": return V.Int(1);
                    case "IsFightMode": return V.Int(fightMode ? 1 : 0);
                    case "IsDead": return V.Int(task == Task.Dead ? 1 : 0);
                    case "FindDialogCharacter": return V.Int(FindDialog?.Invoke() ?? -1);
                    case "IsActive": return V.Int(rig != null && !rig.ActionName.StartsWith("idle", StringComparison.OrdinalIgnoreCase) ? 1 : 0);     // character.cpp isActiveState: anything but standing idle
                    case "DistToLocator":                                                  // "lssse": group, locator, &distance (3D); -1 and 0 when there is no such locator
                    {
                        var l = findLocator(S(2), S(3));
                        if (l == null) { if (d.Length > 4) it.WriteRef(d[4], V.Float(-1f)); return V.Int(0); }
                        float dx = l.M[12] - x, dy = l.M[13] - y, dz = l.M[14] - z;
                        if (d.Length > 4) it.WriteRef(d[4], V.Float(MathF.Sqrt(dx * dx + dy * dy + dz * dz)));
                        return V.Int(1);
                    }
                    case "FindForvardLocator":                                             // "lsse": group, &name: the locator most in front of the character (supervisor.cpp FindForvardLocator)
                    {
                        string found = "";
                        float fx = MathF.Sin(ay), fz = MathF.Cos(ay), best = 0f;
                        int pick = -1, i = 0;
                        foreach (var l in GroupLocators?.Invoke(S(2)) ?? Array.Empty<GmFile.Label>())
                        {
                            float lx = l.M[12] - x, lz = l.M[14] - z, cs = lx * lx + lz * lz;
                            if (cs > 0f)
                            {
                                float k = (lx * fx + lz * fz) / MathF.Sqrt(cs);
                                if (pick > 0) { if (k > best) { pick = i; best = k; found = l.Name; } }     // the original keeps "l > 0" here: index 0 is replaced by the next one
                                else { pick = i; best = k; found = l.Name; }
                            }
                            i++;
                        }
                        if (d.Length > 3) it.WriteRef(d[3], V.Str(found));
                        return V.Int(pick >= 0 ? 1 : 0);
                    }
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
        // a dead body stays dead: the AI type's death handler (LAi_CheckKillCharacter -> chr_ai.type ...) sets "Stay" right after "Dead",
        // which used to stand the corpse up before its death animation (and the body-fall sound) finished
        if (task == Task.Dead && name != "Dead" && Ent.Root.Find("chr_ai.hp", false) is { } hp0 && Conv.ParseFloatPrefix(hp0.Value) < 1f) return;
        switch (name)
        {
            case "None": task = Task.None; break;
            case "Stay": task = Task.Stay; break;
            case "Goto point": gotoIsWait = false; gotoBusyCnt = 0; gotoCool = 0f; path = null; task = Task.Goto; tx = Fl(2); ty = Fl(3); tz = Fl(4); break;
            case "Runto point": gotoIsWait = false; gotoBusyCnt = 0; gotoCool = 0f; path = null; task = Task.Run; tx = Fl(2); ty = Fl(3); tz = Fl(4); break;
            case "Follow character": task = Task.Follow; target = chr(2); followState = 0; break;
            case "Fight":
            {
                // the fight template re-sends the task on every template update: only a new enemy starts the fight over (events, permission)
                var enemy = chr(2);
                if (task != Task.Fight || !ReferenceEquals(enemy, target)) { fightState = 0; fightAllowed = null; }
                task = Task.Fight; target = enemy;
                break;
            }
            case "Escape": task = Task.Escape; target = chr(2); break;
            case "Dead":
                if (task != Task.Dead && Placed && Visible) ChrSnd("Event_ChrSnd_Dead");
                task = Task.Dead;
                if (rig != null && (rig.Play("death_0", false) || rig.Play("death_1", false))) playing = "dead";
                break;
            default:
                if (warned.Add("task " + name)) GD.Print("[character] unknown task: " + name);
                break;
        }
    }

    // ------------------------------------------------------------------ sounds
    // The sit/dialog AI types roll "babble" per frame (rand(300), rand(100) near others): with several people in a tavern the voices pile up on the music.
    // One babble per speaker at a time, a pause between phrases, and at most two babbles heard at once in the whole location.
    static readonly List<int> babbles = new();
    int myBabble; double nextBabble;
    void ChatterOrSnd(string alias)
    {
        if (alias != "male-citizen" && alias != "female-citizen") { Snd(alias); return; }
        var snd = SoundView.Instance; if (snd == null || !Placed || !Visible) return;
        double now = Godot.Time.GetTicksMsec() / 1000.0;
        babbles.RemoveAll(id => !snd.VoiceAlive(id));
        if (now < nextBabble || (myBabble != 0 && snd.VoiceAlive(myBabble)) || babbles.Count >= 2) return;
        nextBabble = now + 6 + idleRng.NextDouble() * 6;
        myBabble = snd.Play3D(alias, x, y + 1.2f, z);
        babbles.Add(myBabble);
    }

    /// <summary>A sound alias at the character's chest (3D, attenuated by the alias distances).</summary>
    void Snd(string alias, float height = 1.2f)
    {
        if (!Placed || !Visible || alias.Length == 0) return;
        SoundView.Instance?.Play3D(alias, x, y + height, z);
    }

    /// <summary>The character sound events of the original engine (handled in Loc_ai/LAi_sounds.c): Event_ChrSnd_Attack/Hit/Dead/Body, data = the character.</summary>
    void ChrSnd(string ev)
    {
        if (!Placed || !Visible) return;
        if (System.Environment.GetEnvironmentVariable("SEADOGS_SOUNDLOG") != null) GD.Print($"[sound] {ev} {Id}");
        it.FireEvent(ev, new List<V> { V.A(Ent.Root) });
    }

    /// <summary>Step sound alias for a patch material: "snd_grass" / "add_snd_stone" / truncated "GrexitT_snd_gras" -> step_grass; others -> step_ground.</summary>
    public static string StepAlias(string material) => CharacterMath.StepAlias(material);

    /// <summary>Events of the .ani tracks: steps, sword swish, drawing/sheathing the blade, the pistol, the body hitting the ground.</summary>
    void OnAnimEvent(string ev)
    {
        if (System.Environment.GetEnvironmentVariable("SEADOGS_ANIEVLOG") != null) GD.Print($"[anim] {Id}: event '{ev}' in {rig?.ActionName}");
        switch (ev.ToLowerInvariant())
        {
            case "lstep": case "rstep":
                Snd(StepAlias(getPatch()?.MaterialAt(x, z, y) ?? ""), 0.1f);
                break;
            case "sound_slash": case "sound_stub": Snd("sword_wind"); break;
            case "blade to hand": Snd("sword_out"); break;
            case "blade to belt": Snd("sword_sh"); break;
            case "sound_pistol": Snd("pistol_out"); break;
            case "death sound": ChrSnd("Event_ChrSnd_Body"); break;
        }
    }

    /// <summary>Used by the camera/main loop.</summary>
    public void SetTimeScale(float s) { if (rig != null) rig.TimeScale = s; }
}
