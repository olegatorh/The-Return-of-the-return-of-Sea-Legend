# Boarding: how many people fight (original addon vs the port)

Checked 2026-11-15. Source of the rules: `addon/merged/PROGRAM/Loc_ai/LAi_boarding.c`, `Locations/init/boarding.c`, `Characters/GeneratorUtilite.c`
(`GetBoardingHP`, `GetBoarding_*_hp_Bonus`), `Loc_ai/LAi_utils.c` (`LAi_CreateFantomCharacterEx`), `Loc_ai/LAi_login.c` (32 logged characters).
The port interprets these scripts unchanged; `IBoardingStatus` (the two crew bars) is never called by any script of the addon, so it does not show anything.

## Rules of the original (all in the scripts)

1. **Crew -> fighters.** `mcrew = GetWeaponCrew(player crew)`: men above the weapons in the hold count half
   (`w + (crew - w)/2`). `ecrew = enemy crew + 1`. Before that the attacker side loses 25 % x (2.1 - Defence)/2 (the defender's advantage).
2. **Cap.** `maxcrew` = locatorNum of the first deck + the locatorNum of every following deck while `maxcrew < BRDLT_MAXCREW (44)`.
   If the bigger side exceeds it: `rel = big / maxcrew`, big = maxcrew, small = small / rel + 0.5 (min 1 each).
   Example (type 6 ship, decks 12 + 7 + 4): maxcrew = 23.
3. **One fighter = `base / fighters` sailors** (`boarding_*_crew_per_chr`). Fighters spawn per deck, at most `locatorNum` per side
   (6 / 9 / 10 / 12 on the upper deck, 7 on the gun decks, 4 hold, 2-4 cabin). The hero and officers take places first: the player side spawns
   `locatorNum - LAi_numloginedcharacters` fighters.
4. **Reinforcements** are not waves: the unspawned rest waits in `boarding_*_crew` and is spawned on the next deck. When the enemy group is dead
   (`LAi_event_GroupKill`) the living player fighters (not officers) go back into `boarding_player_crew`, Space loads the next deck.
5. **Cabin** (4 / 2 / 3 locators) takes only that many; the rest of the player pool is not spawned but still counts as survivors
   (`crew = boarding_player_crew * per_chr` at the end). Dead men of the player = `base - crew` (reduced by Defence skill, medicine from the hold).
6. **HP / rank**: `bNewFantomGenerator` = rank bonus for the bigger side (`0.6*rate*(m-e)/e` for the player, `rate*(e-m)/m` for the enemy); otherwise HP bonus.
7. `GenQuest.CrewSkelMode` on the hero makes the player's boarding fighters skeletons (`GetRandSkelModel`).

## What the port does (measured)

Tool: `tools/boarding_probe.ps1 -Mine N -Enemy M` (the `board` scenario with chosen crews, dumps the script's counters on every deck),
`tools/boarding_table.py` turns the log into a table. Enemy ship boards us (type 6 decks: upper 12, gun deck 7, cabin 4).

| crews (ours / enemy) | base (ours / enemy) | fighters (ours / enemy) | deck 1 (12 loc) | deck 2 (7 loc) | cabin (4 loc) |
|---|---|---|---|---|---|
| 60 / 120 (weapons for 40) | 50 / 121 | 10 / 23 | 10 + hero vs 12 (enemy rest 11) | 6 survivors back + hero vs 7 (rest 4) | 3 + hero vs 4 |
| 20 / 200 | 20 / 201 | 2 / 23 | 2 + hero vs 12 | 1 survivor vs 7 | 1 vs 4 |

Expected from the formulas: 50 -> 10 (50 x 23/121 + 0.5), 121 -> 23, 20 -> 2, per-deck limits as above, `LAi_numloginedcharacters` = hero + both sides (23 on deck 1).
**All numbers match the script formulas exactly**; the survivors go back into the pool and are re-spawned on the next deck; the cabin takes 4 per side.

## Differences / findings

| # | What | Where | Verdict |
|---|---|---|---|
| 1 | Counts of spawned fighters, caps, per-deck limits, pool refill, 32-slot limit | scripts + `LAi_numloginedcharacters` | no deviation found in the port |
| 2 | Locators: every boarding deck model has `loc0..` and `aloc0..` >= `locatorNum` (12/12, 7/7, 4/4 (5), 2/2, 3/3; forts 10/8/11) | `RESOURCE/MODELS/locations/decks/*` | OK (no men dropped for missing locators) |
| 3 | `IBoardingStatus` bars (`DrawCharacterHP`) | `game/LogHud.cs` | nothing in the addon calls it: no bars in the original either |
| 4 | The player is badly outnumbered when the enemy crew is much bigger (20 vs 200: hero + 2 fighters against 23) | `LAi_StartBoarding` formulas | faithful to the original; it is the design (one fighter = ~9 sailors) |
| 5 | Perceived "few men": 50 sailors = 10 fighters (1 : 5), a 300-crew ship is capped at 23 fighters in total | formulas 2-3 | faithful; only the fighters count, the rest are shown as sailors lost in the result |

## What was changed

Nothing in the engine: no deviation was found. Added only the probe tools (`tools/boarding_probe.ps1`, `tools/boarding_table.py`,
`tools/godot_args.ps1 -Max`).

## Not covered

* Officers boarding with the hero (`AbordageMode`) and the fort landing (`IsFort`, troopers) were not measured (the `board` scenario has no officers).
* Player-initiated boarding (the `boardpatent` path) uses the same `LAi_StartBoarding`; its counts were not measured separately.
* With a much stronger player crew the enemy ship does not board us in the scenario, so that branch (`mcrew > ecrew` cap) was only checked by reading.
