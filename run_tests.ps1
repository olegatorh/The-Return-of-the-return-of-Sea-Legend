# Regression checks without playing by hand.
#   ./run_tests.ps1            unit tests (interpreter semantics, parser corpus, headless boot, format readers, language layer) + game build
#   ./run_tests.ps1 -Smoke     + Godot main-menu smoke run (needs a window; ~10 s), fails on [errors]/[unresolved]/script exceptions
#   ./run_tests.ps1 -Intro     + the reference intro run from the main menu into the first location (~3 min), same checks
#   ./run_tests.ps1 -Sea       + sea scenarios from saves/quicksave (town port): leave the port, sail, moor again; sea -> world map -> sea (~2 min)
#   ./run_tests.ps1 -Contracts + engine contract audit (tools/contract_audit.py -> docs/CONTRACTS.md), run after the other runs so it sees fresh logs
param([switch]$Smoke, [switch]$Intro, [switch]$Sea, [switch]$Hidden, [switch]$Contracts)
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
    $bad = $text -split "`n" | Where-Object { $_ -match '^\[errors\]|^\[unresolved\]|SCRIPT ERROR|^ERROR: System\.|Unhandled exception' }
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
        # world-map storm: the script creates it (wdmCreateStorm -> MSG_WORLDMAP_CREATESTORM), a hook moves it onto the ship, WorldMap_PlayerInStorm sends us
        # to the sea with Login.Storm = 1 (storm weather; AIShip.c applies the wind damage)
        $sm = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
                '--sail=+1@700', '--seapos=-600,-101,4.71@800', '--ctl=BI_MapEnter@1000', '--mapstorm=0,0@1200', '--dump=WeatherParams.Storm@1800', '--shot-frame=2000')
        $fail += Invoke-Godot 'storm' $sm 300 @('[map] storm created', '[map] the ship sailed into storm 0', 'Storm = "1"')
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
