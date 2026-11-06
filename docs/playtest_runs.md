# Playtest runs: hook arguments to replay every stage

Written during the QA-by-playing pass (see `PLAYTEST.md` for the findings). Every run is one Godot process driven only by the test hooks of
`CLAUDE.md` (user args after `--`), so it can be replayed by the owner or a later agent. Frames are counted from the start; a dialog answer needs
~80-100 frames between two answers (the view rebuilds one frame after the script writes the node), so space them like below.

Launcher used (any equivalent works):

```powershell
$g = "godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
& $g --path game --position 9000,9000 --resolution 1280x720 -- <args> --shot=pt_NAME.png      # window off-screen; log = stdout, errors = stderr
$env:SEADOGS_LANGLOG = '1'     # new: every dialog line / answer is logged as "[lang] line|answer [RU|EN]: text" (RU = Russian-only letters, EN = Latin words only)
```

`--save=NAME@N` / `--load=NAME@N` write / read `saves\NAME.sdsave` through the script's own save path and do **not** touch `quicksave`.
`--fuzz@N` may now be given several times (each restarts the "talk to everybody in this location" round after a location change).

Notes that cost time while writing these: (1) after a load the script sets `lockedReloadLocator` to the **first** reload locator of the location
(`LoadMainCharacterInFirstLocation(..., "")`), so the first walk onto that locator is swallowed once (original behaviour, `reload.c:174`): in a test
step onto an auto-reload locator twice. (2) Answers are numbered in the order the script creates the links (`link.l2` created before `link.l1`
shows first). (3) `--dialog=Id` needs the character to be in the loaded location (`canDlg=0` otherwise).

## Stage 0 - new game, intro, tutorial deck, arrival in the port (creates the save `pt_port`)

```
--click=B_NEWGAME@200 --click=RUNGAME@400
--answer=1@2700 --answer=1@2800 --answer=1@2900 --answer=1@3000 --answer=1@3100 --answer=1@3200 --answer=1@3300 --answer=1@3400 --answer=1@3500
--save=pt_port@9300 --shot-frame=9500
```
Expected log: dialogs `Gr_Smuggler Agent`, `Gr_ALexusB` (5 answers), `Gr_Abracham Gray` (start -> start1 -> game_begin), `Locations[618]` (the deck `capsm`) then
`Locations[184]` (QC_port), the hero stands on the pier at `reload2` (-8.6, 2.1, 15.2).

## Stage 0b - tutorial deck, the fighting branch (start2 -> start3 -> start4 -> `Tut_KillOnShip`)

```
--click=B_NEWGAME@200 --click=RUNGAME@400 --answer=1@2700 ... --answer=1@3300      (8 answers, every 100 frames)
--answer=2@3400 --answer=2@3500 --answer=2@3600 --answer=2@3680 --answer=1@3800
--swing@4000 --swing@4050 ... --swing@6200 --shot-frame=6500
```
Result: the dialog works, Abracham attacks (`[fight] ... hp 51 -> 21 -> 0`), the hero dies and the game goes back to the main menu (as in the original: "you have
no chance"). Untouched hero: 2 blows kill him; the hero's own blows are covered by the `meleehero` scenario.

## Stage 1 - the town of Quebradas Costillas: every location loads, everybody can be talked to

Tour of 16 locations (loading only): `--load=pt_port@200` then every 300 frames
`--call=DoReloadCharacterToLocation(<id>,reload,reload1)@N` for `QC_town QC_Store(locator2) QC_shipyard(locator2) QC_residence(Reload1) QC_UsurerHouse QC_Tavern
QC_tavern_upstairs QC_brothel QC_brothel_upstairs QC_Pirate_house QC_Pirate_house_inside QC_town_exit QC_Jungle_01 QC_jungle_02 QC_Dungeon QC_port`, `--shot-frame=5300`.

Fuzz round (talk to everybody, first answer, 12 steps max) in the 7 town interiors:
```
SEADOGS_LANGLOG=1
--load=pt_port@200 --fuzz@400
--call=DoReloadCharacterToLocation(QC_town,reload,reload1)@1300 --fuzz@1400
--call=DoReloadCharacterToLocation(QC_Store,reload,locator2)@2400 --fuzz@2500
--call=DoReloadCharacterToLocation(QC_shipyard,reload,locator2)@3500 --fuzz@3600
--call=DoReloadCharacterToLocation(QC_residence,reload,Reload1)@4600 --fuzz@4700
--call=DoReloadCharacterToLocation(QC_UsurerHouse,reload,reload1)@5700 --fuzz@5800
--call=DoReloadCharacterToLocation(QC_Tavern,reload,reload1)@6800 --fuzz@6900
--call=DoReloadCharacterToLocation(QC_brothel,reload,reload1)@7900 --fuzz@8000 --shot-frame=9100
```
Result: 29 characters, 0 `[errors]`, 0 `[unresolved]`, 0 empty answers.

## Stage 2 - saving / loading in the middle of each place

```
--load=pt_port@200 --set=Characters[0].Money=50000@290
(for each of QC_town, QC_Store(locator2), QC_Tavern, QC_UsurerHouse, QC_residence(Reload1), QC_shipyard(locator2), step f = 300 + 900 k:)
--call=DoReloadCharacterToLocation(<id>,reload,<loc>)@f --save=pt_s_<id>@f+250 --call=DoReloadCharacterToLocation(QC_port,reload,reload2)@f+300
--load=pt_s_<id>@f+500 --dump=Characters[0].location@f+800 --dump=Characters[0].Money@f+800
```
Result: the dumped `location` is the saved one every time, money kept, no errors.

## Stage 3 - store, shipyard, tavern

Store (talk, "let's see your goods" opens the trade screen):
`--load=pt_port@200 --set=Characters[0].Money=50000@290 --call=DoReloadCharacterToLocation(QC_Store,reload,locator2)@300 --dialog=Peter_Blowhorn@700 --answer=1@800 --answer=1@900 --shot-frame=1300`

Shipyard, buy the first ship with the keyboard only (mode numbers of `shipyard.c`: 1 empty view, 6 menu, 0 list, 2 buy confirmation):
```
--load=pt_port@200 --set=Characters[0].Money=50000@290 --call=DoReloadCharacterToLocation(QC_shipyard,reload,locator2)@300
--dialog=Jakob_Nickerson@700 --answer=1@800 --answer=1@900 --answer=1@1000
--key=Right@1100 --key=Enter@1150 --key=Enter@1250 --key=Enter@1350 --key=Enter@1450
--dump=Characters[0].Ship.Type@1600 --dump=Characters[0].Money@1600 --shot-frame=1800
```
Result: Ship.Type 0 (tartane), money 50000 -> 48918 (price 1082), the screen stays open for the next purchase.

Tavern keeper (Ines Diaz, walks with a `goto` template and is dialog-ready while in `ani`/`stay`):
`--load=pt_port@200 --call=DoReloadCharacterToLocation(QC_Tavern,reload,reload1)@300 --dialog=Ines_Diaz@500 --answer=1@600 --answer=1@700 --answer=3@800`

## Stage Q - the first quest chain (also `run_tests.ps1 -Quest`, from a NEW game)

Ines "who hires a crew" -> William Blaker -> night in the port -> peaceful way to the ship -> deck fight against 3 mutineers -> sailor in the port -> Sailor_Blaker:
```
--click=B_NEWGAME@200 --click=RUNGAME@400  (8 answers 2700..3400) --answer=1@3400 --answer=1@3500
--call=DoReloadCharacterToLocation(QC_Tavern,reload,reload1)@4400
--dialog=Ines_Diaz@4800 --answer=1@4900 --answer=1@5000 --answer=2@5100 --answer=1@5200 --answer=1@5300
--dialog=William_Blaker@5700 --answer=1@5800 --answer=1@5900 --answer=1@6000
--answer=2@6700   (Take_Ship_4: the peaceful answer is the second link)
--answer=1@7500 --answer=1@7700 --call=LAi_SetImmortal(#Blaze,1)@7650 --killgroup=TmpEnemy@8300 --killgroup=TmpEnemy@8400
--answer=1@8800 --answer=1@9000 --answer=1@9200 --answer=1@9400 --answer=1@9600
--dump=Characters[0].Ship.Type@10000 --dump=Characters[0].location.from_sea@10000 --shot-frame=10100
```
Result: `Ship.Type = 26` (Lugger1, crew 35), `from_sea = QC_port`, hero at the port. Without the immortality hook the 3 mutineers kill the (idle) hero in 2 blows each
(fantom damage 28 of 51 hp): that is the script's balance, the fight itself is covered by `meleehero`.

## Stage 4 - leaving the port to sea with the ship from stage Q

```
--load=pt_quest1b@200 --goto=reload.reload1@400 --ctl=ChrAction@450 --sail=+1@800 --sail=+1@805 --move=1,0@900-1100 --shot-frame=1400
```
(`pt_quest1b` = a save made with `--save=pt_quest1b@4600` at the end of stage Q.) Result: `[sea] ship Blaze group='OurGroup' ... Lugger1`, sailing, no errors.

## Later stages (sea, world map, first island, sea fight, boarding, sell goods, rename, screens)

Already driven by the scenarios of `run_tests.ps1 -Sea` (`sea map mapnight storm fight neutral board trade hold yard door chest landhud melee meleehero drawrun sailto rename
screen_*`) from `saves\quicksave.sdsave`; their hook strings are in the script. Quick save / load in the middle of those stages: `--ctl=Quick_Save@N` / `--ctl=Quick_Load@N`
(overwrites `saves\quicksave`!); the `--save=/--load=` pair above does not.
