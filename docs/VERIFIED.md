# Verification of guessed engine behaviour against the original source

_2026-11-05. Everything earlier agents had to guess without C++ was compared with `reference/storm-engine/src/libs/*` (the open-sourced Storm
Engine, a later version of the PotC engine). The PotC scripts stay the specification: where the reference uses another message/event format than
the PotC scripts expect, the PotC contract was kept. Status: **matches** (the port already did it), **fixed** (changed in this pass),
**different** (intentionally kept, with the reason), **open** (not done)._

| # | Item | Original source file : function | Port before | Port after | Status |
|---|---|---|---|---|---|
| 8 | `Shp_NotEnoughBalls` argument | `sea_ai/src/ai_ship_cannon_controller.cpp`: `Execute` | raised with no argument, only when a broadside fired nothing | every frame for the main character, `"l"` = some bort has more intact cannons than `Shp_GetCurBallsNum` (`SeaCombat.cs` `TickNotEnoughBalls`) | fixed |
| 8 | Other mismatch rows of `CONTRACTS.md`: `GetAttributeName` 2 args | `core/src/internal_functions.cpp` | reads 1 arg | unchanged | different: the only 2-arg call (`ITEMS/itemLogic.c:664`) is dead code (the loop runs over an emptied aref) |
| 8 | `GetEntity/GetEntityNext/GetEntityName` | `core` (layer enumeration) | stubs returning 0 / "" | unchanged | open: needs layer membership tracking + the `scrshoter` class (save thumbnails) |
| 5 | `battle_interface` messages 46002-46014 | `battle_interface/src/sea/i_battle.cpp`: `ProcessMessage` | no handler (`[nohandler]` 46013 187x, 46005, 46003 ...) | `BattleInterface.cs`: ship list, island places, textures, visible flag, not-enough-balls blink, command mode | fixed |
| 5 | `BI_IN_SET_COMMAND_MODE` 46013, `BI_MSG_SET_MSG_ICONS` 46006 | `i_battle.cpp` | — | stored / accepted | matches (the reference ignores both too) |
| 5 | `BI_SetSeaState` | `i_battle.cpp`: `CheckSeaState` (every frame) | every second; map = not in a mooring zone; attack/defend = ship within 2500 m; reload never | every frame from the ship list: map = no living enemy within `freeDistance`, attack = living enemy ship or enemy fort, defend = one of my ships, reload = transferable ship within `boardRadius` (nearest = target) | fixed |
| 5 | `BI_CallUpdateShip`, `BI_GetData`, `BI_GetFortRelation`, `evntGetLandData` | `ships_list.cpp` `Refresh/Add`, `island_descr.cpp` | not raised | raised on REFRESH / CREATE_SHIP / SET_ISLAND | fixed |
| 5 | `BI_VISIBLE` | `spyglass/spyglass.cpp` | not raised | raised with the telescope on/off; the script's `BI_CommandVisible` hides the HUD through `BI_SET_VISIBLE` | fixed |
| 5 | `evntSetUsingAbility`, `BI_GetChargeQuantity`, `evntCheckEnableShip`, `evntCheckEnableLocator` | `battle_ship_command.cpp` | not raised (charge counts read from the cargo) | raised by the command menu (`SeaCommands.cs`) | fixed |
| 5 | Sea HUD layout | `battle_interface` + `INI/battle_interface.ini` | own layout | unchanged | different: the reference layout is a later version (TEHO) of the ini; not verified |
| 1 | Speed law | `ship/src/ship.cpp`: `ApplyStrength`, `CalculateNewSpeedVector`; `rigging/src/sail.cpp` `MSG_SAIL_GET_SPEED` | target = MaxSpeedZ x sail state (0 / 0.5 / 1) | target = MaxSpeedZ x the sails' share (see Sails); inertia law matched already (fMassInertia 0.1, accel/brake of the type) | fixed |
| 1 | `KNOTS2METERS` | `math/include/math_inlines.h` | 0.514444 | 1853 / 3600 | fixed |
| 1 | Frame time, move order | `ship.cpp`: `Execute` (min 0.1 s), `Move` | unclamped; heading turned before the position moved | clamped; position along the old heading, then the turn | fixed |
| 1 | Sideways speed | `ship.cpp`: `ApplyStrength` (x axis) | invented slide = turn rate x speed | only impulses give it, braked by `InertiaBrakingX` | fixed |
| 1 | `Ship.Impulse`, `MSG_SHIP_ADD_MOVE_IMPULSE` 50308 | `ship.cpp`: `Execute`, `ProcessMessage` | ignored | applied once (speed turned into the ship frame) | fixed (the reference also reads Speed from the Rotate attribute, a bug not copied) |
| 1 | Rocking | `ship.cpp`: `ShipRocking` | 4 samples, own gains, heave x 0.9 | 6 x 6 grid over the hull box, mean height, mean slopes of 3 row/column pairs, smoothing `min(dt/0.025 x Rocking.y/az, 1)` | fixed |
| 1 | Heel | `ship.cpp`: `Execute` (`vAng.z += clamp(vRotate.y, ±0.25)`) | turn rate x speed factor + invented wind heel | clamped yaw rate only | fixed |
| 1 | Ship-ship collision | `touch/src/touch.cpp`: `Touch`, `IsIntersectShipsReal`, `ProcessImpulse` | power = speed difference, slide 0.4, speed x 0.6 | closing speed along the normal (knots), slide = sine to the normal, split by type `Weight`, `ProcessImpulse` pushes | fixed (contours replaced by 3 circles per hull: different) |
| 1 | Ship-island collision | `touch.cpp` (island branch) | power = speed / 2, slide 0.3 | power = hull speed, slide 0 | fixed; the stop-at-the-shore rule instead of recoil iterations is different (robust against the raycast island) |
| 2 | Half sail | `sail.cpp`: `AddSail` (rollType), `MSG_SAIL_GET_SPEED` | square sails reefed half way | `ROLLTYPE_FAST` sails (yards `rey_a*`, all yards of mast1) furled, the rest full | fixed |
| 2 | Rolling speed | `sail.cpp` (`ROLLINGSPEED` x `evntGetSRollSpeed`) | ROLLINGSPEED only | x the script's crew/skill factor | fixed |
| 2 | Sail speed share | `sailone.cpp`: `CalculateSailWind`, `GetSailSpeed` | — | area x holes x (min + x xdep + z zdep) of Treangle/Trapecidal/SquareWindSpeed by the wind in the yard frame x unrolled part; sum / total area; `Sail.MinSpeed` | fixed |
| 2 | Yard turning | `sailone.cpp`: `TurnSail`, `sail.cpp` (`TURNSTEPANGL`) | clamp(relative wind angle, MAXTURNANGL), 0.3 rad/s | MAXTURNANGL x side wind in the turned yard frame, 2 rad/s, 0.01 dead band | fixed |
| 2 | Sail cloth / emblem | `sail.cpp`: `SetSailTextures` + PotC `procGetSailTextureData` | nation texture on every square sail | `normalTex` everywhere, nation emblem only on the `GeraldSails` yards | fixed |
| 2 | Flag tile | `flag.cpp` (`GetRiggingData`) | own nation table | the script's `GetFlagTexNum` | fixed |
| 2 | Wind bulge, holes, ropes, vants, flag waving | `sailone.cpp` `goWave`, `rope.cpp`, `vant.cpp`, `flag.cpp` | shader approximations | unchanged | open (visual only) |
| 2 | Mast fall | `mast/src/mast.cpp`: `Mount`, `Execute` + `INI/mast.ini` | gravity-like tipping, random side, sinks accelerating, 16 s | constant rate `(fMinZdang + rand fVarZdang) x 10` rad/s, away from the nearest ship (< 63 m), drifts with the ship's velocity, sinks at `fDeepStep` to `fMaxDeep` | fixed (collision sliding of the falling mast: open) |
| 3 | Particles (old `INI/PARTICLES` format) | `particles/src` is the later `.xps` system; `k2_wrapper/particles.cpp` = PS_* messages | — | unchanged | matches for the messages (angles, lifetime in s); the ini particle physics cannot be verified (no source for PARTICLES.dll) |
| 3 | Ball flight | `sea_ai/src/ai_balls.cpp`: `Execute`, `AddBall` | real time; 0.8 m sphere | time x `AIBalls.SpeedMultiply` (3 in AIBalls.c); radius = ball `Size` 0.2-0.3 m | fixed |
| 3 | Broadside | `ai_ship_cannon_controller.cpp`: `Fire2Position`, `Fire(AIShip*)`; `ai_cannon.cpp`: `Fire`, `RealFire`, `CalcHeightFireAngle`, `CalcMaxFireDistance` | 0-0.6 s random delay, flat-ground elevation, no spread event, Ship_Bort_Fire with zeros | `ShpGetBortFireDelta` spread, `Ship_Bort_Fire` with aim point/direction, `CannGFTime` per cannon, height-aware elevation, range; AI leads (knots -> m/s) and aims at `CannGFHeight` | fixed; reload per bort instead of per cannon: different (same ammunition) |
| 3 | Forts | `sea_ai/src/ai_fort.cpp`: `Execute` | 0.5 s look, range v0²/g, half reload + random | 1 s timer, `CalcMaxFireDistance(h, v0, 0.35)`, `CannGFTime` delay, height angle, full `CannGRTime` | fixed |
| 4 | NPC fight distances | `location/src/np_character.cpp` (`NPC_FIGHT_*`) | stay 1.9 / go 2.3, run beyond 6 m | stay 2.0 / go 2.4, run > 4 m, walk < 2.5 m | fixed |
| 4 | NPC attack/defence decisions, block time, parry | `np_character.cpp`: `FightTick`, `DoFightBlock` | per-second rates of the script | unchanged | different: the reference AI is a later energy-based one (0.2 s ticks, attack/defence weight events PotC does not have); the PotC scripts document their values as per-second rates |
| 4 | Land sounds timing | `.ani` events | from the animation events | unchanged | matches |
| 6 | World map storms | `worldmap/src/wdm_storm.cpp`: `WdmStorm()`, `Update`, `CheckIntersection` | random heading, speed 2.5-4.5, 3 s fade, instant removal, distance alpha floor 0.25 | heads for the ship ± 0.6, speed 5-8, liveAlpha +0.1/-0.3 per s, fade-out on life end / `stormDistKill`, alpha 0 at `stormViewDistMax`, active after 2 s | fixed (steering by the later wind field: not in PotC, not copied) |
| 6 | Encounter ships, map camera, player speed | `wdm_ship.cpp`, `wdm_enemy_ship.cpp`, `wdm_camera.cpp` | own speeds relative to the ship rate | unchanged | different/open: the reference world map is a later version (other create messages, wind field); PotC values unknown |
| 7 | Light flicker | `location/src/lights.cpp`: `Execute` | colour x (1 - fast[0..f] - slow[0..fs]) — only darker | colour x (1 + fast[-f..f] + slow[-fs..fs], interpolated), ≥ 0 | fixed |
| 7 | Light ini defaults | `lights.cpp`: `Init` | att0 1 / att2 0, corona range/size 0 | att0 0 / att2 1, coronaRange 20, coronaSize 1, flicker/freq consistency | fixed |
| 7 | Coronas | `lights.cpp`: `Realize` | alpha 1 - d/range | 0.2..1 ramp over 30 % of the range, squared falloff after 40 %, 8/s visibility fade, ±10 % size/alpha by the flicker | fixed (occlusion trace: open) |
| 7 | Shadows | `shadow/src/shadow.cpp` | blob under characters | unchanged | open |
| 3 | Ship fires (`ship_smoke`/`ship_fire`) | `ship/src/fire_place.cpp` | per-place systems for the script's time | unchanged | matches the script contract (ShpActFirePlace); particle physics unverifiable |
| 5 | Land HUD layout (`battle_land_interface`) | `battle_interface/src/land/*` | positions from `BLI_SetShowParameters` | unchanged | open: reference is the later TEHO layout |
| 2 | Rope width/sag, vants | `rigging/src/rope.cpp`, `vant.cpp` | own geometry | unchanged | open (visual only) |

## Test-suite notes
Ships now fire as the original (spread, delays, ball speed x3), so the -Sea scenarios fight / neutral / board give the player's ship 9000 HP through
`SEADOGS_PLAYERHP` (Sea.cs `AddShip`), and the `--fire` hook aims without the bort spread and delay.
