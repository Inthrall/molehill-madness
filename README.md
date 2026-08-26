# Molehill Mayhem

A turn-based artillery game where everybody takes their turn at the same time.

Four players, four moles each, one shared eight-second clock. Every round all four plot a move at once, then every plan resolves together: shots have to lead moving targets, four ambushes collide mid-air, and the best-laid plan marches proudly into a crater that did not exist when it was drawn. At round eight a line of lava appears along the bottom of the map and starts climbing, then closes in from the sides, until there is nowhere left to be careful.

Steam and Android, cross-play, free, cosmetics only. (iOS is out of scope until after the MVP.) Multiplayer only: no campaign, no AI. No words anywhere in the game.

## Documents

Both are single-file HTML, readable in any browser.

- [`docs/molehill-mayhem-design.html`](docs/molehill-mayhem-design.html) — the game design document. What the game is and every decision behind it.
- [`docs/implementation-plan.html`](docs/implementation-plan.html) — the implementation plan. Seven phases, two hard gates, and the engineering contracts.

## Layout

```
sim/     MoleSim, the deterministic game, and its tests
tools/   headless CLI, terrain dumps, corpus runners
client/  Godot 4 project (added at Phase 2)
relay/   ASP.NET Core lobby and turn-exchange service (added at Phase 4)
docs/    design document, implementation plan
```

The architectural rule, from which everything else follows: **MoleSim is the game and the engine is a lens pointed at it.** MoleSim is a plain `netstandard2.1` library with no engine types, no I/O and no floating-point arithmetic anywhere. The client renders it, the relay ferries its plans around, the tools replay it. Nothing else may mutate its state.

## Determinism

Every device must compute the same match from the same inputs, or online play is impossible. Three rules make that testable rather than hopeful:

1. **Fixed-point maths only.** `Fix64` (Q48.16) throughout the simulation. IEEE 754 arithmetic varies between platforms and would silently fork a live match between a phone and a PC.
2. **One seeded generator per match.** `MatchRng` (xoshiro256\*\*), drawn in a defined order, so replays are bit-exact down to which knockout animation plays.
3. **A golden corpus that only grows.** Every fixed bug leaves a recorded match behind, replayed on every platform in CI. The corpus is the reviewer a solo project does not otherwise have.

## Building

Needs the .NET 10 SDK (the library targets `netstandard2.1`; tests and tools target `net10.0`).

```bash
dotnet build
dotnet test
```

## Status

**Phase 0 gate: passed.** The simulation computes identical results on Windows, Linux and Android, across two CPU architectures and two .NET runtime versions, and the probe runs in 22 ms at 60 fps on a phone. Full evidence and the deferred risks are in [`docs/phase-0-gate.md`](docs/phase-0-gate.md).

**Phase 1 complete.** MoleSim plays a whole match headlessly: seamless movement priced in stamina, the full fifteen-weapon arsenal, projectiles and blasts with line of sight, crates, lava, pacing and the knockout reel. `dotnet run --project tools/Molehill.Cli -- match` plays one to a winner and prints it round by round. A golden corpus of pinned match hashes is verified on every platform in CI.

**Phase 2 in progress.** The game is playable. `client/scenes/Match.tscn` plays a whole match: steer your mole and watch the gauges drain as it walks, aim and stamp the turn's shot, plant a charge if you fancy it, then watch every plan resolve at once over eight seconds. Craters appear when the shells land, damage numbers rise and fade where they hit, and moles leave on one of two rough exits. 250 tests.

Turns are steered rather than drawn. The first version had the player draw the route as a line and looped a translucent ghost along it while the mole stood still, and the first time somebody who had not built it picked it up, they could not tell which mole was theirs or what the line was for. Steering says the same thing with one mole in it: push, and the mole walks, digging or climbing or falling as the ground demands, because the preview runs the real movement solver against a copy of the terrain. The waypoints it leaves behind are positions the mole genuinely stood at, which is a stronger guarantee than a hand-drawn line ever gave: `MoleMotion` has a stall-detector whose comment hoped that real routes would always be reachable, and steering makes that true by construction. Standing still costs nothing, so the eight seconds are eight seconds of walking rather than of wall clock, and a player who spends half a minute thinking has spent none of the round. The path is kept, for the markers and for a reset, and never drawn.

Planning is simultaneous, which is what split screen is for. Every platoon with its own controller plans at the same moment on one shared clock; platoons without one share the pointer and take turns, and the clock resets as it changes hands. The same code covers a couch full of gamepads and a prototype on one mouse, so the testable configuration is not a separate build. The screen carves into a pane per platoon while they plan.

The replay is cut by a director rather than by the seating plan. A round has already resolved before its first frame is drawn, so the whole shape of it is knowable up front: who was in it, where they went, and how far apart the separate pieces of it were. `ReplayDirector` groups the moles by proximity, works out how many cameras that needs, and decides how far back each stands, all once before the round plays, which is the only way to stop the screen splitting and merging every time somebody is punted sideways. The design's rule is that the camera "merges by proximity and splits when the fight spreads", and grouping by proximity is the half that was missing: the first version measured whether everything fitted one screen and otherwise fell back to a pane per platoon at a fixed zoom, so three moles in a scrum and one off on its own gave four panes, three of them showing the same hillside. Cameras frame what they are pointed at and track it, and the zoom never changes mid-round.

Two things fell out of framing the action rather than following one mole. A mole punted forty metres up used to take the camera with it until the ground left the frame and the pane went blank cream, which is correct behaviour and indistinguishable from a fault; a camera framed for the whole flight keeps the ground in shot. And a shot is framed to show eighteen metres of height or thirty-two of width, whichever the pane's proportions can manage, rather than both: demanding both made a wide stacked band show sixty-four metres across in order to satisfy its height, which is wider than the whole map and left two thirds of the frame empty.

Each camera wears its broadcast furniture: viewfinder crop marks in the corners, a tally light that pulses while the round is playing, which camera it is as pips, and a caption of platoon colours saying whose fight is in shot. It is sports television for a reason beyond the look of it. A replay that cuts to two cameras has to say so or the second pane reads as a rendering fault, and a player watching four moles they cannot control needs to find their own in a hurry. All of it wordless, and the camera number is pips rather than a numeral, because the one numeral the design keeps is spent on damage.

The HUD is wordless. Every weapon and gauge is drawn from primitives in `Glyphs.cs` rather than imported, which scales to any screen without a sprite sheet per density, recolours per platoon for free, and leaves the prototype with no art dependency at all. Digits survive only for damage, which is the exception the design carves out on the grounds that a numeral reads the same in every language a seven-year-old might have. The planning clock is a ring for the same reason.

On a phone it collapses to one view with a stick under the left thumb and the controls the design specifies under the right: a weapon wheel you flick, a button to fire that doubles as the aim stick, one to plant, one to reset and one to commit. Since the stick took over movement, a drag on the map means what a drag on a map means everywhere else, so the camera pans with one finger and pinches with two, and the one gesture the game had invented for itself is gone. Touch reaches the same handful of verbs the keys do, so the rules cannot tell a thumb from a keyboard.

Nothing in the arsenal is free except the Clod Lobber. Every other weapon has a finite stock, comes back only from crates, and a plan naming something a platoon has none of is refused rather than degraded, which is the same anti-cheat rule as everything else: a client can submit rubbish, never a state nobody else has. That closes the other half of the crate loop, which until now threw weapon crates away for want of anywhere to put them. Bracing digs in and takes a third off the next blast, where before it only stopped a mole walking, which is what planning nothing already did. A hop is booked with one press for the moment it is pressed, which steering makes possible and drawing did not: a route has no "now" to book anything against.

Nine findings so far, and not one of them came from a test. Seven came from watching rounds render. Craters used to be as wide as the blast that made them, which left the map unrecognisable by round five and contradicted pacing the design had already fixed, so a crater is now its own number and `ArsenalTests` defends the map surviving a dozen rounds. A round resolves before its first frame is drawn, so anything read from live state during playback gives the ending away: the map and the score are both replayed from the recording instead. And a gauge scaled by its own value is unreadable at low values, which is why the wind arrow keeps its length and puts the strength in the streaks behind it.

Three more came from the camera work: the blank pane and the empty band described above, and the shared tally, which is centred horizontally and so sits exactly where a two-by-two split puts its vertical seam, on top of the right-hand pane's own instruments. Its clearance is derived from the same two numbers that strip is built from, so the two cannot drift apart the next time either is retuned.

The other two came from somebody holding the phone. The ghost was the whole planning model and it was the single most confusing thing on the screen, which no test could have told me and no rendered frame did either, because a frame of it looks exactly as intended. And bracing turned out to be booked at the moment the button went down, which was a quiet trap: braced early, it cancelled the mole's own input partway along and silently truncated the walk its owner was still steering. It is worked out at commit now, when where the walk ended is actually known.

Sound is synthesised at startup with no audio assets at all: seven waveforms built from sine, square and noise, played off the same recording the damage numbers come from so each lands on the tick it belongs to. It exists because the gate asks whether the slapstick is funny, and silent slapstick is not the same thing being tested.

`--demo` drives the game through its own interface without a player, `--frail` starts everybody nearly out so the knockouts can be watched, `--split` forces the panes apart, `--touch` brings up the phone controls on a desktop, and `--mute` shuts it up. With Godot's `--write-movie` they make the render layer inspectable frame by frame, which is how seven of the nine findings turned up:

```bash
godot --path client --write-movie frames/f.png --fixed-fps 30 --quit-after 300 -- --demo --split
```

For a phone, `tools/scripts/deploy-android.sh` exports a signed debug APK, checks that the C# actually made it in, and installs it if a device is connected. Pass `--export-only`, or just leave the phone unplugged, and it hands over the APK to sideload instead. USB debugging is not required.

Still owed: the gamepad axis reads have never met real hardware, though the simultaneous planning and the plan verbs they feed are exercised by the driver. The pinch has not either, since a mouse has only ever had one finger. And the structured playtests, which are the actual gate. No amount of this code can answer whether it is funny.

## Licence

All rights reserved. Public so the work is readable and so the documents can be linked; not open source, and not licensed for reuse.
