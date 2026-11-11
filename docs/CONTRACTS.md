# Контракти рушія: скрипти ↔ порт (автоматичний аудит)

_Згенеровано `python tools/contract_audit.py` 2026-11-11 15:02. Не редагувати вручну — перезапустити (`run_tests.ps1 -Contracts`)._

Джерела: скрипти `addon/merged/PROGRAM` (1139 файлів, 1154 #define, 4137 функцій), порт `StormScript/Runtime` + `game/*.cs` (100 файлів), reference `reference/storm-engine` (95 класів, 219 подій, 159 native), логи: 179 (`game/smoke_*.log`, `reports/*/log_tail.txt`).

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
| Повідомлення | 252 | 16 | 2 | 4 |
| Повідомлення (runtime) | 0 | 1 | 0 | 32 |
| Сутності | 51 | 3 | 0 | 11 |
| Функції рушія | 87 | 5 | 3 | 20 |
| Події | 203 | 21 | 2 | 24 |
| Параметри подій: формат | 117 | 0 | 0 | 6 |
| Параметри подій | 253 | 0 | 0 | 3 |
| Події: runtime | 0 | 0 | 0 | 40 |
| Ресурси | 0 | 15 | 0 | 0 |
| Ресурси: ini | 85 | 5 | 2 | 5 |

## Топ-40 найпомітніших для гравця розбіжностей
| # | Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|---|
| 1 | Сутності | HELPCHOOSER | INTERFACE/interface.c:1223 | немає реалізації; скрипти шлють йому 1 різних повідомлень | оригінал: CREATE_CLASS(HELPCHOOSER) |
| 2 | Події | ievntGetHelpTexture | INTERFACE/ship.c:154, INTERFACE/store.c:65 | НЕ підіймається портом | оригінал: core.Event "s" (src/libs/xinterface/src/xinterface.cpp:3301) |
| 3 | Повідомлення | Island → MSG_ISLAND_START (50102) fmt "l" | SEA_AI/AIIsland.c:3 | код 50102 не знайдено в обробнику | оригінал: обробляє (src/libs/island/src/island.cpp:238); порт: game/Sea.cs:115 |
| 4 | Повідомлення | Island → MSG_LOCATION_ADD_MODEL (30100) fmt "liss" | Locations/locations_loader.c:674 | код 30100 не знайдено в обробнику | оригінал: обробляє (src/libs/island/src/island.cpp:238); порт: game/Sea.cs:115 |
| 5 | Повідомлення | location → MSG_LOCATION_HIDELGROUP (30181) fmt "ls" | Locations/locations.c:86 | код 30181 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:284; клас визначено за кодом (reference) |
| 6 | Повідомлення | location → MSG_LOCATION_VIEWLGROUP (30180) fmt "lsffl" | Locations/locations.c:79 | код 30180 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:284; клас визначено за кодом (reference) |
| 7 | Повідомлення | ship → AI_MESSAGE_SET_LAYERS (51015) fmt "lss" | SEA_AI/AISea.c:6 | код 51015 не знайдено в обробнику | оригінал: обробляє (src/libs/ship/src/ship.cpp:1210); порт: game/Sea.cs:111; клас визначено за кодом (reference) |
| 8 | Повідомлення | xinterface → MSG_INTERFACE_CHECK_AGAIN (45057) fmt "l" | INTERFACE/mainmenu.c:407 | код 45057 не знайдено в обробнику | оригінал: обробляє (src/libs/xinterface/src/xinterface.cpp:455); порт: game/Main.cs:246 |
| 9 | Повідомлення | xinterface → MSG_INTERFACE_LAUNCH_DASHBOARD (45054) fmt "l" | INTERFACE/interface.c:1669 | код 45054 не знайдено в обробнику | оригінал: обробляє (src/libs/xinterface/src/xinterface.cpp:455); порт: game/Main.cs:246 |
| 10 | Повідомлення | HELPCHOOSER → MSG_HELPCHOOSER_START (45200) fmt "ls" | INTERFACE/interface.c:1229 | немає EntityHandlers["HELPCHOOSER"] | оригінал: обробляє (src/libs/xinterface/src/help_chooser/help_chooser.cpp:180) |
| 11 | Повідомлення | sea_operator → MSG_SEA_OPERATOR_BALL_UPDATE (77401) fmt "lalffffff" | SEA_AI/AIBalls.c:166 | немає EntityHandlers["sea_operator"] | оригінал: обробляє (src/libs/sea_operator/src/sea_operator.cpp:51); клас визначено за кодом (reference) |
| 12 | Повідомлення | sea_operator → MSG_SEA_OPERATOR_FIRE (77400) fmt "lisffffff" | SEA_AI/shipBortFire.c:45 | немає EntityHandlers["sea_operator"] | оригінал: обробляє (src/libs/sea_operator/src/sea_operator.cpp:51); клас визначено за кодом (reference) |
| 13 | Події | Location_CharacterInLocator | Characters/characters_events.c:18 | НЕ підіймається портом | оригінал: core.Event "iissff" (src/libs/location/src/character.cpp:232) |
| 14 | Події | Location_CharacterSGFire | Loc_ai/LAi_fightparams.c:672 | НЕ підіймається портом | оригінал: core.Event "iif" (src/libs/location/src/player.cpp:812) |
| 15 | Події | SeaGetLayers | SEA_AI/AIShip.c:88 | НЕ підіймається портом | оригінал: core.Event "i" (src/libs/ship/src/ship.cpp:1438) |
| 16 | Події | EWhr_GetFogDensity | Weather/WhrWeather.c:41 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/shadow/src/shadow.cpp:150) |
| 17 | Події | EWhr_GetShadowDensity | Weather/WhrWeather.c:40 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/shadow/src/shadow.cpp:143) |
| 18 | Події | EWhr_GetWindAngle | Weather/WhrWeather.c:38 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/location/src/grass.cpp:323) |
| 19 | Події | EWhr_GetWindSpeed | Weather/WhrWeather.c:39 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/location/src/grass.cpp:343) |
| 20 | Події | WhrCalcFogColor | SEA_AI/Cabin.c:299, Weather/WhrWeather.c:225 | НЕ підіймається портом | оригінал: core.Event "fff" (src/libs/weather/src/sky.cpp:26) |
| 21 | Повідомлення | MODELR → MSG_MODEL_RESTORE (20505) fmt "l" | SEA_AI/AIAbordage.c:70, SEA_AI/AIAbordage.c:74 (+2) | код 20505 не знайдено в обробнику | оригінал: обробляє (src/libs/model/src/model.cpp:300); порт: game/Main.cs:355 |
| 22 | Повідомлення | MODELR → MSG_MODEL_SET_LIGHT_PATH (20503) fmt "ls" | SEA_AI/AIFort.c:106, SEA_AI/sea.c:557 | код 20503 не знайдено в обробнику | оригінал: обробляє (src/libs/model/src/model.cpp:300); порт: game/Main.cs:355 |
| 23 | Події | DelInterfaceTexture | INTERFACE/save_load.c:24 | НЕ підіймається портом | оригінал: core.Event "s\|ss" (src/libs/xinterface/src/nodes/xi_scroll_image.cpp:815) |
| 24 | Події | DisableScroll | INTERFACE/helpscreen.c:29 | НЕ підіймається портом | оригінал: core.Event "sl" (src/libs/xinterface/src/nodes/xi_formt_text.cpp:926) |
| 25 | Події | EmergencyDialogExit | dialog.c:9 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/dialog/src/dialog.cpp:925) |
| 26 | Події | EventSGMode | Loc_ai/LAi_utils.c:706 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/location/src/player.cpp:82) |
| 27 | Події | GetInterfaceTexture | INTERFACE/save_load.c:23 | НЕ підіймається портом | оригінал: core.Event "sl\|sls" (src/libs/xinterface/src/nodes/xi_scroll_image.cpp:677) |
| 28 | Події | GetQuestTextFileName | INTERFACE/BaseInterface.c:6 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/xinterface/src/xinterface.cpp:214) |
| 29 | Події | ISound | INTERFACE/interface.c:1034 | НЕ підіймається портом | оригінал: core.Event "l" (src/libs/xinterface/src/back_scene/back_scene.cpp:261) |
| 30 | Події | LocationTimeUpdate | Locations/locations_loader.c:939 | НЕ підіймається портом | оригінал: core.Event "f" (src/libs/location/src/location.cpp:144) |
| 31 | Події | SetWindowSize | BATTLE_INTERFACE/LogInterface.c:17 | НЕ підіймається портом | оригінал: core.Event "lll" (src/libs/battle_interface/src/log_and_action.cpp:50) |
| 32 | Повідомлення | MODELR → MSG_MODEL_SET_TECHNIQUE (20510) fmt "ls" | ITEMS/itemLogic.c:24 | код 20510 не знайдено в обробнику | оригінал: обробляє (src/libs/model/src/model.cpp:300); порт: game/Main.cs:355 |
| 33 | Повідомлення | TELESCOPE → MSG_TELESCOPE_INIT_ARRAYS (77300) fmt "leee" | SEA_AI/sea.c:846 | код 77300 приймається без дії (згаданий лише в коментарі обробника) | оригінал: класу немає в reference; порт: game/SeaTelescope.cs:43; [nohandler] 6× у логах |
| 34 | Функції рушія | CreateClass() ×5 | gameutils.c:7, lcorrect.c:5 (+3) | немає в порті (Builtins/HostFunctions) | оригінал: 1 арг. (src/libs/core/src/internal_functions.cpp:118); у логах не траплялась |
| 35 | Функції рушія | IDoExit() |  | unresolved під час прогону | 1× у reports/2026-11-07_16-09-00/log_tail.txt |
| 36 | Функції рушія | InitInterface() |  | unresolved під час прогону | 1× у reports/2026-11-05_08-51-31/log_tail.txt |
| 37 | Функції рушія | GlobalLngFileID() ×1 | INTERFACE/utilite.c:376 | немає в порті (Builtins/HostFunctions) | оригінал: 0 арг. (src/libs/xinterface/src/string_service/str_service.cpp:1615); native-оголошення: native int GlobalLngFileID() (INTERFACE/utilite.c:7); у логах не траплялась |
| 38 | Повідомлення | location → MSG_LOCATION_EX_MSG (30155) "TestLocatorsGroup" fmt "lss" | Locations/locators_test.c:311, Locations/locators_test.c:316 (+8) | підкоманду "TestLocatorsGroup" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:284; лише налагоджувальні скрипти |
| 39 | Повідомлення | scrshoter → MSG_SCRSHOT_RELEASE (45044) fmt "ls" | INTERFACE/save_load.c:693 | код 45044 не знайдено в обробнику | оригінал: обробляє (src/libs/xinterface/src/scr_shoter/scr_shoter.cpp:242); порт: game/Main.cs:270; клас визначено за кодом (reference) |
| 40 | Сутності | InfoHandler | INTERFACE/interface.c:1575 | немає реалізації; повідомлень йому скрипти не шлють | оригінал: CREATE_CLASS(InfoHandler) |

## Повідомлення — розбіжності (18)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Повідомлення | Island → MSG_ISLAND_START (50102) fmt "l" | SEA_AI/AIIsland.c:3 | код 50102 не знайдено в обробнику | оригінал: обробляє (src/libs/island/src/island.cpp:238); порт: game/Sea.cs:115 |
| Повідомлення | Island → MSG_LOCATION_ADD_MODEL (30100) fmt "liss" | Locations/locations_loader.c:674 | код 30100 не знайдено в обробнику | оригінал: обробляє (src/libs/island/src/island.cpp:238); порт: game/Sea.cs:115 |
| Повідомлення | location → MSG_LOCATION_HIDELGROUP (30181) fmt "ls" | Locations/locations.c:86 | код 30181 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:284; клас визначено за кодом (reference) |
| Повідомлення | location → MSG_LOCATION_VIEWLGROUP (30180) fmt "lsffl" | Locations/locations.c:79 | код 30180 не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:284; клас визначено за кодом (reference) |
| Повідомлення | ship → AI_MESSAGE_SET_LAYERS (51015) fmt "lss" | SEA_AI/AISea.c:6 | код 51015 не знайдено в обробнику | оригінал: обробляє (src/libs/ship/src/ship.cpp:1210); порт: game/Sea.cs:111; клас визначено за кодом (reference) |
| Повідомлення | xinterface → MSG_INTERFACE_CHECK_AGAIN (45057) fmt "l" | INTERFACE/mainmenu.c:407 | код 45057 не знайдено в обробнику | оригінал: обробляє (src/libs/xinterface/src/xinterface.cpp:455); порт: game/Main.cs:246 |
| Повідомлення | xinterface → MSG_INTERFACE_LAUNCH_DASHBOARD (45054) fmt "l" | INTERFACE/interface.c:1669 | код 45054 не знайдено в обробнику | оригінал: обробляє (src/libs/xinterface/src/xinterface.cpp:455); порт: game/Main.cs:246 |
| Повідомлення | HELPCHOOSER → MSG_HELPCHOOSER_START (45200) fmt "ls" | INTERFACE/interface.c:1229 | немає EntityHandlers["HELPCHOOSER"] | оригінал: обробляє (src/libs/xinterface/src/help_chooser/help_chooser.cpp:180) |
| Повідомлення | sea_operator → MSG_SEA_OPERATOR_BALL_UPDATE (77401) fmt "lalffffff" | SEA_AI/AIBalls.c:166 | немає EntityHandlers["sea_operator"] | оригінал: обробляє (src/libs/sea_operator/src/sea_operator.cpp:51); клас визначено за кодом (reference) |
| Повідомлення | sea_operator → MSG_SEA_OPERATOR_FIRE (77400) fmt "lisffffff" | SEA_AI/shipBortFire.c:45 | немає EntityHandlers["sea_operator"] | оригінал: обробляє (src/libs/sea_operator/src/sea_operator.cpp:51); клас визначено за кодом (reference) |
| Повідомлення | MODELR → MSG_MODEL_RESTORE (20505) fmt "l" | SEA_AI/AIAbordage.c:70, SEA_AI/AIAbordage.c:74, SEA_AI/Cabin.c:252 (+1) | код 20505 не знайдено в обробнику | оригінал: обробляє (src/libs/model/src/model.cpp:300); порт: game/Main.cs:355 |
| Повідомлення | MODELR → MSG_MODEL_SET_LIGHT_PATH (20503) fmt "ls" | SEA_AI/AIFort.c:106, SEA_AI/sea.c:557 | код 20503 не знайдено в обробнику | оригінал: обробляє (src/libs/model/src/model.cpp:300); порт: game/Main.cs:355 |
| Повідомлення | MODELR → MSG_MODEL_SET_TECHNIQUE (20510) fmt "ls" | ITEMS/itemLogic.c:24 | код 20510 не знайдено в обробнику | оригінал: обробляє (src/libs/model/src/model.cpp:300); порт: game/Main.cs:355 |
| Повідомлення | TELESCOPE → MSG_TELESCOPE_INIT_ARRAYS (77300) fmt "leee" | SEA_AI/sea.c:846 | код 77300 приймається без дії (згаданий лише в коментарі обробника) | оригінал: класу немає в reference; порт: game/SeaTelescope.cs:43; [nohandler] 6× у логах |
| Повідомлення | location → MSG_LOCATION_EX_MSG (30155) "TestLocatorsGroup" fmt "lss" | Locations/locators_test.c:311, Locations/locators_test.c:316, Locations/locators_test.c:321 (+7) | підкоманду "TestLocatorsGroup" не знайдено в обробнику | оригінал: обробляє (src/libs/location/src/location.cpp:233); порт: game/Main.cs:284; лише налагоджувальні скрипти |
| Повідомлення | scrshoter → MSG_SCRSHOT_RELEASE (45044) fmt "ls" | INTERFACE/save_load.c:693 | код 45044 не знайдено в обробнику | оригінал: обробляє (src/libs/xinterface/src/scr_shoter/scr_shoter.cpp:242); порт: game/Main.cs:270; клас визначено за кодом (reference) |
| Повідомлення | TELESCOPE → MSG_SHIP_DELETE (50301) fmt "la" | SEA_AI/shipDead.c:8 | код 50301 приймається без дії (згаданий лише в коментарі обробника) | оригінал: класу немає в reference; порт: game/SeaTelescope.cs:43 |
| Повідомлення | TMPTELEPORT → 42222 fmt "la" | teleport.c:242 | немає EntityHandlers["TMPTELEPORT"] | оригінал: обробляє (src/libs/teleport/src/teleport.cpp:199); лише налагоджувальні скрипти |

## Повідомлення (runtime) — розбіжності (1)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Повідомлення (runtime) | SeaLocatorShow \| a \|  |  | немає обробника класу (лише runtime) | [nohandler] 37× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |

## Сутності — розбіжності (3)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Сутності | HELPCHOOSER | INTERFACE/interface.c:1223 | немає реалізації; скрипти шлють йому 1 різних повідомлень | оригінал: CREATE_CLASS(HELPCHOOSER) |
| Сутності | InfoHandler | INTERFACE/interface.c:1575 | немає реалізації; повідомлень йому скрипти не шлють | оригінал: CREATE_CLASS(InfoHandler) |
| Сутності | Rope | SEA_AI/AIShip.c:34 | немає реалізації; повідомлень йому скрипти не шлють | оригінал: CREATE_CLASS(ROPE) |

## Функції рушія — розбіжності (8)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Функції рушія | CreateClass() ×5 | gameutils.c:7, lcorrect.c:5, memtest.c:5 (+2) | немає в порті (Builtins/HostFunctions) | оригінал: 1 арг. (src/libs/core/src/internal_functions.cpp:118); у логах не траплялась |
| Функції рушія | IDoExit() |  | unresolved під час прогону | 1× у reports/2026-11-07_16-09-00/log_tail.txt |
| Функції рушія | InitInterface() |  | unresolved під час прогону | 1× у reports/2026-11-05_08-51-31/log_tail.txt |
| Функції рушія | GlobalLngFileID() ×1 | INTERFACE/utilite.c:376 | немає в порті (Builtins/HostFunctions) | оригінал: 0 арг. (src/libs/xinterface/src/string_service/str_service.cpp:1615); native-оголошення: native int GlobalLngFileID() (INTERFACE/utilite.c:7); у логах не траплялась |
| Функції рушія | ExecuteTechnique() ×10 | Locations/locations_loader.c:76, Locations/locations_loader.c:80, Locations/locations_loader.c:84 (+2) | stub: no-op | порт: StormScript/Runtime/Builtins.cs:227; оригінал: 1 арг. (src/libs/location/src/location_script_lib.cpp:373); native-оголошення: native void ExecuteTechnique(string techName) (Locations/locations.c:17) |
| Функції рушія | GetAttributeName() ×116 | BATTLE_INTERFACE/ActivePerksShow.c:69, BATTLE_INTERFACE/BattleInterface.c:1790, BATTLE_INTERFACE/LandInterface.c:997 (+2) | скрипт передає 1/2 арг., оригінал оголошує 1; порт читає лише 1 з 2 арг. | порт: StormScript/Runtime/Builtins.cs:142; оригінал: 1 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | LayerAddObject() ×101 | animals.c:63, animals.c:64, BATTLE_INTERFACE/ActivePerksShow.c:77 (+2) | порт читає лише 2 з 3 арг. | порт: StormScript/Runtime/Builtins.cs:191; оригінал: 3 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | GetNextLineString() ×1 | Locations/locators_test.c:28 | немає в порті (Builtins/HostFunctions) | оригінал: 0 арг. (src/libs/location/src/location_script_lib.cpp:379); native-оголошення: native string GetNextLineString() (Locations/locations.c:18); у логах не траплялась; лише налагоджувальні скрипти |

## Події — розбіжності (23)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Події | ievntGetHelpTexture | INTERFACE/ship.c:154, INTERFACE/store.c:65 | НЕ підіймається портом | оригінал: core.Event "s" (src/libs/xinterface/src/xinterface.cpp:3301) |
| Події | Location_CharacterInLocator | Characters/characters_events.c:18 | НЕ підіймається портом | оригінал: core.Event "iissff" (src/libs/location/src/character.cpp:232) |
| Події | Location_CharacterSGFire | Loc_ai/LAi_fightparams.c:672 | НЕ підіймається портом | оригінал: core.Event "iif" (src/libs/location/src/player.cpp:812) |
| Події | SeaGetLayers | SEA_AI/AIShip.c:88 | НЕ підіймається портом | оригінал: core.Event "i" (src/libs/ship/src/ship.cpp:1438) |
| Події | EWhr_GetFogDensity | Weather/WhrWeather.c:41 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/shadow/src/shadow.cpp:150) |
| Події | EWhr_GetShadowDensity | Weather/WhrWeather.c:40 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/shadow/src/shadow.cpp:143) |
| Події | EWhr_GetWindAngle | Weather/WhrWeather.c:38 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/location/src/grass.cpp:323) |
| Події | EWhr_GetWindSpeed | Weather/WhrWeather.c:39 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/location/src/grass.cpp:343) |
| Події | WhrCalcFogColor | SEA_AI/Cabin.c:299, Weather/WhrWeather.c:225 | НЕ підіймається портом | оригінал: core.Event "fff" (src/libs/weather/src/sky.cpp:26) |
| Події | DelInterfaceTexture | INTERFACE/save_load.c:24 | НЕ підіймається портом | оригінал: core.Event "s\|ss" (src/libs/xinterface/src/nodes/xi_scroll_image.cpp:815) |
| Події | DisableScroll | INTERFACE/helpscreen.c:29 | НЕ підіймається портом | оригінал: core.Event "sl" (src/libs/xinterface/src/nodes/xi_formt_text.cpp:926) |
| Події | EmergencyDialogExit | dialog.c:9 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/dialog/src/dialog.cpp:925) |
| Події | EventSGMode | Loc_ai/LAi_utils.c:706 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/location/src/player.cpp:82) |
| Події | GetInterfaceTexture | INTERFACE/save_load.c:23 | НЕ підіймається портом | оригінал: core.Event "sl\|sls" (src/libs/xinterface/src/nodes/xi_scroll_image.cpp:677) |
| Події | GetQuestTextFileName | INTERFACE/BaseInterface.c:6 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/xinterface/src/xinterface.cpp:214) |
| Події | ISound | INTERFACE/interface.c:1034 | НЕ підіймається портом | оригінал: core.Event "l" (src/libs/xinterface/src/back_scene/back_scene.cpp:261) |
| Події | LocationTimeUpdate | Locations/locations_loader.c:939 | НЕ підіймається портом | оригінал: core.Event "f" (src/libs/location/src/location.cpp:144) |
| Події | SetWindowSize | BATTLE_INTERFACE/LogInterface.c:17 | НЕ підіймається портом | оригінал: core.Event "lll" (src/libs/battle_interface/src/log_and_action.cpp:50) |
| Події | EventEndHelpChooser | INTERFACE/interface.c:1221 | скрипти підіймають самі, але оригінал підіймає теж — порт ні | оригінал: core.Event "s" (src/libs/xinterface/src/help_chooser/help_chooser.cpp:62); скрипти теж підіймають (INTERFACE/interface.c:1226) |
| Події | evntOptionsBreak | INTERFACE/mainmenu.c:353, INTERFACE/interface.c:25 | скрипти підіймають самі, але оригінал підіймає теж — порт ні | оригінал: core.Event "" (src/libs/xinterface/src/xinterface.cpp:3273); скрипти теж підіймають (INTERFACE/interface.c:1684) |
| Події | LocatorsEventTrace | Locations/locators_test.c:413 | НЕ підіймається портом | оригінал: core.Event "lsss" (src/libs/location/src/location.cpp:1137); лише налагоджувальні скрипти |
| Події | TeleportChoose | teleport.c:14 | НЕ підіймається портом | оригінал: core.Event "l" (src/libs/teleport/src/teleport.cpp:158); лише налагоджувальні скрипти |
| Події | TeleportStart | teleport.c:15 | НЕ підіймається портом | оригінал: core.Event "" (src/libs/teleport/src/teleport.cpp:114); лише налагоджувальні скрипти |

## Ресурси — розбіжності (15)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Ресурси | atlas picture:  (group ) |  | не завантажується портом | 27× у game/smoke_door.log, game/smoke_itemstrade.log, game/smoke_screen_cards.log |
| Ресурси | atlas picture:  (group ICONS) |  | не завантажується портом | 28× у game/smoke_door.log, game/smoke_hold.log, game/smoke_officerhire.log |
| Ресурси | texture: ships\flagall.tga |  | не завантажується портом | 10× у game/smoke_chase.log.err, game/smoke_neutralA.log.err, game/smoke_neutralB.log.err |
| Ресурси | sound: waitress_brm | Loc_ai/types/LAi_waitress.c:149 | не завантажується портом | 6× у game/smoke_officerhire.log, reports/2026-11-05_08-49-52/log_tail.txt, reports/2026-11-05_08-49-54/log_tail.txt |
| Ресурси | atlas picture:  (group GOODS) |  | не завантажується портом | 4× у game/smoke_screen_price.log, reports/2026-11-05_08-49-52/log_tail.txt, reports/2026-11-05_08-49-54/log_tail.txt |
| Ресурси | sound: governor | DIALOGS/Bernardo Gamboa_dialog.c:43, DIALOGS/Conceicao Citizen_dialog.c:197, DIALOGS/Conceicao Citizen_dialog.c:218 | не завантажується портом | 4× у reports/2026-11-07_15-59-52/log_tail.txt, reports/2026-11-07_16-00-15/log_tail.txt, reports/2026-11-07_16-00-50/log_tail.txt |
| Ресурси | atlas picture: -1 (group -1) |  | не завантажується портом | 3× у reports/2026-11-07_12-51-33/log_tail.txt, reports/2026-11-07_12-52-38/log_tail.txt, reports/2026-11-07_15-55-15/log_tail.txt |
| Ресурси | sound: poorman | Characters/init/FalaiseDeFleur.c:549, Characters/init/Redmond.c:470, Characters/init/Santiago.c:472 | не завантажується портом | 3× у reports/2026-11-05_08-46-25/log_tail.txt, reports/2026-11-05_08-49-52/log_tail.txt, reports/2026-11-05_08-49-54/log_tail.txt |
| Ресурси | atlas picture:  (group CARDS) |  | не завантажується портом | 2× у game/smoke_screen_cards.log, game/smoke_screen_dice.log |
| Ресурси | atlas picture:  (group DICE) |  | не завантажується портом | 1× у game/smoke_screen_dice.log |
| Ресурси | atlas picture: Fearless (group SHIPS16) |  | не завантажується портом | 1× у game/smoke_board.log |
| Ресурси | atlas picture: Fearless (group Ships16) |  | не завантажується портом | 1× у game/smoke_board.log |
| Ресурси | sound: Gr_Commendant | Characters/init/Havanna.c:2271, Characters/init/IslaMuelle.c:2350, Characters/init/Redmond.c:2319 | не завантажується портом | 1× у game/smoke_q_hunters.log |
| Ресурси | sound: Gr_Pirat | Characters/init/StoryCharacters.c:1634, Characters/init/StoryCharacters.c:1675, Characters/init/StoryCharacters.c:1709 | не завантажується портом | 1× у game/smoke_q_fordshore.log |
| Ресурси | sound: fireplace0 |  | не завантажується портом | 1× у game/smoke_q_hunters.log |

## Ресурси: ini — розбіжності (7)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Ресурси: ini | RESOURCE/INI/TextureSequence.ini |  | порт не читає цей файл | оригінал читає (reference); ні назви, ні теки в коді порту |
| Ресурси: ini | RESOURCE/INI/dialog.ini |  | порт не читає цей файл | оригінал читає (reference); ні назви, ні теки в коді порту |
| Ресурси: ini | RESOURCE/INI/helpchooser.ini |  | порт не читає цей файл | оригінал читає (reference); ні назви, ні теки в коді порту |
| Ресурси: ini | RESOURCE/INI/loclighter.ini |  | порт не читає цей файл | оригінал читає (reference); ні назви, ні теки в коді порту |
| Ресурси: ini | RESOURCE/INI/particles.ini |  | порт не читає цей файл | оригінал читає (reference); ні назви, ні теки в коді порту |
| Ресурси: ini | RESOURCE/INI/animals.ini: секції [fish schools], [butterflies] |  | секції, яких порт не читає (оригінал читає їх за назвою) | 2 з 4 секцій |
| Ресурси: ini | RESOURCE/INI/fonts.ini: секції [DIALOG3] |  | секції, яких порт не читає (оригінал читає їх за назвою) | 1 з 13 секцій |

## Інформаційне (оригінал теж ігнорує, ціль не визначено, події-дані, не спрацювали в прогонах)
| Контракт | Елемент | Місце в скриптах | Стан у порту | Докази |
|---|---|---|---|---|
| Події | InterfaceKey | seadogs.c:68 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | blieGetMsgIconRoot | BATTLE_INTERFACE/LogInterface.c:13 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | BI_CheckSSShow | BATTLE_INTERFACE/BattleInterface.c:90 | не знайдено; ім'я використовується скриптами як дані (BATTLE_INTERFACE/msg_control.h:46) — можливо, скрипт-подія | у reference не підіймається |
| Події | wdmCreateMap | QUESTS/quests.c:16 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Події | Ball_FlyUpdate | SEA_AI/AIBalls.c:47 | не знайдено; ім'я використовується скриптами як дані (SEA_AI/Script_Defines.h:27) — можливо, скрипт-подія | у reference не підіймається |
| Події | SeaCameras_Switch | SEA_AI/AICameras.c:81 | не знайдено; ім'я використовується скриптами як дані (SEA_AI/AICameras.c:13) — можливо, скрипт-подія | у reference не підіймається |
| Події | ShpChCharge | SEA_AI/AIShip.c:102 | не знайдено; ім'я використовується скриптами як дані (SEA_AI/Script_Defines.h:48) — можливо, скрипт-подія | у reference не підіймається |
| Сутності | CharacterAnimationKipper | reload.c:70, reload.c:96 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(CharacterAnimationKipper) |
| Сутності | FINDFILESINTODIRECTORY | CheckDlgLoad.c:10, teleport.c:220 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(FINDFILESINTODIRECTORY) |
| Сутності | SeaLocatorShow | SEA_AI/sea.c:203 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(SeaLocatorShow) |
| Сутності | TMPTELEPORT | teleport.c:11 | немає реалізації; скрипти шлють йому 1 різних повідомлень (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(TMPTELEPORT); лише налагоджувальні скрипти |
| Сутності | islandED | gameutils.c:7 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: класу немає в reference |
| Сутності | obj_strservice | INTERFACE/BaseInterface.c:15, seadogs.c:403 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(OBJ_STRSERVICE) |
| Сутності | touch | SEA_AI/sea.c:186 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(TOUCH) |
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
| Події: runtime | BI_GetChargeQuantity | BATTLE_INTERFACE/BattleInterface.c:84 | жодного разу в регресійних прогонах | порт: game/SeaCommands.cs:238; [events] топ-40 у 75 логах |
| Події: runtime | BI_VISIBLE | BATTLE_INTERFACE/ispyglass.c:17 | жодного разу в регресійних прогонах | порт: game/SeaTelescope.cs:101; [events] топ-40 у 75 логах |
| Події: runtime | BI_Visible | BATTLE_INTERFACE/BattleInterface.c:87, BATTLE_INTERFACE/LogInterface.c:25 | жодного разу в регресійних прогонах | порт: game/SeaTelescope.cs:101; [events] топ-40 у 75 логах |
| Події: runtime | evntBISelectShip | BATTLE_INTERFACE/BattleInterface.c:53 | жодного разу в регресійних прогонах | порт: game/SeaTelescope.cs:187; [events] топ-40 у 75 логах |
| Події: runtime | evntSetUsingAbility | BATTLE_INTERFACE/BattleInterface.c:97 | жодного разу в регресійних прогонах | порт: game/SeaCommands.cs:265; [events] топ-40 у 75 логах |
| Події: runtime | CannGFHeight | SEA_AI/AICannon.c:17 | жодного разу в регресійних прогонах | порт: game/SeaCombat.cs:250; [events] топ-40 у 75 логах |
| Події: runtime | CannReCaclParam | SEA_AI/AICannon.c:21 | жодного разу в регресійних прогонах | порт: game/SeaForts.cs:139; [events] топ-40 у 75 логах |
| Події: runtime | FortCreate | SEA_AI/AIFort.c:27 | жодного разу в регресійних прогонах | порт: game/SeaForts.cs:43; [events] топ-40 у 75 логах |
| Події: runtime | Location_CharacterBusyPos | Loc_ai/LAi_init.c:32 | жодного разу в регресійних прогонах | порт: game/Characters.cs:990; [events] топ-40 у 75 логах |
| Події: runtime | Location_CharacterEscapeSlide | Loc_ai/LAi_init.c:42 | жодного разу в регресійних прогонах | порт: game/Characters.cs:1018; [events] топ-40 у 75 логах |
| Події: runtime | Location_CharacterTaskFailure | Loc_ai/LAi_init.c:31 | жодного разу в регресійних прогонах | порт: game/Characters.cs:156; [events] топ-40 у 75 логах |
| Події: runtime | Shp2Strand | SEA_AI/AIShip.c:97 | жодного разу в регресійних прогонах | порт: game/SeaGrounding.cs:102; [events] топ-40 у 75 логах |
| Події: runtime | ShpDelete | BATTLE_INTERFACE/BattleInterface.c:79 | жодного разу в регресійних прогонах | порт: game/Sea.cs:548; [events] топ-40 у 75 логах |
| Події: runtime | ShpEatSwimGood | SEA_AI/AISeaGoods.c:18 | жодного разу в регресійних прогонах | порт: game/SeaFx.cs:501; [events] топ-40 у 75 логах |
| Події: runtime | ShpFireAction | SEA_AI/AIShip.c:105 | жодного разу в регресійних прогонах | порт: game/SeaAI.cs:105; [events] топ-40 у 75 логах |
| Події: runtime | ShpGetBortFireDelta | SEA_AI/AIShip.c:106 | жодного разу в регресійних прогонах | порт: game/SeaCombat.cs:140; [events] топ-40 у 75 логах |
| Події: runtime | Shp_ShipIslColl | SEA_AI/AIShip.c:92 | жодного разу в регресійних прогонах | порт: game/SeaGrounding.cs:91; [events] топ-40 у 75 логах |
| Події: runtime | WorldMap_EventWindowChangeSelectoin | WorldMap/worldmap.c:68 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:412; [events] топ-40 у 75 логах |
| Події: runtime | WorldMap_EventWindowSelect | WorldMap/worldmap.c:69 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:411; [events] топ-40 у 75 логах |
| Події: runtime | WorldMap_PlayerInStorm | WorldMap/worldmap.c:66 | жодного разу в регресійних прогонах | порт: game/WorldStorms.cs:241; [events] топ-40 у 75 логах |
| Події: runtime | WorldMap_ShipEncounter | WorldMap/worldmap.c:67 | жодного разу в регресійних прогонах | порт: game/WorldEncounters.cs:428; [events] топ-40 у 75 логах |
| Події: runtime | WorldMap_WaitMenuChangeSelectoin | WorldMap/worldmap.c:70 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:427; [events] топ-40 у 75 логах |
| Події: runtime | WorldMap_WaitMenuSelect | WorldMap/worldmap.c:71 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:423; [events] топ-40 у 75 логах |
| Події: runtime | WorldMap_outside_islandzone | WorldMap/worldmap.c:65 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:455; [events] топ-40 у 75 логах |
| Повідомлення (runtime) | <no entity> \| ls \| 30350 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 90975× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| lsl \| 30373 LockFightMode |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 90975× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| lsl \| 30373 SetFightWOWeapon |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 90972× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| sis \| MoveChr |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 163799× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Події: runtime | EventMastFall | BATTLE_INTERFACE/BattleInterface.c:52 | жодного разу в регресійних прогонах | порт: game/SeaSails.cs:426; [events] топ-40 у 75 логах |
| Події: runtime | ExitFromWorldMap | WorldMap/worldmap.c:74 | жодного разу в регресійних прогонах | порт: game/WorldMap.cs:465; [events] топ-40 у 75 логах |
| Події: runtime | FormatedTextActivate | INTERFACE/addon_team.c:77, INTERFACE/controls.c:62 | жодного разу в регресійних прогонах | порт: game/XInterface.cs:967; [events] топ-40 у 75 логах |
| Події: runtime | IEvnt_MouseClick | INTERFACE/transfer_crew.c:57, INTERFACE/transfer_goods.c:76 | жодного разу в регресійних прогонах | порт: game/XInterface.cs:407; [events] топ-40 у 75 логах |
| Події: runtime | MSG_TELESCOPE_REQUEST | SEA_AI/sea.c:223 | жодного разу в регресійних прогонах | порт: game/SeaTelescope.cs:111; [events] топ-40 у 75 логах |
| Події: runtime | QuestActivate | INTERFACE/boal_questbook.c:33, INTERFACE/QuestBook.c:27 | жодного разу в регресійних прогонах | порт: game/XInterface.cs:912; [events] топ-40 у 75 логах |
| Події: runtime | QuestTopChange | INTERFACE/boal_questbook.c:31, INTERFACE/QuestBook.c:25 | жодного разу в регресійних прогонах | порт: game/XInterface.cs:1042; [events] топ-40 у 75 логах |
| Події: runtime | ReleaseTelescopeInfo | BATTLE_INTERFACE/ispyglass.c:16 | жодного разу в регресійних прогонах | порт: game/SeaTelescope.cs:102; [events] топ-40 у 75 логах |
| Події: runtime | ScrollPosChange | INTERFACE/addon_team.c:79, INTERFACE/boal_questbook.c:36 | жодного разу в регресійних прогонах | порт: game/XInterface.cs:409; [events] топ-40 у 75 логах |
| Події: runtime | ScrollTopChange | INTERFACE/addon_team.c:80, INTERFACE/boal_questbook.c:37 | жодного разу в регресійних прогонах | порт: game/XInterface.cs:409; [events] топ-40 у 75 логах |
| Події: runtime | SetScrollerPos | INTERFACE/addon_team.c:78, INTERFACE/boal_questbook.c:35 | жодного разу в регресійних прогонах | порт: game/XInterface.cs:968; [events] топ-40 у 75 логах |
| Події: runtime | TelescopeActive | SEA_AI/AICameras.c:82 | жодного разу в регресійних прогонах | порт: game/SeaTelescope.cs:100; [events] топ-40 у 75 логах |
| Події: runtime | TornadoDamage | SEA_AI/AIShip.c:110 | жодного разу в регресійних прогонах | порт: game/WeatherFx.cs:131; [events] топ-40 у 75 логах |
| Події: runtime | evntCheckEnableLocator | BATTLE_INTERFACE/BattleInterface.c:98 | жодного разу в регресійних прогонах | порт: game/SeaCommands.cs:302; [events] топ-40 у 75 логах |
| Події: runtime | evntCheckEnableShip | BATTLE_INTERFACE/BattleInterface.c:99 | жодного разу в регресійних прогонах | порт: game/SeaCommands.cs:288; [events] топ-40 у 75 логах |
| Події: runtime | evntUpdateFormtText | INTERFACE/addon_team.c:81, INTERFACE/controls.c:63 | жодного разу в регресійних прогонах | порт: game/XInterface.cs:966; [events] топ-40 у 75 логах |
| Функції рушія | LayerCreate() ×19 | dialog.c:115, dialog.c:181 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:227; у reference немає |
| Функції рушія | LayerDelObject() ×25 | INTERFACE/interface.c:1618, SEA_AI/AISea.c:28 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:227; оригінал: 2 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | LayerFreeze() ×67 | INTERFACE/interface.c:1158, INTERFACE/interface.c:1159 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:227; оригінал: 2 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | ReloadProgressEnd() ×12 | Loc_ai/LAi_boarding.c:509, Locations/locations.c:42 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:227; оригінал: 0 арг. (src/libs/location/src/location_script_lib.cpp:361); native-оголошення: native void ReloadProgressEnd() (Locations/locations.c:15) |
| Функції рушія | ReloadProgressStart() ×12 | Loc_ai/LAi_boarding.c:415, Locations/locations.c:39 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:227; оригінал: 0 арг. (src/libs/location/src/location_script_lib.cpp:349); native-оголошення: native void ReloadProgressStart() (Locations/locations.c:13) |
| Функції рушія | ReloadProgressUpdate() ×123 | Characters/characters.c:24, Characters/characters.c:30 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:227; оригінал: 0 арг. (src/libs/location/src/location_script_lib.cpp:355); native-оголошення: native void ReloadProgressUpdate() (Locations/locations.c:14) |
| Функції рушія | TavernWaitDate2() ×20 | DIALOGS/Antoine Lebretton_dialog.c:173, DIALOGS/Antoine Lebretton_dialog.c:187 | не визначена ні в скриптах, ні в оригінальному рушії — ймовірно дефект скриптів | у reference немає |
| Параметри подій | BI_GetData → BI_GetData() | BATTLE_INTERFACE/BattleInterface.c:89 | скрипт читає 3 значень, передається 2 (зайві читання умовні) (порт = формат reference) | скрипт читає "lll" (BATTLE_INTERFACE/BattleInterface.c:1087); порт передає "ll" (game/BattleInterface.cs:76); оригінал "ll" |
| Параметри подій | BI_GetData → BI_GetData() | BATTLE_INTERFACE/BattleInterface.c:89 | скрипт читає 3 значень, передається 2 (зайві читання умовні) (порт = формат reference) | скрипт читає "lll" (BATTLE_INTERFACE/BattleInterface.c:1087); порт передає "ll" (game/BattleInterface.cs:137); оригінал "ll" |
| Повідомлення | ? → "AddModel" fmt "ssi" | SEA_AI/AIFort.c:110, SEA_AI/sea.c:560 | клас цілі не визначено статично |  |
| Повідомлення | ? → "CheckID" fmt "ss" | Loc_ai/LAi_utils.c:588 | клас цілі не визначено статично |  |
| Повідомлення | ? → "LightPath" fmt "ss" | SEA_AI/sea.c:547 | клас цілі не визначено статично |  |
| Повідомлення | ? → "ModelsPath" fmt "ss" | SEA_AI/sea.c:546 | клас цілі не визначено статично |  |
| Повідомлення (runtime) | <no entity> \| ssf \| SetGroupHear |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 164× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| ssf \| SetGroupLook |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 164× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| ssf \| SetGroupSay |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 164× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| ls \| 30373 IsFightMode |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 157× у game/smoke_firstquest.log, game/smoke_fortland.log, game/smoke_intro.log |
| Повідомлення (runtime) | <no entity> \| ss \| RegistryGroup |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 154× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Події | Cheat | seadogs.c:70 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається |
| Функції рушія | LayerSetRealize() ×6 | dialog.c:116, dialog.c:182 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:227; оригінал: 1 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Параметри подій | FormatedTextActivate → ProcHelpSectionActivate() | INTERFACE/helpscreen.c:30 | #1: string замість int (порт = формат reference) | скрипт читає "l" (INTERFACE/helpscreen.c:236); порт передає "sl" (game/XInterface.cs:967); оригінал "sl" |
| Повідомлення (runtime) | <no entity> \| l \| 10006 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 77× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| l \| 51025 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 77× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| lll \| 77004 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 75× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| lllas \| 51011 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 66× у game/smoke_firstquest.log, game/smoke_intro.log |
| Функції рушія | GetNPCInformation() ×4 | DIALOGS/Docker Generated_dialog.c:153, DIALOGS/Docker Generated_dialog.c:162 | не визначена ні в скриптах, ні в оригінальному рушії — ймовірно дефект скриптів | у reference немає |
| Функції рушія | Sea_TurnRateMagicNumber() ×1 | SEA_AI/AIShip.c:2103 | не визначена ні в скриптах, ні в оригінальному рушії — ймовірно дефект скриптів | у reference немає |
| Повідомлення (runtime) | <no entity> \| ssi \| AddModel |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 49× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| ss \| LightPath |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 37× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| ss \| ModelsPath |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 37× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| lisffffff \| 77400 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 35× у game/smoke_board.log, game/smoke_boardpatent.log, game/smoke_chase.log |
| Повідомлення (runtime) | <no entity> \| ssss \| SetRelation |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 25× у game/smoke_boardpatent.log, game/smoke_fortland.log, game/smoke_neutralB.log |
| Функції рушія | EntityUpdate() ×2 | SEA_AI/AIBalls.c:55, SEA_AI/AIBalls.c:78 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:227; оригінал: 1 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | LayerSetExecute() ×2 | gameutils.c:6, SEA_AI/sea.c:174 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:227; оригінал: 1 арг. (src/libs/core/src/internal_functions.cpp:118) |
| Функції рушія | RandSum() ×2 | DIALOGS/Henry_dialog.c:164, DIALOGS/Henry_dialog.c:346 | не визначена ні в скриптах, ні в оригінальному рушії — ймовірно дефект скриптів | у reference немає |
| Функції рушія | SetReloadNextTipsImage() ×2 | reload.c:169, WorldMap/worldmap_reload.c:8 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:227; оригінал: 0 арг. (src/libs/location/src/location_script_lib.cpp:343); native-оголошення: native void SetReloadNextTipsImage() (Locations/locations.c:19) |
| Повідомлення (runtime) | <no entity> \| l \| 50102 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 23× у game/smoke_chest.log, game/smoke_firstquest.log, game/smoke_intro.log |
| Повідомлення (runtime) | <no entity> \| ls \| 30400 Stay |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 20× у game/smoke_officerhire.log, game/smoke_q_fordshore.log, game/smoke_q_hunters.log |
| Повідомлення (runtime) | <no entity> \| sssss \| SetAlarmReaction |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 15× у game/smoke_boardpatent.log, game/smoke_fortland.log, game/smoke_neutralB.log |
| Функції рушія | RDTSC_B() ×1 | SEA_AI/sea.c:441 | stub: returns 0 (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:89; оригінал: 0 арг. (src/libs/script_library/src/vano_files.cpp:118); native-оголошення: native int RDTSC_B() (seadogs.c:58) |
| Функції рушія | RDTSC_E() ×1 | SEA_AI/sea.c:852 | stub: returns 0 (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:89; оригінал: 1 арг. (src/libs/script_library/src/vano_files.cpp:124); native-оголошення: native int RDTSC_E(int iRDTSC) (seadogs.c:59) |
| Функції рушія | SetReloadProgressBackImage() ×1 | Locations/locations.c:38 | stub: no-op (у Godot не потрібно: шари/профайлер/екран завантаження) | порт: StormScript/Runtime/Builtins.cs:227; оригінал: 1 арг. (src/libs/location/src/location_script_lib.cpp:337); native-оголошення: native void SetReloadProgressBackImage(string imageName) (Locations/locations.c:12) |
| Повідомлення (runtime) | <no entity> \| ssl \| SetGroupPriority |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 10× у game/smoke_boardpatent.log, game/smoke_fortland.log, game/smoke_neutralB.log |
| Повідомлення (runtime) | <no entity> \| s \| ClearAllTargets |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 6× у game/smoke_boardpatent.log, game/smoke_fortland.log, game/smoke_neutralB.log |
| Повідомлення (runtime) | TELESCOPE \| lsflll \| 77302 eye_BadTub.tga |  | застарілий лог: обробник класу вже є (game/SeaTelescope.cs:43) | [nohandler] 6× у game/smoke_chase.log, game/smoke_neutralA.log, game/smoke_neutralB.log |
| Повідомлення (runtime) | <no entity> \| llsslf \| 50304 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 5× у game/smoke_boardpatent.log |
| Повідомлення (runtime) | <no entity> \| llllll \| 46013 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 4× у game/smoke_board.log, game/smoke_boardpatent.log |
| Повідомлення (runtime) | <no entity> \| lsi \| 30400 Follow character |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 4× у game/smoke_officerhire.log, game/smoke_q_hunters.log |
| Повідомлення (runtime) | <no entity> \| leee \| 30202 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 3× у game/smoke_q_hunters.log |
| Повідомлення (runtime) | <no entity> \| l \| 46005 |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 2× у game/smoke_board.log, game/smoke_boardpatent.log |
| Повідомлення (runtime) | <no entity> \| ls \| 30400 None |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 2× у game/smoke_firstquest.log, game/smoke_intro.log |
| Параметри подій: формат | CannFire | SEA_AI/AICannon.c:18 | порт передає "afffffff", reference "afffffffff" — але обробники PotC-скриптів читають саме те, що дає порт (різниця версій рушія) | порт: game/SeaCombat.cs:209; оригінал: src/libs/sea_ai/src/ai_cannon.cpp:149 |
| Параметри подій: формат | GetRiggingData | BATTLE_INTERFACE/BattleInterface.c:51 | порт передає "sll", reference "slll\|sllla" — але обробники PotC-скриптів читають саме те, що дає порт (різниця версій рушія) | порт: game/Sea.cs:104; оригінал: src/libs/rigging/src/flag.cpp:858 |
| Параметри подій: формат | Location_CharacterAttack | Loc_ai/LAi_init.c:37 | порт передає "aaff", reference "iislll" — але обробники PotC-скриптів читають саме те, що дає порт (різниця версій рушія) | порт: game/Characters.cs:1174; оригінал: src/libs/location/src/character.cpp:5097 |
| Параметри подій: формат | Location_CharacterEntryToLocator | BATTLE_INTERFACE/LandInterface.c:77, Characters/characters_events.c:17 | порт передає "aass", reference "iissf" — але обробники PotC-скриптів читають саме те, що дає порт (різниця версій рушія) | порт: game/Characters.cs:854; оригінал: src/libs/location/src/character.cpp:245 |
| Параметри подій: формат | ShpDelete | BATTLE_INTERFACE/BattleInterface.c:79 | порт передає "l", reference "li" — але обробники PotC-скриптів читають саме те, що дає порт (різниця версій рушія) | порт: game/Sea.cs:548; оригінал: src/libs/ship/src/ship.cpp:753 |
| Параметри подій: формат | ShpSailDamage | SEA_AI/AIShip.c:94 | порт передає "lfff", reference "lfffl" — але обробники PotC-скриптів читають саме те, що дає порт (різниця версій рушія) | порт: game/SeaSails.cs:404; оригінал: src/libs/rigging/src/sail.cpp:1659 |
| Ресурси: ini | RESOURCE/INI/camera.ini |  | порт не читає; оригінал (reference) теж не читає за цим шляхом | ні назви, ні теки в коді порту; скрипти не передають |
| Ресурси: ini | RESOURCE/INI/fonts_boal.ini |  | порт не читає; оригінал (reference) теж не читає за цим шляхом | ні назви, ні теки в коді порту; скрипти не передають |
| Ресурси: ini | RESOURCE/INI/fonts_eng.ini |  | порт не читає; оригінал (reference) теж не читає за цим шляхом | ні назви, ні теки в коді порту; скрипти не передають |
| Ресурси: ini | RESOURCE/INI/fonts_rus.ini |  | порт не читає; оригінал (reference) теж не читає за цим шляхом | ні назви, ні теки в коді порту; скрипти не передають |
| Ресурси: ini | RESOURCE/INI/geo.ini |  | порт не читає; оригінал (reference) теж не читає за цим шляхом | ні назви, ні теки в коді порту; скрипти не передають |
| Повідомлення (runtime) | <no entity> \| ls \| 30373 FindDialogCharacter |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 1× у game/smoke_firstquest.log |
| Повідомлення (runtime) | <no entity> \| s \| SaveData |  | ціль без сутності в момент виклику (оригінал теж ігнорує, якщо сутність не створена; перевірити, чи порт створює її так само рано) | [nohandler] 1× у game/smoke_fortland.log |
| Сутності | FINDDIALOGNODES | teleport.c:231 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: CREATE_CLASS(FINDDIALOGNODES); лише налагоджувальні скрипти |
| Сутності | dummy | lcorrect.c:5, test.c:11 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: класу немає в reference; лише налагоджувальні скрипти |
| Сутності | env | lcorrect.c:4 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: класу немає в reference; лише налагоджувальні скрипти |
| Сутності | scriptscreen | StartInterface.c:120 | немає реалізації; повідомлень йому скрипти не шлють (налагоджувальний/службовий клас) | оригінал: класу немає в reference; лише налагоджувальні скрипти |
| Події | QuickTeleport | teleport.c:4 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається; лише налагоджувальні скрипти |
| Події | key_event | StartInterface.c:54 | не підіймається; reference, ini і скрипти теж не підіймають — подія лише PotC-рушія або мертвий обробник, перевірити вручну | у reference не підіймається; лише налагоджувальні скрипти |
| Функції рушія | InitBaseShipsTypes() ×2 | InitInterface.c:33, StartInterface.c:36 | extern без визначення в жодному скрипті (мертвий код скриптів) | у reference немає; лише налагоджувальні скрипти |
| Функції рушія | InitCharacters() ×2 | InitInterface.c:37, StartInterface.c:40 | extern без визначення в жодному скрипті (мертвий код скриптів) | у reference немає; лише налагоджувальні скрипти |
| Функції рушія | InitPlayer() ×1 | InitInterface.c:41 | extern без визначення в жодному скрипті (мертвий код скриптів) | у reference немає; лише налагоджувальні скрипти |
