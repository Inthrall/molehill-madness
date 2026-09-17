# Where these came from

The sheets in this folder are generated art, held outright rather than licensed from a library, so nothing here needs attribution and nothing here is restricted from a public tree. This file exists for a different reason: both stores ask which tool made what, and a folder of sheets with no provenance cannot answer that later. See `docs/art-sourcing.md` for what each store wants and in what form.

Record the tool and the month whenever a sheet lands or is replaced. A sheet that is regenerated keeps its filename, so this table is the only record that it changed hands.

| Sheets | Tool | Arrived |
| --- | --- | --- |
| The ground: `foreground underground`, `background underground`, `background surface`, `lava floor`, `lava wall` | Not recorded | August 2026 |
| Decor: `grass background`, `grass background decor` | Not recorded | August 2026 |
| Moles: `mole baseline`, `aim`, `airborne`, `dig`, `hit`, `KO`, `power claw`, `rooted`, `Mole Walk`, `Mole face icons` | Not recorded | August 2026 |
| Knockout exits: `mole death 1` to `mole death 9` | Not recorded | August 2026 |
| Weapons and glyphs: `weapon glyph 1`, `weapon glyph 2`, `weapons glyph 3`, `icon glyphs`, `numbers and symbols`, `projectiles` | Not recorded | August 2026 |
| Objects: `crate`, `drill`, `girder`, `traps`, `geyser oil`, `charge arrow` | Not recorded | August 2026 |
| Effects: `explosion`, `energy effect` | Not recorded | August 2026 |
| Front of house: `app icon`, `main menu background`, `molehill madness title` | Not recorded | August 2026 |

"Not recorded" is the honest state rather than a placeholder to ignore. Three of these sheets carry a soft four-pointed star watermark, which is the only surviving evidence of which tool drew them, and `import-art.ps1` patches it out of the three that tile. Settle this before the first store submission.

## Regridded sheets

| Sheet | Built from | Why |
| --- | --- | --- |
| `explosion regrid.png` | `explosion.png` | The original is two rows on different pitches, five cells of 291 over three of 485, and its widest drawing does not fit a top-row cell. Nothing can cut all seven frames off it. `regrid-explosion.ps1` lifts each drawing on its own measured centre, masks it to the cell it came from, and lays the seven out on one grid in playing order. Run that script to rebuild it. |

A regrid is a derived file and carries no new artwork, so it inherits whatever the original's row above says about its tool. The original stays in this folder as the record of what actually arrived, and is what any future regrid starts from.

## Store declarations

Steam takes one checkbox in the content survey covering the game, with a short description of the categories. Everything in the table above falls under it.

Google Play takes a declaration per store listing asset. That covers the icon, the feature graphic and the screenshots, and it is the only place a player sees a label against an individual image.
