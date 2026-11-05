# Regression checks without playing by hand.
#   ./run_tests.ps1            unit tests (interpreter semantics, parser corpus, headless boot, format readers, language layer) + game build
#   ./run_tests.ps1 -Smoke     + Godot main-menu smoke run (needs a window; ~10 s), fails on [errors]/[unresolved]/script exceptions
#   ./run_tests.ps1 -Intro     + the reference intro run from the main menu into the first location (~3 min), same checks
#   ./run_tests.ps1 -Sea       + sea scenarios from saves/quicksave (town port): leave the port, sail, moor again; sea -> world map -> sea (~2 min)
param([switch]$Smoke, [switch]$Intro, [switch]$Sea, [switch]$Hidden)
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
    $p = Start-Process $godot -ArgumentList (@('--path', "`"$root\game`"") + $(if ($Hidden) { @('--position', '9000,9000', '--resolution', '1280x720') } else { @('--position', '0,0', '--resolution', '1280x720') }) + @('--') + $userArgs + @("--shot=smoke_$name.png")) `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow
    if (-not $p.WaitForExit($timeoutSec * 1000)) { $p.Kill(); Write-Host "TIMEOUT after $timeoutSec s" -ForegroundColor Red; return 1 }
    $text = (Get-Content $log, "$log.err" -ErrorAction SilentlyContinue) -join "`n"
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
        # Martinique: the French squadron is placed abeam and shot at; the script must apply the damage
        $c = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
               '--sail=+1@700', '--seapos=-600,-101,4.71@720', '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--mappos=-193,768,0@1200', '--act@1250',
               '--seapos=-110,100,0@2400', '--duel@2450', '--fire@2500', '--shot-frame=3000')
        # trade: open the store, choose the ship, buy 3 portions of cannon balls
        $t = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@400', '--set=Characters[0].Money=5000@410', '--call=LaunchStore(0)@500', '--key=Enter@600', '--click=BUY_BUTTON@650',
               '--call=ProcessTradeIncrease()@700', '--call=ProcessTradeIncrease()@705', '--call=ProcessTradeIncrease()@710', '--click=OK_BUTTON@760', '--dump=Characters[0].Ship.Cargo.Goods.Balls@900', '--shot-frame=905')
        $fail += Invoke-Godot 'trade' $t 200 @('Balls = "160"')
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
        # 'sail to' the port from the command menu: the squadron is moved next to the port's reload locator behind the fader, where mooring is offered
        $st = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
                '--seapos=-500,-400,3.0@700', '--call=SeaAI_SailToLocator(reload_1)@800', '--act@1300', '--shot-frame=1500')
        $fail += Invoke-Godot 'sailto' $st 300 @('[sea] sail to', '[sea] action: Moor')
        $fail += Invoke-Godot 'fight' $c 400 @('fires 4 cannons', 'hit on CapitanFF01', 'fort FalaiseDeFleur Commander: 38 cannons')
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
    }
}

if ($fail -gt 0) { Write-Host "FAILED ($fail)" -ForegroundColor Red; exit 1 }
Write-Host 'ALL OK' -ForegroundColor Green
