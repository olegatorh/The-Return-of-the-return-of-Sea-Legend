# Starts the game straight at sea (needs saves\quicksave.sdsave made in the town port, F4 in game) and leaves the controls to you.
# W/S sails, A/D turn, arrows/PgUp/PgDn camera, LMB broadside, Enter port, M world map (leave the port zone first).
$root = $PSScriptRoot
$godot = "$root\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
dotnet build "$root\game\SeaDogs.csproj" -nologo -v q
& $godot --path "$root\game" -- --ctl=Quick_Load@200 "--call=initTutMainCharacterShip()@300" "--call=SetPlayerShipLocation(184)@320" "--goto=reload.reload1@400" --ctl=ChrAction@450 --sail=+1@700
