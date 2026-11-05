# Аудит: що є в оригінальній грі і що реалізовано в порті

_Стан на 2026-11-05 (коміт `Sail to port/ship ...`). Під час аудиту код не змінювався. Джерела: `addon/merged/PROGRAM` (усі `CreateEntity`, `#define` повідомлень,
`SetEventHandler`), `RESOURCE/INI`, DLL у `MODULES/`, код `game/*.cs`, логи `game/smoke_*.log`, `reports/*/log_tail.txt`, скріншоти `game/review/*.png`._

Позначки: ✅ реалізовано · 🟡 частково (що бракує вказано) · ❌ відсутнє · ⚪ не потрібно (налагодження, Xbox, внутрішнє для DX8) · ❓ код є, перевірки в логах немає.

Як рахувалося: номери повідомлень із `messages.h`, `sail_msg.h`, `SEA_AI/Script_Defines.h`, `SEA_AI/seaPeople.h`, `BATTLE_INTERFACE/*.h`, `INTERFACE/messages.h`
зіставлені з кодом порту (пошук номера/імені в `game/*.cs`, далі ручна перевірка `switch`). Події: імена з `SetEventHandler`/`#event_handler`, які скрипти
самі не викликають через `Event/PostEvent`, тобто їх має підіймати рушій. `[nohandler]` у логах = повідомлення класу без `EntityHandlers[...]`.

## 0. Розбіжності в документації (виправити разом із наступним оновленням статусу)
| Документ | Що застаріло |
|---|---|
| `CLAUDE.md` → Status | "Missing: world map, other ships + sea AI, cannons, sea HUD..." — усе це вже є (частково). |
| `docs/ARCHITECTURE.md` §3 | `CharactersGroups`, `particles`, `Ship`, `Island`, `Sail`, `worldmap` позначені "not implemented" — вони реалізовані. |
| `docs/STATUS.md` | `Tornado` = MISSING, але є `WeatherFx.cs` (смерч + `TornadoDamage`); `Rope/Flag/Vant` = MISSING, але є `RiggingFx.cs`; `TELESCOPE` = MISSING, але є вигляд у підзорну трубу (`Sea.cs`); класи `SEA_OPERATOR` і `lighter` не враховані (скрипти створюють 67 класів + 3 класи персонажів, а не 66). |
| `game/Location.cs:77` | коментар `EX_MSG ("AddFlys", "AddEagle", ...)` — повертає 0, нічого не робить. |

## 1. Класи рушія (CreateEntity у скриптах) і DLL

### 1.1 Ядро, локації, персонажі
| Клас (DLL) | Оригінал | Порт | Докази | Що зробити |
|---|---|---|---|---|
| `xinterface` (XINTERFACE) | усі екрани з `INI/INTERFACES` | 🟡 більшість повідомлень 450xx; `LOCK_NODE` 45046, `SET_BLIND` 45039, `SETHELPSTRING` 45022 — заглушки; немає `DEL_SCROLLIMAGE` 45025, `REFRESH_SCROLL` 45029, `DELETE_PICTURE/STRING` 45049/45050, `CHECK_AGAIN` 45057, `LAUNCH_DASHBOARD` 45054 (⚪) | `XInterface.cs:64-158` | реалізувати 45025/45029 (пасажири, списки), 45049/45050 (екран довідки), 45046 (блокування вузлів) |
| `fader` | затемнення, картинки завантаження | 🟡 немає `FADER_STARTFRAME` 33002 (12 викликів: знімок кадру під фейдер) | `Main.cs:190` | дрібниця |
| `location` (Location) | моделі, патчі, локатори, світло, анімовані пропси | 🟡 моделі/патчі/локатори ✅; `MODEL_SET_ROT/UVS/LAMPS/REFLECTION` 30111-30114 і `ADD_LIGHT` 30128 — заглушки (млини, водоспади, ліхтарі, нічне світло); `GET_MODEL` 30101 → 0; `EX_MSG` 30155 (`AddEagle/AddFlys/AddRats/AddLizards/GetPatchMiddlePos`) → 0; `VIEW/HIDELGROUP` 30180/30181 ❌ (налагодження) | `Location.cs:40-77` | анімовані моделі (rot/uv), точкові світла `ADD_LIGHT` + лампи вночі |
| `NPCharacter/Player/Character` (Location, Animation, blade) | моделі, задачі, бій, зброя | 🟡 задачі, повороти, детектори ✅; `SETBLADE/SETGUN/SETFTGLEVEL/VISIBLE/VIEWDAMAGE` 30360-30372 — заглушки; `FINDNEAR` 30370 → 0; `GETTASK` 30403 ❌; `PUSH/POPTASK` — заглушки; `EX_MSG`: `FindDialogCharacter`, `IsActive`, `IsSetBalde`, `FindForvardLocator`, `DistToLocator`, `ChrBlock`, `SetFightMode` → 0 | `Characters.cs:570-640` | `FindDialogCharacter` (бойовий/земний HUD), `FINDNEAR` (ШІ натовпу), `VISIBLE`, `GETTASK` |
| `locationcamera` | follow / topos / free / рух | 🟡 немає `MSG_CAMERA_MOVE` 30513 і `SLEEP` 30520 (кат-сцени) | `Main.cs:217-225` | для квестових сцен |
| `MODEL`, `MODELR` (MODEL) | предмети, скрині, моделі абордажу | 🟡 немає `MODEL_RESTORE` 20505, `SET_LIGHT_PATH` 20503, `SET_TECHNIQUE` 20510 (світіння випадкових предметів) | `Main.cs:238-266` | ❓ мало перевірено |
| `dialog` (DIALOG, CameraDialog) | вікно діалогу, камера, голова, голос | 🟡 текст/відповіді ✅; немає «говорячої голови», камери діалогу | `Dialog.cs` | портрет `Heads/h_*.gm` |
| `CharactersGroups` | групи, тривога, цілі | ✅ усі 21 команда скриптів | `Groups.cs` | — |
| `CharacterAnimationKipper` | тримає анімації в пам'яті | ⚪ | — | — |
| `obj_strservice`, `FINDFILESINTODIRECTORY`, `FINDDIALOGNODES` | тексти, пошук файлів | ✅ | `Builtins`/`Language.cs` | — |
| `ILogAndActions` | журнал і підказка дії | ✅ (`LOG_LAUNCH_ACTIONS` 45023, `LI_OTHER_MSG` 45029 не використовуються) | `LogHud.cs` | — |
| `battle_land_interface` | земний HUD: портрети героя й офіцерів зі смугами HP, іконки команд | ✅ 46013/46100-46104: портрети (HP, перезарядка, заряди пістоля, іконки повідомлень, блимання тривоги/отрути), меню команд по Enter (`BI_SetPossibleCommands` / `BI_CommandEndChecking` / `BI_LaunchCommand`, `UserIcons`, `ComState`); точне змішування/порядок малювання C++ невідомі | `LandHud.cs`, тест `landhud`, `review/land*.png` | — |
| `Animals` (animals) | чайки, риби, метелики, акули | ❌ (`[nohandler] 77100-77109`) | `smoke_sea.log`, `smoke_fight.log` | чайки/метелики — дешева атмосфера |
| `blots` (Blot) | сліди від ударів на корпусі/фортах | ❌ (`[nohandler] 30600`) | `smoke_*` | низький пріоритет |
| `Sound` (SOUND, SoundService) | аліаси, схеми, 3D | 🟡 усі ini з `INI/ALIASES` (з `Greetings_alias.ini`: 224 привітання, 78 з 87 імен у скриптах є в ini — решти нема й в оригіналі); 3D-звук у точці (`SOUND_WAV_3D` + x,y,z) з `minDistance/maxDistance` аліаса; немає `SET_3D_PARAM`, `DUPLICATE`, `GET_POSITION`, позиції камери 77001/77002 (слухач = камера Godot) | `SoundView.cs`, `SEADOGS_SOUNDLOG=1` | `SET_3D_PARAM` |
| `particles` (PARTICLES) | системи з `INI/PARTICLES/*.ini` | ✅ усі 36 систем з 5 ini (ключі, які читає PARTICLES.dll: емісія, час життя, криві розміру/альфи/швидкості/обертання/вітру, вага, техніки Particles / Particles_Inv / ParticlesFire); `PS_CREATE/CREATEX/_RIC/CREATEXX` 10000-10009, `PS_DELETE` 10007, `PS_CLEARALL` 10006, `PS_PAUSEALL` 10010, `PS_SETVECTORS` 10001; вітер з атрибутів `windpower/winddirection`. Одиниці виведені з даних (вихідного C++ немає); текстура вогню малюється адитивно (інакше тьмяна); `[geo]`-частинки лише через `blast` | `Particles.cs`, скріни `review/pfx*.png` | — |
| `blast` | вибухи (LM_SETPOINT) | 🟡 1600/1601: 6-11 уламків `MODELS/particles/palka01..05` розлітаються балістично (точна поведінка оригіналу невідома); дим/спалах — системи `blast`/`blast_inv` скрипту | `Particles.cs`, `--shipboom@N` | — |
| `env`, `controls_container`, `scriptscreen`, `TMPTELEPORT`, `SeaLocatorShow`, `lighter` (Lighter), `FREE_CAMERA`, `touch` | налагодження / інструменти | ⚪ | — | — |
| `scrshoter` | мініатюри збережень | ✅ власним способом (`SaveFiles.WriteThumbnail`), повідомлення 45042-45044 → -1 | `SaveFiles.cs`, `XInterface.cs:151` | — |
| `CAviPlayer`, `WMVideoPlay` | ролики `RESOURCE/VIDEOS/*.wmv` (9 шт.) | ❌ показується підпис «Ви загинули» замість ролика | `Main.cs:144` | конвертувати wmv→ogv (потрібен дозвіл на інструмент) |
| `WideScreen` | чорні смуги в кат-сценах | ❌ | `QUESTS/quests_movie.c` | дрібниця |
| `InfoHandler` | інфо-екран (картинка/текст) поверх гри | ❌ | `INTERFACE/interface.c:1575` | ❓ |
| `HELPCHOOSER` | вибір розділу довідки за картинкою | ❌ | `interface.c:1229`, `helpchooser.ini` | разом з екраном довідки |
| `ActivePerkShower` | іконки активних навичок на HUD | ❌ (47000) | `ActivePerksShow.c` | середній |
| `artifact` | ефект артефакту (квест) | ❌ | `MODELS/...`, `PARTICLES/artifact.ini` | квестовий |
| `shadow.dll`, `WaterRings.dll`, `blade.dll` (створює рушій) | тіні персонажів, кола у воді, сліди клинка/кров | ❌ (`MSG_BLADE_*` у скриптах не вживаються) | — | тіні — помітно |

### 1.2 Море
| Клас (DLL) | Оригінал | Порт | Докази | Що зробити |
|---|---|---|---|---|
| `sea_ai` (sea_ai) | кораблі, групи, задачі, гармати | 🟡 обробляє 51001, 51010, 51011, 51013 (`SET_OFFICER_2_SHIP`: офіцер приймає захоплений корабель), 51014 (`SWAP_SHIPS`: капітани міняються кораблями), 51017, 51018, 51021-51027 (51026 `CHARACTER_DEAD`: корабель тоне); **немає** `SHIP_CHANGE_GROUP` 51012, `UPDATE_RELATIONS` 51016, `SET_COMPANION_ENEMY` 51028, `GROUP_SET_TYPE` 51020, `GROUP_SET_LOCATION_OTHER_GROUP` 51019 (переслідування), `CANNON_FIRE/RELOAD` 51002/51003, `SET_CAMERAS_ATTRIBUTE` 51004 | `Sea.cs:180-226` | **51016/51012** — зміна відносин і груп |
| `Ship` (ship) | модель, фізика, пожежі, імпульси | 🟡 модель/рух/хвилі/крен ✅ (через sea_ai); **пожежі ✅**: мітки `fireplace`, `GET_NUM_FIRE_PLACES` 50306, `ACTIVATE_FIRE_PLACE` 50304 (системи `ship_smoke` + `ship_fire` рухаються з кораблем, після часу скрипту гаснуть, звук зупиняється), найближче місце пожежі передається в `Shp_HullHit` (скрипт вирішує займання), `GET_CHARACTER_INDEX` 50307 ✅; немає `ADD_MOVE_IMPULSE` 50308 | `SeaFires.cs`, `--shipfire@N` (`=me`) | імпульс від вибуху брандера |
| `Sail` (rigging) | вітрила: роздування, згортання, дірки | ✅ згортання до рею / складання трикутних за станом 0 / 0.5 / 1 (анімація, ROLLINGSPEED, rollSSailForm/rollTSailForm), роздування й тріпотіння від вітру (fSsailWindDepend/fTsailWindDepend), поворот реїв до вітру (MAXTURNANGL), дірки: `DoSailHole`, `RandomHole2Sail`, `DeleteOneSailHole`, `funcGetSailSpeed`, `MSG_SAIL_SCRIPT_PROCESSING` 40099 "RandomSailsDmg" → `evntRandomSailDmg`, текстура `parus_hole.tga`; стан 0.5 = прямі вітрила взяті на половину (моє прочитання) | `SeaSails.cs`, скріни `--sailholes=me@N` | тінь вітру між вітрилами |
| `Rope`, `Vant`, `Flag` (rigging) | канати, ванти, прапори | 🟡 будуються з міток моделі (`RiggingFx.cs`), прапор нації ✅; `MSG_FLAG_INIT` 40200 для **прапорів міст/фортів** ❌ (`[nohandler] Flag 40200`) | `RiggingFx.cs`, `smoke_*` | прапори в локаціях і на фортах |
| `MAST` (MAST.dll) | падіння щогли | ✅ ядро проти щогли → `ShpMastDamage` (`Ship.Masts.mastN`), на 1: `EventMastFall` (скрипт шле 77500, матроси звільняють точки щогли), `DoSailHole` з ім'ям щогли для її вітрил, щогла з реями/вітрилами/прапором падає за борт і тоне, канати прибираються; зламані у збереженні щогли не будуються | `SeaSails.cs`, `SeaSailors.cs`, `--mastfall=me@N` | зіткнення щогли з кораблями/островом |
| `Island` (ISLAND) | острів, відображення | ✅ 50100; `ISLAND_START` 50102 у `[nohandler]` | `Sea.cs:101` | — |
| `locator` | локатори островів | ✅ 1504 | `Sea.cs:104` | — |
| `sea` (sea) | вода, хвилі, відображення, піна | 🟡 шейдер хвиль, френель, відблиск ✅; немає прозорості з дном (`_sb`), відображень, піни біля берега (`RESOURCE/foam`) | `OutdoorEnv.cs`, `SeaWaves.cs`, `Location.cs:105` | піна берега, прозорість |
| `Seafoam` (seafoam) | піна від корпусу | 🟡 власний «кільватер» (`SeaPhysics.cs`), класу немає | — | — |
| `AIBalls`, `BallSplash` | ядра, бризки | ✅/🟡 балістика і події ✅; кожен постріл підіймає `CannFire` (скрипт `Ball_AddBall`: розкид за точністю, дим `cancloud`, звук пострілу); бризки — системи `watersplash` + `ball_splash` з particles.ini (власний спрайт BallSplash.dll `splash.tga` не відтворено) | `SeaCombat.cs`, `Sea.cs` | — |
| `AIFort` | форти | 🟡 51005/51008, стрільба ✅; прапор форту ❌, `blots` ❌ | `SeaForts.cs` | — |
| `SEA_CAMERAS`, `SHIP_CAMERA`, `DECK_CAMERA`, `FREE_CAMERA` | камери моря | 🟡 корабельна камера ✅; **камери з палуби немає** (`DECK_CAMERA_SET_VIEWPOINT` 41800, `SeaCameras_Switch` не підіймається) | `Sea.cs:102` | камера з палуби |
| `TELESCOPE` (telescope), `ispyglass` | підзорна труба + панель інфо про корабель | 🟡 вигляд з окуляром ✅ (`telescope.ini`); `77300/77302` у `[nohandler]`, **панелі з назвою/типом/корпусом/командою цілі немає** (`MSG_ISG_UPDATE` 46202) | `Sea.cs:771-820`, `smoke_sea.log` | панель ispyglass |
| `battle_interface` (battle_interface) | HUD моря | 🟡 власний макет з оригінальних текстур (`SeaHud.cs`, `SeaCommands.cs`); `BI_IN_SET_COMMAND_MODE` 46013, `BI_MSG_REFRESH` 46005, `BI_MSG_SET_ISLAND` 46003 у `[nohandler]`; не підіймаються `evntBISelectShip`, `GetRiggingData`, `GetSailTextureData`, `BI_GetData`, `evntSetUsingAbility` | `smoke_map.log`, `smoke_fight.log` | довести до оригінального контракту |
| `shippointer` | стрілка над вибраним кораблем | ❌ (46301) | `BattleInterface.c:1854` | дрібниця |
| `IBoardingStatus` | смуги HP під час абордажу | ✅ 45025/45024: дві смуги за атрибутами скрипта; **жоден скрипт аддона не викликає `DrawCharacterHP`**, тож у грі сутність не створюється (як і в оригіналі) | `LogHud.cs` (`BoardingStatus`), `LogInterface.c:149` | — |
| `PEOPLE_ON_SHIP` | матроси на палубі | 🟡 ходять графами, біжать до гармат; падіння щогли (77500) закриває її точки; немає лазіння | `SeaSailors.cs` | — |
| `SEA_OPERATOR` | кінокамера пострілів/влучань | ❌ (`[nohandler] 77400`) | `smoke_*`, `shipBortFire.c:45` | низький |
| `AISeaGoods` | плаваючі бочки з вантажем | ❌ (`ShpEatSwimGood` не підіймається) | `AISeaGoods.c`, `MODELS/SwimGoods` | середній (лут після бою) |
| `SINKEFFECT` | вир/бульбашки при потопленні | ❌ | — | низький |
| `Sharks` (SeaCreatures) | акули | ❌ | — | низький |
| `Weather`, `Sky`, `rain`, `Lightning`, `SunGlow`, `Tornado` (WEATHER, Tornado) | погода | 🟡 небо, туман, дощ, блискавка, смерч ✅; `SunGlow` (сонце/відблиски) ❌; грому немає; не підіймаються `EWhr_GetWindAngle/Speed/FogDensity`, `WhrCalcFogColor` | `OutdoorEnv.cs`, `WeatherFx.cs` | SunGlow, звук грому |

### 1.3 Карта світу
| Клас | Оригінал | Порт | Докази | Що зробити |
|---|---|---|---|---|
| `worldmap` (WorldMap) | сцена, кораблі, зустрічі, шторми, меню | ✅ сцена, зони островів, дата, зустрічі 31100-31200, шторми 31000/31001 (хмари/дощ/блискавки, `WorldMap_PlayerInStorm`, `info.playerInStorm`, `storm.*`), вікно подій (`eventWindow`, `WorldMap_EventWindow*`), меню очікування з пропуском часу (`waitMenu`, клавіша T, `WorldMap_WaitMenu*`), `WorldMap_IsSkipEnable`; 🟡 рух/вигляд штормів і склад меню очікування — моя реконструкція (C++ коду немає), без смерчу на карті і звуку дощу | `WorldStorms.cs`, `WorldMapUi.cs`, тест `storm` | — |

## 2. Події, які має підіймати рушій (231 ім'я, 44 підіймаються)
Події інтерфейсів (`BuyPress`, `SavePress`, `ConfirmYes` ...) підіймаються **загально** через `command = ...,event:Name` в ini (`XView.Run`), тому тут не рахуються.
| Група | Підіймаються | Не підіймаються (важливі) |
|---|---|---|
| Земля, персонажі | `CharacterUpdate`, `CharactersStateUpdate`, `Location_CharacterEndTask/EntryToLocator/ExitFromLocator/FollowGo/FollowStay/Attack/Block/Fire` | `Event_ChrSnd_Attack/Hit/Dead/Body` (**звуки ударів і смерті**), `Location_CharacterFightGo/FightStay/IsFight/IsFire/SGFire/EscapeSlide/TaskFailure/BusyPos/ColThreshold/InLocator`, `Location_Character_EndAction`, `NPC_Event_GetAttackActive/GetDefenceActive/GetFireActive/GetAggresive/EnableFire` (бій NPC іде за власними правилами порту, а не за параметрами скрипта), `CharacterGroup_UpdateAlarm`, `LocationTimeUpdate`, `EventCharacterDead` |
| Море | `ShpCreate/ShpDelete/Shp_UpdParams/Shp_HullHit/Shp_Ship2ShipColl/Shp_ShipIslColl/ShpMastDamage/ShpSailDamage/Shp_NotEnoughBalls`, `Ball_*Hit`, `CannLoad/CannGRTime`, `FortCreate/FortCDamage`, `TornadoDamage`, `Ship_Bort_Fire`, `BI_*` (4) | `CannFire/CannUnload/CannGFTime/CannGFHeight/CannReCaclParam`, `ShpChCharge`, `ShpFireAction`, `Shp_ChkSituation`, `Shp2Strand`, `ShpEatSwimGood`, `Ball_FlyUpdate`, `SeaGetLayers`, `GrpChkTsk`, `AbordageCanBe`, `EventMastFall`, `DoSailHole`, `evntRandomSailDmg`, `TelescopeActive`, `SetTelescopeInfo/ReleaseTelescopeInfo`, `SeaCameras_Switch` |
| Карта | `WorldMap_UpdateDate/EncounterCreate/ShipEncounter/DeleteShipEncounter/inside_/outside_islandzone`, `ExitFromWorldMap`, `WorldMap_PlayerInStorm`, `WorldMap_WaitMenu*`, `WorldMap_EventWindow*`, `WorldMap_IsSkipEnable` | — |
| Інтерфейс/керування | `ievnt_command`, `FormatedTextActivate`, `SetScrollerPos`, `QuestActivate`, `DialogEvent`, `Control Activation` | `evntKeyChoose` (вибір клавіш — замінено власним екраном), `InterfaceKey/MainMenuKey`, `DialogCancel`, `EmergencyDialogExit`, `FaderEvent_StartFadeIn/EndFadeIn`, `ScrollPosChange/ScrollTopChange` (частково через вузли) |

## 3. Інтерфейси (`RESOURCE/INI/INTERFACES`, 66 ini)
| Екран (ini) | Порт | Доказ |
|---|---|---|
| mainmenu, boal_newchar (створення героя), character, save_load, ship, shiphold, shipyard, store | ✅ відкривається, сценарії | `[xinterface] loading` у `game/smoke_*.log`, `reports/*` |
| items, perks, salary, TradeBook, NationRelation, helpscreen, option_screen, controls (власний екран клавіш), cannons, Transfer_crew, itemsbox (скриня), boal_questbook/QuestBook, passengers, hirecrew, itemstrade, transfer_goods | 🟡 відкриваються і рендеряться (скріни `game/review/*.png`, STATUS); логів сценаріїв немає | `review/items.png`, `perks*.png`, `salary.png`, `tradebook.png`, `nations.png`, `help.png`, `options.png`, `controls.png`, `cannons.png`, `tcrew.png`, `chest.png` |
| ransack_main (Transfer_main.ini), обмін кораблями, вихід із потопленням ворога | ✅ сценарій `board`: екран «Грабунок» з нашим і ворожим кораблем, «Обміняти», вихід з підтвердженням | `review/board_ransack.png`, `review/board_swap.png` |
| ransack_crew/captain/join (здача ворога), transfer_characters (офіцер на призовий корабель), fortcapture, citycapture, colony_Capture/Munity/Siege, Forts_Escadr/Siege, Munitycapture, Repayment_colony, materials | ❓ жодного відкриття в логах | — |
| boal_cards, boal_dice (азартні ігри), boal_map, map_cabin, map_show, map_read (карти), boal_statistic, boal_price (прайс), boal_hero (вільний герой), NationLegend, addon_team, boal_frame | ❓ не перевірялися | — |
| boal_setup, boal_beta, boal_debug | 🟡 сторінка бета доступна через `settings.ini` | `Main.cs:120` |
| LanguageSelect, select_menu, playgame, endgame, selectCharacter, defaultnode | ⚪/❓ (вибір мови замінено портом; endgame — фінал гри не перевірено) | — |
| Типи вузлів | ✅ PICTURE, BUTTON, TEXTBUTTON, SCROLLIMAGE, FOURIMAGES, SLIDER, SCROLLER, SCROLLBAR, LRCHANGER, CHANGER, FORMATEDTEXT, STATUSLINE, QUEST*; ❌ VIDEO (фон не грає), KEYCHOOSER, EDITBOX (**назва корабля/імені не вводиться**: `ShipNameChange`), GLOWS/GLOWCURSOR, CONTEXTHELP, XBOX (⚪) | `XInterface.cs:364-390` |
| Шрифти | ❌ шрифти оригіналу (`fonts.ini`, растрові) не використовуються — системний шрифт Godot | `fonts*.ini` ніде не читаються |

## 4. Ігрові області
| Область | Оригінальна функція | Порт | Доказ | Що зробити |
|---|---|---|---|---|
| Земля | локації, освітлення, патчі, перезавантаження | ✅ | `smoke_door.log`, STATUS 85 % | — |
| Земля | анімовані пропси, ліхтарі, нічне світло, тіні | ❌ | `Location.cs:50,58` | див. 1.1 |
| Земля | тварини, птахи, щури, ящірки, метелики | ❌ | `[nohandler] Animals`, `EX_MSG`→0 | — |
| Земля | рукопашний бій | 🟡 удари/блок/парирування/постріл власні, параметри NPC-подій скрипта ігноруються, звуків ударів немає | `Characters.cs`, `smoke_fight.log` | `Event_ChrSnd_*`, `NPC_Event_*` |
| Земля | діалоги | ✅ текст; ❌ голова, голосові привітання | `Dialog.cs` | Greetings |
| Земля | предмети на землі, скрині | 🟡 скриня відкривається (`review/chest.png`, `bx*.png`), підбір ❓ | `Main.cs:238` | сценарій тесту |
| Земля | квести, кат-сцени | 🟡 логіка скриптова ✅; рух камери `MSG_CAMERA_MOVE`, `WideScreen`, відео ❌ | `quests_movie.c` | — |
| Земля | земний HUD (`battle_land_interface`) | ✅ портрети + меню команд (розмова, швидкий перехід, двері, зілля, скриня) | `LandHud.cs`, тест `landhud` | — |
| Море | кораблі, рух, хвилі, зіткнення | ✅/🟡 | `smoke_sea.log`, STATUS 50-75 % | — |
| Море | ШІ кораблів | 🟡 задачі move/attack/follow/defend/flee; немає переслідування групи, зміни груп/відносин | `Sea.cs:180` | 51012/51016/51019/51020 |
| Море | гармати, ядра, пошкодження, потоплення | 🟡 без вогню на палубі, без дірок у вітрилах, без падіння щогли, без SINKEFFECT | `SeaCombat.cs` | — |
| Море | абордаж | ✅/🟡 повний цикл (тест `board`): ворог сильніший — бере на абордаж сам (`Ship_CheckMainCharacter` з `SeaAI.Update`), гравець — командою «Абордаж» (`Sea_AbordageLoad`); верхня палуба → гарматна → каюта (команди знаходять одна одну через радіус «say» груп), екран грабунку, обмін кораблями, потоплення, повернення в море; сцени з абордажними гаками немає; здача ворога (ransack_join/captain) не перевірена | `review/board_*.png`, `smoke_board.log` | ransack_join/captain, офіцер через transfer_characters |
| Море | форти | 🟡 стріляють, отримують шкоду; захоплення фортів/колоній (екрани `fortcapture`, `colony_*`) ❓ | `SeaForts.cs` | сценарій |
| Море | камери палуби/каюти, підзорна труба | 🟡 труба ✅ без панелі; каюта/палуба як локації ✅; камера з палуби ❌ | — | — |
| Море | погода, шторм, смерч | 🟡 дощ/блискавка/смерч ✅, грім ❌, SunGlow ❌ | `WeatherFx.cs` | — |
| Море | частинки (дим, вогонь, бризки) | ✅ оригінальні системи `INI/PARTICLES` (дим гармат, вибухи, бризки, пожежі кораблів і фортів); у локаціях — смолоскипи, свічки, вогнища, дим з локаторів | `Particles.cs` | — |
| Море | плаваючий вантаж, акули, чайки | ❌ | — | — |
| Карта світу | сцена, кораблі, зустрічі, дата | ✅/🟡 | `smoke_map.log` | — |
| Карта світу | шторми, меню очікування, вікно подій | ✅/🟡 шторм на карті -> море в шторм (погода, шкода від вітру); вікна з текстур `WorldMap/Interfaces` | `smoke_storm.log` | — |
| HUD моря | навігатор, ескадра, ціль, команди | 🟡 власний макет; немає панелі труби, стрілки корабля, активних навичок | `SeaHud.cs` | — |
| Збереження | `OnSave/OnLoad`, мініатюри | ✅ | unit-тест, `reports/*` | — |
| Налаштування | `SAVEOPTIONS/LOADOPTIONS` 45052/45053 | 🟡 повертають 0; порт зберігає свої в `settings.ini` | `XInterface.cs:152` | — |
| Керування | `CONTROLS/*.c` + PC_CONTROLS.dll | 🟡 власна таблиця клавіш, KEYCHOOSER замінено | `KeyBindings.cs` | — |
| Звук | аліаси, схеми, музика, привітання, звуки персонажів | 🟡 привітання NPC на початку діалогу (`Dialog.greeting`, гучність мови); кроки за матеріалом патча `.ptc` (`snd_grass` → `step_grass`), свист/вихоплення/ховання шаблі і пістоль з подій `.ani`, удар у тіло / дзвін клинків, постріл, `Event_ChrSnd_Attack/Hit/Dead/Body`; 3D за відстанями аліасів; немає грому, `SET_3D_PARAM`; моменти бойових звуків — мої таймери, не C++ | `SoundView.cs`, `Characters.cs`, `Dialog.cs` | — |
| Відео | 9 роликів `.wmv` | ❌ | `Main.cs:144` | конвертація (з дозволу) |
| Переклад | UI ~100 %, діалоги ~79 % машинний | 🟡 | `docs/STATUS.md` | редагування перших годин |

## 5. Наступні кроки (за помітністю для гравця)
1. ~~**Земний HUD `battle_land_interface`**~~ — зроблено (`LandHud.cs`).
2. ~~**Повний цикл абордажу і захоплення**~~ — зроблено (51013/51014/51026, `IBoardingStatus`, `FindClassNext`, тест `board`); лишилось перевірити здачу ворога і призначення офіцера через екран.
3. ~~**Шторми на карті світу** (`MSG_WORLDMAP_CREATESTORM`, `WorldMap_PlayerInStorm`) і меню очікування/вікно подій на карті.~~ — зроблено (`WorldStorms.cs`, `WorldMapUi.cs`, тест `storm`).
4. ~~**Вітрила**: згортання/розгортання за станом, роздування вітром, дірки (`RandomSailsDmg`), падіння щогли (MAST) з реакцією матросів (77500).~~ — зроблено (`SeaSails.cs`).
5. ~~Пожежі на кораблях і частинки з оригінальних `INI/PARTICLES/*.ini`~~ — зроблено (`Particles.cs`, `SeaFires.cs`).
6. ~~**Звуки персонажів** (`Event_ChrSnd_Attack/Hit/Dead/Body`) і **голосові привітання NPC** (`Greetings_alias.ini`).~~ — зроблено (`SoundView.cs` 3D, `Characters.cs` події `.ani`, `Dialog.cs`).
7. **Параметри бою NPC зі скрипта** (`NPC_Event_GetAttackActive/GetDefenceActive/GetFireActive/GetAggresive`, `Location_CharacterIsFight/FightGo/FightStay`) замість власних правил.
8. **Освітлення локацій**: `MSG_LOCATION_ADD_LIGHT`, лампи вночі (`MODEL_LAMPS`), анімовані моделі (`SET_ROT/SET_UVS`: млини, вода), тіні персонажів.
9. **Відносини і групи на морі**: `UPDATE_RELATIONS`, `SHIP_CHANGE_GROUP`, `SET_COMPANION_ENEMY`, `GROUP_SET_TYPE`, `GROUP_SET_LOCATION_OTHER_GROUP` (переслідування).
10. **Перевірити непротестовані екрани**: захоплення фортів/колоній (`fortcapture`, `colony_*`, `Forts_*`), `Transfer_main`, `transfer_characters`, `materials`, карти (`map_*`, `boal_map`), азартні ігри (`boal_cards`, `boal_dice`), `endgame`.
11. **EDITBOX** (перейменування корабля, введення назв збережень) і `MSG_INTERFACE_DEL_SCROLLIMAGE/REFRESH_SCROLL/DELETE_PICTURE/DELETE_STRING`, `LOCK_NODE`.
12. **Панель підзорної труби** (`ispyglass`, `MSG_ISG_UPDATE`) і **камера з палуби** (`DECK_CAMERA`).
13. **Атмосфера**: чайки/метелики/риби (`Animals`), `SunGlow`, грім, піна біля берега, прозора вода з дном, `AISeaGoods` (плаваючий вантаж), акули, `SINKEFFECT`.
14. **Відео** (9 `.wmv`) — потрібна конвертація в `.ogv` (інструмент лише з дозволу власника); кат-сцени: `MSG_CAMERA_MOVE`, `WideScreen`.
15. **Шрифти оригіналу** (`fonts.ini`), прапори міст і фортів (`MSG_FLAG_INIT` для локацій), `blots`, `shippointer`, `ActivePerkShower`; оновити `CLAUDE.md`/`ARCHITECTURE.md`/`status_data.txt` згідно з розділом 0.
