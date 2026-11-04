> **Orientation for newcomers (human or AI):** read `CLAUDE.md` (rules, layout, how to run/test), then `docs/ARCHITECTURE.md` (how script and engine talk),
> then `docs/ROADMAP.md` (open work). This README is the long chronological log of discoveries.

# Sea Dogs 2 / PotC v1.03 (+ VML bits) -> modern engine port

Goal: run the original game data + original Storm scripts (`../PROGRAM/**/*.c`) on a modern,
text-friendly engine (plan: Godot 4 + C#), à la OpenMW/ScummVM. Originals are never modified.

## Tools (`tools/`, pure Python 3, no dependencies)
| tool | what | status |
|---|---|---|
| `tx2png.py` | `.tx` (DXT1/3/5, A8R8G8B8, X8R8G8B8, R5G6B5, A1R5G5B5, A4R4G4B4) -> PNG | 2224/2224 files decode; 4 files with header flags=2 (cube maps?) have a different size layout, output unverified |
| `gm.py` | reader for `.gm` (RDF '1.05'): materials, labels(locators), objects, triangles, vertex buffers | 2889/2894 files; 5 older-version files (v1.01/1.02: big_sky, sky, small_sky, light, sphere) not supported yet |
| `gm2gltf.py` | `.gm` -> glTF 2.0 (z negated for handedness), locators as empty nodes | 2889/2894 ok; skinned type-4 vertices exported in bind pose, no skin/weights yet |
| `preview_gm.py` | software-rendered PNG preview for sanity checks | works |

Vertex buffer types: 0 = 36 B (pos,norm,color,uv), 1 = 44 B (+uv1), 4 = 44 B (pos,weight,boneid,norm,color,uv).

Output goes to `out/` (git-ignored): `out/textures`, `out/models`.

## The addon "Return of the Sea Legend" is inside `stormex2.dll`
The folder looked like the base game (v1.03 scripts in `PROGRAM`, dated 2002-2003), but the addon's code is **embedded in
`stormex2.dll`** (Jan 2006): a 9 KB stub PE followed by an overlay of 691 zlib streams (the script files, 9.2 MB of source) and an
index at the end: per entry `u32 name_length | name | u64 uncompressed_size | u64 stream_offset`. `data1.dat` is unrelated (random-looking, not used by our work).
`tools/unpack_stormex2.py <dll> <out>` unpacks it (691/691 sizes verified); `tools/merge_addon.py` overlays it on the base `PROGRAM`
(the engine evidently reads the archive first, then the folder): 389 new files (305 dialogs of the new islands/NPCs, interface, sea_ai,
characters, locations), 206 changed (incl. `seadogs.c`, `globals.c`), 96 identical, 459 base files not overridden.
Result in `addon/merged/PROGRAM` (git-ignored, regenerate with the two tools).

Addon state after "New game" (headless, `docs/run_addon.txt`): 2520 script functions, 0 runtime errors, 1368 characters (base: 968),
619 locations (288), 15 islands (8), 173 items (128), 23 stores (9), 35 goods (27). Parser: 1124/1139 files clean, 15 with recovered
syntax errors, 0 unparseable. Still to implement for it: `LanguageOpenFile/ConvertString/CloseFile/GetLanguage` (text files under
`RESOURCE/INI/TEXTS/<LANGUAGE>`) and `XI_ControlMakeInvert/SetColorCorrection/SetMouseSensitivity/ConvertString`.
More facts: `#define` names are **case-sensitive** (items.h has `#define MAP "map"` while scripts use the attribute `.map`; do not change this); variable/function/keyword names are case-insensitive; names may contain cp1251 letters (a Cyrillic "c" typo exists in a shipped script).

## Language: Ukrainian (`lang/ukrainian/`, `LanguageService`)
The game has Russian and English texts only. The port adds a **Ukrainian layer** that is merged key by key over the Russian base, so a
partial translation always works (untranslated keys stay Russian). Default language of the Godot port: `Ukrainian` (`--lang=Russian` to switch back).
- `lang/ukrainian/texts/<file>`: same formats as `RESOURCE/INI/TEXTS/RUSSIAN` (`string = key,"text"` in `common.ini`, `key{text}` in `.txt`), UTF-8.
  Done: `common.ini` (604 keys: all screen titles, buttons, goods, ships, skills, help hints, messages, towns), `commands_name.txt`, `SeaSection.txt`,
  `ShipEatGood.txt`, `HelpSectionTitles.txt`, `globals.txt`.
  Not yet: `ItemsDescribe` (1133 lines), `ControlsNames` (484), `AbilityDescribe` (317), `QuestBook` (305), `ShipsDescribe` (302), `GoodsDescribe` (240),
  `HeroDescribe` (373), `RPGDescribe` (111), `StartGameDescribe` (90), `MapDescribe` (75), `HELPSECTIONS/`.
- Dialogs: the lines are the `DLG_TEXT` array of every `DIALOGS/*_dialog.h` (278 files, **12 831 lines, ~115 000 words**). `lang/ukrainian/dialogs/<name>.tsv`
  (`index<TAB>text`) replaces single lines when the dialog segment is loaded (`Interp.Declare`), the rest stays Russian. Done so far: lines 0-32 of
  `Malcolm Hatcher_dialog` (the boatswain's introduction), as a working sample; the two tutorial dialogs `Malcolm Hatcher_dialog` and `blaze_dialog` are ~500 lines.
- Engine side: `LanguageOpenFile/ConvertString/CloseFile/GetLanguage`, `XI_ConvertString` read through the layer; interface captions (`string = New Game`) are
  keys of `common.ini`; `CreateString` labels use fixed-width boxes and map the original bitmap fonts (Dialog21 small, Dialog2 title) to sizes.
- Tools: `tools/extract_dialogs.py <PROGRAM> --stats | --dump <name>...` (source lines), `tools/check_lang_all.py lang/ukrainian` (Latin letters inside
  Ukrainian words, Russian-only letters ы э ъ ё, stray accents), `tools/check_lang.py` (key coverage of `common.ini`).
- Pictures with baked-in text (loading screens `loading\*_rus.tga`) stay Russian; so do the Russian voice lines in `Sounds/VOICE`.

### Character movement, player control, engine loop (`game/Characters.cs`, `Main.cs`)
The engine side of the script AI is implemented: every frame `CharactersStateUpdate(dt)` and `CharacterUpdate(chr, dt)` are raised (dt scaled by the script's
`SetTimeScale`, 0 during dialogs), `MSG_NPCHARACTER_SETTASK` runs tasks (`Goto point`, `Runto point` at the .ani speeds 1.6 / 3.3 m/s, `Follow character`, `Fight`,
`Escape`, `Dead`, `Stay`/`None`) and `Location_CharacterEndTask(name, chr)` is raised on arrival - the AI templates in `Loc_ai/` rely on it (the waitress in the
Fort-de-France tavern walks around by herself). Turn/locator messages, `ENTRY_TO_LOCATION`, `InDialog`, `ACTIONPLAY` (scripted action while idle) are handled.
Player (AI type `player`): W/A/S/D move relative to the camera, Shift runs, the character cannot leave the walkable `.ptc` patch (`PtcFile.Contains`, sliding along edges);
camera follows (arrows or right mouse drag orbit, wheel zoom); **E/T** talks to the nearest person within 3.2 m; F1 free camera; F2 name tags.
Test hooks: `--control@FRAME` (ends the scripted intro: `LAi_SetPlayerType`), `--move=X,Y@A-B` (hold a direction between frames).
Not yet: collisions between people, sitting/other poses (sitting NPCs stand on the benches), fighting, items, weapons in hands, `Location_Character*` entry events.

### The real game start works (hero selection -> tavern intro -> story -> first location)
`New game` -> the hero-selection screen (`boal_newchar.ini`; picture atlas `game/Atlas.cs` reads `INI/INTERFACES/pictures.ini` = groups ICONS, ICONS_SPEC, ...;
node types BUTTON, RECTANGLE, BOUNDER, TITLE, IMAGECOLLECTION, FORMATEDTEXT added to `XInterface.cs`, `SetNewPicture`/`SetFormatedText` messages, live refresh of
`GameInterface.strings.*`) -> `Start` -> the addon intro runs **entirely through the original scripts**: the hero sits in the Fort-de-France tavern, `Romantic_Pirat`
walks up (follow task + `Location_CharacterFollowGo/Stay` events), the story dialogs play (`--answer=1@N` test hook clicks through them), `newMainHero_end` reloads to the
tutorial cabin / next locations (fader protocol `StartFade` -> hold -> `EndFade`, `UnloadLocation` deletes the location and the people in it).
Engine-side fixes found on the way (all were silent logic bugs, worth remembering):
- `ClearEvents()` used to wipe every handler incl. those declared with `#event_handler` (LAi templates, controls...) - now only run-time `SetEventHandler` ones go
  (`Interp.SetStaticHandler/ClearDynamicHandlers`).
- the dialog class restores `TimeScale` to 1 when it dies (the script only sets 0), else everything froze after the first dialog.
- `makeref(ref, someArray)` makes the reference denote the array (random ship names); `&variable` given to string builtins (`findSubStr(&s,...)`, `strcut`) now yields
  the variable's value (`Conv.ToStr/ToInt/ToFloat` follow slot references) - fixed hero model lists `a,b,c`.
- `NativeFindLoadCharacter` returns the index of the loaded location (reload uses it to know what to unload).
- `locationcamera` entity: FOLLOW / TOPOS (script-placed camera, used in the intro) / FREE; `MSG_CHARACTER_GETMODEL`; sound-by-name messages ignored.
Debug aids: `SEADOGS_TRACE=Func1,Func2` (env) logs calls of script functions; Godot hooks `--dump=Global[i].path@frame`, and at screenshot time the log lists
runtime errors, unresolved functions, fired events with handlers, live entities and every character's state.
Outdoors (`game/OutdoorEnv.cs`): the script's `Sky` entity -> sky box from the `weather/skies/N` textures `sky_side`/`sky_up`, `sea` -> opaque water plane at y=0 with a
fresnel shader and fog from `Sea.Fog.*`; the harbour "sea bottom" model (`models.always.sb`) is hidden. The ground of a location uses the baked lightmap texture
`shadow.tga` from `Textures/lighting/<day>/<models path>/` (not the generic `INTERFACES/shadow.tga`). Locator detection (`AddCharacterLocatorGroup`) raises
`Location_CharacterEntryToLocator/ExitFromLocator` (plan distance only): doors/transitions with `autoreload` switch locations by themselves; the "Reload/Closed/Pick"
action hint and the message log are drawn by `game/LogHud.cs` (`ILogAndActions`). Original control names are sent to the script as "Control Activation":
Esc=MainMenuDouble, F1=MainMenu, F2=Interface, Space=ChrAction, F4/F6 quick save/load; F3 name tags, F9 free camera.
Test hooks added: `--ctl=Name@frame`, `--goto=group.locator@frame` (puts the hero on a locator); env `SEADOGS_MATLOG`, `SEADOGS_DETECTLOG`, `SEADOGS_TRACE`.
**Interface geometry (important):** the visible interface area is x -40..680, y -30..510 (a 720x540 canvas); item positions in the `.ini` files are relative
to the 640x480 core placed at (40,30) in it, `bAbsoluteRectangle` bits 1/2/4/8 snap the left/top/right/bottom edge to the screen edge (BOUNDER draws the frame
around the whole screen). The Godot project therefore uses a 720x540 viewport. More node types: STRINGCOLLECTION, LINECOLLECTION, STATUSLINE (empty bar),
SCROLLIMAGE (`game/ScrollImage.cs`: strip of atlas pictures driven by the script's `GameInterface.<node>.pic*/ImagesGroup/current/ListSize`), command chaining
`com:cmd:NODE` / `select:NODE`, messages PLACE_IMAGE, CHANGE_STR_COLOR, SCROLL_CHANGE. Character screen (F2) and item screen render with real data.
Interpreter fix with big effect: `&variable` passed for a `ref` parameter is now copied back when the callee re-points the parameter
(`Items_FindItem(id, &arItem)` returned nothing before).
More done afterwards: sound (`game/SoundView.cs`: aliases from `INI/ALIASES`, schemes from `INI/sound_scheme.ini`, OGG music, PCM WAV), sitting NPCs (the script's
`actions.idle.*` list is played at random), quest book (QUESTTITLE/QUESTTEXT, message 45031/45033, texts from `QuestBook.txt`), `GetAssembledString`
(`#sName#`, `#dName#`, `#f.2Name#`), people no longer walk through each other, exits are reported before entries (locator detection), dialog answers wrap.
The head models `Heads/h_*.gm` are NOT drawn in the world (the body model has its own head; the heads are for the dialog portrait).
Language: `tools/mt/texts.py` machine-translates the `RESOURCE/INI/TEXTS/RUSSIAN/*.txt` files into `lang/ukrainian/texts/` (draft, tags protected),
`tools/polish_items.py` re-applies the hand-corrected item names, `tools/names_check.py` + `tools/names_apply.py` write transcribed character names
(Russian -> Ukrainian letters) into `strings/_common_extra.tsv`. Hero names (HeroDescribe) are set by hand.
Test hook: `--call=Func(arg,arg)@frame` (ints and strings), `--camdist=N`. Full run of the intro to the first location: see "The real game start works".
**Save / load works** (`StormScript/Runtime/SaveState.cs`, `game/SaveFiles.cs`): `SaveEngineState(name)` writes every global variable + attribute tree, the run-time event
handlers and the posted events to `port/saves/<name>.sdsave` (GZip JSON) and `ISetSaveData` the description (`.desc`); `LoadEngineState` restores them (loading the saved
script segments first) and the game window then calls the script's `OnLoad()` exactly like the original engine did (it re-creates the interfaces, quests and re-enters the saved
location); `OnSave()` is called before saving and asks the location to remember where everybody stands (`MSG_LOCATION_SETCHRPOSITIONS/TLPCHRPOSITIONS/CLRCHRPOSITIONS`). Works from a fresh process (F4 quick
save, F6 quick load, also Esc -> menu). Not stored: thumbnails, entities of the sea. A game clock runs while a location is shown (one game minute per 2.5 s via `AddTimeToCurrent`).
Also: `SLIDER` nodes (volume sliders in the options; value in `GameInterface.nodes.<name>.value`), `FOURIMAGES`, interface item priority (items without one sit at 80: above the
background layers 60, below 100+ - fixes the dimmed screens), camera collision (trimesh colliders of the visible location models, the follow camera shortens its boom
at walls), free-standing models (`MODELR`/`MODEL`: items/chests on the ground - untested on screen), save thumbnails are not stored.
Test hook `--fuzz@FRAME`: talks to everybody in the location and clicks the first answer repeatedly, then prints the engine functions the scripts needed
(`[unresolved]` lines) - a cheap way to find missing engine API. `tools/polish_places.py` holds hand translations of place labels and ranks.
Seen but not done: `CharactersGroups` class (relations/targets: huge call volume `GetTrg`, `MoveChr`), `Sound`, `battle_land_interface` (HUD), `particles`, `Animals`,
sea travel (islands as reload targets), fighting, items/inventory screens, saves.

### Dialog window and the addon's real start (`game/Dialog.cs`)
The engine class `dialog` is implemented: the script writes the NPC line to `Dialog.Text` and answers to `Dialog.Links.l1..` (+ `.go`); the view
rebuilds whenever they change, an answer click stores `Dialog.CurrentNode = go` and raises `DialogEvent`; `DeleteClass(&Dialog)` closes it.
Test hooks (Godot user args, `_` stands for a space): `--dialog=Antoine_Lebretton@1200` (calls the script's `DialogMain`), `--answer=1@1300`,
`--set=var=value@frame`, `--call=Function@frame`, `--click=NODE@frame`, `--shot=<png> --shot-frame=N`. Key `T` talks to the nearest character.
**The addon does not start in the tutorial cabin**: "New game" opens a hero-setup screen (not built yet: SCROLLIMAGE/FORMATEDTEXT nodes), then the hero
`ALexusB` (AI type `actor`, scripted intro) appears in the Fort-de-France tavern (`Falaise_de_fleur_tavern`: Antoine Lebretton (barman), two regulars,
a waitress, "Romantic_Pirat", a smuggler). The scripted intro needs character movement (`MoveChr`), which is not emulated; for the dialog test the hero
is switched to AI type `player` as the game does when the intro ends.

### Translating script text
Two mechanisms, both in `LanguageService`: `DLG_TEXT` lines by index (`lang/ukrainian/dialogs/<name>.tsv`) and **Cyrillic string literals by exact text**
per script file (`lang/ukrainian/strings/<script file name>.tsv`, `russian<TAB>translation`, empty translation allowed). Workflow that cannot mistype keys:
`tools/extract_strings.py <PROGRAM> --dump DIALOGS/X.c` (numbered, `¶` marks the end so trailing spaces are visible) -> write `index<TAB>translation` into
`lang/ukrainian/strings_work/X.uk.txt` -> `tools/build_strings.py <PROGRAM> DIALOGS/X.c <work file> lang/ukrainian/strings/X.tsv` (checks placeholders).
Russian grammar gender is built by script (`GetSexPhrase("","а")`: separate literals "хотел"+"а"); Ukrainian text must be written gender-neutral or the same
suffix literal must be mapped to an empty string. Totals: 478 dialog `.c` files with 6652 Cyrillic literals + 12 831 `DLG_TEXT` lines.
### Translation layers (priority high -> low) and coverage
1. per-script manual files `strings/<script>.tsv` (+ `<script>.h.tsv`) and the `DLG_TEXT` index files `dialogs/*.tsv`;
2. `strings/_common_extra.tsv` (address forms) and `_common.tsv` (140 hand-translated frequent strings, `tools/mt/frequent.py` + `build_common.py`);
3. `strings/_global.tsv` = translation memory of complete sentences from layer 1 (`tools/merge_global.py`);
4. `strings/_mt.tsv` = **machine translation draft** of everything else (`tools/mt/*`): NLLB-200 distilled 600M (CTranslate2 int8, CPU, model from
   huggingface.co/Code-Dev/nllb-200-distilled-600M-ct2-int8, licence CC-BY-NC-4.0, non-commercial) in an isolated venv `tools/mt/venv` + `tools/mt/model` (git-ignored, ~0.9 GB).
   Pipeline: `collect.py` (what is not covered) -> `translate.py` (sentence by sentence, punctuation/space/lower-case-start of fragments re-applied, `
` kept, strings with
   `%1`/`#tag#`/backslashes and tiny gender-suffix pieces skipped, outputs with Russian-only letters retried with 8-best) -> `apply.py` -> `_mt.tsv`. Three passes were run
   (full, retry of rejected, fragments translated by hand elsewhere). Run time ~25 min for 14.7k strings.
Coverage (`tools/coverage.py`): of 20 217 Cyrillic strings: manual/common/memory 13 %, machine 79 %, still Russian 7 % (mostly names in `Characters` that the model leaves as they are,
plus the skipped pieces). **Machine quality is "gist" level**: it keeps the meaning in most simple lines but drops or distorts some ("Умрите гады..." lost, "смахивает на правду" ->
"смакує на правду"), mixes ти/ви, and cannot do Russian gender-suffix constructions. First scene (Fort-de-France tavern) is hand-translated; everything else should be reviewed
progressively: a better model (e.g. NLLB 1.3B) or hand polishing of the most visible dialogs (stores, shipyard, governors, church, quests) is the next quality step.
Check any new translation file with `tools/check_lang_all.py lang/ukrainian`.

## Storm Script front end (`StormScript/`, `StormScript.Cli/`, C# / .NET 9)
Lexer + preprocessor (`#include`, `#define`, `#ifdef/#ifndef/#else/#endif`, `#event_handler`, `#libriary`) + parser + AST + walker.
Run: `dotnet run -c Release --project StormScript.Cli -- check|api|surface <PROGRAM dir> [out]`.

Language facts learned (all verified on the 750 shipped files):
- Variable/function names and keywords are **case-insensitive** (`String`, `iday` == `iDay`, `characterFromID` == `CharacterFromId`); `#define` names are case-sensitive.
- Strings have **no escapes**; a backslash is a literal character (paths). `#define NAME <tokens>` is plain substitution.
- Types: `int float string bool ref aref object void`. Objects are dynamic attribute trees: `a.b.c`, `a.(expr).x`, `a.1x0`.
- `call var(args)` = dynamic call by function name held in a string; `&x` takes a reference; `makeref/makearef` are functions.
- `string DLG_TEXT[50] = { ... };` array initialisers, `for(int i...)`, `switch` on strings/ints/defines, no ternary, no `goto`.
- The original compiler tolerates missing `;`, stray `)` and `, ;` - the parser does too (warnings only).

Results: **750/750 files parse** (740 clean, 10 with 15 recovered syntax errors, see `docs/source_defects.md`).
Scripts define 2505 functions. They call only **103 functions that are not defined in script** (`docs/engine_api.txt`,
25.7k call sites; ~45 of them matter: attributes, events, entities, layers, math) and talk to the engine through
`CreateEntity` (65 entity classes), `SendMessage` (208 distinct message ids, 109 format strings, 962 sites with computed ids)
and 252 named events (`docs/engine_surface.txt`). That list is the exact work-plan for the new runtime.

## Headless interpreter (`StormScript/Runtime`)
Tree-walking interpreter with the original VM semantics (`docs/vm_semantics.md`): attribute-tree objects, refs, events with
simulated time, segments, ~80 built-in functions, engine-class stubs. `run_newgame.ps1` boots the real game scripts:
`Main()` -> 3 logo videos -> main menu -> "New game" -> `InitGame()`.

Result of that run (`docs/run_newgame.txt`): **0 runtime errors, 0 unresolved functions/variables, ~1 s**; the game state is real:
968 characters, 128 items, 288 locations, 168 ship types, 27 goods, 8 islands, 9 stores.
What the scripts asked of the (still stubbed) engine during that run is listed in the report under
"engine messages received by stub entities" - this is the measured to-do list for the new engine.

## Godot port (`game/`, Godot 4.7.2 .NET in `godot/`)
`run_game.ps1` builds and starts it. The Godot project references the `StormScript` library, boots the **original scripts**
and implements engine classes on top of them. Textures are read straight from the original `.tx` files (`StormScript/Formats/TxDecoder.cs`).

Working now: `xinterface` (screens from `RESOURCE/INI/INTERFACES/*.ini`: PICTURE/SLIDEPICTURE/TEXTBUTTON nodes, strings,
selectable/focus, click -> script events) and `fader` (loading picture). Verified end to end with screenshots:
logo videos -> script `InitGame` -> real main menu (random background, rotating wheel, focus/disabled buttons) ->
click "New Game" -> scripts start the new game and show the original "loading" picture -> script calls `LoadLocation(Tutorial_Deck)`.
Test hooks: `-- --shot=<png without spaces> --shot-frame=N --click=NODE@FRAME` (see `game/Main.cs`).

Not implemented yet (the log prints `[xinterface] not implemented: ...`): node types VIDEO, RECTANGLE, BOUNDER, TITLE, FORMATEDTEXT,
GLOWS; original bitmap fonts (Godot's default font is used); language files (`GET_LANGUAGE_STRING` echoes the key);
**locations (3D)**, characters, sea, sound, saves.

### 3D locations (`game/Location.cs`, `ModelFactory.cs`, `Characters.cs`, `Fader.cs`)
New game now runs the original `LoadLocation` for `Tutorial_Deck` to the end: the engine class `location` loads the static
`.gm` models (`StormScript/Formats/GmFile.cs`), applies the baked per-vertex lighting from `*_<lightpath>.col` (x2 gain),
exposes the model labels as `locators.<group>.<name>.{radius,x,y,z,vx,vy,vz}` attributes (as the original does), accepts the
character patch (`.ptc`, only checked for existence so far). `NPCharacter`/`Player` are placeholder capsules with a name,
teleported to their locators. `fader` implements FADER_OUT/IN with `FaderEvent_StartFade/EndFade`.
Debug camera: WASD/QE move, right mouse look, Shift fast.

Bugs found in the interpreter on the way (fixed): dynamic attribute names may be dotted paths (`loc.(sat)` with "models.always.l1");
local variables are function-wide (the original uses `stmp` one line before declaring it).

Still missing for the first playable moment: character models + animation (`.an`), the `.ptc` navigation patch (walking, collisions),
the location camera, controls, lights, windows technique, ships/sea outside, sound, dialogs UI (FORMATEDTEXT/TITLE).

### Characters (`game/CharacterRig.cs`, `StormScript/Formats/AnFile.cs`, `PtcFile.cs`)
Skinned characters: body `MODELS\Characters\<model>.gm` + head `MODELS\Heads\<headModel>.gm` on the skeleton of `animation\<anim>.an`,
actions (idle_2, walk, run, ...) from `<anim>.ani`; the original `SETMODEL` message returns 0 if a model or animation is missing.
Format facts established by measurement (not documented anywhere): `.an` = i32 frames, i32 joints, f32 fps, parent[], local joint
offsets, root position per frame, then a quaternion per joint per frame (joint-major); quaternions act on coordinates like
ordinary ones (reflecting z gives (-x,-y,z,w)); **the meshes are modelled in the pose of frame 0** (a zero-rotation skeleton is a T-pose), so rest = frame 0.
Vertex type 4: weight `w` belongs to the bone in the low byte of `boneid`, `1-w` to the next byte.
Characters are snapped to the nearest point of the location's walkable patch `.ptc` ("PTC " header, 16-byte triangles with neighbour links).

Not done: hats/hair, equipped weapons (labels give attachment bones), walking/turning/fight actions driven by the scripts (`MoveChr`, `30373 ...`),
facial animation, lighting of characters (unshaded for now), the location camera, controls.

## Next
1. `.an` animation + `.col`/.ptc/.grs/.zap formats, skinning for type-4 meshes, old `.gm` versions.
2. (done) Storm Script parser and headless interpreter. Next: implement engine classes for real, starting with the ones scripts hit most (see report): characters (`MoveChr`, `LockFightMode`, ...), locations, xinterface.
3. (done) Godot boot, main menu, first 3D location with locators and placeholder characters. Next: the interface atlas + node types for the hero-selection screen (real game start), then character collisions/sitting, items, sea.
4. Sea, battles, world map, ...

Reference for formats: storm-devs/storm-engine (GPLv3, archived) `src/libs/geometry/src/rdf.h`.
`data1.dat` (6.5 MB, looks encrypted) is not needed: resources live unpacked in `../RESOURCE`.
