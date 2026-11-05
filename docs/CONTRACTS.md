# Контракти рушія: скрипти ↔ порт (автоматичний аудит)

_Згенеровано `python tools/contract_audit.py` 2026-11-05 15:44. Не редагувати вручну — перезапустити (`run_tests.ps1 -Contracts`)._

Джерела: скрипти `addon/merged/PROGRAM` (1139 файлів, 1154 #define, 4137 функцій), порт `StormScript/Runtime` + `game/*.cs` (73 файлів), reference `reference/storm-engine` (95 класів, 219 подій, 159 native), логи: 50 (`game/smoke_*.log`, `reports/*/log_tail.txt`).

## Відомі обмеження
* Обробник порту «обробляє» код, якщо число коду (або підкоманда-рядок) трапляється будь-де в коді, досяжному з лямбди `EntityHandlers[...]` (замикання викликів до 5 рівнів, неоднозначні методи — усі кандидати). Тому пропуски скоріше НЕДОоцінені: випадковий збіг числа ховає проблему, але хибних «немає» мало. Заглушки `case N: return 0` рахуються як обробка.
* Клас цілі `SendMessage` береться з `CreateEntity/FindClass` змінної; для `aref`-параметрів — за кодом (де ще цей код шлють відомій сутності) або за reference (`ProcessMessage`, що має цей `case`). 6 викликів з нестатичним кодом пропущено.
* Reference (`reference/storm-engine`) — відкритий рушій пізнішої гри (TEHO); коди/формати здебільшого збігаються з PotC, але не завжди. «Оригінал не обробляє» = у його `ProcessMessage` такого `case` немає.
* Події: «рушійна» = скрипти слухають, але самі не підіймають `Event/PostEvent` з константною назвою, або reference її підіймає. Назви, які скрипти зберігають як дані (для пізнішого `Event(змінна)`), позначені як info. Порт «підіймає», якщо назва є першим аргументом `FireEvent/PostEventInternal` (рядок) або хоч десь у коді порту як рядок.
* Параметри: послідовність `GetEventData()` у тексті обробника (умовні читання позначені), тип — з оголошення змінної; `?` = невідомо (атрибут, `sti(GetEventData())`). Аргументи порту — з ініціалізатора `new List<V> { V.Int(..), ... }`; списки, зібрані окремо, не перевіряються. int↔float — не критично (VM конвертує).
* Runtime: `[events]` у логах — лише топ-40 подій кожного прогону, тож «0× у логах» означає «не в топ-40». Логи можуть бути старші за код (запускати після `run_tests.ps1 -Smoke/-Sea ...`); `[nohandler]` класу, який уже має обробник, позначено як застарілий.
* «Параметри подій: формат» — аргументи порту проти формату `core.Event` у reference; якщо обробники PotC-скриптів читають саме те, що дає порт, розбіжність вважається різницею версій рушія (info).
* Функції: native = викликається, але не визначена в жодному скрипті. Арність порту — найбільший індекс `I/S/F/Arg(a, N)`; лямбди з `a.Skip/a.Length` вважаються змінної арності.

## Підсумок
| Контракт | реалізовано | відсутнє | розбіжність | інфо |
|---|---|---|---|---|
| Повідомлення | 223 | 36 | 4 | 11 |
| Повідомлення (runtime) | 0 | 1 | 0 | 26 |
| Сутності | 40 | 12 | 0 | 13 |
| Функції рушія | 77 | 4 | 12 | 21 |
| Події | 186 | 38 | 2 | 24 |
| Параметри подій: формат | 95 | 0 | 0 | 6 |
| Параметри подій | 208 | 0 | 0 | 2 |
| Події: runtime | 0 | 0 | 0 | 39 |
| Ресурси | 0 | 10 | 0 | 0 |
| Ресурси: ini | 82 | 7 | 0 | 6 |

## Топ-40 найпомітніших для гравця розбіжностей
| # | Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|---|
| 1 | Повідомлення | Flag → MSG_FLAG_INIT (40200) fmt "lil" | Locations/locations_loader.c:660, SEA_AI/AIFort.c:263 | немає EntityHandlers["Flag"] | оригінал: обробляє (src/libs/rigging/src/flag.cpp:148); [nohandler] 6× у логах |
| 2 | Повідомлення | SINKEFFECT → MSG_SHIP_DELETE (50301) fmt "la" | SEA_AI/shipDead.c:7 | немає EntityHandlers["SINKEFFECT"] | оригінал: обробляє (src/libs/sink_effect/src/sink_effect.cpp:39); [nohandler] 1× у логах |
| 3 | Повідомлення | Seafoam → MSG_SHIP_DELETE (50301) fmt "la" | SEA_AI/shipDead.c:5 | немає EntityHandlers["Seafoam"] | оригінал: обробляє (src/libs/sea_foam/src/seafoam.cpp:457); [nohandler] 1× у логах |
| 4 | Повідомлення | blots → MSG_BLOTS_SETMODEL (30600) fmt "lia" | SEA_AI/AIFort.c:253 | немає EntityHandlers["blots"] | оригінал: обробляє (src/libs/blot/src/blots.cpp:70); [nohandler] 6× у логах |
| 5 | Повідомлення | fader → FADER_STARTFRAME (33002) fmt "l" | Loc_ai/LAi_boarding.c:543, QUESTS/quests.c:896 (+10) | код 33002 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/fader.cpp:171); порт: game/Main.cs:227 |
| 6 | Повідомлення | xinterface → MSG_INTERFACE_FOURIMAGE_CHANGE (45020) fmt "lsl" | INTERFACE/passengers.c:327, INTERFACE/shipyard.c:1332 (+1) | код 45020 не знайдено в обробнику | оригінал: обробляє (src/libs/xinterface/src/xinterface.cpp:455); порт: game/Main.cs:221 |
| 7 | Сутності | Animals | animals.c:6, animals.c:62 | немає реалізації; скрипти шлють йому 7 різних повідомлень | оригінал: CREATE_CLASS(ANIMALS) |
| 8 | Сутності | HELPCHOOSER | INTERFACE/interface.c:1223 | немає реалізації; скрипти шлють йому 1 різних повідомлень | оригінал: CREATE_CLASS(HELPCHOOSER) |
| 9 | Сутності | SINKEFFECT | SEA_AI/sea.c:195 | немає реалізації; скрипти шлють йому 1 різних повідомлень | оригінал: CREATE_CLASS(SINKEFFECT) |
| 10 | Сутності | Seafoam | SEA_AI/sea.c:837 | немає реалізації; скрипти шлють йому 1 різних повідомлень | оригінал: CREATE_CLASS(SEAFOAM) |
| 11 | Сутності | blots | SEA_AI/AIFort.c:252 | немає реалізації; скрипти шлють йому 1 різних повідомлень | оригінал: CREATE_CLASS(Blots) |
| 12 | Повідомлення | location → MSG_LOCATION_EX_MSG (30155) "AddRats" fmt "lsl" | Locations/locations_loader.c:491, Locations/locations_loader.c:494 (+5) | підкоманду "AddRats" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242 |
| 13 | Повідомлення | locationcamera → MSG_CAMERA_MOVE (30513) fmt "lf" | Locations/locations_camera.c:52, Locations/locations_loader.c:445 | код 30513 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location_camera.cpp:358); порт: game/Main.cs:254 |
| 14 | Події | ievntGetHelpTexture | INTERFACE/ship.c:154, INTERFACE/store.c:65 | НЕ підіймається портом | оригінал: core.Event "s" (src/libs/xinterface/src/xinterface.cpp:3301) |
| 15 | Повідомлення | ILogAndActions → LOG_AND_ACTIONS_CHANGE (45026) fmt "lll" | BATTLE_INTERFACE/LogInterface.c:72 | код 45026 не знайдено в обробнику | оригінал: обробляє (src/libs/battle_interface/src/log_and_action.cpp:102); порт: game/Main.cs:313 |
| 16 | Повідомлення | ILogAndActions → LOG_AND_ACTIONS_INIT (45022) fmt "lll" | BATTLE_INTERFACE/LogInterface.c:22 | код 45022 не знайдено в обробнику | оригінал: обробляє (src/libs/battle_interface/src/log_and_action.cpp:102); порт: game/Main.cs:313 |
| 17 | Повідомлення | Island → MSG_ISLAND_START (50102) fmt "l" | SEA_AI/AIIsland.c:3 | код 50102 не знайдено в обробнику | оригінал: обробляє (src/libs/island/src/island.cpp:238); порт: game/Sea.cs:120 |
| 18 | Повідомлення | Island → MSG_LOCATION_ADD_MODEL (30100) fmt "liss" | Locations/locations_loader.c:674 | код 30100 не знайдено в обробнику | оригінал: обробляє (src/libs/island/src/island.cpp:238); порт: game/Sea.cs:120 |
| 19 | Повідомлення | location → MSG_LOCATION_HIDELGROUP (30181) fmt "ls" | Locations/locations.c:86 | код 30181 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242; клас визначено за кодом (reference) |
| 20 | Повідомлення | location → MSG_LOCATION_VIEWLGROUP (30180) fmt "lsffl" | Locations/locations.c:79 | код 30180 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242; клас визначено за кодом (reference) |
| 21 | Повідомлення | locationcamera → MSG_CAMERA_SLEEP (30520) fmt "ll" | Locations/locations_camera.c:59 | код 30520 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location_camera.cpp:358); порт: game/Main.cs:254 |
| 22 | Повідомлення | npcharacter → MSG_NPCHARACTER_GETTASK (30403) fmt "le" | Characters/characters_ai.c:167 | код 30403 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/character.cpp:694); порт: game/Main.cs:362; клас визначено за кодом (reference) |
| 23 | Повідомлення | ship → AI_MESSAGE_SET_LAYERS (51015) fmt "lss" | SEA_AI/AISea.c:6 | код 51015 не знайдено в обробнику | оригінал: обробляє (src/libs/ship/src/ship.cpp:1210); порт: game/Sea.cs:116; клас визначено за кодом (reference) |
| 24 | Повідомлення | xinterface → MSG_INTERFACE_CHECK_AGAIN (45057) fmt "l" | INTERFACE/mainmenu.c:407 | код 45057 не знайдено в обробнику | оригінал: обробляє (src/libs/xinterface/src/xinterface.cpp:455); порт: game/Main.cs:221 |
| 25 | Повідомлення | xinterface → MSG_INTERFACE_LAUNCH_DASHBOARD (45054) fmt "l" | INTERFACE/interface.c:1669 | код 45054 не знайдено в обробнику | оригінал: обробляє (src/libs/xinterface/src/xinterface.cpp:455); порт: game/Main.cs:221 |
| 26 | Повідомлення | HELPCHOOSER → MSG_HELPCHOOSER_START (45200) fmt "ls" | INTERFACE/interface.c:1229 | немає EntityHandlers["HELPCHOOSER"] | оригінал: обробляє (src/libs/xinterface/src/help_chooser/help_chooser.cpp:180) |
| 27 | Повідомлення | sea_operator → MSG_SEA_OPERATOR_BALL_UPDATE (77401) fmt "lalffffff" | SEA_AI/AIBalls.c:166 | немає EntityHandlers["sea_operator"] | оригінал: обробляє (src/libs/sea_operator/src/sea_operator.cpp:51); клас визначено за кодом (reference) |
| 28 | Повідомлення | sea_operator → MSG_SEA_OPERATOR_FIRE (77400) fmt "lisffffff" | SEA_AI/shipBortFire.c:45 | немає EntityHandlers["sea_operator"] | оригінал: обробляє (src/libs/sea_operator/src/sea_operator.cpp:51); клас визначено за кодом (reference) |
| 29 | Події | CannReCaclParam | SEA_AI/AICannon.c:21 | НЕ підіймається портом | оригінал: core.Event "l" (src/libs/sea_ai/src/ai_fort.cpp:82) |
| 30 | Події | CannUnload | SEA_AI/AICannon.c:19 | НЕ підіймається портом | оригінал: core.Event "a" (src/libs/sea_ai/src/ai_cannon.cpp:193) |
| 31 | Події | CharacterGroup_UpdateAlarm | Loc_ai/LAi_groups.c:468 | НЕ підіймається портом | оригінал: core.Event "fl" (src/libs/location/src/characters_groups.cpp:202) |
| 32 | Події | Location_CharacterInLocator | Characters/characters_events.c:18 | НЕ підіймається портом | оригінал: core.Event "iissff" (src/libs/location/src/character.cpp:232) |
| 33 | Події | Location_CharacterSGFire | Loc_ai/LAi_fightparams.c:672 | НЕ підіймається портом | оригінал: core.Event "iif" (src/libs/location/src/player.cpp:812) |
| 34 | Події | Location_Character_EndAction | Loc_ai/LAi_init.c:44 | НЕ підіймається портом | оригінал: core.Event "i" (src/libs/location/src/character.cpp:2719) |
| 35 | Події | SeaGetLayers | SEA_AI/AIShip.c:88 | НЕ підіймається портом | оригінал: core.Event "i" (src/libs/ship/src/ship.cpp:1438) |
| 36 | Події | Shp2Strand | SEA_AI/AIShip.c:97 | НЕ підіймається портом | оригінал: core.Event "ll" (src/libs/ship/src/ship.cpp:511) |
| 37 | Події | ShpEatSwimGood | SEA_AI/AISeaGoods.c:18 | НЕ підіймається портом | оригінал: core.Event "llsl" (src/libs/sea_ai/src/ai_sea_goods.cpp:92) |
| 38 | Повідомлення | location → MSG_LOCATION_EX_MSG (30155) "AddEagle" fmt "ls" | Locations/locations_loader.c:469 | підкоманду "AddEagle" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242 |
| 39 | Повідомлення | location → MSG_LOCATION_EX_MSG (30155) "AddFlys" fmt "lsfff" | Locations/locations_loader.c:404 | підкоманду "AddFlys" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242 |
| 40 | Повідомлення | location → MSG_LOCATION_EX_MSG (30155) "AddLizards" fmt "ls" | Locations/locations_loader.c:481 | підкоманду "AddLizards" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242 |

## Повідомлення — розбіжності (40)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Повідомлення | Flag → MSG_FLAG_INIT (40200) fmt "lil" | Locations/locations_loader.c:660, SEA_AI/AIFort.c:263 | немає EntityHandlers["Flag"] | оригінал: обробляє (src/libs/rigging/src/flag.cpp:148); [nohandler] 6× у логах |
| Повідомлення | SINKEFFECT → MSG_SHIP_DELETE (50301) fmt "la" | SEA_AI/shipDead.c:7 | немає EntityHandlers["SINKEFFECT"] | оригінал: обробляє (src/libs/sink_effect/src/sink_effect.cpp:39); [nohandler] 1× у логах |
| Повідомлення | Seafoam → MSG_SHIP_DELETE (50301) fmt "la" | SEA_AI/shipDead.c:5 | немає EntityHandlers["Seafoam"] | оригінал: обробляє (src/libs/sea_foam/src/seafoam.cpp:457); [nohandler] 1× у логах |
| Повідомлення | blots → MSG_BLOTS_SETMODEL (30600) fmt "lia" | SEA_AI/AIFort.c:253 | немає EntityHandlers["blots"] | оригінал: обробляє (src/libs/blot/src/blots.cpp:70); [nohandler] 6× у логах |
| Повідомлення | fader → FADER_STARTFRAME (33002) fmt "l" | Loc_ai/LAi_boarding.c:543, QUESTS/quests.c:896, reload.c:288 (+9) | код 33002 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/fader.cpp:171); порт: game/Main.cs:227 |
| Повідомлення | xinterface → MSG_INTERFACE_FOURIMAGE_CHANGE (45020) fmt "lsl" | INTERFACE/passengers.c:327, INTERFACE/shipyard.c:1332, INTERFACE/transfer_characters.c:459 | код 45020 не знайдено в обробнику | оригінал: обробляє (src/libs/xinterface/src/xinterface.cpp:455); порт: game/Main.cs:221 |
| Повідомлення | location → MSG_LOCATION_EX_MSG (30155) "AddRats" fmt "lsl" | Locations/locations_loader.c:491, Locations/locations_loader.c:494, Locations/locations_loader.c:503 (+4) | підкоманду "AddRats" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242 |
| Повідомлення | locationcamera → MSG_CAMERA_MOVE (30513) fmt "lf" | Locations/locations_camera.c:52, Locations/locations_loader.c:445 | код 30513 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location_camera.cpp:358); порт: game/Main.cs:254 |
| Повідомлення | ILogAndActions → LOG_AND_ACTIONS_CHANGE (45026) fmt "lll" | BATTLE_INTERFACE/LogInterface.c:72 | код 45026 не знайдено в обробнику | оригінал: обробляє (src/libs/battle_interface/src/log_and_action.cpp:102); порт: game/Main.cs:313 |
| Повідомлення | ILogAndActions → LOG_AND_ACTIONS_INIT (45022) fmt "lll" | BATTLE_INTERFACE/LogInterface.c:22 | код 45022 не знайдено в обробнику | оригінал: обробляє (src/libs/battle_interface/src/log_and_action.cpp:102); порт: game/Main.cs:313 |
| Повідомлення | Island → MSG_ISLAND_START (50102) fmt "l" | SEA_AI/AIIsland.c:3 | код 50102 не знайдено в обробнику | оригінал: обробляє (src/libs/island/src/island.cpp:238); порт: game/Sea.cs:120 |
| Повідомлення | Island → MSG_LOCATION_ADD_MODEL (30100) fmt "liss" | Locations/locations_loader.c:674 | код 30100 не знайдено в обробнику | оригінал: обробляє (src/libs/island/src/island.cpp:238); порт: game/Sea.cs:120 |
| Повідомлення | location → MSG_LOCATION_HIDELGROUP (30181) fmt "ls" | Locations/locations.c:86 | код 30181 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242; клас визначено за кодом (reference) |
| Повідомлення | location → MSG_LOCATION_VIEWLGROUP (30180) fmt "lsffl" | Locations/locations.c:79 | код 30180 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242; клас визначено за кодом (reference) |
| Повідомлення | locationcamera → MSG_CAMERA_SLEEP (30520) fmt "ll" | Locations/locations_camera.c:59 | код 30520 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location_camera.cpp:358); порт: game/Main.cs:254 |
| Повідомлення | npcharacter → MSG_NPCHARACTER_GETTASK (30403) fmt "le" | Characters/characters_ai.c:167 | код 30403 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/character.cpp:694); порт: game/Main.cs:362; клас визначено за кодом (reference) |
| Повідомлення | ship → AI_MESSAGE_SET_LAYERS (51015) fmt "lss" | SEA_AI/AISea.c:6 | код 51015 не знайдено в обробнику | оригінал: обробляє (src/libs/ship/src/ship.cpp:1210); порт: game/Sea.cs:116; клас визначено за кодом (reference) |
| Повідомлення | xinterface → MSG_INTERFACE_CHECK_AGAIN (45057) fmt "l" | INTERFACE/mainmenu.c:407 | код 45057 не знайдено в обробнику | оригінал: обробляє (src/libs/xinterface/src/xinterface.cpp:455); порт: game/Main.cs:221 |
| Повідомлення | xinterface → MSG_INTERFACE_LAUNCH_DASHBOARD (45054) fmt "l" | INTERFACE/interface.c:1669 | код 45054 не знайдено в обробнику | оригінал: обробляє (src/libs/xinterface/src/xinterface.cpp:455); порт: game/Main.cs:221 |
| Повідомлення | HELPCHOOSER → MSG_HELPCHOOSER_START (45200) fmt "ls" | INTERFACE/interface.c:1229 | немає EntityHandlers["HELPCHOOSER"] | оригінал: обробляє (src/libs/xinterface/src/help_chooser/help_chooser.cpp:180) |
| Повідомлення | sea_operator → MSG_SEA_OPERATOR_BALL_UPDATE (77401) fmt "lalffffff" | SEA_AI/AIBalls.c:166 | немає EntityHandlers["sea_operator"] | оригінал: обробляє (src/libs/sea_operator/src/sea_operator.cpp:51); клас визначено за кодом (reference) |
| Повідомлення | sea_operator → MSG_SEA_OPERATOR_FIRE (77400) fmt "lisffffff" | SEA_AI/shipBortFire.c:45 | немає EntityHandlers["sea_operator"] | оригінал: обробляє (src/libs/sea_operator/src/sea_operator.cpp:51); клас визначено за кодом (reference) |
| Повідомлення | location → MSG_LOCATION_EX_MSG (30155) "AddEagle" fmt "ls" | Locations/locations_loader.c:469 | підкоманду "AddEagle" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242 |
| Повідомлення | location → MSG_LOCATION_EX_MSG (30155) "AddFlys" fmt "lsfff" | Locations/locations_loader.c:404 | підкоманду "AddFlys" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242 |
| Повідомлення | location → MSG_LOCATION_EX_MSG (30155) "AddLizards" fmt "ls" | Locations/locations_loader.c:481 | підкоманду "AddLizards" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242 |
| Повідомлення | location → MSG_LOCATION_EX_MSG (30155) "GetPatchMiddlePos" fmt "lseee" | animals.c:9 | підкоманду "GetPatchMiddlePos" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242 |
| Повідомлення | npcharacter → MSG_CHARACTER_EX_MSG (30373) "DistToLocator" fmt "lssse" | Characters/characters_ai.c:151 | підкоманду "DistToLocator" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/character.cpp:694); порт: game/Main.cs:362; клас визначено за кодом (reference) |
| Повідомлення | npcharacter → MSG_CHARACTER_EX_MSG (30373) "FindForvardLocator" fmt "lsse" | Characters/characters_ai.c:160 | підкоманду "FindForvardLocator" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/character.cpp:694); порт: game/Main.cs:362; клас визначено за кодом (reference) |
| Повідомлення | npcharacter → MSG_CHARACTER_EX_MSG (30373) "IsActive" fmt "ls" | Loc_ai/types/LAi_player.c:44 | підкоманду "IsActive" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/character.cpp:694); порт: game/Main.cs:362; клас визначено за кодом (reference) |
| Повідомлення | MODELR → MSG_MODEL_RESTORE (20505) fmt "l" | SEA_AI/AIAbordage.c:70, SEA_AI/AIAbordage.c:74, SEA_AI/Cabin.c:252 (+1) | код 20505 не знайдено в обробнику | оригінал: обробляє (src/libs/model/src/model.cpp:300); порт: game/Main.cs:303 |
| Повідомлення | MODELR → MSG_MODEL_SET_LIGHT_PATH (20503) fmt "ls" | SEA_AI/AIFort.c:106, SEA_AI/sea.c:557 | код 20503 не знайдено в обробнику | оригінал: обробляє (src/libs/model/src/model.cpp:300); порт: game/Main.cs:303 |
| Повідомлення | MODELR → MSG_MODEL_SET_TECHNIQUE (20510) fmt "ls" | ITEMS/itemLogic.c:24 | код 20510 не знайдено в обробнику | оригінал: обробляє (src/libs/model/src/model.cpp:300); порт: game/Main.cs:303 |
| Повідомлення | TELESCOPE → MSG_TELESCOPE_INIT_ARRAYS (77300) fmt "leee" | SEA_AI/sea.c:846 | код 77300 приймається без дії (згаданий лише в коментарі обробника) | оригінал: класу немає в reference; порт: game/SeaTelescope.cs:41; [nohandler] 6× у логах |
| Повідомлення | scrshoter → MSG_SCRSHOT_READ (45043) fmt "lsse" | INTERFACE/save_load.c:653, INTERFACE/save_load.c:655 | немає EntityHandlers["scrshoter"] | оригінал: обробляє (src/libs/xinterface/src/scr_shoter/scr_shoter.cpp:242); клас визначено за кодом (reference) |
| Повідомлення | scrshoter → MSG_SCRSHOT_RELEASE (45044) fmt "ls" | INTERFACE/save_load.c:693 | немає EntityHandlers["scrshoter"] | оригінал: обробляє (src/libs/xinterface/src/scr_shoter/scr_shoter.cpp:242); клас визначено за кодом (reference) |
| Повідомлення | ship → MSG_SHIP_CREATE (50300) fmt "laa" | Locations/locations_loader.c:874, Locations/locations_loader.c:894 | код 50300 приймається без дії (згаданий лише в коментарі обробника) | оригінал: обробляє (src/libs/ship/src/ship.cpp:1210); порт: game/Sea.cs:116 |
| Повідомлення | location → MSG_LOCATION_EX_MSG (30155) "TestLocatorsGroup" fmt "lss" | Locations/locators_test.c:311, Locations/locators_test.c:316, Locations/locators_test.c:321 (+7) | підкоманду "TestLocatorsGroup" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:242; лише налагоджувальні скрипти |
| Повідомлення | TELESCOPE → MSG_SHIP_DELETE (50301) fmt "la" | SEA_AI/shipDead.c:8 | код 50301 приймається без дії (згаданий лише в коментарі обробника) | оригінал: класу немає в reference; порт: game/SeaTelescope.cs:41 |
| Повідомлення | TELESCOPE → MSG_TELESCOPE_REQUEST (77301) fmt "l" | SEA_AI/telescope.c:4 | код 77301 приймається без дії (згаданий лише в коментарі обробника) | оригінал: класу немає в reference; порт: game/SeaTelescope.cs:41 |
| Повідомлення | TMPTELEPORT → 42222 fmt "la" | teleport.c:242 | немає EntityHandlers["TMPTELEPORT"] | оригінал: обробляє (src/libs/teleport/src/teleport.cpp:199); лише налагоджувальні скрипти |

## Повідомлення (runtime) — розбіжності (1)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Повідомлення (runtime) | SeaLocatorShow \| a \|  |  | немає обробника класу (лише runtime) | [nohandler] 19× у game/smoke_board.log, game/smoke_chase.log, game/smoke_door.log |

## Сутності — розбіжності (12)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Сутності | Animals | animals.c:6, animals.c:62 | немає реалізації; скрипти шлють йому 7 різних повідомлень | оригінал: CREATE_CLASS(ANIMALS) |
| Сутності | HELPCHOOSER | INTERFACE/interface.c:1223 | немає реалізації; скрипти шлють йому 1 різних повідомлень | оригінал: CREATE_CLASS(HELPCHOOSER) |
| Сутності | SINKEFFECT | SEA_AI/sea.c:195 | немає реалізації; скрипти шлють йому 1 різних повідомлень | оригінал: CREATE_CLASS(SINKEFFECT) |
| Сутності | Seafoam | SEA_AI/sea.c:837 | немає реалізації; скрипти шлють йому 1 різних повідомлень | оригінал: CREATE_CLASS(SEAFOAM) |
| Сутності | blots | SEA_AI/AIFort.c:252 | немає реалізації; скрипти шлють йому 1 різних повідомлень | оригінал: CREATE_CLASS(Blots) |
| Сутності | Flag | SEA_AI/AIShip.c:38 | без обробника повідомлень; клас згадується в коді порту; скрипти шлють йому 1 різних повідомлень | оригінал: CREATE_CLASS(FLAG); порт: game/RiggingFx.cs:330 |
| Сутності | AISeaGoods | SEA_AI/AISeaGoods.c:12 | немає реалізації; повідомлень йому скрипти не шлють | оригінал: CREATE_CLASS(AISeaGoods) |
| Сутності | InfoHandler | INTERFACE/interface.c:1575 | немає реалізації; повідомлень йому скрипти не шлють | оригінал: CREATE_CLASS(InfoHandler) |
| Сутності | Rope | SEA_AI/AIShip.c:34 | немає реалізації; повідомлень йому скрипти не шлють | оригінал: CREATE_CLASS(ROPE) |
| Сутності | Sharks | SEA_AI/sea.c:235 | немає реалізації; повідомлень йому скрипти не шлють | оригінал: CREATE_CLASS(Sharks) |
| Сутності | SunGlow | Weather/WhrSun.c:28 | немає реалізації; повідомлень йому скрипти не шлють | оригінал: CREATE_CLASS(SUNGLOW) |
| Сутності | WideScreen | QUESTS/quests_movie.c:80 | немає реалізації; повідомлень йому скрипти не шлють | оригінал: CREATE_CLASS(WideScreen) |

## Функції рушія — розбіжності (16)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Функції рушія | CreateClass() ×5 | gameutils.c:7, lcorrect.c:5, memtest.c:5 (+2) | немає в порті (Builtins/HostFunctions) | оригінал: 1 арг. (src/libs/core/src/internal_functions.cpp:118); у логах не траплялась |
| Функції рушія | InitInterface() |  | unresolved під час прогону | 1× у reports/2026-11-05_08-51-31/log_tail.txt |
| Функції рушія | GlobalLngFileID() ×1 | INTERFACE/utilite.c:376 | немає в порті (Builtins/HostFunctions) | оригінал: 0 арг. (src/libs/xinterface/src/string_service/str_service.cpp:1615); native-оголошення: native int GlobalLngFileID() (INTERFACE/utilite.c:7); у логах не траплялась |
| Функції рушія | ExecuteTechnique() ×10 | Locations/locations_loader.c:76, Locations/locations_loader.c:80, Locations/locations_loader.c:84 (+2) | stub: no-op | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 1 арг. (src/libs/location/src/location_script_lib.cpp:373); native-оголошення: native void ExecuteTechnique(string techName) (Locations/locations.c:17) |
| Функції рушія | LockControl() ×10 | BATTLE_INTERFACE/ActivePerksShow.c:197, CONTROLS/controls.c:285, CONTROLS/controls.c:303 (+2) | stub: no-op | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 2 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | MapControl() ×1 | CONTROLS/controls.c:120 | stub: no-op | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 2 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | GetAttributeName() ×116 | BATTLE_INTERFACE/ActivePerksShow.c:69, BATTLE_INTERFACE/BattleInterface.c:1790, BATTLE_INTERFACE/LandInterface.c:997 (+2) | скрипт передає 1/2 арг., оригінал оголошує 1; порт читає лише 1 з 2 арг. | порт: StormScript/Runtime/Builtins.cs:142; оригінал: 1 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | XI_SetMouseSensitivity() ×2 | CONTROLS/controls.c:357, INTERFACE/option_screen.c:1350 | stub: no-op | порт: StormScript/Runtime/Builtins.cs:224; оригінал: 2 арг. (src/libs/xinterface/src/string_service/str_service.cpp:1633); native-оголошення: native void XI_SetMouseSensitivity( float fMouseXSens, float fMouseYSens ) (INTERFACE/utilite.c:11) |
| Функції рушія | CreateControl() ×1 | CONTROLS/controls.c:112 | stub: returns 0 | порт: StormScript/Runtime/Builtins.cs:212; оригінал: 1 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | GetEntity() ×1 | INTERFACE/save_load.c:702 | stub: returns 0 | порт: StormScript/Runtime/Builtins.cs:171; оригінал: 2 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | GetEntityNext() ×1 | INTERFACE/save_load.c:711 | stub: returns 0 | порт: StormScript/Runtime/Builtins.cs:171; у reference немає |
| Функції рушія | SetControlFlags() ×1 | CONTROLS/controls.c:127 | stub: no-op | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 2 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | SetControlTreshold() ×1 | CONTROLS/init_xbox.c:94 | stub: no-op | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 2 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | XI_ControlMakeInvert() ×1 | CONTROLS/controls.c:331 | stub: no-op | порт: StormScript/Runtime/Builtins.cs:224; оригінал: 2 арг. (src/libs/xinterface/src/string_service/str_service.cpp:1639); native-оголошення: native void XI_ControlMakeInvert(string sControlName, int bInvertState) (INTERFACE/utilite.c:12) |
| Функції рушія | GetNextLineString() ×1 | Locations/locators_test.c:28 | немає в порті (Builtins/HostFunctions) | оригінал: 0 арг. (src/libs/location/src/location_script_lib.cpp:379); native-оголошення: native string GetNextLineString() (Locations/locations.c:18); у логах не траплялась; лише налагоджувальні скрипти |
| Функції рушія | GetEntityName() ×1 | INTERFACE/save_load.c:706 | порт читає лише 0 з 1 арг. | порт: StormScript/Runtime/Builtins.cs:172; у reference немає |

## Події — розбіжності (40)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Події | ievntGetHelpTexture | INTERFACE/ship.c:154, INTERFACE/store.c:65 | НЕ підіймається портом | оригінал: core.Event "s" (src/libs/xinterface/src/xinterface.cpp:3301) |
| Події | CannReCaclParam | SEA_AI/AICannon.c:21 | НЕ підіймається портом | оригінал: core.Event "l" (src/libs/sea_ai/src/ai_fort.cpp:82) |
| Події | CannUnload | SEA_AI/AICannon.c:19 | НЕ підіймається портом | оригінал: core.Event "a" (src/libs/sea_ai/src/ai_cannon.cpp:193) |
| Події | CharacterGroup_UpdateAlarm | Loc_ai/LAi_groups.c:468 | НЕ підіймається портом | оригінал: core.Event "fl" (src/libs/location/src/characters_groups.cpp:202) |
| Події | Location_CharacterInLocator | Characters/characters_events.c:18 | НЕ підіймається портом | оригінал: core.Event "iissff" (src/libs/location/src/character.cpp:232) |
| Події | Location_CharacterSGFire | Loc_ai/LAi_fightparams.c:672 | НЕ підіймається портом | оригінал: core.Event "iif" (src/libs/location/src/player.cpp:812) |
| Події | Location_Character_EndAction | Loc_ai/LAi_init.c:44 | НЕ підіймається портом | оригінал: core.Event "i" (src/libs/location/src/character.cpp:2719) |
| Події | SeaGetLayers | SEA_AI/AIShip.c:88 | НЕ підіймається портом | оригінал: core.Event "i" (src/libs/ship/src/ship.cpp:1438) |
| Події | Shp2Strand | SEA_AI/AIShip.c:97 | НЕ підіймається портом | оригінал: core.Event "ll" (src/libs/ship/src/ship.cpp:511) |
| Події | ShpEatSwimGood | SEA_AI/AISeaGoods.c:18 | НЕ підіймається портом | оригінал: core.Event "llsl" (src/libs/sea_ai/src/ai_sea_goods.cpp:92) |
| Події | FaderEvent_EndFadeIn | Loc_ai/LAi_utils.c:661, Locations/locations.c:119, INTERFACE/interface.c:23 | НЕ підіймається портом | оригінал: core.Event "li" (src/libs/location/src/fader.cpp:293) |
| Події | FaderEvent_StartFadeIn | INTERFACE/interface.c:21 | НЕ підіймається портом | оригінал: core.Event "li" (src/libs/location/src/fader.cpp:278) |
| Події | EWhr_GetFogDensity | Weather/WhrWeather.c:41 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/shadow/src/shadow.cpp:150) |
| Події | EWhr_GetShadowDensity | Weather/WhrWeather.c:40 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/shadow/src/shadow.cpp:143) |
| Події | EWhr_GetWindAngle | Weather/WhrWeather.c:38 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/location/src/grass.cpp:323) |
| Події | EWhr_GetWindSpeed | Weather/WhrWeather.c:39 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/location/src/grass.cpp:343) |
| Події | WhrCalcFogColor | SEA_AI/Cabin.c:299, Weather/WhrWeather.c:225 | НЕ підіймається портом | оригінал: core.Event "fff" (src/libs/weather/src/sky.cpp:26) |
| Події | CameraPosAng | seadogs.c:69 | НЕ підіймається портом | оригінал: core.Event "ffffff" (src/libs/renderer/src/s_device.cpp:2078) |
| Події | DelInterfaceTexture | INTERFACE/save_load.c:24 | НЕ підіймається портом | оригінал: core.Event "s\|ss" (src/libs/xinterface/src/nodes/xi_scroll_image.cpp:815) |
| Події | DisableScroll | INTERFACE/helpscreen.c:29 | НЕ підіймається портом | оригінал: core.Event "sl" (src/libs/xinterface/src/nodes/xi_formt_text.cpp:926) |
| Події | EmergencyDialogExit | dialog.c:9 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/dialog/src/dialog.cpp:925) |
| Події | EventSGMode | Loc_ai/LAi_utils.c:706 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/location/src/player.cpp:82) |
| Події | GetCurrentCharge | BATTLE_INTERFACE/BattleInterface.c:86 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/battle_interface/src/sea/battle_navigator.cpp:1420) |
| Події | GetInterfaceTexture | INTERFACE/save_load.c:23 | НЕ підіймається портом | оригінал: core.Event "sl\|sls" (src/libs/xinterface/src/nodes/xi_scroll_image.cpp:677) |
| Події | GetQuestTextFileName | INTERFACE/BaseInterface.c:6 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/xinterface/src/xinterface.cpp:214) |
| Події | GetRelationEvent | NATIONS/nations.c:3 | НЕ підіймається портом | оригінал: core.Event "ll" (src/libs/sea_ai/src/ai_helper.cpp:95) |
| Події | IEvnt_MouseClick | INTERFACE/transfer_crew.c:57, INTERFACE/transfer_goods.c:76 | НЕ підіймається портом | оригінал: core.Event "sl" (src/libs/xinterface/src/nodes/xi_lr_changer.cpp:208) |
| Події | ISound | INTERFACE/interface.c:1034 | НЕ підіймається портом | оригінал: core.Event "l" (src/libs/xinterface/src/back_scene/back_scene.cpp:261) |
| Події | LocationTimeUpdate | Locations/locations_loader.c:939 | НЕ підіймається портом | оригінал: core.Event "f" (src/libs/location/src/location.cpp:144) |
| Події | MSG_TELESCOPE_REQUEST | SEA_AI/sea.c:223 | НЕ підіймається портом | оригінал: core.Event "l" (src/libs/battle_interface/src/spyglass/spyglass.cpp:170) |
| Події | QuestTopChange | INTERFACE/boal_questbook.c:31, INTERFACE/QuestBook.c:25 | НЕ підіймається портом | оригінал: core.Event "l" (src/libs/xinterface/src/nodes/xi_quest_titles.cpp:180) |
| Події | ScrollPosChange | INTERFACE/addon_team.c:79, INTERFACE/boal_questbook.c:36, INTERFACE/controls.c:66 (+4) | НЕ підіймається портом | оригінал: core.Event "fs" (src/libs/xinterface/src/nodes/xi_scroller.cpp:316) |
| Події | ScrollTopChange | INTERFACE/addon_team.c:80, INTERFACE/boal_questbook.c:37, INTERFACE/helpscreen.c:31 (+3) | НЕ підіймається портом | оригінал: core.Event "l" (src/libs/xinterface/src/nodes/xi_scroller.cpp:197) |
| Події | SetWindowSize | BATTLE_INTERFACE/LogInterface.c:17 | НЕ підіймається портом | оригінал: core.Event "lll" (src/libs/battle_interface/src/log_and_action.cpp:50) |
| Події | ievent_SetGameTime | INTERFACE/interface.c:17 | НЕ підіймається портом | оригінал: core.Event "lll" (src/libs/xinterface/src/xinterface.cpp:3158) |
| Події | EventEndHelpChooser | INTERFACE/interface.c:1221 | скрипти підіймають самі, але оригінал підіймає теж — порт ні | оригінал: core.Event "s" (src/libs/xinterface/src/help_chooser/help_chooser.cpp:62); скрипти теж підіймають (INTERFACE/interface.c:1226) |
| Події | evntOptionsBreak | INTERFACE/mainmenu.c:353, INTERFACE/interface.c:25 | скрипти підіймають самі, але оригінал підіймає теж — порт ні | оригінал: core.Event "" (src/libs/xinterface/src/xinterface.cpp:3273); скрипти теж підіймають (INTERFACE/interface.c:1684) |
| Події | LocatorsEventTrace | Locations/locators_test.c:413 | НЕ підіймається портом | оригінал: core.Event "lsss" (src/libs/location/src/location.cpp:1137); лише налагоджувальні скрипти |
| Події | TeleportChoose | teleport.c:14 | НЕ підіймається портом | оригінал: core.Event "l" (src/libs/teleport/src/teleport.cpp:158); лише налагоджувальні скрипти |
| Події | TeleportStart | teleport.c:15 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/teleport/src/teleport.cpp:114); лише налагоджувальні скрипти |

## Ресурси — розбіжності (10)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Ресурси | texture: ships\flagall.tga |  | не завантажується портом | 14× у game/smoke_board.log.err, game/smoke_chase.log.err, game/smoke_door.log.err |
| Ресурси | atlas picture:  (group ) |  | не завантажується портом | 11× у game/smoke_door.log, game/smoke_trade.log, reports/2026-11-04_21-06-24/log_tail.txt |
| Ресурси | atlas picture:  (group ICONS) |  | не завантажується портом | 10× у game/smoke_door.log, game/smoke_hold.log, game/smoke_rename.log |
| Ресурси | texture: ships\sail_rope.tga |  | не завантажується портом | 8× у game/smoke_board.log.err, game/smoke_door.log.err, game/smoke_fight.log.err |
| Ресурси | texture: ships\vant.tga | SEA_AI/AIShip.c:43 | не завантажується портом | 8× у game/smoke_board.log.err, game/smoke_door.log.err, game/smoke_fight.log.err |
| Ресурси | atlas picture:  (group GOODS) |  | не завантажується портом | 3× у reports/2026-11-05_08-49-52/log_tail.txt, reports/2026-11-05_08-49-54/log_tail.txt, reports/2026-11-05_08-51-31/log_tail.txt |
| Ресурси | sound: poorman | Characters/init/FalaiseDeFleur.c:549, Characters/init/Redmond.c:470, Characters/init/Santiago.c:472 | не завантажується портом | 3× у reports/2026-11-05_08-46-25/log_tail.txt, reports/2026-11-05_08-49-52/log_tail.txt, reports/2026-11-05_08-49-54/log_tail.txt |
| Ресурси | sound: waitress_brm | Loc_ai/types/LAi_waitress.c:149 | не завантажується портом | 3× у reports/2026-11-05_08-49-52/log_tail.txt, reports/2026-11-05_08-49-54/log_tail.txt, reports/2026-11-05_08-51-31/log_tail.txt |
| Ресурси | atlas picture: Fearless (group SHIPS16) |  | не завантажується портом | 1× у game/smoke_board.log |
| Ресурси | atlas picture: Fearless (group Ships16) |  | не завантажується портом | 1× у game/smoke_board.log |

## Ресурси: ini — розбіжності (7)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Ресурси: ini | RESOURCE/INI/TextureSequence.ini |  | порт не читає цей файл | оригінал читає (reference); ні назви, ні теки в коді порту |
| Ресурси: ini | RESOURCE/INI/animals.ini |  | порт не читає цей файл | оригінал читає (reference); ні назви, ні теки в коді порту |
| Ресурси: ini | RESOURCE/INI/dialog.ini |  | порт не читає цей файл | оригінал читає (reference); ні назви, ні теки в коді порту |
| Ресурси: ini | RESOURCE/INI/fonts.ini |  | порт не читає цей файл | оригінал читає (reference); ні назви, ні теки в коді порту |
| Ресурси: ini | RESOURCE/INI/helpchooser.ini |  | порт не читає цей файл | оригінал читає (reference); ні назви, ні теки в коді порту |
| Ресурси: ini | RESOURCE/INI/loclighter.ini |  | порт не читає цей файл | оригінал читає (reference); ні назви, ні теки в коді порту |
| Ресурси: ini | RESOURCE/INI/particles.ini |  | порт не читає цей файл | оригінал читає (reference); ні назви, ні теки в коді порту |

## Інформаційне (оригінал теж ігнорує, ціль не визначено, події-дані, не спрацювали в прогонах)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Сутності | scrshoter | INTERFACE/interface.c:637 | немає реалізації; скрипти шлють йому 2 різних повідомлень (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(SCRSHOTER) |
| Повідомлення | Animals → MSG_ANIMALS_BUTTERFLIES_HIDE (77109) fmt "l" | animals.c:19, animals.c:69 | немає EntityHandlers["Animals"] (оригінал теж ігнорує) | оригінал: НЕ обробляє (src/libs/animals/src/animals.cpp:35); [nohandler] 47× у логах |
| Повідомлення | Animals → MSG_ANIMALS_SEAGULLS_ADD (77103) fmt "lfff" | animals.c:41, animals.c:80 | немає EntityHandlers["Animals"] (оригінал теж ігнорує) | оригінал: НЕ обробляє (src/libs/animals/src/animals.cpp:35); [nohandler] 3× у логах |
| Повідомлення | Animals → MSG_ANIMALS_SEAGULLS_HIDE (77101) fmt "l" | animals.c:21, animals.c:71 | немає EntityHandlers["Animals"] (оригінал теж ігнорує) | оригінал: НЕ обробляє (src/libs/animals/src/animals.cpp:35); [nohandler] 47× у логах |
| Повідомлення | Animals → MSG_ANIMALS_SEAGULLS_SHOW (77100) fmt "l" | animals.c:32, animals.c:79 | немає EntityHandlers["Animals"] (оригінал теж ігнорує) | оригінал: НЕ обробляє (src/libs/animals/src/animals.cpp:35); [nohandler] 4× у логах |
| Повідомлення | Animals → MSG_ANIMALS_FISHSCHOOLS_HIDE (77107) fmt "l" | animals.c:20 | немає EntityHandlers["Animals"] (оригінал теж ігнорує) | оригінал: НЕ обробляє (src/libs/animals/src/animals.cpp:35); [nohandler] 28× у логах |
| Повідомлення | Animals → MSG_ANIMALS_FISHSCHOOLS_SHOW (77106) fmt "l" | animals.c:70 | немає EntityHandlers["Animals"] (оригінал теж ігнорує) | оригінал: НЕ обробляє (src/libs/animals/src/animals.cpp:35); [nohandler] 19× у логах |
| Події | InterfaceKey | seadogs.c:68 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | blieGetMsgIconRoot | BATTLE_INTERFACE/LogInterface.c:13 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | BI_CheckSSShow | BATTLE_INTERFACE/BattleInterface.c:90 | не знайдено; ім'я використовується скриптами як дані (BATTLE_INTERFACE/msg_control.h:46) — можливо, скрипт-подія | у reference не підіймається |
| Події | wdmCreateMap | QUESTS/quests.c:16 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | Ball_FlyUpdate | SEA_AI/AIBalls.c:47 | не знайдено; ім'я використовується скриптами як дані (SEA_AI/Script_Defines.h:27) — можливо, скрипт-подія | у reference не підіймається |
| Події | SeaCameras_Switch | SEA_AI/AICameras.c:81 | не знайдено; ім'я використовується скриптами як дані (SEA_AI/AICameras.c:13) — можливо, скрипт-подія | у reference не підіймається |
| Події | ShpChCharge | SEA_AI/AIShip.c:102 | не знайдено; ім'я використовується скриптами як дані (SEA_AI/Script_Defines.h:48) — можливо, скрипт-подія | у reference не підіймається |
| Сутності | CharacterAnimationKipper | reload.c:70, reload.c:96 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(CharacterAnimationKipper) |
| Сутності | FINDFILESINTODIRECTORY | CheckDlgLoad.c:10, teleport.c:220 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(FINDFILESINTODIRECTORY) |
| Сутності | FREE_CAMERA | SEA_AI/AICameras.c:22 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(FREE_CAMERA) |
| Сутності | SeaLocatorShow | SEA_AI/sea.c:203 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(SeaLocatorShow) |
| Сутності | TMPTELEPORT | teleport.c:11 | немає реалізації; скрипти шлють йому 1 різних повідомлень (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(TMPTELEPORT); лише налагоджувальні скрипти |
| Сутності | islandED | gameutils.c:7 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: класу немає в reference |
| Сутності | obj_strservice | INTERFACE/BaseInterface.c:15, seadogs.c:403 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(OBJ_STRSERVICE) |
| Сутності | touch | SEA_AI/sea.c:186 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(TOUCH) |
| Повідомлення | Animals → MSG_ANIMALS_BUTTERFLIES_SHOW (77108) fmt "l" | animals.c:50 | немає EntityHandlers["Animals"] (оригінал теж ігнорує) | оригінал: НЕ обробляє (src/libs/animals/src/animals.cpp:35) |
| Події | CriptData | INTERFACE/mainmenu.c:9 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | EquipPress | INTERFACE/items.c:37 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | EventGetSpecialMode | Locations/locations_camera.c:80 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | MainMenuKey | seadogs.c:67 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | QuestSceneCommand | QUESTS/quests_scenes.c:9 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | eventAllarm | sound.c:486 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | evntCheckFailure | INTERFACE/mainmenu.c:10 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | ievent_GameOver | INTERFACE/interface.c:16 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | AbordageCanBe | SEA_AI/AIAbordage.c:19 | не знайдено; ім'я використовується скриптами як дані (SEA_AI/Script_Defines.h:14) — можливо, скрипт-подія | у reference не підіймається |
| Події | EventCharacterGroupDead | QUESTS/quests.c:15 | не знайдено; ім'я використовується скриптами як дані (Loc_ai/LAi_defines.c:76) — можливо, скрипт-подія | у reference не підіймається |
| Події | GetCannonByTypeEvent | CANNONS/Cannons.c:13 | не знайдено; ім'я використовується скриптами як дані (Events.h:8) — можливо, скрипт-подія | у reference не підіймається |
| Події | NewGameEvent | seadogs.c:64 | не знайдено; ім'я використовується скриптами як дані (Events.h:14) — можливо, скрипт-подія | у reference не підіймається |
| Події | PassengerDown | INTERFACE/passengers.c:69 | не знайдено; ім'я використовується скриптами як дані (INTERFACE/passengers.c:69) — можливо, скрипт-подія | у reference не підіймається |
| Події | startAfterWaitScene | QUESTS/quests_scenes.c:10 | не знайдено; ім'я використовується скриптами як дані (QUESTS/quests_scenes.c:10) — можливо, скрипт-подія | у reference не підіймається |
| Події: runtime | BI_GetChargeQuantity | BATTLE_INTERFACE/BattleInterface.c:84 | жодного разу в регресійних прогонах | порт: game/SeaCommands.cs:210; [events] топ-40 у 20 логах |
| Події: runtime | BI_GetFortRelation | BATTLE_INTERFACE/BattleInterface.c:92 | жодного разу в регресійних прогонах | порт: game/BattleInterface.cs:99; [events] топ-40 у 20 логах |
| Події: runtime | BI_VISIBLE | BATTLE_INTERFACE/ispyglass.c:17 | жодного разу в регресійних прогонах | порт: game/SeaTelescope.cs:88; [events] топ-40 у 20 логах |
| Події: runtime | BI_Visible | BATTLE_INTERFACE/BattleInterface.c:87, BATTLE_INTERFACE/LogInterface.c:25 | жодного разу в регресійних прогонах | порт: game/SeaTelescope.cs:88; [events] топ-40 у 20 логах |
| Події: runtime | evntSetUsingAbility | BATTLE_INTERFACE/BattleInterface.c:97 | жодного разу в регресійних прогонах | порт: game/SeaCommands.cs:235; [events] топ-40 у 20 логах |
| Події: runtime | CannGFHeight | SEA_AI/AICannon.c:17 | жодного разу в регресійних прогонах | порт: game/SeaCombat.cs:240; [events] топ-40 у 20 логах |
| Події: runtime | FortCreate | SEA_AI/AIFort.c:27 | жодного разу в регресійних прогонах | порт: game/SeaForts.cs:43; [events] топ-40 у 20 логах |
| Події: runtime | Location_CharacterBusyPos | Loc_ai/LAi_init.c:32 | жодного разу в регресійних прогонах | порт: game/Characters.cs:838; [events] топ-40 у 20 логах |
| Події: runtime | Location_CharacterEscapeSlide | Loc_ai/LAi_init.c:42 | жодного разу в регресійних прогонах | порт: game/Characters.cs:861; [events] топ-40 у 20 логах |
| Події: runtime | Location_CharacterFire | Loc_ai/LAi_init.c:39 | жодного разу в регресійних прогонах | порт: game/Characters.cs:255; [events] топ-40 у 20 логах |
| Події: runtime | Location_CharacterFollowGo | Loc_ai/LAi_init.c:33 | жодного разу в регресійних прогонах | порт: game/Characters.cs:886; [events] топ-40 у 20 логах |
| Події: runtime | Location_CharacterIsFire | Loc_ai/LAi_init.c:40 | жодного разу в регресійних прогонах | порт: game/Characters.cs:160; [events] топ-40 у 20 логах |
| Події: runtime | Location_CharacterTaskFailure | Loc_ai/LAi_init.c:31 | жодного разу в регресійних прогонах | порт: game/Characters.cs:147; [events] топ-40 у 20 логах |
| Події: runtime | ShpDelete | BATTLE_INTERFACE/BattleInterface.c:79 | жодного разу в регресійних прогонах | порт: game/Sea.cs:548; [events] топ-40 у 20 логах |
| Події: runtime | ShpFireAction | SEA_AI/AIShip.c:105 | жодного разу в регресійних прогонах | порт: game/SeaAI.cs:112; [events] топ-40 у 20 логах |
| Події: runtime | Shp_ShipIslColl | SEA_AI/AIShip.c:92 | жодного разу в регресійних прогонах | порт: game/SeaGrounding.cs:91; [events] топ-40 у 20 логах |
| Події: runtime | WorldMap_DeleteShipEncounter | WorldMap/worldmap.c:72 | жодного разу в регресійних прогонах | порт: game/WorldEncounters.cs:135; [events] топ-40 у 20 логах |
| Події: runtime | WorldMap_EventWindowChangeSelectoin | WorldMap/worldmap.c:68 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:360; [events] топ-40 у 20 логах |
| Події: runtime | WorldMap_EventWindowSelect | WorldMap/worldmap.c:69 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:359; [events] топ-40 у 20 логах |
| Події: runtime | WorldMap_IsSkipEnable | WorldMap/worldmap_events.c:178 | жодного разу в регресійних прогонах | порт: game/WorldEncounters.cs:204; [events] топ-40 у 20 логах |
| Події: runtime | WorldMap_PlayerInStorm | WorldMap/worldmap.c:66 | жодного разу в регресійних прогонах | порт: game/WorldStorms.cs:241; [events] топ-40 у 20 логах |
| Події: runtime | WorldMap_ShipEncounter | WorldMap/worldmap.c:67 | жодного разу в регресійних прогонах | порт: game/WorldEncounters.cs:221; [events] топ-40 у 20 логах |
| Події: runtime | WorldMap_WaitMenuChangeSelectoin | WorldMap/worldmap.c:70 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:375; [events] топ-40 у 20 логах |
| Події: runtime | WorldMap_WaitMenuSelect | WorldMap/worldmap.c:71 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:371; [events] топ-40 у 20 логах |
| Події: runtime | WorldMap_outside_islandzone | WorldMap/worldmap.c:65 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:403; [events] топ-40 у 20 логах |
| Повідомлення (runtime) | <no entity> \| ls \| 30350 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 24812× у game/smoke_board.log, game/smoke_chase.log, game/smoke_chest.log |
| Повідомлення (runtime) | <no entity> \| lsl \| 30373 LockFightMode |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 24812× у game/smoke_board.log, game/smoke_chase.log, game/smoke_chest.log |
| Повідомлення (runtime) | <no entity> \| lsl \| 30373 SetFightWOWeapon |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 24812× у game/smoke_board.log, game/smoke_chase.log, game/smoke_chest.log |
| Повідомлення (runtime) | <no entity> \| sis \| MoveChr |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 44677× у game/smoke_board.log, game/smoke_chase.log, game/smoke_chest.log |
| Події: runtime | EventMastFall | BATTLE_INTERFACE/BattleInterface.c:52 | жодного разу в регресійних прогонах | порт: game/SeaSails.cs:426; [events] топ-40 у 20 логах |
| Події: runtime | ExitFromWorldMap | WorldMap/worldmap.c:74 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:412; [events] топ-40 у 20 логах |
| Події: runtime | FormatedTextActivate | INTERFACE/addon_team.c:77, INTERFACE/controls.c:62 | жодного разу в регресійних прогонах | порт: game/XInterface.cs:918; [events] топ-40 у 20 логах |
| Події: runtime | NextDay | QUESTS/quests.c:38, BATTLE_INTERFACE/BattleInterface.c:49 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:335; [events] топ-40 у 20 логах |
| Події: runtime | QuestActivate | INTERFACE/boal_questbook.c:33, INTERFACE/QuestBook.c:27 | жодного разу в регресійних прогонах | порт: game/XInterface.cs:863; [events] топ-40 у 20 логах |
| Події: runtime | ReleaseTelescopeInfo | BATTLE_INTERFACE/ispyglass.c:16 | жодного разу в регресійних прогонах | порт: game/SeaTelescope.cs:92; [events] топ-40 у 20 логах |
| Події: runtime | SetScrollerPos | INTERFACE/addon_team.c:78, INTERFACE/boal_questbook.c:35 | жодного разу в регресійних прогонах | порт: game/XInterface.cs:919; [events] топ-40 у 20 логах |
| Події: runtime | SetTelescopeInfo | BATTLE_INTERFACE/ispyglass.c:15 | жодного разу в регресійних прогонах | порт: game/SeaTelescope.cs:108; [events] топ-40 у 20 логах |
| Події: runtime | TelescopeActive | SEA_AI/AICameras.c:82 | жодного разу в регресійних прогонах | порт: game/SeaTelescope.cs:87; [events] топ-40 у 20 логах |
| Події: runtime | TornadoDamage | SEA_AI/AIShip.c:110 | жодного разу в регресійних прогонах | порт: game/WeatherFx.cs:130; [events] топ-40 у 20 логах |
| Події: runtime | evntCheckEnableLocator | BATTLE_INTERFACE/BattleInterface.c:98 | жодного разу в регресійних прогонах | порт: game/SeaCommands.cs:271; [events] топ-40 у 20 логах |
| Події: runtime | evntCheckEnableShip | BATTLE_INTERFACE/BattleInterface.c:99 | жодного разу в регресійних прогонах | порт: game/SeaCommands.cs:257; [events] топ-40 у 20 логах |
| Події: runtime | evntSave | INTERFACE/save_load.c:259, INTERFACE/save_load.c:846 | жодного разу в регресійних прогонах | порт: game/Main.cs:936; [events] топ-40 у 20 логах |
| Події: runtime | evntUpdateFormtText | INTERFACE/addon_team.c:81, INTERFACE/controls.c:63 | жодного разу в регресійних прогонах | порт: game/XInterface.cs:917; [events] топ-40 у 20 логах |
| Функції рушія | LayerAddObject() ×101 | animals.c:63, animals.c:64 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 3 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | LayerCreate() ×19 | dialog.c:115, dialog.c:181 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:208; у reference немає |
| Функції рушія | LayerDelObject() ×25 | INTERFACE/interface.c:1618, SEA_AI/AISea.c:28 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 2 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | LayerFreeze() ×67 | INTERFACE/interface.c:1158, INTERFACE/interface.c:1159 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 2 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | ReloadProgressEnd() ×12 | Loc_ai/LAi_boarding.c:509, Locations/locations.c:42 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 0 арг. (src/libs/location/src/location_script_lib.cpp:361); native-оголошення: native void ReloadProgressEnd() (Locations/locations.c:15) |
| Функції рушія | ReloadProgressStart() ×12 | Loc_ai/LAi_boarding.c:415, Locations/locations.c:39 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 0 арг. (src/libs/location/src/location_script_lib.cpp:349); native-оголошення: native void ReloadProgressStart() (Locations/locations.c:13) |
| Функції рушія | ReloadProgressUpdate() ×123 | Characters/characters.c:24, Characters/characters.c:30 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 0 арг. (src/libs/location/src/location_script_lib.cpp:355); native-оголошення: native void ReloadProgressUpdate() (Locations/locations.c:14) |
| Функції рушія | TavernWaitDate2() ×20 | DIALOGS/Antoine Lebretton_dialog.c:173, DIALOGS/Antoine Lebretton_dialog.c:187 | не визначена ні в скриптах, ні в оригінальному рушії — ймовірно дефект скриптів | у reference немає |
| Параметри подій | BI_GetData → BI_GetData() | BATTLE_INTERFACE/BattleInterface.c:89 | скрипт читає 3 значень, передається 2 (зайві читання умовні) (порт = формат reference) | скрипт читає "lll" (BATTLE_INTERFACE/BattleInterface.c:1087); порт передає "ll" (game/BattleInterface.cs:75); оригінал "ll" |
| Повідомлення | ? → "AddModel" fmt "ssi" | SEA_AI/AIFort.c:110, SEA_AI/sea.c:560 | клас цілі не визначено статично |  |
| Повідомлення | ? → "CheckID" fmt "ss" | Loc_ai/LAi_utils.c:588 | клас цілі не визначено статично |  |
| Повідомлення | ? → "LightPath" fmt "ss" | SEA_AI/sea.c:547 | клас цілі не визначено статично |  |
| Повідомлення | ? → "ModelsPath" fmt "ss" | SEA_AI/sea.c:546 | клас цілі не визначено статично |  |
| Події | Cheat | seadogs.c:70 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Функції рушія | LayerSetRealize() ×6 | dialog.c:116, dialog.c:182 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 1 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Параметри подій | FormatedTextActivate → ProcHelpSectionActivate() | INTERFACE/helpscreen.c:30 | #1: string замість int (порт = формат reference) | скрипт читає "l" (INTERFACE/helpscreen.c:236); порт передає "sl" (game/XInterface.cs:918); оригінал "sl" |
| Повідомлення (runtime) | <no entity> \| ls \| 30373 IsFightMode |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 88× у game/smoke_intro.log |
| Функції рушія | GetNPCInformation() ×4 | DIALOGS/Docker Generated_dialog.c:153, DIALOGS/Docker Generated_dialog.c:162 | не визначена ні в скриптах, ні в оригінальному рушії — ймовірно дефект скриптів | у reference немає |
| Функції рушія | Sea_TurnRateMagicNumber() ×1 | SEA_AI/AIShip.c:2103 | не визначена ні в скриптах, ні в оригінальному рушії — ймовірно дефект скриптів | у reference немає |
| Повідомлення (runtime) | <no entity> \| ssf \| SetGroupHear |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 44× у game/smoke_board.log, game/smoke_chase.log, game/smoke_chest.log |
| Повідомлення (runtime) | <no entity> \| ssf \| SetGroupLook |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 44× у game/smoke_board.log, game/smoke_chase.log, game/smoke_chest.log |
| Повідомлення (runtime) | <no entity> \| ssf \| SetGroupSay |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 44× у game/smoke_board.log, game/smoke_chase.log, game/smoke_chest.log |
| Повідомлення (runtime) | <no entity> \| ss \| RegistryGroup |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 42× у game/smoke_board.log, game/smoke_chase.log, game/smoke_chest.log |
| Повідомлення (runtime) | <no entity> \| lllas \| 51011 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 33× у game/smoke_intro.log |
| Повідомлення (runtime) | <no entity> \| ssi \| AddModel |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 25× у game/smoke_board.log, game/smoke_chase.log, game/smoke_door.log |
| Функції рушія | EntityUpdate() ×2 | SEA_AI/AIBalls.c:55, SEA_AI/AIBalls.c:78 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 1 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | LayerSetExecute() ×2 | gameutils.c:6, SEA_AI/sea.c:174 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 1 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | RandSum() ×2 | DIALOGS/Henry_dialog.c:164, DIALOGS/Henry_dialog.c:346 | не визначена ні в скриптах, ні в оригінальному рушії — ймовірно дефект скриптів | у reference немає |
| Функції рушія | SetReloadNextTipsImage() ×2 | reload.c:169, WorldMap/worldmap_reload.c:8 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 0 арг. (src/libs/location/src/location_script_lib.cpp:343); native-оголошення: native void SetReloadNextTipsImage() (Locations/locations.c:19) |
| Повідомлення (runtime) | <no entity> \| lisffffff \| 77400 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 23× у game/smoke_board.log, game/smoke_chase.log, game/smoke_fight.log |
| Повідомлення (runtime) | <no entity> \| l \| 10006 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 21× у game/smoke_board.log, game/smoke_chase.log, game/smoke_chest.log |
| Повідомлення (runtime) | <no entity> \| l \| 51025 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 21× у game/smoke_board.log, game/smoke_chase.log, game/smoke_chest.log |
| Повідомлення (runtime) | <no entity> \| lll \| 77004 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 20× у game/smoke_board.log, game/smoke_chase.log, game/smoke_chest.log |
| Повідомлення (runtime) | <no entity> \| ss \| LightPath |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 19× у game/smoke_board.log, game/smoke_chase.log, game/smoke_door.log |
| Повідомлення (runtime) | <no entity> \| ss \| ModelsPath |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 19× у game/smoke_board.log, game/smoke_chase.log, game/smoke_door.log |
| Функції рушія | RDTSC_B() ×1 | SEA_AI/sea.c:441 | stub: returns 0 (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:89; оригінал: 0 арг. (src/libs/script_library/src/vano_files.cpp:118); native-оголошення: native int RDTSC_B() (seadogs.c:58) |
| Функції рушія | RDTSC_E() ×1 | SEA_AI/sea.c:852 | stub: returns 0 (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:89; оригінал: 1 арг. (src/libs/script_library/src/vano_files.cpp:124); native-оголошення: native int RDTSC_E(int iRDTSC) (seadogs.c:59) |
| Функції рушія | SetReloadProgressBackImage() ×1 | Locations/locations.c:38 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:208; оригінал: 1 арг. (src/libs/location/src/location_script_lib.cpp:337); native-оголошення: native void SetReloadProgressBackImage(string imageName) (Locations/locations.c:12) |
| Повідомлення (runtime) | TELESCOPE \| lsflll \| 77302 eye_BadTub.tga |  | застарілий лог: обробник класу вже є (game/SeaTelescope.cs:41) | [nohandler] 6× у game/smoke_chase.log, game/smoke_neutralA.log, game/smoke_neutralB.log |
| Повідомлення (runtime) | <no entity> \| ssss \| SetRelation |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 5× у game/smoke_neutralB.log |
| Повідомлення (runtime) | <no entity> \| l \| 50102 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 4× у game/smoke_chest.log, game/smoke_intro.log, game/smoke_landhud.log |
| Повідомлення (runtime) | <no entity> \| sssss \| SetAlarmReaction |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 3× у game/smoke_neutralB.log |
| Повідомлення (runtime) | <no entity> \| llllll \| 46013 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 2× у game/smoke_board.log |
| Повідомлення (runtime) | <no entity> \| ssl \| SetGroupPriority |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 2× у game/smoke_neutralB.log |
| Параметри подій: формат | CannFire | SEA_AI/AICannon.c:18 | порт передає "afffffff", reference "afffffffff" — але обробники PotC-скриптів читають саме те, що дає порт (різниця версій рушія) | порт: game/SeaCombat.cs:205; оригінал: src/libs/sea_ai/src/ai_cannon.cpp:149 |
| Параметри подій: формат | GetRiggingData | BATTLE_INTERFACE/BattleInterface.c:51 | порт передає "sll", reference "slll\|sllla" — але обробники PotC-скриптів читають саме те, що дає порт (різниця версій рушія) | порт: game/Sea.cs:103; оригінал: src/libs/rigging/src/flag.cpp:858 |
| Параметри подій: формат | Location_CharacterAttack | Loc_ai/LAi_init.c:37 | порт передає "aaff", reference "iislll" — але обробники PotC-скриптів читають саме те, що дає порт (різниця версій рушія) | порт: game/Characters.cs:941; оригінал: src/libs/location/src/character.cpp:5097 |
| Параметри подій: формат | Location_CharacterEntryToLocator | BATTLE_INTERFACE/LandInterface.c:77, Characters/characters_events.c:17 | порт передає "aass", reference "iissf" — але обробники PotC-скриптів читають саме те, що дає порт (різниця версій рушія) | порт: game/Characters.cs:715; оригінал: src/libs/location/src/character.cpp:245 |
| Параметри подій: формат | ShpDelete | BATTLE_INTERFACE/BattleInterface.c:79 | порт передає "l", reference "li" — але обробники PotC-скриптів читають саме те, що дає порт (різниця версій рушія) | порт: game/Sea.cs:548; оригінал: src/libs/ship/src/ship.cpp:753 |
| Параметри подій: формат | ShpSailDamage | SEA_AI/AIShip.c:94 | порт передає "lfff", reference "lfffl" — але обробники PotC-скриптів читають саме те, що дає порт (різниця версій рушія) | порт: game/SeaSails.cs:404; оригінал: src/libs/rigging/src/sail.cpp:1659 |
| Ресурси: ini | RESOURCE/INI/EURO/fonts.ini |  | порт не читає; оригінал (reference) теж не читає за цим шляхом | ні назви, ні теки в коді порту; скрипти не передають |
| Ресурси: ini | RESOURCE/INI/camera.ini |  | порт не читає; оригінал (reference) теж не читає за цим шляхом | ні назви, ні теки в коді порту; скрипти не передають |
| Ресурси: ini | RESOURCE/INI/fonts_boal.ini |  | порт не читає; оригінал (reference) теж не читає за цим шляхом | ні назви, ні теки в коді порту; скрипти не передають |
| Ресурси: ini | RESOURCE/INI/fonts_eng.ini |  | порт не читає; оригінал (reference) теж не читає за цим шляхом | ні назви, ні теки в коді порту; скрипти не передають |
| Ресурси: ini | RESOURCE/INI/fonts_rus.ini |  | порт не читає; оригінал (reference) теж не читає за цим шляхом | ні назви, ні теки в коді порту; скрипти не передають |
| Ресурси: ini | RESOURCE/INI/geo.ini |  | порт не читає; оригінал (reference) теж не читає за цим шляхом | ні назви, ні теки в коді порту; скрипти не передають |
| Повідомлення (runtime) | <no entity> \| l \| 46005 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 1× у game/smoke_board.log |
| Повідомлення (runtime) | <no entity> \| ls \| 30400 None |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 1× у game/smoke_intro.log |
| Повідомлення (runtime) | <no entity> \| s \| ClearAllTargets |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 1× у game/smoke_neutralB.log |
| Сутності | FINDDIALOGNODES | teleport.c:231 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(FINDDIALOGNODES); лише налагоджувальні скрипти |
| Сутності | dummy | lcorrect.c:5, test.c:11 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: класу немає в reference; лише налагоджувальні скрипти |
| Сутності | env | lcorrect.c:4 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: класу немає в reference; лише налагоджувальні скрипти |
| Сутності | scriptscreen | StartInterface.c:120 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: класу немає в reference; лише налагоджувальні скрипти |
| Події | QuickTeleport | teleport.c:4 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається; лише налагоджувальні скрипти |
| Події | key_event | StartInterface.c:54 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається; лише налагоджувальні скрипти |
| Функції рушія | InitBaseShipsTypes() ×2 | InitInterface.c:33, StartInterface.c:36 | extern без визначення в жодному скрипті (мертвий код скриптів) | у reference немає; лише налагоджувальні скрипти |
| Функції рушія | InitCharacters() ×2 | InitInterface.c:37, StartInterface.c:40 | extern без визначення в жодному скрипті (мертвий код скриптів) | у reference немає; лише налагоджувальні скрипти |
| Функції рушія | InitPlayer() ×1 | InitInterface.c:41 | extern без визначення в жодному скрипті (мертвий код скриптів) | у reference немає; лише налагоджувальні скрипти |
