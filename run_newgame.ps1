# Boots the original scripts headlessly, picks "New game" in the menu, runs 30 s of game time and prints a state report.
$root = Split-Path -Parent $PSScriptRoot   # the game folder (contains PROGRAM)
Push-Location "$PSScriptRoot\StormScript.Cli"
dotnet run -c Release -- run "$root\PROGRAM" `
  --step call:Main --step sim:20 `
  --step set:interfaceResultCommand=45038 --step call:InterfaceDoExit --step sim:30 `
  --step "dump:Characters[1]" --report "$PSScriptRoot\docs\run_newgame.txt"
Pop-Location
