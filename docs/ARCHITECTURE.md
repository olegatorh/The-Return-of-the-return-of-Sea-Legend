# Architecture: how the port works

## 1. Big picture
```
 original scripts (PROGRAM/*.c, cp1251)         ← unchanged, ~2500 functions, 1100 files
        │  parsed by StormScript (Lexer → Preprocessor → Parser → AST)
        ▼
 Interp (StormScript/Runtime)  — tree-walking interpreter, values, attribute trees, events, builtins
        │  SendMessage(entity, "fmt", MSG_x, args...)   CreateEntity(&obj, "class")   Event/PostEvent
        ▼
 Godot game (game/)   — one C# class per engine "entity class": XView (xinterface), LocationView (location),
                        CharacterView (NPCharacter/Player), DialogView, SoundView, OutdoorEnv, LogHud, ...
        ▲  raises events back into the script (Interp.FireEvent): frame, CharactersStateUpdate, Location_CharacterEndTask, ...
```
`game/Main.cs` is the glue: it loads `seadogs.c`, registers `Interp.EntityHandlers["class"]` delegates, runs the per-frame loop (`_Process`), input, camera,
game clock, test hooks, and rebuilds the world after a load.

## 2. The script language and the interpreter (StormScript/)
* Storm Script is C-like: `int float string bool ref aref object`, arrays, `#include/#define/#ifdef/#event_handler`, `call funcname(args)` (dynamic call),
  `obj.(expr)` dynamic attribute access. Details and the VM's quirks (string comparison, truthiness, `&` semantics): **`docs/vm_semantics.md`**.
* **Everything that looks like an object is an attribute tree** (`Attr`: name, string value, ordered children). All values are strings in attributes; numbers are
  re-parsed on read. `object X;` globals own a root `Attr`; `ref/aref` variables point at some node (V.Attr).
* `V` struct: `VT.Int/Float/String/Attr/Slot`. `Slot` = a variable (`Name`, `Type`, `Val`, `Arr` for arrays). `Globals` is a dictionary of slots.
  `&var` yields the Attr for attr-typed variables, else a `VT.Slot` reference; for a `ref` parameter that the callee **re-points** (`param = other;`) the new value is
  copied back to the caller's variable after the call (`Invoke(..., outs)`).
* **Events** (`Events.cs`): `SetEventHandler(evt, fn, prio)`, `Event` (sync), `PostEvent(evt, delayMs, "fmt", args...)` (queue, run by `Advance(ms)` each frame),
  `GetEventData()` reads the argument list in order. `#event_handler("evt","fn")` registers **static** handlers that survive `ClearEvents()`.
* **Segments**: `LoadSegment("file.c")`/`UnloadSegment` load/unload script files at run time (dialogs, interfaces); includes are forgotten on unload.
* **Builtins** (`Builtins.cs`): ~100 engine functions (strings, math, attributes, entities, language, save hooks). Missing ones are logged as `UNRESOLVED function X`.
  Host-implemented functions can be registered in `Interp.HostFunctions`. The list of engine functions the scripts call is in `docs/engine_api.txt`.
* **Language layer** (`Language.cs`): `LanguageOpenFile/ConvertString` read `RESOURCE/INI/TEXTS/<RUSSIAN>/*.txt|ini` and overlay `lang/<language>/texts/`.
  String literals in scripts are translated at evaluation time by exact Russian text (`lang/ukrainian/strings/<script>.tsv`, then `_global/_common/_common_extra`,
  lowest priority the machine draft `_mt.tsv`); `DLG_TEXT[i]` lines can be replaced by index (`lang/ukrainian/dialogs/<name>.tsv`).
* **Save/load** (`SaveState.cs`): serialises Globals (+ attr trees), dynamic handlers, posted events to GZip-JSON. See §6.

## 3. Entity classes → Godot classes
Scripts create engine objects with `CreateEntity(&obj, "class")` and talk to them with `SendMessage`. Class names are matched case-insensitively.

| Script class | Implementation | Notes |
|---|---|---|
| `xinterface` (GameInterface) | `game/XInterface.cs` (`XView`) + `ScrollImage.cs`, `FourImages.cs`, `Slider.cs`, `Atlas.cs` | builds a screen from an `INI/INTERFACES/*.ini`; see §5 |
| `fader` | `Fader.cs` | `FADER_OUT` fires `FaderEvent_StartFade`, then (2 frames later) `FaderEvent_EndFade`; `FADER_IN` fades away |
| `location` | `Location.cs` (`LocationView`) | models, locators (`locators.<group>.<name>.{x,y,z,radius,vx..}` written into the entity attributes), walk patch (`.ptc`), baked lighting `.col`, saved character positions |
| `NPCharacter`, `Player`, `Character` | `Characters.cs` (`CharacterView`) + `CharacterRig.cs` | skinned models, tasks, locator detectors, idle lists |
| `locationcamera` | `Main.cs` | modes FOLLOW / TOPOS (script placed) / FREE |
| `dialog` | `Dialog.cs` | text in `Dialog.Text`, answers `Dialog.Links.lN` (+`.go`), `DialogEvent`; time scale restored when it dies |
| `Sound` | `SoundView.cs` | aliases (`INI/ALIASES`), schemes (`INI/sound_scheme.ini`), OGG music |
| `Sky`, `sea` | `OutdoorEnv.cs` | sky box, water plane + fog from the script's attributes |
| `ILogAndActions` | `LogHud.cs` | message log + action hint ("Reload", "Pick", ...) |
| `battle_land_interface` | `LandHud.cs` | portraits with HP/reload bars, message icons; land command menu (Enter): script events BI_SetPossibleCommands / BI_CommandEndChecking / BI_LaunchCommand |
| `MODELR`, `MODEL` | `Main.cs` | free-standing models (items/chests); untested |
| `CharactersGroups`, `particles`, `Animals`, `Ship`, `Island`, `Sail`, `worldmap`, ... | **not implemented** (messages ignored, return 0) | `[nohandler]` lines show the traffic |

## 4. Characters, locations, locators
* Coordinates: scripts/files use **Direct3D space (left-handed, y up)**; Godot rendering negates z. `CharacterView` keeps script-space `x,y,z,ay` and converts in `Apply()`.
* **Tasks**: `MSG_NPCHARACTER_SETTASK` (30400) with `Stay | Goto point | Runto point | Follow character | Fight | Escape | Dead`. The engine raises
  `Location_CharacterEndTask(taskName, chr)`, `Location_CharacterFollowGo/Stay(taskName, chr)` (data order: **task name first, then the character**).
  Every frame: `CharactersStateUpdate(dt)` and `CharacterUpdate(chr, dt)` (dt × TimeScale; the script's `SetTimeScale(0)` freezes dialogs).
* **Locator detectors**: `AddCharacterLocatorGroup(chr, "reload")` → message 30220. The engine raises `Location_CharacterEntryToLocator(loc, chr, group, locator)` and
  `...ExitFromLocator(loc, chr, group, locator, secondsInside)` using plan distance < locator radius. **Exits are raised before entries.** Doors with
  `autoreload` switch locations immediately; others need the `ChrAction` control (Space).
* **Reload flow** (script `reload.c`): fader out → `ReloadStartFade` (UnloadLocation) → fader EndFade → `ReloadToLocation` → `LoadLocation` → fader in. `UnloadLocation`
  deletes the `location` entity; `Main.EntityDeleted` then deletes all character entities and resets the camera mode.
* **Animations**: `.ani` = named actions (frame ranges, speeds, events) over a `.an` skeleton animation. Idle animation lists come from the script
  (`character.actions.idle.i1..`). Head models `Heads/h_*.gm` are not drawn in the world (portrait only).
* **Walkable surface**: `.ptc` triangle patch (`PtcFile.Contains/Snap`). People avoid each other (`Blocked`).

## 5. Interface system (xinterface)
* **Geometry**: the visible area is x −40..680, y −30..510 (720×540 canvas = Godot viewport). Positions in the ini files are relative to a 640×480 core placed at (40,30);
  `bAbsoluteRectangle` bits 1/2/4/8 snap left/top/right/bottom to the screen edges. `BOUNDER` draws the frame around the screen.
* **Items** (`item = [priority,][pc,]TYPE,NAME`): drawn in ascending priority; **default priority is 80** (backgrounds 60, top buttons 100+).
  Implemented types: PICTURE, SLIDEPICTURE, TEXTBUTTON, BUTTON, RECTANGLE, BOUNDER, TITLE, IMAGECOLLECTION, FORMATEDTEXT, VIDEO(no-op), STRINGCOLLECTION, LINECOLLECTION,
  STATUSLINE(empty bar), CHANGER(no-op), SCROLLIMAGE, FOURIMAGES, SLIDER, QUESTTITLE, QUESTTEXT, CONTEXTHELP(no-op). Missing: SCROLLER, SCROLLBAR, KEYCHOOSER, EDITBOX, XBOX, ...
* **Pictures** come from the atlas file `INI/INTERFACES/pictures.ini` (`Atlas.Get(group, name)`).
* **Commands**: ini lines `command = click,select:NODE,com:cmd:NODE2,event:EventName`; `XView.Run(node, cmd)` fires `ievnt_command(cmd, node)` into the script and executes
  the chained entries.
* **Data exchange** with scripts goes through attributes of `GameInterface`: `GameInterface.strings.<name>` (labels created by `CreateString`, refreshed every frame),
  `GameInterface.<scrollnode>.{ListSize,current,ImagesGroup.tN,picK.{img1,tex1,str1,img2,tex2}}` (scroll/four images), `GameInterface.nodes.<name>.value` (sliders),
  `GameInterface.title` (key of the title text). Messages: `MSG_INTERFACE_*` in `PROGRAM/INTERFACE/messages.h` (45010 init, 45012 create string, 45038 set text, ...).

## 6. Save / load
`SaveEngineState(name)` → host hook: call script `OnSave()` (remembers location, positions: `MSG_LOCATION_SETCHRPOSITIONS`), then `Interp.SaveState` → `port/saves/<name>.sdsave`.
`LoadEngineState(name)` → load saved segments, `Interp.RestoreState`, then (next frame) call script `OnLoad()` which recreates interfaces/quests and re-enters the
location (`TLPCHRPOSITIONS` puts people back). Entities are never serialised. Save UI helpers: `SaveFiles.cs` + messages 45035..45048.

## 6b. Sea mode (`game/Sea.cs`, class `SeaView`)
Entry from a port: the script (`reload.c: ReloadToSea` → `SeaLogin` in `SEA_AI/sea.c`) unloads the location and creates the entities `sea_ai`, `Ship`, `Island`,
`SEA_CAMERAS`, `battle_interface` ... Everything the port needs to know arrives as messages:
* `sea_ai` (51xxx): `AI_MESSAGE_GROUP_SET_XYZ_AY` (group position), `..SET_COMMANDER` (also sets `chr.SeaAI.Group.Name`), `AI_MESSAGE_ADD_SHIP` (character, ship type) →
  `SeaView.AddShip`: model assembled from the hull's `geometry/mastN` labels, a mast's `geometry/rey_*` labels and a yard's `sail1..sail4` corner labels
  (`ships\parus_<nation>.tga`), then events `ShpCreate` (idx, ship attr) and `Shp_UpdParams` (idx, sail state).
* The port writes back every frame: `Ship.Pos.x/y/z`, `Ship.Ang.y`, `Ship.Speed.z`; every second `SeaAI.Update.Ships/Forts` of the player (the script checks them in
  `Ship_CheckMainCharacter` to decide `bCanEnterToLand`, boarding ...). Speed/turn limits are **computed by the script** (`Ship_UpdateParameters` → `Ship.MaxSpeedZ/MaxSpeedY`
  from wind `Weather.Wind.*`, load, sail damage, skills); the port only integrates them (`SeaView.Tick`).
* Island: `Island` message 50100 loads `MODELS\<filespath.models>\<model>.gm` with the `<model>_<light>.col` vertex colours. The engine class `locator` (message 1504
  `LM_LOCATE_I`, `SeaView.LocateIsland`) fills `Islands[n].reload.lK.{x,y,z,ay}` and `.ships.l0..` from `<island>_locators.gm`; this is refilled after a load (`RelocateIslands`).
* Controls (no engine control system yet): W/S sail up/down, A/D turn, arrows/PgUp/PgDn camera, Enter/Space = execute the HUD action (`g_ActiveActionName` → event `BI_FastCommand`:
  Moor/Board/Map...), M/1..4 → `Control Activation` (`BI_MapEnter`, `BI_Charge*`). Ship camera parameters come from the `SHIP_CAMERA` entity attributes.
* Units: 1 unit = 1 m, engine heading `ay`: forward = (sin ay, cos ay) in (x, z); Godot z = -z, node yaw = -ay.
* Ship AI (`SeaView.Steer`): per-ship tasks from `AI_MESSAGE_SHIP_SET_TASK` (move, attack = come alongside at ~110 m and fire broadsides, follow, runaway); group tasks are script-level.
* Combat (`game/SeaCombat.cs`): LMB / `--fire@N` fires the broadside facing the camera; cannon positions = hull labels `cannonl/cannonr/cannonf/cannonb`, ammunition by event `CannLoad`, reload by `CannGRTime`,
  ball speed `Ship.Cannons.SpeedV0` (ballistics g = 9.81), hits raise `Shp_HullHit` (damage is the script's), `Ball_WaterHit`, `Ball_IslandHit`. A ship whose `Ship.HP` reaches 0 sinks and is removed.
  Beware: in `AI_MESSAGE_ADD_SHIP` the second argument is an entity, not the character index (use `chr.index`).
* Physics (`game/SeaPhysics.cs`): wave rocking and heel, wake wedge, ship-ship collisions (`Shp_Ship2ShipColl`), shore touches (`Shp_ShipIslColl`); the script converts impacts to damage.
* Forts (`game/SeaForts.cs`, class `AIFort`): `AI_MESSAGE_ADD_FORT` -> cannons = `cannon_N/culverin_N` labels of `<fort>_locators.gm`, events `FortCreate`, `FortCDamage`; they shoot enemies (relation = script `GetRelation`).
* Map encounters (`game/WorldEncounters.cs`): messages 311xx spawn merchant/follow/war ships on the map; contact raises `WorldMap_ShipEncounter` (the script shows the Attack/Flee screen) and the
  ships are logged into the sea through `wdmLoginToSea.encounters`. `worldMap.encounter.cur` is an SPI attribute (write hook `Attr.OnWrite`).
* Missing: original battle_interface HUD (46xxx), sail/mast damage and fires, storms/tornado, telescope, cabin/deck cameras, balls splash/smoke visuals, sea sounds, sail trim by wind.

## 6c. World map (`game/WorldMap.cs`, class `WorldMapView`)
`Sea_MapLoad` (script) fades out, `Sea_MapEndFade` calls `wdmCreateMap` which creates the entity `worldmap`; the object `worldMap` holds all data (islands with `position.x/z`, `rx/rz`
(reference point used for sea coordinates), `radius` (zone), locations with `modelName` (`MODELS\WorldMap\<name>.gm`), labels, `date.*`, `playerShipX/Z/AY`).
The port builds the scene (`Islands.gm`, sea texture, markers, labels, `ship.gm`) on the first frame, moves the ship, runs the clock (`date.hourPerSec` hours per second, events
`WorldMap_UpdateDate`, `NextDay`), reports island zones (`WorldMap_inside_islandzone`/`outside_islandzone`) and asks the script for encounters every 0.5 s (`WorldMap_EncounterCreate`).
Enter/Space raises `ExitFromWorldMap` -> `wdmReloadToSea` -> `SeaLogin` with the ship placed relative to the island (`(shipPos - island.rx/rz) * 19.6`).
The first visit starts a scripted tutorial scene (deck location `qdeck`) - that is the game, not a bug. The fader entity's events carry data `(int, fader)` (`WdmEndFade` reads it).

## 7. Pitfalls found the hard way (read before debugging)
1. **`ClearEvents()` must keep `#event_handler` registrations** — otherwise AI templates silently die after "new game".
2. The dialog class restores `TimeScale` to 1 when it is deleted; the script only sets 0.
3. `&variable` passed to string builtins (`findSubStr(&s,..)`) means the variable's value; `makeref(r, array)` means the array.
4. Item/character lookups use `Func(id, &aref)` with **copy-out** of re-pointed ref parameters.
5. Location ground uses the lightmap texture `Textures/lighting/<day>/<models path>/shadow.tga`; the harbour `sb` model (sea bottom) is hidden; `shadow.tga` of the
   generic folder is a different file.
6. Interface items without an explicit priority are drawn at 80; wrong ordering shows as "dimmed" screens.
7. Sky/sea/fog and weather are **entities created by the script** (`CreateWeatherEnvironment`); the port reads their attributes each frame.
8. Hero models in `HeroDescribe.txt` are comma lists; the first name is the model. Hero type/nation values in that file are used by scripts verbatim — never translate them.
9. Script defects exist (missing `if`, stray `)`, Cyrillic letters in identifiers): `docs/source_defects.md`; the parser recovers.
10. `Bring2Range(min1,max1,min2,max2,v)` maps v from [min2,max2] ONTO [min1,max1] (it was reversed once; unit-tested).
11. After a code change that "does nothing": check `[unresolved]`, `[nohandler]`, then trace with `SEADOGS_TRACE`.

## 8. Formats (all little endian; readers in StormScript/Formats and tools/*.py)
`.tx` 24-byte header + DXT/uncompressed mips; `.gm` "RDF 1.05" geometry (materials, labels = locators, objects, vertex types 0/1/4); `.an` skeleton animation
(frames, joints, fps, parents, joint offsets, root positions, quaternions; **meshes are modelled in the pose of frame 0**); `.ani` action table (text);
`.ptc` walk patch; `.col` baked per-vertex colours (BGRA, ×2 gain); `.ini` Storm ini. Details in README.md.

## 9. Where to look for what
| Question | Look at |
|---|---|
| How is message X handled? | `grep -rn "case 4501" game/` and `docs/engine_surface.txt` |
| What does the script expect from class Y? | grep `SendMessage(&Y` in `addon/merged/PROGRAM`, read the `*.h` message defines |
| Which engine functions are missing? | run any test, read `[unresolved]`; `docs/engine_api.txt` |
| Translation coverage / workflow | `README.md` ("Language"), `tools/coverage.py`, `tools/mt/texts.py` |
| Open problems | `CLAUDE.md` (Status), `README.md` ("Seen but not done") |
