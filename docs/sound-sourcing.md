# Sound sourcing

Where the noises come from, and the licence trap that decides what can go in this repository at all.

The game began with seven synthesised waveforms built at startup by `client/src/Game/Sfx.cs` and no audio files whatsoever. That was the right call for the fun gate and the wrong call for a release: a synthesised waveform cannot do a slide whistle, and slapstick without a slide whistle is just physics. This is the shopping list, the paperwork, and a note on how far through it we are.

Mole vocalisations are deliberately absent. Every entry here is a thing, a tool or the world making a noise, not a mole reacting to one.

## The trap: only CC0 may be committed

This repository is **public**. That single fact disqualifies most of the good free libraries as a place to keep files, while leaving them perfectly usable in the shipped game, and the distinction is easy to miss because every one of them advertises itself as free for commercial use. They are. Commercial use is not the constraint. **Redistribution** is.

| Source | Commercial use | Attribution | Raw files in a public repo |
| --- | --- | --- | --- |
| CC0 (Kenney, OpenGameArt CC0, Freesound CC0) | Yes | None | **Yes** |
| CC-BY (much of Freesound and OpenGameArt) | Yes | Required, per file | Yes, with credits kept |
| Sonniss GDC bundle | Yes | None | **No** |
| Pixabay | Yes | None | **No** |

Sonniss forbids it outright: the licensee "may not distribute, publish, sub-license or otherwise supply the sound effects as sound effects to any other person". Pixabay forbids distributing content "on a Standalone basis", meaning unmodified and with no creative effort applied, which is exactly what a `.wav` sitting in an assets folder is.

Both are fine baked into an exported build, because there they are part of a work rather than a sound library. Neither is fine in the tree. If a Sonniss recording is wanted, the honest options are to keep it out of git entirely and have the build fetch it, or to process it into something that is no longer the original asset.

So: prefer CC0 for everything, and treat Sonniss as the place to go when CC0 has nothing suitable.

Sonniss also expressly forbids using its audio to train AI models, which is irrelevant to shipping a game and worth knowing before anybody feeds the folder to a tool.

## Sources worth using

- **Kenney** — <https://kenney.nl/assets> — CC0, no signup, no attribution. Impact Sounds (130 files) and Interface Sounds (100 files) between them cover most of the interface and a good deal of the percussion. First stop for anything abstract.
- **OpenGameArt** — <https://opengameart.org> — mixed licences, filterable, with CC0 explosion, steam and cartoon-bounce packs. Read the licence per submission rather than per collection, because collections mix them.
- **Freesound** — <https://freesound.org> — over 500,000 sounds, mixed licences. Filter with the licence facet beside any search result, or put `license:"Creative Commons 0"` in the query. This is where specific real-world recordings live: soil, steam, fuses.
- **Sonniss GDC bundle** — <https://gdc.sonniss.com/> — 7.47 GB of professional library audio, royalty-free, no attribution, no redistribution. The quality ceiling, with the repository restriction above.

## What has landed

The Kenney CC0 packs are in, curated rather than dumped: 68 files covering 17 sounds, 696 KB, in `client/audio/`, with provenance in `client/audio/SOURCES.md`. Startup reports `sfx: 17 of 21 sounds recorded, 68 files`.

`Sfx.cs` now prefers a recording and falls back to its waveform, loading by convention: `Sound.Dig` looks for `dig_0.ogg` upward until one is missing. A new recording needs a file and an import, and no code at all.

Four of the original seven are still synthesised, and they are the four Kenney has nothing for: Boom, Launch, Poof and Ouch. Boom is the conspicuous one, since an explosion is the loudest thing the game does and a synthesised thump is the least convincing sound in it. That wants a CC0 explosion from OpenGameArt or Freesound next.

Fourteen of the new sounds are loaded but nothing plays them yet: the enum entries and the files exist, and no event fires them. Wiring is a separate job, and not a trivial one, because sound is read from the round recording rather than from live state (see the note at the end).

## What the game needs

Grouped by where it happens. Seven of these exist as synthesised stand-ins; the rest are events already in the simulation that make no noise at all.

### Interface and flow

| Sound | Notes | First stop |
| --- | --- | --- |
| Click | Exists. Buttons, and every notch of the weapon wheel | Kenney Interface Sounds |
| Wheel notch | Wants to be distinct from a button and quieter, since a turn crosses several | Kenney Interface Sounds |
| Commit | The plan locked in. Weightier than a click, and once per turn | Kenney Interface Sounds |
| Reset | Tearing the turn up. Should sound like a cost, because it is one | Kenney Interface Sounds |
| Clock warning | The last five seconds, where the ring already turns red | Kenney Digital Audio |
| Round start and end | Two short stings, not music | Kenney Music Jingles |
| Victory and defeat | The scoreboard, and the one place a jingle is welcome | Kenney Music Jingles |

### Movement

| Sound | Notes | First stop |
| --- | --- | --- |
| Dig | Exists. Wants three variants for turf, loose soil and packed soil, since the cost differs and the ear should know | Freesound CC0, "digging dirt" |
| Walk | On turf, and muffled underground | Kenney RPG Audio footsteps |
| Hop | The jump. Cartoon rather than athletic | OpenGameArt CC0 bounce |
| Land | A thud scaled by fall speed. The crate landing sample would do | Kenney Impact Sounds |
| Drill | The Tunnel Torpedo, and the only one needing a loop rather than a hit: it runs 26 ticks, 0.87 s | Freesound CC0, motor or servo loop |
| Sandbag | Soil landing in a heap. Three per turn are allowed, so it must not grate | Freesound CC0, "sack" or "soil drop" |

### Weapons

| Sound | Notes | First stop |
| --- | --- | --- |
| Launch | Exists. A lob leaving the paws | Kenney Impact Sounds, or a whoosh |
| Beetle launcher | Wants an insect note over the launch. The one weapon with a voice that is not a mole | Freesound CC0, "insect buzz" |
| Acorn mortar | A thump on firing, and a lighter split when it clusters into three | Kenney Impact Sounds |
| Explosion, three sizes | Exists as one. The arsenal spans a 1.25 m crater to a 2.5 m one, and one sample for all of them wastes the range | OpenGameArt CC0 explosions |
| Fuse | A three-second hiss, which is three seconds of dread and the best tension the game has | Freesound CC0, "fuse burning" |
| Big Whack | A comedy impact, and it should be the funniest sound in the game | OpenGameArt CC0 cartoon impact |
| Fracking | Low rumble. The only weapon that reaches through dirt | Freesound CC0, "earth rumble" |
| Geyser | Steam burst, shared with the SteamPop exit | OpenGameArt CC0 steam |
| Snap trap | A metal snap. Short, nasty, unmistakable | Kenney Impact Sounds |
| Root snare | Vines and rustle, closing over a round | Freesound CC0, "foliage rustle" |

### Crates and the world

| Sound | Notes | First stop |
| --- | --- | --- |
| Parachute | Fabric flutter while a crate descends | Freesound CC0, "fabric flap" |
| Thunk | Exists. A crate arriving on a ledge | Kenney Impact Sounds |
| Crate opened | The reward noise. Currently nothing, and crates are the whole scramble | Kenney Interface Sounds, rising |
| Wind | An ambient bed. It already affects shots, so it may as well be audible | Freesound CC0, "wind loop" |
| Lava | Bubbling and a low rumble, arriving at round eight and climbing | Freesound CC0, "lava" or "mud bubble" |

### Knockout exits

Seven exits are drawn and none of them make a sound. These are the slapstick payoff and deserve more care than the weapons.

| Exit | Sound |
| --- | --- |
| SpinAndPoof | Exists as Poof. A dust puff, plus a spin whistle |
| StretcherSquad | Small squeaky wheels, hurrying |
| DizzyBirds | Tweeting. Straight cartoon convention, which everybody reads instantly |
| BalloonExit | A squeak and a rising drift |
| MoleShapedHole | Impact and crumble, once |
| HelmetSpin | Metal spinning to a stop on a hard floor |
| SteamPop | Shared with the geyser |

## Before any of it is wired up

Three things that will otherwise bite.

**The existing class expects to be thrown away.** `Sfx.cs` says so in its own remarks. The `Sound` enum and `Play(sound, volumeDb, pitchSpread)` are the interface worth keeping; the seven generator methods are not. Note that `Play` already applies a random pitch wobble, which is what stops a repeated sample sounding like a stuck record, so samples want recording dry and leaving to it.

**Sound is drawn from the recording, not from live state.** The existing remarks are explicit that sound lands on the right tick because it reads the same recording the damage numbers do. Anything new has to go through that path or it will drift out of step with the replay, and nothing in it may feed back into the simulation.

**Formats.** Godot wants `.ogg` for anything long or looping and `.wav` for short hits. Kenney ships both; Freesound ships whatever the uploader had, frequently a 96 kHz `.wav` that wants downsampling. The synthesised streams run at 22050 Hz, which is fine for hits and mean for a wind bed.

## Credits

CC0 needs none, which is most of why it is preferred here. Anything CC-BY needs a line in a credits file shipped with the game, naming the sound, the author and the licence. There is no such file yet, and no `LICENSE` for the project either, which is worth settling before the first attributed asset lands rather than after.
