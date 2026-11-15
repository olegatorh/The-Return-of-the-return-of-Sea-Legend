# Builds saves\debug_pearl_jack: from saves\quicksave (the hero in the port of Quebradas Costillas) -> Jack Sparrow (dep0) with the Black Pearl, a crew of 300 skeletons.
# usage: powershell -ExecutionPolicy Bypass -File tools\make_debug_save_pearl.ps1
$a = @('--ctl=Quick_Load@200', '--set=Characters[0].model=dep0@500', '--call=SetNewModelToChar(#Blaze)@510', '--set=Characters[0].FaceId=57@520',
       '--set=Characters[0].Ship.Type=160@600', '--call=SetBaseShipData(#Blaze)@610', '--set=Characters[0].Ship.Name=Чорна_Перлина@620',
       '--set=Characters[0].Ship.Cannons.Type=8@625', '--call=SetCrewQuantity(#Blaze,300)@630', '--set=Characters[0].Ship.Crew.Morale=100@640',
       '--set=Characters[0].SkeletonCrew=1@650', '--set=Characters[0].money=5000000@655',
       '--dump=Characters[0].Ship.HP@1000', '--dump=Characters[0].Ship.Crew@1000', '--save=debug_pearl_jack@1100', '--shot-frame=1250')
& "$PSScriptRoot\godot_args.ps1" -ArgText ($a -join ';') -Grep 'saved SAVE|dump|\[errors\]'
