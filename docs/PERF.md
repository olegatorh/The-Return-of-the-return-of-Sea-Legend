# Performance notes

Measured on the development PC (Windows 11, hardware GPU), window 1280x720, `--disable-vsync --fixed-fps 60` (game time is deterministic, the printed fps is the real
throughput), env `SEADOGS_PROF=1`. Scenario = the `fight` recipe of `run_tests.ps1` (player + 3 French ships of the Martinique squadron, island, fort in view).
Do not use vsync-on numbers: they only show the monitor rate (144 Hz here).

## Instruments
* `SEADOGS_PROF=1`: every 120 frames `[prof] per frame ms: world / advance / frameEvent / fps / draw calls / primitives / objects`, `[prof] sea ms: <section> ...`
  (ships, collisions, ai, guns, combat, fires, forts, sailors, sails, fx, flags, camera, hud; sections under 0.02 ms are not printed), `[prof] frame N: ...` for frames that
  took > 25 ms or changed the Godot object count by > 3000 (a GC burst), `[load] model ...`, `[load] texture ...`, `[load] prefetched N textures`, and at screenshot time
  `[load] ModelFactory.Build total ...`.
* `SEADOGS_NOPREFETCH=1` turns the parallel texture decoding off, `SEADOGS_OLDRAY=1` restores one new ray query object per land test (for comparisons).

## Numbers (before -> after)
| What | Before | After |
|---|---|---|
| Sea, 4 ships, script+sea CPU per frame (`world`) | 0.9-2.9 ms | 0.7 ms (sea tick `ships` 0.9 -> 0.3 ms) |
| Sea, 4 ships, unlimited fps (216k primitives, 140 draw calls) | 340 fps | 358-372 fps (2.7 ms/frame) |
| Godot object count while sailing | 7k <-> 50k (RefCounted garbage, collected in 6-frame bursts costing 1.5-6 ms) | stable 4-5k |
| Town port (QCport), unlimited fps | - | 166 fps (203k primitives, 34 draw calls) |
| World map, unlimited fps | - | 408 fps (0.3 ms script) |
| Texture work on the main thread (sea run: port, map, island, ships) | 1387 ms | 617 ms |
| `ModelFactory.Build` total (156 models) | 1152 ms | 944 ms |
| Port location models (6) | 311 ms | 255 ms |

Cause of the sea garbage: `LandHeight` created a `PhysicsRayQueryParameters3D` per ray; ships test 4 corners per frame and the AI looks ahead with up to 9 headings x 2 points
x 4 corners every 0.35 s. One reusable query object removes it.

## What was changed
* `Sea.cs` `LandHeight`: one query object. `Main.cs` / `Sea.cs`: profiling output.
* `GameAssets.Prefetch` (called by `ModelFactory.Build` for the materials of the model): file read, `.tx` decode and mip generation of the missing textures on all cores
  (`Parallel.ForEach`, only `Image` objects are created off the main thread); `Texture()` only uploads them. `TxDecoder` hot loops are `AggressiveOptimization`.
* Not done / ideas: a disk cache of decoded textures (the 2048x2048 `.tx` files take 55 ms decode + 25 ms mips each, the world map loads four); LOD / instancing of ropes
  (the draw call count is already 140 for four ships); interpreter hot paths are below 0.1 ms per frame (`advance`), `Shp_ChkSituation` per ship and second does not show up
  (`ai` section < 0.02 ms), so nothing was changed there.
