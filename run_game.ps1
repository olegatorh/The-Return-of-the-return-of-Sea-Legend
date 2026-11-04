# Builds and starts the Godot port. Reads the original game files from the folder above this one (or $env:SEADOGS_GAME).
$g = "$PSScriptRoot\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
dotnet build "$PSScriptRoot\game\SeaDogs.csproj" -nologo -v q
& $g --path "$PSScriptRoot\game"
