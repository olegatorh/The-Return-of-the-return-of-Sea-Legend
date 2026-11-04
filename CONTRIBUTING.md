# Contributing

This project re-implements the **Storm Engine** (Akella, 2003) in Godot 4 / C# so that *Pirates of the Caribbean / Sea Dogs 2* with the addon
*Return of the Sea Legend* (v1.06) runs on a modern engine. The original game scripts are interpreted unchanged; only the engine is rewritten.

## What this repository does NOT contain
No game files. You need your own copy of the game (base v1.03 + the addon): this repository is expected to sit **inside** the game folder as `port/`
(`<game folder>/port`). Never commit game scripts, models, textures, sounds or the unpacked addon (`addon/` is git-ignored). Pull requests that add them will be closed.

## Getting started
1. Read `CLAUDE.md` (entry point), `docs/ARCHITECTURE.md`, `docs/STATUS.md` (what is done and how accurate it is), `docs/ROADMAP.md` (what to do).
2. Build the merged script tree once: `tools/unpack_stormex2.py`, `tools/merge_addon.py` (see README.md). Install Godot 4.7 mono into `godot/` (git-ignored).
3. Run: `powershell -ExecutionPolicy Bypass -File run_game.ps1`, tests: `powershell -ExecutionPolicy Bypass -File run_tests.ps1 [-Smoke] [-Intro] [-Sea]`.

## Rules
* **Never modify the original scripts.** If the engine must behave differently, change the engine (or add a `DefineOverrides` entry, see `settings.ini`).
* Every interpreter bug fix needs a unit test (`StormScript.Tests/InterpreterTests.cs`).
* Run `run_tests.ps1` before opening a pull request; add or update a scenario when you add an engine feature.
* When a subsystem changes stage, update its row in `docs/status_data.txt` and run `python tools/status.py` (commit `docs/STATUS.md` too).
* Keep pull requests small and focused; English commit messages; code comments in English; UI strings in Ukrainian.
* Do not write C# or Python containing backslashes through shell heredocs; write files with your editor (backslash mangling caused many silent bugs).

## Good first areas
See `docs/ROADMAP.md`: store/trade screens, original sea HUD, storms, telescope/cabin cameras, land combat blocks and pistols, translation polishing (`lang/ukrainian`).
