# Controls of the original (CONTROLS/init_pc.c) and what the port does with them

Generated from the script once; "keys" are Windows virtual key names of `ExternInitKeyCodes`. A control with an empty group is global (never locked by the key group switch of `controls.c`). `INVERSE` = `INVERSE_CONTROL` (active while the key is released; for axes the value is negated), `R` = remappable on the options pages.

| Group (+ also in) | Control | Default | Flags | R |---|---|---|---|---|---|
| - | ICancel | VK_ESCAPE |  |  |  |
| - | IAction | VK_SPACE |  |  |  |
| - | TimeScalePause | KEY_R |  |  | script (R) |
| - | TimeScalePause2 | KEY_R | INVERSE |  | script |
| - | VK_PAUSETimePause | VK_PAUSE |  |  | script |
| - | TimeScale | KEY_T |  |  | script (hold T: x5 / x3 on the map) |
| - | TimeScale2 | KEY_T | INVERSE |  | script (T released) |
| - | TimeScaleFaster | VK_ADD |  |  | script (+) |
| - | TimeScaleSlower | VK_SUBTRACT |  |  | script (-) |
| - | BOAL_Control | VK_F11 |  |  | F11 debug screen: not raised (the port: full screen) |
| - | BOAL_Control2 | VK_F12 |  |  | script |
| - | BOAL_SetCamera | VK_F10 |  |  | F10: not raised (the port: land style switch) |
| - | BOAL_Control3 | VK_F9 |  |  | F9 debug screen: not raised (the port: free camera) |
| - | BOAL_ControF7 | VK_F7 |  |  | script |
| - | BOAL_ControF5 | VK_F5 |  |  | script |
| - | BOAL_ControlDebug | VK_INSERT |  |  | script |
| - | Quick_Save | VK_F4 |  |  | QuickSave |
| - | Quick_Load | VK_F6 |  |  | QuickLoad |
| - | ChrCamTurnV | mouse Y axis |  |  | classic scheme: mouse tilts the camera |
| - | ChrCamTurnH | mouse X axis |  |  |  |
| - | Turn V | mouse Y axis | INVERSE |  |  |
| - | Turn H | mouse X axis |  |  |  |
| PrimaryLand | ChrCamCameraSwitch | VK_TAB |  | R | LandLook (first person) |
| - | ShipCamera_Turn_V | mouse Y axis | INVERSE |  | mouse turns the ship camera |
| - | ShipCamera_Turn_H | mouse X axis |  |  | mouse turns the ship camera |
| Sailing3Pers | ShipCamera_Forward | VK_MBUTTON |  | R | zoom in (SeaView.ShipCameraControls) |
| Sailing3Pers | ShipCamera_Backward | VK_RBUTTON |  | R | zoom out |
| - | DeckCamera_Turn_V | mouse Y axis |  |  |  |
| - | DeckCamera_Turn_H | mouse X axis |  |  |  |
| Sailing1Pers | DeckCamera_Forward | VK_RBUTTON |  | R | RMB walks on deck |
| Sailing1Pers | DeckCamera_Backward | VK_MBUTTON |  | R | MMB walks back on deck |
| - | FreeCamera_Turn_V | mouse Y axis | INVERSE |  |  |
| - | FreeCamera_Turn_H | mouse X axis |  |  |  |
| - | FreeCamera_Forward | VK_LBUTTON |  |  | open (debug) |
| - | FreeCamera_Backward | VK_RBUTTON |  |  | open (debug) |
| - | ChrTurnH | mouse X axis |  |  | classic scheme: mouse turns the hero |
| PrimaryLand | ChrAction | VK_LBUTTON |  | R | LMB: reload / talk / pick (LandAction); the port also raises the event itself |
| PrimaryLand + FightModeControls, BattleInterfaceControls | ChrForward | VK_RBUTTON |  | R | classic scheme: hold = walk (also W) |
| PrimaryLand + FightModeControls, BattleInterfaceControls | ChrBackward | VK_MBUTTON |  | R | classic scheme: hold = walk back (also S) |
| PrimaryLand + FightModeControls, BattleInterfaceControls | ChrRun | VK_SHIFT |  | R | LandRun (classic: Shift = walk) |
| PrimaryLand + BattleInterfaceControls, PrimaryLand | ChrFightMode | KEY_E |  | R | LandSword |
| PrimaryLand + FightModeControls | BOAL_UsePotion | KEY_C |  | R | LandPotion |
| PrimaryLand + FightModeControls | BOAL_DeadSearch | KEY_1 |  | R | script (raised by the control service) |
| PrimaryLand + FightModeControls | ChrTurnHR | KEY_D |  | R | LandRight (classic: turn, modern: strafe) |
| PrimaryLand + FightModeControls | ChrTurnHL | KEY_A |  | R | LandLeft |
| FightModeControls + BattleInterfaceControls | ChrBlock | VK_CONTROL |  | R | LandBlock (honours the script lock) |
| FightModeControls | ChrBlock_Unlock | VK_CONTROL | INVERSE | R | script (raised on key release) |
| FightModeControls | ChrAttack | VK_LBUTTON |  | R | LMB in a fight: quick click = normal, hold = strong |
| FightModeControls | ChrRecoil | KEY_Z |  | R | LandRecoil (honours the script lock) |
| FightModeControls + BattleInterfaceControls, PrimaryLand | ChrFightMode | KEY_E |  | R | LandSword |
| FightModeControls + BattleInterfaceControls | ChrFire | KEY_Q |  | R | LandFire |
| FightModeControls | BOAL_ActivateRush | KEY_F |  | R | script |
| Sailing3Pers + Sailing1Pers | Ship_TurnLeft | KEY_A |  | R | SeaLeft |
| Sailing3Pers + Sailing1Pers | Ship_TurnRight | KEY_D |  | R | SeaRight |
| Sailing3Pers + Sailing1Pers | Ship_SailUp | KEY_W |  | R | SeaSailUp |
| Sailing3Pers + Sailing1Pers | Ship_SailDown | KEY_S |  | R | SeaSailDown |
| Sailing3Pers + Sailing1Pers | Ship_Fire | VK_LBUTTON |  | R | LMB: SeaView.FirePlayer |
| Sailing3Pers + Sailing1Pers | Sea_CameraSwitch | VK_TAB |  | R | SeaCameraSwitch |
| Sailing3Pers + Sailing1Pers | BI_ChargeCannonballs | KEY_1 |  | R | SeaBalls |
| Sailing3Pers + Sailing1Pers | BI_ChargeGrapes | KEY_2 |  | R | SeaGrapes |
| Sailing3Pers + Sailing1Pers | BI_ChargeKnippels | KEY_3 |  | R | SeaKnippels |
| Sailing3Pers + Sailing1Pers | BI_ChargeBombs | KEY_4 |  | R | SeaBombs |
| Sailing3Pers + Sailing1Pers | BI_MapEnter | KEY_M |  | R | SeaMap |
| Sailing1Pers | TelescopeIn | VK_CONTROL |  | R | SeaTelescope (deck camera only: group Sailing1Pers) |
| Sailing1Pers | TelescopeOut | VK_CONTROL | INVERSE | R | script |
| - | WMapTurnH | mouse X axis |  |  |  |
| - | WMapTurnV | mouse Y axis | INVERSE |  |  |
| WorldMapControls | WMapCameraRotate | VK_CONTROL |  | R | open |
| WorldMapControls | WMapForward | VK_LBUTTON |  | R | open (map zoom while held) |
| WorldMapControls | WMapBackward | VK_RBUTTON |  | R | open |
| WorldMapControls | WMapCameraSwitch | VK_TAB |  | R | SeaCameraSwitch on the map |
| WorldMapControls | WMapShipSailUp | KEY_W |  | R | SeaSailUp on the map |
| WorldMapControls | WMapShipSailDown | KEY_S |  | R | SeaSailDown on the map |
| WorldMapControls | WMapShipTurnLeft | KEY_A |  | R | SeaLeft on the map |
| WorldMapControls | WMapShipTurnRight | KEY_D |  | R | SeaRight on the map |
| WorldMapControls | WMapCancel | VK_SPACE |  | R | SeaAction on the map (exit) |
| - | ITurnH | mouse X axis |  |  |  |
| - | ITurnV | mouse Y axis | INVERSE |  |  |
| - | ILClick | VK_LBUTTON |  |  |  |
| - | IRClick | VK_RBUTTON |  |  |  |
| Sailing1Pers + Sailing3Pers, PrimaryLand, WorldMapControls, FightModeControls | Interface | VK_F2 |  |  | Interface |
| - | MainMenu | VK_F1 |  |  | MainMenu |
| Sailing1Pers + Sailing3Pers, PrimaryLand, WorldMapControls, FightModeControls | MainMenuDouble | VK_ESCAPE |  |  | MainEsc |
| - | IStartButton | VK_RETURN |  |  |  |
| - | ICancel | VK_ESCAPE |  |  |  |
| - | IAction | VK_SPACE |  |  |  |
| - | ILeftShift | VK_SHIFT |  |  |  |
| - | IRightShift | VK_SHIFT |  |  |  |
| - | IUp | VK_UP |  |  |  |
| - | IDown | VK_DOWN |  |  |  |
| - | IRight | VK_RIGHT |  |  |  |
| - | ILeft | VK_LEFT |  |  |  |
| Sailing3Pers + Sailing1Pers | BICommandsActivate | VK_RETURN |  | R | SeaCommands (Enter) |
| PrimaryLand + FightModeControls | LICommandsActivate | VK_RETURN |  | R | LandCommands (Enter) |
| BattleInterfaceControls | BICommandsConfirm | VK_RETURN |  | R | command menu (Enter) |
| BattleInterfaceControls | BICommandsCancel | VK_ESCAPE |  | R | command menu (Esc) |
| BattleInterfaceControls | BICommandsLeft | VK_LEFT |  | R | command menu |
| BattleInterfaceControls | BICommandsRight | VK_RIGHT |  | R | command menu |
| Sailing3Pers + Sailing1Pers | MiniMapZoomIn | KEY_E |  | R | script/C++ minimap (not wired) |
| Sailing3Pers + Sailing1Pers | MiniMapZoomOut | KEY_F |  | R | script/C++ minimap (not wired) |
| Sailing3Pers + Sailing1Pers, PrimaryLand, FightModeControls | BIFastCommand | VK_SPACE |  | R | SeaAction (Space; land: LandAction) |
| DialogControls | DlgAction | VK_SPACE |  |  |  |
| - | DlgCancel | VK_ESCAPE |  |  |  |
| DialogControls | DlgUp | VK_UP |  |  |  |
| DialogControls | DlgDown | VK_DOWN |  |  |  |
| DialogControls | DlgScrollUp | VK_PRIOR |  |  |  |
| DialogControls | DlgScrollDown | VK_NEXT |  |  |  |

There are no port-only controls on the pages any more: the land scheme is always the original one (mouse turns the hero and the camera, A / D turn, right / middle button walk, Shift = walk, left button = ChrAction), the sea camera and the map follow the original mouse controls, no arrow-key camera, no wheel. Port-only fixed keys without a control (not shown, `settings.ini [keys]`): F3 name tags, B map wait, F8 bug report, F9 free camera. F11 full screen. On the map: hold WMapForward / WMapBackward (left / right button) to zoom, Ctrl + mouse turns the camera.

Test hooks: `--mousemove=dx,dy@A-B` (raw mouse motion per frame), `--btn=L+|L-|R+|R-|M+|M-@N` (real mouse button), env `SEADOGS_CTLLOG=1` (current key group and the locked controls every 100 frames).
