# Boarding probe: the 'board' scenario of run_tests.ps1 with chosen crews, dumping the script's boarding counters on every deck.
# usage: powershell -ExecutionPolicy Bypass -File tools\boarding_probe.ps1 -Mine 60 -Enemy 120 [-Out game\probe.log]
param([int]$Mine = 60, [int]$Enemy = 120, [string]$Out = 'game\probe_run.log')
$root = Split-Path $PSScriptRoot -Parent
$a = @('--ctl=Quick_Load@200', '--call=initTutMainCharacterShip()@300', '--call=SetPlayerShipLocation(184)@320', '--goto=reload.reload1@400', '--ctl=ChrAction@450',
       '--sail=+1@700', '--seapos=-600,-101,4.71@720', '--ctl=BI_MapEnter@1000', '--ctl=BI_MapEnter@1100', '--mappos=-193,768,0@1200', '--act@1250',
       '--seapos=-110,100,0@2400', "--set=Characters[0].Ship.Crew.Quantity=$Mine@2440", "--set=Characters[#CapitanFF01].Ship.Crew.Quantity=$Enemy@2440",
       '--duel@2450', '--sail=-1@2460', '--sail=-1@2470', '--call=LAi_SetImmortal(#Blaze,1)@2480')
foreach ($f in 26..56) {
  foreach ($g in 'boarding_player_crew','boarding_player_crew_start','boarding_enemy_crew','boarding_enemy_crew_start','boarding_player_base_crew','boarding_enemy_base_crew','LAi_numloginedcharacters','boarding_location_type') { $a += "--dump=$g@$($f * 100 + 20)" }
}
foreach ($f in 32..60) { $a += "--killgroup=brdenemy@$($f * 100)"; $a += "--ctl=ChrAction@$($f * 100 + 50)" }
$a += '--shot-frame=6000'
$env:SEADOGS_DUELDIST = '25'; $env:SEADOGS_PLAYERHP = '9000'
& "$PSScriptRoot\godot_args.ps1" -ArgText ($a -join ';') -Grep '\[boarding|dump:|^[a-zA-Z_]+ = |\[location\] model|\[sea\] swap|Start boarding|\[errors\]' -Timeout 500 -Max 5000 | Set-Content (Join-Path $root $Out) -Encoding UTF8
