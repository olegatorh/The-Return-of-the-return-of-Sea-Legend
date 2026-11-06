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
| 6 Quest book | `LaunchQuestBook()` | **whole base quest log was Russian** (422 of 457 entries: "Помощь контрабандиста...", "Справка: ...") | missing overlay | this pass: 371 entries machine-translated + 59 titles by hand | translated by hand in pass 2 (see counts below) |
| 6 Items / ships / perks | texts overlay vs base | 10 item descriptions, 2 ship texts, 4 flag perks, 1 control name were Russian | missing keys | this pass | `StartGameDescribe` option hints are empty in the base too |
| 6 Misc | stderr of all runs | only exit-time leak warnings left; godot.log 0 ERROR | - | - | `[nohandler]` counts (MoveChr, 30373 LockFightMode ...) are messages to not yet created entities, as before |

Counts of untranslated Russian per screen (dialog literals, whole game) - translation polish pass 2 (`tools/scan_lang.py`, `tools/scan_texts.py`):

| Measure | Before | After |
|---|---|---|
| Russian sentences (>=3 words) with no translation (script literals) | 162 (Eng_guber 33, Silehard 27, Massoni 24 = voice file names, not text, Albermal 8, others) | 0 (the 24 `voice\...` names are sound paths, ignored) |
| Sentences with machine-translation damage found by the scanner (Russian words left, >=34% of words equal to the source, `Спроси`/`Красавица`/`Пойдіть`...) | 583 | 0 flagged; all rewritten by hand in `lang/ukrainian/strings/_polish.tsv` (736 entries, top priority in `LanguageService.LiteralText`) |
| Shown translations with Russian-only letters (ы э ъ ё) | 0 | 0 (test-guarded) |
| Remaining "Russianism" words (curated list) in the shown strings | ~60 | 21 name-pool / debug items, none in dialogs; `tools/polish_words.py` fixed 158 MT values (піастрів, Вітаю, ...) |
| Quest book entries still Russian (QuestBook) | 56 | 2 (developer comment keys `dummy3_1`, `dummy666_1`, never shown) |
| Text-file keys showing Russian (all `texts/*`, `scan_texts.py`) | 258 | ~110, all cognates that are valid Ukrainian too (Золото, Фрегат, Магазин...) |
| Hero types / nations in `HeroDescribe.txt` | Russian words | script codes (`Merchant`, `ENGLAND`...: the scripts switch on them and map the Russian words themselves) |

Regression check: `StormScript.Tests/LanguageQualityTests.cs` (runs in `run_tests.ps1`) scans every Cyrillic literal of the merged scripts through `LanguageService.LiteralText`
and fails on any Russian-only letter, on more than 3 Russianisms, or on more than 5 untranslated sentences; a second test checks `lang/ukrainian/texts`.
What remains: ~13k machine-translated fragments (`_mt.tsv`) are readable but not individually proof-read (case/gender slips are possible; the scanner cannot see them);
name pools (`Characters_names`, `ships_name`) only got a spelling pass (`tools/polish_names.py`); `HeroDescribe.info` (developer doc) is Russian.
FPS: 95-100 in every stage (window 1280x720), no drops below 60 seen.

## Part 2: after the first quests (ports of other islands, world map travel, sea encounters)

Replay arguments: `playtest_runs.md`, section "Part 2". Hero: the town-port quicksave with the tutorial lugger (`initTutMainCharacterShip`).

| Stage | What was done | Result | Defects found | Fixed in | Still open |
|---|---|---|---|---|---|
| 7 Map -> Antigua | QC -> map -> `--mappos` Antigua -> exit to sea -> `SeaAI_SailToLocator(reload_1)` | island, fort (69 cannons), squadron load; as a pirate (nation 3) the squadron and fort attack at once (script: `Ship_CheckFlagEnemy` + hunter score, random) | none in the engine; hooks need `nation=0`, `SetNationRelation2MainCharacter(0,0)` (0 = friend, 1 = neutral, 2 = enemy) and may still hit the random flag check | - | Moor needs a friendly or lucky flag check |
| 7b All 12 other islands | map -> island -> sail to the port, one process per island (Redmond, Oxbay, Douwesen, Conceicao, IslaMuelle, KhaelRoa, Guadelupa, Sent, Gaity, Cuba, Cayman, Terks) | all load, 0 `[errors]`/`[unresolved]`; Moor reached on Cayman, KhaelRoa, Oxbay, Terks | - | - | - |
| 8 Antigua town fuzz | port, town, store, shipyard, townhall, tavern, usurer, church, 60+ characters | no errors; hostile guards alarm lines for a pirate hero | RU leaks in guard lines ("Стража", "Следуйте за мною"), shipyard "Корабли", ship names with Russian "э" (Нуэстра) | translation agent | - |
| 9 Store / shipyard / tavern crew screens (Antigua) | dialog -> trade screen (Space/Enter picks the ship), shipyard list, hire crew | OK; ship must be in port (`location.from_sea`), else "no ship" (script) | stray "6" text left of the ship slot on the ship-choice screens, ship type label overflows its frame | - | cosmetic, needs the original to compare |
| 10 Sea encounter from the map | script-generated merchant/follow encounter -> "Sails on the horizon" -> Attack | fenc_* squadron spawns in the sea and attacks; storm weather works | `wdmCreateMerchantShip` made by a hook has no script-side encounter record, so SeaLogin skips it (hook limitation, not an engine bug) | scenario `mapencounter` | - |
| 11 Save/load in Antigua tavern | `--save`/`--load` after renting a room dialog | location, money, time kept | none | - | sleeping needs the later answers of `room_night` (not driven) |

Not reached in this pass (no time): boarding a script-spawned enemy and selling the prize, officers screen, repair at the yard, day/night while sailing.

## Part 3: with a patent (the pirate flag no longer makes every port shoot), boarding, loot, yard, officers, fort landing

Setup: `--call=GiveItem2Character(#Blaze,patent_eng)` + `--call=EquipCharacterbyItem(#Blaze,patent_eng)` (the item has `quest = "eng_flag_rise"`, so equipping it runs the same quest reaction as the governor's patent: nation 3 -> 0, England friend, France / Spain enemy, pirates enemy); `patent_fra` / `patent_spa` / `patent_hol` likewise. Replay arguments: `playtest_runs.md`, section "Part 3". Regression: `run_tests.ps1 -Sea` (short scenarios `ransackcapt yardrepair yardsell salary`) and `-Stages3` (long: `boardpatent fortland officerhire tavernsleep`).

| Stage | What was done | Result | Defects found | Fixed in | Still open |
|---|---|---|---|---|---|
| 12 Own boarding (Martinique, English patent) | French squadron hostile, `--duel` at 25 m, sails down, Enter = "Board" action; decks cleared by hook | upper deck, gun deck, cabin load, 0 `[errors]`; the enemy also boards us by himself at 25 m (script) | none | - | - |
| 13 Ransack screens | main (goods / characters / crew / swap / exit + confirm), `transfer_goods`, `transfer_crew`, `ransack_captain` (kill / take prisoner -> back to ransack main), exit sinks the prize | all open, return and leave | **`[unresolved] LeaveConfirm`** every time a screen was left (ransack_main registers the handler for `exitCancel` and never removes it, then its segment is unloaded; the original skips handlers of unloaded functions); ship-type captions ("Посилений лагер", "Королівський корабель") ran over the neighbouring face picture and were hidden *behind* the pictures (script strings were drawn under the script pictures, the original draws them above) | e2cf6a7, df14978 | - |
| 14 Sell prize / goods, repair, buy, change captain | companion ship sold in the yard (4156 gold, ship type -> 1000), store "sell all" (existing test), yard repair 54% / 40% -> 100% for 3139 gold, 4 h pass, change-captain panel | OK | none | scenarios `yardsell yardrepair` | the selected slot of the yard shows a faint second line under the ship caption (cosmetic, not reproduced in a crop) |
| 15 Four-image screens (store / yard / hire / ship) | digit and caption of the slots | the "6" and the caption were drawn at the *left edge outside the slot*; in `xi_four_img.cpp` the string anchor is the slot centre + offset and the sign of the offset is the alignment | stray "6", caption overflow (part 2 findings) | d2dc879 | - |
| 16 Officer hire, crew, passengers | Antigua tavern: "I need an officer" -> boatswain -> the man arrives -> hire (price 4240) -> passengers screen shows him; "crew" screen (part 2) | OK; the tavern needs `bBettaTestMode=1` for a sure "yes" (the script rolls `rand(2)`) | price text "на4290" (the hand-polished fragment lost the trailing space of the Russian literal; 74 entries) | a52282a (+ unit test) | MT slips in `Enc_Officer_dialog` ("згоднаен", "Всего добре", "доберему", "теснити") |
| 17 Officers' orders | the land command menu of this version only has Fast reload / Dialog / Open door (no officer orders on land: script `LandInterface.c`); ship orders: companion `SailTo / Defend / Charge ...` through the C menu | OK, `BI_CommandEndChecking` / `BI_LaunchCommand` chain with 0 errors | none | - | - |
| 18 Tavern sleep, time, hunger, salary | `room_night` -> 11:34 -> 22:34 upstairs (-5 gold); `WaitDate` 8 h / 3 days at sea (`DailyEatCrewUpdate`, weather updates) 0 errors; month change -> `LaunchSalayScreen` (crew 850 + officer, "Yes" pays) | OK | none | scenarios `tavernsleep salary` | - |
| 19 Fort of Antigua / Martinique | cannon duel (part 2), then `Fort_SetAbordageMode` + `Character_SetAbordageEnable`, ship next to the fort, Enter = "Land troops" -> fort deck (`fort2`, `fort3`, `fort1`) -> fortcapture screen (goods / crew work) -> "enter battle" -> town (`GrPort`) | works after the fix | **`SeaAI.Update.Forts` was always empty**: the script never saw a fort, so `LandTroops` (and entering a destroyed fort's port, `FORT_DEAD`) could never be offered | this pass (Sea.cs: idx / relation / Distance to the fort label / d_ay as in `ai_ship.cpp`) | Martinique's fort has `Abordage.Enable = 0` until its quest enables it, and a storm weather (`bWeatherIsStorm`) disables boarding and landing: script rules, not engine |
| 20 First story lines | tavern convoy quest (Goris Tantum: `prepare_convoy_quest_*` -> `convoy_agreeded`), Ines -> Blaker chain (part 1), `FirstEnterMap` (part 1) | start and advance, 0 errors | none | - | - |

FPS 95-100 in every stage (1280x720); `godot.log` has only the exit-time RID leak warnings.
