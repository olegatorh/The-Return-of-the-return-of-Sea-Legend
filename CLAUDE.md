# CLAUDE.md — start here (for AI assistants and new contributors)

## What this project is
A **re-implementation of the Storm Engine** (Akella, 2003) in **Godot 4 (C#)**, so that the old game *Pirates of the Caribbean / Sea Dogs 2* with the addon
**"Return of the Sea Legend" (Возвращение морской легенды, v1.06)** runs on a modern, readable engine. The approach is OpenMW-style:
**the original game scripts (Storm Script, `PROGRAM/*.c`) are kept unchanged and interpreted**; only the engine (the C++ part) is rewritten.
The game must be playable **in Ukrainian** (translation layer, see below). The owner speaks Ukrainian; answer in Ukrainian unless asked otherwise.

## Hard rules
1. **Never modify the original game files** (everything one level above this folder: `PROGRAM`, `RESOURCE`, `stormex2.dll`, ...). All work lives in `port/`.
2. **Ask before downloading anything** (name, source, size). Pip packages/models were approved once for the translation tools; nothing else.
3. The addon's scripts are **inside `stormex2.dll`** (zlib overlay). `port/addon/merged/PROGRAM` = base `PROGRAM` overlaid with the unpacked addon files; Godot runs
   this merged tree (git-ignored, regenerate with `tools/unpack_stormex2.py` + `tools/merge_addon.py`).
4. `#define` names are **case-sensitive**, variable/function/keyword names are case-insensitive. Do not "fix" this (see `docs/vm_semantics.md`).
5. Windows + Git Bash/PowerShell. **Do not write C#/Python with backslashes through shell heredocs** (they get mangled): use the Write/Edit tools, or write a
   script file first. Verify every patch applied (many silent failures happened).
6. Commit style: small commits, English messages, trailer `Co-Authored-By: ...` as instructed by the harness.

## Layout (all paths relative to `port/`)
| Path | What |
|---|---|
| `StormScript/` | C# library: lexer, preprocessor, parser, **interpreter** (`Runtime/`), file-format readers (`Formats/`: `.tx` textures, `.gm` geometry, `.an/.ani` animation, `.ptc` walk patches) |
| `StormScript.Cli/` | headless runner (`ssc check/api/surface/run`) used for parser checks and boot tests |
| `game/` | the **Godot project** (engine classes implemented as Godot nodes). Entry: `Main.cs` |
| `lang/ukrainian/` | translation layers (UI texts, dialogs, string literals) |
| `tools/` | Python tools: converters, translation pipeline (`tools/mt/` machine translation), name/place polishing |
| `docs/` | engine API notes, VM semantics, source defects, run reports |
| `addon/` (ignored) | unpacked addon + merged script tree |
| `saves/` (ignored) | save games |
| `godot/` (ignored) | the Godot 4.7.2 mono editor/runtime (approved download) |
| `README.md` | long status log with all discoveries; `ЯК_ГРАТИ.md` — player guide (Ukrainian) |

Read **`docs/ARCHITECTURE.md`** next: how script ↔ engine communication works and where each engine class lives.

## Build / run / test
```powershell
./run_game.ps1                      # builds game/SeaDogs.csproj and starts Godot with the project
```
Godot executable: `godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64.exe --path game -- <user args>`.
Build only: `dotnet build game/SeaDogs.csproj -nologo -v q` (must print `0 Error(s)`).

**Automated regression tests — run these BEFORE and AFTER every change (do not rely on playing by hand):**
```powershell
powershell -ExecutionPolicy Bypass -File run_tests.ps1           # unit tests + game build (seconds)
powershell -ExecutionPolicy Bypass -File run_tests.ps1 -Smoke    # + Godot main-menu run, fails on [errors]/[unresolved]/exceptions
powershell -ExecutionPolicy Bypass -File run_tests.ps1 -Intro    # + intro into the first location (~3 min)
```
`StormScript.Tests/` (xunit, works offline from the local NuGet cache): `InterpreterTests` (VM semantics, one tiny script per rule —
**add a test for every interpreter bug you fix**), `ScriptCorpusTests` (all 1139 original scripts parse, ≤12 known defects; the game boots headless with
0 runtime errors / 0 unresolved functions), `FormatTests` (.ptc/.gm readers, path finding), `LanguageTests` (translation overlay).
Tests needing game data return early if `addon/merged/PROGRAM` or the game folder is missing. `GroupsState`/`Characters` live in the Godot project and
are only covered by the smoke runs.

**Manual test hooks** (Godot *user args* after `--`; frames are counted from start; the game window runs at ~60 fps, 1280x960):
| Arg | Effect |
|---|---|
| `--click=NODE@N` | click interface node at frame N (e.g. `B_NEWGAME`, `RUNGAME`) |
| `--answer=1@N` | choose dialog answer 1 at frame N |
| `--ctl=Name@N` | send a control to the script (`Quick_Save`, `Quick_Load`, `Interface`, `MainMenuDouble`, `ChrAction`) |
| `--goto=group.locator@N` | teleport the player to a locator (e.g. `reload.reload3_back`) |
| `--call=Func(arg,..)@N` | call a script function; ints, strings (`+` = space), `#CharacterId` |
| `--set=var=value@N`, `--dump=Global[i].path@N` | set / print script variables |
| `--dialog=Id_with_underscores@N` | start a dialog with a character |
| `--fuzz@N` | talk to everyone in the location, click the first answer; prints missing engine functions |
| `--shot=path.png --shot-frame=N` | save a screenshot (path **without spaces**) and print diagnostics, then quit |
| `--camdist=N`, `--move=X,Y@A-B`, `--control@N` | camera distance, hold a move direction, give control to the player |

Env vars: `SEADOGS_TRACE=Func1,Func2` (log script calls), `SEADOGS_MATLOG`, `SEADOGS_DETECTLOG`, `SEADOGS_RIGLOG`, `SEADOGS_SOUNDLOG`.
At screenshot time the log lists: `[errors]` (runtime errors), `[unresolved]` (engine functions the scripts call that do not exist), `[events]`, `[nohandler]`
(SendMessage to engine classes without a handler), `[chars]`, entities alive. **This is the main debugging instrument.**

**Fast loop**: play to a point once, save with `--ctl=Quick_Save@N`, then start tests with `--ctl=Quick_Load@200` (seconds instead of a minute).
Intro reference run (from the main menu to the first location, ~9000 frames):
`--click=B_NEWGAME@200 --click=RUNGAME@400 --answer=1@2700 --answer=1@2800 ... (13 answers, every 100 frames)`.

## Working method that paid off
* Scripts are the specification. To implement an engine class, grep the scripts for `SendMessage(&Obj, "fmt", MSG_...)`, the `#define`d message numbers
  (`messages.h`) and the events the engine must raise (`GetEventData()` order in the handlers). Never guess a contract before reading its script users.
* Run, read `[unresolved]`/`[nohandler]`/`[errors]`, fix the top entry, repeat. Compare with screenshots (Read the PNG).
* Silent semantic bugs in the interpreter were the costliest (see "Pitfalls" in `docs/ARCHITECTURE.md`). When a script flow "does nothing", trace it with
  `SEADOGS_TRACE` before suspecting the engine class.

## Status (see README.md for details)
Works: boot, menu, hero selection, real intro, locations (3D, lighting, sky/sea/fog), characters + AI tasks + sitting poses, dialogs, locator detection and
reloads, game clock, sound, save/load (`OnSave/OnLoad`), interfaces: character, items, quest book, options (sound), shipyard (empty), Ukrainian layer.
Basic melee combat works (groups, NPC attacks, death/game over; player: F draws the sword, LMB attacks).
Missing (largest first): **sea/ships/world map** (Ship, Island, Sail, sea AI, battle interface), blocks/pistols/weapon models, trade/store flow,
items/chests on the ground (code exists, untested), key-binding screen, save thumbnails, mouse look, polishing of the machine translation.
