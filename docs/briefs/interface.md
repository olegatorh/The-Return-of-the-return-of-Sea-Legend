# Brief: INTERFACE (xinterface screens, store, shipyard, key screen, text input, saves)

Read this instead of the big docs.

## Where the code is (`game/`)
* `XInterface.cs` (node engine: loads `RESOURCE/INI/INTERFACES/*.ini`, nodes, messages 45xxx, `ApplySnaps`, string fitting), node views: `ScrollImage.cs`, `FourImages.cs`, `ScrollBarView.cs`, `ScrollerView.cs`,
  `FormattedListView.cs`, `ChangerView.cs`, `EditBox.cs`, `Slider.cs`, `ContextHelp.cs`; layout/scaling `UiLayout.cs` (720×540 canvas, 640×480 core at (40,30), `UiLayout.Hud` = 540/600 for the battle HUD).
* Saves: `SaveFiles.cs` (+ `scrshoter` thumbnails). Options/key screen: `OptionsFile.cs`, `Controls.cs`, `KeyBindings.cs`, `KeyGlyphs.cs` (original script pages run; keys/mouse remappable).
* Dialog window: `Dialog.cs`.

## Contracts and owner decisions
* The scripts own the screens (`PROGRAM/INTERFACE/*.c`); the engine draws nodes, sends `*command` events (`ProcessCommandExecute`) and node messages. Store/shipyard logic is script (`store.c`, `shipyard.c`); the engine's carousel (`SCROLLIMAGE` per `xi_scroll_image.cpp`), `FOURIMAGE` (`GameInterface.FourImage`) and quantity scrollbar must follow the C++.
* Buying/selling goods is in the STORE screen (the ship hold screen has only Throw away / Exchange / Info — as in the original). Mouse hold on the quantity arrow does one change (C++), repeat only from the key.
* Ship rename: KEYCHOOSER + `key_hook.c` (Ukrainian letters from the system layout); EDITBOX node exists.
* Texts are fitted to the widths (`Fit/RefitString`); SPECIAL names on the character screen are fitted before the script's +/- arrows (x 75..115).
* Quick keys: Esc/F1 menu, F2 character, F4/F6 quick save/load, F8 bug report (`reports/`), F10 land control scheme, F11 fullscreen.

## Hooks and scenarios
`--click=NODE@N`, `--mouse=NODE@N` (real click), `--key=Name@N` (`Shift+Right`), `--type=Text@N`, `--call=LaunchStore(0)@N`; `SEADOGS_NODEMSG=1`. Scenarios: trade*, tradecompanion, itemstrade, hold, yard*, rename, chest, screens, salary, ransackcapt.

## Open
CONTEXTHELP texts for some screens, GLOWS/GLOWCURSOR, VIDEO background nodes, XBOX, `ievntGetHelpTexture`.
