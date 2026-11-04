# Syntax defects found in the shipped scripts

Found by `StormScript.Cli check`. The parser skips the broken statement and continues, so every other
statement in these files is still analysed. These are real bugs of the original game (the corresponding
quest/dialog branch cannot work as written) and are candidates for the first round of fixes.

| location | parser message | likely fix |
|---|---|---|
| `Henry_dialog.h(6)` | expected '}' (found ', не обойдите милостью своей убогого. Пожалуйста-а!') | missing comma between two array strings |
| `Jaoquin de masse_dialog.c(57)` | expected ';' (found ':') | ':' used instead of ';' |
| `Jaoquin de masse_dialog.c(75)` | expected ';' (found ':') | ':' used instead of ';' |
| `marc blacque_dialog.c(171)` | expected ';' (found 'PChar') | missing '+' before PChar.name |
| `marc blacque_dialog.c(180)` | expected ';' (found 'PChar') | missing '+' before PChar |
| `rachel blacque_dialog.c(235)` | expected ';' (found 'DLG_TEXT') | missing '+' (Address_Form.Fra DLG_TEXT[73]) |
| `RelationAgent_dialog.c(58)` | expected ';' (found '?') | missing '+' before "?" |
| `ps.c(4)` | expected declaration (found 'class') | C++ "class VECTOR" test file, not part of the game |
| `quests_reaction.c(1894)` | expected ')' (found '.0') | bad number literal "3.0.0" |
| `quests_reaction.c(5442)` | expected attribute name after '.' (found ')') | dangling ".)" in expression |
| `quests_reaction.c(7328)` | expected ';' (found '.2003') | bad literal ".2003" |
| `quests_reaction.c(8295)` | expected ']' (found ';') | unbalanced '[' in array index |
| `quests_reaction.c(8297)` | expected ']' (found ';') | unbalanced '[' in array index |
| `quests_reaction.c(8299)` | expected ']' (found ';') | unbalanced '[' in array index |
| `quests_reaction.c(10962)` | expected ';' (found ',') | ',' where ';' expected |

Also tolerated, like the original compiler does (see Parser.ExpectSemi): ~95 missing `;` at line end,
stray `)` after statements (e.g. `globals.c:79`, `WhrSun.c:23`), and `, ;`.
