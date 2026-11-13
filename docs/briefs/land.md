# Brief: LAND (locations, characters, melee, dialogs, items, camera, lights)

Read this instead of the big docs; grep `docs/VERIFIED.md` rows L1–L27 only when you touch combat/physics.

## Where the code is (`game/`)
* Locations: `Location.cs` (view, locators, reloads), `ModelFactory.cs`, `GameAssets.cs` (textures/mipmaps, `Prefetch`), `LocationLights.cs` (ADD_LIGHT, per-character lighting), `CharShadow.cs`, `LocAnimals.cs`/`Animals.cs`, `LandFlags.cs`, `OutdoorEnv.cs`, `Grass.cs` (the engine's Grass entity: models.always.grassPatch -> *.grs density map, 4x4 atlas Textures\Grass\grass.tga; static chunk meshes + a shader that repeats grass.fx and the wind/LOD/character push of grass.cpp; light = LocationLights ambient+sun; log `[grass]`, scenarios grassjungle/grassindoor).
* Characters: `Characters.cs` (CharacterView: tasks, fight AI, strikes, blocks), `CharacterRig.cs` (skinned model + .ani playback), `CharacterJump.cs`, `Groups.cs` (+ `StormScript/Runtime/GroupRelations.cs`, `CharacterMath.cs`), `HitFx.cs`.
* Camera/input: `LandCamera.cs` (camera_follow.cpp), `Controls.cs` + `KeyBindings.cs` (original control table, `docs/CONTROLS.md`), `Main.cs` (input routing, `LandAction`).
* UI on land: `LandHud.cs` (battle_land_interface: portraits, bars, Enter command menu), `Dialog.cs`, `LogHud.cs` (action hint).

## Contracts and owner decisions
* Sword: key **E** (ChrFightMode), LMB strike (on release; held > 0.45 s = strong), Ctrl block, Q pistol, Z recoil, C potion. Clicks during a playing blow are NOT queued; the hero cannot guard while his own blow plays.
* Combat runs at the animation file's real speed (no CombatSpeed hack; man.an is 15 fps x action speed 1.2: attack_1 hits 0.35 s after it starts, lasts 0.9 s). Damage lands on the `Attack_slash` event frame of the REAL playback (log `[windup]` with `SEADOGS_MELEELOG`, scenario `windup`). Block = the defender is in the guard NOW (character.cpp CheckAttackHit: no raise delay; a reeling character cannot block); a block pressed within 0.3 s before the blow parries; pressing block stops the hero's own blow (player.cpp boal); fight actions blend in over their `Blend time` (0.2 s); a hit reaction locks the target for the whole clip (hero: only 50 % of hits, stunChance).  NPC params come from the script (`NPC_Event_GetAttackActive/GetDefenceActive/GetAggresive/GetFireActive`), rolled every 0.2 s; hit reaction stuns 0.35 s.
* Enemy health is shown as green lines "damage/health" rising over the head after the hero's blow (red when ~2 blows left) — `HitFx.cs`.
* Dialog partner = the character the hero FACES (3 m, 30° cone); at dialog start both characters turn to each other.
* Drawing the sword while running stops the hero for the draw animation. Foreign-ship-like "by eye" rules: n/a on land.

## Hooks and scenarios (details: `docs/TESTING.md`)
`--goto=`, `--bring=Id__Name:metres@N`, `--key=E@N`, `--swing@N`, `--showhit=Id:dmg@N`, `--dialog=`, `--move=`, `--classic`, `--at=`, `--jump@N`; env `SEADOGS_FIGHTLOG=1`, `SEADOGS_MELEELOG=1`, `SEADOGS_CAMLOG=1`.
Scenarios in `run_tests.ps1 -Sea`: melee, meleehero, drawrun, landhud, heal, chest, door, screens.

## Open
Strafe/dodge, round/feint strikes (not in this game's scripts), female-hero text branches, caustics/second texture stage, jump damage (none in the original), per-character face/hair variety.
