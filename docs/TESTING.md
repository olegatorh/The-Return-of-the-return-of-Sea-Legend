# Test hooks (Godot user args) and debug env vars

`run_tests.ps1 -Quiet` prints only failures and the verdict (`ALL OK` / `FAILED (n)`): use it for full-suite runs to save tokens.

Moved out of `CLAUDE.md` (2026-11-09) to keep the entry point short. How to run the suites: `CLAUDE.md`, section Build / run / test.

**Manual test hooks** (Godot *user args* after `--`; frames are counted from start; the game window runs at ~60 fps, 1280x960):
| Arg | Effect |
|---|---|
| `--click=NODE@N` | click interface node at frame N (e.g. `B_NEWGAME`, `RUNGAME`): a direct call, bypasses the input system |
| `--mouse=NODE@N` | the same through a REAL mouse click (motion + press + release) in the middle of the node; `--key=Space@N` sends a real key. Env `SEADOGS_KEYLOG=1` logs every key with the UI state |
| `--type=Text@N` | real key presses with characters (text inputs: ship rename); `+` = space, `%XXXX` = Unicode hex (`%0457` = ї) |
| `--answer=1@N` | choose dialog answer 1 at frame N |
| `--ctl=Name@N` | send a control to the script (`Quick_Save`, `Quick_Load`, `Interface`, `MainMenuDouble`, `ChrAction`) |
| `--goto=group.locator@N` | teleport the player to a locator (e.g. `reload.reload3_back`) |
| `--call=Func(arg,..)@N` | call a script function; ints, strings (`+` = space), `#CharacterId` |
| `--set=var=value@N`, `--dump=Global[i].path@N` | set / print script variables |
| `--dialog=Id_with_underscores@N` | start a dialog with a character |
| `--fuzz@N` | talk to everyone in the location, click the first answer; prints missing engine functions |
| `--shot=path.png --shot-frame=N` | save a screenshot (path **without spaces**) and print diagnostics, then quit |
| `--camdist=N`, `--move=X,Y@A-B`, `--control@N` | camera distance, hold a move direction (at sea: X = steering), give control to the player |
| `--sail=+1@N`, `--act@N` | sea: sail state up/down; execute the offered sea action (Moor...). Env `SEADOGS_SEACAM=dist,pitch,yawOffset` fixes the sea camera; `SEADOGS_SCOPE=1` (or `=N`: from frame N) holds the telescope up; `--ctl=Sea_CameraSwitch@N` = deck camera (Tab), `SEADOGS_DECKCAM=yaw,pitch` its view |
| `--duel@N` (env `SEADOGS_DUELDIST`, default 80 m), `--killgroup=brdenemy@N` | sea: park the first enemy ship abeam (25 m = close enough for boarding); land: kill every living member of a script group (boarding decks, test `board`) |
| `--mousemove=dx,dy@A-B`, `--btn=L+@N` | raw mouse motion per frame (ChrTurnH / ChrCamTurnV, ShipCamera_Turn_*: through the control service, needs no captured mouse); a real mouse button press (`L`/`R`/`M`, `+` down, `-` up). Env `SEADOGS_CTLLOG=1`: key group + locked controls every 100 frames. Controls: docs/CONTROLS.md |
| `--classic`, `--look@N`, `--at=x,y,z,ay@N`, `--jump@N` | land: the original control scheme (mouse turns the hero; with it `--move=turn,fwd`: turn x 0.12 rad per frame, fwd = +-1); Tab (first person) at frame N; stand at a point of the walk patch (engine coordinates); jump forward at frame N. Env `SEADOGS_CAMLOG=1` (camera radius/yaw every 50 frames), `SEADOGS_JUMPLOG=1` (ledge scan, jumps, swimming), `SEADOGS_NOSHADOW=1`, `SEADOGS_SHADOWDEBUG=1` |
| `--mappos=x,z,ay@N`, `--mapstorm=dx,dz@N`, `--mapmeet@N` | world map: place the ship; a storm at ship + (dx, dz) (`0,0` = sail into it); put the ship next to the first encounter ship |
| `--mastfall@N`, `--sailholes@N` (`=me@N`: own ship) | sea: break the main mast / put balls through 8 sails of the nearest other ship through the script events (game/SeaSails.cs) |
| `--shipfire@N`, `--shipboom@N` (`=me@N`: own ship) | sea: set 3 fire places of the nearest other ship on fire (ShpActFirePlace + ShpFDamage) / explode it (Ship_Serious_Boom: blast + debris). Env `SEADOGS_PSLOG=1` logs particle systems (game/Particles.cs, SeaFires.cs) |
| `--sharks[=jump]@N`, `--blot@N`, `--splash@N` | sea: a shark abeam of the ship (pinned; `=jump` starts the jump); three balls through the nearest fort wall (Martinique duel recipe) and the ship moved in front of it; three BallSplash effects abeam. Env `SEADOGS_LOOKSUN=<pitch offset deg>` aims the sea camera at the sun, `SEADOGS_NOSUNFX=1` turns the sun glow off |

Sea test recipe (from a save made in the town port): `--ctl=Quick_Load@200 --call=initTutMainCharacterShip()@300 --call=SetPlayerShipLocation(184)@320 --goto=reload.reload1@400 --ctl=ChrAction@450`
(184 = index of the port location in `Locations[]`); `run_tests.ps1 -Sea` does exactly this.

Env vars: `SEADOGS_TRACE=Func1,Func2` (log script calls), `SEADOGS_MATLOG`, `SEADOGS_DETECTLOG`, `SEADOGS_RIGLOG`, `SEADOGS_SOUNDLOG`.
