# Brief: WORLD MAP and UKRAINIAN LAYER

## World map (`game/`)
* `WorldMap.cs` (scene, player ship speed/inertia per `wdm_ship.cpp`, island zones, exit-to-sea, day/night multiplier, camera per `wdm_camera.cpp`), `WorldEncounters.cs` (merchant / follow / war ships per `wdm_*_ship.cpp`: births ahead of the player, A* routes around land, lifetimes),
  `WorldStorms.cs` (31000/31001, clouds/rain/lightning), `WorldMapUi.cs` (event window `worldMap.eventWindow`, wait menu key T), `WorldLand.cs` (land mask from `Islands.gm`; ships and spawns never on land).
* The original has no patrols/chases/flee: scripts pick type/nation/count, the engine has 3 ship kinds. Only "follow" ships attack (radius 16); merchants/war pairs are met with Enter.
* Hooks: `--mappos=x,z,ay@N`, `--mapstorm=dx,dz@N`, `--mapmeet@N`; env `SEADOGS_MAPLOG=1`, `SEADOGS_MAPSPEED`, `SEADOGS_MAPNOKILL`. Scenarios: map, storm, mapnight, maptraffic, mapfollow, mapmerchant, mapencounter.

## Ukrainian layer (`lang/ukrainian/`, `StormScript/Runtime/Language.cs`, `tools/`)
* Priority order for string literals: `strings/_polish.tsv` (hand-polished, highest) → other layers → `_mt.tsv` (machine translation, ~9.9k fragments still unproofread, hand-polished so far ≈ 4.7k).
* Scripts build words from two literals (`"приш"+GetSexPhrase("ёл","ла")`): fix the stem so the fixed suffix completes the word; female-hero branches mostly not polished.
* Keep format tokens (`%s`, `#`, `\n`, colour codes), edge spaces, names exactly. Tools: `tools/scan_lang.py --suspicious`, `scan_texts.py`, `polish_batch.py`, `apply_texts.py`. Quality tests: `StormScript.Tests/LanguageQualityTests.cs` (0 Russian-only letters, ≤3 Russianisms, 0 untranslated sentences).
* Env `SEADOGS_LANGLOG=1` logs every shown dialog/interface string flagged RU/EN — use it to find what players actually see before proofreading.
