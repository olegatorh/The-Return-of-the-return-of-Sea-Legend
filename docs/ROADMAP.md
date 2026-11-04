# Roadmap / open work (ordered by importance)

Legend: effort S (hours) / M (a day) / L (days+). "Hint" says where to start.

## A. Needed to play beyond the first island
1. **Sea mode (L) — basics DONE** (own ship, sails, island, camera, mooring back; see ARCHITECTURE 6b). Still to do: HUD, other ships + AI, cannons/balls, forts, island collision, wake, sea sounds, storms. Original item: — `Ship`, `Island`, `Sail`, `Rope`, `Flag`, `Vant`, `SEA_CAMERAS`, `sea_ai`, `AIFort`, `AIBalls`, `battle_interface`, `ispyglass`.
   Hint: scripts `PROGRAM/SEA_AI`, `Ships/`, `BATTLE_INTERFACE`; entry `ReloadToSea` in `reload.c`; `sea` entity already renders a plane (`OutdoorEnv.cs`).
   The hero currently has no ship at the start; ships come from the shipyard (`LaunchShipyard`, needs gold) or quests.
2. **World map (L) — basics DONE** (`game/WorldMap.cs`: map model, ship with A/D/W/S, island zones, date, ExitFromWorldMap; sea -> map -> sea round trip). To do: map encounters (ships/storms: `MSG_WORLDMAP_CREATE*`), weather/sky, enemy ships, speed/time balance, fog of unknown locations. Original item: — class `worldmap` (`PROGRAM/WorldMap`); `wdm*` script functions.
3. **Combat (M, basic version DONE)** — melee works end to end: `CharactersGroups` (`game/Groups.cs`: relations, alarm, GetTrg/IsEnemy/Attack/AddTarget),
   NPCs in task `Fight` path-find over the walk patch (`PtcFile.FindPath`), swing in reach, raise `Location_CharacterAttack(attack, enemy, 1, 1)`; the script computes
   damage, death (`Dead` task) and game over (back to the main menu). Player: key **F** draws the sword, left mouse button attacks the nearest person in reach (2.2 m).
   Still missing: blocks/parries (`Location_CharacterBlock`), attack variety and timing from `.ani` events, pistols (`Location_CharacterFire`), weapons in hands,
   hit/death effects, ragdoll, fight camera, `LAi_*` fight-mode checks beyond IsFightMode. Test: `--call=LAi_group_MoveCharacter(#Id,foes)@N`,
   `--call=LAi_group_FightGroups(foes,player,false)@N`, `--call=LAi_group_SetLookRadius(foes,100)@N`, dump `Characters[0].chr_ai.hp`.
4. **CharactersGroups (DONE, simplified)** — alarm decay constants and look/hear radii semantics are guesses; LOS is ignored.

## B. Gameplay screens
5. **Trade/store flow (M)** — needs a ship hold; interfaces `store.ini`, `itemstrade.ini`, `shiphold.ini`, `passengers.ini`; missing node types SCROLLER/SCROLLBAR.
6. **Items & chests on the ground (S–M)** — `MODELR` handler exists (`Main.cs`), untested; scripts: `ITEMS/itemLogic.c`, interface `itemsbox.ini`.
7. **Key bindings screen (S)** — `KEYCHOOSER` node; controls are mapped in `game/Main.cs` (`_UnhandledKeyInput`).
8. **Save thumbnails (S)** — `scrshoter` class, `MSG_SCRSHOT_*` (45042..45044); `SaveFiles.cs`.
9. Other nodes: EDITBOX, XBOX, GLOWS/GLOWCURSOR (cosmetic), STATUSLINE fill value, CHANGER selection drawing.

## C. Fidelity
10. Water: transparent water with depth (shows the sea-bottom `sb` model), shore foam (`foam` models), waves — `OutdoorEnv.cs`.
11. Particles (`particles` class: candles, smoke), `Animals` (birds, crabs), lightning/rain (`rain`, `Lightning`), `SunGlow`.
12. Mouse look/steering like the original (RMB forward, mouse turns the hero); gamepad.
13. Talking-head portraits in the dialog window (`Heads/h_*.gm`).
14. Night/day lighting switch when the game clock crosses hours (lighting sets `day1`, `night`; scripts call `Whr_UpdateWeather`).

## D. Language (Ukrainian)
15. Machine draft covers ~79% of dialog strings (NLLB-200 600M); quality is "gist". Options: hand-polish the first-hour dialogs (QC port/town, tavern, governor),
    or run a larger model (needs explicit download approval). Workflow: `tools/extract_strings.py` → work file in `lang/ukrainian/strings_work/` →
    `tools/build_strings.py` → `lang/ukrainian/strings/<script>.tsv`.
16. Untranslated text files: `HELPSECTIONS/`, `interface_strings.txt` (partly), per-hero descriptions are machine drafts.
17. Pictures with baked-in Russian text (loading screens) stay Russian.

## E. Engineering
18. Tests — DONE basics: `StormScript.Tests` (23 xunit tests) + `run_tests.ps1 [-Smoke] [-Intro]` (see CLAUDE.md). Still to do: tests for `GroupsState`/`Characters`
    (needs the logic split from Godot types), a `--fuzz` smoke mode in the script, golden-value checks of script flows (e.g. dump `Characters[0].chr_ai.hp` after a fight).
19. Remove debug env hooks or document them (done in CLAUDE.md), clean the `out/` converters output, pin tool versions.
20. Performance: trimesh colliders for large locations, `Blocked()` is O(n²) over characters (fine for <50).
