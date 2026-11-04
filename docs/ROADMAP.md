# Roadmap / open work (ordered by importance)

Legend: effort S (hours) / M (a day) / L (days+). "Hint" says where to start.

## A. Needed to play beyond the first island
1. **Sea mode (L)** — `Ship`, `Island`, `Sail`, `Rope`, `Flag`, `Vant`, `SEA_CAMERAS`, `sea_ai`, `AIFort`, `AIBalls`, `battle_interface`, `ispyglass`.
   Hint: scripts `PROGRAM/SEA_AI`, `Ships/`, `BATTLE_INTERFACE`; entry `ReloadToSea` in `reload.c`; `sea` entity already renders a plane (`OutdoorEnv.cs`).
   The hero currently has no ship at the start; ships come from the shipyard (`LaunchShipyard`, needs gold) or quests.
2. **World map (L)** — class `worldmap` (`PROGRAM/WorldMap`); `wdm*` script functions.
3. **Combat (L)** — melee/pistols: `Location_CharacterAttack(attack, enemy, attackDmg, hitDmg)` etc. (`Loc_ai/LAi_events.c`), fight animations in `RESOURCE/Animation/man.ani`
   (`attack_*`, `block`, `hit_*`, `fight stand_*`), death/ragdoll, weapons in hands, plus
4. **CharactersGroups (M)** — message protocol in `Loc_ai/LAi_groups.c` (`RegistryGroup, SetRelation, SetAlarm*, MoveChr, GetTrg, VldTrg, IsEnemy, Attack, AddTarget`).
   Currently every unhandled call returns 0 = "no enemies" (a peaceful world).

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
18. Unit tests for the interpreter (semantics in `docs/vm_semantics.md`) and a CI-style smoke script (intro run + `--fuzz`).
19. Remove debug env hooks or document them (done in CLAUDE.md), clean the `out/` converters output, pin tool versions.
20. Performance: trimesh colliders for large locations, `Blocked()` is O(n²) over characters (fine for <50).
