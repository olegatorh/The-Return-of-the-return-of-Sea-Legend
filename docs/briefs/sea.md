# Brief: SEA (ships, combat, AI, weather, sea camera/HUD)

Read this instead of the big docs; open `docs/VERIFIED.md`/`AUDIT.md` only for the rows of your topic (grep the topic, don't read whole).

## Where the code is (`game/`)
* `Sea.cs` (SeaView: entities Island/sea_ai/ship, `Tick`, `Steer`, `OnSeaAi` messages 51xxx), `SeaPhysics.cs` (rocking, wake, ship-ship collisions), `SeaGrounding.cs` (hull vs island, `SailToPoint`),
  `SeaGuns.cs` (per-cannon loading), `SeaCombat.cs` (salvos, balls), `SeaForts.cs`, `SeaTactics.cs` (AI attack/flee), `SeaAI.cs` (relations, groups, events `Shp_ChkSituation`/`GrpChkTsk`).
* Look: `SeaSails.cs` (rolling, holes, mast fall), `RiggingFx.cs` (ropes, vants, flags), `SeaFx.cs` (foam, sink vortex, floating goods), `SeaFires.cs`, `Particles.cs` (INI/PARTICLES), `SeaSharks.cs`, `SeaBlots.cs`, `SeaSplash.cs`, `SeaSailors.cs`, `SeaLocShips.cs`.
* Camera/HUD: `SeaShipCamera.cs`, `SeaDeckCamera.cs`, `SeaTelescope.cs`, `SeaHud.cs`, `SeaNavigator.cs`, `SeaMiniMap.cs`, `SeaCommands.cs`, `BattleInterface.cs` (class `battle_interface`).
* Weather: `WeatherFx.cs` (rain, lightning, tornado), `WeatherSun.cs`, `OutdoorEnv.cs` (sky, sea shader, fog), `SeaWaves.cs`.

## Contracts to remember
* Speeds are knots (`KNOTS2METERS` 1853/3600); inertia per `ship.cpp` (`InertiaAcceleration/Braking X/Y/Z`, fMassInertia 0.1); the script computes `MaxSpeedZ/Y`.
* The script decides AI (tasks AITASK_*, `Shp_ChkSituation` every second, relations via `UpdateRelations` 51016); the engine executes.
* Foreign ship name/hull/crew/flag/distance are shown ONLY through the telescope (ispyglass); by eye there is only the arrow of the ship chosen in the command menu (`shippointer`).
* Sea camera keeps the angle the player left (no auto-centering). Telescope works from the deck camera only (`MSG_TELESCOPE_REQUEST`, script decides).
* Aim circle (`SeaAim.cs`, AIShipCameraController): shown ONLY in the deck camera (script `Crosshair.OutsideCamera`), hidden in ship/free camera, telescope, command menu; only when a side can fire along the view (FireZone, FireAngMin/Max); aim point = ship + CalcMaxFireDistance(camera elevation) along the 3D view; ship under it if within 0.8*length of (centre + 0.8*length away from us); colour enemy red / friend green / neutral grey / white, size pulses 0.9..1.1. Log `[aim]`, scenario `aimcircle`, env `SEADOGS_AIMLOG=1`.
* Engine x,z = sea plane; Godot z = −engine z; forward = (sin ay, cos ay).

## Test recipe and hooks (details: `docs/TESTING.md`)
`--ctl=Quick_Load@200 --call=initTutMainCharacterShip()@300 --call=SetPlayerShipLocation(184)@320 --goto=reload.reload1@400 --ctl=ChrAction@450` then `--sail=+1@700`, `--duel@N`, `--fire@N`,
`--shipfire`, `--mastfall`, `--sailholes`, `--sharks`, `--blot`, `--splash`, env `SEADOGS_SEACAM=dist,pitch,yaw`, `SEADOGS_SCOPE=1`. Scenarios: `-Sea` (sea, map, fight, neutral, board, sailto, seacmds…), `-Cameras`, `-Rules`, `-Stages3`.

## Open (see `docs/PROGRESS.md`, `docs/HANDOFF.md`)
Sea HUD/command menu details (submenus, hints), windward tactics, deck-camera wall sliding, sun-glow occlusion, windward foam/rain splashes, ship persistence between map and sea.
* Water: `settings.ini [game] WaterShader = boujie | classic` (default boujie: `game/water/boujie/sea_boujie.gdshader` after the MIT Boujie Water Shader: our harmonics as Gerstner waves, foam texture crest foam, shore foam + depth fog + refraction from screen/depth textures; classic = the old shader in `SeaSurface.Shader`; env `SEADOGS_WATER=classic`, `SEADOGS_NOSCREEN=1`, `SEADOGS_WATERDBG=1`).
