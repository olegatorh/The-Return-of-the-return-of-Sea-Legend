# Runs the game with the given user args (after --) off-screen and waits; prints the log lines that match $Grep.
# usage: powershell -ExecutionPolicy Bypass -File tools\godot_args.ps1 -Grep 'dump|save' -ArgText "--ctl=Quick_Load@200;--dump=SHIP_BLACK_PEARL@400" ... (semicolon separated)
param([string]$ArgText, [string]$Grep = '.', [int]$Timeout = 300, [int]$Max = 60, [switch]$Visible, [string]$Resolution = '1280x720', [string]$ShotName = 'tool_run.png')
$ArgList = $ArgText -split "s*;s*"
$root = Split-Path $PSScriptRoot -Parent
$godot = "$root\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
$log = "$root\game\tool_run.log"
Remove-Item $log, "$log.err" -ErrorAction SilentlyContinue
$pos = if ($Visible) { @('--position', '320,180') } else { @('--position', '9000,9000') }
$p = Start-Process $godot -ArgumentList (@('--path', "`"$root\game`"") + $pos + @('--resolution', $Resolution, '--') + $ArgList + @("--shot=$ShotName")) -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow
if (-not $p.WaitForExit($Timeout * 1000)) { $p.Kill(); Write-Host 'TIMEOUT' }
Get-Content $log, "$log.err" -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -match $Grep } | Select-Object -First $Max
