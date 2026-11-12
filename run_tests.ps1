# Regression checks without playing by hand.
#   ./run_tests.ps1            unit tests (interpreter semantics, parser corpus, headless boot, format readers, language layer) + game build
#   ./run_tests.ps1 -Smoke     + Godot main-menu smoke run (needs a window; ~10 s), fails on [errors]/[unresolved]/script exceptions
#   ./run_tests.ps1 -Intro     + the reference intro run from the main menu into the first location (~3 min), same checks
#   ./run_tests.ps1 -Sea       + sea scenarios from saves/quicksave (town port): leave the port, sail, moor again; sea -> world map -> sea (~2 min)
#   ./run_tests.ps1 -Contracts + engine contract audit (tools/contract_audit.py -> docs/CONTRACTS.md), run after the other runs so it sees fresh logs
param([switch]$Smoke, [switch]$Intro, [switch]$Sea, [switch]$Hidden, [switch]$Contracts, [switch]$Quest, [switch]$Stages3, [switch]$Quests, [switch]$Rules, [switch]$Cameras, [switch]$Quiet)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fail = 0

# -Quiet: print only failures and the final verdict (saves tokens when an AI agent reads the output)
function Say([string]$text, [string]$color = 'Cyan') { if (-not $Quiet) { Write-Host $text -ForegroundColor $color } }

Say '== unit tests'
if ($Quiet) { $o = dotnet test "$root\StormScript.Tests\StormScript.Tests.csproj" -nologo -v q 2>&1; if ($LASTEXITCODE -ne 0) { $o | Select-Object -Last 25 | ForEach-Object { Write-Host $_ } } }
else { dotnet test "$root\StormScript.Tests\StormScript.Tests.csproj" -nologo -v q }
if ($LASTEXITCODE -ne 0) { $fail++ }

Say '== game build'
if ($Quiet) { $o = dotnet build "$root\game\SeaDogs.csproj" -nologo -v q 2>&1; if ($LASTEXITCODE -ne 0) { $o | Select-Object -Last 25 | ForEach-Object { Write-Host $_ } } }
else { dotnet build "$root\game\SeaDogs.csproj" -nologo -v q }
if ($LASTEXITCODE -ne 0) { $fail++ }

function Invoke-Godot([string]$name, [string[]]$userArgs, [int]$timeoutSec, [string[]]$mustContain = @()) {
    if ($env:SEADOGS_ONLY -and $name -notmatch $env:SEADOGS_ONLY) { return 0 }   # run a subset: $env:SEADOGS_ONLY='q_eng4|q_pearl'
    $godot = "$root\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
    $shot = "$root\game\smoke_$name.png"
    $log = "$root\game\smoke_$name.log"
    Remove-Item $shot, $log -ErrorAction SilentlyContinue
    Say "== godot run '$name'"
    $p = Start-Process $godot -ArgumentList (@('--path', "`"$root\game`"") + $(if ($Hidden) { @('--position', '9000,9000', '--resolution', '1280x720') } else { @('--position', '320,180', '--resolution', '1280x720') }) + @('--') + $userArgs + @("--shot=smoke_$name.png")) `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow
    if (-not $p.WaitForExit($timeoutSec * 1000)) { $p.Kill(); Write-Host "[$name] TIMEOUT after $timeoutSec s" -ForegroundColor Red; return 1 }
    $text = (Get-Content $log, "$log.err" -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"
    $bad = $text -split "`n" | Where-Object { $_ -match '^\[errors\]|^\[unresolved\]|UNRESOLVED function IDoExit|SCRIPT ERROR|^ERROR: System\.|Unhandled exception|^ERROR: Invalid polygon data' }
    if (-not (Test-Path $shot)) { Write-Host "[$name] no screenshot was produced (the run did not reach the end)" -ForegroundColor Red; return 1 }
    foreach ($m in $mustContain) { if ($text -notmatch [regex]::Escape($m)) { Write-Host "[$name] expected in the log: $m" -ForegroundColor Red; return 1 } }
    if ($bad) { Write-Host "[$name] problems:" -ForegroundColor Red; $bad | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }; return 1 }
    Say "ok (screenshot: game\smoke_$name.png, log: game\smoke_$name.log)" 'Green'
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
        $env:SEADOGS_MAPSAIL = '0'; $env:SEADOGS_SEED = '3'   # fixed seed: the pursuer's birth must not depend on chance
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
        # tradereal: the same purchase with REAL input only (mouse on BUY / OK, keys Right, Shift+Right): 1+1+10 units of 5 = 60 balls (160, money 4985)
        $tr = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@400', '--set=Characters[0].Money=5000@410', '--call=LaunchStore(0)@500', '--key=Enter@600', '--mouse=BUY_BUTTON@650',
                '--key=Right@700', '--key=Right@710', '--key=Shift+Right@720', '--mouse=OK_BUTTON@760', '--dump=Characters[0].Ship.Cargo.Goods.Balls@900', '--dump=Characters[0].Money@901', '--shot-frame=905')
        $fail += Invoke-Godot 'tradereal' $tr 200 @('Balls = "160"', 'money = "4985"')
        # saving: (13) menu -> options -> back -> save -> menu again -> Resume: the main menu's IDoExit must still resolve after the other screens' segments were unloaded (the log must not hold
        # 'UNRESOLVED function IDoExit'); (9) the same on the world map (saves\debug_map_lugger); (11) a dead hero (chr_ai.hp 0) cannot be saved, a living one can
        if ((Test-Path "$root\saves\q_dl.sdsave") -and (Test-Path "$root\saves\debug_map_lugger.sdsave")) {
            $ms = @('--load=q_dl@200', '--ctl=MainMenuDouble@500', '--mouse=B_OPTIONS@550', '--mouse=EXIT_BTN@600', '--mouse=B_SAVE@650', '--mouse=SAVE_BUTTON@750', '--ctl=MainMenuDouble@850', '--mouse=B_RESUMEGAME@900', '--shot-frame=960')
            $fail += Invoke-Godot 'menusave' $ms 200 @('[save] saved SAVE')
            $mm = @('--load=debug_map_lugger@200', '--ctl=MainMenuDouble@500', '--mouse=B_SAVE@550', '--mouse=SAVE_BUTTON@650', '--ctl=MainMenuDouble@750', '--mouse=B_RESUMEGAME@800', '--shot-frame=850')
            $fail += Invoke-Godot 'mapsave' $mm 200 @('[save] saved SAVE')
            $ds = @('--load=q_dl@200', '--set=Characters[0].chr_ai.hp=0@410', '--ctl=Quick_Save@420', '--set=Characters[0].chr_ai.hp=40@500', '--ctl=Quick_Save@520', '--shot-frame=600')
            # this scenario writes saves\quicksave*: keep a copy so the other scenarios still start from the owner's/baseline quicksave
            $qsBak = Get-ChildItem "$root\saves" -Filter 'quicksave.*' | ForEach-Object { $b = "$($_.FullName).bak"; Copy-Item $_.FullName $b -Force; $b }
            $fail += Invoke-Godot 'deadsave' $ds 200 @('[save] refused: the hero is dead', '[save] saved SAVE\quicksave')
            foreach ($b in $qsBak) { Move-Item $b ($b -replace '\.bak$', '') -Force }
        }
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
        $tr = @('--ctl=Quick_Load@200', '--call=DoReloadCharacterToLocation(QC_UsurerHouse,reload,reload1)@300', '--set=Characters[0].GenQuest.God_hit_us=0@650', '--set=Characters[0].chr_ai.hp=900@651', '--set=Characters[0].chr_ai.hp_max=900@651',
                '--goto=box.box1@700', '--key=Space@800', '--key=Escape@900', '--set=Characters[0].GenQuest.God_hit_us=1@950', '--call=DoQuestCheckDelay(God_hit_us,0.2)@1000', '--key=Space@1300', '--shot-frame=1600')
        $fail += Invoke-Godot 'chesttrap' $tr 300 @('[traps] QC_UsurerHouse:box1: the last attempt was a trap', 'itemsbox.ini')
        # one dialog at a time: two more dialog starts while a dialog runs are refused, one greeting only
        $od = @('--ctl=Quick_Load@200', '--call=DoReloadCharacterToLocation(QC_Tavern,reload,reload1)@300', '--dialog=Ines_Diaz@600', '--dialog=William_Blaker@640', '--dialog=QC_Diplomat@660', '--shot-frame=900')
        $fail += Invoke-Godot 'onedialog' $od 200 @('starting dialog with Ines Diaz', 'a dialog is already running, not starting one with William Blaker', 'a dialog is already running, not starting one with QC Diplomat')
        # talking with the original control: the left mouse button (ChrAction) starts the dialog with the character the hero faces (player.cpp Update)
        $tk = @('--ctl=Quick_Load@200', '--call=DoReloadCharacterToLocation(QC_Tavern,reload,reload1)@300', '--goto=goto.goto3@400', '--bring=William_Blaker:1.5@600', '--btn=L+@900', '--btn=L-@905', '--shot-frame=1100')
        $fail += Invoke-Godot 'talkclick' $tk 200 @('starting dialog with William Blaker')
        # dialog camera: it swings to the interlocutor when the dialog starts and back to the follow angles when it ends (DialogCamera.cs)
        $env:SEADOGS_DIALOGLOG = '1'
        $dc = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@250', '--set=Characters[0].location.from_sea=Antigua_port@256', '--call=DoReloadCharacterToLocation(Antigua_Tavern,reload,reload1)@300', '--call=locCameraFollow()@500',
                '--dialog=Ronald_Shorter@600', '--call=DialogExit()@900', '--shot-frame=1200')
        $fail += Invoke-Godot 'dialogcam' $dc 200 @('[dialogcam] focus on Ronald Shorter', '[dialogcam] dialog over')
        # rest until morning (tavern / cabin script TavernWaitDate): the message log says that the next day has come (TimeSkipNotice.cs)
        $tw = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@250', '--set=Characters[0].location.from_sea=Antigua_port@256', '--call=DoReloadCharacterToLocation(Antigua_Tavern,reload,reload1)@300', '--call=TavernWaitDate(wait_day)@500', '--shot-frame=900')
        $fail += Invoke-Godot 'timeskip' $tw 200 @('[time] ')
        # officer in a fight: Gilles is the hero's officer (type officer, group player, follow template), Terry a hostile warrior of the hero's enemy group; the officer must take him on
        $env:SEADOGS_FIGHTLOG = '1'
        $of = @('--ctl=Quick_Load@200', '--call=LAi_SetOfficerType(#Gilles+Roche)@400', '--call=LAi_group_MoveCharacter(#Gilles+Roche,player)@405', '--call=LAi_SetWarriorType(#Terry++Snider)@400',
                '--call=LAi_group_MoveCharacter(#Terry++Snider,TestB)@410', '--call=LAi_group_FightGroups(player,TestB,1)@420', '--call=LAi_SetHP(#Terry++Snider,400,400)@430',
                '--set=Characters[0].chr_ai.hp=900@431', '--set=Characters[0].chr_ai.hp_max=900@431', '--bring=Gilles_Roche:2@440', '--bring=Terry__Snider:7@441', '--key=E@460', '--shot-frame=1500')
        $fail += Invoke-Godot 'officerfight' $of 300 @('[fight] Gilles Roche vs Terry  Snider:')
        Remove-Item Env:SEADOGS_FIGHTLOG
        # dice stakes: the partner may raise the scripted 50 / 200 stake (GameStakes.cs); the dice screen still opens
        $ds = @('--ctl=Quick_Load@200', '--set=Characters[0].Money=60000@250', '--set=Characters[0].GenQuest.Dice.iRate=200@250', '--set=Characters[0].GenQuest.Dice.npcharIdx=1@250', '--call=LaunchDiceGame()@400', '--shot-frame=800')
        $fail += Invoke-Godot 'dicestakes' $ds 200 @('[stakes] Dice: offered 200, cap 6666', 'boal_dice.ini')
        Remove-Item Env:SEADOGS_DIALOGLOG
        # land HUD: Enter opens the command menu, fast travel to the tavern (second choice: the place), then talk to a man brought next to the hero
        $lh = @('--ctl=Quick_Load@200', '--key=Enter@500', '--key=Enter@530', '--key=Right@540', '--key=Right@550', '--key=Enter@560', '--bring=William_Blaker@800',
                '--key=Enter@1000', '--key=Right@1010', '--key=Right@1020', '--key=Enter@1030', '--shot-frame=1150')       # the third entry: [Перехід, Використати (potion, the hero is hurt), Говорити, Відчинити]
        $fail += Invoke-Godot 'landhud' $lh 300 @("[land] command BI_FastReload 'reload3'", "[land] command BI_DialogStart", '[landhud] started=True show=False')
        # land melee by the script's rules: two townsmen of the port are put into hostile groups (LAi_group_FightGroups -> warrior type -> fight template);
        # the engine asks NPC_Event_GetAttackActive/GetDefenceActive/GetAggresive/GetFireActive/EnableFire (values = 0.3 + 0.7 / 0.3 + 0.35 x fight level),
        # reports FightStay, and the blows go back through Location_CharacterAttack/Block; E draws the hero's sword after Location_CharacterIsFight
        $ml = @('--ctl=Quick_Load@200', '--goto=goto.goto2@300', '--call=LAi_SetWarriorType(#Gilles+Roche)@400', '--call=LAi_SetWarriorType(#Terry++Snider)@400',
                '--call=LAi_group_MoveCharacter(#Gilles+Roche,TestA)@410', '--call=LAi_group_MoveCharacter(#Terry++Snider,TestB)@410', '--call=LAi_group_FightGroups(TestA,TestB,1)@420',
                '--call=LAi_SetHP(#Gilles+Roche,400,400)@430', '--call=LAi_SetHP(#Terry++Snider,400,400)@430', '--bring=Gilles_Roche:3@440', '--bring=Terry__Snider:5@445', '--key=E@450', '--shot-frame=1600')
        $fail += Invoke-Godot 'melee' $ml 300 @('[fight] Gilles Roche vs Terry  Snider: attack=1/s defence=0.65/s', '[fight] Terry  Snider vs Gilles Roche:', 'script-driven=True player fightMode=True')
        # a hostile soldier against the HERO (group 'player' made enemy of TestB): he must run up, swing and wound the hero within the run (the NPC rolls its
        # wishes every 0.2 s like np_character.cpp, so the attack rate of the script means a blow as soon as the last one is over). The hero is drawn while running:
        # he stops for the draw animation (log "hero stopped for the draw") and runs on afterwards
        $mh = @('--ctl=Quick_Load@200', '--call=LAi_SetWarriorType(#Terry++Snider)@400', '--call=LAi_group_MoveCharacter(#Terry++Snider,TestB)@410', '--call=LAi_group_FightGroups(player,TestB,1)@420',
                '--call=LAi_SetHP(#Terry++Snider,400,400)@430', '--set=Characters[0].chr_ai.hp=900@431', '--set=Characters[0].chr_ai.hp_max=900@431', '--bring=Terry__Snider:7@440', '--key=E@460', '--shot-frame=1500')
        $fail += Invoke-Godot 'meleehero' $mh 300 @('[fight] Terry  Snider vs Blaze:', '[fight] the hero was hit by Terry  Snider')
        # wind-up before the damage (character.cpp: the blow lands on the animation's Attack event): the soldier's blows must land at the event frame of the real
        # playback (hit_after_ms ~ expected_ms, no animation speed hack) and after a visible wind-up (>= 250 ms)
        $env:SEADOGS_MELEELOG = '1'
        $fail += Invoke-Godot 'windup' $mh 300 @('[windup] Terry  Snider')
        Remove-Item Env:SEADOGS_MELEELOG
        if (Test-Path "$root\game\smoke_windup.log") {
            $w = Select-String -Path "$root\game\smoke_windup.log" -Pattern 'hit_after_ms=(\d+) expected_ms=(\d+)' | ForEach-Object { [pscustomobject]@{ got = [int]$_.Matches[0].Groups[1].Value; exp = [int]$_.Matches[0].Groups[2].Value } }
            $badw = $w | Where-Object { $_.got -lt 250 -or [math]::Abs($_.got - $_.exp) -gt 120 }
            if ($w -and $badw) { Write-Host "[windup] timing out of range: $(($badw | ForEach-Object { "$($_.got)/$($_.exp)" }) -join ', ')" -ForegroundColor Red; $fail += 1 }
            elseif ($w) { Say "windup ok: $(@($w).Count) blows, e.g. $($w[0].got) ms (expected $($w[0].exp))" 'Green' }
        }
        # healing: potions in the pack, health 10, the quick-heal key C (BOAL_UsePotion) drinks the first usable one, then the land command menu
        # (Enter, Right = "Use", Enter, Enter = first potion); the count drops by one each time and the health rises (log '[heal]', hp_bottle poured in by LAi_AllCharactersUpdate)
        $hl = @('--ctl=Quick_Load@200', '--call=GiveItem2Character(#Blaze,potion1)@300', '--call=GiveItem2Character(#Blaze,potion2)@300', '--set=Characters[0].chr_ai.hp=10@310',
                '--dump=Characters[0].Items.potion1@320', '--key=C@330', '--dump=Characters[0].Items.potion1@340', '--key=Enter@500', '--key=Right@510', '--key=Enter@520', '--key=Enter@540',
                '--dump=Characters[0].Items.potion1@560', '--shot-frame=900')
        $fail += Invoke-Godot 'heal' $hl 300 @('[heal] Blaze: health rose', 'potion1 = "6"', 'potion1 = "5"', "[land] command BI_ItemsUse 'potion1'", 'potion1 = "4"')
        $dr = @('--ctl=Quick_Load@200', '--run', '--move=0,1@480-1100', '--key=E@560', '--shot-frame=1200')
        $fail += Invoke-Godot 'drawrun' $dr 300 @('[move] hero stopped for the draw', '[move] hero moves again')
        # the original follow camera (camera_follow.cpp): the mouse turns the hero (ChrTurnH x 0.01 rad per frame at the action's turn rate), the camera follows his heading with
        # the inertia rotInertia = 5: after the turn the angle camera-heading decays like exp(-5 t) (log '[camfollow] ok')
        $env:SEADOGS_CAMLOG = '100'
        $cf = @('--ctl=Quick_Load@200', '--move=0,1@400-600', '--mousemove=6,0@450-520', '--shot-frame=640')
        $fail += Invoke-Godot 'landcamfollow' $cf 300 @('[camfollow] ok')
        Remove-Item Env:\SEADOGS_CAMLOG -ErrorAction SilentlyContinue
        # 'sail to' the port from the command menu: the squadron is moved next to the port's reload locator behind the fader, where mooring is offered
        $st = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
                '--seapos=-500,-400,3.0@700', '--call=SeaAI_SailToLocator(reload_1)@800', '--act@1300', '--shot-frame=1500')
        $fail += Invoke-Godot 'sailto' $st 300 @('[sea] sail to', '[sea] action: Moor')
        # sea command menu (BattleInterface.c via BI_LaunchCommand, Enter = BICommandsActivate): Enter, Charge (7), grapes (2); Enter, Speed (8), half sails (2); Enter, Sail to (2) lists the island's
        # places with the locator pictures; the squadron row, the island picture of the minimap and the fast-command icon are drawn from the script's values
        $cm = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
                '--sail=+1@700', '--key=Enter@900', '--key=Key7@920', '--key=Key2@940', '--key=Enter@1000', '--key=Key8@1020', '--key=Key2@1040', '--key=Enter@1100', '--key=Key2@1120', '--shot-frame=1180')
        $fail += Invoke-Godot 'seacmds' $cm 300 @('[sea] commands of', 'BI_CommandEndChecking(BI_Charge) = 256', '[sea] command BI_Charge by 0, target 2', 'BI_CommandEndChecking(BI_Speed) = 512', "[sea] command BI_Speed by 0, target -1, 'sail_midi'", 'BI_CommandEndChecking(BI_SailTo)', '[sea] navigator island map')
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
        # playtest part 3 (docs/playtest_runs.md): ransack captain choice (take him prisoner -> back to the ransack main screen), shipyard repair (hull / sails to 100 for 3139, 4 h pass), selling a companion's ship,
        # monthly salary screen (script: NextDay -> wdmNextDayUpdate -> Event_CheckMoneyData -> LaunchSalayScreen; the crew costs 850 of the 3850 shown)
        $fail += Invoke-Godot 'ransackcapt' @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=LaunchRansackCaptain(#Blaze,#Cayman+Commander)@400', '--mouse=CAPTURE_BUTTON@500', '--shot-frame=620') 150 @('ransack_captain.ini', 'transfer_main.ini')
        $fail += Invoke-Godot 'yardrepair' @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--set=Characters[0].Money=50000@305', '--set=Characters[0].Ship.HP=300@306', '--set=Characters[0].Ship.SP=40@306',
            '--call=LaunchShipyard(#Esteban+Castillo)@400', '--key=Enter@450', '--mouse=REPAIR_BUTTON@500', '--mouse=FIXALL_BUTTON@600', '--dump=Characters[0].Ship.HP@650', '--dump=Characters[0].Ship.SP@650', '--dump=Characters[0].Money@650', '--shot-frame=700') 200 @('HP = "550"', 'SP = "100"', 'money = "46861"')
        $fail += Invoke-Godot 'yardsell' @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '"--set=Characters[0].GenQuest.CallFunctionParam.CharacterIntoCompanion.Id=William Blaker@305"', '--set=Characters[0].GenQuest.CallFunctionParam.CharacterIntoCompanion.Remove=1@306',
            '--call=Character_into_companion()@310', '--set=Characters[0].Money=1000@311', '--call=LaunchShipyard(#Esteban+Castillo)@400', '--key=Right@450', '--key=Enter@500', '--mouse=SELLSHIP_BUTTON@560', '--mouse=OK_BUTTON@620',
            '--dump=Characters[659].Ship.Type@700', '--dump=Characters[0].Money@700', '--shot-frame=720') 200 @('Type = "1000"', 'money = "5156"')
        $fail += Invoke-Godot 'salary' @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@250', '--set=Characters[0].Money=50000@255', '--set=NullCharacter.SalayPayMonth=7@300', '--call=WaitDate(nopost,0,0,1,0,0)@400', '--mouse=B_OK@900',
            '--dump=Characters[0].Money@1100', '--shot-frame=1150') 200 @('salary.ini', 'money = "49150"')
    }
}

# playtest part 3, long stages with an English / French patent (the pirate flag makes every port shoot): boarding by the player ("Board" action) -> loot screens -> back at sea; storming a fort
# (SeaAI.Update.Forts -> LandTroops -> fortcapture -> the town); the officer from the tavern; sleeping in the tavern
if ($Stages3) {
    if (-not (Test-Path "$root\saves\quicksave.sdsave")) { Write-Host 'no saves\quicksave.sdsave' -ForegroundColor Yellow }
    else {
        $env:SEADOGS_PLAYERHP = '9000'
        $mart = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=GiveItem2Character(#Blaze,patent_eng)@305', '--call=EquipCharacterbyItem(#Blaze,patent_eng)@310', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400',
            '--ctl=ChrAction@450', '--sail=+1@700', '--seapos=-600,-101,4.71@720', '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--mappos=-193,768,0@1200', '--act@1250', '--seapos=-110,100,0@2400')
        # the French ship is parked 25 m abeam with the sails down; Enter offers "Board" (the player's own boarding), the decks are cleared by a hook, the ransack screen: "exit" + confirm sinks the prize
        $env:SEADOGS_DUELDIST = '25'
        $b = $mart + @('--duel@2450', '--sail=-1@2460', '--sail=-1@2470', '--key=Enter@2600', '--call=LAi_SetImmortal(#Blaze,1)@2650')
        foreach ($f in 32..60) { $b += "--killgroup=brdenemy@$($f * 100)"; $b += "--ctl=ChrAction@$($f * 100 + 50)" }
        $b += @('--call=SetCrewQuantityOverMax(#Blaze,300)@6250', '--click=EXIT_BTN@6400', '--click=CONFIRM_YES_BUTTON@6500', '--shot-frame=8200')
        $fail += Invoke-Godot 'boardpatent' $b 500 @('[sea] action: Board', 'transfer_main.ini', '[sea] CapitanFF01 is dead: the ship sinks')
        Remove-Item Env:SEADOGS_DUELDIST
        # Antigua with a French patent: the fort is put into FORT_ABORDAGE (its squadron is gone), the ship goes next to it; Enter = "land troops" -> fort deck -> fortcapture -> the town
        $an = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=GiveItem2Character(#Blaze,patent_fra)@305', '--call=EquipCharacterbyItem(#Blaze,patent_fra)@310', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400',
            '--ctl=ChrAction@450', '--sail=+1@700', '--seapos=-600,-101,4.71@720', '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--mappos=-696,753,0@1200', '--act@1250', '--seapos=-60,-110,0@2400',
            '--call=Fort_SetAbordageMode(#Blaze,#Antigua+Commander)@2450', '--call=Character_SetAbordageEnable(#Antigua+Commander,1)@2455', '--sail=-1@2460', '--set=bWeatherIsStorm=0@2500', '--call=LAi_SetImmortal(#Blaze,1)@2550', '--act@2700')
        foreach ($f in 36..70) { $an += "--killgroup=brdenemy@$($f * 100)"; $an += "--ctl=ChrAction@$($f * 100 + 50)" }
        $an += @('--click=GOODS@7400', '--key=Escape@7700', '--click=SLAVES@8000', '--key=Escape@8300', '--click=ENTER_BATTLE@8600', '--shot-frame=9500')
        $fail += Invoke-Godot 'fortland' $an 600 @('[sea] action: LandTroops', 'fortcapture.ini', 'transfer_goods.ini', 'ransack_crew.ini', '[location] model GrPort_l')
        Remove-Item Env:SEADOGS_PLAYERHP
        # Antigua tavern: officer hire (BetaTestMode removes the random "nobody today"), the officer joins, the passengers screen; sleeping to the night
        $tav = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@250', '--call=GiveItem2Character(#Blaze,patent_eng)@252', '--call=EquipCharacterbyItem(#Blaze,patent_eng)@254', '--set=Characters[0].Money=50000@255',
            '--set=Characters[0].location.from_sea=Antigua_port@256', '--set=bBettaTestMode=1@256', '--call=DoReloadCharacterToLocation(Antigua_Tavern,reload,reload1)@300', '--dialog=Ronald_Shorter@600', '--answer=1@700', '--answer=2@800', '--answer=3@900',
            '--answer=1@1000', '--answer=1@1100', '--dialog=Location_fantom_character_<1>@1300', '--answer=1@1400', '--answer=1@1500', '--answer=1@1600', '--answer=1@1700', '--answer=1@1800', '--call=LaunchPassengers(#Blaze)@2100', '--shot-frame=2300')
        $fail += Invoke-Godot 'officerhire' $tav 200 @("answer 1 -> node 'hire'", 'passengers.ini')
        $sl = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@250', '--call=GiveItem2Character(#Blaze,patent_eng)@252', '--call=EquipCharacterbyItem(#Blaze,patent_eng)@254', '--set=Characters[0].Money=50000@255',
            '--set=Characters[0].location.from_sea=Antigua_port@256', '--call=DoReloadCharacterToLocation(Antigua_Tavern,reload,reload1)@300', '--dialog=Ronald_Shorter@600', '--answer=1@700', '--answer=3@800', '--answer=1@900', '--answer=1@1000',
            '--dump=Characters[0].Money@1600', '--dump=Characters[0].location@1600', '--shot-frame=1700')
        $fail += Invoke-Godot 'tavernsleep' $sl 200 @("answer 1 -> node 'room_night_wait'", 'money = "49995"', 'location = "Antigua_tavern_upstairs"')
    }
}

# playtest part 4 (docs/PLAYTEST.md "Part 4"): the quest machinery driven from the quicksave (town port): win_condition kinds (location, locator, NPC_Death, Timer, item, MapEnter ...),
# LAi actor tasks with quest callbacks, the English line (governor -> Albermal -> Spanish flute at Oxbay), the Pirate line (Jurcksen -> James Ford -> fight -> captive -> shore gang),
# the necronomicon locator, quest map encounters (Black Pearl), head hunters -> prison cell, quest delays across save / load
if ($Quests) {
    if (-not (Test-Path "$root\saves\quicksave.sdsave")) { Write-Host 'no saves\quicksave.sdsave' -ForegroundColor Yellow }
    else {
        $env:SEADOGS_TRACE = 'QuestComplete,SetActorDialogAny2Pchar,Encounter_SetQuestMapEncounter,Group_SetAddress'
        $pat = @('--call=GiveItem2Character(#Blaze,patent_eng)@252', '--call=EquipCharacterbyItem(#Blaze,patent_eng)@254')
        $fail += Invoke-Godot 'q_eng1' (@('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@250') + $pat + @('--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@300', '--dialog=eng_guber@600') +
            @(700, 800, 900, 1000, 1100, 1200, 1300 | ForEach-Object { "--answer=1@$_" }) + @('--dump=Characters[0].questTemp.State@1500', '--dump=Characters[0].QuestInfo.Eng_Line_1_Albermal@1500', '--shot-frame=1600')) 200 @("answer 1 -> node 'Step_1_3'", 'State = "to_Albermal"', '@1"')
        # the Spanish flute: location condition "Oxbay" at sea -> Group_SetAddress -> ship at the island -> NPC_Death of its captain (hull destroyed) -> next condition registered
        $seaq = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300') + $pat + @('--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450', '--sail=+1@700', '--seapos=-600,-101,4.71@720',
            '--set=Characters[0].quest.fight_withSpanish_Albermal.win_condition.l1=location@900', '--set=Characters[0].quest.fight_withSpanish_Albermal.win_condition.l1.location=Oxbay@900', '--set=Characters[0].quest.fight_withSpanish_Albermal.win_condition=fight_withSpanish_Albermal@900',
            '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--mappos=341.9,739.2,0@1200', '--act@1250', '--call=Ship_ApplyHullHitpoints(#CaptainAttack_1,99999.0,1,0)@3200', '--dump=Characters[0].quest.goto_Oxbay_Albermal@3800', '--shot-frame=4000')
        $fail += Invoke-Godot 'q_engsea' $seaq 400 @('CALL Group_SetAddress(Spa_Attack, Oxbay', '[sea] CaptainAttack_1 task: attack Blaze', '[sea] CaptainAttack_1 is dead: the ship sinks', 'win_condition = "goto_Oxbay_Albermal"')
        # entering the townhall of Oxbay: Albermal walks up to the hero and talks by himself (SetActorDialogAny2Pchar + LAi_ActorFollow)
        $fail += Invoke-Godot 'q_albermal' (@('--ctl=Quick_Load@200', '--set=Characters[0].quest.goto_Oxbay_Albermal.win_condition.l1=location@250', '--set=Characters[0].quest.goto_Oxbay_Albermal.win_condition.l1.location=Oxbay_townhall@250',
            '--set=Characters[0].quest.goto_Oxbay_Albermal.win_condition=goto_Oxbay_Albermal@250', '--call=DoReloadCharacterToLocation(Oxbay_townhall,reload,reload1)@300', '--answer=1@1200', '--answer=1@1300', '--shot-frame=1700')) 200 @('CALL SetActorDialogAny2Pchar(Albermal', "node 'Albermal_go_out'")
        # English line 2: governor sends the hero to talk to four pirates (Morgan, Morris, Mansfield, Jackman: each dialog counts up questTemp.Count), then the governor pays 15000 (Step_2_4).
        # Character indexes in --set/--dump paths are written Characters[#Id] (looked up with GetCharacterIndex by the test hook, game/Main.cs ResolveCharIdx).
        $e2 = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@250', '--call=LAi_SetImmortal(#Blaze,1)@251') + $pat + @('--set=Characters[0].questTemp.State=empty@256', '--set=Characters[0].questTemp.CurQuestNumber=2@256',
            '--set=Characters[0].questTemp.Waiting_time=0@256', '--call=SaveCurrentQuestDateParam(questTemp)@258', '--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@300', '--dialog=eng_guber@600') + @(700, 800, 900, 1000, 1100, 1200 | ForEach-Object { "--answer=1@$_" })
        $f = 1300
        foreach ($d in @(@('Antigua_InsideHouseR14', 'Henry_Morgan', 6), @('Greenford_house_Figuiera', 'John_Morris', 6), @('Gaity_Pirate_town_residence', 'Edward_Mansfield', 8), @('QC_residence', 'Jackman', 7))) {
            $e2 += "--call=DoReloadCharacterToLocation($($d[0]),reload,reload1)@$f"; $f += 500; $e2 += "--dialog=$($d[1])@$f"
            1..$d[2] | ForEach-Object { $f += 100; $e2 += "--answer=1@$f" }; $f += 100
        }
        $e2 += @("--dump=Characters[0].questTemp.State@$f", "--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@$($f + 10)", "--dialog=eng_guber@$($f + 500)")
        $f += 500
        1..7 | ForEach-Object { $f += 100; $e2 += "--answer=1@$f" }
        $e2 += @("--dump=Characters[0].questTemp.State@$($f + 100)", "--dump=Characters[0].money@$($f + 100)", "--shot-frame=$($f + 200)")
        $fail += Invoke-Godot 'q_eng2' $e2 700 @("node 'Step_2_2'", 'State = "after_talks_with_pirates"', "node 'Step_2_4'", 'State = "empty"', 'money = "115000"')
        # English line 3 (Cumana raid): Morgan "hot business" -> companion, Jackman joins; the governor of Cumana (Capture_Gover_dialog, node EngLine3Quest_Venesuela_attack) names the gold -> Any_fort_3 -> Morgan splits the gold -> state empty
        $e3 = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@250', '--call=LAi_SetImmortal(#Blaze,1)@251') + $pat + @('--set=Characters[0].questTemp.State=empty@256', '--set=Characters[0].questTemp.CurQuestNumber=3@256', '--set=Characters[0].questTemp.Waiting_time=60@256',
            '--call=SaveCurrentQuestDateParam(questTemp)@258', '--call=LAi_SetHuberType(#Henry+Morgan)@259', '--call=ChangeCharacterAddressGroup(#Henry+Morgan,Redmond_HouseInsideR20,sit,sit1)@259', '--set=Characters[#Henry+Morgan].dialog.currentnode=hot_business@259',
            '--call=DoReloadCharacterToLocation(Redmond_HouseInsideR20,reload,reload1)@300', '--dialog=Henry_Morgan@800') + @(900, 1000, 1100, 1200, 1300, 1400 | ForEach-Object { "--answer=1@$_" }) +
            @('--dump=Characters[0].questTemp.State@1600', '--call=DoReloadCharacterToLocation(QC_residence,reload,Reload1)@2400', '--dialog=Jackman@2900', '--answer=1@3000', '--answer=1@3100', '--dump=Characters[0].questTemp.CompanionQuantity@3300',
            '--set=Characters[#Major_Cumana].Dialog.Filename=Capture_Gover_dialog.c@3350', '--set=Characters[#Major_Cumana].dialog.CurrentNode=EngLine3Quest_Venesuela_attack@3350', '--call=DoReloadCharacterToLocation(Conceicao_townhall,reload,reload1)@3400', '--dialog=Major_Cumana@3900') +
            @(4000, 4100, 4200, 4300, 4400 | ForEach-Object { "--answer=1@$_" }) + @('--dump=Characters[0].location@5100', '--dialog=Henry_Morgan@5200', '--answer=1@5300', '--answer=1@5400', '--answer=1@5500', '--dump=Characters[0].questTemp.State@5700', '--shot-frame=5800')
        $fail += Invoke-Godot 'q_eng3' $e3 700 @('State = "AttackCumana_GoOn"', "node 'Step_3_7'", "node 'Step_7'", "node 'Step_5'", 'location = "Any_fort_3"', "node 'out_fort'", 'State = "empty"')
        # English line 3 timeout: 31 days pass, the timer waits for the map, on MapEnter the hero lands on the Ship_deck, Morgan leaves the squadron (-50000), quest 4
        $e3t = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450', '--sail=+1@700', '--seapos=-600,-101,4.71@720', '--call=LAi_SetImmortal(#Blaze,1)@721',
            '--set=Characters[0].questTemp.State=AttackCumana_GoOn@722', '--set=Characters[0].questTemp.CurQuestNumber=3@722', '--call=SetTimerCondition(time_over_Venesuela_attack,0,0,1,0)@730', '--call=WaitDate(nopost,0,0,2,0,0)@800',
            '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--dump=Characters[0].location@1600', '--dialog=Henry_Morgan@1700', '--answer=1@1800', '--dump=Characters[0].questTemp.CurQuestNumber@2300', '--dump=Characters[0].money@2300', '--shot-frame=2500')
        $fail += Invoke-Godot 'q_eng3late' $e3t 300 @('location = "Ship_deck"', "node 'Step_3_8'", 'CurQuestNumber = "4"', 'money = "50000"')
        # English line 4 (Curacao): the money-paid condition "location Douwesen" -> Dutch brig (CaptainAttack_1, Hol_Attack) attacks at sea -> sunk -> state Intelligence_Curasao_Sink -> governor Step_4_1 -> state empty, quest 5
        # (the hero is in fight mode after the first-quest mutiny of the quicksave, so it is reset before the dialog: CanDialog refuses a hero with the sword out)
        $e4 = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@250') + $pat + @('--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450', '--sail=+1@700', '--seapos=-600,-101,4.71@720',
            '--set=Characters[0].questTemp.State=Intelligence_Curasao_GaveMoneyYozef@890', '--set=Characters[0].questTemp.CurQuestNumber=4@890', '--set=Characters[0].quest.Intelligence_Curasao_GoToShore.win_condition.l1=location@900',
            '--set=Characters[0].quest.Intelligence_Curasao_GoToShore.win_condition.l1.location=Douwesen@900', '--set=Characters[0].quest.Intelligence_Curasao_GoToShore.win_condition=Intelligence_Curasao_GoToShore@900',
            '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--mappos=804.3,153.3,0@1200', '--act@1250', '--call=Ship_ApplyHullHitpoints(#CaptainAttack_1,99999.0,1,0)@3200', '--dump=Characters[0].questTemp.State@3800',
            '--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@3900', '--call=LAi_SetFightMode(#Blaze,0)@4400', '--dialog=eng_guber@4500') + @(4600, 4700, 4800, 4900, 5000 | ForEach-Object { "--answer=1@$_" }) +
            @('--dump=Characters[0].questTemp.State@5200', '--dump=Characters[0].questTemp.CurQuestNumber@5200', '--shot-frame=5300')
        $fail += Invoke-Godot 'q_eng4' $e4 500 @('State = "Intelligence_Curasao_Sink"', "node 'Step_4_1'", 'State = "empty"', 'CurQuestNumber = "5"', '[sea] CaptainAttack_1 is dead')
        # English line 5 (Fort Orange): governor Step_5_1 -> Fort_Orange_exit (AttackFortOrange_GoToFort: GetCharIDXForTownAttack, SetLocationCapturedState, Capture_Forts) -> Orange_residence: the commandant
        # walks up (SetActorDialogAny2Pchar) and attacks (FantomDialog Step_E_1) -> group TmpEnemy killed by a hook -> AttackFortOrange_WeWin -> governor Step_5_2 / 5_3 -> state empty, quest 6
        $e5 = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@250', '--call=LAi_SetImmortal(#Blaze,1)@251') + $pat + @('--set=Characters[0].questTemp.State=empty@256', '--set=Characters[0].questTemp.CurQuestNumber=5@256',
            '--set=Characters[0].questTemp.Waiting_time=0@256', '--call=SaveCurrentQuestDateParam(questTemp)@258', '--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@300', '--dialog=eng_guber@600') + @(700, 800, 850, 900 | ForEach-Object { "--answer=1@$_" }) +
            @('--dump=Characters[0].questTemp.State@950', '--call=DoReloadCharacterToLocation(Fort_Orange_exit,reload,reload2)@1000', '--call=DoReloadCharacterToLocation(Orange_residence,reload,reload1)@1600', '--answer=1@2400', '--killgroup=TmpEnemy@3000', '--killgroup=TmpEnemy@3100',
            '--dump=Characters[0].questTemp.State@3300', '--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@3400', '--call=LAi_SetFightMode(#Blaze,0)@3850', '--dialog=eng_guber@3900') + @(4000, 4100, 4150, 4200 | ForEach-Object { "--answer=1@$_" }) +
            @('--dump=Characters[0].questTemp.State@4300', '--dump=Characters[0].questTemp.CurQuestNumber@4300', '--shot-frame=4400')
        $fail += Invoke-Godot 'q_eng5' $e5 400 @('State = "AttackFortOrange_GoToFort"', 'QuestComplete(Capture_Forts)', "node 'Step_E_1'", 'QuestComplete(AttackFortOrange_AfterFightWithCommendant)', 'State = "AttackFortOrange_WeWin"', "node 'Step_5_3'", 'CurQuestNumber = "6"')
        # English line 6 (Morris Williams, the political branch): Step_6_1 -> sea at Redmond (ShipToPort: Group_SetAddress Willams_Group) -> ship deck talk (Sea_DeckBoatLoad, Capitans_dialog Step_E_1..5, DeleteNPCfromDeck as the exit from the deck does) ->
        # governor Step_6_2 (+5000) -> the port: boatswain walks up (MeetWithManFromWillams) -> tavern with Willams (InTavernWithWillams) -> governor Step_6_3..6_5 -> state ModifordPolitic. The arrest / pinnace branches are not driven.
        $eng6 = @'
--ctl=Quick_Load@200
--call=initTutMainCharacterShip()@250
--call=LAi_SetImmortal(#Blaze,1)@251
--call=GiveItem2Character(#Blaze,patent_eng)@252
--call=EquipCharacterbyItem(#Blaze,patent_eng)@254
--set=Characters[0].questTemp.State=empty@256
--set=Characters[0].questTemp.CurQuestNumber=6@256
--set=Characters[0].questTemp.Waiting_time=0@256
--call=SaveCurrentQuestDateParam(questTemp)@258
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@300
--dialog=eng_guber@600
--answer=1@700
--answer=1@800
--answer=1@850
--answer=1@900
--answer=1@950
--dump=Characters[0].questTemp.State@1000
--call=DoReloadCharacterToLocation(Redmond_port,reload,reload1)@1100
--set=Characters[0].location.from_sea=Redmond_port@1400
--goto=reload.sea_1@1500
--ctl=ChrAction@1550
--sail=+1@1800
--call=Sea_DeckBoatLoad($Albermal)@2400
--dialog=Albermal@3000
--answer=1@3100
--answer=1@3200
--answer=1@3300
--answer=1@3400
--answer=1@3500
--answer=1@3600
--dump=Characters[0].questTemp.State@3800
--dump=Characters[0].location@3800
--call=DeleteNPCfromDeck()@3820
--call=LAi_SetFightMode(#Blaze,0)@3850
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@3900
--dialog=eng_guber@4500
--answer=1@4600
--answer=1@4700
--answer=1@4800
--answer=1@4900
--answer=1@5000
--dump=Characters[0].questTemp.State@5100
--dump=Characters[0].money@5100
--call=DoReloadCharacterToLocation(Redmond_port,reload,reload_2_city)@5200
--answer=1@6100
--answer=1@6200
--answer=1@6300
--answer=1@6400
--answer=1@6500
--answer=1@6600
--answer=1@6700
--answer=1@6800
--answer=1@6900
--answer=1@7000
--answer=1@7100
--answer=1@7200
--dump=Characters[0].questTemp.State@7400
--dump=Characters[0].location@7400
--call=LAi_SetFightMode(#Blaze,0)@7450
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@7500
--dialog=eng_guber@8100
--answer=1@8200
--answer=1@8300
--answer=1@8400
--answer=1@8500
--answer=1@8600
--answer=1@8700
--dump=Characters[0].questTemp.State@8800
--shot-frame=8900
'@ -split "`r?`n"
        $fail += Invoke-Godot 'q_eng6' $eng6 800 @('State = "MorrisWillams_GoToMorrisBegin"', 'Group_SetAddress(Willams_Group, Redmond', 'node ''Step_6_2''', 'QuestComplete(MorrisWillams_InTavernWithWillams)', 'State = "MorrisWillams_ToModifordForCoolTalking"', 'node ''Step_6_5''', 'State = "MorrisWillams_ModifordPolitic"')
        # English line 7 (seven treasures): Step_7_1 -> Step_7_3 (GenQuestBox of the caves) -> the box of Antigua_Cave opens (OpenBox, itemsbox.ini) -> the gold is handed over (Step_7_4..7_6) -> 70000, quest 8
        $eng7 = @'
--ctl=Quick_Load@200
--call=initTutMainCharacterShip()@250
--call=LAi_SetImmortal(#Blaze,1)@251
--call=GiveItem2Character(#Blaze,patent_eng)@252
--call=EquipCharacterbyItem(#Blaze,patent_eng)@254
--set=Characters[0].questTemp.State=empty@256
--set=Characters[0].questTemp.CurQuestNumber=7@256
--set=Characters[0].questTemp.Waiting_time=0@256
--call=SaveCurrentQuestDateParam(questTemp)@258
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@300
--dialog=eng_guber@600
--answer=1@700
--answer=1@800
--answer=1@850
--answer=1@900
--answer=1@950
--answer=1@1000
--dump=Characters[0].questTemp.State@1100
--call=DoReloadCharacterToLocation(Antigua_Cave,reload,reload1)@1200
--goto=box.box1@1700
--key=Space@1800
--key=Escape@1900
--dump=Characters[0].GenQuestBox.Antigua_Cave.box1.items.jewelry5@1950
--set=Characters[0].items.jewelry5=2100@2000
--call=LAi_SetFightMode(#Blaze,0)@2050
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@2100
--dialog=eng_guber@2700
--answer=1@2800
--answer=1@2900
--answer=1@3000
--answer=1@3100
--answer=1@3200
--answer=1@3300
--dump=Characters[0].questTemp.State@3400
--dump=Characters[0].questTemp.CurQuestNumber@3400
--dump=Characters[0].money@3400
--shot-frame=3500
'@ -split "`r?`n"
        $fail += Invoke-Godot 'q_eng7' $eng7 500 @('State = "SevenTreasures_GoOn"', '[land] action: OpenBox', 'node ''Step_7_6''', 'CurQuestNumber = "8"', 'money = "170000"')
        # English line 8 (Mansfield), land part: Step_8_1/8_2 -> Gaity_Pirate_town (SetLocationCapturedState, IntoTheTown: LAi_ActorRunToLocator + ActorSelfDialog) -> DialogMan Dik Manson (Step_E_11..17) -> state CatchToPardal -> leaving the town fires OutFromFort
        $eng8 = @'
--ctl=Quick_Load@200
--call=initTutMainCharacterShip()@250
--call=LAi_SetImmortal(#Blaze,1)@251
--call=GiveItem2Character(#Blaze,patent_eng)@252
--call=EquipCharacterbyItem(#Blaze,patent_eng)@254
--set=Characters[0].questTemp.State=empty@256
--set=Characters[0].questTemp.CurQuestNumber=8@256
--set=Characters[0].questTemp.Waiting_time=0@256
--call=SaveCurrentQuestDateParam(questTemp)@258
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@300
--dialog=eng_guber@600
--answer=1@700
--answer=1@800
--answer=1@850
--answer=1@900
--answer=1@950
--answer=1@1000
--dump=Characters[0].questTemp.State@1100
--call=DoReloadCharacterToLocation(Gaity_Pirate_town,reload,reload1)@1200
--answer=1@2200
--dump=Characters[0].location@2400
--call=DoReloadCharacterToLocation(Gaity_Pirate_town_house_01,reload,reload1)@2500
--dialog=DialogMan@3200
--answer=1@3300
--answer=1@3400
--answer=1@3500
--answer=1@3600
--answer=1@3700
--answer=1@3800
--answer=1@3900
--dump=Characters[0].questTemp.State@4000
--call=DoReloadCharacterToLocation(Gaity_Pirate_town_exit,reload,reload1)@4100
--dump=Characters[0].location@4800
--shot-frame=4900
'@ -split "`r?`n"
        $fail += Invoke-Godot 'q_eng8' $eng8 500 @('State = "ToMansfield_GoOn"', 'QuestComplete(ToMansfield_MyselfDialog)', 'node ''Step_E_17''', 'State = "ToMansfield_CatchToPardal"', 'QuestComplete(<attr Characters[0].quest.ToMansfield_OutFromFort.win_condition>)')
        # English line 8, sea part: the Spanish galleon appears at Cuba (location condition -> Group_SetAddress Spa_Attack), is sunk -> ToMansfield_GaleonSink -> governor Step_8_5 -> state empty, quest 9
        $eng8sea = @'
--ctl=Quick_Load@200
--call=initTutMainCharacterShip()@250
--call=GiveItem2Character(#Blaze,patent_eng)@252
--call=EquipCharacterbyItem(#Blaze,patent_eng)@254
--call=SetPlayerShipLocation(184)@320
--goto=reload.reload1@400
--ctl=ChrAction@450
--sail=+1@700
--seapos=-600,-101,4.71@720
--call=LAi_SetImmortal(#Blaze,1)@721
--set=Characters[0].questTemp.State=ToMansfield_CatchToPardal@722
--set=Characters[0].questTemp.CurQuestNumber=8@722
--call=SaveCurrentQuestDateParam(questTemp)@723
--call=QuestComplete(ToMansfield_OutFromFort)@730
--ctl=BI_MapEnter@1000
--ctl=BI_MapEnter@1100
--mappos=-53.7,-810.3,0@1200
--act@1250
--call=Ship_ApplyHullHitpoints(#CaptainAttack_1,99999.0,1,0)@3200
--dump=Characters[0].questTemp.State@3800
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@3900
--call=LAi_SetFightMode(#Blaze,0)@4400
--dialog=eng_guber@4500
--answer=1@4600
--answer=1@4700
--answer=1@4800
--answer=1@4900
--answer=1@5000
--dump=Characters[0].questTemp.State@5200
--dump=Characters[0].questTemp.CurQuestNumber@5200
--shot-frame=5300
'@ -split "`r?`n"
        $fail += Invoke-Godot 'q_eng8sea' $eng8sea 500 @('Group_SetAddress(Spa_Attack, Cuba', 'State = "ToMansfield_GaleonSink"', 'node ''Step_8_5''', 'CurQuestNumber = "9"')
        # English line 9 (Santiago), land part: Step_9_1..9_3 -> the governor of Santiago (Capture_Gover_dialog intercepts the node EngLine9Quest_Santiago_attack) -> Step_9: Albermal becomes the captive passenger, the Cuba condition is registered, back to the port
        $eng9 = @'
--ctl=Quick_Load@200
--call=initTutMainCharacterShip()@250
--call=LAi_SetImmortal(#Blaze,1)@251
--call=GiveItem2Character(#Blaze,patent_eng)@252
--call=EquipCharacterbyItem(#Blaze,patent_eng)@254
--set=Characters[0].questTemp.State=empty@256
--set=Characters[0].questTemp.CurQuestNumber=9@256
--set=Characters[0].questTemp.Waiting_time=0@256
--call=SaveCurrentQuestDateParam(questTemp)@258
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@300
--dialog=eng_guber@600
--answer=1@700
--answer=1@800
--answer=1@850
--answer=1@900
--answer=1@950
--answer=1@1000
--answer=1@1050
--dump=Characters[0].questTemp.State@1100
--set=Characters[#Major_Santiago].Dialog.Filename=Capture_Gover_dialog.c@1150
--call=LAi_SetFightMode(#Blaze,0)@1160
--call=DoReloadCharacterToLocation(Santiago_Residence,reload,reload1)@1200
--dialog=Major_Santiago@1700
--answer=1@1800
--answer=1@1900
--answer=1@2000
--answer=1@2100
--answer=1@2200
--dump=Characters[0].location@3000
--dump=Characters[0].quest.AttackSantiago_FightWithPardal.win_condition.l1.location@3000
--dump=Characters[0].questTemp.State@3000
--shot-frame=3100
'@ -split "`r?`n"
        $fail += Invoke-Godot 'q_eng9' $eng9 500 @('State = "AttackSantiago_GoOn"', 'node ''Step_9''', 'QuestComplete(Return_Gover_Dialog)', 'location = "Cuba"')
        # English line 9, sea part: Pardal's squadron (4 ships, Group_SetAddress Spa_Attack at Cuba), the 4 NPC_Death conditions -> AttackSantiago_GoodWork -> governor Step_9_4/9_5 -> quest 10
        $eng9sea = @'
--ctl=Quick_Load@200
--call=initTutMainCharacterShip()@250
--call=GiveItem2Character(#Blaze,patent_eng)@252
--call=EquipCharacterbyItem(#Blaze,patent_eng)@254
--call=SetPlayerShipLocation(184)@320
--goto=reload.reload1@400
--ctl=ChrAction@450
--sail=+1@700
--seapos=-600,-101,4.71@720
--call=LAi_SetImmortal(#Blaze,1)@721
--set=Characters[0].questTemp.State=AttackSantiago_GoOn@722
--set=Characters[0].questTemp.CurQuestNumber=9@722
--call=SaveCurrentQuestDateParam(questTemp)@723
--set=Characters[0].quest.AttackSantiago_FightWithPardal.win_condition.l1=location@724
--set=Characters[0].quest.AttackSantiago_FightWithPardal.win_condition.l1.location=Cuba@724
--set=Characters[0].quest.AttackSantiago_FightWithPardal.win_condition=AttackSantiago_FightWithPardal@724
--ctl=BI_MapEnter@1000
--ctl=BI_MapEnter@1100
--mappos=-53.7,-810.3,0@1200
--act@1250
--call=Ship_ApplyHullHitpoints(#CaptainAttack_1,99999.0,1,0)@3000
--call=Ship_ApplyHullHitpoints(#CaptainAttack_2,99999.0,1,0)@3050
--call=Ship_ApplyHullHitpoints(#CaptainAttack_3,99999.0,1,0)@3100
--call=Ship_ApplyHullHitpoints(#CaptainAttack_4,99999.0,1,0)@3150
--dump=Characters[0].questTemp.State@3800
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@3900
--call=LAi_SetFightMode(#Blaze,0)@4400
--dialog=eng_guber@4500
--answer=1@4600
--answer=1@4700
--answer=1@4800
--answer=1@4900
--answer=1@5000
--answer=1@5100
--dump=Characters[0].questTemp.State@5300
--dump=Characters[0].questTemp.CurQuestNumber@5300
--shot-frame=5400
'@ -split "`r?`n"
        $fail += Invoke-Godot 'q_eng9sea' $eng9sea 500 @('Group_SetAddress(Spa_Attack, Cuba', 'QuestComplete(<attr Characters[0].quest.AttackSantiago_AfterFight.win_condition>)', 'State = "AttackSantiago_GoodWork"', 'node ''Step_9_5''', 'CurQuestNumber = "10"')
        # English line 10 (Port Royal defence): the governor's text registers the Redmond condition -> the Spanish squadron of 7 ships (Group_SetAddress Spa_Attack, task attack, the fort squadron set on it) -> 7 NPC_Death conditions -> SpanishAttack_GoodWork -> governor Step_10_1/10_2 -> quest 11
        $eng10 = @'
--ctl=Quick_Load@200
--call=initTutMainCharacterShip()@250
--call=LAi_SetImmortal(#Blaze,1)@251
--call=GiveItem2Character(#Blaze,patent_eng)@252
--call=EquipCharacterbyItem(#Blaze,patent_eng)@254
--set=Characters[0].questTemp.State=empty@256
--set=Characters[0].questTemp.CurQuestNumber=10@256
--set=Characters[0].questTemp.Waiting_time=0@256
--call=SaveCurrentQuestDateParam(questTemp)@258
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@300
--dialog=eng_guber@600
--answer=1@700
--answer=1@800
--answer=1@900
--dump=Characters[0].questTemp.State@1000
--call=DoReloadCharacterToLocation(Redmond_port,reload,reload_2_city)@1100
--set=Characters[0].location.from_sea=Redmond_port@1400
--goto=reload.sea_1@1500
--ctl=ChrAction@1550
--sail=+1@1800
--call=Ship_ApplyHullHitpoints(#CaptainAttack_1,99999.0,1,0)@3550
--call=Ship_ApplyHullHitpoints(#CaptainAttack_2,99999.0,1,0)@3600
--call=Ship_ApplyHullHitpoints(#CaptainAttack_3,99999.0,1,0)@3650
--call=Ship_ApplyHullHitpoints(#CaptainAttack_4,99999.0,1,0)@3700
--call=Ship_ApplyHullHitpoints(#CaptainAttack_5,99999.0,1,0)@3750
--call=Ship_ApplyHullHitpoints(#CaptainAttack_6,99999.0,1,0)@3800
--call=Ship_ApplyHullHitpoints(#CaptainAttack_7,99999.0,1,0)@3850
--dump=Characters[0].questTemp.State@4200
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@4300
--call=LAi_SetFightMode(#Blaze,0)@4800
--dialog=eng_guber@4900
--answer=1@5000
--answer=1@5100
--answer=1@5200
--answer=1@5300
--answer=1@5400
--dump=Characters[0].questTemp.State@5600
--dump=Characters[0].questTemp.CurQuestNumber@5600
--shot-frame=5700
'@ -split "`r?`n"
        $fail += Invoke-Godot 'q_eng10' $eng10 600 @('State = "SpanishAttack_ShipsInShore"', 'Group_SetAddress(Spa_Attack, Redmond', 'State = "SpanishAttack_GoodWork"', 'node ''Step_10_2''', 'CurQuestNumber = "11"')
        # English line 11 (Puerto Rico): Step_11_1 registers the Muelle_Canyon condition -> PuertoRico_SanDomingoAttack (GetCharIDXForTownAttack, SetLocationCapturedState, Capture_Forts) -> the two town governors (Capture_Gover_dialog nodes EngLine11Quest_*) set Q11_SanHuan / Q11_SantoDomingo -> governor Step_11_2 -> quest 12
        $eng11 = @'
--ctl=Quick_Load@200
--call=initTutMainCharacterShip()@250
--call=LAi_SetImmortal(#Blaze,1)@251
--call=GiveItem2Character(#Blaze,patent_eng)@252
--call=EquipCharacterbyItem(#Blaze,patent_eng)@254
--set=Characters[0].questTemp.State=empty@256
--set=Characters[0].questTemp.CurQuestNumber=11@256
--set=Characters[0].questTemp.Waiting_time=0@256
--call=SaveCurrentQuestDateParam(questTemp)@258
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@300
--dialog=eng_guber@600
--answer=1@700
--answer=1@800
--answer=1@850
--answer=1@900
--answer=1@950
--dump=Characters[0].questTemp.State@1000
--call=DoReloadCharacterToLocation(Muelle_Canyon,reload,Reload1)@1100
--dump=Characters[0].location@1800
--set=Characters[#Major_Rico].Dialog.Filename=Capture_Gover_dialog.c@1850
--set=Characters[#Major_SD].Dialog.Filename=Capture_Gover_dialog.c@1850
--call=LAi_SetFightMode(#Blaze,0)@1860
--call=DoReloadCharacterToLocation(Muelle_Residence,reload,reload1)@1900
--dialog=Major_Rico@2400
--answer=1@2500
--answer=1@2600
--answer=1@2700
--dump=Characters[0].questTemp.Q11_SanHuan@2900
--call=DoReloadCharacterToLocation(SD_townhall,reload,reload1)@3000
--dialog=Major_SD@3500
--answer=1@3600
--answer=1@3700
--answer=1@3800
--dump=Characters[0].questTemp.Q11_SantoDomingo@4000
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@4100
--dialog=eng_guber@4700
--answer=1@4800
--answer=1@4900
--answer=1@5000
--answer=1@5100
--dump=Characters[0].questTemp.State@5300
--dump=Characters[0].questTemp.CurQuestNumber@5300
--shot-frame=5400
'@ -split "`r?`n"
        $fail += Invoke-Godot 'q_eng11' $eng11 600 @('State = "PuertoRico_GoOn"', 'QuestComplete(Capture_Forts)', 'Q11_SanHuan = "1"', 'Q11_SantoDomingo = "1"', 'node ''Step_11_2''', 'CurQuestNumber = "12"')
        # English line 12 (Colonel Linch) and the end of the line: Step_12_1 -> Oxbay_townhall: Albermal becomes Linch and walks up (ColonelLinch_IntoTheResidence) -> Step_E_15..22 -> governor Step_12_2..12_4: the guard fight (EnemyFight group killed by a hook) -> ColonelLinch_AfterFight, prison of Redmond, ColonelLinch_Exit -> quest 13 -> Step_12_5/12_6 (+2000000), state EndOfQuestLine
        $eng12 = @'
--ctl=Quick_Load@200
--call=initTutMainCharacterShip()@250
--call=LAi_SetImmortal(#Blaze,1)@251
--call=GiveItem2Character(#Blaze,patent_eng)@252
--call=EquipCharacterbyItem(#Blaze,patent_eng)@254
--set=Characters[0].questTemp.State=empty@256
--set=Characters[0].questTemp.CurQuestNumber=12@256
--set=Characters[0].questTemp.Waiting_time=0@256
--call=SaveCurrentQuestDateParam(questTemp)@258
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@300
--dialog=eng_guber@600
--answer=1@700
--answer=1@800
--answer=1@850
--answer=1@900
--answer=1@950
--dump=Characters[0].questTemp.State@1000
--call=DoReloadCharacterToLocation(Oxbay_townhall,reload,reload1)@1100
--answer=1@2000
--answer=1@2100
--answer=1@2200
--answer=1@2300
--answer=1@2400
--answer=1@2500
--answer=1@2600
--answer=1@2700
--answer=1@2800
--dump=Characters[0].questTemp.State@3000
--call=LAi_SetFightMode(#Blaze,0)@3050
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@3100
--dialog=eng_guber@3700
--answer=1@3800
--answer=1@3900
--answer=1@4000
--answer=1@4100
--answer=1@4200
--killgroup=EnemyFight@5000
--killgroup=EnemyFight@5100
--killgroup=EnemyFight@5200
--dump=Characters[0].questTemp.State@5500
--dump=Characters[0].location@5500
--dialog=Albermal@5700
--answer=1@5800
--call=LAi_SetFightMode(#Blaze,0)@5850
--call=DoReloadCharacterToLocation(Redmond_Residence,reload,reload1)@5900
--dialog=eng_guber@6500
--answer=1@6600
--answer=1@6700
--answer=1@6800
--dump=Characters[0].questTemp.CurQuestNumber@6900
--set=Characters[0].questTemp.Waiting_time=0@6910
--dialog=eng_guber@7000
--answer=1@7100
--answer=1@7200
--answer=1@7300
--answer=1@7400
--answer=1@7500
--dump=Characters[0].questTemp.State@7700
--dump=Characters[0].money@7700
--shot-frame=7800
'@ -split "`r?`n"
        $fail += Invoke-Godot 'q_eng12' $eng12 700 @('State = "ColonelLinch_GoToKingstown"', 'node ''Step_E_22''', 'State = "ColonelLinch_ArrestModiford"', 'QuestComplete(ColonelLinch_AfterFight)', 'State = "ColonelLinch_ModifordArrested"', 'CurQuestNumber = "13"', 'State = "EndOfQuestLine"', 'money = "2100000"')
        # Pirate line: Jurcksen -> James Ford in the tavern of Fort Orange -> escort fight (Story_Battle_Ford..5) -> Ford beaten (LAi_SetCheckMinHP) -> captured -> the passengers
        $pl = @('--ctl=Quick_Load@200', '--set=Characters[0].questTemp.Begin_Quests_Pirate=1@250', '--call=LAi_SetImmortal(#Blaze,1)@260', '--call=DoReloadCharacterToLocation(QC_residence,reload,Reload1)@300', '--dialog=Isenbrandt_Jurcksen@700') +
            @(800, 900, 1000, 1100, 1200, 1300 | ForEach-Object { "--answer=1@$_" }) + @('--call=DoReloadCharacterToLocation(Orange_Tavern,reload,reload1)@1500', '--dialog=James_Ford@1900') + @(2000, 2100, 2200, 2300, 2400 | ForEach-Object { "--answer=1@$_" }) +
            @('--call=DoReloadCharacterToLocation(Fort_Orange_exit,reload,reload2)@3100', '--answer=1@4400', '--answer=1@4800', '--call=LAi_ApplyCharacterDamage(#James+Ford,70.0)@5400', '--answer=1@6000', '--answer=1@6200', '--dump=Characters[0].QuestInfo.Pirate_line@7200', '--shot-frame=7300')
        $fail += Invoke-Godot 'q_pirate' $pl 500 @("node 'Story_find_Ford3'", "node 'battle1'", 'QuestComplete(Story_Battle_Ford2)', 'QuestComplete(Story_Battle_Ford5)', 'QuestComplete(Story_Set_Ford)', "node 'Captive'", 'QuestComplete(Ford_captive2)', 'QuestComplete(People_Ford)', '@3,')
        # the three pirates on the shore (l1..l3 NPC_Death conditions) fight and are killed by a hook -> Pirate_Ford_Deads
        $fail += Invoke-Godot 'q_fordshore' @('--ctl=Quick_Load@200', '--call=LAi_SetImmortal(#Blaze,1)@250', '--call=CompleteQuestName(People_Ford)@260', '--call=DoReloadCharacterToLocation(Redmond_shore_02,reload,reload1)@300', '--answer=1@1500', '--answer=1@1700',
            '--answer=1@1900', '--killgroup=PIRATE_FORD@2600', '--killgroup=PIRATE_FORD@2700', '--shot-frame=3200') 200 @('QuestComplete(People_Ford_Talk2)', 'QuestComplete(People_Ford_Battle)', 'Characters[0].quest.Pirate_Ford_Deads.win_condition>')
        # locator condition (necronomicon in the dungeon of Port Royal), reached by walking onto randitem1
        $fail += Invoke-Godot 'q_necro' @('--ctl=Quick_Load@200', '--set=Characters[0].quest.Story_Necronomicon.win_condition.l1=locator@250', '--set=Characters[0].quest.Story_Necronomicon.win_condition.l1.location=Redmond_Dungeon_03@250',
            '--set=Characters[0].quest.Story_Necronomicon.win_condition.l1.locator_group=randitem@250', '--set=Characters[0].quest.Story_Necronomicon.win_condition.l1.locator=randitem1@250', '--set=Characters[0].quest.Story_Necronomicon.win_condition=Story_Necronomicon@250',
            '--call=LAi_SetImmortal(#Blaze,1)@260', '--call=DoReloadCharacterToLocation(Redmond_Dungeon_03,reload,reload1)@300', '--goto=randitem.randitem1@900', '--dump=Characters[0].QuestInfo.Necronomicon@1300', '--shot-frame=1400') 150 @('@13"')
        # win_condition kinds on land: ExitFromLocation, location, location_port, Location_Coast, Timer (WaitDate), Goods
        $pr = @(); foreach ($p in @(@('pexit', 'ExitFromLocation', 'location=QC_port'), @('pport', 'location_port'), @('pcoast', 'Location_Coast'), @('ptimer', 'Timer', 'date.year=1665', 'date.month=1', 'date.day=2'),
                                    @('pgoods', 'Goods', 'goods=0', 'quantity=1', 'operation=>'), @('ploc', 'location', 'location=QC_town'))) {
            $pr += "--set=Characters[0].quest.$($p[0]).win_condition.l1=$($p[1])@300"
            foreach ($kv in $p[2..($p.Count - 1)]) { if ($p.Count -gt 2) { $pr += "--set=Characters[0].quest.$($p[0]).win_condition.l1.$kv@300" } }
            $pr += "--set=Characters[0].quest.$($p[0]).win_condition=pr_$($p[0])@300"
        }
        $fail += Invoke-Godot 'q_cond_land' (@('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@250') + $pr + @('--call=DoReloadCharacterToLocation(QC_town,reload,reload1)@500', '--call=DoReloadCharacterToLocation(QC_port,reload,reload1)@800',
            '--call=WaitDate(nopost,0,0,2,0,0)@1100', '--shot-frame=1300')) 200 @('quest.pexit.win_condition>', 'quest.ploc.win_condition>', 'quest.pport.win_condition>', 'quest.pcoast.win_condition>', 'quest.ptimer.win_condition>', 'quest.pgoods.win_condition>')
        # at sea / map: MapEnter, Ship_location, Crew_Space
        $pm = @('--set=Characters[0].quest.pmap.win_condition.l1=MapEnter@900', '--set=Characters[0].quest.pmap.win_condition=pr_pmap@900', '--set=Characters[0].quest.pship.win_condition.l1=Ship_location@900', '--set=Characters[0].quest.pship.win_condition.l1.location=QC_port@900',
                '--set=Characters[0].quest.pship.win_condition=pr_pship@900', '--set=Characters[0].quest.pcrew.win_condition.l1=Crew_Space@900', '--set=Characters[0].quest.pcrew.win_condition.l1.quantity=5@900', '--set=Characters[0].quest.pcrew.win_condition.l1.operation=>@900',
                '--set=Characters[0].quest.pcrew.win_condition=pr_pcrew@900')
        $fail += Invoke-Godot 'q_cond_sea' (@('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450', '--sail=+1@700', '--seapos=-600,-101,4.71@720') + $pm +
            @('--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--shot-frame=1500')) 300 @('quest.pmap.win_condition>', 'quest.pship.win_condition>', 'quest.pcrew.win_condition>')
        # LAi actor tasks with quest callbacks: animation, follow, run / go to a locator, go to a location (the hero stands on the reload: the template timeout teleports and still completes)
        $fail += Invoke-Godot 'q_actors' @('--ctl=Quick_Load@200', '--call=DoReloadCharacterToLocation(QC_town,reload,reload1)@300', '--call=LAi_SetActorType(#Filosof)@500', '--call=LAi_ActorGoToLocator(#Filosof,goto,goto1,probe_goto,20.0)@501',
            '--call=LAi_SetActorType(#Luisa+Drake)@500', '--call=LAi_ActorRunToLocator(#Luisa+Drake,goto,goto2,probe_run,20.0)@501', '--call=LAi_SetActorType(#Blaze)@500', '--call=LAi_ActorAnimation(#Blaze,attack_2,probe_anim,1.5)@501',
            '--call=LAi_SetActorType(#Natan+Bueno)@500', '--call=LAi_ActorFollow(#Natan+Bueno,#Blaze,probe_follow,10.0)@501', '--call=LAi_SetActorType(#Hamund+Verret)@500', '--call=LAi_ActorGoToLocation(#Hamund+Verret,reload,reload1,none,x,x,probe_gl,20.0)@501',
            '--shot-frame=3500') 200 @('QuestComplete(probe_anim)', 'QuestComplete(probe_follow)', 'QuestComplete(probe_run)', 'QuestComplete(probe_goto)', 'QuestComplete(probe_gl)')
        # delay timers survive a save / load (DoQuestCheckDelay -> PostEventQuest.questDelay -> QuestsInit)
        $fail += Invoke-Godot 'q_delay' @('--ctl=Quick_Load@200', '--call=DoQuestCheckDelay(pdl,15.0)@300', '--save=q_dl@500', '--load=q_dl@700', '--shot-frame=4500') 200 @('CALL QuestComplete(pdl)')
        # head hunters (map-less land generator): "I will follow you" -> prison of the nation -> money and weapons taken -> the cell dialog
        $fail += Invoke-Godot 'q_hunters' @('--ctl=Quick_Load@200', '--set=Characters[0].enghunter=40@250', '--call=DoReloadCharacterToLocation(QC_town,reload,reload1)@300', '--call=CompleteQuestName(Land_HuntersBegin)@600', '--answer=1@1400', '--answer=3@1600',
            '--answer=1@4300', '--answer=1@4500', '--dump=Characters[0].money@5400', '--shot-frame=5500') 300 @("node 'ToPrison'", 'QuestComplete(Hunters_GoToPrison_2)', 'QuestComplete(MoveToPrison_Cell)', 'QuestComplete(Prison_Cell_WaitTalk)', 'money = "0"')
        # quest map encounter: the Black Pearl is registered with Encounter_SetQuestMapEncounter, the pursuer of the map is the quest group; it fights at sea
        $env:SEADOGS_MAPSPEED = '1'; $env:SEADOGS_MAPSAIL = '0'; $env:SEADOGS_SEED = $(if ($env:PEARL_SEED) { $env:PEARL_SEED } else { '2' }); $env:SEADOGS_MEETFOLLOW = '1'; $env:SEADOGS_PLAYERHP = '90000'
        $fail += Invoke-Godot 'q_pearl' @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450', '--sail=+1@700', '--seapos=-600,-101,4.71@720',
            '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--call=BlackPearlOnMap()@1150', '--set=worldMap.date.hourPerSec=0.05@1160', '--set=Characters[0].worldmapencountersoff=0@1160', '--mapmeet@1700', '--key=Enter@1710', '--key=Enter@1900', '--shot-frame=3000') 300 @('Encounter_SetQuestMapEncounter(Black_Pearl', '[sea] Barbossa task: attack Blaze')
        foreach ($v in 'SEADOGS_MAPSPEED', 'SEADOGS_MAPSAIL', 'SEADOGS_SEED', 'SEADOGS_MEETFOLLOW', 'SEADOGS_PLAYERHP', 'SEADOGS_TRACE') { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
    }
}

# engine contract audit (scripts vs port vs reference engine + the smoke logs above): a report, never fails the run
if ($Contracts) {
    Write-Host '== engine contract audit -> docs\CONTRACTS.md, docs\contracts.json' -ForegroundColor Cyan
    python "$root\tools\contract_audit.py"
}

# docs/RULES_CHECK.md: the addon FAQ rules whose engine side is checked with targeted runs (script results are read with SEADOGS_CALLLOG=1 / --call)
if ($Rules) {
    if (-not (Test-Path "$root\saves\quicksave.sdsave")) { Write-Host 'no saves\quicksave.sdsave' -ForegroundColor Yellow }
    else {
        $env:SEADOGS_CALLLOG = '1'
        # rule 1: CheckForSurrender: reputation 10 surrenders, 50 does not; a class-1 enemy never; Sailing penalty to the skills of a companion
        $r1 = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--set=defeat=0@305', '--set=SurrenderModifier=7@305', '--set=boarding_location_type=0@305',
                '--set=Characters[757].Ship.Type=94@306', '--set=Characters[757].Ship.Crew.Quantity=20@306', '--set=Characters[757].Ship.Crew.Morale=0@306', '--set=Characters[0].Ship.Crew.Quantity=40@306')
        foreach ($s in 'Leadership', 'Grappling', 'Fencing', 'Sneak') { $r1 += "--set=Characters[0].Skill.$s=80@400"; $r1 += "--set=Characters[757].Skill.$($s -replace 'Grappling','Defence')=20@400" }
        $r1 += @('--set=Characters[0].reputation=10@400', '--call=CheckForSurrender(#Blaze,#Cayman+Commander)@410', '--set=Characters[0].reputation=50@420', '--call=CheckForSurrender(#Blaze,#Cayman+Commander)@430',
                 '--set=Characters[0].reputation=10@440', '--set=Characters[757].Ship.Type=140@440', '--call=CheckForSurrender(#Blaze,#Cayman+Commander)@450',
                 '--set=Characters[757].Ship.Crew.Quantity=20@460', '--set=Characters[0].Ship.Type=94@460', '--call=DelBakSkillAttr(#Blaze)@461', '--call=GetCharacterSkill(#Blaze,Leadership)@470', '--shot-frame=520')
        $fail += Invoke-Godot 'rules_surrender' $r1 150 @('CheckForSurrender(<attr Characters[0]>,<attr Characters[757]>) -> 1', 'CheckForSurrender(<attr Characters[0]>,<attr Characters[757]>) -> 0', 'GetCharacterSkill(<attr Characters[0]>,Leadership) -> 20')
        # rules 3 + 4: the crew floor of grapeshot (25% of MinCrew) and the weapons in the hold (GetWeaponCrew)
        $r3 = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--set=Characters[757].Ship.Type=94@305', '--set=Characters[757].Ship.Crew.Quantity=100@305', '--set=Characters[757].TmpSkill.Defence=0.5@305',
                '--call=Ship_ApplyCrewHitpoints(#Cayman+Commander,99999)@320', '--dump=Characters[757].Ship.Crew.Quantity@321', '--call=SetCharacterGoods(#Blaze,6,0)@330', '--call=GetWeaponCrew(#Blaze,40)@331',
                '--call=SetCharacterGoods(#Blaze,6,50)@350', '--call=GetWeaponCrew(#Blaze,40)@351', '--shot-frame=400')
        $fail += Invoke-Godot 'rules_crew' $r3 150 @('Quantity = "8"', 'GetWeaponCrew(<attr Characters[0]>,40) -> 20', 'GetWeaponCrew(<attr Characters[0]>,40) -> 40')
        # rule 2: every spyglass shows what its scope.show flags say (the panel data is logged); the duel ship is 80 m abeam, the deck camera (Tab, the telescope works only there) from 2550, the telescope is held up from frame 2600
        $env:SEADOGS_PLAYERHP = '9000'; $env:SEADOGS_DUELDIST = '80'; $env:SEADOGS_SCOPE = '2600'; $env:SEADOGS_SEACAM = '40,0.3,1.5708'; $env:SEADOGS_DECKCAM = '1.5708,0.0'
        $r2 = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=GiveItem2Character(#Blaze,patent_eng)@305', '--call=EquipCharacterbyItem(#Blaze,patent_eng)@310', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400',
                '--ctl=ChrAction@450', '--sail=+1@700', '--seapos=-600,-101,4.71@720', '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--mappos=-193,768,0@1200', '--act@1250', '--seapos=-110,100,0@2400', '--duel@2450', '--sail=-1@2460', '--sail=-1@2470', '--ctl=Sea_CameraSwitch@2550')
        $f = 2700
        foreach ($n in 1..5) { $r2 += "--call=GiveItem2Character(#Blaze,spyglass$n)@$f"; $r2 += "--call=EquipCharacterbyItem(#Blaze,spyglass$n)@$f"; $f += 250 }
        $r2 += "--shot-frame=$f"
        $fail += Invoke-Godot 'rules_spyglass' $r2 300 @('spyglass panel: type ''1'' hull -1 sails -1 crew -1', 'hull 100 sails 100 crew -1 speed', 'crew 490 speed')
        Remove-Item Env:SEADOGS_DUELDIST, Env:SEADOGS_SCOPE, Env:SEADOGS_SEACAM, Env:SEADOGS_DECKCAM
        # rule 7: Antigua (French patent): the player's balls destroy the fort cannons through the script (--fortkill) -> Fort.Mode 1 (FORT_ABORDAGE); the garrison squadron is gone (Character_SetAbordageEnable) -> LandTroops -> fort deck
        $r7 = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=GiveItem2Character(#Blaze,patent_fra)@305', '--call=EquipCharacterbyItem(#Blaze,patent_fra)@310', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400',
                '--ctl=ChrAction@450', '--sail=+1@700', '--seapos=-600,-101,4.71@720', '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--mappos=-696,753,0@1200', '--act@1250', '--seapos=-60,-110,0@2400',
                '--fortkill@2450', '--sail=-1@2460', '--set=bWeatherIsStorm=0@2500', '--call=LAi_SetImmortal(#Blaze,1)@2550', '--call=Character_SetAbordageEnable(#Antigua+Commander,1)@2570', '--act@2700', '--shot-frame=3600')
        $fail += Invoke-Godot 'rules_fortkill' $r7 300 @('Fort.Mode = 1', '[sea] action: LandTroops', '[location] model fort2')
        # rule 9: bWorldAlivePause survives a save / load
        $fail += Invoke-Godot 'rules_pause' @('--ctl=Quick_Load@200', '--set=bWorldAlivePause=0@310', '--save=rulespause@320', '--set=bWorldAlivePause=1@400', '--load=rulespause@500', '--dump=bWorldAlivePause@900', '--shot-frame=950') 200 @('bWorldAlivePause = 0')
        Remove-Item "$root\saves\rulespause*" -ErrorAction SilentlyContinue
        Remove-Item Env:SEADOGS_CALLLOG, Env:SEADOGS_PLAYERHP
    }
}

# docs/VERIFIED.md "Telescope, cameras, sailors": deck camera + telescope (the script gates it), the free camera (debug flag), sailors climbing the vants and jumping when a mast falls
if ($Cameras) {
    if (-not (Test-Path "$root\saves\quicksave.sdsave")) { Write-Host 'no saves\quicksave.sdsave: skip -Cameras' -ForegroundColor Yellow }
    else {
        $env:SEADOGS_PLAYERHP = '9000'; $env:SEADOGS_DECKCAM = '1.5708,0.0'; $env:SEADOGS_SCOPE = '1100'
        $fail += Invoke-Godot 'cam_scope' @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450', '--ctl=Sea_CameraSwitch@800', '--shot-frame=1400') 300 @('[sea] camera: DECK_CAMERA', '[sea] telescope raised')
        Remove-Item Env:SEADOGS_SCOPE
        $fail += Invoke-Godot 'cam_free' @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450', '--set=locCameraEnableFree=1@790', '--ctl=Sea_CameraSwitch@800', '--ctl=Sea_CameraSwitch@850', '--shot-frame=1000') 300 @('[sea] camera: FREE_CAMERA')
        Remove-Item Env:SEADOGS_DECKCAM
        $env:SEADOGS_MASTCHANCE = '1'; $env:SEADOGS_SEACAM = '28,0.25,1.2'
        $fail += Invoke-Godot 'cam_sailors' @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--set=Characters[0].Ship.Type=94@305', '--set=Characters[0].Ship.Crew.Quantity=100@305', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450', '--mastfall=me@1250', '--shot-frame=1500') 300 @('climbs up from point', 'men jumped overboard')
        Remove-Item Env:SEADOGS_PLAYERHP, Env:SEADOGS_MASTCHANCE, Env:SEADOGS_SEACAM
    }
}

if ($fail -gt 0) { Write-Host "FAILED ($fail)" -ForegroundColor Red; exit 1 }
Write-Host 'ALL OK' -ForegroundColor Green
