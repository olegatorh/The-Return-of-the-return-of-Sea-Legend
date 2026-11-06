# Playtest of the first hours (QA by playing, driven by the test hooks)

Replay arguments of every stage: `playtest_runs.md`. Date 2026-11-06. Hero: new game, pirate start (Quebradas Costillas / Nevis). "Fixed in" = commit of this pass.

| Stage | What was done | Result | Defects found | Fixed in | Still open |
|---|---|---|---|---|---|
| 0 New game, intro, tutorial deck | main menu -> New game -> Smuggler Agent / ALexusB / Abracham Gray dialogs -> `Tutorial_Deck` -> QC_port | OK, hero on the pier at `reload2`, 0 `[errors]`/`[unresolved]` | `ships\flagall/vant/sail_rope.tga` "texture not found" on stderr every run (first lookup tried the wrong folder) | this pass (RiggingFx lookup order) | videos (`EndVideoPlay` has no handler), known |
| 0b Deck, fighting branch | answers start2 -> start3 -> start4 -> `Tut_KillOnShip` | dialog works, Abracham attacks, idle hero dies in 2 blows (hp 51), game returns to the main menu | none (script balance: "you have no chance") | - | no death video |
| 1 Town tour | reload into 16 QC locations; fuzz round (talk to everyone) in 7 interiors, 29 people | all load, no errors, no empty answers | walking onto an auto-reload locator after a load does nothing the first time: original `lockedReloadLocator` quirk (script), not an engine bug | documented | - |
| 1 Language | `SEADOGS_LANGLOG=1` on the fuzz round: 193 shown lines/answers | 4 flagged RU (agents' "patent_0" lines), 0 EN; speaker name "Голландскій агент", "Англійська агент", "Инес" (names with a leading Russian И) | untranslated piece of Agent_dialog, wrong nation adjectives, 20 names with `И` | commit 94a7d0c | machine translation quality ("Спроси", "Красавица", "Пойдіть" Russianisms in MT output) |
| 2 Save / load in the middle | `--save`/`--load` in town, store, tavern, usurer, residence, shipyard | location, money kept, 0 errors | none | - | - |
| 3 Store | Peter Blowhorn dialog -> goods screen; trade scenario of `-Sea` | OK | none | - | dialog camera shows only the hero (NPC out of frame) |
| 3 Shipyard | Jakob Nickerson -> buy tartane with Enter only | ship bought (type 0, 50000 -> 48918) | first view is an empty info panel (UpdateFourData sets EMPTYVIEW: same order as the script, so likely original); after buying the info shows stale data | - | check against the original if possible |
| 3 Tavern | Ines Diaz: about, rent a room (sleep to the night), crew | sleeping works (11:19 -> 22:29, money -5); crew says "no ship in port" until the quest ship exists | MT text quality | - | - |
| 3 Usurer | loan dialog Small -> Interest -> Period | works (text "11 в місяць" = the original lacks "percent") | none | - | - |
| Q First quest chain | Ines "who hires" -> William Blaker -> night port -> deck dialog -> 3 mutineers (hero immortal, group killed by hook) -> sailor -> `Sailor_Blaker` | ship Lugger1 + crew 35, `from_sea = QC_port`; test `run_tests -Quest` | none in the chain; idle hero dies to 3 mutineers in 2 blows each (balance of the script) | scenario added | - |
| 4 Leave the port, sail | `reload1` + `ChrAction`, sails up, steer | OK, 100 fps | **canvas error "triangulation failed" ~1000x per sea run** (minimap disc had a duplicate closing vertex; degenerate ship triangles) | this pass (SeaNavigator, `run_tests` now fails on `Invalid polygon data`) | - |
| 5 World map first entry | `BI_MapEnter` -> `FirstEnterMap` quest: Dagon Teacher appears in the tavern, Ines offers "monk in the corner", Ludwig Prinn dialog chain | works | none | - | - |
| 5 Sea fight, boarding, return, selling goods | `run_tests -Sea` scenarios (fight, neutral, board, trade, sailto ...) | all ok | none new | - | - |
| 6 Quest book | `LaunchQuestBook()` | **whole base quest log was Russian** (422 of 457 entries: "Помощь контрабандиста...", "Справка: ...") | missing overlay | this pass: 371 entries machine-translated + 59 titles by hand | 51 long entries stay Russian (MT output rejected: Eng_Line, Ascold, animists...) |
| 6 Items / ships / perks | texts overlay vs base | 10 item descriptions, 2 ship texts, 4 flag perks, 1 control name were Russian | missing keys | this pass | `StartGameDescribe` option hints are empty in the base too |
| 6 Misc | stderr of all runs | only exit-time leak warnings left; godot.log 0 ERROR | - | - | `[nohandler]` counts (MoveChr, 30373 LockFightMode ...) are messages to not yet created entities, as before |

Counts of untranslated Russian per screen (dialog literals, whole game): 668 pieces, ~460 are gender suffixes / fragments the MT rule skips on purpose,
205 are real sentences (42 translated now; biggest left: Eng_guber quest 33, Robert Christopher Silehard 27, Valentin Massoni 24, Albermal 8).
FPS: 95-100 in every stage (window 1280x720), no drops below 60 seen.
