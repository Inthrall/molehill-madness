# Where these came from

Every file in this folder is **CC0** (Creative Commons Zero, public domain) from **Kenney**, <https://kenney.nl>, taken from the Impact Sounds, Interface Sounds and RPG Audio packs.

CC0 requires no attribution and Kenney's own licence text says crediting is appreciated rather than mandatory. This file exists anyway, because a folder of renamed `.ogg` files with no provenance is impossible to audit later, and the licence question is the one that matters if the game is ever sold.

Renamed on the way in. The game loads by convention (`Sound.Dig` looks for `dig_0.ogg`, `dig_1.ogg`, and so on until one is missing), so the original names could not survive, and this table is the only remaining record of what each file actually is.

| In here | From | Original |
| --- | --- | --- |
| `dig_0..4` | Impact Sounds | `impactMining_000..004` |
| `walk_0..4` | Impact Sounds | `footstep_grass_000..004` |
| `burrow_0..4` | Impact Sounds | `footstep_snow_000..004` |
| `land_0..4` | Impact Sounds | `impactSoft_heavy_000..004` |
| `sandbag_0..4` | Impact Sounds | `impactSoft_medium_000..004` |
| `whack_0..4` | Impact Sounds | `impactPunch_heavy_000..004` |
| `thunk_0..4` | Impact Sounds | `impactWood_heavy_000..004` |
| `mortar_0..4` | Impact Sounds | `impactPlank_medium_000..004` |
| `helmet_0..4` | Impact Sounds | `impactTin_medium_000..004` |
| `snap_0` | RPG Audio | `metalLatch` |
| `snap_1..3` | Impact Sounds | `impactMetal_light_000..002` |
| `stretcher_0..2` | RPG Audio | `creak1..3` |
| `click_0..2` | Interface Sounds | `click_001..003` |
| `notch_0..1` | Interface Sounds | `tick_001..002` |
| `commit_0..2` | Interface Sounds | `confirmation_001..003` |
| `reset_0..2` | Interface Sounds | `scratch_001..003` |
| `warn_0` | Interface Sounds | `bong_001` |
| `warn_1..2` | Interface Sounds | `question_001..002` |
| `collect_0..1` | Interface Sounds | `pluck_001..002` |

## Why only Kenney

The repository is public, and most free sound libraries permit commercial use while forbidding redistribution of the raw files. Sonniss says so outright; Pixabay forbids distributing content "on a Standalone basis", which is exactly what an unmodified `.ogg` in a git repository is. CC0 is the only licence that permits keeping the files here at all.

Which is also why several sounds this game needs are still synthesised waveforms rather than recordings: Kenney has no explosion, no fuse, no steam, no wind and no lava. Those want CC0 submissions from Freesound or OpenGameArt, or Sonniss audio fetched outside git. See `docs/sound-sourcing.md`.

## Adding more

Drop `<name>_<n>.ogg` in, numbered from zero with no gaps, where `<name>` is a `Sound` enum value in lower case. Then import, or Godot will not see it:

```
godot --headless --path client --import
```

The `.ogg.import` files are committed on purpose: an export resolves resources through them, so a build without them has no audio.

No code change is needed for a new sound that already has an enum entry. Startup prints a line saying how many of the list are real, which is the quickest way to tell whether a file landed properly.
