# Where we stopped (handoff, 2026-11-06)

> **Update 2026-11-11:** the controls hooks/docs listed below as "uncommitted" (`--mousemove`, `--btn`) are in git now (commit 6fa8753: also the tests draw the
> sword with E, the original ChrFightMode key, instead of F — the probable cause of the failing `melee` / `meleehero` / `drawrun`), so next step 2 is done.
> The four failing scenarios (step 1) are being re-run after the patch `sea-legend-changes.patch` was imported (Characters/Groups refactor into unit-tested `CharacterMath` / `GroupRelations`, +272 polished Ukrainian lines, docs split: `docs/TESTING.md`, `AGENTS.md`).

The owner paused all agents ("постав на паузу, я потім вернусь"). Everything below is in git (`main`, not pushed) unless it says "uncommitted".

## Stopped in the middle (resume these first)
| Task | Agent (resume with SendMessage) | State |
|---|---|---|
| Controls / mouse look / key binding screen vs the original (`docs/CONTROLS.md`) | `a813674848548a188` | real control service + key binding screen committed (a047125, 292d3fb, 6a3cd20, 6ac7420). **Uncommitted** in the tree: `CLAUDE.md`, `game/Main.cs`, `run_tests.ps1`, `ЯК_ГРАТИ.md` (the `--mousemove` / `--btn` test hooks and docs). It was adding a hook to hold a real mouse button. Build is 0 errors. Not yet run: the full suite for this batch |
| Sea battle interface + command menu vs the original | `a4bb709f669be5628` | committed: command bar, squadron row, fast-command icon (ae6d3fb, 9620ee7), minimap island picture + fire-zone fans (a55ee7e), scenario `seacmds`, VERIFIED rows (9da4ea4). It was waiting for the full suite to finish |
| Camera/telescope/deck/free camera/sailors | done (`ad7f40407e6737893`) | 4e460c5, 1cb80bd, 6417098, 1aa5e5f. The agent's last full suite did not finish and four scenarios failed there (`mapmerchant`, `mapencounter`, `melee`, `meleehero`) — cause not examined; re-run them: `run_tests.ps1 -Smoke -Sea` |

## Next steps (suggested order)
1. Run `powershell -ExecutionPolicy Bypass -File run_tests.ps1 -Smoke -Sea -Contracts` once; look at the four scenarios above (flaky or broken by the camera/controls/HUD changes?).
2. Commit the uncommitted controls hooks/docs (`git add CLAUDE.md game/Main.cs run_tests.ps1 "ЯК_ГРАТИ.md"` after reviewing the diff).
3. Owner plays by hand and sends F8 reports (`reports/`): feel of ship speed/turning, melee (strike speed, enemy defence), dialogs facing, healing keys (C), store, map traffic, the new sea HUD (no name/flag/distance without telescope), camera (no auto-centering).
4. Still open: videos (9 `.wmv`, needs a converter = a download, ask the owner), quests beyond the first steps (English line steps 2–13, pirate line, Necronomicon, Black Pearl duel), ~9.9k unproofread machine-translated fragments (`lang/ukrainian/strings/_mt.tsv`), female-hero gender branches, weak spots (`docs/PROGRESS.md`): controls/cameras 60%, sea HUD 60%.
5. Status docs: `docs/STATUS.md` (76% mean) and `docs/PROGRESS.md` (weighted ~78%); contract report `docs/CONTRACTS.md`; verification tables `docs/VERIFIED.md`, `docs/RULES_CHECK.md`, `docs/PLAYTEST.md`, `docs/PERF.md`, `docs/AUDIT.md`.

## Owner's standing rules
* Answer in Ukrainian. Test windows on screen: `--position 320,180 --resolution 1280x720` (middle of the primary monitor); `run_tests.ps1 -Hidden` for off-screen.
* Do NOT run the full regression suite after every small change (build + one targeted check); the full suite once per batch.
* Ask before downloading anything. Never modify files outside `port/`. The owner pushes git himself.
* Original C++ for reference: `reference/storm-engine` (git-ignored).
