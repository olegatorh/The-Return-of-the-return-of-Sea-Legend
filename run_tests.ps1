# Regression checks without playing by hand.
#   ./run_tests.ps1            unit tests (interpreter semantics, parser corpus, headless boot, format readers, language layer) + game build
#   ./run_tests.ps1 -Smoke     + Godot main-menu smoke run (needs a window; ~10 s), fails on [errors]/[unresolved]/script exceptions
#   ./run_tests.ps1 -Intro     + the reference intro run from the main menu into the first location (~3 min), same checks
#   ./run_tests.ps1 -Sea       + sea scenarios from saves/quicksave (town port): leave the port, sail, moor again; sea -> world map -> sea (~2 min)
#   ./run_tests.ps1 -Contracts + engine contract audit (tools/contract_audit.py -> docs/CONTRACTS.md), run after the other runs so it sees fresh logs
param([switch]$Smoke, [switch]$Intro, [switch]$Sea, [switch]$Hidden, [switch]$Contracts, [switch]$Quest)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fail = 0

Write-Host '== unit tests' -ForegroundColor Cyan
dotnet test "$root\StormScript.Tests\StormScript.Tests.csproj" -nologo -v q
if ($LASTEXITCODE -ne 0) { $fail++ }

Write-Host '== game build' -ForegroundColor Cyan
dotnet build "$root\game\SeaDogs.csproj" -nologo -v q
if ($LASTEXITCODE -ne 0) { $fail++ }

function Invoke-Godot([string]$name, [string[]]$userArgs, [int]$timeoutSec, [string[]]$mustContain = @()) {
    $godot = "$root\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
    $shot = "$root\game\smoke_$name.png"
    $log = "$root\game\smoke_$name.log"
    Remove-Item $shot, $log -ErrorAction SilentlyContinue
    Write-Host "== godot run '$name'" -ForegroundColor Cyan
    $p = Start-Process $godot -ArgumentList (@('--path', "`"$root\game`"") + $(if ($Hidden) { @('--position', '9000,9000', '--resolution', '1280x720') } else { @('--position', '320,180', '--resolution', '1280x720') }) + @('--') + $userArgs + @("--shot=smoke_$name.png")) `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow
    if (-not $p.WaitForExit($timeoutSec * 1000)) { $p.Kill(); Write-Host "TIMEOUT after $timeoutSec s" -ForegroundColor Red; return 1 }
    $text = (Get-Content $log, "$log.err" -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"
    $bad = $text -split "`n" | Where-Object { $_ -match '^\[errors\]|^\[unresolved\]|SCRIPT ERROR|^ERROR: System\.|Unhandled exception|^ERROR: Invalid polygon data' }
    if (-not (Test-Path $shot)) { Write-Host 'no screenshot was produced (the run did not reach the end)' -ForegroundColor Red; return 1 }
    foreach ($m in $mustContain) { if ($text -notmatch [regex]::Escape($m)) { Write-Host "expected in the log: $m" -ForegroundColor Red; return 1 } }
    if ($bad) { Write-Host 'problems:' -ForegroundColor Red; $bad | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }; return 1 }
    Write-Host "ok (screenshot: game\smoke_$name.png, log: game\smoke_$name.log)" -ForegroundColor Green
    return 0
}

if ($Smoke) { $fail += Invoke-Godot 'menu' @('--shot-frame=300') 120 }
if ($Intro) {
    $a = @('--click=B_NEWGAME@200', '--click=RUNGAME@400')
    foreach ($i in 0..12) { $a += "--answer=1@$(2700 + 100 * $i)" }
    $fail += Invoke-Godot 'intro' ($a + @('--shot-frame=9500')) 600
}

# first quest chain from a NEW game (docs/playtest_runs.md, stage Q): intro -> port -> tavern: Ines "who hires a crew?" -> William Blaker -> night in the port ->
# peaceful way to the ship (Ship_deck) -> the 3 mutineers (hero immortal, group killed by a hook) -> sailor in the port -> Sailor_Blaker gives the Lugger and
# location.from_sea = QC_port. Also checks the Abracham Gray branch of the tutorial deck (start2 -> start3 -> start4: fight) in the same run
if ($Quest) {
    $q = @('--click=B_NEWGAME@200', '--click=RUNGAME@400')
    foreach ($i in 0..7) { $q += "--answer=1@$(2700 + 100 * $i)" }
    $q += @('--answer=1@3400', '--answer=1@3500')       # Abracham: "Not the first time" -> game_begin (the fighting branch is covered by the 'deckfight' run of docs/playtest_runs.md)
    $q += @('--call=DoReloadCharacterToLocation(QC_Tavern,reload,reload1)@4400', '--dialog=Ines_Diaz@4800', '--answer=1@4900', '--answer=1@5000', '--answer=2@5100', '--answer=1@5200', '--answer=1@5300',
            '--dialog=William_Blaker@5700', '--answer=1@5800', '--answer=1@5900', '--answer=1@6000', '--answer=2@6700', '--answer=1@7500', '--answer=1@7700', '--call=LAi_SetImmortal(#Blaze,1)@7650',
            '--killgroup=TmpEnemy@8300', '--killgroup=TmpEnemy@8400')
    foreach ($f in 8800, 9000, 9200, 9400, 9600) { $q += "--answer=1@$f" }
    $q += @('--dump=Characters[0].Ship.Type@10000', '--dump=Characters[0].location.from_sea@10000', '--shot-frame=10100')
    $fail += Invoke-Godot 'firstquest' $q 600 @('answer 1 -> node ''Talk_about_Blaker''', 'answer 1 -> node ''Take_Ship_3''', 'answer 2 -> node ''Take_Ship_6''', 'answer 1 -> node ''Take_Ship_8''',
                                               'node ''node1''', 'Type = "26"', 'from_sea = "QC_port"')
}

if ($Sea) {
    if (-not (Test-Path "$root\saves\quicksave.sdsave")) { Write-Host 'no saves\quicksave.sdsave: play to the first town and press F4, or skip -Sea' -ForegroundColor Yellow }
    else {
        $a = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
               '--sail=+1@700', '--sail=+1@705', '--move=1,0@900-1100', '--act@1500', '--shot-frame=1900')
        $fail += Invoke-Godot 'sea' $a 300 @('[sea] ship Blaze', '[sea] action: Moor', 'locations=1')
        # sea -> world map -> back to the sea at the island (the tutorial deck scene the script starts on the first map visit is not reached before the shot)
        $b = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
               '--sail=+1@700', '--seapos=-600,-101,4.71@800', '--ctl=BI_MapEnter@1000', '--act@1250', '--shot-frame=2000')
        $fail += Invoke-Godot 'map' $b 300 @('[map] world map built', '[map] exit to sea at QuebradasCostillas')
        # day / night of the world map: the script sets the clock (worldMap.date.hour), the engine multiplies the map with the daylight colour of that hour
        $nm = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
                '--sail=+1@700', '--seapos=-600,-101,4.71@800', '--ctl=BI_MapEnter@1000', '--set=worldMap.date.hourPerSec=0.01@1250', '--set=worldMap.date.hour=23@1251', '--set=worldMap.date.hourPerSec=0.01@1400', '--set=worldMap.date.hour=23@1401', '--shot-frame=1420')
        $fail += Invoke-Godot 'mapnight' $nm 300 @('[map] daylight at 23')
        # world-map storm: the script creates it (wdmCreateStorm -> MSG_WORLDMAP_CREATESTORM), a hook moves it onto the ship, WorldMap_PlayerInStorm sends us
        # to the sea with Login.Storm = 1 (storm weather; AIShip.c applies the wind damage)
        $sm = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
                '--sail=+1@700', '--seapos=-600,-101,4.71@800', '--ctl=BI_MapEnter@1000', '--mapstorm=0,0@1200', '--dump=WeatherParams.Storm@1800', '--shot-frame=2000')
        $fail += Invoke-Godot 'storm' $sm 300 @('[map] storm created', '[map] the ship sailed into storm 0', 'Storm = "1"')
        # map traffic: the player's ship stands still near Quebradas Costillas, the script's war / pirate encounters are off, two merchants are asked to sail to the island Sent (260 units away);
        # the engine sails them round the coast (A* on the land mask) and removes them at the port ("arrived at Sent"); the map runs 4 steps per frame, the clock is slowed so the quests do not interfere
        $env:SEADOGS_MAPLOG = '1'; $env:SEADOGS_MAPSPEED = '4'; $env:SEADOGS_MAPSAIL = '0'; $env:SEADOGS_MAPNOKILL = '1'; $env:SEADOGS_SEED = '7'
        $tr = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
                '--sail=+1@700', '--seapos=-600,-101,4.71@800', '--ctl=BI_MapEnter@1000', '--set=worldMap.date.hourPerSec=0.02@1010', '--set=Characters[0].worldmapencountersoff=1@1010',
                '--call=wdmCreateMerchantShip(0,Sent,0)@1300', '--call=wdmCreateMerchantShip(0,Sent,0)@1350', '--shot-frame=5000')
        $fail += Invoke-Godot 'maptraffic' $tr 400 @('merchant type 0', 'bound for Sent', 'arrived at Sent', 'travelled ')
        Remove-Item Env:SEADOGS_MAPNOKILL
        # a pursuer (follow ship = pirate / hunter / war squadron) sails to the player and attacks within the action radius: the script gets WorldMap_ShipEncounter and leaves the map
        $env:SEADOGS_MAPSAIL = '0'
        $fo = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
                '--sail=+1@700', '--seapos=-600,-101,4.71@800', '--ctl=BI_MapEnter@1000', '--set=worldMap.date.hourPerSec=0.02@1010', '--set=Characters[0].worldmapencountersoff=0@1010',
                '--call=wdmCreateFollowShip(1,0)@1300', '--shot-frame=3500')
        $fail += Invoke-Godot 'mapfollow' $fo 300 @('+follow type 1', 'encounter with ship type 1 (Follow')
        # a merchant never attacks: the player has to come close and press Enter (WdmPlayerShip::ExitFromMap); without it the ship only sails by
        $me = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
                '--sail=+1@700', '--seapos=-600,-101,4.71@800', '--ctl=BI_MapEnter@1000', '--set=worldMap.date.hourPerSec=0.02@1010', '--set=Characters[0].worldmapencountersoff=1@1010',
                '--call=wdmCreateMerchantShip(0,Sent,0)@1300', '--mapmeet@1400', '--key=Enter@1410', '--shot-frame=2000')
        $env:SEADOGS_MAPSPEED = '1'
        $fail += Invoke-Godot 'mapmerchant' $me 300 @('+merchant type 0', 'encounter with ship type 0 (Merchant')
        # script-generated encounter -> "Sails on the horizon" -> Attack: SeaLogin must put the encounter group (fenc_*) into the sea and give it the attack task
        $env:SEADOGS_MAPSPEED = '1'; $env:SEADOGS_SEED = '3'
        $ec = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
                '--sail=+1@700', '--seapos=-600,-101,4.71@800', '--ctl=BI_MapEnter@1000', '--set=worldMap.date.hourPerSec=0.05@1010', '--set=Characters[0].worldmapencountersoff=0@1010',
                '--mapmeet@1500', '--key=Enter@1510', '--key=Enter@1700', '--shot-frame=3200')
        $fail += Invoke-Godot 'mapencounter' $ec 300 @('encounter with ship type 0', '[sea] ship fenc_', 'task: attack Blaze')
        Remove-Item Env:SEADOGS_MAPLOG, Env:SEADOGS_MAPSPEED, Env:SEADOGS_MAPSAIL, Env:SEADOGS_SEED
        # Martinique: the French squadron is placed abeam and shot at; the script must apply the damage (a perfect gunner for this shot: the per-ball
        # dispersion of Ball_AddBall grows with 1.2 - TmpSkill.Accuracy and the tutorial gunner would miss now and then)
        $c = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
               '--sail=+1@700', '--seapos=-600,-101,4.71@720', '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--mappos=-193,768,0@1200', '--act@1250',
               '--seapos=-110,100,0@2400', '--duel@2450', '--set=Characters[0].TmpSkill.Accuracy=1@2495', '--fire@2500', '--shot-frame=3000')
        # trade: open the store, choose the ship, buy 3 portions of cannon balls
        $t = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@400', '--set=Characters[0].Money=5000@410', '--call=LaunchStore(0)@500', '--key=Enter@600', '--click=BUY_BUTTON@650',
               '--call=ProcessTradeIncrease()@700', '--call=ProcessTradeIncrease()@705', '--call=ProcessTradeIncrease()@710', '--click=OK_BUTTON@760', '--dump=Characters[0].Ship.Cargo.Goods.Balls@900', '--dump=Characters[0].Money@901',
              '--mouse=SELL_BUTTON@920', '--key=Right@940', '--key=Right@950', '--mouse=OK_BUTTON@980', '--dump=Characters[0].Ship.Cargo.Goods.Balls@1000', '--dump=Characters[0].Money@1001', '--shot-frame=1005')
        # buy 60 balls (100 -> 160, money 5000 -> 4985), then sell 40 through the real SELL button / Right keys / OK (160 -> 120, money +4)
        $fail += Invoke-Godot 'trade' $t 200 @('Balls = "160"', 'money = "4985"', 'Balls = "120"', 'money = "4989"')
        # store, no money: the quantity cannot grow, OK changes nothing (Balls 100, money 0); 3 gold buy only 12 balls (112, money 0)
        $t0 = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@400', '--set=Characters[0].Money=0@410', '--call=LaunchStore(0)@500', '--key=Enter@600', '--mouse=BUY_BUTTON@650',
                '--key=Right@700', '--key=Right@705', '--key=Right@710', '--dump=GameInterface.strings.TradeQuantity@750', '--mouse=OK_BUTTON@800', '--dump=Characters[0].Ship.Cargo.Goods.Balls@900', '--dump=Characters[0].Money@901', '--shot-frame=910')
        $fail += Invoke-Godot 'tradenomoney' $t0 200 @('TradeQuantity = "0"', 'Balls = "100"', 'money = "0"')
        $t3 = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@400', '--set=Characters[0].Money=3@410', '--call=LaunchStore(0)@500', '--key=Enter@600', '--mouse=BUY_BUTTON@650',
                '--key=Right@700', '--key=Right@705', '--key=Right@710', '--dump=GameInterface.strings.TradeQuantity@750', '--mouse=OK_BUTTON@800', '--dump=Characters[0].Ship.Cargo.Goods.Balls@900', '--dump=Characters[0].Money@901', '--shot-frame=910')
        $fail += Invoke-Godot 'tradepoor' $t3 200 @('TradeQuantity = "12"', 'Balls = "112"', 'money = "0"')
        # store, hold nearly full (capacity 250): "buy all" stops at the free space (1500 balls), the free space ends at 0 and the quantity cannot grow any more
        $tc = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@400', '--set=Characters[0].Money=100000@410', '--set=ShipsTypes[169].Capacity=250@411', '--call=LaunchStore(0)@500', '--key=Enter@600',
                '--mouse=BUY_BUTTON@650', '--mouse=B_BUYALL@700', '--dump=GameInterface.strings.TradeQuantity@750', '--mouse=OK_BUTTON@800', '--dump=Characters[0].Ship.Cargo.Goods.Balls@900', '--dump=GameInterface.strings.OnShipSpaceQuantity@902',
                '--mouse=BUY_BUTTON@950', '--key=Right@1000', '--dump=GameInterface.strings.TradeQuantity@1050', '--shot-frame=1060')
        $fail += Invoke-Godot 'tradefull' $tc 200 @('TradeQuantity = "1500"', 'Balls = "1600"', 'OnShipSpaceQuantity = "0"', 'TradeQuantity = "0"')
        # store, "sell all": every good the store trades leaves the hold at once (the planks are not on the list), the money grows
        $ts = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@400', '--set=Characters[0].Money=5000@410', '--call=LaunchStore(0)@500', '--key=Enter@600', '--mouse=SELLEVERYTHING_BUTTON@700',
                '--dump=Characters[0].Ship.Cargo.Goods.Balls@800', '--dump=Characters[0].Ship.Cargo.Goods.Powder@801', '--dump=Characters[0].Money@802', '--shot-frame=810')
        $fail += Invoke-Godot 'tradesellall' $ts 200 @('Balls = "0"', 'Powder = "0"', 'money = "10230"')
        # store, a companion: a lugger (William Blaker's Victoria) joins, Right in the ship choice selects it (the four-image attributes are GameInterface.FourImage), the purchase goes into ITS hold (900 -> 960), not the hero's
        $tp = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@400', '--set=Characters[0].Money=5000@405', '"--set=Characters[0].GenQuest.CallFunctionParam.CharacterIntoCompanion.Id=William Blaker@410"',
                '--set=Characters[0].GenQuest.CallFunctionParam.CharacterIntoCompanion.Remove=1@411', '--call=Character_into_companion()@420', '--call=LaunchStore(0)@500', '--key=Right@550', '--key=Enter@600', '--mouse=BUY_BUTTON@650',
                '--key=Right@700', '--key=Right@705', '--key=Right@710', '--dump=GameInterface.strings.ShipName@750', '--mouse=OK_BUTTON@800', '--dump=Characters[659].Ship.Cargo.Goods.Balls@900', '--dump=Characters[0].Ship.Cargo.Goods.Balls@901', '--dump=Characters[0].Money@902', '--shot-frame=910')
        $fail += Invoke-Godot 'tradecompanion' $tp 200 @('ShipName = "', 'Balls = "960"', 'Balls = "100"', 'money = "4985"')
        # items trade (itemstrade.ini): the captain's potions: buy (Right +1, +1, Shift+Right +10 capped by the stock 5, Left -1 = 4: 5 -> 9, 4 x 139 gold), then sell: the quantity starts at everything (9), Left twice = 7 sold (9 -> 2, 7 x 75 gold)
        $ti = @('--ctl=Quick_Load@200', '--set=Characters[0].Money=5000@405', '--call=LaunchItemsTrade(#William+Blaker)@500', '--key=Right@600', '--key=Right@610', '--mouse=BUY_BUTTON@650',
                '--key=Right@700', '--key=Right@705', '--dump=GameInterface.strings.TradeQuantity@710', '--key=Shift+Right@720', '--dump=GameInterface.strings.TradeQuantity@730', '--key=Left@740', '--dump=GameInterface.strings.TradeQuantity@750',
                '--mouse=OK_BUTTON@800', '--dump=Characters[0].Items.potion1@900', '--dump=Characters[0].Money@901', '--mouse=SELL_BUTTON@950', '--key=Left@1000', '--key=Left@1005', '--mouse=OK_BUTTON@1050', '--dump=Characters[0].Items.potion1@1100', '--dump=Characters[0].Money@1101', '--shot-frame=1110')
        $fail += Invoke-Godot 'itemstrade' $ti 200 @('TradeQuantity = "3"', 'TradeQuantity = "5"', 'TradeQuantity = "4"', 'potion1 = "9"', 'money = "4444"', 'potion1 = "2"', 'money = "4969"')
        # screens that must open without errors: ship hold and shipyard
        $fail += Invoke-Godot 'hold' @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@400', '--call=LaunchShipHold()@500', '--shot-frame=700') 200
        $fail += Invoke-Godot 'yard' @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@400', '--call=LaunchShipyard(#Esteban+Castillo)@500', '--shot-frame=700') 200
        # controls: after a screen has been opened and closed the REAL keys must still work (the empty interface view used to swallow Space): the door is opened with the Space key
        $d = @('--ctl=Quick_Load@200', '--ctl=Interface@300', '--key=Escape@450', '--goto=reload.reload1@600', '--key=Space@650', '--shot-frame=1000')
        $fail += Invoke-Godot 'door' $d 300 @('[land] action: Reload')
        # chest: the usurer's house of the port town, walk onto the box locator, Space opens the items box, give 2 coins into it, Escape, Space opens it again
        $x = @('--ctl=Quick_Load@200', '--call=DoReloadCharacterToLocation(QC_UsurerHouse,reload,reload1)@300', '--set=Characters[0].GenQuest.God_hit_us=0@650', '--goto=box.box1@700',
               '--key=Space@760', '--mouse=ONEDOWN_BUTTON@820', '--mouse=ONEDOWN_BUTTON@840', '--dump=Characters[0].Money@870', '--dump=Locations[192].box1.Money@870',
               '--key=Escape@900', '--key=Space@1000', '--shot-frame=1100')
        $fail += Invoke-Godot 'chest' $x 300 @('[land] action: OpenBox', 'itemsbox.ini', 'money = "99998"', 'Money = "2"')
        # land HUD: Enter opens the command menu, fast travel to the tavern (second choice: the place), then talk to a man brought next to the hero
        $lh = @('--ctl=Quick_Load@200', '--key=Enter@500', '--key=Enter@530', '--key=Right@540', '--key=Right@550', '--key=Enter@560', '--bring=William_Blaker@800',
                '--key=Enter@1000', '--key=Right@1010', '--key=Enter@1030', '--shot-frame=1150')
        $fail += Invoke-Godot 'landhud' $lh 300 @("[land] command BI_FastReload 'reload3'", "[land] command BI_DialogStart", '[landhud] started=True show=False')
        # land melee by the script's rules: two townsmen of the port are put into hostile groups (LAi_group_FightGroups -> warrior type -> fight template);
        # the engine asks NPC_Event_GetAttackActive/GetDefenceActive/GetAggresive/GetFireActive/EnableFire (values = 0.3 + 0.7 / 0.3 + 0.35 x fight level),
        # reports FightStay, and the blows go back through Location_CharacterAttack/Block; F draws the hero's sword after Location_CharacterIsFight
        $ml = @('--ctl=Quick_Load@200', '--goto=goto.goto2@300', '--call=LAi_SetWarriorType(#Gilles+Roche)@400', '--call=LAi_SetWarriorType(#Terry++Snider)@400',
                '--call=LAi_group_MoveCharacter(#Gilles+Roche,TestA)@410', '--call=LAi_group_MoveCharacter(#Terry++Snider,TestB)@410', '--call=LAi_group_FightGroups(TestA,TestB,1)@420',
                '--call=LAi_SetHP(#Gilles+Roche,400,400)@430', '--call=LAi_SetHP(#Terry++Snider,400,400)@430', '--key=F@450', '--shot-frame=1600')
        $fail += Invoke-Godot 'melee' $ml 300 @('[fight] Gilles Roche vs Terry  Snider: attack=1/s defence=0.65/s', '[fight] Terry  Snider vs Gilles Roche:', 'script-driven=True player fightMode=True')
        # a hostile soldier against the HERO (group 'player' made enemy of TestB): he must run up, swing and wound the hero within the run (the NPC rolls its
        # wishes every 0.2 s like np_character.cpp, so the attack rate of the script means a blow as soon as the last one is over). The hero is drawn while running:
        # he stops for the draw animation (log "hero stopped for the draw") and runs on afterwards
        $mh = @('--ctl=Quick_Load@200', '--call=LAi_SetWarriorType(#Terry++Snider)@400', '--call=LAi_group_MoveCharacter(#Terry++Snider,TestB)@410', '--call=LAi_group_FightGroups(player,TestB,1)@420',
                '--call=LAi_SetHP(#Terry++Snider,400,400)@430', '--set=Characters[0].chr_ai.hp=900@431', '--set=Characters[0].chr_ai.hp_max=900@431', '--bring=Terry__Snider:7@440', '--key=F@460', '--shot-frame=1500')
        $fail += Invoke-Godot 'meleehero' $mh 300 @('[fight] Terry  Snider vs Blaze:', '[fight] the hero was hit by Terry  Snider')
        $dr = @('--ctl=Quick_Load@200', '--run', '--move=0,1@480-1100', '--key=F@560', '--shot-frame=1200')
        $fail += Invoke-Godot 'drawrun' $dr 300 @('[move] hero stopped for the draw', '[move] hero moves again')
        # 'sail to' the port from the command menu: the squadron is moved next to the port's reload locator behind the fader, where mooring is offered
        $st = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
                '--seapos=-500,-400,3.0@700', '--call=SeaAI_SailToLocator(reload_1)@800', '--act@1300', '--shot-frame=1500')
        $fail += Invoke-Godot 'sailto' $st 300 @('[sea] sail to', '[sea] action: Moor')
        $env:SEADOGS_PLAYERHP = '9000'   # the French squadron now hits as the original does: keep the test ship afloat
        $fail += Invoke-Godot 'fight' $c 400 @('fires 4 cannons', 'hit on CapitanFF01', 'fort FalaiseDeFleur Commander: 38 cannons')
        # relations: France is made neutral (UpdateRelations -> AI_MESSAGE_UPDATE_RELATIONS), the squadron a trade group (AI_MESSAGE_GROUP_SET_TYPE); our broadside
        # raises ShpFireAction, the script (Ship_FireAction -> Ship_NationAgressive, quest NationUpdate) turns the ship hostile and gives its group the attack task.
        # CheckFlagYet on the duel ship: the script's random flag check (AIShip.c Ship_CheckFlagEnemy) could otherwise make it hostile before our shot
        $n = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
               '--sail=+1@700', '--seapos=-600,-101,4.71@720', '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--mappos=-193,768,0@1200', '--act@1250',
               '--seapos=-110,100,0@2400', '--call=SetNationRelation2MainCharacter(1,1)@2430', '--call=Group_SetType(FortFranceSquadron,trade)@2430', '--call=UpdateRelations()@2435', '--set=Characters[706].CheckFlagYet=1@1500', '--set=Characters[706].CheckFlagYet=1@1560', '--set=Characters[706].CheckFlagYet=1@1620', '--set=Characters[706].CheckFlagYet=1@1680', '--set=Characters[706].CheckFlagYet=1@1740', '--set=Characters[706].CheckFlagYet=1@1800', '--set=Characters[706].CheckFlagYet=1@1860', '--set=Characters[706].CheckFlagYet=1@1920', '--set=Characters[706].CheckFlagYet=1@1980', '--set=Characters[706].CheckFlagYet=1@2040', '--set=Characters[706].CheckFlagYet=1@2100', '--set=Characters[706].CheckFlagYet=1@2160', '--set=Characters[706].CheckFlagYet=1@2220', '--set=Characters[706].CheckFlagYet=1@2280', '--set=Characters[706].CheckFlagYet=1@2340', '--set=Characters[706].CheckFlagYet=1@2400', '--set=Characters[706].CheckFlagYet=1@2440',
               '--duel@2450', '--fire@2700', '--shot-frame=3100')
        $fail += Invoke-Godot 'neutral' $n 400 @("relation Blaze -> CapitanFF01 (group 'FortFranceSquadron'): enemy -> neutral", "fire action: Blaze fires at CapitanFF01 (group 'FortFranceSquadron', neutral)",
                                                  "relation Blaze -> CapitanFF01 (group 'FortFranceSquadron'): neutral -> enemy", 'CapitanFF01 task: attack Blaze')
        # boarding: the French ship is parked 25 m abeam with our sails down; its crew is stronger, so the script boards us itself (Ship_CheckMainCharacter ->
        # Sea_AbordageLoad): upper deck, gun deck, cabin (the enemy group is killed by a hook, Space = next deck), the ransack screen, swap the ships, leave:
        # the old ship sinks (AI_MESSAGE_SWAP_SHIPS / AI_MESSAGE_CHARACTER_DEAD) and we are back at sea on the captured one
        $bd = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
                '--sail=+1@700', '--seapos=-600,-101,4.71@720', '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--mappos=-193,768,0@1200', '--act@1250',
                '--seapos=-110,100,0@2400', '--duel@2450', '--sail=-1@2460', '--sail=-1@2470', '--call=LAi_SetImmortal(#Blaze,1)@2480')
        foreach ($f in 32..60) { $bd += "--killgroup=brdenemy@$($f * 100)"; $bd += "--ctl=ChrAction@$($f * 100 + 50)" }
        $bd += @('--call=SetCrewQuantityOverMax(#Blaze,300)@6250', '--click=SWAP_BUTTON@6300', '--click=EXIT_BTN@6400', '--click=CONFIRM_YES_BUTTON@6500', '--shot-frame=7000')
        $env:SEADOGS_DUELDIST = '25'
        $fail += Invoke-Godot 'board' $bd 500 @('[location] model udeck_l', '[location] model deck1_l', '[location] model cap_l', 'INTERFACES\transfer_main.ini',
                                                '[sea] swap ships: Blaze <-> CapitanFF01', '[sea] CapitanFF01 is dead: the ship sinks', 'battle_interfacex1')
        Remove-Item Env:SEADOGS_DUELDIST
        Remove-Item Env:SEADOGS_PLAYERHP
        # ship rename on the ship screen (addon text input, INTERFACE/key_hook.c): click the name, erase it with Backspace (script events through
        # KEYCHOOSER/evntKeyChoose), type "Hawk" + 4 Ukrainian letters with real key presses (Ukrainian letters from the system layout), Enter -> SetShipName
        $uk = "Hawk $([char]0x0407)$([char]0x0436)$([char]0x0430)$([char]0x043A)"
        $rn = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=LaunchShipState()@400', '--mouse=B_SHIPNAME@500')
        foreach ($i in 0..19) { $rn += "--key=Backspace@$(510 + $i)" }
        $rn += @('--type=Hawk+%0407%0436%0430%043A@540', '--key=Enter@560', '--dump=Characters[0].Ship.Name@600', '--shot-frame=620')
        $fail += Invoke-Godot 'rename' $rn 300 @('[xinterface] lock: KEY_CHOOSER', "[xinterface] text input: '$uk'", '[xinterface] unlock', "Name = `"$uk`"")
        # screens: interface screens that no other scenario opens, each launched by its script function from the quicksave; must open without [errors]/[unresolved]
        $en = '#Cayman+Commander'
        $screens = @(@('fortcapture', "LaunchFortCapture(#Blaze,$en)", 'fortcapture.ini'), @('citycapture', "LaunchCityCapture($en)", 'citycapture.ini'),
                     @('colcapture', "LaunchColonyCapture($en)", 'colony_Capture.ini'), @('materials', "LaunchMaterialsBuild($en)", 'materials.ini'),
                     @('transferchars', "LaunchTransferCharacter(#Blaze,$en)", 'transfer_main.ini'), @('ransackcrew', "LaunchRansackCrew(#Blaze,$en)", 'ransack_crew.ini'),
                     @('passengers', 'LaunchPassengers(#Blaze)', 'passengers.ini'), @('cards', 'LaunchCardsGame()', 'boal_cards.ini'), @('dice', 'LaunchDiceGame()', 'boal_dice.ini'),
                     @('boalmap', 'LaunchBoalMapScreen()', 'boal_map.ini'), @('price', 'LaunchPriceList()', 'boal_price.ini'), @('statistic', 'LaunchBoalStatisticScreen(#Blaze)', 'boal_statistic.ini'))
        foreach ($sc in $screens) {
            $fail += Invoke-Godot "screen_$($sc[0])" @('--ctl=Quick_Load@200', "--call=$($sc[1])@400", '--shot-frame=520') 150 @($sc[2])
        }
    }
}

# engine contract audit (scripts vs port vs reference engine + the smoke logs above): a report, never fails the run
if ($Contracts) {
    Write-Host '== engine contract audit -> docs\CONTRACTS.md, docs\contracts.json' -ForegroundColor Cyan
    python "$root\tools\contract_audit.py"
}

if ($fail -gt 0) { Write-Host "FAILED ($fail)" -ForegroundColor Red; exit 1 }
Write-Host 'ALL OK' -ForegroundColor Green
