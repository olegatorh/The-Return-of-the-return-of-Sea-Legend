# AGENTS.md

The single source of agent instructions for this repository is [CLAUDE.md](CLAUDE.md). Read it first and follow it exactly;
this file only points there so that every agent (Codex, Claude, others) gets the same rules. Edit the rules in `CLAUDE.md`, not here.

Then read `docs/HANDOFF.md` (where work stopped), `docs/ARCHITECTURE.md` and `docs/STATUS.md`.

Code navigation: query the local graph before grepping (`graphify query "<question>" --budget 1500`, `graphify path`, `graphify explain`;
overview `graphify-out/GRAPH_REPORT.md`), and run `graphify update .` after code changes. Details: `CLAUDE.md`, "First steps of every session".
